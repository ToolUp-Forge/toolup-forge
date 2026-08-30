// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Support.FakeBlobStorage

open System
open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── An in-process IBlobStorage for the file-connector arms ───────
//
// The Phase 10d connectors acquire every byte through `IBlobStorage`
// and never touch `System.IO`, which is what makes them testable with
// no filesystem, no temp directory and no cleanup: the fixture files
// are written into this dictionary and the connector reads them back
// through exactly the seam a deployment composes `LocalFileStorage` or
// a cloud companion into.
//
// It is deliberately NOT a general-purpose double. The compose and
// erase members answer honestly and minimally, because no file
// connector calls them — a connector that started to would fail these
// tests loudly rather than silently exercising an untested path.

/// A dictionary-backed blob store. Blob names are `/`-delimited, as
/// `IBlobStorage` requires of every backend.
type InMemoryBlobStorage() =

    let blobs = ConcurrentDictionary<string * string, byte[]>()

    /// Write (or replace) a blob.
    member _.Put(container: string, blobName: string, content: byte[]) = blobs[(container, blobName)] <- content

    /// Every blob currently held, as `container/name`.
    member _.Names =
        blobs.Keys
        |> Seq.map (fun (container, name) -> $"%s{container}/%s{name}")
        |> List.ofSeq

    interface IBlobStorage with
        member _.Upload(container, blobName, content) = async {
            blobs[(container, blobName)] <- content
            return Ok blobName
        }

        member _.Download(container, blobName) = async {
            match blobs.TryGetValue((container, blobName)) with
            | true, content -> return Ok content
            | false, _ -> return Error $"blob '%s{blobName}' not found in container '%s{container}'"
        }

        member _.Delete(container, blobName) = async {
            blobs.TryRemove((container, blobName)) |> ignore
            return Ok()
        }

        member _.List(container, prefix) = async {
            let prefix = if isNull prefix then "" else prefix

            return
                blobs.Keys
                |> Seq.filter (fun (c, name) -> c = container && name.StartsWith(prefix, StringComparison.Ordinal))
                |> Seq.map snd
                |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b))
                |> List.ofSeq
        }

        member _.Exists(container, blobName) = async { return blobs.ContainsKey((container, blobName)) }

        member _.GetMetadata(container, blobName) = async {
            match blobs.TryGetValue((container, blobName)) with
            | true, content ->
                return
                    Ok {
                        Size = int64 content.Length
                        LastModified = DateTime.UtcNow
                        ContentType = None
                    }
            | false, _ -> return Error $"blob '%s{blobName}' not found in container '%s{container}'"
        }

        member this.DownloadRange(container, blobName, offset, length) =
            downloadRangeViaDownload (this :> IBlobStorage) container blobName offset length

        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            composeNotSupported "InMemoryBlobStorage (test double)"

        member this.Erase(container, prefix, policy, dryRun) =
            eraseByPrefix (this :> IBlobStorage) container prefix policy dryRun