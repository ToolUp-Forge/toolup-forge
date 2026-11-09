// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Server

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra

// ─── The distributed idempotency store ──────────────────────────────
//
// `BlobIdempotencyStore` lives beside the document store it reads through,
// not beside the attribute and the in-memory default in
// `Remoting/Idempotency.fs`: the remoting tier compiles before the store
// tier, and a memoised response is a document like any other — absent,
// unreadable and undecodable are the document store's three answers, not
// this file's. The shared layout (`BlobIdempotencyLayout`) stays with the
// remoting tier, where the TTL sweep finds it.

/// Raised by `BlobIdempotencyStore.Store` when the memoised
/// envelope was not written. `IIdempotencyStore.Store` returns
/// `Async<unit>`, so an exception is the one channel a failed write has.
/// Internal: the dispatcher handles any exception from `Store` the same
/// way, and the public surface does not move.
type internal IdempotencyStoreWriteException(container: string, blobName: string, storageError: string) =
    inherit
        Exception(
            $"idempotency store write failed: the memoised response `{container}/{blobName}` was not written: {storageError}"
        )

    member _.StorageError = storageError

/// One memoised response as stored, with its absolute expiry.
type private MemoisedEntry = {
    Response: MemoisedResponse
    ExpiryUtcTicks: int64
}

/// The envelope's JSON, written with `Utf8JsonWriter` and read with
/// `JsonDocument` — no reflection over an F# record type, so the round-trip
/// is robust regardless of STJ's F#-record handling (and the bytes are the
/// ones the store has always written). The body is base64 so the JSON stays
/// text-clean for binary responses.
module private MemoisedEntry =

    let codec: BlobCodec<MemoisedEntry> = {
        Encode =
            fun entry ->
                use ms = new System.IO.MemoryStream()

                (use writer = new System.Text.Json.Utf8JsonWriter(ms)
                 writer.WriteStartObject()
                 writer.WriteNumber("StatusCode", entry.Response.StatusCode)
                 writer.WriteString("ContentType", entry.Response.ContentType)
                 writer.WriteString("RequestBodyHash", entry.Response.RequestBodyHash)
                 writer.WriteNumber("ExpiryUtcTicks", entry.ExpiryUtcTicks)
                 writer.WriteString("BodyBase64", Convert.ToBase64String entry.Response.Body)
                 writer.WriteEndObject()
                 writer.Flush())

                ms.ToArray()
        Decode =
            fun bytes ->
                try
                    use doc = System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
                    let root = doc.RootElement

                    Ok {
                        Response = {
                            Body = Convert.FromBase64String(root.GetProperty("BodyBase64").GetString())
                            StatusCode = root.GetProperty("StatusCode").GetInt32()
                            ContentType = root.GetProperty("ContentType").GetString()
                            RequestBodyHash = root.GetProperty("RequestBodyHash").GetString()
                        }
                        ExpiryUtcTicks = root.GetProperty("ExpiryUtcTicks").GetInt64()
                    }
                with ex ->
                    Error ex.Message
    }

/// Distributed `IIdempotencyStore` over `IBlobStorage`.
/// Memoised entries are JSON documents under the `_platform` container (the
/// reserved SDK-level scope), named by a SHA-256 of `{scope}|{key}` so
/// arbitrary subject ids / method names / client keys produce a safe,
/// collision-resistant blob name. TTL is carried in the envelope and
/// enforced lazily on `TryGet` (an expired entry is read as a miss and
/// best-effort deleted).
///
/// **An entry it cannot read REFUSES the call** (`TryGet` raises
/// `DocumentUnavailableException`; an undecodable entry is set aside by the
/// document store). Read as a miss, as it used to be, it re-executed the
/// handler — the one thing this store exists to prevent, on exactly the
/// calls (payments, provisioning) that carry an idempotency key.
///
/// **Concurrency.** Two requests racing the same key both miss-then-store;
/// over a backend with conditional writes the store's guarded write orders
/// them, and without one the second overwrites the first — last-write-wins.
/// The handler is idempotent by contract, so both writes carry the same
/// response and the race is benign for replay correctness; it does NOT
/// guarantee exactly-once *handler invocation* under a concurrent race
/// (neither does the in-process default — that needs a claim before the
/// handler runs, which the interface doesn't model). Distributed
/// deployments that need stricter once-only semantics wire a store with a
/// compare-and-set primitive (Redis SETNX / DynamoDB conditional put)
/// against the same contract.
type BlobIdempotencyStore(blobStorage: IBlobStorage, ?container: string) =
    let container = defaultArg container BlobIdempotencyLayout.DefaultContainer

    /// The key whose container is `container`: `_platform` by default.
    let scope =
        ScopeKey.tryParseContainer container
        |> Option.defaultValue (ScopeKey.Claim container)

    let entries =
        BlobDocumentStore<MemoisedEntry>(
            blobStorage,
            MemoisedEntry.codec,
            JsonConsoleLogger.JsonConsoleLogger() :> ILogger
        )

    let pathOf (scope: string) (key: string) : DocPath =
        use sha = System.Security.Cryptography.SHA256.Create()
        let bytes = System.Text.Encoding.UTF8.GetBytes(scope + "|" + key)
        let hash = Convert.ToHexString(sha.ComputeHash bytes).ToLowerInvariant()
        DocPath.ofString (BlobIdempotencyLayout.BlobPrefix + hash + ".json")

    interface IIdempotencyStore with
        member _.TryGet(key, callScope) = async {
            let path = pathOf callScope key
            let! read = entries.Read(scope, path)

            match DocumentUnavailable.orRaise read with
            | None -> return None
            | Some entry when DateTimeOffset.UtcNow.UtcTicks < entry.ExpiryUtcTicks -> return Some entry.Response
            | Some _ ->
                // Lazy TTL expiry — best-effort delete, read as miss.
                let! _ = entries.Delete(scope, path) // best-effort-write: expired envelope reads as a miss; the next TryGet re-issues the delete and IdempotencySweepJob re-lists it
                return None
        }

        member _.Store(key, callScope, response, ttl) = async {
            let path = pathOf callScope key

            let entry = {
                Response = response
                ExpiryUtcTicks = (DateTimeOffset.UtcNow + ttl).UtcTicks
            }

            // A failed write RAISES: `Store` returning normally is the claim
            // "this call is memoised", and a retry that finds nothing
            // re-executes the handler — the one thing this store exists to
            // prevent. The dispatcher catches it and answers the call
            // un-memoised, loudly.
            match! entries.Replace(scope, path, entry) with
            | Ok() -> return ()
            | Error e ->
                return
                    raise (IdempotencyStoreWriteException(container, DocPath.value path, BlobMapStoreError.describe e))
        }