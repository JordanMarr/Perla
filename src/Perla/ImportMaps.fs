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

  /// Extracts all module names from import statements in the given JS/TS code.
  /// Returns a list of the module names as strings.
  let extractImportModuleNames(code: string) : string list =
    let patterns = [
      "import\\s+(.+?)\\s+from\\s+['\"]([^'\"]+)['\"]" // import ... from "module" (more permissive)
      "import\\s+['\"]([^'\"]+)['\"]" // import "module"
      "import\\s*\\(\\s*['\"]([^'\"]+)['\"]" // import("module")
    ]

    patterns
    |> List.collect(fun pattern ->
      Regex.Matches(code, pattern)
      |> Seq.cast<Match>
      |> Seq.choose(fun m ->
        // Try to get the last non-empty group (module name)
        m.Groups
        |> Seq.cast<Group>
        |> Seq.skip 1
        |> Seq.map(fun g -> g.Value)
        |> Seq.filter(fun v -> not(System.String.IsNullOrWhiteSpace v))
        |> Seq.tryLast)
      |> Seq.toList)

  /// Replaces module names in import statements using the provided paths map.
  /// For each module name found in the code, if it exists in the map, replaces it with the mapped value.
  let replaceFromPaths
    (paths: Map<string<BareImport>, string<ResolutionUrl>>)
    (content: string)
    : string =
    let sortedKeys = paths |> Map.toList

    // Patterns: (pattern, groupIdx for module name)
    let patterns = [
      // import ... from 'module' or "module"
      "(import\\s+.+?\\s+from\\s+['\"])([^'\"]+)(['\"])", 2
      // import 'module' or "module"
      "(import\\s+['\"])([^'\"]+)(['\"])", 2
      // import('module') or import('module', ...)
      // This matches import('module') and import('module', ...)
      "(import\\s*\\(\\s*['\"])([^'\"]+)(['\"])(\s*(,|\)))", 2
    ]

    // For each pattern, replace all matches in the content
    let replaced =
      patterns
      |> List.fold
        (fun acc (pattern, groupIdx) ->
          Regex.Replace(
            acc,
            pattern,
            fun (m: Match) ->
              let before = m.Groups.[1].Value
              let moduleName = m.Groups.[groupIdx].Value
              let after = m.Groups.[groupIdx + 1].Value

              let trailing =
                if m.Groups.Count > groupIdx + 2 then
                  m.Groups.[groupIdx + 2].Value
                else
                  ""
              // Find the first prefix that matches
              let prefixOpt =
                sortedKeys
                |> List.tryFind(fun (prefix, _) ->
                  moduleName.StartsWith(UMX.untag prefix))

              match prefixOpt with
              | Some(prefix, replacementPrefix) ->
                let replacedModule =
                  $"{replacementPrefix}{moduleName.Substring((UMX.untag prefix).Length)}"

                before + replacedModule + after + trailing
              | None -> before + moduleName + after + trailing
          ))
        content

    replaced

  /// Creates a PluginInfo for the perla-paths-replacer-plugin
  let createPathsReplacerPlugin
    (pathsA: Map<string<BareImport>, string<ResolutionUrl>> aval)
    : Perla.Plugins.PluginInfo =
    let shouldTransform ext =
      [ ".js"; ".ts"; ".jsx"; ".tsx" ] |> List.contains ext

    let transform: Perla.Plugins.Transform =
      fun file ->
        let paths = pathsA |> AVal.force

        {
          file with
              content = replaceFromPaths paths file.content
        }

    plugin "perla-paths-replacer-plugin" {
      should_process_file shouldTransform
      with_transform transform
    }
