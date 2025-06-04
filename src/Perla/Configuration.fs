﻿module Perla.Configuration

open FSharp.UMX
open Perla.Types
open Perla.Units
open Perla.PackageManager.Types
open Perla.Logger
open System.Runtime.InteropServices
open FsToolkit.ErrorHandling

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Types =

  type DevServerField =
    | Port of int
    | Host of string
    | LiveReload of bool
    | UseSSL of bool
    | MinifySources of bool

  [<RequireQualifiedAccess>]
  type TestingField =
    | Browsers of Browser seq
    | Includes of string seq
    | Excludes of string seq
    | Watch of bool
    | Headless of bool
    | BrowserMode of BrowserMode

  type FableField =
    | Project of string
    | Extension of string
    | SourceMaps of bool
    | OutDir of bool

  [<RequireQualifiedAccess>]
  type PerlaWritableField =
    | Configuration of RunConfiguration
    | Provider of Provider
    | Dependencies of Dependency seq
    | DevDependencies of Dependency seq
    | Fable of FableField seq
    | Paths of Map<string<BareImport>, string<ResolutionUrl>>

open Types
open System.Text.Json.Nodes

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Defaults =

  let FableConfig: FableConfig = {
    project = UMX.tag "./src/App.fsproj"
    extension = UMX.tag ".fs.js"
    sourceMaps = true
    outDir = None
  }

  let DevServerConfig: DevServerConfig = {
    port = 7331
    host = "localhost"
    liveReload = true
    useSSL = false
    proxy = Map.empty
  }

  let EsbuildConfig: EsbuildConfig = {
    esBuildPath = FileSystem.FileSystem.EsbuildBinaryPath None
    version = UMX.tag Constants.Esbuild_Version
    ecmaVersion = Constants.Esbuild_Target
    minify = true
    injects = Seq.empty
    externals = Seq.empty
    fileLoaders =
      [ ".png", "file"; ".woff", "file"; ".woff2", "file"; ".svg", "file" ]
      |> Map.ofList
    jsxAutomatic = false
    jsxImportSource = None
  }

  let BuildConfig = {
    includes = Seq.empty
    excludes = seq {
      "./**/obj/**"
      "./**/bin/**"
      "./**/*.fs"
      "./**/*.fsi"
      "./**/*.fsproj"
    }
    outDir = UMX.tag "./dist"
    emitEnvFile = true
  }

  let TestConfig = {
    browsers = [ Browser.Chromium ]
    includes = [
      "**/*.test.js"
      "**/*.spec.js"
      "**/*.Test.fs.js"
      "**/*.Spec.fs.js"
    ]
    excludes = []
    watch = false
    headless = true
    browserMode = BrowserMode.Parallel
    fable = None
  }

  let PerlaConfig = {
    index = UMX.tag Constants.IndexFile
    runConfiguration = RunConfiguration.Production
    provider = Provider.Jspm
    plugins = [ Constants.PerlaEsbuildPluginName ]
    build = BuildConfig
    devServer = DevServerConfig
    esbuild = EsbuildConfig
    testing = TestConfig
    fable = None
    mountDirectories =
      Map.ofList [ UMX.tag<ServerUrl> "/src", UMX.tag<UserPath> "./src" ]
    enableEnv = true
    envPath = UMX.tag Constants.EnvPath
    paths = Map.empty
    dependencies = Seq.empty
    devDependencies = Seq.empty
  }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module internal Json =
  open Perla.Json

  let getConfigDocument(perlaJsonText: string) : JsonObject =
    JsonObject
      .Parse(
        perlaJsonText,
        nodeOptions = DefaultJsonNodeOptions(),
        documentOptions = DefaultJsonDocumentOptions()
      )
      .AsObject()

  let updateFileFields
    (jsonContents: byref<JsonObject option>)
    (fields: PerlaWritableField seq)
    : unit =
    let configuration =
      fields
      |> Seq.tryPick(fun f ->
        match f with
        | PerlaWritableField.Configuration config -> Some config
        | _ -> None)
      |> Option.map(fun f -> f.AsString)

    let provider =
      fields
      |> Seq.tryPick(fun f ->
        match f with
        | PerlaWritableField.Provider config -> Some config
        | _ -> None)
      |> Option.map(fun f -> f.AsString)

    let dependencies =
      fields
      |> Seq.tryPick(fun f ->
        match f with
        | PerlaWritableField.Dependencies deps -> Some deps
        | _ -> None)

    let devDependencies =
      fields
      |> Seq.tryPick(fun f ->
        match f with
        | PerlaWritableField.DevDependencies deps -> Some deps
        | _ -> None)

    let paths =
      fields
      |> Seq.tryPick(fun f ->
        match f with
        | PerlaWritableField.Paths paths -> Some paths
        | _ -> None)

    let fable =
      fields
      |> Seq.tryPick(fun f ->
        match f with
        | PerlaWritableField.Fable fields ->
          let mutable f = {|
            project = None
            extension = None
            sourceMaps = None
            outDir = None
          |}

          for field in fields do
            match field with
            | FableField.Project path -> f <- {| f with project = Some path |}
            | FableField.Extension ext ->
              f <- {| f with extension = Some ext |}
            | FableField.SourceMaps sourceMaps ->
              f <- {|
                f with
                    sourceMaps = Some sourceMaps
              |}
            | FableField.OutDir outDir ->
              f <- {| f with outDir = Some outDir |}

          Some f
        | _ -> None)

    let addConfig(content: JsonObject) =
      match configuration with
      | Some config -> content["runConfiguration"] <- Json.ToNode(config)
      | None -> ()

      content

    let addProvider(content: JsonObject) =
      match provider with
      | Some config -> content["provider"] <- Json.ToNode(config)
      | None -> ()

      content

    let addDeps(content: JsonObject) =
      match dependencies with
      | Some deps -> content["dependencies"] <- Json.ToNode(deps)
      | None -> ()

      content

    let addDevDeps(content: JsonObject) =
      match devDependencies with
      | Some deps -> content["devDependencies"] <- Json.ToNode(deps)
      | None -> ()

      content

    let addFable(content: JsonObject) =
      match fable with
      | Some fable -> content["fable"] <- Json.ToNode(fable)
      | None -> ()

      content

    let addPaths(content: JsonObject) =
      match paths with
      | Some paths -> content["paths"] <- Json.ToNode(paths)
      | None -> ()

      content

    let content =
      match jsonContents with
      | Some content -> content
      | None ->
        JsonObject
          .Parse($"""{{ "$schema": "{Constants.JsonSchemaUrl}" }}""")
          .AsObject()

    match
      content["$schema"]
      |> Option.ofObj
      |> Option.map(fun schema -> schema.GetValue<string>() |> Option.ofObj)
      |> Option.flatten
    with
    | Some _ -> ()
    | None -> content["$schema"] <- Json.ToNode(Constants.JsonSchemaUrl)

    jsonContents <-
      content
      |> addConfig
      |> addProvider
      |> addDeps
      |> addDevDeps
      |> addFable
      |> addPaths
      |> Some

module internal ConfigExtraction =

  [<RequireQualifiedAccess>]
  module FromDecoders =
    open Json.ConfigDecoders

    let GetFable(config: FableConfig option, fable: DecodedFableConfig option) = option {
      let! decoded = fable
      let fable = defaultArg config Defaults.FableConfig

      let outDir = decoded.outDir |> Option.orElseWith(fun () -> fable.outDir)

      return {
        fable with
            project = defaultArg decoded.project fable.project
            extension = defaultArg decoded.extension fable.extension
            sourceMaps = defaultArg decoded.sourceMaps fable.sourceMaps
            outDir = outDir
      }
    }

    let GetDevServer
      (config: DevServerConfig, devServer: DecodedDevServer option)
      =
      option {
        let! decoded = devServer

        return {
          config with
              port = defaultArg decoded.port config.port
              host = defaultArg decoded.host config.host
              liveReload = defaultArg decoded.liveReload config.liveReload
              useSSL = defaultArg decoded.useSSL config.useSSL
              proxy = defaultArg decoded.proxy config.proxy
        }
      }

    let GetBuild(config: BuildConfig, build: DecodedBuild option) = option {
      let! decoded = build

      return {
        config with
            includes = defaultArg decoded.includes config.includes
            excludes = defaultArg decoded.excludes config.excludes
            outDir = defaultArg decoded.outDir config.outDir
            emitEnvFile = defaultArg decoded.emitEnvFile config.emitEnvFile
      }
    }

    let GetEsbuild(config: EsbuildConfig, esbuild: DecodedEsbuild option) = option {
      let! decoded = esbuild

      return {
        config with
            esBuildPath = defaultArg decoded.esBuildPath config.esBuildPath
            version = defaultArg decoded.version config.version
            ecmaVersion = defaultArg decoded.ecmaVersion config.ecmaVersion
            minify = defaultArg decoded.minify config.minify
            injects = defaultArg decoded.injects config.injects
            externals = defaultArg decoded.externals config.externals
            fileLoaders = defaultArg decoded.fileLoaders config.fileLoaders
            jsxAutomatic = defaultArg decoded.jsxAutomatic config.jsxAutomatic
            jsxImportSource = decoded.jsxImportSource
      }
    }

    let GetTesting(config: TestConfig, testing: DecodedTesting option) = option {
      let! testing = testing

      return {
        config with
            browsers = defaultArg testing.browsers config.browsers
            includes = defaultArg testing.includes config.includes
            excludes = defaultArg testing.excludes config.excludes
            watch = defaultArg testing.watch config.watch
            headless = defaultArg testing.headless config.headless
            browserMode = defaultArg testing.browserMode config.browserMode
            fable = GetFable(config.fable, testing.fable)
      }
    }

    let GetPlugins(plugins: string list option) = option {
      let! plugins = plugins

      if plugins.Length = 0 then
        return [ Constants.PerlaEsbuildPluginName ]
      else
        return plugins
    }

  [<RequireQualifiedAccess>]
  module FromFields =
    let GetServerFields
      (config: DevServerConfig, serverOptions: DevServerField seq option)
      =
      let getDefaults() = seq {
        DevServerField.Port config.port
        DevServerField.Host config.host
        DevServerField.LiveReload config.liveReload
        DevServerField.UseSSL config.useSSL
      }

      let options = serverOptions |> Option.defaultWith getDefaults

      if Seq.isEmpty options then getDefaults() else options

    let GetMinify(config: RunConfiguration, serverOptions: DevServerField seq) =
      serverOptions
      |> Seq.tryPick(fun opt ->
        match opt with
        | MinifySources minify -> Some minify
        | _ -> None)
      |> Option.defaultWith(fun _ ->
        match config with
        | RunConfiguration.Production -> true
        | RunConfiguration.Development -> false)

    let GetDevServerOptions
      (config: DevServerConfig, serverOptions: DevServerField seq)
      =
      serverOptions
      |> Seq.fold
        (fun current next ->
          match next with
          | Port port -> { current with port = port }
          | Host host -> { current with host = host }
          | LiveReload liveReload -> { current with liveReload = liveReload }
          | UseSSL useSSL -> { current with useSSL = useSSL }
          | _ -> current)
        config

    let GetTesting
      (testing: TestConfig, testingOptions: TestingField seq option)
      =
      defaultArg testingOptions Seq.empty
      |> Seq.fold
        (fun current next ->
          match next with
          | TestingField.Browsers value -> { current with browsers = value }
          | TestingField.Includes value -> { current with includes = value }
          | TestingField.Excludes value -> { current with excludes = value }
          | TestingField.Watch value -> { current with watch = value }
          | TestingField.Headless value -> { current with headless = value }
          | TestingField.BrowserMode value ->
              { current with browserMode = value })
        testing


  // will enable in the future
  let FromEnv(config: PerlaConfig) : PerlaConfig = config

  let FromCli
    (runConfig: RunConfiguration option)
    (provider: Provider option)
    (serverOptions: DevServerField seq option)
    (testingOptions: TestingField seq option)
    (config: PerlaConfig)
    : PerlaConfig =
    let configuration =
      defaultArg runConfig Defaults.PerlaConfig.runConfiguration

    let provider = defaultArg provider Defaults.PerlaConfig.provider

    let serverOptions =
      FromFields.GetServerFields(config.devServer, serverOptions)

    let devServer =
      FromFields.GetDevServerOptions(config.devServer, serverOptions)

    let testing = FromFields.GetTesting(config.testing, testingOptions)

    let esbuild = {
      config.esbuild with
          minify = FromFields.GetMinify(configuration, serverOptions)
    }

    {
      config with
          provider = provider
          runConfiguration = configuration
          devServer = devServer
          esbuild = esbuild
          testing = testing
    }


  let FromFile
    (fileContent: JsonObject option)
    (config: PerlaConfig)
    : PerlaConfig =
    option {
      let! userConfig = option {
        let! fileContent =
          fileContent
          |> Option.map(fun fileContent ->
            fileContent.ToJsonString() |> Json.Json.FromConfigFile)

        match fileContent with
        | Ok decoded -> return decoded
        | Error err ->
          Logger.log $"Failed to parse config file: {err}"
          return! None
      }

      let fable = FromDecoders.GetFable(config.fable, userConfig.fable)

      let devServer =
        FromDecoders.GetDevServer(config.devServer, userConfig.devServer)
        |> Option.defaultValue Defaults.DevServerConfig

      let build =
        FromDecoders.GetBuild(config.build, userConfig.build)
        |> Option.defaultValue Defaults.BuildConfig

      let esbuild =
        FromDecoders.GetEsbuild(config.esbuild, userConfig.esbuild)
        |> Option.defaultValue Defaults.EsbuildConfig

      let testing =
        FromDecoders.GetTesting(config.testing, userConfig.testing)
        |> Option.defaultValue Defaults.TestConfig

      let plugins =
        FromDecoders.GetPlugins(userConfig.plugins)
        |> Option.defaultValue Defaults.PerlaConfig.plugins

      return {
        config with
            index = defaultArg userConfig.index config.index
            runConfiguration =
              defaultArg userConfig.runConfiguration config.runConfiguration
            provider = defaultArg userConfig.provider config.provider
            plugins = plugins
            build = build
            devServer = devServer
            fable = fable
            esbuild = esbuild
            testing = testing
            mountDirectories =
              defaultArg userConfig.mountDirectories config.mountDirectories
            enableEnv = defaultArg userConfig.enableEnv config.enableEnv
            envPath = defaultArg userConfig.envPath config.envPath
            paths = defaultArg userConfig.paths config.paths
            dependencies =
              defaultArg userConfig.dependencies config.dependencies
            devDependencies =
              defaultArg userConfig.devDependencies config.devDependencies
      }
    }
    |> Option.defaultValue Defaults.PerlaConfig


type ConfigurationManager
  (
    readPerlaJsonText: unit -> string option,
    writePerlaJsonText: JsonObject option -> unit
  ) =

  let _getPerlaText() =
    match readPerlaJsonText() with
    | Some text -> text |> Json.getConfigDocument |> Some
    | None -> None

  let mutable _runConfig = None
  let mutable _provider = None
  let mutable _serverOptions = None

  let mutable _testingOptions = None

  let mutable _fileConfig = _getPerlaText()

  let mutable _configContents =
    Defaults.PerlaConfig
    |> ConfigExtraction.FromEnv
    |> ConfigExtraction.FromFile _fileConfig
    |> ConfigExtraction.FromCli
      _runConfig
      _provider
      _serverOptions
      _testingOptions


  let runPipeline() =
    _configContents <-
      Defaults.PerlaConfig
      |> ConfigExtraction.FromEnv
      |> ConfigExtraction.FromFile _fileConfig
      |> ConfigExtraction.FromCli
        _runConfig
        _provider
        _serverOptions
        _testingOptions

  member _.CurrentConfig = _configContents

  member _.UpdateFromCliArgs
    (
      [<Optional>] ?runConfig: RunConfiguration,
      [<Optional>] ?provider: Provider,
      [<Optional>] ?serverOptions: DevServerField seq,
      [<Optional>] ?testingOptions: TestingField seq
    ) =
    _runConfig <- runConfig
    _serverOptions <- serverOptions
    _provider <- provider
    _testingOptions <- testingOptions

    runPipeline()

  member _.UpdateFromFile() =
    _fileConfig <- _getPerlaText()
    runPipeline()

  member _.WriteFieldsToFile(newValues: PerlaWritableField seq) =
    Json.updateFileFields &_fileConfig newValues
    writePerlaJsonText _fileConfig
    runPipeline()


let ConfigurationManager =
  ConfigurationManager(
    FileSystem.FileSystem.PerlaConfigText,
    (fun content -> FileSystem.FileSystem.WritePerlaConfig(?config = content))
  )
