// tests/Perla.Tests/PlatformOps.Tests.fs
module Perla.Tests.PlatformOps

open Xunit
open Microsoft.Extensions.Logging
open Perla
open System.Runtime.InteropServices
open System

[<Fact>]
let ``PlatformOps.Create should create a valid PlatformOps instance``() =
  // Create a logger (we can use a null logger for testing)
  let logger = LoggerFactory.Create(fun _ -> ()).CreateLogger("Test")

  // Create the PlatformOps instance
  let platformOps = PlatformOps.Create(logger)

  // Verify it's not null
  Assert.NotNull(platformOps)

[<Fact>]
let ``IsWindows should return correct platform detection``() =
  let logger = LoggerFactory.Create(fun _ -> ()).CreateLogger("Test")
  let platformOps = PlatformOps.Create(logger)

  let isWindows = platformOps.IsWindows()
  let expectedWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)

  Assert.Equal(expectedWindows, isWindows)

[<Fact>]
let ``PlatformString should return correct platform string``() =
  let logger = LoggerFactory.Create(fun _ -> ()).CreateLogger("Test")
  let platformOps = PlatformOps.Create(logger)

  let platformString = platformOps.PlatformString()

  // Verify it returns one of the expected platform strings
  let validPlatforms = [ "win32"; "linux"; "darwin"; "freebsd" ]
  Assert.Contains(platformString, validPlatforms)

  // Verify it matches the current platform
  if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
    Assert.Equal("win32", platformString)
  elif RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then
    Assert.Equal("linux", platformString)
  elif RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then
    Assert.Equal("darwin", platformString)
  elif RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD) then
    Assert.Equal("freebsd", platformString)

[<Fact>]
let ``ArchString should return correct architecture string``() =
  let logger = LoggerFactory.Create(fun _ -> ()).CreateLogger("Test")
  let platformOps = PlatformOps.Create(logger)

  let archString = platformOps.ArchString()

  // Verify it returns one of the expected architecture strings
  let validArchitectures = [ "arm"; "arm64"; "x64"; "ia32" ]
  Assert.Contains(archString, validArchitectures)

  // Verify it matches the current architecture
  match RuntimeInformation.OSArchitecture with
  | Architecture.Arm -> Assert.Equal("arm", archString)
  | Architecture.Arm64 -> Assert.Equal("arm64", archString)
  | Architecture.X64 -> Assert.Equal("x64", archString)
  | Architecture.X86 -> Assert.Equal("ia32", archString)
  | _ -> Assert.True(false, "Unsupported architecture detected")

[<Fact>]
let ``PlatformString should be consistent with IsWindows``() =
  let logger = LoggerFactory.Create(fun _ -> ()).CreateLogger("Test")
  let platformOps = PlatformOps.Create(logger)

  let isWindows = platformOps.IsWindows()
  let platformString = platformOps.PlatformString()

  if isWindows then
    Assert.Equal("win32", platformString)
  else
    Assert.NotEqual<string>("win32", platformString)

[<Fact>]
let ``Multiple calls to platform methods should return consistent results``() =
  let logger = LoggerFactory.Create(fun _ -> ()).CreateLogger("Test")
  let platformOps = PlatformOps.Create(logger)

  // Call methods multiple times
  let isWindows1 = platformOps.IsWindows()
  let isWindows2 = platformOps.IsWindows()
  let platformString1 = platformOps.PlatformString()
  let platformString2 = platformOps.PlatformString()
  let archString1 = platformOps.ArchString()
  let archString2 = platformOps.ArchString()

  // Verify consistency
  Assert.Equal(isWindows1, isWindows2)
  Assert.Equal(platformString1, platformString2)
  Assert.Equal(archString1, archString2)

[<Fact>]
let ``ProcessEvent should have correct discriminated union cases``() =
  // Test the ProcessEvent discriminated union cases
  let startedEvent = ProcessEvent.Started(1234)
  let stdOutEvent = ProcessEvent.StandardOutput("test output")
  let stdErrEvent = ProcessEvent.StandardError("test error")
  let exitedEvent = ProcessEvent.Exited(0)

  match startedEvent with
  | ProcessEvent.Started(pid) -> Assert.Equal(1234, pid)
  | _ -> Assert.True(false, "ProcessEvent.Started not matched correctly")

  match stdOutEvent with
  | ProcessEvent.StandardOutput(text) -> Assert.Equal("test output", text)
  | _ -> Assert.True(false, "ProcessEvent.StandardOutput not matched correctly")

  match stdErrEvent with
  | ProcessEvent.StandardError(text) -> Assert.Equal("test error", text)
  | _ -> Assert.True(false, "ProcessEvent.StandardError not matched correctly")

  match exitedEvent with
  | ProcessEvent.Exited(code) -> Assert.Equal(0, code)
  | _ -> Assert.True(false, "ProcessEvent.Exited not matched correctly")

[<Fact>]
let ``ProcessResult should have correct structure``() =
  let processResult = {
    ExitCode = 0
    StandardOutput = "output"
    StandardError = "error"
  }

  Assert.Equal(0, processResult.ExitCode)
  Assert.Equal("output", processResult.StandardOutput)
  Assert.Equal("error", processResult.StandardError)
