namespace Perla

open FSharp.UMX
open FSharp.Data.Adaptive
open Perla.Types
open Perla.Units
open Perla.PkgManager
open Perla.Plugins.Plugin

module ImportMaps =

  open System.Text.RegularExpressions

  let withPaths
    (paths: Map<string<BareImport>, string<ResolutionUrl>>)
    (importMap: ImportMap)
    : ImportMap =
    {
      importMap with
          imports =
            paths
            |> Map.fold
              (fun acc k v -> Map.add (UMX.untag k) (UMX.untag v) acc)
              importMap.imports
    }

  /// Adaptive function: merges config.paths into importMap, both as avals
  let withPathsA (config: PerlaConfig aval) (map: ImportMap aval) =
    AVal.map2
      (fun config importMap -> withPaths config.paths importMap)
      config
      map

  /// Replaces module names in import statements using the provided paths map.
  /// Returns the modified code string with replacements applied.
  let replaceImports
    (paths: Map<string<BareImport>, string<ResolutionUrl>>)
    (content: string)
    : string =
    let sortedKeys = paths |> Map.toList

    let combinedPattern =
      "import\\s+(?:.+?\\s+from\\s+['\"]([^'\"]+)['\"]|['\"]([^'\"]+)['\"])|import\\s*\\(\\s*(['\"])([^'\"]+)\\3\\s*([,)])"

    Regex.Replace(
      content,
      combinedPattern,
      fun (m: Match) ->
        let moduleName =
          if m.Groups.[1].Success then m.Groups.[1].Value
          elif m.Groups.[2].Success then m.Groups.[2].Value
          elif m.Groups.[4].Success then m.Groups.[4].Value
          else ""

        let prefixOpt =
          sortedKeys
          |> List.tryFind(fun (prefix, _) ->
            moduleName.StartsWith(UMX.untag prefix))

        match prefixOpt with
        | Some(prefix, replacementPrefix) ->
          let replacedModule =
            $"{replacementPrefix}{moduleName.Substring((UMX.untag prefix).Length)}"

          m.Value.Replace(moduleName, replacedModule)
        | None -> m.Value
    )

  /// Creates a PluginInfo for the perla-paths-replacer-plugin
  let createPathsReplacerPlugin
    (pathsA: Map<string<BareImport>, string<ResolutionUrl>> aval)
    : Perla.Plugins.PluginInfo =
    let shouldTransform ext =
      [ ".js"; ".ts"; ".jsx"; ".tsx" ] |> List.contains ext

    let transform: Plugins.Transform =
      fun file ->
        let paths = pathsA |> AVal.force
        let replaced = replaceImports paths file.content
        { file with content = replaced }

    plugin "perla-paths-replacer-plugin" {
      should_process_file shouldTransform
      with_transform transform
    }
