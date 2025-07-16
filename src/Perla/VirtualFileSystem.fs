namespace Perla.VirtualFs

open System
open System.Collections.Concurrent
open System.IO

open Perla
open Perla.Units
open Perla.Plugins
open Perla.Extensibility

open FSharp.UMX
open FSharp.Control
open FSharp.Control.Reactive

open IcedTasks

open Fake.IO.Globbing.Operators
open Microsoft.Extensions.Logging
open AngleSharp.Io


// New types for refactored VFS
type MountedDirectories = Map<string<ServerUrl>, string<UserPath>>
type ApplyPluginsFn = FileTransform -> Async<FileTransform>

// Existing types
[<Struct>]
type ChangeKind =
  | Created
  | Deleted
  | Renamed
  | Changed

type FileChangedEvent = {
  serverPath: string<ServerUrl>
  userPath: string<UserPath>
  oldPath: string<SystemPath> option
  oldName: string<SystemPath> option
  changeType: ChangeKind
  path: string<SystemPath>
  name: string<SystemPath>
}

type FileContent = {
  filename: string
  mimetype: string
  content: string
  source: string<SystemPath>
}

type BinaryFileInfo = {
  filename: string
  mimetype: string
  source: string<SystemPath>
}

type FileKind =
  | TextFile of FileContent
  | BinaryFile of BinaryFileInfo


module FileKind =
  let source(file: FileKind) =
    match file with
    | TextFile content -> content.source
    | BinaryFile info -> info.source

  let mimetype(file: FileKind) =
    match file with
    | TextFile content -> content.mimetype
    | BinaryFile info -> info.mimetype

  let filename(file: FileKind) =
    match file with
    | TextFile content -> content.filename
    | BinaryFile info -> info.filename

type VirtualFileEntry = {
  kind: FileKind
  lastModified: DateTime
}

type VirtualFileSystem =
  inherit IDisposable
  abstract member Resolve: string<ServerUrl> -> FileKind option
  abstract member Load: MountedDirectories -> Async<unit>

  abstract member ToDisk:
    ?location: string<SystemPath> -> Async<string<SystemPath>>

  abstract member FileChanges: IObservable<FileChangedEvent>

type VirtualFileSystemArgs = {
  Extensibility: ExtensibilityService
  Logger: ILogger
}

module VirtualFs =

  // Move these type definitions to the top of the module
  type VfsServiceDeps = {
    logger: ILogger
    extensibility: ExtensibilityService
    files: ConcurrentDictionary<string<ServerUrl>, VirtualFileEntry>
    nodeModulesFiles: ConcurrentDictionary<string<ServerUrl>, VirtualFileEntry>
  }

  type ProcessFileArgs = {
    systemPath: string<SystemPath>
    userPath: string<UserPath>
    serverPath: string<ServerUrl>
  }

  type ProcessFileChangeEventArgs = { event: FileChangedEvent }

  let getMimeType(filename: string) =
    filename
    |> Path.GetExtension
    |> defaultIfNull ""
    |> function
      | ".json" -> MimeTypeNames.ApplicationJson
      | others -> MimeTypeNames.FromExtension others

  let shouldIgnoreFile(path: string) =
    let normalized = path.Replace("\\", "/")

    normalized.Contains("/bin/")
    || normalized.Contains("/obj/")
    || normalized.EndsWith(".fsproj")
    || normalized.EndsWith(".fs")
    || normalized.EndsWith(".fsx")

  let collectSourceFiles (logger: ILogger) (mountedDirs: MountedDirectories) = [
    for KeyValue(serverPath, userPath) in mountedDirs do
      let fullPath = Path.GetFullPath(UMX.untag userPath)

      logger.LogDebug(
        "Collecting source files from {Directory} -> {ServerPath}",
        fullPath,
        UMX.untag serverPath
      )

      let files =
        !! $"{fullPath}/**/*"
        -- $"{fullPath}/**/bin/**"
        -- $"{fullPath}/**/obj/**"
        -- $"{fullPath}/**/*.fs"
        -- $"{fullPath}/**/*.fsproj"
        -- $"{fullPath}/**/*.fsx"

      let fileList = files |> Seq.toList

      logger.LogDebug(
        "Found {FileCount} files in {Directory}",
        fileList.Length,
        fullPath
      )

      for file in fileList do
        UMX.tag<SystemPath> file, serverPath, UMX.tag<UserPath> fullPath
  ]

  let transformSourcePath
    (systemPath: string<SystemPath>)
    (userPath: string<UserPath>)
    (serverPath: string<ServerUrl>)
    =
    let sourcePath = UMX.untag systemPath
    let basePath = UMX.untag userPath
    let targetBase = UMX.untag serverPath

    let relativePath = Path.GetRelativePath(basePath, sourcePath)
    // If relativePath is '.', don't append anything
    let cleanRelativePath =
      if relativePath = "." then
        ""
      else
        relativePath.Replace("\\", "/")

    let serverFilePath =
      if cleanRelativePath = "" then
        // Just the mount point
        targetBase
      elif targetBase = "/" then
        "/" + cleanRelativePath
      else
        targetBase.TrimEnd('/') + "/" + cleanRelativePath

    UMX.tag<ServerUrl> serverFilePath

  let applyPlugins
    (logger: ILogger)
    (extensibility: ExtensibilityService)
    (content: string)
    (fileLocation: string)
    (extension: string)
    =
    async {
      // Create FileTransform for plugin processing
      let fileTransform = {
        content = content
        extension = extension
        fileLocation = fileLocation
      }

      // Check if there are plugins available for this extension
      if extensibility.HasPluginsForExtension(extension) then
        logger.LogDebug("Applying plugins for extension {Extension}", extension)

        // Get all available plugins and run them
        let allPlugins = extensibility.GetAllPlugins()
        let pluginOrder = allPlugins |> List.map(_.name)

        logger.LogTrace(
          "Running {PluginCount} plugins for {Extension}: {PluginNames}",
          pluginOrder.Length,
          extension,
          pluginOrder
        )

        let! result = extensibility.RunPlugins pluginOrder fileTransform

        if result.extension <> extension then
          logger.LogDebug(
            "Plugin transformed file extension from {OldExt} to {NewExt}",
            extension,
            result.extension
          )

        return result
      else
        // No plugins available for this extension, return unchanged
        logger.LogTrace(
          "No plugins available for extension {Extension}",
          extension
        )

        return fileTransform
    }

  let readFileContent
    (sourcePath: string)
    (targetPath: string<ServerUrl>)
    (files: ConcurrentDictionary<string<ServerUrl>, VirtualFileEntry>)
    =
    asyncEx {
      let! token = Async.CancellationToken

      try
        use fs =
          new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite
          )

        use sr = new StreamReader(fs)
        return! sr.ReadToEndAsync token
      with ex ->
        // Try to return the content from the existing entry if available
        match targetPath with
        | Found files entry ->
          match entry.kind with
          | TextFile content -> return content.content
          | _ -> return ""
        | _ -> return ""
    }

  // Refactor processNodeModulesFile
  type ProcessNodeModulesFileArgs = {
    systemPath: string<SystemPath>
    targetPath: string<ServerUrl>
    filename: string
    extension: string
  }

  let processNodeModulesFile
    (deps: VfsServiceDeps)
    (args: ProcessNodeModulesFileArgs)
    =
    let {
          systemPath = systemPath
          targetPath = targetPath
          filename = filename
          extension = extension
        } =
      args

    async {
      let sourcePath = UMX.untag systemPath

      deps.logger.LogDebug(
        "Registering file in node_modules: {FilePath}",
        sourcePath
      )

      let entry = {
        kind =
          TextFile {
            filename = Path.GetFileName sourcePath |> nonNull
            mimetype = getMimeType(filename + extension)
            content = "" // Content is empty for node_modules
            source = systemPath
          }
        lastModified = File.GetLastWriteTime(sourcePath)
      }

      deps.nodeModulesFiles[targetPath] <- entry

      deps.logger.LogTrace(
        "Registered node_modules file {FilePath}",
        sourcePath
      )
    }

  // Refactor processBinaryFile
  type ProcessBinaryFileArgs = {
    systemPath: string<SystemPath>
    targetPath: string<ServerUrl>
    filename: string
    mimeType: string
  }

  let processBinaryFile (deps: VfsServiceDeps) (args: ProcessBinaryFileArgs) =
    let {
          systemPath = systemPath
          targetPath = targetPath
          filename = filename
          mimeType = mimeType
        } =
      args

    async {
      let sourcePath = UMX.untag systemPath

      try
        let binaryInfo = {
          filename = filename
          mimetype = mimeType
          source = systemPath
        }

        let entry = {
          kind = BinaryFile binaryInfo
          lastModified = File.GetLastWriteTime sourcePath
        }

        deps.files[targetPath] <- entry
        deps.logger.LogTrace("Processed binary file {FilePath}", sourcePath)
      with ex ->
        deps.logger.LogError(
          "Error processing binary file {FilePath}, {error}",
          sourcePath
        )

        deps.logger.LogTrace("Exception: {Error}", ex)
    }

  // Refactor processTextFileWithPlugins
  type ProcessTextFileWithPluginsArgs = {
    systemPath: string<SystemPath>
    targetPath: string<ServerUrl>
    filename: string
    extension: string
    content: string
  }

  let processTextFileWithPlugins
    (deps: VfsServiceDeps)
    (args: ProcessTextFileWithPluginsArgs)
    =
    let {
          systemPath = systemPath
          targetPath = targetPath
          filename = filename
          extension = extension
          content = content
        } =
      args

    async {
      let sourcePath = UMX.untag systemPath

      let! transform =
        applyPlugins deps.logger deps.extensibility content sourcePath extension
        |> Async.Catch

      match transform with
      | Choice2Of2 ex ->
        deps.logger.LogError(
          "Error applying plugins to file {FilePath}",
          sourcePath
        )

        deps.logger.LogTrace("Exception: {Error}", ex)
        return ()
      | Choice1Of2 transform ->
        let fileContent = {
          filename = Path.GetFileName sourcePath |> nonNull
          mimetype = getMimeType(filename + transform.extension)
          content = transform.content
          source = systemPath
        }

        let finalPath =
          if transform.extension <> extension then
            let newName =
              $"{Path.GetFileNameWithoutExtension(sourcePath)}{transform.extension}"

            let dir = Path.GetDirectoryName(UMX.untag targetPath) |> nonNull

            let newPath =
              UMX.tag<ServerUrl>(Path.Combine(dir, newName).Replace("\\", "/"))

            deps.logger.LogDebug(
              "File extension transformed from {OldExt} to {NewExt}, path changed to {NewPath}",
              extension,
              transform.extension,
              UMX.untag newPath
            )

            newPath
          else
            targetPath

        try
          let entry = {
            kind = TextFile fileContent
            lastModified = File.GetLastWriteTime(sourcePath)
          }

          deps.files[finalPath] <- entry
          deps.logger.LogTrace("Processed text file {FilePath}", sourcePath)
        with ex ->
          deps.logger.LogError(
            "Error storing processed text file {FilePath} - {Error}",
            sourcePath,
            ex
          )
    }

  // Update processFile to use the new helpers
  let processFile (deps: VfsServiceDeps) (args: ProcessFileArgs) =
    let {
          systemPath = systemPath
          userPath = userPath
          serverPath = serverPath
        } =
      args

    async {
      let sourcePath = UMX.untag systemPath
      let extension = Path.GetExtension(sourcePath) |> defaultIfNull ""
      let filename = Path.GetFileName(sourcePath) |> nonNull
      let mimeType = getMimeType filename
      let targetPath = transformSourcePath systemPath userPath serverPath

      if sourcePath.Contains("node_modules") then
        do!
          processNodeModulesFile deps {
            systemPath = systemPath
            targetPath = targetPath
            filename = filename
            extension = extension
          }
      else
        deps.logger.LogDebug(
          "Processing file {FilePath} -> {TargetPath}",
          sourcePath,
          UMX.untag targetPath
        )

        if mimeType = MimeTypeNames.Binary then
          do!
            processBinaryFile deps {
              systemPath = systemPath
              targetPath = targetPath
              filename = filename
              mimeType = mimeType
            }
        else
          let! content = readFileContent sourcePath targetPath deps.files

          if content = "" then
            deps.logger.LogWarning(
              "Could not read file {FilePath} due to IO exception (possibly locked by another process)",
              sourcePath
            )

          do!
            processTextFileWithPlugins deps {
              systemPath = systemPath
              targetPath = targetPath
              filename = filename
              extension = extension
              content = content
            }
    }

  let processFileChangeEvent
    (deps: VfsServiceDeps)
    (args: ProcessFileChangeEventArgs)
    =
    async {
      let event = args.event

      try
        deps.logger.LogInformation(
          "Processing file change event {ChangeType} for {FilePath}",
          event.changeType,
          UMX.untag event.path
        )

        let isNodeModules = (UMX.untag event.path).Contains("node_modules")

        if isNodeModules then
          // We do not watch or process node_modules file changes
          ()
        else
          match event.changeType with
          | Deleted ->
            let targetPath =
              transformSourcePath event.path event.userPath event.serverPath

            let removed = deps.files.TryRemove targetPath

            if fst removed then
              deps.logger.LogDebug(
                "Removed file {FilePath} from virtual file system",
                UMX.untag targetPath
              )
            else
              deps.logger.LogWarning(
                "Attempted to remove non-existent file {FilePath}",
                UMX.untag targetPath
              )
          | Created
          | Changed ->
            // Always update the entry for the current file
            do!
              processFile deps {
                systemPath = event.path
                userPath = event.userPath
                serverPath = event.serverPath
              }
          | Renamed ->
            // Remove the old entry if present
            match event.oldPath, event.oldName with
            | Some oldSystemPath, Some _ ->
              let oldTargetPath =
                transformSourcePath
                  oldSystemPath
                  event.userPath
                  event.serverPath

              let removed = deps.files.TryRemove oldTargetPath

              if fst removed then
                deps.logger.LogDebug(
                  "Removed old file {FilePath} due to rename",
                  UMX.untag oldTargetPath
                )
              else
                deps.logger.LogWarning(
                  "Attempted to remove non-existent old file {FilePath} during rename",
                  UMX.untag oldTargetPath
                )
            | _ -> ()
            // Add/update the new file
            do!
              processFile deps {
                systemPath = event.path
                userPath = event.userPath
                serverPath = event.serverPath
              }
      with ex ->
        deps.logger.LogError(
          ex,
          "Error processing file change event for {FilePath}",
          UMX.untag event.path
        )
    }

  let loadAllFiles (deps: VfsServiceDeps) (mountedDirs: MountedDirectories) = async {
    let sourceFiles = collectSourceFiles deps.logger mountedDirs

    deps.logger.LogInformation(
      "Loading {FileCount} files from {DirCount} mounted directories",
      sourceFiles.Length,
      mountedDirs.Count
    )

    do!
      sourceFiles
      |> List.map(fun (systemPath, serverPath, userPath) ->
        processFile deps {
          systemPath = systemPath
          userPath = userPath
          serverPath = serverPath
        })
      |> Async.Parallel
      |> Async.Ignore

    deps.logger.LogInformation(
      "Successfully loaded all files into virtual file system"
    )
  }

  let createFileChangeStream
    (deps: VfsServiceDeps)
    (watchers: ResizeArray<FileSystemWatcher>)
    (mountedDirs: MountedDirectories)
    : IObservable<FileChangedEvent> =
    let fileChangeObservables = ResizeArray<IObservable<FileChangedEvent>>()
    let mountedDirs = mountedDirs |> Map.remove(UMX.tag "/node_modules")

    deps.logger.LogDebug(
      "Creating file change stream for {DirCount} mounted directories",
      mountedDirs.Count
    )

    for KeyValue(serverPath, userPath) in mountedDirs do
      let fullPath = Path.GetFullPath(UMX.untag userPath)

      if Directory.Exists fullPath then
        deps.logger.LogDebug(
          "Setting up file watcher for {Directory} -> {ServerPath}",
          fullPath,
          UMX.untag serverPath
        )

        let watcher =
          new FileSystemWatcher(
            fullPath,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
          )

        watchers.Add watcher

        let fileEvents =
          Observable.merge
            (Observable.merge watcher.Changed watcher.Created)
            watcher.Deleted
          |> Observable.filter(fun e ->
            not(shouldIgnoreFile e.FullPath)
            && not(e.FullPath.Contains("node_modules")))
          |> Observable.map(fun (e: FileSystemEventArgs) ->
            let systemPath = UMX.tag<SystemPath> e.FullPath

            let changeKind =
              match e.ChangeType with
              | WatcherChangeTypes.Created -> Created
              | WatcherChangeTypes.Deleted -> Deleted
              | WatcherChangeTypes.Changed -> Changed
              | _ -> Changed

            let relativeUserPath = Path.GetRelativePath(fullPath, e.FullPath)

            let serverUrl =
              UMX.tag<ServerUrl>(
                Path
                  .Combine(UMX.untag serverPath, relativeUserPath)
                  .Replace('\\', '/')
              )

            {
              serverPath = serverUrl
              userPath = UMX.tag<UserPath> e.FullPath
              oldPath = None
              oldName = None
              changeType = changeKind
              path = systemPath
              name = UMX.tag<SystemPath>(Path.GetFileName(e.FullPath))
            })

        let renamedEvents =
          watcher.Renamed
          |> Observable.filter(fun e ->
            not(shouldIgnoreFile e.FullPath)
            && not(e.FullPath.Contains("node_modules")))
          |> Observable.map(fun (e: RenamedEventArgs) ->
            let systemPath = UMX.tag<SystemPath> e.FullPath
            let relativeUserPath = Path.GetRelativePath(fullPath, e.FullPath)

            let serverUrl =
              UMX.tag<ServerUrl>(
                Path
                  .Combine(UMX.untag serverPath, relativeUserPath)
                  .Replace('\\', '/')
              )

            {
              serverPath = serverUrl
              userPath = UMX.tag<UserPath> e.FullPath
              oldPath = Some(UMX.tag<SystemPath> e.OldFullPath)
              oldName =
                Some(UMX.tag<SystemPath>(Path.GetFileName e.OldFullPath))
              changeType = Renamed
              path = systemPath
              name = UMX.tag<SystemPath>(Path.GetFileName e.FullPath)
            })

        fileChangeObservables.Add(Observable.merge fileEvents renamedEvents)
      else
        deps.logger.LogWarning(
          "Directory {Directory} does not exist, skipping file watcher setup",
          fullPath
        )

    // Set up the processing pipeline
    fileChangeObservables
    |> Observable.mergeSeq
    |> Observable.map(fun event -> async {
      do! processFileChangeEvent deps { event = event }
      return event
    })
    |> Observable.switchAsync

  let stopWatching
    (logger: ILogger)
    (watchers: ResizeArray<FileSystemWatcher>)
    ()
    =
    logger.LogDebug("Stopping {WatcherCount} file watchers", watchers.Count)

    for watcher in watchers do
      watcher.Dispose()

    watchers.Clear()
    logger.LogDebug("All file watchers stopped and disposed")

  /// Create and initialize a new VirtualFileSystem instance
  let Create(args: VirtualFileSystemArgs) =
    let files = ConcurrentDictionary<string<ServerUrl>, VirtualFileEntry>()

    let nodeModulesFiles =
      ConcurrentDictionary<string<ServerUrl>, VirtualFileEntry>()

    let watchers = ResizeArray<FileSystemWatcher>()

    let fileChangedSubject = Subject<FileChangedEvent>.broadcast

    let mutable connection: IDisposable option = None

    args.Logger.LogDebug("Creating new Virtual File System instance")

    { new VirtualFileSystem with
        member _.Resolve(url: string<ServerUrl>) =
          if (UMX.untag url).Contains("node_modules") then
            match url with
            | Found nodeModulesFiles entry ->
              // Read file content on demand
              match entry.kind with
              | TextFile content ->
                let filePath = UMX.untag content.source

                try
                  let fileContent = File.ReadAllText(filePath)
                  let updatedContent = { content with content = fileContent }
                  Some(TextFile updatedContent)
                with _ ->
                  Some(TextFile content)
              | _ -> Some entry.kind
            | _ -> None
          else
            match url with
            | Found files entry ->
              args.Logger.LogTrace("Resolved file {Url}", UMX.untag url)
              Some entry.kind
            | _ ->
              args.Logger.LogTrace("File not found {Url}", UMX.untag url)
              None

        member _.Load(mountedDirs: MountedDirectories) = async {
          args.Logger.LogDebug(
            "Loading virtual file system with {DirCount} mounted directories",
            mountedDirs.Count
          )

          // Load all files initially and create the file change stream with watchers
          do!
            loadAllFiles
              {
                logger = args.Logger
                extensibility = args.Extensibility
                files = files
                nodeModulesFiles = nodeModulesFiles
              }
              mountedDirs

          let connectable =
            createFileChangeStream
              {
                logger = args.Logger
                extensibility = args.Extensibility
                files = files
                nodeModulesFiles = nodeModulesFiles
              }
              watchers
              mountedDirs
            |> Observable.multicast fileChangedSubject


          // Connect to start the stream
          connection <- Some(connectable.Connect())

          args.Logger.LogDebug(
            "Virtual file system loaded and file watching started"
          )
        }

        member _.ToDisk(?location: string<SystemPath>) = async {
          let outputDir =
            match location with
            | Some path -> UMX.untag path
            | None -> Path.GetTempPath() + Path.GetRandomFileName()

          args.Logger.LogInformation(
            "Exporting virtual file system to disk at {OutputDir}",
            outputDir
          )

          Directory.CreateDirectory outputDir |> ignore

          // Local function to copy a single entry
          let copyEntry(KeyValue(serverUrl: string<ServerUrl>, entry)) =
            let serverUrl = UMX.untag serverUrl
            let relativePath = serverUrl.TrimStart('/')
            let targetPath = Path.Combine(outputDir, relativePath)
            let targetDir = Path.GetDirectoryName targetPath |> nonNull

            Directory.CreateDirectory targetDir |> ignore

            match entry.kind with
            | TextFile content ->
              let fileSource = UMX.untag content.source

              if fileSource.Contains("node_modules") then
                File.Copy(fileSource, targetPath, true)
              else
                File.WriteAllText(targetPath, content.content)
            | BinaryFile info ->
              File.Copy(UMX.untag info.source, targetPath, true)

          // Merge all entries into a single array
          let allEntries = [|
            yield! files |> Seq.toArray
            yield! nodeModulesFiles |> Seq.toArray
          |]

          // Copy all files in parallel
          allEntries |> Array.Parallel.iter copyEntry

          args.Logger.LogInformation(
            "Successfully exported {FileCount} files to {OutputDir}",
            allEntries.Length,
            outputDir
          )

          return UMX.tag<SystemPath> outputDir
        }

        member _.FileChanges = fileChangedSubject

        member _.Dispose() =
          args.Logger.LogDebug("Disposing virtual file system")
          connection |> Option.iter(_.Dispose())
          stopWatching args.Logger watchers ()
          args.Logger.LogDebug("Virtual file system disposed")
    }
