// Learn more about F# at http://docs.microsoft.com/dotnet/fsharp

open Microsoft.Extensions.Logging
open FSharp.SystemCommandLine
open Perla
open Perla.RequestHandler
open Perla.FileSystem
open Perla.Commands
open Perla.Logger

module Env =

  let SetupAppContainer() =
    let lf =
      LoggerFactory.Create(fun builder ->
        builder
          .AddPerlaLogger()
#if DEBUG
          .SetMinimumLevel(LogLevel.Debug)
#else
          .SetMinimumLevel(LogLevel.Information)
#endif
        |> ignore)

    let AppLogger = lf.CreateLogger("Perla")

    let directories = PerlaDirectories.Create()

    directories.SetCwdToProject()

    let platform = PlatformOps.Create(AppLogger)

    let requestHandler =
      RequestHandler.Create {
        Logger = AppLogger
        PlatformOps = platform
        PerlaDirectories = directories
      }

    let pfsm =
      FileSystem.GetManager {
        Logger = AppLogger
        PlatformOps = platform
        PerlaDirectories = directories
        RequestHandler = requestHandler
      }

    AppContainer.Create {
      Logger = AppLogger
      Directories = directories
      FsManager = pfsm
      Platform = platform
      RequestHandler = requestHandler
    }

[<EntryPoint>]
let main argv =

  let appContainer = Env.SetupAppContainer()

  rootCommand argv {
    description "The Perla Dev Server!"

    configure(fun cfg ->
      // don't replace leading @ strings e.g. @lit-labs/task
      cfg.ResponseFileTokenReplacer <- null)

    inputs Input.context
    helpActionAsync

    addCommands [
      Commands.NewProject appContainer
      Commands.Install appContainer
      Commands.AddPackage appContainer
      Commands.RemovePackage appContainer
      Commands.ListPackages appContainer
      Commands.Serve appContainer
      Commands.Build appContainer
      Commands.Test appContainer
      Commands.Template appContainer
      Commands.Describe appContainer
    ]
  }
  |> Async.AwaitTask
  |> Async.RunSynchronously
