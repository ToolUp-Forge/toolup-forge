module LocalFileStorage

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Threading
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

    // ─── Concurrent same-blob access ─────────────────────────────
    //
    // Every cloud backend tolerates a Download racing an Upload of
    // the same blob: the reader observes the previous version or the
    // new one — never an error, never a torn buffer. The naive local
    // implementation (File.WriteAllBytes / File.ReadAllBytes)
    // diverged: each side's handle blocked the other's open, so an
    // overlapping pair surfaced as "the process cannot access the
    // file … because it is being used by another process" — a
    // works-in-production, fails-locally class first hit by the job
    // store's external-compute callback ingress reading a run blob
    // while the reconciliation poll rewrote the same blob (found
    // building Phase 320). Pinned by the "Concurrent Upload and
    // Download" case in the IBlobStorage contract pack. Three
    // mechanisms close it:
    //
    //   • Writers never open the destination. Content lands in a
    //     uniquely-named file under `<baseDir>/.tmp/` and reaches
    //     the blob path via `File.Move(…, overwrite = true)` — an
    //     atomic replace, so a reader observes the old blob or the
    //     new one, never a partial write. (`.tmp` is thereby a
    //     reserved top-level name; identity sanitisation at the auth
    //     boundary never mints a dotted container segment.)
    //   • Same-path operations that hold a file handle (the reads,
    //     the replace, the delete) serialise on a per-path lock
    //     WITHIN this process — the same striping (and the same
    //     DevOnly single-process posture) the Phase 600 CAS already
    //     declared. This is load-bearing, not belt-and-braces:
    //     Windows' MoveFileEx(REPLACE_EXISTING) refuses to replace a
    //     file with ANY open handle — ERROR_ACCESS_DENIED, even when
    //     the handle shares Delete — so lock-free temp+rename alone
    //     merely trades the reader-side sharing violation for a
    //     writer-side one. (POSIX rename has no such refusal; this
    //     is a Windows-specific constraint.)
    //   • Readers open with `FileShare.ReadWrite ||| FileShare.Delete`,
    //     so a CROSS-process writer's replace is at least not blocked
    //     by this process's read handles, and a cross-process
    //     reader's handle never blocks ours.
    //
    // A short bounded retry backstops the cross-process edge the lock
    // cannot see: another process's transient handle on the same
    // destination. Missing-file failures are NOT retried — a blob
    // that isn't there is an answer, not contention.
    let tempDir = Path.Combine(rootedBase, ".tmp")

    let isRetryableIo (attempt: int) (ex: exn) =
        attempt < 5
        && (ex :? UnauthorizedAccessException
            || (ex :? IOException
                && not (ex :? FileNotFoundException)
                && not (ex :? DirectoryNotFoundException)))

    // The lock striping the mechanism-list above describes. Was the
    // Phase 600 CAS lock (`casLocks`); the same stripes now also
    // serialise plain writes, reads, range-reads and deletes of one
    // path, so it lives here where every helper can reach it.
    let pathLocks = ConcurrentDictionary<string, obj>()

    let pathLockFor (path: string) =
        pathLocks.GetOrAdd(path, fun _ -> obj ())

    let writeAtomically (path: string) (content: byte[]) =
        ensureDir path
        Directory.CreateDirectory tempDir |> ignore
        let temp = Path.Combine(tempDir, Guid.NewGuid().ToString "N" + ".tmp")
        File.WriteAllBytes(temp, content)

        let rec move attempt =
            try
                File.Move(temp, path, true)
            with ex when isRetryableIo attempt ex ->
                Thread.Sleep(5 <<< attempt)
                move (attempt + 1)

        try
            lock (pathLockFor path) (fun () -> move 0)
        finally
            // A failed final attempt must not strand the temp file.
            if File.Exists temp then
                try
                    File.Delete temp
                with _ ->
                    ()

    let openSharedRead (path: string) =
        let rec attemptOpen attempt =
            try
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
            with ex when isRetryableIo attempt ex ->
                Thread.Sleep(5 <<< attempt)
                attemptOpen (attempt + 1)

        attemptOpen 0

    /// Read a whole blob under the per-path lock. The handle is a
    /// consistent snapshot: an atomic replace swaps the *name*, not
    /// the file object already open.
    let readAllBytesShared (path: string) =
        lock (pathLockFor path) (fun () ->
            use stream = openSharedRead path
            let buffer = Array.zeroCreate<byte> (int stream.Length)
            stream.ReadExactly(buffer, 0, buffer.Length)
            buffer)

    // ─── Phase 600 — conditional writes ──────────────────────────
    //
    // ETag = SHA-256 of content (hex). Compare-and-swap runs under
    // the per-path lock striping above, which serialises conditional
    // writers WITHIN this process — matching the impl's DevOnly
    // single-process posture (the same boundary the in-process job
    // scheduler declares). Cross-process CAS needs a cloud backend.
    // Monitor is reentrant, so the CAS holding the lock across its
    // read-compare-write while the inner helpers re-take it is fine.
    let etagOf (content: byte[]) =
        Convert.ToHexString(SHA256.HashData content).ToLowerInvariant()

    interface IConditionalBlobStorage with
        member _.DownloadWithETag(container, blobName) = async {
            match resolveBlobPath container blobName with
            | Result.Error reason -> return Error reason
            | Result.Ok path ->
                try
                    if File.Exists path then
                        let bytes = readAllBytesShared path
                        return Ok(bytes, etagOf bytes)
                    else
                        return Error $"Blob not found: {container}/{blobName}"
                with ex ->
                    return Error ex.Message
        }

        member _.UploadWithETag(container, blobName, content, condition) = async {
            match resolveBlobPath container blobName with
            | Result.Error reason -> return Error(ConditionalWriteFailure reason)
            | Result.Ok path ->
                return
                    lock (pathLockFor path) (fun () ->
                        try
                            let current =
                                if File.Exists path then
                                    Some(etagOf (readAllBytesShared path))
                                else
                                    None

                            let permitted =
                                match condition, current with
                                | IfAbsent, None -> true
                                | IfMatch expected, Some actual -> expected = actual
                                | IfAbsent, Some _
                                | IfMatch _, None -> false

                            if permitted then
                                writeAtomically path content
                                Ok(etagOf content)
                            else
                                Error(ETagMismatch current)
                        with ex ->
                            Error(ConditionalWriteFailure ex.Message))
        }

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
                    writeAtomically path content
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
                        return Ok(readAllBytesShared path)
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
                        // Whole range read under the per-path lock —
                        // the handle must not outlive the lock or a
                        // writer's replace could race it.
                        return
                            lock (pathLockFor path) (fun () ->
                                if not (File.Exists path) then
                                    Error $"Blob not found: {container}/{blobName}"
                                else
                                    use stream = openSharedRead path

                                    if offset >= stream.Length then
                                        Ok Array.empty
                                    else
                                        stream.Seek(offset, SeekOrigin.Begin) |> ignore
                                        let count = min (int64 length) (stream.Length - offset) |> int
                                        let buffer = Array.zeroCreate<byte> count
                                        stream.ReadExactly(buffer, 0, count)
                                        Ok buffer)
                    with ex ->
                        return Error ex.Message
        }

        member _.Delete(container, blobName) = async {
            match resolveBlobPath container blobName with
            | Result.Error reason -> return Error reason
            | Result.Ok path ->
                try
                    lock (pathLockFor path) (fun () ->
                        if File.Exists path then
                            File.Delete path)

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
                            // `GetRelativePath` returns the OS separator, so
                            // this yielded `memberships\alice.json` on Windows
                            // and `memberships/alice.json` on Linux. Blob names
                            // on `IBlobStorage` are `/`-delimited — that is how
                            // every caller builds them and what the cloud
                            // backends return — so the local backend must
                            // normalise rather than leak the filesystem's shape.
                            //
                            // The leak was silent, which is why it survived:
                            // `Download` accepts either separator on Windows, so
                            // round-tripping worked. What broke was callers that
                            // strip a known prefix to recover an id
                            // (`name.Replace("memberships/", "")`) — the replace
                            // simply no-opped and handed back a mangled id. That
                            // is how `TeamStore.GetTeamMembers` reported
                            // `UserId = "memberships\alice"`, making
                            // `IsLastOwner` always false and letting the last
                            // Owner of a team be removed on Windows (Phase 617).
                            // Pinned by the `IBlobStorage` contract pack.
                            |> Array.map (fun p -> Path.GetRelativePath(dir, p).Replace('\\', '/'))
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