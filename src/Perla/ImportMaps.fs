namespace Perla

open System.Text.RegularExpressions
open FSharp.UMX
open FSharp.Data.Adaptive
open Perla.Types
open Perla.Units
open Perla.PkgManager
open Perla.Plugins.Plugin

module ImportMaps =
  // Checks if a path is a relative path (starts with ./ or ../ or similar patterns)
  let isRelativePath(path: string) =
    // Normalize separators to forward slashes for consistency
    let path = path.Replace('\\', '/')

    // Check if the path is not rooted (absolute)
    not(System.IO.Path.IsPathRooted(path))
    &&
    // Optionally check that it doesn't start with Windows-style drive letter (C:\ or C:/)
    not(Regex.IsMatch(path, @"^[a-zA-Z]:[/\\]"))
    &&
    // Basic sanity check that it's not empty or whitespace
    not(System.String.IsNullOrWhiteSpace(path))


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

  let cleanupLocalPaths
    (paths: Map<string<BareImport>, string<ResolutionUrl>>)
    (importMap: ImportMap)
    : ImportMap =
    {
      importMap with
          imports =
            paths
            |> Map.fold
              (fun acc k v ->
                if isRelativePath(UMX.untag v) then
                  acc
                else

                Map.add (UMX.untag k) (UMX.untag v) acc)
              importMap.imports
    }

  /// Replaces module names in import statements using the provided paths map.
  /// Returns the modified code string with replacements applied.
  let replaceImports
    (paths: Map<string<BareImport>, string<ResolutionUrl>>)
    (importingFile: string)
    (sourcesRoot: string<SystemPath>)
    (content: string)
    : string =
    let pattern =
      "import\\s+(?:.+?\\s+from\\s+['\"]([^'\"]+)['\"]|['\"]([^'\"]+)['\"])|import\\s*\\(\\s*(['\"])([^'\"]+)\\3\\s*([,)])"

    let extractModuleName(m: System.Text.RegularExpressions.Match) : string =
      if m.Groups[1].Success then m.Groups[1].Value
      elif m.Groups[2].Success then m.Groups[2].Value
      elif m.Groups[4].Success then m.Groups[4].Value
      else ""

    let computeRelativeImport
      (importingDir: string)
      (replacementStr: string)
      (rest: string)
      : string =
      let target =
        if replacementStr.StartsWith("./") then
          replacementStr.Substring(2)
        elif replacementStr.StartsWith("../") then
          replacementStr
        else
          replacementStr
      // Ensure importingDir is absolute, fallback to sourcesRoot if empty
      let importingDirAbs =
        let dir =
          if System.String.IsNullOrWhiteSpace(importingDir) then
            UMX.untag sourcesRoot
          elif System.IO.Path.IsPathRooted(importingDir) then
            importingDir
          else
            System.IO.Path.Combine(UMX.untag sourcesRoot, importingDir)

        System.IO.Path.GetFullPath(dir)

      let targetAbs =
        if System.IO.Path.IsPathRooted(target) then
          System.IO.Path.GetFullPath(target)
        else
          System.IO.Path.GetFullPath(target, UMX.untag sourcesRoot)

      let rel =
        System.IO.Path
          .GetRelativePath(importingDirAbs, targetAbs)
          .Replace('\\', '/')

      let rel =
        if rel.StartsWith(".") || rel.StartsWith("/") then
          rel
        else
          "./" + rel

      let rel = rel.TrimEnd('/')
      let rest = rest.TrimStart('/')
      if rest = "" then rel else rel + "/" + rest

    let computeNewImport
      (importingDir: string)
      (moduleName: string)
      (prefixStr: string)
      (replacementStr: string)
      : string =
      let rest = moduleName.Substring(prefixStr.Length)

      if
        isRelativePath replacementStr
        && not(System.String.IsNullOrWhiteSpace importingDir)
      then
        computeRelativeImport importingDir replacementStr rest
      else
        replacementStr + rest

    let replaceMatch(m: System.Text.RegularExpressions.Match) : string =
      let moduleName = extractModuleName m

      // Ensure importingFile is absolute, fallback to sourcesRoot if not
      let importingFileAbs =
        if System.IO.Path.IsPathRooted(importingFile) then
          importingFile
        else
          System.IO.Path.Combine(UMX.untag sourcesRoot, importingFile)
          |> System.IO.Path.GetFullPath

      let importingDir =
        System.IO.Path.GetDirectoryName(importingFileAbs) |> nonNull

      let tryReplace
        (prefix: string<BareImport>, replacement: string<ResolutionUrl>)
        : string option =
        let prefixStr, replacementStr = UMX.untag prefix, UMX.untag replacement

        if moduleName.StartsWith(prefixStr) then
          let newImport =
            computeNewImport importingDir moduleName prefixStr replacementStr

          Some(m.Value.Replace(moduleName, newImport))
        else
          None

      paths
      |> Map.toSeq
      |> Seq.tryPick tryReplace
      |> Option.defaultValue m.Value

    Regex.Replace(content, pattern, replaceMatch)

  /// Creates a PluginInfo for the perla-paths-replacer-plugin
  let createPathsReplacerPlugin
    (pathsA: Map<string<BareImport>, string<ResolutionUrl>> aval)
    (sourcesRoot: string<SystemPath>)
    : Perla.Plugins.PluginInfo =
    let shouldTransform ext =
      [ ".js"; ".ts"; ".jsx"; ".tsx" ] |> List.contains ext

    let transform: Plugins.Transform =
      fun file ->
        let paths = pathsA |> AVal.force

        let replaced =
          replaceImports paths file.fileLocation sourcesRoot file.content

        { file with content = replaced }

    plugin Constants.PerlaPathsReplacerPluginName {
      should_process_file shouldTransform
      with_transform transform
    }

  let getExternalsFromPaths
    (map: Map<string<BareImport>, string<ResolutionUrl>> aval)
    =


    map
    |> AVal.map(fun map -> [
      for KeyValue(k, v) in map do
        if
          not(isRelativePath(UMX.untag v))
          || System.Uri.IsWellFormedUriString(
            UMX.untag v,
            System.UriKind.Absolute
          )
        then
          k
    ])
    |> AVal.force
