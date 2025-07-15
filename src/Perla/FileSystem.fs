namespace Perla.FileSystem

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Text.Json.Nodes
open Microsoft.Extensions.Logging
open Perla.Types

open IcedTasks
open FSharp.UMX

open FsToolkit.ErrorHandling

open FSharp.Data.Adaptive

open Fake.IO.Globbing.Operators
open Fake.IO.Globbing

open Spectre.Console

open Perla
open Perla.Units
open Perla.Json
open Perla.RequestHandler

[<RequireQualifiedAccess>]
type PerlaFileChange =
  | Index
  | PerlaConfig
  | ImportMap

[<Interface>]
type PerlaFsManager =

  abstract CopyFiles:
    sourcePath: DirectoryInfo * targetPath: string<SystemPath> -> unit

  abstract PerlaConfiguration: Types.PerlaConfig aval

  abstract ResolveIndexPath: string<SystemPath> aval

  abstract ResolveIndex: string aval

  abstract DotEnvContents: Map<string, string> aval

  abstract ResolveImportMap: PkgManager.ImportMap aval

  abstract ResolveTsConfig: string option aval

  abstract ResolveOfflineTemplatesConfig:
    unit -> CancellableTask<DecodedTemplateConfiguration>

  abstract ResolveDescriptionsFile: unit -> CancellableTask<Map<string, string>>

  abstract ResolvePluginPaths: unit -> (string * string)[]

  abstract ResolveEsbuildPath: unit -> string<SystemPath>

  abstract ResolveLiveReloadScript: unit -> CancellableTask<string>
  abstract ResolveWorkerScript: unit -> CancellableTask<string>
  abstract ResolveTestingHelpersScript: unit -> CancellableTask<string>
  abstract ResolveMochaRunnerScript: unit -> CancellableTask<string>

  abstract SaveImportMap: map: PkgManager.ImportMap -> CancellableTask<unit>

  abstract SavePerlaConfig: config: PerlaConfig -> CancellableTask<unit>

  abstract SavePerlaConfig:
    updates: PerlaConfig.PerlaWritableField seq -> CancellableTask<unit>


  abstract SetupEsbuild: string<Semver> -> CancellableTask<unit>

  abstract SetupFable: unit -> CancellableTask<unit>

  abstract SetupTemplate:
    user: string * repository: string<Repository> * branch: string<Branch> ->
      CancellableTask<(string<SystemPath> * DecodedTemplateConfiguration) option>

  abstract CopyGlobs: buildConfig: BuildConfig -> unit

  abstract EmitEnvFile:
    config: PerlaConfig * ?tmpPath: string<SystemPath> -> unit

[<AutoOpen>]
module Operators =
  let inline (/) a b = Path.Combine(a, b)

  let inline (|/) (a: string<SystemPath>) (b: string) =
    Path.Combine(UMX.untag a, UMX.untag b) |> UMX.tag<SystemPath>

type PerlaFsManagerArgs = {
  Logger: ILogger
  PlatformOps: PlatformOps
  PerlaDirectories: PerlaDirectories
  RequestHandler: RequestHandler
}

[<RequireQualifiedAccess>]
module FileSystem =

  let resolveEsbuildPath
    (platform: PlatformOps, directories: PerlaDirectories)
    (version: string<Semver>)
    =
    let bin = if platform.IsWindows() then "" else "bin"
    let exec = if platform.IsWindows() then ".exe" else ""

    directories.PerlaArtifactsRoot
    |/ UMX.untag version
    |/ "package"
    |/ bin
    |/ $"esbuild{exec}"
    |> UMX.untag
    |> Path.GetFullPath
    |> UMX.tag<SystemPath>

  let GetManager(args: PerlaFsManagerArgs) : PerlaFsManager =
    { new PerlaFsManager with
        member _.CopyFiles(sourcePath, targetPath) =
          let progress = AnsiConsole.Progress()
          let files = sourcePath.GetFiles("*", SearchOption.AllDirectories)

          let targetDir = DirectoryInfo(UMX.untag targetPath)
          targetDir.Create()

          progress.Start(fun ctx ->
            let tsk =
              ctx.AddTask("Creating project...", true, maxValue = files.Length)

            files
            |> Array.Parallel.iter(fun file ->
              // Compute the relative path from the source root
              let relPath =
                Path.GetRelativePath(sourcePath.FullName, file.FullName)

              let destPath = Path.Combine(UMX.untag targetPath, relPath)

              destPath
              |> Path.GetDirectoryName
              |> nonNull
              |> Path.GetFullPath
              |> Directory.CreateDirectory
              |> ignore

              File.Copy(file.FullName, destPath, true)
              tsk.Increment 1)

            tsk.StopTask())

        member _.PerlaConfiguration = adaptive {
          let path = UMX.untag args.PerlaDirectories.PerlaConfigPath
          let! content = AdaptiveFile.TryReadAllText path

          match content with
          | None -> return Defaults.PerlaConfig
          | Some content -> return PerlaConfig.FromString content
        }

        member this.ResolveIndexPath =
          this.PerlaConfiguration |> AVal.map _.index

        member this.ResolveIndex = adaptive {
          let! indexPath = this.ResolveIndexPath
          let! content = AdaptiveFile.TryReadAllText(UMX.untag indexPath)
          return defaultArg content ""
        }

        member _.DotEnvContents = adaptive {

          let envVarRegex =
            RegularExpressions.Regex
              "PERLA_(?<envvarname>[a-zA-Z0-9_]+)\\s*=\\s*(?<content>.+)"

          let path = UMX.untag args.PerlaDirectories.CurrentWorkingDirectory
          let dotEnvFiles = AdaptiveDirectory.GetFiles(path, @".*\.env$")

          let parseEnvLine line =
            let matchResult = envVarRegex.Match line

            if matchResult.Success then
              Some(
                matchResult.Groups["envvarname"].Value,
                matchResult.Groups["content"].Value
              )
            else
              None

          let reduction =
            AdaptiveReduction.fold Map.empty<string, string>
            <| Map.fold(fun acc k v -> Map.add k v acc)

          let! dotEnvFilesContent =
            dotEnvFiles
            |> ASet.mapA(fun file -> adaptive {
              let! fileContent = AdaptiveFile.TryReadAllLines file.FullName
              let fileContent = fileContent |> Option.defaultValue Array.empty

              return fileContent |> Array.choose parseEnvLine |> Map.ofArray
            })
            |> ASet.reduce reduction

          let! envVars =
            Environment.GetEnvironmentVariables()
            |> Seq.cast<Collections.DictionaryEntry>
            |> Seq.filter(fun entry ->
              nonNull(entry.Key.ToString()).StartsWith("PERLA_"))
            |> Seq.map(fun entry ->
              nonNull(entry.Key.ToString()).Replace("PERLA_", ""),
              (nonNull entry.Value).ToString() |> nonNull)
            |> Map.ofSeq
            |> AVal.constant

          let allVars =
            dotEnvFilesContent
            |> Map.fold (fun acc k v -> Map.add k v acc) envVars

          return allVars
        }

        member _.ResolveImportMap = adaptive {
          let path =
            args.PerlaDirectories.CurrentWorkingDirectory
            |/ Constants.ImportMapName

          let! content = AdaptiveFile.TryReadAllBytes(UMX.untag path)

          let importMap = content |> Option.map Json.FromBytes

          return defaultArg importMap PkgManager.ImportMap.Empty
        }

        member _.ResolveTsConfig = adaptive {
          let path =
            args.PerlaDirectories.CurrentWorkingDirectory |/ "tsconfig.json"

          let! content = AdaptiveFile.TryReadAllText(UMX.untag path)
          return content
        }

        member _.ResolveDescriptionsFile() = cancellableTask {
          let path =
            UMX.untag args.PerlaDirectories.AssemblyRoot / "descriptions.json"
            |> UMX.tag<SystemPath>

          try
            use content = File.OpenRead(UMX.untag path)
            let! descriptions = Json.FromStream<Map<string, string>> content
            return descriptions
          with _ ->
            return Map.empty<string, string>
        }

        member _.ResolveOfflineTemplatesConfig() = cancellableTask {
          let path =
            UMX.untag args.PerlaDirectories.OfflineTemplates
            / "perla.config.json"

          use file = File.OpenRead path

          return! Json.FromStream file
        }


        member _.ResolvePluginPaths() =
          let path =
            args.PerlaDirectories.CurrentWorkingDirectory
            |/ ".perla" / "plugins"

          !! $"{path}/**/*.fsx"
          |> Seq.toArray
          |> Array.Parallel.map(fun path -> path, File.ReadAllText path)

        member this.ResolveEsbuildPath() =
          resolveEsbuildPath
            (args.PlatformOps, args.PerlaDirectories)
            (this.PerlaConfiguration |> AVal.map _.esbuild.version |> AVal.force)

        member _.ResolveLiveReloadScript() = cancellableTask {
          let! token = CancellableTask.getCancellationToken()

          let! content =
            File.ReadAllTextAsync(
              UMX.untag args.PerlaDirectories.AssemblyRoot / "livereload.js",
              token
            )

          return content
        }

        member _.ResolveMochaRunnerScript() = cancellableTask {
          let! token = CancellableTask.getCancellationToken()

          let! content =
            File.ReadAllTextAsync(
              UMX.untag args.PerlaDirectories.AssemblyRoot / "mocha-runner.js",
              token
            )

          return content
        }

        member _.ResolveTestingHelpersScript() = cancellableTask {
          let! token = CancellableTask.getCancellationToken()

          let! content =
            File.ReadAllTextAsync(
              UMX.untag args.PerlaDirectories.AssemblyRoot
              / "testing-helpers.js",
              token
            )

          return content
        }

        member _.ResolveWorkerScript() = cancellableTask {
          let! token = CancellableTask.getCancellationToken()

          let! content =
            File.ReadAllTextAsync(
              UMX.untag args.PerlaDirectories.AssemblyRoot / "worker.js",
              token
            )

          return content
        }

        member _.SaveImportMap(map) = cancellableTask {
          let! token = CancellableTask.getCancellationToken()

          let path =
            args.PerlaDirectories.CurrentWorkingDirectory
            |/ Constants.ImportMapName

          let content = Json.ToText(map)
          do! File.WriteAllTextAsync(UMX.untag path, content, token)
        }

        member _.SavePerlaConfig(config: PerlaConfig) = cancellableTask {
          let! token = CancellableTask.getCancellationToken()

          let path =
            args.PerlaDirectories.CurrentWorkingDirectory
            |/ Constants.PerlaConfigName

          let content = Json.ToText(config)
          do! File.WriteAllTextAsync(UMX.untag path, content, token)
        }

        member _.SavePerlaConfig(updates: PerlaConfig.PerlaWritableField seq) = cancellableTask {
          let! token = CancellableTask.getCancellationToken()

          let path =
            args.PerlaDirectories.CurrentWorkingDirectory
            |/ Constants.PerlaConfigName

          let! mutableConfig = taskOption {
            try
              let! content = File.ReadAllTextAsync(UMX.untag path, token)

              if String.IsNullOrWhiteSpace content then
                return! None
              else
                return
                  (JsonObject.Parse(
                    content,
                    nodeOptions = DefaultJsonNodeOptions(),
                    documentOptions = DefaultJsonDocumentOptions()
                   )
                   |> nonNull)
                    .AsObject()
            with :? FileNotFoundException ->
              return! None
          }

          let updatedContent =
            PerlaConfig.UpdateFileFields mutableConfig updates
            |> _.ToJsonString(DefaultJsonOptions())

          do! File.WriteAllTextAsync(UMX.untag path, updatedContent, token)
        }

        member this.SetupEsbuild(version) = cancellableTask {
          do! args.RequestHandler.DownloadEsbuild version

          if not(args.PlatformOps.IsWindows()) then
            let esbuildBinaryPath = this.ResolveEsbuildPath() |> UMX.untag

            args.Logger.LogInformation(
              "Setting executable permissions for \"{esbuildBinaryPath}\"",
              esbuildBinaryPath
            )

            // Set executable permissions on Unix-like systems
            try
              // Use UnixFileMode to set executable permissions
              File.SetUnixFileMode(
                esbuildBinaryPath,
                UnixFileMode.UserRead
                ||| UnixFileMode.UserWrite
                ||| UnixFileMode.UserExecute
                ||| UnixFileMode.GroupRead
                ||| UnixFileMode.GroupExecute
                ||| UnixFileMode.OtherRead
                ||| UnixFileMode.OtherExecute
              )

              args.Logger.LogInformation(
                "Successfully set executable permissions for {esbuildBinaryPath}",
                esbuildBinaryPath
              )
            with ex ->
              args.Logger.LogWarning(
                "Failed to set executable permissions for {esbuildBinaryPath}: {Error}",
                esbuildBinaryPath,
                ex.Message
              )

            args.Logger.LogInformation
              "This setup should happen once per esbuild version. If you see it often please report a bug."

            return ()
          else
            args.Logger.LogInformation
              "This setup should happen once per esbuild version. If you see it often please report a bug."

            return ()
        }

        member _.SetupFable() = cancellableTask {
          let! fableExists = args.PlatformOps.IsFableAvailable()

          if fableExists then
            args.Logger.LogInformation
              "Fable is already installed, skipping setup."

            return ()

          args.Logger.LogInformation "Fable is not installed, installing..."

          let! installResult = args.PlatformOps.InstallDotnetTool("fable")

          if installResult.ExitCode <> 0 then
            args.Logger.LogError(
              "Failed to install Fable: {Error}",
              installResult.StandardError
            )

            return ()

          args.Logger.LogInformation "Fable installed successfully."
          return ()
        }

        member _.SetupTemplate(user, repository, branch) = cancellableTask {
          let! token = CancellableTask.getCancellationToken()

          use! stream =
            args.RequestHandler.DownloadTemplate(user, repository, branch)

          let targetPath =
            Path.Combine(
              UMX.untag args.PerlaDirectories.Templates,
              $"{user}-{repository}-{branch}"
            )

          try
            Directory.Delete(targetPath, true)
          with _ ->
            ()

          use zip = new ZipArchive(stream)

          zip.ExtractToDirectory(
            UMX.untag args.PerlaDirectories.Templates,
            true
          )

          Directory.Move(
            Path.Combine(
              UMX.untag args.PerlaDirectories.Templates,
              $"{repository}-{branch}"
            ),
            targetPath
          )

          let! config = cancellableTask {
            try
              let! content =
                File.ReadAllBytesAsync(targetPath / "perla.config.json", token)

              let content = Json.FromBytes<DecodedTemplateConfiguration> content
              return Some content
            with error ->
              args.Logger.LogWarning(
                "Failed to decode template configuration: {error}",
                error
              )

              return None
          }

          match config with
          | Some config -> return Some(UMX.tag<SystemPath> targetPath, config)
          | None ->
            args.Logger.LogWarning(
              "No Configuration File found in template {user}/{repository}@{branch}",
              user,
              repository,
              branch
            )

            return None
        }

        member _.CopyGlobs(buildConfig: BuildConfig) =
          let outDir = UMX.untag buildConfig.outDir |> Path.GetFullPath
          let cwd = args.PerlaDirectories.CurrentWorkingDirectory |> UMX.untag

          let includes = buildConfig.includes |> Seq.toList
          let excludes = buildConfig.excludes |> Seq.toList

          let pattern = {
            BaseDirectory = cwd
            Includes = includes
            Excludes = excludes
          }

          let filesToCopy = pattern |> Seq.toArray

          let copyAndIncrement (tsk: ProgressTask) (file: string) =
            tsk.Increment 1
            let targetPath = file.Replace(cwd, outDir)

            try
              Path.GetDirectoryName targetPath
              |> nonNull
              |> Directory.CreateDirectory
              |> ignore
            with _ ->
              ()

            File.Copy(file, targetPath, true)

          AnsiConsole
            .Progress()
            .Start(fun ctx ->
              let task =
                ctx.AddTask(
                  "Copy Files to Output",
                  true,
                  filesToCopy.Length |> float
                )

              filesToCopy |> Array.Parallel.iter(copyAndIncrement task))

        member this.EmitEnvFile
          (config: PerlaConfig, ?tmpPath: string<SystemPath>)
          =
          let tmpPath = defaultArg tmpPath config.build.outDir |> UMX.untag
          let content = this.DotEnvContents |> AVal.force

          if Map.isEmpty content then
            args.Logger.LogInformation
              "No environment variables to emit, skipping."
          else
            args.Logger.LogInformation(
              "Emitting environment variables to {Path}",
              UMX.untag config.envPath
            )

          // ensure the directory exists
          Directory.CreateDirectory tmpPath |> ignore

          let content =
            content
            |> Map.fold
              (fun (sb: StringBuilder) key value ->
                sb.AppendLine $"export const {key} = \"{value}\"")
              (StringBuilder())
            |> _.ToString()

          // remove the leading slash
          let targetFile = (UMX.untag config.envPath)[1..]

          let path = Path.Combine(tmpPath, targetFile) |> Path.GetFullPath
          File.WriteAllText(path, content)
    }
