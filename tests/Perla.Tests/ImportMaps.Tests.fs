module Perla.Tests.ImportMaps

open Xunit
open Perla
open Perla.Units
open FSharp.UMX

[<Fact>]
let ``replaceImports: import { Button } from '/components/my-button.js'``() =
  let input = "import { Button } from '/components/my-button.js'"
  let prefix = "/components/"
  let replacement = "./components/"

  let expected =
    "import { Button } from './perla-test-root/components/my-button.js'"

  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]

  let actual =
    ImportMaps.replaceImports
      paths
      "C:\\dummy.js"
      (UMX.tag<SystemPath> "C:\\perla-test-root")
      input

  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceImports: import { Button } from at_src_services_my_service_js``() =
  let input = "import { Button } from \"@src/services/my-service.js\""
  let prefix = "@src/"
  let replacement = "./src/"

  let expected =
    "import { Button } from \"./perla-test-root/src/services/my-service.js\""

  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]

  let actual =
    ImportMaps.replaceImports
      paths
      "C:\\dummy.js"
      (UMX.tag<SystemPath> "C:\\perla-test-root")
      input

  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceImports: import x from '/components/other.js'``() =
  let input = "import x from '/components/other.js'"
  let prefix = "/components/"
  let replacement = "./components/"
  let expected = "import x from './perla-test-root/components/other.js'"
  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]

  let actual =
    ImportMaps.replaceImports
      paths
      "C:\\dummy.js"
      (UMX.tag<SystemPath> "C:\\perla-test-root")
      input

  Assert.Equal(expected, actual)

[<Fact>]
let ``getExternalsFromPaths: should extract external imports``() =
  // Setup a map with both local and external paths
  let pathsMap =
    [
      (UMX.tag<Units.BareImport> "react",
       UMX.tag<Units.ResolutionUrl> "https://esm.sh/react")
      (UMX.tag<Units.BareImport> "lodash",
       UMX.tag<Units.ResolutionUrl> "https://cdn.skypack.dev/lodash")
      (UMX.tag<Units.BareImport> "local-lib",
       UMX.tag<Units.ResolutionUrl> "./local/path/lib.js")
      (UMX.tag<Units.BareImport> "local-lib2",
       UMX.tag<Units.ResolutionUrl> "local/path/lib2.js")
      (UMX.tag<Units.BareImport> "rooted-path",
       UMX.tag<Units.ResolutionUrl> "/local/path/lib2.js")
    ]
    |> Map.ofList

  // Create an adaptive value from the map
  let pathsAVal = FSharp.Data.Adaptive.cval pathsMap

  // Call the function under test
  let result = ImportMaps.getExternalsFromPaths pathsAVal

  // Assert that only external paths (not local/relative) are extracted
  Assert.Equal(3, result.Length)
  Assert.Contains(UMX.tag<Units.BareImport> "react", result)
  Assert.Contains(UMX.tag<Units.BareImport> "lodash", result)
  Assert.Contains(UMX.tag<Units.BareImport> "rooted-path", result)
  Assert.DoesNotContain(UMX.tag<Units.BareImport> "local-lib", result)
  Assert.DoesNotContain(UMX.tag<Units.BareImport> "local-lib2", result)

[<Fact>]
let ``getExternalsFromPaths: should handle empty map``() =
  // Setup an empty map
  let emptyMap =
    Map.empty<string<Units.BareImport>, string<Units.ResolutionUrl>>

  // Create an adaptive value from the empty map
  let emptyAVal = FSharp.Data.Adaptive.cval emptyMap

  // Call the function under test
  let result = ImportMaps.getExternalsFromPaths emptyAVal

  // Assert that the result is an empty list
  Assert.Empty(result)

[<Fact>]
let ``getExternalsFromPaths: should handle map with only local imports``() =
  // Setup a map with only local paths
  let localPathsMap =
    [
      (UMX.tag<Units.BareImport> "ui-components",
       UMX.tag<Units.ResolutionUrl> "./components/ui.js")
      (UMX.tag<Units.BareImport> "local-utils",
       UMX.tag<Units.ResolutionUrl> "./utils/index.js")
    ]
    |> Map.ofList

  // Create an adaptive value from the map
  let localPathsAVal = FSharp.Data.Adaptive.cval localPathsMap

  // Call the function under test
  let result = ImportMaps.getExternalsFromPaths localPathsAVal

  // Assert that no paths are extracted since all paths are local/relative
  Assert.Empty(result)

[<Fact>]
let ``replaceImports: import('/components/dyn.js')``() =
  let input = "import('/components/dyn.js')"
  let prefix = "/components/"
  let replacement = "./components/"
  let expected = "import('./perla-test-root/components/dyn.js')"
  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]

  let actual =
    ImportMaps.replaceImports
      paths
      "C:\\dummy.js"
      (UMX.tag<SystemPath> "C:\\perla-test-root")
      input

  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceImports: import('/components/dyn.js', { with: { type: 'json' } })``
  ()
  =
  let input = "import('/components/dyn.js', { with: { type: 'json' } })"
  let prefix = "/components/"
  let replacement = "./components/"

  let expected =
    "import('./perla-test-root/components/dyn.js', { with: { type: 'json' } })"

  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]

  let actual =
    ImportMaps.replaceImports
      paths
      "C:\\dummy.js"
      (UMX.tag<SystemPath> "C:\\perla-test-root")
      input

  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceImports: import(`/components/${var}`) (should not replace)``() =
  let input = "import(`/components/${var}`)"
  let prefix = "/components/"
  let replacement = "./components/"
  let expected = "import(`/components/${var}`)"
  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]

  let actual =
    ImportMaps.replaceImports
      paths
      "C:\\dummy.js"
      (UMX.tag<SystemPath> "C:\\perla-test-root")
      input

  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceImports: import('/not-matching.js') (should not replace)``() =
  let input = "import('/not-matching.js')"
  let prefix = "/components/"
  let replacement = "./components/"
  let expected = "import('/not-matching.js')"
  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]

  let actual =
    ImportMaps.replaceImports
      paths
      "C:\\dummy.js"
      (UMX.tag<SystemPath> "C:\\perla-test-root")
      input

  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceImports prefers longer prefix match``() =
  let input = "import { X } from '@src/longer/path/file.js'"

  let paths =
    [
      (UMX.tag "@src/", UMX.tag "./src/")
      (UMX.tag "@src/longer/path/", UMX.tag "./src/longer/path/")
    ]
    |> Map.ofList

  let expected = "import { X } from './perla-test-root/src/longer/path/file.js'"

  let actual =
    ImportMaps.replaceImports
      paths
      "C:\\dummy.js"
      (UMX.tag<SystemPath> "C:\\perla-test-root")
      input

  Assert.Equal(expected, actual)

[<Theory>]
[<InlineData("import('/components/' + var)", "/components/", "./components/")>]
[<InlineData("import(`/components/${var}`)", "/components/", "./components/")>]
let ``replaceImports does not replace dynamic imports with expressions``
  (input: string, prefix: string, replacement: string)
  =
  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]

  let actual =
    ImportMaps.replaceImports
      paths
      "C:\\dummy.js"
      (UMX.tag<SystemPath> "C:\\perla-test-root")
      input

  Assert.Equal(input, actual)

[<Fact>]
let ``replaceImports: replaces multiple different imports in the same file``() =
  let input =
    "import { Button } from \"/components/my-button.js\"\nimport { Button } from \"@src/services/my-service.js\""

  let paths =
    [
      (UMX.tag "/components/", UMX.tag "./components/")
      (UMX.tag "@src/", UMX.tag "./src/")
    ]
    |> Map.ofList

  let expected =
    "import { Button } from \"./perla-test-root/components/my-button.js\"\nimport { Button } from \"./perla-test-root/src/services/my-service.js\""

  let actual =
    ImportMaps.replaceImports
      paths
      "C:\\dummy.js"
      (UMX.tag<SystemPath> "C:\\perla-test-root")
      input

  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceImports: replaces multiple different imports in a single line``() =
  let input =
    "import { Button } from \"/components/my-button.js\";import { Button } from \"@src/services/my-service.js\""

  let paths =
    [
      (UMX.tag "/components/", UMX.tag "./components/")
      (UMX.tag "@src/", UMX.tag "./src/")
    ]
    |> Map.ofList

  let expected =
    "import { Button } from \"./perla-test-root/components/my-button.js\";import { Button } from \"./perla-test-root/src/services/my-service.js\""

  let actual =
    ImportMaps.replaceImports
      paths
      "C:\\dummy.js"
      (UMX.tag<SystemPath> "C:\\perla-test-root")
      input

  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceImports: deeply nested import path to top-level resolution url``
  ()
  =
  let input =
    "import { PillButton, PrimaryButton } from '/components/buttons.js'"

  let prefix = "/components/"
  let replacement = "./components/"

  let expected =
    "import { PillButton, PrimaryButton } from '../../../../components/buttons.js'"

  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]
  // importing file is deeply nested
  let importingFile = "src/feature1/deep/nested/code.js"
  // sourcesRoot is project root
  let actual =
    ImportMaps.replaceImports
      paths
      importingFile
      (UMX.tag<SystemPath> "C:\\perla-test-root")
      input

  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceImports: deeply nested import path to top-level resolution url (single import)``
  ()
  =
  let input = "import x from '/components/foo.js'"
  let prefix = "/components/"
  let replacement = "./components/"
  let expected = "import x from '../../../../components/foo.js'"
  let paths = Map.ofList [ (UMX.tag prefix, UMX.tag replacement) ]
  let importingFile = "src/feature1/deep/nested/code.js"
  let sourcesRoot = UMX.tag<SystemPath> "C:\\perla-test-root"

  let actual = ImportMaps.replaceImports paths importingFile sourcesRoot input

  Assert.Equal(expected, actual)

[<Fact>]
let ``replaceImports: deeply nested file with multiple import map entries``() =
  let paths =
    [
      (UMX.tag "/components/", UMX.tag "./src/internal/components/")
      (UMX.tag "@services", UMX.tag "./src/services")
    ]
    |> Map.ofList

  // Test importing from /components/ in a deeply nested file
  let input1 = "import Button from '/components/Button.js'"
  let importingFile1 = "src/feature1/views/my-view.js"
  let expected1 = "import Button from '../../internal/components/Button.js'"

  let actual1 =
    ImportMaps.replaceImports
      paths
      importingFile1
      (UMX.tag<SystemPath> "C:\\perla-test-root")
      input1

  Assert.Equal(expected1, actual1)

  // Test importing from @services in a deeply nested file
  let input2 = "import api from '@services/api.js'"
  let importingFile2 = "src/feature2/deep/other.js"
  let expected2 = "import api from '../../services/api.js'"

  let actual2 =
    ImportMaps.replaceImports
      paths
      importingFile2
      (UMX.tag<SystemPath> "C:\\perla-test-root")
      input2

  Assert.Equal(expected2, actual2)

  // Test both imports in the same file
  let input3 =
    "import Btn from '/components/Btn.js'\nimport svc from '@services/util.js'"

  let importingFile3 = "src/feature1/views/my-view.js"

  let expected3 =
    "import Btn from '../../internal/components/Btn.js'\nimport svc from '../../services/util.js'"

  let actual3 =
    ImportMaps.replaceImports
      paths
      importingFile3
      (UMX.tag<SystemPath> "C:\\perla-test-root")
      input3

  Assert.Equal(expected3, actual3)
