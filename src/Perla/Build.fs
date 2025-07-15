namespace Perla.Build

open System

open AngleSharp
open AngleSharp.Html.Dom

open Perla
open Perla.Types
open Perla.Units
open Perla.Logger

open FSharp.UMX
open FsToolkit.ErrorHandling

open FSharp.Data.Adaptive
open IcedTasks
open System.IO
open Microsoft.Extensions.Logging
open AngleSharp.Html.Parser

type BuildServiceArgs = {
  Logger: Microsoft.Extensions.Logging.ILogger
  FsManager: Perla.FileSystem.PerlaFsManager
  EsbuildService: Perla.Esbuild.EsbuildService
  ExtensibilityService: Perla.Extensibility.ExtensibilityService
  VirtualFileSystem: Perla.VirtualFs.VirtualFileSystem
  FableService: Perla.Fable.FableService
}

type BuildOptions = { enablePreview: bool }

[<Interface>]
type BuildService =
  abstract RunFable: config: PerlaConfig aval -> CancellableTask<unit>

  abstract CleanOutput: config: PerlaConfig aval -> unit

  abstract LoadPlugins:
    config: PerlaConfig aval * vfsOutputDir: string<SystemPath> -> unit

  abstract LoadVfs: config: PerlaConfig aval -> CancellableTask<unit>

  abstract CopyVfsToDisk:
    vfsOutputDir: string<SystemPath> -> CancellableTask<string<SystemPath>>

  abstract EmitEnvFile:
    config: PerlaConfig aval * tempDir: string<SystemPath> -> unit

  abstract RunEsbuild:
    config: PerlaConfig aval *
    tempDir: string<SystemPath> *
    cssPaths: seq<string<ServerUrl>> *
    jsBundleEntrypoints: seq<string<ServerUrl>> *
    externals: string list ->
      CancellableTask<string<SystemPath>>

  abstract MoveOrCopyOutput:
    config: PerlaConfig aval *
    tempDir: string<SystemPath> *
    esbuildOutput: string<SystemPath> ->
      unit

  abstract WriteIndex:
    config: PerlaConfig aval *
    document: AngleSharp.Html.Dom.IHtmlDocument *
    map: Perla.PkgManager.ImportMap *
    jsPaths: seq<string<ServerUrl>> *
    cssPaths: seq<string<ServerUrl>> ->
      CancellableTask<unit>

[<RequireQualifiedAccess>]
module Build =

  let EnsureBody(document: IHtmlDocument) =
    match document.Body with
    | null ->
      let b = document.CreateElement("body")
      document.AppendChild(b) |> ignore
      b
    | body -> body

  let EnsureHead(document: IHtmlDocument) =
    match document.Head with
    | null ->
      let h = document.CreateElement("head")
      document.InsertBefore(h, document.Body) |> ignore
      h
    | head -> head

  let insertCssFiles
    (document: IHtmlDocument, cssEntryPoints: string<ServerUrl> seq)
    =
    let head = EnsureHead document

    for file in cssEntryPoints do
      let style = document.CreateElement("link")
      style.SetAttribute("rel", "stylesheet")
      style.SetAttribute("href", UMX.untag file)
      style |> head.AppendChild |> ignore

  let insertImportMap
    (document: IHtmlDocument, importMap: PkgManager.ImportMap)
    =
    let head = EnsureHead document
    let script = document.CreateElement("script")
    script.SetAttribute("type", "importmap")
    script.TextContent <- importMap.ToJson()
    head.AppendChild(script) |> ignore

  let insertJsFiles
    (document: IHtmlDocument, jsEntryPoints: string<ServerUrl> seq)
    =
    let body = EnsureBody document

    for entryPoint in jsEntryPoints do
      let script = document.CreateElement("script")
      script.SetAttribute("type", "module")
      script.SetAttribute("src", UMX.untag entryPoint)
      body.AppendChild(script) |> ignore

  let EntryPoints(document: IHtmlDocument) =
    let cssBundles =
      document.QuerySelectorAll("[data-entry-point][rel=stylesheet]")
      |> Seq.choose(fun el -> option {
        let! href = el.Attributes["href"]

        if String.IsNullOrWhiteSpace href.Value then
          return! None
        else
          return UMX.tag<ServerUrl> href.Value
      })

    let jsBundles =
      document.QuerySelectorAll("[data-entry-point][type=module]")
      |> Seq.choose(fun el -> option {
        let! dataEntryPoint = el.Attributes["data-entry-point"]
        let! entryPoint = dataEntryPoint.Value

        if entryPoint = "standalone" then
          return! None
        else
          let! src = el.Attributes["src"]
          return UMX.tag<ServerUrl> src.Value
      })

    let standaloneBundles =
      document.QuerySelectorAll("[data-entry-point=standalone][type=module]")
      |> Seq.choose(fun el -> option {
        let! src = el.Attributes["src"]

        if String.IsNullOrWhiteSpace src.Value then
          return! None
        else
          return UMX.tag<ServerUrl> src.Value
      })

    cssBundles, jsBundles, standaloneBundles

  let Externals(config: PerlaConfig) = seq {

    if config.enableEnv && config.build.emitEnvFile then
      UMX.untag config.envPath
      Constants.EnvBareImport

    yield! config.esbuild.externals
  }

  let Index
    (
      document: IHtmlDocument,
      importMap: PkgManager.ImportMap,
      jsExtras: string<ServerUrl> seq,
      cssExtras: string<ServerUrl> seq
    ) =

    insertCssFiles(document, cssExtras)

    // importmap needs to go first
    insertImportMap(document, importMap)

    // remove any existing entry points, we don't need them at this point
    document.QuerySelectorAll("[data-entry-point][type=module]")
    |> Seq.iter(_.Remove())

    document.QuerySelectorAll("[data-entry-point=standalone][type=module]")
    |> Seq.iter(_.Remove())

    document.QuerySelectorAll("[data-entry-point][rel=stylesheet]")
    |> Seq.iter(_.Remove())

    // insert the resolved entry points which should match paths in mounted directories
    insertJsFiles(document, jsExtras)

    document.Minify()

module BuildService =
  let Create(args: BuildServiceArgs) : BuildService =
    { new BuildService with
        member _.RunFable(config) = cancellableTask {
          let config = config |> AVal.force

          match config.fable with
          | None ->
            args.Logger.LogWarning(
              "Fable configuration not found. Skipping Fable build."
            )

            return ()
          | Some fableConfig ->
            args.Logger.LogInformation(
              "Fable configuration found. Running Fable build."
            )

            do! args.FableService.Run fableConfig
        }

        member _.CleanOutput(config) =
          let config = config |> AVal.force
          let outDir = DirectoryInfo(UMX.untag config.build.outDir)

          try
            outDir.Delete(true)
          with ex ->
            args.Logger.LogWarning(
              "Failed to clean output directory {path}: {error}",
              outDir.FullName,
              ex.Message
            )

        member _.LoadPlugins(config, vfsOutputDir) =
          let config = config |> AVal.force
          let plugins = args.FsManager.ResolvePluginPaths()

          let isEsbuildPluginPresent =
            config.plugins |> List.contains Constants.PerlaEsbuildPluginName

          let isPathsReplacerPresent =
            config.plugins
            |> List.contains Constants.PerlaPathsReplacerPluginName

          let defaultPlugins = seq {
            if isPathsReplacerPresent || not(Map.isEmpty config.paths) then
              ImportMaps.createPathsReplacerPlugin
                (AVal.constant config.paths)
                vfsOutputDir

            if isEsbuildPluginPresent then
              args.EsbuildService.GetPlugin config.esbuild
          }

          args.ExtensibilityService.LoadPlugins(plugins, defaultPlugins)
          |> Result.teeError(fun err ->
            args.Logger.LogError("Failed to load plugins: {error}", err))
          |> Result.ignore
          |> Result.ignoreError

        member _.LoadVfs(config) = cancellableTask {
          let config = config |> AVal.force
          return! args.VirtualFileSystem.Load config.mountDirectories
        }

        member _.CopyVfsToDisk(vfsOutputDir) = cancellableTask {
          return! args.VirtualFileSystem.ToDisk vfsOutputDir
        }

        member _.EmitEnvFile(config, tempDir) =
          let config = config |> AVal.force

          if config.build.emitEnvFile then
            args.Logger.LogInformation("Writing Env File")
            args.FsManager.EmitEnvFile(config, tempDir)

        member _.RunEsbuild
          (config, tempDir, cssPaths, jsBundleEntrypoints, externals)
          =
          cancellableTask {
            let config = config |> AVal.force
            let esbuildOutput = Path.Combine(UMX.untag tempDir, "esbuild")

            Directory.CreateDirectory esbuildOutput |> ignore

            let isEsbuildPluginPresent =
              config.plugins |> List.contains Constants.PerlaEsbuildPluginName

            if not isEsbuildPluginPresent then
              return UMX.tag esbuildOutput
            else

            for entrypoint in jsBundleEntrypoints do
              do!
                args.EsbuildService.ProcessJS(
                  entrypoint,
                  tempDir,
                  config.build.outDir,
                  {
                    config.esbuild with
                        externals =
                          externals @ (Build.Externals config |> Seq.toList)
                  }
                )

            for entrypoint in cssPaths do
              do!
                args.EsbuildService.ProcessCss(
                  entrypoint,
                  UMX.tag esbuildOutput,
                  config.build.outDir,
                  config.esbuild
                )

            return UMX.tag esbuildOutput
          }

        member _.MoveOrCopyOutput(config, tempDir, esbuildOutputPath) =
          let config = config |> AVal.force
          let outDir = config.build.outDir

          let isEsbuildPluginPresent =
            config.plugins |> List.contains Constants.PerlaEsbuildPluginName

          if isEsbuildPluginPresent then
            if config.useLocalPkgs then
              args.Logger.LogDebug(
                "Copying all files from esbuild output to outDir (esbuild present, useLocalPkgs true)"
              )

              args.FsManager.CopyFiles(
                DirectoryInfo(UMX.untag esbuildOutputPath),
                outDir
              )
            else
              args.Logger.LogDebug(
                "esbuild present, useLocalPkgs false: skipping full esbuild output copy (only globs will be copied)"
              )

              () // Only CopyGlobs below
          else
            args.Logger.LogDebug(
              "Copying all files from tempDir to outDir (esbuild not present)"
            )

            args.FsManager.CopyFiles(DirectoryInfo(UMX.untag tempDir), outDir)

          // globs are always copied
          args.FsManager.CopyGlobs config.build

        member _.WriteIndex(config, document, map, jsPaths, cssPaths) = cancellableTask {
          let! token = CancellableTask.getCancellationToken()
          let config = config |> AVal.force
          let indexContent = Build.Index(document, map, jsPaths, cssPaths)

          let outPath =
            Path.Combine(UMX.untag config.build.outDir, "index.html")

          do! File.WriteAllTextAsync(outPath, indexContent, token)
        }
    }
