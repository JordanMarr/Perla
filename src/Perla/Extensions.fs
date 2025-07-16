[<AutoOpen>]
module Perla.Extensions

open System.Collections.Generic
open System.Text.RegularExpressions

[<return: Struct>]
let (|Found|_|)
  (dictionary: IDictionary<'Key, 'Value>)
  (property: 'Key)
  : 'Value voption =
  match dictionary.TryGetValue(property) with
  | true, value -> ValueSome value
  | _ -> ValueNone

[<return: Struct>]
let (|ParseRegex|_|) (regex: Regex) (str: string) =
  let m = regex.Match(str)

  if m.Success then
    ValueSome(List.tail [ for x in m.Groups -> x.Value ])
  else
    ValueNone

[<return: Struct>]
let (|FullRepositoryName|_|)(value: string option) =
  match value with
  | Some name ->
    let regex = Regex(@"^([-_\w\d]+)\/([-_\w\d]+):?([\w\d-_]+)?$")

    match name with
    | ParseRegex regex [ username; repository; branch ] ->
      ValueSome(username, repository, branch)
    | ParseRegex regex [ username; repository ] ->
      ValueSome(username, repository, "main")
    | _ -> ValueNone
  | None -> ValueNone

[<return: Struct>]
let (|TemplateAndChild|_|)(templateName: string) =
  let parts =
    templateName.Split('/')
    |> Array.filter(System.String.IsNullOrWhiteSpace >> not)

  match parts with
  | [| user; template; child |] -> ValueSome(Some user, template, Some child)
  | [| template; child |] -> ValueSome(None, template, Some child)
  | [| template |] -> ValueSome(None, template, None)
  | _ -> ValueNone

[<return: Struct>]
let (|ParsedPackageName|_|)(name: string) =
  let atIdx = name.LastIndexOf("@")

  let namePart, version =
    if atIdx > 0 && not(name.StartsWith("@")) then
      let before = name.Substring(0, atIdx)
      let after = name.Substring(atIdx + 1)
      before, Some after
    else
      name, None

  let parts = namePart.Split('/')

  let basePkg =
    if parts.Length > 1 then
      if namePart.StartsWith("@") then
        if parts.Length >= 2 then
          $"{parts[0]}/{parts[1]}"
        else
          namePart
      else
        parts[0]
    else
      namePart

  let fullImport = namePart
  ValueSome(basePkg, fullImport, version)

[<return: Struct>]
let (|PkgWithVersion|_|)(name, version) =
  match name with
  | ParsedPackageName(basePkg, full, _) -> ValueSome(basePkg, full, version)
  | _ -> ValueNone

[<return: Struct>]
let (|InstallString|_|)(name, version) =
  match name with
  | ParsedPackageName(basePkg, full, _) ->
    match version with
    | Some v when full <> basePkg ->
      ValueSome($"{basePkg}@{v}/{full.Substring(basePkg.Length + 1)}")
    | Some v -> ValueSome($"{basePkg}@{v}")
    | None -> ValueSome(full)
  | _ -> ValueNone

let (|ScopedPackage|Package|)(package: string) =
  if package.StartsWith("@") then
    ScopedPackage(package.Substring(1))
  else
    Package package

let (|Log|Debug|Info|Err|Warning|Clear|)(level: string) =
  match level with
  | "assert"
  | "debug" -> Debug
  | "info" -> Info
  | "error" -> Err
  | "warning" -> Warning
  | "clear" -> Clear
  | "log"
  | "dir"
  | "dirxml"
  | "table"
  | "trace"
  | "startGroup"
  | "startGroupCollapsed"
  | "endGroup"
  | "profile"
  | "profileEnd"
  | "count"
  | "timeEnd" -> Log
  | _ -> Log

let (|TopLevelProp|NestedProp|TripleNestedProp|InvalidPropPath|)
  (propPath: string)
  =
  match propPath.Split('.') with
  | [| prop |] -> TopLevelProp prop
  | [| prop; child |] -> NestedProp(prop, child)
  | [| prop; node; innerNode |] -> TripleNestedProp(prop, node, innerNode)
  | _ -> InvalidPropPath
