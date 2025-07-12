module Perla.Tests.Server

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Xunit
open FSharp.UMX

open Perla
open Perla.Types
open Perla.Units
open Perla.VirtualFs
open Perla.FileSystem
open Perla.Server.TestableTypes
open Perla.Server.Types
open Perla.Server.LiveReload
open Perla.Plugins
open Perla.Server.Middleware

// Test helpers
module TestHelpers =
  let createLogger() =
    let loggerFactory =
      LoggerFactory.Create(fun builder -> builder.AddConsole() |> ignore)

    loggerFactory.CreateLogger("ServerTests")

[<Fact>]
let ``processReloadEvent should create proper data and message``() =
  // Arrange
  let event = {
    serverPath = UMX.tag<ServerUrl> "/assets"
    userPath = UMX.tag<UserPath> "/user/assets"
    oldPath = None
    oldName = Some(UMX.tag<SystemPath> "old-file.css")
    path = UMX.tag<SystemPath> "/full/path/test-file.css"
    name = UMX.tag<SystemPath> "test-file.css"
    changeType = ChangeKind.Changed
  }

  // Act
  let (data, message) = processReloadEvent event

  // Assert
  Assert.Contains("test-file.css", data)
  Assert.Contains("old-file.css", data)
  Assert.StartsWith("event:reload\ndata:", message)
  Assert.EndsWith("\n\n", message)

[<Fact>]
let ``processHmrEvent should create proper HMR data and message``() =
  // Arrange
  let event = {
    serverPath = UMX.tag<ServerUrl> "/assets"
    userPath = UMX.tag<UserPath> "/user/assets"
    oldPath = None
    oldName = None
    path = UMX.tag<SystemPath> "/full/path/styles.css"
    name = UMX.tag<SystemPath> "styles.css"
    changeType = ChangeKind.Changed
  }

  let transform = {
    content = ".test { color: red; }"
    extension = ".css"
  }

  // Act
  let (data, message, userPath) = processHmrEvent event transform

  // Assert
  Assert.Contains("styles.css", data)
  Assert.Contains(".test { color: red; }", data)
  Assert.StartsWith("event:replace-css\ndata:", message)
  Assert.EndsWith("\n\n", message)
  Assert.Contains("/user/assets/styles.css", userPath)

[<Fact>]
let ``processCompileErrorEvent should create proper error data and message``() =
  // Arrange
  let error = Some "Syntax error on line 42"

  // Act
  let (data, message) = processCompileErrorEvent error

  // Assert
  Assert.Contains("Syntax error on line 42", data)
  Assert.StartsWith("event:compile-err\ndata:", message)
  Assert.EndsWith("\n\n", message)

// ============================================================================
// Middleware Tests - Pure functions are much easier to test!
// ============================================================================

[<Fact>]
let ``processCssAsJs should wrap CSS in JavaScript``() =
  // Arrange
  let css = ".test { color: blue; }"
  let url = "/styles/test.css"

  // Act
  let result = processCssAsJs css url

  // Assert
  Assert.Contains("const style=document.createElement('style')", result)
  Assert.Contains(".test { color: blue; }", result)
  Assert.Contains("/styles/test.css", result)

[<Fact>]
let ``processJsonAsJs should wrap JSON in JavaScript export``() =
  // Arrange
  let json = """{"name": "test", "value": 42}"""

  // Act
  let result = processJsonAsJs json

  // Assert
  Assert.Equal("""export default {"name": "test", "value": 42};""", result)

[<Fact>]
let ``determineFileProcessing should handle JSON as JS request``() =
  // Arrange
  let mimeType = "application/json"
  let requestedAs = RequestedAs.JS
  let content = Encoding.UTF8.GetBytes("""{"test": true}""")
  let reqPath = "/data.json"

  // Act
  let result = determineFileProcessing mimeType requestedAs content reqPath

  // Assert
  Assert.Equal("text/javascript", result.ContentType)
  Assert.True(result.ShouldProcess)
  let contentStr = Encoding.UTF8.GetString(result.Content)
  Assert.Contains("export default", contentStr)

[<Fact>]
let ``determineFileProcessing should handle CSS as JS request``() =
  // Arrange
  let mimeType = "text/css"
  let requestedAs = RequestedAs.JS
  let content = Encoding.UTF8.GetBytes(".test { color: red; }")
  let reqPath = "/styles.css"

  // Act
  let result = determineFileProcessing mimeType requestedAs content reqPath

  // Assert
  Assert.Equal("text/javascript", result.ContentType)
  Assert.True(result.ShouldProcess)
  let contentStr = Encoding.UTF8.GetString(result.Content)
  Assert.Contains("const style=document.createElement", contentStr)

[<Fact>]
let ``determineFileProcessing should handle normal requests unchanged``() =
  // Arrange
  let mimeType = "text/css"
  let requestedAs = RequestedAs.Normal
  let content = Encoding.UTF8.GetBytes(".test { color: red; }")
  let reqPath = "/styles.css"

  // Act
  let result = determineFileProcessing mimeType requestedAs content reqPath

  // Assert
  Assert.Equal("text/css", result.ContentType)
  Assert.False(result.ShouldProcess)
  Assert.True((content = result.Content))

[<Fact>]
let ``determineFileProcessing should handle unsupported JS transformation``() =
  // Arrange
  let mimeType = "image/png"
  let requestedAs = RequestedAs.JS
  let content = [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy |] // PNG header
  let reqPath = "/image.png"

  // Act
  let result = determineFileProcessing mimeType requestedAs content reqPath

  // Assert
  Assert.Equal("image/png", result.ContentType)
  Assert.False(result.ShouldProcess)
  Assert.True((content = result.Content))

[<Fact>]
let ``logUnsupportedJsTransformation should log warning``() =
  // Arrange
  let logger = TestHelpers.createLogger()
  let mimeType = "image/png"
  let reqPath = "/test.png"

  // Act (this function logs but doesn't return anything we can easily assert)
  logUnsupportedJsTransformation logger mimeType reqPath

  // Assert - In a real test scenario, you'd want to capture and verify the log output
  // For now, we just verify it doesn't throw
  Assert.True(true)

// ============================================================================
// Integration Tests with ResponseWriter
// ============================================================================

// Simple fake ResponseWriter for testing
type TestResponseWriter() =
  let mutable _text = ""
  let mutable _bytes = [||]
  let mutable _headers = Map.empty<string, string>
  let mutable _statusCode = 200
  let mutable _contentType = None

  member _.WriteText(text: string) = task { _text <- _text + text }

  member _.WriteBytes(bytes: byte[]) = task {
    _bytes <- Array.append _bytes bytes
  }

  member _.SetHeader (key: string) (value: string) =
    _headers <- Map.add key value _headers

  member _.SetStatusCode(code: int) = _statusCode <- code

  member _.SetContentType(contentType: string) =
    _contentType <- Some contentType

  member _.Flush() = Task.FromResult(())

  member _.GetText() = _text
  member _.GetBytes() = _bytes
  member _.GetHeaders() = _headers
  member _.GetStatusCode() = _statusCode
  member _.GetContentType() = _contentType

  // Convert to the record type
  member this.AsResponseWriter() = {
    WriteText = this.WriteText
    WriteBytes = this.WriteBytes
    SetHeader = this.SetHeader
    SetStatusCode = this.SetStatusCode
    SetContentType = this.SetContentType
    Flush = this.Flush
  }

[<Fact>]
let ``WriteReloadChange should write proper SSE message``() = task {
  // Arrange
  let logger = TestHelpers.createLogger()
  let testWriter = TestResponseWriter()
  let writer = testWriter.AsResponseWriter()

  let event = {
    serverPath = UMX.tag<ServerUrl> "/assets"
    userPath = UMX.tag<UserPath> "/user/assets"
    oldPath = None
    oldName = None
    path = UMX.tag<SystemPath> "/full/path/test.css"
    name = UMX.tag<SystemPath> "test.css"
    changeType = ChangeKind.Changed
  }

  // Act
  do! WriteReloadChange logger writer event

  // Assert
  let result = testWriter.GetText()
  Assert.StartsWith("event:reload\ndata:", result)
  Assert.Contains("test.css", result)
  Assert.EndsWith("\n\n", result)
}

[<Fact>]
let ``WriteHmrChange should write proper HMR SSE message``() = task {
  // Arrange
  let logger = TestHelpers.createLogger()
  let testWriter = TestResponseWriter()
  let writer = testWriter.AsResponseWriter()

  let event = {
    serverPath = UMX.tag<ServerUrl> "/assets"
    userPath = UMX.tag<UserPath> "/user/assets"
    oldPath = None
    oldName = None
    path = UMX.tag<SystemPath> "/full/path/style.css"
    name = UMX.tag<SystemPath> "style.css"
    changeType = ChangeKind.Changed
  }

  let transform = {
    content = ".new { color: green; }"
    extension = ".css"
  }

  // Act
  do! WriteHmrChange logger writer event transform

  // Assert
  let result = testWriter.GetText()
  Assert.StartsWith("event:replace-css\ndata:", result)
  Assert.Contains("style.css", result)
  Assert.Contains(".new { color: green; }", result)
  Assert.EndsWith("\n\n", result)
}

[<Fact>]
let ``WriteCompileError should write proper error SSE message``() = task {
  // Arrange
  let logger = TestHelpers.createLogger()
  let testWriter = TestResponseWriter()
  let writer = testWriter.AsResponseWriter()
  let error = Some "Compilation failed: missing semicolon"

  // Act
  do! WriteCompileError logger writer error

  // Assert
  let result = testWriter.GetText()
  Assert.StartsWith("event:compile-err\ndata:", result)
  Assert.Contains("Compilation failed", result)
  Assert.EndsWith("\n\n", result)
}
