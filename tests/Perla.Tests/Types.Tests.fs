// tests/Perla.Tests/Types.Tests.fs
module Perla.Tests.Types

open Xunit
open Perla.Types

[<Fact>]
let ``Browser.FromString should return correct Browser enum``() =
  Assert.Equal(Browser.Webkit, Browser.FromString "webkit")
  Assert.Equal(Browser.Firefox, Browser.FromString "firefox")
  Assert.Equal(Browser.Chromium, Browser.FromString "chromium")
  Assert.Equal(Browser.Edge, Browser.FromString "edge")
  Assert.Equal(Browser.Chrome, Browser.FromString "chrome")

[<Fact>]
let ``Browser.FromString should be case insensitive``() =
  Assert.Equal(Browser.Webkit, Browser.FromString "WEBKIT")
  Assert.Equal(Browser.Firefox, Browser.FromString "Firefox")
  Assert.Equal(Browser.Chromium, Browser.FromString "ChRoMiUm")

[<Fact>]
let ``Browser.FromString should return Chromium for invalid input``() =
  Assert.Equal(Browser.Chromium, Browser.FromString "invalid")
  Assert.Equal(Browser.Chromium, Browser.FromString "")
  Assert.Equal(Browser.Chromium, Browser.FromString "unknown")

[<Fact>]
let ``Browser.AsString should return correct string``() =
  Assert.Equal("webkit", Browser.Webkit.AsString)
  Assert.Equal("firefox", Browser.Firefox.AsString)
  Assert.Equal("chromium", Browser.Chromium.AsString)
  Assert.Equal("edge", Browser.Edge.AsString)
  Assert.Equal("chrome", Browser.Chrome.AsString)

[<Fact>]
let ``BrowserMode.FromString should return correct BrowserMode enum``() =
  Assert.Equal(BrowserMode.Parallel, BrowserMode.FromString "parallel")
  Assert.Equal(BrowserMode.Sequential, BrowserMode.FromString "sequential")

[<Fact>]
let ``BrowserMode.FromString should be case insensitive``() =
  Assert.Equal(BrowserMode.Parallel, BrowserMode.FromString "PARALLEL")
  Assert.Equal(BrowserMode.Sequential, BrowserMode.FromString "Sequential")

[<Fact>]
let ``BrowserMode.FromString should return Parallel for invalid input``() =
  Assert.Equal(BrowserMode.Parallel, BrowserMode.FromString "invalid")
  Assert.Equal(BrowserMode.Parallel, BrowserMode.FromString "")
  Assert.Equal(BrowserMode.Parallel, BrowserMode.FromString "unknown")

[<Fact>]
let ``BrowserMode.AsString should return correct string``() =
  Assert.Equal("parallel", BrowserMode.Parallel.AsString)
  Assert.Equal("sequential", BrowserMode.Sequential.AsString)
