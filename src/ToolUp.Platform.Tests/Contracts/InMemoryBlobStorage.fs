module ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

open System
open System.Collections.Concurrent
open ToolUp.Platform.BlobStorage

// ─── Shared in-memory IBlobStorage test double ───────────────────────
//
// Hermetic dict-backed IBlobStorage for contract packs that exercise a
// real blob-backed store (DataObjectStore, blob-backed ConfigStore /
// LineageStore, the Phase 9h per-store erasure bindings). Forward-slash
// blob names; `List` is a prefix scan within one container.

type InMemoryBlobStorage() =
    let blobs = ConcurrentDictionary<string * string, byte[]>()

    // Phase 600 — conditional writes: etag = SHA-256 of content; CAS
    // serialised by a single lock (a test double doesn't need striping).
    let casLock = obj ()

    let etagOf (content: byte[]) =
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData content).ToLowerInvariant()

    interface IConditionalBlobStorage with
        member _.DownloadWithETag(container, blobName) = async {
            match blobs.TryGetValue((container, blobName)) with
            | true, bytes -> return Ok(bytes, etagOf bytes)
            | false, _ -> return Error $"not found: {container}/{blobName}"
        }

        member _.UploadWithETag(container, blobName, content, condition) = async {
            return
                lock casLock (fun () ->
                    let current =
                        match blobs.TryGetValue((container, blobName)) with
                        | true, bytes -> Some(etagOf bytes)
                        | false, _ -> None

                    let permitted =
                        match condition, current with
                        | IfAbsent, None -> true
                        | IfMatch expected, Some actual -> expected = actual
                        | IfAbsent, Some _
                        | IfMatch _, None -> false

                    if permitted then
                        blobs[(container, blobName)] <- content
                        Ok(etagOf content)
                    else
                        Error(ETagMismatch current))
        }

    interface IBlobStorage with
        member this.Erase(container, prefix, policy, dryRun) =
            ToolUp.Platform.BlobStorage.eraseByPrefix (this :> IBlobStorage) container prefix policy dryRun

        member _.Upload(container, blobName, content) = async {
            blobs[(container, blobName)] <- content
            return Ok blobName
        }

        member _.Download(container, blobName) = async {
            match blobs.TryGetValue((container, blobName)) with
            | true, bytes -> return Ok bytes
            | false, _ -> return Error $"not found: {container}/{blobName}"
        }

        member this.DownloadRange(container, blobName, offset, length) =
            ToolUp.Platform.BlobStorage.downloadRangeViaDownload (this :> IBlobStorage) container blobName offset length

        member _.Delete(container, blobName) = async {
            blobs.TryRemove((container, blobName)) |> ignore
            return Ok()
        }

        member _.List(container, prefix) = async {
            return
                blobs.Keys
                |> Seq.filter (fun (c, n) -> c = container && n.StartsWith prefix)
                |> Seq.map snd
                |> List.ofSeq
        }

        member _.Exists(container, blobName) = async { return blobs.ContainsKey((container, blobName)) }

        member _.GetMetadata(container, blobName) = async {
            match blobs.TryGetValue((container, blobName)) with
            | true, bytes ->
                return
                    Ok {
                        Size = int64 bytes.Length
                        LastModified = DateTime.UtcNow
                        ContentType = None
                    }
            | false, _ -> return Error $"not found: {container}/{blobName}"
        }