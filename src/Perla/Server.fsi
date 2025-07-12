namespace Perla.Server

open System
open System.Runtime.InteropServices
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open System.Collections.Generic
open Yarp.ReverseProxy.Configuration
open Perla.Types
open Perla.Plugins
open Perla.VirtualFs
open System.Reactive.Subjects
open Perla.FileSystem
open FSharp.Data.Adaptive
open IcedTasks

[<AutoOpen>]
module TestableTypes =

  type HttpResponse = {
    Headers: Map<string, string>
    Body: string
    StatusCode: int
    ContentType: string option
  }

  type HttpRequest = {
    Path: string
    Method: string
    Query: Map<string, string>
    Headers: Map<string, string>
  }

  type ResponseWriter = {
    WriteText: string -> Task<unit>
    WriteBytes: byte[] -> Task<unit>
    SetHeader: string -> string -> unit
    SetStatusCode: int -> unit
    SetContentType: string -> unit
    Flush: unit -> Task<unit>
  }

[<AutoOpen>]
module internal Types =

  [<RequireQualifiedAccess; Struct>]
  type ReloadKind =
    | FullReload
    | HMR

  [<RequireQualifiedAccess; Struct>]
  type PerlaScript =
    | LiveReload
    | Worker
    | Env
    | TestingHelpers
    | MochaTestRunner

  [<RequireQualifiedAccess>]
  type ReloadEvents =
    | FullReload of string
    | ReplaceCSS of string
    | CompileError of string

    member AsString: string

module internal LiveReload =

  val processReloadEvent: event: FileChangedEvent -> string * string

  val processHmrEvent:
    event: FileChangedEvent ->
    transform: FileTransform ->
      string * string * string

  val processCompileErrorEvent: error: string option -> string * string

  val WriteReloadChange:
    logger: ILogger ->
    writer: TestableTypes.ResponseWriter ->
    event: FileChangedEvent ->
      Task<unit>

  val WriteHmrChange:
    logger: ILogger ->
    writer: TestableTypes.ResponseWriter ->
    event: FileChangedEvent ->
    transform: FileTransform ->
      Task<unit>

  val WriteCompileError:
    logger: ILogger ->
    writer: TestableTypes.ResponseWriter ->
    error: string option ->
      Task<unit>

module internal Middleware =

  [<Struct>]
  type RequestedAs =
    | JS
    | Normal

  type FileProcessingResult = {
    ContentType: string
    Content: byte[]
    ShouldProcess: bool
  }

  val processCssAsJs: content: string -> url: string -> string

  val processJsonAsJs: content: string -> string

  val determineFileProcessing:
    mimeType: string ->
    requestedAs: RequestedAs ->
    content: byte[] ->
    reqPath: string ->
      FileProcessingResult

  val logUnsupportedJsTransformation:
    logger: ILogger -> mimeType: string -> reqPath: string -> unit

  val processFile:
    logger: ILogger ->
    (string * byte array -> Task<'a>) *
    string *
    string *
    RequestedAs *
    byte array ->
      Task

  val ResolveFile: logger: ILogger -> HttpContext -> RequestDelegate -> Task

  val ProcessTestEvent:
    logger: ILogger ->
    testEvents: ISubject<TestEvent> ->
    ctx: HttpContext ->
      Task

  val SendScript:
    logger: ILogger ->
    script: Types.PerlaScript ->
    ctx: HttpContext ->
      CancellableTask<IResult>

  val SseHandler:
    vfs: VirtualFileSystem ->
    fileChangedEvents: IObservable<FileChangedEvent> ->
    compileErrorEvents: IObservable<string option> ->
    ctx: HttpContext ->
      Task<IResult>

  val IndexHandler: config: PerlaConfig -> ctx: HttpContext -> IResult

  val TestingIndex:
    config: PerlaConfig aval ->
    map: Perla.PkgManager.ImportMap aval ->
    ctx: HttpContext ->
      CancellableTask<IResult>

module internal SpaMiddleware =

  val spaFallback:
    config: PerlaConfig ->
    next: RequestDelegate ->
    context: HttpContext ->
      Task<unit>

module internal ProxyConfiguration =

  val createRoutesAndClusters:
    proxyConfig: Map<string, string> -> List<RouteConfig> * List<ClusterConfig>

module internal Server =

  val isAddressPortOccupied: address: string -> port: int -> bool

  val GetServerURLs:
    host: string -> port: int -> useSSL: bool -> string * string

  val addServices:
    config: PerlaConfig aval ->
    vfs: VirtualFileSystem ->
    fsManager: PerlaFsManager ->
    builder: WebApplicationBuilder ->
      unit

  val addCommonMiddleware:
    host: string -> port: int -> useSSL: bool -> app: WebApplication -> unit

  val addVirtualFileSystemMiddleware:
    logger: ILogger -> app: WebApplication -> WebApplication

  val addLiveReload:
    logger: ILogger ->
    vfs: VirtualFileSystem ->
    fileChangedEvents: IObservable<FileChangedEvent> ->
    compileErrorEvents: IObservable<string option> ->
    app: WebApplication ->
      WebApplication

  val addEnv:
    logger: ILogger ->
    config: PerlaConfig aval ->
    app: WebApplication ->
      WebApplication

  module DevApp =
    val addIndexHandler: app: WebApplication -> WebApplication

  module TestApp =
    val addIndexHandler:
      dependencies: Perla.PkgManager.ImportMap aval ->
      app: WebApplication ->
        WebApplication

    val addTestingHandlers:
      files: string seq option ->
      testConfig: TestConfig ->
      mochaConfig: Map<string, obj> option ->
      testingEvents: ISubject<TestEvent> ->
      app: WebApplication ->
        WebApplication

[<Class>]
type Server =
  static member GetServerApp:
    config: PerlaConfig aval *
    vfs: VirtualFileSystem *
    fileChangedEvents: IObservable<FileChangedEvent> *
    compileErrorEvents: IObservable<string option> *
    fsManager: PerlaFsManager ->
      WebApplication

  static member GetTestingApp:
    config: PerlaConfig aval *
    vfs: VirtualFileSystem *
    dependencies: Perla.PkgManager.ImportMap aval *
    testEvents: ISubject<TestEvent> *
    fileChangedEvents: IObservable<FileChangedEvent> *
    compileErrorEvents: IObservable<string option> *
    fsManager: PerlaFsManager *
    [<Optional>] ?fileGlobs: string seq *
    [<Optional>] ?mochaOptions: Map<string, obj> ->
      WebApplication

  static member GetStaticServer: config: PerlaConfig aval -> WebApplication
