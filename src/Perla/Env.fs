[<RequireQualifiedAccess>]
module Perla.Env

open System
open System.Collections
open System.Runtime.InteropServices
open System.Text
open FSharp.UMX
open Perla.Units
open System.IO
open Perla.Logger

open FsToolkit.ErrorHandling
open System.Text.RegularExpressions

let IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)

let PlatformString =
  if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
    "win32"
  else if RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then
    "linux"
  else if RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then
    "darwin"
  else if RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD) then
    "freebsd"
  else
    failwith "Unsupported OS"

let ArchString =
  match RuntimeInformation.OSArchitecture with
  | Architecture.Arm -> "arm"
  | Architecture.Arm64 -> "arm64"
  | Architecture.X64 -> "x64"
  | Architecture.X86 -> "ia32"
  | _ -> failwith "Unsupported Architecture"

[<Literal>]
let PerlaEnvPrefix = "PERLA_"

let internal getPerlaEnvVars() =
  let env = Environment.GetEnvironmentVariables()

  [
    for entry in env do
      let entry = entry :?> DictionaryEntry
      let key = entry.Key :?> string
      let value = entry.Value :?> string

      if key.StartsWith(PerlaEnvPrefix) then
        (key.Replace(PerlaEnvPrefix, String.Empty), value)
  ]

let GetEnvContent() = option {
  let env = getPerlaEnvVars()
  let sb = StringBuilder()

  for key, value in env do
    sb.Append($"""export const {key} = "{value}";""") |> ignore

  let content = sb.ToString()

  if String.IsNullOrWhiteSpace content then
    return! None
  else
    return content
}

let envVarRegex = Regex(@"^([\w\d ]+)=([^\n\r]+)$")

let getGroups (regex: Regex) (input: string) =
  if regex.IsMatch input then
    [
      for group in regex.Match(input).Groups do
        if String.IsNullOrWhiteSpace group.Value then
          ()
        else
          group.Value.Trim()
    ]
  else
    List.empty

[<return: Struct>]
let (|PerlaPrefixed|_|) line =

  match getGroups envVarRegex line with
  | [ _; key; value ] when key.StartsWith(PerlaEnvPrefix) ->
    ValueSome(key, value)
  | _ -> ValueNone

[<return: Struct>]
let (|NotPerlaPrefixed|_|) line =

  match getGroups envVarRegex line with
  | [ _; key; value ] when not(key.StartsWith(PerlaEnvPrefix)) ->
    ValueSome(key, value)
  | _ -> ValueNone

let LoadEnvFiles(files: string<SystemPath> seq) =
  let readLinesFromFile(file: string<SystemPath>) =
    let file = UMX.untag file

    Logger.log $"Loading Environment variables from '{Path.GetFileName file}'"

    try
      File.ReadAllLines(file)
    with ex ->
      Array.empty

  for line in Seq.collect readLinesFromFile files do
    match line with
    | PerlaPrefixed(varName, varContent) ->
      Environment.SetEnvironmentVariable(varName, varContent)
    | NotPerlaPrefixed(varName, varContent) ->
      Environment.SetEnvironmentVariable(
        $"{PerlaEnvPrefix}{varName}",
        varContent
      )
    | _ -> ()
