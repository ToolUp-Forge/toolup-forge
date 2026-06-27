// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Collections.Concurrent
open ToolUp.Platform
open DataManagementTypes

// ─── Per-file ingestion-status store (Phase 173) ─────────────────
//
// Surfaces RAG ingestion status (`NotIngested` / `Pending` / `Indexed`
// / `Failed reason`) per Data Manager file. The store interface lives
// in `ToolUp.Platform.Server` — not in the RAG companion — because
// `FileManagement` (Platform.Server) resolves it to join status onto
// the file-list read, and a lower layer can't depend on the RAG
// companion above it. RAG owns the *writes* (the post-save hook marks
// `Pending` / `Failed`, the ingestion observer marks `Indexed`); the
// SDK core owns the seam + the default `IDataObjectStore`-backed
// implementation, mirroring the `ProcessedEntryStore` /
// `ColumnMappingStore` sidecar precedent.
//
// Registered only when RAG is composed (a deployment with no
// `VectorisationHandler`s never resolves it ⇒ no status column,
// GP 13). Off-by-default-safe by construction: absence of the
// singleton makes `FileManagement` emit an empty `Ingestion` list.

/// Per-file RAG ingestion-status store. Every method is `scopeId`-first
/// + `Async` per the six portability rules; `scopeId` carries the
/// storage container (e.g. `team-{id}` / `user-{id}`), matching how
/// `ProcessedEntryStore` keys the file sidecars it sits alongside.
///
/// `documentId` is the file name (the post-save hook attributes the
/// `DocumentIngestionJob` by `entry.FileName`), so a `List` result
/// joins onto `FileListSnapshot.Files` by name directly.
type IIngestionStatusStore =
    /// Record a document as `Pending` with its expected chunk total
    /// (called by the post-save hook at enqueue time). The total drives
    /// the observer's completion detection — `Indexed` fires when the
    /// indexed-chunk count reaches it.
    abstract SetPending: scopeId: string * documentId: string * totalChunks: int -> Async<unit>

    /// Set an explicit / terminal status (`Indexed` / `Failed` /
    /// `NotIngested`). Overwrites any prior status for the document.
    abstract Set: scopeId: string * documentId: string * status: FileIngestionStatus -> Async<unit>

    /// Current status for one document, or `None` when the store has no
    /// entry for it (⇒ treated as `NotIngested` by callers).
    abstract Get: scopeId: string * documentId: string -> Async<FileIngestionStatus option>

    /// Recorded chunk total for a document (0 when unknown / no entry).
    /// Used by the ingestion observer to detect the last chunk.
    abstract GetTotal: scopeId: string * documentId: string -> Async<int>

    /// Every `(documentId, status)` pair in a scope — the file-list join.
    abstract List: scopeId: string -> Async<(string * FileIngestionStatus) list>

/// Default `IIngestionStatusStore` implementations: an
/// `IDataObjectStore`-backed sidecar (durable, survives restart) and an
/// in-memory fallback for ephemeral / test scopes with no
/// `IDataObjectStore`.
module IngestionStatusStore =
    open System.Text.Json
    open ToolUp.Remoting.Json.SystemTextJson

    /// Sidecar `ObjectId` prefix — matches the `_processed_entry__` /
    /// `_conversion__` SDK-internal-data-type idiom so the entries stay
    /// distinguishable in `IDataCatalog` views.
    [<Literal>]
    let private Prefix = "_ingestionstatus__"

    /// `dataType` metadata tag recorded on the persisted blob.
    [<Literal>]
    let DataType = "_ingestionstatus"

    let private jsonOptions = FableConverters.create ()

    /// Persisted shape — the status plus the expected chunk total so the
    /// observer can detect completion after a process restart (the
    /// total would otherwise be lost with the in-memory enqueue state).
    type private Entry = {
        Status: FileIngestionStatus
        TotalChunks: int
    }

    let private objectIdFor (documentId: string) : string = Prefix + documentId

    let private tryParseDocumentId (objectId: string) : string option =
        if objectId.StartsWith Prefix then
            Some(objectId.Substring Prefix.Length)
        else
            None

    /// `IDataObjectStore`-backed sidecar store. Durable: status survives
    /// a process restart, so a mid-ingest restart still reports the last
    /// persisted state.
    let create (store: IDataObjectStore) (logger: ILogger option) : IIngestionStatusStore =
        let warn (msg: string) =
            logger |> Option.iter (fun l -> l.Warn msg)

        let loadEntry (scopeId: string) (documentId: string) : Async<Entry option> = async {
            match! store.Get(scopeId, objectIdFor documentId) with
            | Ok(_, bytes) ->
                try
                    let json = System.Text.Encoding.UTF8.GetString bytes
                    return Some(JsonSerializer.Deserialize<Entry>(json, jsonOptions))
                with ex ->
                    warn (
                        sprintf "[IngestionStatusStore] could not deserialise status for '%s': %s" documentId ex.Message
                    )

                    return None
            | Error _ -> return None
        }

        let saveEntry (scopeId: string) (documentId: string) (entry: Entry) : Async<unit> = async {
            let json = JsonSerializer.Serialize(entry, jsonOptions)
            let bytes = System.Text.Encoding.UTF8.GetBytes json

            do!
                store.Save(scopeId, objectIdFor documentId, bytes, DataType, "system", Map.empty, Unversioned)
                |> Async.Ignore
        }

        { new IIngestionStatusStore with
            member _.SetPending(scopeId, documentId, totalChunks) =
                saveEntry scopeId documentId {
                    Status = Pending
                    TotalChunks = totalChunks
                }

            member _.Set(scopeId, documentId, status) = async {
                // Preserve the recorded chunk total across a status
                // transition so a later restart-time read still knows it.
                let! existing = loadEntry scopeId documentId
                let total = existing |> Option.map _.TotalChunks |> Option.defaultValue 0

                do! saveEntry scopeId documentId { Status = status; TotalChunks = total }
            }

            member _.Get(scopeId, documentId) = async {
                let! entry = loadEntry scopeId documentId
                return entry |> Option.map _.Status
            }

            member _.GetTotal(scopeId, documentId) = async {
                let! entry = loadEntry scopeId documentId
                return entry |> Option.map _.TotalChunks |> Option.defaultValue 0
            }

            member _.List(scopeId) = async {
                let! objects = store.ListObjects(scopeId)

                let! pairs =
                    objects
                    |> List.choose (fun o -> tryParseDocumentId o.ObjectId)
                    |> List.map (fun documentId -> async {
                        let! entry = loadEntry scopeId documentId
                        return entry |> Option.map (fun e -> documentId, e.Status)
                    })
                    |> Async.Sequential

                return pairs |> Array.toList |> List.choose id
            }
        }

    /// In-memory fallback for ephemeral / test scopes with no
    /// `IDataObjectStore`. Single-process only — keyed by
    /// `scopeId/documentId` so scope isolation holds (status for scope A
    /// is invisible to scope B).
    let createInMemory () : IIngestionStatusStore =
        let entries = ConcurrentDictionary<string * string, FileIngestionStatus * int>()

        { new IIngestionStatusStore with
            member _.SetPending(scopeId, documentId, totalChunks) = async {
                entries[(scopeId, documentId)] <- (Pending, totalChunks)
            }

            member _.Set(scopeId, documentId, status) = async {
                let total =
                    match entries.TryGetValue((scopeId, documentId)) with
                    | true, (_, t) -> t
                    | _ -> 0

                entries[(scopeId, documentId)] <- (status, total)
            }

            member _.Get(scopeId, documentId) = async {
                match entries.TryGetValue((scopeId, documentId)) with
                | true, (status, _) -> return Some status
                | _ -> return None
            }

            member _.GetTotal(scopeId, documentId) = async {
                match entries.TryGetValue((scopeId, documentId)) with
                | true, (_, total) -> return total
                | _ -> return 0
            }

            member _.List(scopeId) = async {
                return
                    entries
                    |> Seq.filter (fun kv -> fst kv.Key = scopeId)
                    |> Seq.map (fun kv -> snd kv.Key, fst kv.Value)
                    |> Seq.toList
            }
        }