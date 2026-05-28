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