namespace Perla.PkgManager

open System
open System.IO
open System.Threading
open Microsoft.Extensions.Logging
open FsHttp
open IcedTasks
open System.Threading.Tasks

type PkgManagerConfiguration = { GlobalCachePath: string; cwd: string }

type PkgManagerServiceArgs = {
  reqHandler: RequestHandler.JspmService
  logger: ILogger
  config: PkgManagerConfiguration
}

type PkgManager =

  abstract member Install:
    packages: string seq *
    ?options: GeneratorOption seq *
    ?cancellationToken: CancellationToken ->
      Task<GeneratorResponseKind>

  abstract member Update:
    map: ImportMap *
    packages: string seq *
    ?options: GeneratorOption seq *
    ?cancellationToken: CancellationToken ->
      Task<GeneratorResponseKind>

  abstract member Uninstall:
    map: ImportMap *
    packages: string seq *
    ?options: GeneratorOption seq *
    ?cancellationToken: CancellationToken ->
      Task<GeneratorResponseKind>

  abstract member GoOffline:
    map: ImportMap *
    ?options: DownloadOption seq *
    ?cancellationToken: CancellationToken ->
      Task<ImportMap>

module Result =
  let toOption result =
    match result with
    | Ok value -> Some value
    | Error _ -> None

module PkgManager =

  /// strictly speaking, these are not node modules; however, I think
  /// it might help with existing tooling trying to discover the sources
  [<Literal>]
  let LOCAL_CACHE_PREFIX = "/node_modules"

  let extractPackagesWithScopes(map: ImportMap) =
    let imports = map.imports |> Map.values
    let scopeImports = map.scopes |> Map.values |> Seq.collect Map.values

    [
      for value in [| yield! imports; yield! scopeImports |] do
        let uri = Uri(value)

        match ProviderOps.extractFromUri uri with
        | Ok package -> package
        | Error _ -> ()
    ]
    |> Set

  // Download all files for a package to the global cache directory
  let private downloadPackage
    (logger: ILogger)
    (cacheDir: DirectoryInfo)
    (package: string)
    (content: DownloadPackage)
    =
    asyncEx {
      let globalPkgPath = Path.Combine(cacheDir.FullName, package)

      if not(Directory.Exists(globalPkgPath)) then
        logger.LogDebug(
          "[Download] Downloading package '{package}'...",
          package
        )

        logger.LogTrace(
          "[Download] Downloading to: {globalPkgPath}",
          globalPkgPath
        )

        logger.LogDebug(
          "[Download] Working through {fileCount} files...",
          content.files.Length
        )

        for file in content.files do
          let filePath = Path.Combine(cacheDir.FullName, package, file)
          let downloadUri = Uri(content.pkgUrl, file)

          logger.LogTrace(
            "[Download] Downloading file: {file} from {uri}",
            file,
            downloadUri
          )

          let! response =
            get(downloadUri.ToString())
            |> Config.timeoutInSeconds 10
            |> Request.sendAsync

          use! fileContent = response |> Response.toStreamAsync

          Directory.CreateDirectory(
            Path.GetDirectoryName filePath |> nonNull |> Path.GetFullPath
          )
          |> ignore

          use fileStream = File.OpenWrite filePath
          do! fileContent.CopyToAsync(fileStream)
          logger.LogTrace("[Download] Downloaded file: {filePath}", filePath)
      else
        logger.LogDebug(
          "[Download] Package '{package}' already exists, skipping download.",
          package
        )
    }

  // Create the .perla symlink for a package
  let private createPerlaSymlink
    (logger: ILogger)
    (cacheDir: DirectoryInfo)
    (perlaDir: DirectoryInfo)
    (package: string)
    =
    asyncEx {
      let medusaPkgPath = Path.Combine(perlaDir.FullName, package)
      let globalPkgPath = Path.Combine(cacheDir.FullName, package)

      Path.GetDirectoryName medusaPkgPath
      |> nonNull
      |> Directory.CreateDirectory
      |> ignore

      if Directory.Exists(medusaPkgPath) then
        logger.LogDebug(
          "[Symlink] .perla symlink for '{package}' already exists, skipping.",
          package
        )

        logger.LogTrace(
          "[Symlink] Existing .perla symlink: {medusaPkgPath}",
          medusaPkgPath
        )
      else
        logger.LogDebug(
          "[Symlink] Creating .perla symlink: {medusaPkgPath} -> {globalPkgPath}",
          medusaPkgPath,
          globalPkgPath
        )

        Directory.CreateSymbolicLink(medusaPkgPath, globalPkgPath) |> ignore

        logger.LogTrace(
          "[Symlink] Created .perla symlink: {medusaPkgPath} -> {globalPkgPath}",
          medusaPkgPath,
          globalPkgPath
        )
    }

  // Create the flat symlink for a package
  let private createFlatSymlink
    (logger: ILogger)
    (localCacheDir: DirectoryInfo)
    (perlaDir: DirectoryInfo)
    (package: string)
    =
    asyncEx {
      let packageName = ProviderOps.extractPackageNameForFlatStructure package
      let flatPkgPath = Path.Combine(localCacheDir.FullName, packageName)
      let medusaPkgPath = Path.Combine(perlaDir.FullName, package)

      Path.GetDirectoryName flatPkgPath
      |> nonNull
      |> Directory.CreateDirectory
      |> ignore

      if Directory.Exists(flatPkgPath) then
        logger.LogDebug(
          "[Symlink] Flat symlink for '{packageName}' already exists, skipping.",
          packageName
        )

        logger.LogTrace(
          "[Symlink] Existing flat symlink: {flatPkgPath}",
          flatPkgPath
        )
      else
        logger.LogDebug(
          "[Symlink] Creating flat symlink: {flatPkgPath} -> {medusaPkgPath}",
          flatPkgPath,
          medusaPkgPath
        )

        Directory.CreateSymbolicLink(flatPkgPath, medusaPkgPath) |> ignore

        logger.LogTrace(
          "[Symlink] Created flat symlink: {flatPkgPath} -> {medusaPkgPath}",
          flatPkgPath,
          medusaPkgPath
        )
    }

  let cacheResponse
    (args: PkgManagerServiceArgs)
    (response: Map<string, DownloadPackage>)
    =
    cancellableTask {
      let { logger = logger; config = config } = args
      let cacheDir = DirectoryInfo(config.GlobalCachePath)

      let localCacheDir =
        DirectoryInfo(Path.Combine(config.cwd, "node_modules"))

      let perlaDir =
        DirectoryInfo(Path.Combine(localCacheDir.FullName, ".perla"))

      localCacheDir.Create()
      perlaDir.Create()

      logger.LogDebug(
        "Caching downloaded packages to: {cacheDir}",
        cacheDir.FullName
      )

      logger.LogTrace("Working with Download Map: {downloadMap}", response)
      // Per-package pipeline: download, then .perla symlink, then flat symlink
      let perPackageTasks =
        response
        |> Map.toArray
        |> Array.map(fun (package, content) -> asyncEx {
          do! downloadPackage logger cacheDir package content
          do! createPerlaSymlink logger cacheDir perlaDir package
          do! createFlatSymlink logger localCacheDir perlaDir package
        })

      do! Async.Parallel(perPackageTasks, 5) |> Async.Ignore
      return ()
    }

  let download
    (dependencies: PkgManagerServiceArgs)
    (options: DownloadOption seq)
    (map: ImportMap)
    =
    cancellableTask {
      let! token = CancellableTask.getCancellationToken()
      let packages = extractPackagesWithScopes map

      let {
            reqHandler = reqHandler
            logger = logger
          } =
        dependencies

      let options = [
        for option in options do
          match option with
          | Provider provider ->
            "provider",
            match provider with
            | JspmIo -> "jspm.io"
            | JsDelivr -> "jsdelivr"
            | Unpkg -> "unpkg"
          | Exclude excludes ->
            "exclude",
            [|
              for exclude in excludes ->
                match exclude with
                | Unused -> "unused"
                | Types -> "types"
                | SourceMaps -> "sourcemaps"
                | Readme -> "readme"
                | License -> "license"
            |]
            |> String.concat ","
      ]

      let packages = packages |> String.concat ","

      logger.LogTrace("Downloading packages: {packages}", packages)
      logger.LogTrace("Download options: {options}", options)

      let! response =
        reqHandler.Download(packages, options, cancellationToken = token)

      match response with
      | DownloadError err ->
        return raise(Exception $"Download failed: {err.error}")
      | DownloadSuccess response ->
        logger.LogDebug(
          "Download Success: {count} packages downloaded",
          response.Count
        )

        return response
    }

  let install
    (dependencies: PkgManagerServiceArgs)
    (options: GeneratorOption seq)
    (packages: Set<string>)
    =
    cancellableTask {
      let! token = CancellableTask.getCancellationToken()
      let { reqHandler = reqHandler } = dependencies

      let finalOptions = GeneratorOption.toDict options
      finalOptions.Add("install", packages)

      return! reqHandler.Install(finalOptions, cancellationToken = token)
    }

  let update
    (dependencies: PkgManagerServiceArgs)
    (options: GeneratorOption seq)
    (map: ImportMap)
    (packages: Set<string>)
    =
    cancellableTask {
      let! token = CancellableTask.getCancellationToken()
      let { reqHandler = reqHandler } = dependencies

      // Resolve each package name to the actual key in the import map (case-insensitive, exact)
      let resolvedPackages =
        packages
        |> Seq.choose(fun pkg ->
          map.imports
          |> Map.tryFindKey(fun key _ ->
            key.Equals(pkg, StringComparison.InvariantCultureIgnoreCase))
          |> Option.defaultValue pkg // fallback to original if not found
          |> Some)
        |> Set.ofSeq

      let finalOptions = GeneratorOption.toDict options
      finalOptions.Add("update", resolvedPackages)
      finalOptions["inputMap"] <- map

      return! reqHandler.Update(finalOptions, cancellationToken = token)
    }

  let uninstall
    (dependencies: PkgManagerServiceArgs)
    (options: GeneratorOption seq)
    (map: ImportMap)
    (packages: Set<string>)
    =
    cancellableTask {
      let! token = CancellableTask.getCancellationToken()
      let { reqHandler = reqHandler } = dependencies

      // Resolve each package name to the actual key in the import map (case-insensitive, exact)
      let resolvedPackages =
        packages
        |> Seq.choose(fun pkg ->
          map.imports
          |> Map.tryFindKey(fun key _ ->
            key.Equals(pkg, StringComparison.InvariantCultureIgnoreCase))
          |> Option.defaultValue pkg // fallback to original if not found
          |> Some)
        |> Set.ofSeq

      let finalOptions = GeneratorOption.toDict options
      finalOptions.Add("uninstall", resolvedPackages)
      finalOptions["inputMap"] <- map

      return! reqHandler.Uninstall(finalOptions, cancellationToken = token)
    }

  let isValidTopLevelPackageKey(key: string) =
    if key.StartsWith("@") then
      // Scoped: valid if no '/' after '@scope/pkg' or '@scope/pkg@version'
      // e.g. '@babel/core', '@babel/core@1.2.3' are valid
      // '@babel/core/deep', '@babel/core@1.2.3/deep' are not
      let parts = key.Split('/')

      if parts.Length = 2 then
        // '@scope/pkg' or '@scope/pkg@version'
        true
      else
        false
    else
      // Unscoped: valid if no '/' at all
      not(key.Contains "/")

  let goOffline
    (dependencies: PkgManagerServiceArgs)
    (options: DownloadOption seq)
    (map: ImportMap)
    =
    cancellableTask {
      let { logger = logger } = dependencies

      // Filter out deep imports (not valid top-level package keys)
      let filteredImports =
        map.imports |> Map.filter(fun k _ -> isValidTopLevelPackageKey k)

      let filteredScopes =
        map.scopes
        |> Map.map(fun _ scopeMap ->
          scopeMap |> Map.filter(fun k _ -> isValidTopLevelPackageKey k))

      let allScopedImports =
        filteredScopes |> Map.values |> Seq.collect Map.toSeq |> Map.ofSeq

      let combinedImports =
        filteredImports
        |> Map.fold (fun state k v -> state |> Map.add k v) allScopedImports

      let! pkgs =
        download dependencies options {
          map with
              imports = combinedImports
              scopes = filteredScopes
        }

      // Cache the downloaded packages
      do! cacheResponse dependencies pkgs

      let localPrefix = LOCAL_CACHE_PREFIX

      // Helper function to extract the package name from a key
      let extractPackageName(package: string) =
        match ProviderOps.extractPkgAndVersion package with
        | Some(packageName, _) -> packageName
        | None -> package // fallback to original string if parsing fails

      // Helper function to find a matching package key
      let findMatchingKey (pkgName: string) (importUrl: string) =
        pkgs
        |> Map.keys
        |> Seq.tryFind(fun k ->
          let pkgNameFromKey = extractPackageName k
          pkgNameFromKey = pkgName || importUrl.Contains(k))

      // Helper function to convert URL to the local cache path
      let convertToLocalPath importUrl matchingKey isScoped =
        match matchingKey with
        | None -> importUrl
        | Some key ->
          let uri = Uri importUrl
          let filePath = ProviderOps.extractFilePath logger uri
          // If extractFilePath returned the original URL (couldn't extract), keep it as is
          if filePath = importUrl then
            importUrl
          else
            let basePath =
              if isScoped then
                // Scoped packages point to .perla/<package@version>
                Path.Combine(localPrefix, ".perla", key)
              else
                // Non-scoped packages point to a flat structure
                let packageName =
                  ProviderOps.extractPackageNameForFlatStructure key

                Path.Combine(localPrefix, packageName)

            Path.Combine(basePath, filePath).Replace('\\', '/')

      // Helper function to update a scope map
      let updateScopeMap(scopeMap: Map<string, string>) =
        scopeMap
        |> Map.map(fun pkgName importUrl ->
          let matchingKey = findMatchingKey pkgName importUrl

          logger.LogDebug(
            "Updating scope '{pkgName}' with import URL '{importUrl}'",
            pkgName,
            importUrl
          )

          let converted = convertToLocalPath importUrl matchingKey true

          logger.LogDebug(
            "Converted import URL to local path: '{converted}'",
            converted
          )

          converted)

      // Build a new imports map with local paths
      let updatedImports =
        map.imports
        |> Map.map(fun pkgName importUrl ->
          let matchingKey = findMatchingKey pkgName importUrl
          convertToLocalPath importUrl matchingKey false)

      let updatedScopes =
        map.scopes
        |> Seq.map(fun (KeyValue(_, scopeMap)) ->
          localPrefix + "/", updateScopeMap scopeMap)
        |> Map.ofSeq

      let offlineMap = {
        map with
            imports = updatedImports
            scopes = updatedScopes
      }

      logger.LogDebug("Generated offline map {map}", offlineMap)

      return offlineMap
    }


  let create(dependencies: PkgManagerServiceArgs) : PkgManager =
    { new PkgManager with
        member _.Install(packages, options, cancellationToken) =
          install
            dependencies
            (defaultArg options Seq.empty)
            (Set packages)
            (defaultArg cancellationToken CancellationToken.None)

        member _.Update(map, packages, options, cancellationToken) =
          update
            dependencies
            (defaultArg options Seq.empty)
            map
            (Set packages)
            (defaultArg cancellationToken CancellationToken.None)

        member _.Uninstall(map, packages, options, cancellationToken) =
          uninstall
            dependencies
            (defaultArg options Seq.empty)
            map
            (Set packages)
            (defaultArg cancellationToken CancellationToken.None)

        member _.GoOffline(map, options, cancellationToken) =
          goOffline
            dependencies
            (defaultArg options Seq.empty)
            map
            (defaultArg cancellationToken CancellationToken.None)
    }

  type ImportMap with
    member this.ExtractDependencies() =
      // extract the package name and version from the import map, do not transform the key
      this.imports
      |> Map.toSeq
      |> Seq.map(fun (key, value) ->
        let uri = Uri value

        match ProviderOps.extractFromUri uri with
        | Ok packageWithVersion ->
          match ProviderOps.extractPkgAndVersion packageWithVersion with
          | Some(_, Some version) -> key, Some version
          | Some(_, None) -> key, None
          | None -> key, None
        | Error _ -> key, None)
      |> Set

    member this.FindDependency(packageName: string) =
      let imports = this.imports |> Map.toSeq

      imports
      |> Seq.tryPick(fun (key, value) ->
        if
          key.Equals(packageName, StringComparison.InvariantCultureIgnoreCase)
        then
          let uri = Uri value

          match ProviderOps.extractFromUri uri with
          | Ok packageWithVersion ->
            match ProviderOps.extractPkgAndVersion packageWithVersion with
            | Some(_, Some version) -> Some(key, Some version)
            | _ -> None
          | Error _ -> None
        else
          None)

    member this.FindDependencies(packages: string seq) =
      packages |> Seq.choose this.FindDependency |> Set
