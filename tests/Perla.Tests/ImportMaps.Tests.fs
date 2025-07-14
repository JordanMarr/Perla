module Perla.Tests.ImportMaps

open Xunit
open Perla
open Perla.Units
open FSharp.UMX

[<Fact>]
let ``extractImportModuleNames: import defaultExport from "module-name";``() =
  let code = "import defaultExport from \"module-name\";"
  let expected = [| "module-name" |]
  let actual = ImportMaps.extractImportModuleNames code |> List.toArray
  Assert.Equal<string>(expected, actual)

[<Fact>]
let ``extractImportModuleNames: import * as name from "module-name";``() =
  let code = "import * as name from \"module-name\";"
  let expected = [| "module-name" |]
  let actual = ImportMaps.extractImportModuleNames code |> List.toArray
  Assert.Equal<string>(expected, actual)

[<Fact>]
let ``extractImportModuleNames: import { export1 } from "module-name";``() =
  let code = "import { export1 } from \"module-name\";"
  let expected = [| "module-name" |]
  let actual = ImportMaps.extractImportModuleNames code |> List.toArray
  Assert.Equal<string>(expected, actual)

[<Fact>]
let ``extractImportModuleNames: import { export1 as alias1 } from "module-name";``
  ()
  =
  let code = "import { export1 as alias1 } from \"module-name\";"
  let expected = [| "module-name" |]
  let actual = ImportMaps.extractImportModuleNames code |> List.toArray
  Assert.Equal<string>(expected, actual)

[<Fact>]
let ``extractImportModuleNames: import { default as alias } from "module-name";``
  ()
  =
  let code = "import { default as alias } from \"module-name\";"
  let expected = [| "module-name" |]
  let actual = ImportMaps.extractImportModuleNames code |> List.toArray
  Assert.Equal<string>(expected, actual)

[<Fact>]
let ``extractImportModuleNames: import { export1, export2 } from "module-name";``
  ()
  =
  let code = "import { export1, export2 } from \"module-name\";"
  let expected = [| "module-name" |]
  let actual = ImportMaps.extractImportModuleNames code |> List.toArray
  Assert.Equal<string>(expected, actual)

[<Fact>]
let ``extractImportModuleNames: import { export1, export2 as alias2, /* … */ } from "module-name";``
  ()
  =
  let code =
    "import { export1, export2 as alias2, /* … */ } from \"module-name\";"

  let expected = [| "module-name" |]
  let actual = ImportMaps.extractImportModuleNames code |> List.toArray
  Assert.Equal<string>(expected, actual)

[<Fact>]
let ``extractImportModuleNames: import { "string name" as alias } from "module-name";``
  ()
  =
  let code = "import { \"string name\" as alias } from \"module-name\";"
  let expected = [| "module-name" |]
  let actual = ImportMaps.extractImportModuleNames code |> List.toArray
  Assert.Equal<string>(expected, actual)

[<Fact>]
let ``extractImportModuleNames: import defaultExport, { export1, /* … */ } from "module-name";``
  ()
  =
  let code = "import defaultExport, { export1, /* … */ } from \"module-name\";"
  let expected = [| "module-name" |]
  let actual = ImportMaps.extractImportModuleNames code |> List.toArray
  Assert.Equal<string>(expected, actual)

[<Fact>]
let ``extractImportModuleNames: import defaultExport, * as name from "module-name";``
  ()
  =
  let code = "import defaultExport, * as name from \"module-name\";"
  let expected = [| "module-name" |]
  let actual = ImportMaps.extractImportModuleNames code |> List.toArray
  Assert.Equal<string>(expected, actual)

[<Fact>]
let ``extractImportModuleNames: import "module-name";``() =
  let code = "import \"module-name\";"
  let expected = [| "module-name" |]
  let actual = ImportMaps.extractImportModuleNames code |> List.toArray
  Assert.Equal<string>(expected, actual)

[<Fact>]
let ``extractImportModuleNames: import x from 'module-name' with { type: "json" };``
  ()
  =
  let code = "import x from 'module-name' with { type: \"json\" };"
  let expected = [| "module-name" |]
  let actual = ImportMaps.extractImportModuleNames code |> List.toArray
  Assert.Equal<string>(expected, actual)

[<Fact>]
let ``extractImportModuleNames: import("./module-name");``() =
  let code = "import(\"./module-name\");"
  let expected = [| "./module-name" |]
  let actual = ImportMaps.extractImportModuleNames code |> List.toArray
  Assert.Equal<string>(expected, actual)

[<Fact>]
let ``extractImportModuleNames: import("./module-name", { with: { type: "json" } });``
  ()
  =
  let code = "import(\"./module-name\", { with: { type: \"json\" } });"
  let expected = [| "./module-name" |]
  let actual = ImportMaps.extractImportModuleNames code |> List.toArray
  Assert.Equal<string>(expected, actual)

[<Fact>]
let ``extractImportModuleNames: import("./module-name", options);``() =
  let code = "import(\"./module-name\", options);"
  let expected = [| "./module-name" |]
  let actual = ImportMaps.extractImportModuleNames code |> List.toArray
  Assert.Equal<string>(expected, actual)


open System.Collections.Generic


[<Fact>]
let ``replaceFromPaths: import { Button } from '/components/my-button.js'``() =
  let input = "import { Button } from '/components/my-button.js'"
  let prefix = "/components/"
  let replacement = "./components/"
  let expected = "import { Button } from './components/my-button.js'"
  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]
  let actual = ImportMaps.replaceFromPaths paths input
  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceFromPaths: import { Button } from at_src_services_my_service_js``
  ()
  =
  let input = "import { Button } from \"@src/services/my-service.js\""
  let prefix = "@src/"
  let replacement = "./src/"
  let expected = "import { Button } from \"./src/services/my-service.js\""
  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]
  let actual = ImportMaps.replaceFromPaths paths input
  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceFromPaths: import x from '/components/other.js'``() =
  let input = "import x from '/components/other.js'"
  let prefix = "/components/"
  let replacement = "./components/"
  let expected = "import x from './components/other.js'"
  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]
  let actual = ImportMaps.replaceFromPaths paths input
  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceFromPaths: import('/components/dyn.js')``() =
  let input = "import('/components/dyn.js')"
  let prefix = "/components/"
  let replacement = "./components/"
  let expected = "import('./components/dyn.js')"
  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]
  let actual = ImportMaps.replaceFromPaths paths input
  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceFromPaths: import('/components/dyn.js', { with: { type: 'json' } })``
  ()
  =
  let input = "import('/components/dyn.js', { with: { type: 'json' } })"
  let prefix = "/components/"
  let replacement = "./components/"
  let expected = "import('./components/dyn.js', { with: { type: 'json' } })"
  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]
  let actual = ImportMaps.replaceFromPaths paths input
  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceFromPaths: import(`/components/${var}`) (should not replace)``() =
  let input = "import(`/components/${var}`)"
  let prefix = "/components/"
  let replacement = "./components/"
  let expected = "import(`/components/${var}`)"
  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]
  let actual = ImportMaps.replaceFromPaths paths input
  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceFromPaths: import('/not-matching.js') (should not replace)``() =
  let input = "import('/not-matching.js')"
  let prefix = "/components/"
  let replacement = "./components/"
  let expected = "import('/not-matching.js')"
  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]
  let actual = ImportMaps.replaceFromPaths paths input
  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceFromPaths prefers longer prefix match``() =
  let input = "import { X } from '@src/longer/path/file.js'"

  let paths =
    [
      (UMX.tag "@src/", UMX.tag "./src/")
      (UMX.tag "@src/longer/path/", UMX.tag "./src/longer/path/")
    ]
    |> Map.ofList

  let expected = "import { X } from './src/longer/path/file.js'"
  let actual = ImportMaps.replaceFromPaths paths input
  Assert.Equal(expected, actual)

[<Theory>]
[<InlineData("import('/components/' + var)", "/components/", "./components/")>]
[<InlineData("import(`/components/${var}`)", "/components/", "./components/")>]
let ``replaceFromPaths does not replace dynamic imports with expressions``
  (input: string, prefix: string, replacement: string)
  =
  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]
  let actual = ImportMaps.replaceFromPaths paths input
  Assert.Equal(input, actual)

[<Fact>]
let ``replaceFromPaths: replaces multiple different imports in the same file``
  ()
  =
  let input =
    "import { Button } from \"/components/my-button.js\"\nimport { Button } from \"@src/services/my-service.js\""

  let paths =
    [
      (UMX.tag "/components/", UMX.tag "./components/")
      (UMX.tag "@src/", UMX.tag "./src/")
    ]
    |> Map.ofList

  let expected =
    "import { Button } from \"./components/my-button.js\"\nimport { Button } from \"./src/services/my-service.js\""

  let actual = ImportMaps.replaceFromPaths paths input
  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceFromPaths: replaces multiple different imports in a single line``
  ()
  =
  let input =
    "import { Button } from \"/components/my-button.js\";import { Button } from \"@src/services/my-service.js\""

  let paths =
    [
      (UMX.tag "/components/", UMX.tag "./components/")
      (UMX.tag "@src/", UMX.tag "./src/")
    ]
    |> Map.ofList

  let expected =
    "import { Button } from \"./components/my-button.js\";import { Button } from \"./src/services/my-service.js\""

  let actual = ImportMaps.replaceFromPaths paths input
  Assert.Equal(expected, actual)
