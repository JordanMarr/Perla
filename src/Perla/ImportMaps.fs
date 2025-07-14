namespace Perla

open FSharp.UMX
open FSharp.Data.Adaptive
open Perla.Types
open Perla.Units
open Perla.PkgManager

module ImportMaps =

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
