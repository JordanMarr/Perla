namespace Perla

open Spectre.Console.Rendering

open FSharp.UMX
open FsToolkit.ErrorHandling
open Perla // Ensure extensions are available

[<AutoOpen>]
module Lib =
  open System
  open Perla.Types
  open Spectre.Console
  open System.Text.RegularExpressions

  let dependencyTable(deps: PkgDependency Set, title: string) =
    let table = Table().AddColumns([| "Name"; "Version" |])

    table.Title <- TableTitle(title)

    for column in table.Columns do
      column.Alignment <- Justify.Left

    for dependency in deps do
      table.AddRow(dependency.package, UMX.untag dependency.version) |> ignore

    table

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
        | "provider" ->
          Text(this.provider |> PkgManager.DownloadProvider.asString)
          :> IRenderable
          |> Some
        | "uselocalpkgs" ->
          $"{this.useLocalPkgs}" |> Text :> IRenderable |> Some
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
          |> Seq.fold (fun current next -> $"{next.version};{current}") ""
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
