﻿namespace Perla

open Spectre.Console.Rendering

open FSharp.UMX
open Perla.Units
open Perla.PackageManager.Types
open FsToolkit.ErrorHandling

module private ImportMap =

  let private RemoveResolutionsFromMap
    (current: Map<string, string>)
    (import: string<BareImport>)
    (_: string<ResolutionUrl>)
    =
    current |> Map.remove(UMX.untag import)

  let private AddResolutionsToMap
    (current: Map<string, string>)
    (import: string<BareImport>)
    (resolution: string<ResolutionUrl>)
    =
    current |> Map.add (UMX.untag import) (UMX.untag resolution)

  [<RequireQualifiedAccess>]
  type MapUpdate =
    | Add of Map<string<BareImport>, string<ResolutionUrl>>
    | Remove of Map<string<BareImport>, string<ResolutionUrl>>

  let ModifyMap(map: ImportMap, operation: MapUpdate) =
    let imports =
      match operation with
      | MapUpdate.Add resolutions ->
        Map.fold AddResolutionsToMap map.imports resolutions
      | MapUpdate.Remove resolutions ->
        Map.fold RemoveResolutionsFromMap map.imports resolutions

    { map with imports = imports }

[<AutoOpen>]
module Lib =
  open System
  open Perla.Types
  open Spectre.Console
  open System.Text.RegularExpressions
  open ImportMap

  [<return: Struct>]
  let internal (|ParseRegex|_|) (regex: Regex) str =
    let m = regex.Match(str)

    if m.Success then
      ValueSome(List.tail [ for x in m.Groups -> x.Value ])
    else
      ValueNone

  let ExtractDependencyInfoFromUrl url =

    match url with
    | ParseRegex (Regex(@"https://cdn.skypack.dev/pin/(@?[^@]+)@v([\d.]+)(-?\w+(?!\w+)[\d.]?[\d+]+)?")) [ name
                                                                                                          version
                                                                                                          preview ] ->
      ValueSome(Provider.Skypack, name, $"{version}{preview}")
    | ParseRegex (Regex(@"https://cdn.jsdelivr.net/npm/(@?[^@]+)@([\d.]+)(-?[-]\w+[\d.]?[\d+]+)?")) [ name
                                                                                                      version
                                                                                                      preview ] ->
      ValueSome(Provider.Jsdelivr, name, $"{version}{preview}")
    | ParseRegex (Regex(@"https://ga.jspm.io/npm:(@?[^@]+)@([\d.]+)(-?[-]\w+[\d.]?[\d+]+)?")) [ name
                                                                                                version
                                                                                                preview ] ->
      ValueSome(Provider.Jspm, name, $"{version}{preview}")
    | ParseRegex (Regex(@"https://unpkg.com/(@?[^@]+)@([\d.]+)(-?[-]\w+[\d.]?[\d+]+)?")) [ name
                                                                                           version
                                                                                           preview ] ->
      ValueSome(Provider.Unpkg, name, $"{version}{preview}")
    | _ -> ValueNone

  let parseFullRepositoryName(value: string option) = voption {
    let! name = value
    let regex = Regex(@"^([-_\w\d]+)\/([-_\w\d]+):?([\w\d-_]+)?$")

    return!
      match name with
      | ParseRegex regex [ username; repository; branch ] ->
        ValueSome(username, repository, branch)
      | ParseRegex regex [ username; repository ] ->
        ValueSome(username, repository, "main")
      | _ -> ValueNone
  }

  let getTemplateAndChild(templateName: string) =
    match
      templateName.Split("/") |> Array.filter(String.IsNullOrWhiteSpace >> not)
    with
    | [| user; template; child |] -> Some user, template, Some child
    | [| template; child |] -> None, template, Some child
    | [| template |] -> None, template, None
    | _ -> None, templateName, None

  let dependencyTable(deps: Dependency seq, title: string) =
    let table = Table().AddColumns([| "Name"; "Version"; "Alias" |])

    table.Title <- TableTitle(title)

    for column in table.Columns do
      column.Alignment <- Justify.Left

    for dependency in deps do
      table.AddRow(
        dependency.name,
        defaultArg dependency.version "",
        defaultArg dependency.alias ""
      )
      |> ignore

    table

  let (|ScopedPackage|Package|)(package: string) =
    if package.StartsWith("@") then
      ScopedPackage(package.Substring(1))
    else
      Package package

  let parsePackageName(name: string) =

    let getVersion parts =

      let version =
        let version = parts |> Seq.tryLast |> Option.defaultValue ""

        if String.IsNullOrWhiteSpace version then
          None
        else
          Some version

      version

    match name with
    | ScopedPackage name ->
      // check if the user is looking to install a particular version
      // i.e. package@5.0.0
      if name.Contains("@") then
        let parts = name.Split("@")
        let version = getVersion parts

        $"@{parts[0]}", version
      else
        $"@{name}", None
    | Package name ->
      if name.Contains("@") then
        let parts = name.Split("@")

        let version = getVersion parts
        parts[0], version
      else
        name, None

  let (|Log|Debug|Info|Err|Warning|Clear|) level =
    match level with
    | "assert"
    | "debug" -> Debug
    | "info" -> Info
    | "error" -> Err
    | "warning" -> Warning
    | "clear" -> Clear
    | "log"
    | "dir"
    | "dirxml"
    | "table"
    | "trace"
    | "startGroup"
    | "startGroupCollapsed"
    | "endGroup"
    | "profile"
    | "profileEnd"
    | "count"
    | "timeEnd"
    | _ -> Log

  let (|TopLevelProp|NestedProp|TripleNestedProp|InvalidPropPath|)
    (propPath: string)
    =
    match propPath.Split('.') with
    | [| prop |] -> TopLevelProp prop
    | [| prop; child |] -> NestedProp(prop, child)
    | [| prop; node; innerNode |] -> TripleNestedProp(prop, node, innerNode)
    | _ -> InvalidPropPath

  type FableConfig with

    member this.Item
      with get (value: string) =
        match value.ToLowerInvariant() with
        | "project" -> UMX.untag this.project |> Text :> IRenderable |> Some
        | "extension" -> UMX.untag this.extension |> Text :> IRenderable |> Some
        | "sourcemaps" -> $"{this.sourceMaps}" |> Text :> IRenderable |> Some
        | "outdir" ->
          this.outDir |> Option.map(fun v -> Text($"{v}") :> IRenderable)
        | _ -> None

    member this.ToTree() =
      let outDir =
        this.outDir |> Option.map UMX.untag |> Option.defaultValue String.Empty

      let tree = Tree("fable")

      tree.AddNodes(
        $"project -> {this.project}",
        $"extension -> {this.extension}",
        $"sourcemaps -> {this.sourceMaps}",
        $"outDir -> {outDir}"
      )

      tree

  type DevServerConfig with

    member this.Item
      with get (value: string) =
        match value.ToLowerInvariant() with
        | "port" -> $"{this.port}" |> Text :> IRenderable |> Some
        | "host" -> $"{this.host}" |> Text :> IRenderable |> Some
        | "livereload" -> $"{this.liveReload}" |> Text :> IRenderable |> Some
        | "usessl" -> $"{this.useSSL}" |> Text :> IRenderable |> Some
        | "proxy" ->
          this.proxy
          |> Map.fold (fun current key value -> $"{key}={value};{current}") ""
          |> Text
          :> IRenderable
          |> Some
        | _ -> None

    member this.ToTree() =
      let proxy = this["proxy"] |> Option.defaultValue(Text "")
      let tree = Tree("devServer")

      tree.AddNodes(
        $"port -> {this.port}",
        $"host -> {this.host}",
        $"liveReload -> {this.liveReload}",
        $"useSSL -> {this.useSSL}"
      )

      tree.AddNode(proxy) |> ignore

      tree

  type EsbuildConfig with

    member this.Item
      with get (value: string) =
        match value.ToLowerInvariant() with
        | "esbuildpath" ->
          UMX.untag this.esBuildPath |> Text :> IRenderable |> Some
        | "version" -> UMX.untag this.version |> Text :> IRenderable |> Some
        | "ecmaversion" ->
          UMX.untag this.ecmaVersion |> Text :> IRenderable |> Some
        | "minify" -> $"{this.minify}" |> Text :> IRenderable |> Some
        | "injects" ->
          this.injects
          |> Seq.fold (fun current next -> $"{next};{current}") ""
          |> Text
          :> IRenderable
          |> Some
        | "externals" ->
          this.externals
          |> Seq.fold (fun current next -> $"{next};{current}") ""
          |> Text
          :> IRenderable
          |> Some
        | "fileloaders" ->
          this.fileLoaders
          |> Map.fold (fun current key value -> $"{key}={value};{current}") ""
          |> Text
          :> IRenderable
          |> Some
        | "jsxautomatic" ->
          $"{this.jsxAutomatic}" |> Text :> IRenderable |> Some
        | "jsximportsource" ->
          $"{this.jsxImportSource}" |> Text :> IRenderable |> Some
        | _ -> None

    member this.ToTree() =
      let injects = this["injects"] |> Option.defaultValue(Text "")
      let externals = this["externals"] |> Option.defaultValue(Text "")
      let fileLoaders = this["fileloaders"] |> Option.defaultValue(Text "")
      let tree = Tree("esbuild")

      tree.AddNodes(
        $"esbuildPath -> {this.esBuildPath}",
        $"version -> {this.version}",
        $"ecmaVersion -> {this.ecmaVersion}",
        $"minify -> {this.minify}",
        $"jsxAutomatic -> {this.jsxAutomatic}",
        $"jsxImportSource -> {this.jsxImportSource}"
      )

      tree.AddNode(injects).AddNode(externals).AddNode(fileLoaders) |> ignore

      tree

  type BuildConfig with

    member this.Item
      with get (value: string) =
        match value.ToLowerInvariant() with
        | "includes" ->
          this.includes
          |> Seq.fold (fun current next -> $"{next};{current}") ""
          |> Text
          :> IRenderable
          |> Some
        | "excludes" ->
          this.excludes
          |> Seq.fold (fun current next -> $"{next};{current}") ""
          |> Text
          :> IRenderable
          |> Some
        | "outdir" -> UMX.untag this.outDir |> Text :> IRenderable |> Some
        | "emitenvfile" -> $"{this.emitEnvFile}" |> Text :> IRenderable |> Some
        | _ -> None

    member this.ToTree() =
      let includes = this["includes"] |> Option.defaultValue(Text "")
      let excludes = this["excludes"] |> Option.defaultValue(Text "")
      let tree = Tree("build")

      tree.AddNode(includes).AddNode(excludes) |> ignore

      tree.AddNodes(
        $"outDir -> {this.outDir}",
        $"emitEnvFile -> {this.emitEnvFile}"
      )

      tree

  type TestConfig with

    member this.Item
      with get (value: string) =
        match value.ToLowerInvariant() with
        | "browsers" ->
          this.browsers
          |> Seq.fold (fun current next -> $"{next};{current}") ""
          |> Text
          :> IRenderable
          |> Some
        | "includes" ->
          this.includes
          |> Seq.fold (fun current next -> $"{next};{current}") ""
          |> Text
          :> IRenderable
          |> Some
        | "excludes" ->
          this.excludes
          |> Seq.fold (fun current next -> $"{next};{current}") ""
          |> Text
          :> IRenderable
          |> Some
        | "watch" -> $"{this.watch}" |> Text :> IRenderable |> Some
        | "headless" -> $"{this.headless}" |> Text :> IRenderable |> Some
        | "fable" ->
          this.fable |> Option.map(fun value -> value.ToTree() :> IRenderable)
        | _ -> None

    member this.Item
      with get (value: string * string) =
        let prop, node = value

        match prop.ToLowerInvariant() with
        | "fable" ->
          this.fable |> Option.map(fun fable -> fable[node]) |> Option.flatten
        | _ -> None

    member this.ToTree() =
      let tree = Tree("testing")
      let browsers = this["browsers"] |> Option.defaultValue(Text "")
      let includes = this["includes"] |> Option.defaultValue(Text "")
      let excludes = this["excludes"] |> Option.defaultValue(Text "")

      tree.AddNodes(browsers, includes, excludes)

      tree.AddNodes($"watch -> {this.watch}", $"headless -> {this.headless}")

      match this.fable with
      | Some fable -> tree.AddNode(fable.ToTree() :> IRenderable) |> ignore
      | None -> ()

      tree

  type PerlaConfig with

    member this.Item
      with get (value: string): IRenderable option =
        match value.ToLowerInvariant() with
        | "index" -> Text(UMX.untag this.index) :> IRenderable |> Some
        | "runconfiguration" ->
          Text(this.runConfiguration.AsString) :> IRenderable |> Some
        | "provider" -> Text(this.provider.AsString) :> IRenderable |> Some
        | "plugins" ->
          this.plugins
          |> Seq.fold (fun current next -> $"{next};{current}") ""
          |> Text
          :> IRenderable
          |> Some
        | "build" -> this.build.ToTree() :> IRenderable |> Some
        | "devserver" -> this.devServer.ToTree() :> IRenderable |> Some
        | "fable" ->
          this.fable |> Option.map(fun fable -> fable.ToTree() :> IRenderable)
        | "esbuild" -> this.esbuild.ToTree() :> IRenderable |> Some
        | "testing" -> this.testing.ToTree() :> IRenderable |> Some
        | "mountdirectories" ->
          this.mountDirectories
          |> Map.fold (fun current key value -> $"{key}->{value};{current}") ""
          |> Text
          :> IRenderable
          |> Some
        | "enableenv" -> $"{this.enableEnv}" |> Text :> IRenderable |> Some
        | "envpath" -> $"{this.envPath}" |> Text :> IRenderable |> Some
        | "dependencies" ->
          this.dependencies
          |> Seq.fold
            (fun current next -> $"{next.AsVersionedString};{current}")
            ""
          |> Text
          :> IRenderable
          |> Some
        | "devdependencies" ->
          this.devDependencies
          |> Seq.fold
            (fun current next -> $"{next.AsVersionedString};{current}")
            ""
          |> Text
          :> IRenderable
          |> Some
        | _ -> None

    member this.Item
      with get (value: string * string) =
        let prop, node = value

        match prop.ToLowerInvariant() with
        | "fable" ->
          this.fable |> Option.map(fun fable -> fable[node]) |> Option.flatten
        | "devservr" -> this.devServer[node]
        | "build" -> this.build[node]
        | "esbuild" -> this.esbuild[node]
        | "testing" -> this.testing[node]
        | _ -> None

    member this.Item
      with get (value: string * string * string) =
        let testing, fable, node = value

        match testing.ToLowerInvariant() with
        | "testing" -> this.testing[(fable, node)]
        | _ -> None

  type ImportMap with

    member this.RemoveResolutions
      (resolutions: Map<string<BareImport>, string<ResolutionUrl>>)
      =
      ModifyMap(this, MapUpdate.Remove resolutions)

    member this.AddResolutions
      (resolutions: Map<string<BareImport>, string<ResolutionUrl>>)
      =
      ModifyMap(this, MapUpdate.Add resolutions)

    member this.AddEnvResolution(config: PerlaConfig) =
      if config.enableEnv then
        this.AddResolutions(
          [ Constants.EnvBareImport |> UMX.tag, config.envPath |> UMX.cast ]
          |> Map.ofList
        )
      else
        this
