namespace Perla.Testing

open System
open System.Threading.Tasks
open Microsoft.Playwright
open Microsoft.Extensions.Logging

open Perla
open Perla.Logger
open Perla.Types

open FSharp.Control
open FSharp.Control.Reactive

open Spectre.Console
open Spectre.Console.Rendering

open IcedTasks

type ClientTestException(message: string, stack: string) =
  inherit Exception(message)

  override _.StackTrace = stack

type ReportedError = {
  test: Test option
  message: string
  stack: string
}


module Print =

  let test(test: Test, error: (string * string) option) : IRenderable list =
    let stateColor =
      match test.state with
      | Some "passed" -> "green"
      | Some "failed" -> "red"
      | _ -> "grey"

    let duration =
      TimeSpan.FromMilliseconds(test.duration |> Option.defaultValue 0.)

    let speedColor =
      match test.speed with
      | Some "slow" -> "orange"
      | Some "medium" -> "yellow"
      | Some "fast" -> "green"
      | _ -> "grey"

    let skipped = if test.pending then "skipped" else ""

    [
      Markup(
        $"[bold yellow]{test.fullTitle.EscapeMarkup()}[/] - [bold {stateColor}]{test.state}[/] [{speedColor}]{duration}[/] [dim blue]{skipped}[/]"
      )
      match error with
      | Some error -> ClientTestException(error).GetRenderable()
      | _ -> ()
    ]

  let suite(suite: Suite, includeTests: bool) : IRenderable =
    let skipped = if suite.pending then "skipped" else ""

    let rows: IRenderable list = [
      Markup($"{suite.title.EscapeMarkup()} - [dim blue]{skipped}[/]")
      if includeTests then
        yield!
          suite.tests
          |> List.map(fun suiteTest -> test(suiteTest, None) |> List.head)
    ]

    Panel(
      Rows(rows),
      Header = PanelHeader($"[bold yellow]{suite.fullTitle.EscapeMarkup()}[/]")
    )

  let Stats(stats: TestStats) =

    let content =
      let content: IRenderable seq = [
        Markup($"Started {stats.start}")
        Markup(
          $"[yellow]Total Suites[/] [bold yellow]{stats.suites}[/] - [yellow]Total Tests[/] [bold yellow]{stats.tests}[/]"
        )
        Markup($"[green]Passed Tests[/] [bold green]{stats.passes}[/]")
        Markup($"[red]Failed Tests[/] [bold red]{stats.failures}[/]")
        Markup($"[blue]Skipped Tests[/] [bold blue]{stats.pending}[/]")
        match stats.``end`` with
        | Some endTime -> Markup($"Start {endTime}")
        | None -> ()
      ]

      Rows(content)

    AnsiConsole.Write(
      Panel(content, Header = PanelHeader("[bold white]Test Results[/]"))
    )

type Print =

  static member Test(test: Test, ?error: string * string) =
    Print.test(test, error) |> Rows |> AnsiConsole.Write

  static member Suite(suite: Suite, ?includeTests: bool) =
    Print.suite(suite, defaultArg includeTests false)
    |> Rows
    |> AnsiConsole.Write

  static member Report
    (stats: TestStats, suites: Suite list, errors: ReportedError list)
    =
    let getChartItem(color, label, value) =
      { new IBreakdownChartItem with
          member _.Color = color
          member _.Label = label
          member _.Value = value
      }

    let chart =
      let chart =
        BreakdownChart(ShowTags = true, ShowTagValues = true).FullSize()

      chart.AddItems(
        [
          getChartItem(Color.Green, "Tests Passed", stats.passes)
          getChartItem(Color.Red, "Tests Failed", stats.failures)
        ]
      )

    let endTime = stats.``end`` |> Option.defaultWith(fun _ -> DateTime.Now)
    let difference = endTime - stats.start

    let rows: IRenderable seq = [
      for suite in suites do
        Print.suite(suite, true)
      if errors.Length > 0 then
        Rule("Test run errors", Style = Style.Parse("bold red"))

        for error in errors do
          let errorMessage =
            match error.test with
            | Some test -> $"{test.fullTitle} -> {error.message}"
            | None -> error.message

          ClientTestException(errorMessage, error.stack).GetRenderable()

        Rule("", Style = Style.Parse("bold red"))
      Panel(
        chart,
        Header =
          PanelHeader(
            $"[yellow] TestRun of {stats.suites} suites and {stats.tests} tests - Duration:[/] [bold yellow]{difference}[/]"
          )
      )
    ]

    rows |> Rows |> AnsiConsole.Write

module Testing =
  let SetupPlaywright() =
    Logger.log(
      "[bold yellow]Setting up playwright...[/] This will install [bold cyan]all[/] of the supported browsers",
      escape = false
    )

    try
      let exitCode = Program.Main([| "install" |])

      if exitCode = 0 then
        Logger.log(
          "[bold yellow]Playwright setup[/] [bold green]complete[/]",
          escape = false
        )
      else
        Logger.log(
          "[bold red]We couldn't setup Playwright[/]: you may need to set it up manually",
          escape = false
        )

        Logger.log
          "For more information please visit https://playwright.dev/dotnet/docs/browsers"

    with ex ->
      Logger.log(
        "[bold red]We couldn't setup Playwright[/]: you may need to set it up manually",
        ex = ex,
        escape = false
      )

      Logger.log
        "For more information please visit https://playwright.dev/dotnet/docs/browsers"


  let BuildReport
    (events: TestEvent list)
    : TestStats * Suite list * ReportedError list =
    let suiteEnds =
      events
      |> List.choose(fun event ->
        match event with
        | SuiteEnd(_, _, suite) -> Some suite
        | _ -> None)

    let errors =
      events
      |> List.choose(fun event ->
        match event with
        | TestFailed(_, _, test, message, stack) ->
          Some {
            test = Some test
            message = message
            stack = stack
          }
        | TestImportFailed(_, message, stack) ->
          Some {
            test = None
            message = message
            stack = stack
          }
        | _ -> None)

    let stats =
      events
      |> List.tryPick(fun event ->
        match event with
        | SessionEnd(_, stats) -> Some stats
        | _ -> None)
      |> Option.defaultWith(fun _ -> {
        suites = 0
        tests = 0
        passes = 0
        pending = 0
        failures = 0
        start = DateTime.Now
        ``end`` = None
      })

    stats, suiteEnds, errors


  let private startNotifications
    (tasks:
      {|
        allTask: ProgressTask
        failedTask: ProgressTask
        passedTask: ProgressTask
      |})
    totalTests
    =
    tasks.allTask.MaxValue <- totalTests
    tasks.failedTask.MaxValue <- totalTests
    tasks.passedTask.MaxValue <- totalTests
    tasks.allTask.IsIndeterminate <- true

  let private endSession
    (tasks:
      {|
        allTask: ProgressTask
        failedTask: ProgressTask
        passedTask: ProgressTask
      |})
    =
    tasks.failedTask.StopTask()
    tasks.passedTask.StopTask()
    tasks.allTask.StopTask()

  let private passTest
    (tasks:
      {|
        allTask: ProgressTask
        failedTask: ProgressTask
        passedTask: ProgressTask
      |})
    =
    tasks.passedTask.Increment(1)
    tasks.allTask.Increment(1)

  let private failTest
    (tasks:
      {|
        allTask: ProgressTask
        failedTask: ProgressTask
        passedTask: ProgressTask
      |})
    (errors: ResizeArray<_>)
    (test, message, stack)
    =
    tasks.failedTask.Increment(1)
    tasks.allTask.Increment(1)

    errors.Add(
      {
        test = Some test
        message = message
        stack = stack
      }
    )

  let private endSuite (suites: ResizeArray<_>) suite = suites.Add(suite)

  let private failImport (errors: ResizeArray<_>) (message, stack) =
    errors.Add(
      {
        test = None
        message = message
        stack = stack
      }
    )

  let private signalEnd
    (overallStats: TestStats)
    (suites: _ list)
    (errors: _ list)
    =
    Console.Clear()
    AnsiConsole.Clear()
    Print.Report(overallStats, suites, errors)

  let PrintReportLive(events: IObservable<TestEvent>) =
    AnsiConsole
      .Progress(HideCompleted = false, AutoClear = false)
      .Start(fun ctx ->
        let suites = ResizeArray()
        let errors = ResizeArray()

        let mutable overallStats = {
          suites = 0
          tests = 0
          passes = 0
          pending = 0
          failures = 0
          start = DateTime.Now
          ``end`` = None
        }

        let tasks = {|
          allTask = ctx.AddTask("All Tests")
          failedTask = ctx.AddTask("Tests Failed")
          passedTask = ctx.AddTask("Tests Passed")
        |}

        let startNotifications = startNotifications tasks

        let failTest = failTest tasks errors
        let endSuite = endSuite suites
        let failImport = failImport errors

        events
        |> Observable.subscribeSafe(fun value ->
          match value with
          | SessionStart(_, _, totalTests) -> startNotifications totalTests
          | SessionEnd(_, stats) ->
            overallStats <- stats
            endSession tasks
          | TestPass _ -> passTest tasks
          | TestFailed(_, _, test, message, stack) ->
            failTest(test, message, stack)
          | SuiteStart(_, stats, _) -> overallStats <- stats
          | SuiteEnd(_, stats, suite) ->
            overallStats <- stats
            endSuite suite
          | TestImportFailed(_, message, stack) -> failImport(message, stack)
          | TestRunFinished _ ->
            signalEnd
              overallStats
              (suites |> Seq.toList)
              (errors |> Seq.toList)))

  let getBrowser(browser: Browser, headless: bool, pl: IPlaywright) = task {
    let options = BrowserTypeLaunchOptions(Headless = headless)

    match browser with
    | Browser.Chrome ->
      options.Channel <- "chrome"
      return! pl.Chromium.LaunchAsync(options)
    | Browser.Edge ->
      options.Channel <- "edge"
      return! pl.Chromium.LaunchAsync(options)
    | Browser.Chromium -> return! pl.Chromium.LaunchAsync(options)
    | Browser.Firefox -> return! pl.Firefox.LaunchAsync(options)
    | Browser.Webkit -> return! pl.Webkit.LaunchAsync(options)
  }

  let monitorPageLogs(page: IPage) =
    page.Console
    |> Observable.subscribeSafe(fun e ->
      let getText color =
        $"[bold {color}]{e.Text.EscapeMarkup()}[/]".EscapeMarkup()

      let writeRule() =
        let rule =
          Rule(
            $"[dim blue]{e.Location}[/]",
            Style = Style.Parse("dim"),
            Justification = Justify.Right
          )

        AnsiConsole.Write(rule)

      match e.Type with
      | Debug -> ()
      | Info ->
        Logger.log(getText "cyan", target = PrefixKind.Browser)
        writeRule()
      | Err ->
        Logger.log(getText "red", target = PrefixKind.Browser)
        writeRule()
      | Warning ->
        Logger.log(getText "orange", target = PrefixKind.Browser)
        writeRule()
      | Clear ->
        let link = $"[link]{e.Location.EscapeMarkup()}[/]"

        Logger.log(
          $"Browser Console cleared at: {link.EscapeMarkup()}",
          target = PrefixKind.Browser
        )

        writeRule()
      | _ ->
        Logger.log($"{e.Text.EscapeMarkup()}", target = PrefixKind.Browser)
        writeRule()

    )


open Testing

type Testing =

  static member GetBrowser(pl: IPlaywright, browser: Browser, headless: bool) = task {
    let options = BrowserTypeLaunchOptions(Headless = headless)

    match browser with
    | Browser.Chrome ->
      options.Channel <- "chrome"
      return! pl.Chromium.LaunchAsync(options)
    | Browser.Edge ->
      options.Channel <- "edge"
      return! pl.Chromium.LaunchAsync(options)
    | Browser.Chromium -> return! pl.Chromium.LaunchAsync(options)
    | Browser.Firefox -> return! pl.Firefox.LaunchAsync(options)
    | Browser.Webkit -> return! pl.Webkit.LaunchAsync(options)
  }

  static member GetExecutor(url: string, browser: Browser) =
    fun (iBrowser: IBrowser) -> task {
      let! page =
        iBrowser.NewPageAsync(BrowserNewPageOptions(IgnoreHTTPSErrors = true))

      use _ = monitorPageLogs page

      do! page.GotoAsync url :> Task

      Logger.log(
        $"Starting session for {browser.AsString}: {iBrowser.Version}",
        target = PrefixKind.Browser
      )

      do!
        page.WaitForConsoleMessageAsync(
          PageWaitForConsoleMessageOptions(
            Predicate = (fun event -> event.Text = "__perla-test-run-finished")
          )
        )
        :> Task

      return! page.CloseAsync(PageCloseOptions(RunBeforeUnload = false))
    }

  static member GetLiveExecutor
    (url: string, browser: Browser, fileChanges: IObservable<unit>)
    =
    fun (iBrowser: IBrowser) -> task {
      let! page =
        iBrowser.NewPageAsync(BrowserNewPageOptions(IgnoreHTTPSErrors = true))

      let monitor = monitorPageLogs page

      do! page.GotoAsync url :> Task

      Logger.log(
        $"Starting session for {browser.AsString}: {iBrowser.Version}",
        target = PrefixKind.Browser
      )

      return
        fileChanges
        |> Observable.map(fun _ ->
          page.ReloadAsync() |> Async.AwaitTask |> Async.Ignore)
        |> Observable.switchAsync
        |> Observable.finallyDo(fun _ -> monitor.Dispose())
    }

[<Interface>]
type PlaywrightService =
  inherit IDisposable

  /// Initialize Playwright (must be called before other operations)
  abstract Initialize: unit -> CancellableTask<unit>

  /// Install Playwright browsers
  abstract InstallBrowsers: unit -> CancellableTask<bool>

  /// Launch a browser instance
  abstract LaunchBrowser:
    browser: Browser * headless: bool -> CancellableTask<IBrowser>

  /// Get the underlying IPlaywright instance (for advanced scenarios)
  abstract GetPlaywright: unit -> IPlaywright

[<Interface>]
type TestReportPrinter =

  /// Print a single test result
  abstract PrintTest: test: Test * ?error: (string * string) -> unit

  /// Print a test suite
  abstract PrintSuite: suite: Suite * ?includeTests: bool -> unit

  /// Print final test report with statistics
  abstract PrintReport:
    stats: TestStats * suites: Suite list * errors: ReportedError list -> unit

[<Interface>]
type TestingService =

  /// Setup Playwright browsers (installs all supported browsers)
  abstract SetupPlaywright: unit -> CancellableTask<bool>

  /// Get a browser instance for testing
  abstract GetBrowser:
    browser: Browser * headless: bool -> CancellableTask<IBrowser>

  /// Run tests once with specified configuration
  abstract RunOnce:
    browsers: Browser seq *
    browserMode: BrowserMode *
    headless: bool *
    serverUrl: string *
    ?fileGlobs: string seq ->
      CancellableTask<TestStats * Suite list * ReportedError list>

  /// Run tests in watch mode with file change monitoring
  abstract RunWatch:
    browser: Browser *
    headless: bool *
    serverUrl: string *
    fileChanges: IObservable<unit> *
    ?fileGlobs: string seq ->
      CancellableTask<TestStats * Suite list * ReportedError list>

  /// Build test report from events
  abstract BuildReport:
    events: TestEvent list -> TestStats * Suite list * ReportedError list

  /// Monitor test events and print live progress
  abstract PrintReportLive: events: IObservable<TestEvent> -> IDisposable

// ============================================================================
// Service Arguments
// ============================================================================

type PlaywrightServiceArgs = { Logger: ILogger }

type TestReportPrinterArgs = { Logger: ILogger }

type TestingServiceArgs = {
  Logger: ILogger
  PlaywrightService: PlaywrightService
  ReportPrinter: TestReportPrinter
}

// ============================================================================
// Playwright Service Implementation
// ============================================================================

module PlaywrightService =

  let Create(args: PlaywrightServiceArgs) : PlaywrightService =
    let logger = args.Logger
    let mutable playwright: IPlaywright option = None
    let mutable disposed = false

    { new PlaywrightService with
        member _.Initialize() = cancellableTask {
          if disposed then
            failwith "PlaywrightService has been disposed"

          if playwright.IsNone then
            logger.LogInformation("Initializing Playwright...")
            let! pw = Playwright.CreateAsync()
            playwright <- Some pw
            logger.LogInformation("Playwright initialized successfully")
        }

        member _.InstallBrowsers() = cancellableTask {
          logger.LogInformation(
            "Setting up Playwright... This will install all supported browsers"
          )

          try
            let exitCode = Program.Main([| "install" |])

            if exitCode = 0 then
              logger.LogInformation("Playwright setup complete")
              return true
            else
              logger.LogError(
                "Couldn't setup Playwright: you may need to set it up manually. "
                + "For more information visit https://playwright.dev/dotnet/docs/browsers"
              )

              return false

          with ex ->
            logger.LogError(
              ex,
              "Couldn't setup Playwright: you may need to set it up manually. "
              + "For more information visit https://playwright.dev/dotnet/docs/browsers"
            )

            return false
        }

        member this.LaunchBrowser(browser: Browser, headless: bool) = cancellableTask {
          if disposed then
            failwith "PlaywrightService has been disposed"

          match playwright with
          | None ->
            do! this.Initialize()
            return! this.LaunchBrowser(browser, headless)
          | Some pw ->
            let options = BrowserTypeLaunchOptions()
            // Note: Devtools property is deprecated in newer Playwright versions
            options.Headless <- headless

            logger.LogInformation(
              "Launching {Browser} browser (headless: {Headless})",
              browser.AsString,
              headless
            )

            match browser with
            | Browser.Chrome ->
              options.Channel <- "chrome"
              return! pw.Chromium.LaunchAsync(options)
            | Browser.Edge ->
              options.Channel <- "edge"
              return! pw.Chromium.LaunchAsync(options)
            | Browser.Chromium -> return! pw.Chromium.LaunchAsync(options)
            | Browser.Firefox -> return! pw.Firefox.LaunchAsync(options)
            | Browser.Webkit -> return! pw.Webkit.LaunchAsync(options)
        }

        member _.GetPlaywright() =
          match playwright with
          | Some pw -> pw
          | None ->
            failwith "Playwright not initialized. Call Initialize() first."

        member _.Dispose() =
          if not disposed then
            disposed <- true

            match playwright with
            | Some pw ->
              logger.LogInformation("Disposing Playwright...")
              pw.Dispose()
              playwright <- None
            | None -> ()
    }

// ============================================================================
// Test Report Printer Implementation
// ============================================================================

module TestReportPrinter =

  let Create(args: TestReportPrinterArgs) : TestReportPrinter =
    let _ = args.Logger

    { new TestReportPrinter with
        member _.PrintTest(test, ?error) = Print.Test(test, ?error = error)

        member _.PrintSuite(suite, ?includeTests) =
          Print.Suite(suite, ?includeTests = includeTests)

        member _.PrintReport(stats, suites, errors) =
          Print.Report(stats, suites, errors)
    }

// ============================================================================
// Testing Service Implementation
// ============================================================================

module TestingService =

  let Create(args: TestingServiceArgs) : TestingService =
    let logger = args.Logger
    let playwrightService = args.PlaywrightService
    let _ = args.ReportPrinter

    { new TestingService with
        member _.SetupPlaywright() = playwrightService.InstallBrowsers()

        member _.GetBrowser(browser, headless) =
          playwrightService.LaunchBrowser(browser, headless)

        member _.BuildReport(events) = BuildReport events

        member _.PrintReportLive(events) = PrintReportLive events

        member _.RunOnce
          (browsers, browserMode, headless, serverUrl, ?fileGlobs)
          =
          cancellableTask {
            let! token = CancellableTask.getCancellationToken()

            logger.LogInformation(
              "Starting test run with {BrowserCount} browsers in {Mode} mode",
              Seq.length browsers,
              match browserMode with
              | BrowserMode.Parallel -> "parallel"
              | BrowserMode.Sequential -> "sequential"
            )

            try
              match browserMode with
              | BrowserMode.Parallel ->
                let! _ =
                  browsers
                  |> Seq.map(fun browser -> cancellableTask {
                    let! browserInstance =
                      playwrightService.LaunchBrowser(browser, headless)

                    try
                      let executor = Testing.GetExecutor(serverUrl, browser)
                      return! executor browserInstance
                    finally
                      browserInstance.CloseAsync() |> ignore
                  })
                  |> CancellableTask.whenAll

                logger.LogInformation("All browser sessions completed")

              | BrowserMode.Sequential ->
                for browser in browsers do
                  if not token.IsCancellationRequested then
                    let! browserInstance =
                      playwrightService.LaunchBrowser(browser, headless)

                    try
                      let executor = Testing.GetExecutor(serverUrl, browser)
                      do! executor browserInstance
                    finally
                      browserInstance.CloseAsync() |> ignore

              // Return results - this would integrate with actual test event collection
              return BuildReport []

            with ex ->
              logger.LogError(ex, "Error during test execution")

              return
                ({
                  suites = 0
                  tests = 0
                  passes = 0
                  pending = 0
                  failures = 1
                  start = DateTime.Now
                  ``end`` = Some DateTime.Now
                 },
                 [],
                 [
                   {
                     test = None
                     message = ex.Message
                     stack =
                       Option.ofObj ex.StackTrace |> Option.defaultValue ""
                   }
                 ])
          }

        member _.RunWatch
          (browser, headless, serverUrl, fileChanges, ?fileGlobs)
          =
          cancellableTask {
            let! token = CancellableTask.getCancellationToken()

            logger.LogInformation(
              "Starting test watch mode with {Browser}",
              browser.AsString
            )

            try
              let! browserInstance =
                playwrightService.LaunchBrowser(browser, headless)

              try
                let! observable =
                  Testing.GetLiveExecutor
                    (serverUrl, browser, fileChanges)
                    browserInstance

                use _ =
                  observable.Subscribe(fun _ ->
                    logger.LogInformation(
                      "Test files reloaded due to file changes"
                    ))

                while not token.IsCancellationRequested do
                  do! Task.Delay(1000, token)

                logger.LogInformation("Test watch mode stopped")
                return BuildReport []

              finally
                logger.LogInformation("Closing browser instance")
                browserInstance.CloseAsync() |> ignore

            with ex ->
              logger.LogError(ex, "Error during test watch mode")

              return
                ({
                  suites = 0
                  tests = 0
                  passes = 0
                  pending = 0
                  failures = 1
                  start = DateTime.Now
                  ``end`` = Some DateTime.Now
                 },
                 [],
                 [
                   {
                     test = None
                     message = ex.Message
                     stack =
                       Option.ofObj ex.StackTrace |> Option.defaultValue ""
                   }
                 ])
          }
    }
