module LocalFileStorage

open System
open System.IO
open ToolUp.Platform.BlobStorage

/// Local filesystem implementation of IBlobStorage.
/// Stores blobs as files in a base directory.
type LocalFileStorage(baseDir: string) =
    // Phase 6l.H — defence-in-depth path-traversal check. Identity
    // sanitisation at the auth boundary should already catch `..` /
    // `/` / `\` in the container segment, but a misbehaving caller
    // (or a future code path that passes arbitrary container names)
    // shouldn't be able to escape `baseDir`. `rootedBase` is captured
    // once at construction; every read/write resolves the full path
    // and verifies it sits under the root.
    let rootedBase =
        Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

    let ensureDir (path: string) =
        let dir = Path.GetDirectoryName(path)

        if not (Directory.Exists dir) then
            Directory.CreateDirectory dir |> ignore

    /// Verify that `path` resolves under `rootedBase`. Comparison uses
    /// a separator suffix to prevent prefix-match bypass (`/data` vs
    /// `/data-other`).
    let isUnderRoot (path: string) =
        let rootWithSep = rootedBase + string Path.DirectorySeparatorChar
        path.StartsWith(rootWithSep, StringComparison.Ordinal) || path = rootedBase

    /// Resolve a blob's full on-disk path and verify it sits under
    /// `baseDir`. Returns `Result<string, string>` — `Error` carries
    /// a generic "invalid path" reason; the offending container/blob
    /// name MUST NOT be echoed back to the caller (could leak the
    /// attacker-controlled string into logs).
    let resolveBlobPath container blobName : Result<string, string> =
        let combined = Path.Combine(rootedBase, container, blobName)

        try
            let resolved = Path.GetFullPath combined

            if isUnderRoot resolved then
                Result.Ok resolved
            else
                Result.Error "blob path resolves outside base directory"
        with _ ->
            Result.Error "invalid blob path"

    /// Resolve a container's directory path (no blob component) and
    /// verify it sits under `baseDir`. Used by `List` which needs the
    /// directory itself, not a child blob path.
    let resolveContainerDir container : Result<string, string> =
        let combined = Path.Combine(rootedBase, container)

        try
            let resolved = Path.GetFullPath combined

            if isUnderRoot resolved then
                Result.Ok resolved
            else
                Result.Error "container path resolves outside base directory"
        with _ ->
            Result.Error "invalid container path"

    interface IBlobStorage with
        member this.Erase(container, prefix, policy, dryRun) =
            ToolUp.Platform.BlobStorage.eraseByPrefix
                (this :> ToolUp.Platform.BlobStorage.IBlobStorage)
                container
                prefix
                policy
                dryRun

        member _.Upload(container, blobName, content) = async {
            match resolveBlobPath container blobName with
            | Result.Error reason -> return Error reason
            | Result.Ok path ->
                try
                    ensureDir path
                    File.WriteAllBytes(path, content)
                    return Ok path
                with ex ->
                    return Error ex.Message
        }

        member _.Download(container, blobName) = async {
            match resolveBlobPath container blobName with
            | Result.Error reason -> return Error reason
            | Result.Ok path ->
                try
                    if File.Exists path then
                        return Ok(File.ReadAllBytes path)
                    else
                        return Error $"Blob not found: {container}/{blobName}"
                with ex ->
                    return Error ex.Message
        }

        member _.DownloadRange(container, blobName, offset, length) = async {
            if offset < 0L then
                return Error "DownloadRange: offset must be non-negative"
            elif length <= 0 then
                return Error "DownloadRange: length must be positive"
            else
                match resolveBlobPath container blobName with
                | Result.Error reason -> return Error reason
                | Result.Ok path ->
                    try
                        if not (File.Exists path) then
                            return Error $"Blob not found: {container}/{blobName}"
                        else
                            use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)

                            if offset >= stream.Length then
                                return Ok Array.empty
                            else
                                stream.Seek(offset, SeekOrigin.Begin) |> ignore
                                let count = min (int64 length) (stream.Length - offset) |> int
                                let buffer = Array.zeroCreate<byte> count
                                stream.ReadExactly(buffer, 0, count)
                                return Ok buffer
                    with ex ->
                        return Error ex.Message
        }

        member _.Delete(container, blobName) = async {
            match resolveBlobPath container blobName with
            | Result.Error reason -> return Error reason
            | Result.Ok path ->
                try
                    if File.Exists path then
                        File.Delete path

                    return Ok()
                with ex ->
                    return Error ex.Message
        }

        member _.List(container, prefix) = async {
            // Same defence-in-depth path-traversal check as the
            // mutating methods. A container value carrying `..` falls
            // through to `[]` rather than enumerating outside `baseDir`.
            match resolveContainerDir container with
            | Result.Error _ -> return []
            | Result.Ok dir ->
                if Directory.Exists dir then
                    // `GetFiles` treats slashes in the search pattern as
                    // path segments and throws `DirectoryNotFoundException`
                    // if the implied subdirectory doesn't exist. Cloud
                    // stores return `[]` for a missing prefix; match that
                    // behaviour here so callers get consistent semantics
                    // across local and cloud backends.
                    try
                        return
                            Directory.GetFiles(dir, $"{prefix}*", SearchOption.AllDirectories)
                            |> Array.map (fun p -> Path.GetRelativePath(dir, p))
                            |> Array.toList
                    with :? DirectoryNotFoundException ->
                        return []
                else
                    return []
        }

        member _.Exists(container, blobName) = async {
            match resolveBlobPath container blobName with
            | Result.Error _ -> return false
            | Result.Ok path -> return File.Exists path
        }

        member _.GetMetadata(container, blobName) = async {
            match resolveBlobPath container blobName with
            | Result.Error reason -> return Error reason
            | Result.Ok path ->
                try
                    if not (File.Exists path) then
                        return Error $"Blob not found: {container}/{blobName}"
                    else
                        let info = FileInfo path

                        return
                            Ok {
                                Size = info.Length
                                LastModified = info.LastWriteTimeUtc
                                // Local filesystem doesn't track content type —
                                // callers that need it MIME-sniff the bytes.
                                ContentType = None
                            }
                with ex ->
                    return Error ex.Message
        }