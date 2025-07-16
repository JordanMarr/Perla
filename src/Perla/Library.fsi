namespace Perla

open System.Text.RegularExpressions

open Spectre.Console
open Spectre.Console.Rendering
open Perla.Types

[<AutoOpen>]
module Lib =

  val internal dependencyTable: deps: PkgDependency Set * title: string -> Table

  type FableConfig with

    member Item: string -> IRenderable option with get
    member ToTree: unit -> Tree

  type DevServerConfig with

    member Item: string -> IRenderable option with get
    member ToTree: unit -> Tree

  type EsbuildConfig with

    member Item: string -> IRenderable option with get
    member ToTree: unit -> Tree

  type BuildConfig with

    member Item: string -> IRenderable option with get
    member ToTree: unit -> Tree

  type TestConfig with

    member Item: string -> IRenderable option with get
    member Item: (string * string) -> IRenderable option with get
    member ToTree: unit -> Tree

  type PerlaConfig with

    member Item: string -> IRenderable option with get
    member Item: (string * string) -> IRenderable option with get
    member Item: (string * string * string) -> IRenderable option with get
