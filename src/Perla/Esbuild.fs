namespace Perla.Esbuild


open IcedTasks
open FSharp.UMX
open FSharp.Data.Adaptive

open Perla
open Perla.Types
open Perla.Units
open Perla.FileSystem
open Perla.Plugins

[<RequireQualifiedAccess; Struct>]
type LoaderType =
  | Typescript
  | Tsx
  | Jsx
  | Css

type EsbuildServiceArgs = {
  Cwd: string<SystemPath>
  PerlaFsManager: PerlaFsManager
  Logger: Microsoft.Extensions.Logging.ILogger
  PlatformOps: PlatformOps
}

[<Interface>]
type EsbuildService =

  abstract ProcessJS:
    entrypoint: string<ServerUrl> *
    sourcesPath: string<SystemPath> *
    outdir: string<SystemPath> *
    config: EsbuildConfig ->
      CancellableTask<unit>

  abstract ProcessCss:
    entrypoint: string<ServerUrl> *
    sourcesPath: string<SystemPath> *
    outdir: string<SystemPath> *
    config: EsbuildConfig ->
      CancellableTask<unit>

  abstract GetPlugin: config: EsbuildConfig -> PluginInfo

[<RequireQualifiedAccess>]
module Esbuild =

  let singleFileCmd
    (
      source: string,
      loader: LoaderType option,
      config: EsbuildConfig,
      tsconfig: string option,
      platformOps: PlatformOps,
      pfsm: PerlaFsManager
    ) =
    cancellableTask {
      let loaderStr =
        match loader with
        | Some loader ->
          match loader with
          | LoaderType.Typescript -> Some "ts"
          | LoaderType.Tsx -> Some "tsx"
          | LoaderType.Jsx -> Some "jsx"
          | LoaderType.Css -> Some "css"
        | None -> None

      let! result =
        platformOps.RunEsbuildTransform(
          pfsm.ResolveEsbuildPath(),
          source,
          loaderStr,
          config.ecmaVersion,
          config.minify,
          config.jsxAutomatic,
          config.jsxImportSource,
          tsconfig
        )

      return result
    }

  let Create(serviceArgs: EsbuildServiceArgs) =
    { new EsbuildService with
        member this.GetPlugin(config: EsbuildConfig) : PluginInfo =
          let shouldTransform: FilePredicate =
            fun extension ->
              [ ".jsx"; ".tsx"; ".ts"; ".css"; ".js" ]
              |> List.contains extension

          let transform: TransformAsync =
            fun args -> async {

              let loader =
                match args.extension with
                | ".css" -> Some LoaderType.Css
                | ".jsx" -> Some LoaderType.Jsx
                | ".tsx" -> Some LoaderType.Tsx
                | ".ts" -> Some LoaderType.Typescript
                | ".js" -> None
                | _ -> None

              let tsConfig =
                serviceArgs.PerlaFsManager.ResolveTsConfig |> AVal.force

              let! result =
                singleFileCmd(
                  args.content,
                  loader,
                  config,
                  tsConfig,
                  serviceArgs.PlatformOps,
                  serviceArgs.PerlaFsManager
                )

                |> Async.AwaitCancellableTask

              return {
                content = result
                extension = if args.extension = ".css" then ".css" else ".js"
                fileLocation = args.fileLocation
              }
            }

          plugin Constants.PerlaEsbuildPluginName {
            should_process_file shouldTransform
            with_transform transform
          }

        member this.ProcessCss
          (entrypoint, sourcesPath, outdir, config: EsbuildConfig)
          : CancellableTask<unit> =
          cancellableTask {
            let esbuildPath = serviceArgs.PerlaFsManager.ResolveEsbuildPath()

            do!
              serviceArgs.PlatformOps.RunEsbuildCss(
                esbuildPath,
                sourcesPath,
                UMX.untag entrypoint,
                UMX.untag outdir,
                config.minify,
                config.fileLoaders
              )

            return ()
          }

        member this.ProcessJS
          (entrypoint, sourcesPath, outdir, config: EsbuildConfig)
          : CancellableTask<unit> =
          cancellableTask {
            let esbuildPath = serviceArgs.PerlaFsManager.ResolveEsbuildPath()

            do!
              serviceArgs.PlatformOps.RunEsbuildJs(
                esbuildPath,
                sourcesPath,
                UMX.untag entrypoint,
                UMX.untag outdir,
                config
              )

            return ()
          }

    }
