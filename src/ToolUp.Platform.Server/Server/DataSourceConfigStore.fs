module ToolUp.Platform.DataSourceConfigStore

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Storage layout ──────────────────────────────────────────────
//
// Container: always `_platform`.
// Config blob: `data-sources/{scopeId}/configs/{sourceId}.json`
//
// Mirrors the `_platform/jobs/{scopeId}/...` and `_platform/webhooks/
// {scopeId}/...` layouts so operators see one consistent shape under
// `_platform/`. Cross-scope reads are structurally impossible — the
// store never widens the prefix during a scoped operation.

let private platformContainer = ScopeKey.PlatformContainer

let private configBlob (scopeId: string) (sourceId: DataSourceId) =
    $"data-sources/{scopeId}/configs/{sourceId}.json"

let private configsPrefix (scopeId: string) = $"data-sources/{scopeId}/configs/"

// ─── JSON ────────────────────────────────────────────────────────
//
// `DataSourceConfig` round-trips to the Fable admin UI, so use
// `FableConverters` (DU-aware shape compatible with `Fable.SimpleJson`
// on the client). Same pattern as `WebhookRegistry`, `ConfigStore`,
// `BlobJobStore`.

module private Json =
    let private options = FableConverters.create ()

    let serialize (value: 'T) : byte[] =
        JsonSerializer.Serialize(value, options) |> Encoding.UTF8.GetBytes

    let tryDeserialize<'T> (bytes: byte[]) : 'T option =
        try
            let json = Encoding.UTF8.GetString bytes
            Some(JsonSerializer.Deserialize<'T>(json, options))
        with _ ->
            None

let private downloadAll<'T> (storage: IBlobStorage) (blobNames: string list) : Async<'T list> = async {
    let! results =
        blobNames
        |> List.map (fun name -> async {
            let! result = storage.Download(platformContainer, name)

            return
                match result with
                | Ok bytes -> Json.tryDeserialize<'T> bytes
                | Error _ -> None
        })
        |> Async.Parallel

    return results |> Array.choose id |> Array.toList
}

// ─── BlobDataSourceConfigStore ───────────────────────────────────

/// Blob-backed `IDataSourceConfigStore`. One JSON blob per config.
/// Concurrency note: read-modify-write is non-atomic — the same
/// trade-off as `BlobWebhookRegistry`. Configs change rarely (admin
/// UI action), so the last-write-wins window is acceptable. A
/// distributed implementation adding ETag-based CAS drops in without
/// changing the interface.
type BlobDataSourceConfigStore(storage: IBlobStorage) =

    interface IDataSourceConfigStore with
        member _.Save(scopeId, config) = async {
            let bytes = Json.serialize config
            let! result = storage.Upload(platformContainer, configBlob scopeId config.Id, bytes)

            match result with
            | Ok _ -> return ()
            | Error e -> return failwith $"DataSourceConfigStore: failed to persist {config.Id}: {e}"
        }

        member _.Get(scopeId, sourceId) = async {
            let! result = storage.Download(platformContainer, configBlob scopeId sourceId)

            match result with
            | Ok bytes -> return Json.tryDeserialize<DataSourceConfig> bytes
            | Error _ -> return None
        }

        member _.List(scopeId) = async {
            let! names = storage.List(platformContainer, configsPrefix scopeId)
            let! configs = downloadAll<DataSourceConfig> storage names
            return configs |> List.sortBy _.Id
        }

        member _.Delete(scopeId, sourceId) = async {
            // Idempotent — `IBlobStorage.Delete` answers Ok on a missing
            // blob, so deleting a non-existent id is still a no-op. An
            // Error is therefore a refusal (Phase 971): it raises, as a
            // refused `Save` does, rather than reading as deleted while
            // the config is still there.
            match! storage.Delete(platformContainer, configBlob scopeId sourceId) with
            | Ok() -> return ()
            | Error e -> return failwith $"DataSourceConfigStore: failed to delete {sourceId}: {e}"
        }

let create (storage: IBlobStorage) : IDataSourceConfigStore =
    BlobDataSourceConfigStore(storage) :> IDataSourceConfigStore
// ─── Phase 1015 — the declared-delivery ledger ───────────────────
//
// Two records, both under `_platform`:
//
//   * the seeded set — one blob, `data-sources-declared/ledger.json`, beside
//     the per-scope prefixes and never under one, holding the
//     (source id, scope id) pairs the declared sources were seeded at. A
//     source removed from the composition, or whose principal now resolves
//     to another scope, is retired from where the ledger says it was.
//   * the watched objects — one marker blob per ingested object, at
//     `data-sources/{scopeId}/watched/{sourceId}/{sha256(identity)}`, so
//     "was this object ingested" is an `Exists` and two writers never
//     read-modify-write one list. The marker's content is the identity
//     itself, for an operator reading the store.

let private seededLedgerBlob = "data-sources-declared/ledger.json"

let private watchedMarkerBlob (scopeId: string) (sourceId: DataSourceId) (identity: string) =
    let digest =
        SHA256.HashData(Encoding.UTF8.GetBytes identity)
        |> Convert.ToHexString
        |> fun hex -> hex.ToLowerInvariant()

    $"data-sources/{scopeId}/watched/{sourceId}/{digest}"

/// Blob-backed `IDeclaredDeliveryLedger` (Phase 1015).
type BlobDeclaredDeliveryLedger(storage: IBlobStorage) =

    interface IDeclaredDeliveryLedger with

        member _.ReadSeeded() = async {
            let! exists = storage.Exists(platformContainer, seededLedgerBlob)

            if not exists then
                return Ok Set.empty
            else
                match! storage.Download(platformContainer, seededLedgerBlob) with
                | Error e -> return Error $"the declared data-source ledger could not be read: {e}"
                | Ok bytes ->
                    // An array of [source id, scope id] pairs: a BCL shape, so
                    // the ledger reads without the wire converter set.
                    try
                        let entries =
                            JsonSerializer.Deserialize<string array array>(Encoding.UTF8.GetString bytes)

                        if isNull entries || entries |> Array.exists (fun e -> isNull e || e.Length <> 2) then
                            return Error "the declared data-source ledger is not a list of [source, scope] pairs"
                        else
                            return Ok(entries |> Array.map (fun e -> e[0], e[1]) |> Set.ofArray)
                    with ex ->
                        return Error $"the declared data-source ledger is not a ledger this version wrote: {ex.Message}"
        }

        member _.WriteSeeded(seeded) = async {
            let entries =
                seeded
                |> Set.toArray
                |> Array.map (fun (sourceId, scopeId) -> [| sourceId; scopeId |])

            let! result =
                storage.Upload(
                    platformContainer,
                    seededLedgerBlob,
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize entries)
                )

            return result |> Result.map ignore
        }

        member _.WasIngested(scopeId, sourceId, identity) = async {
            try
                let! exists = storage.Exists(platformContainer, watchedMarkerBlob scopeId sourceId identity)
                return Ok exists
            with ex ->
                return Error $"the watched-object record for '{sourceId}' could not be read: {ex.Message}"
        }

        member _.RecordIngested(scopeId, sourceId, identity) = async {
            let! result =
                storage.Upload(
                    platformContainer,
                    watchedMarkerBlob scopeId sourceId identity,
                    Encoding.UTF8.GetBytes identity
                )

            return
                result
                |> Result.map ignore
                |> Result.mapError (fun e -> $"the watched-object record for '{sourceId}' could not be written: {e}")
        }

/// The blob-backed declared-delivery ledger over the deployment's
/// `IBlobStorage` (Phase 1015).
let declaredDeliveryLedger (storage: IBlobStorage) : IDeclaredDeliveryLedger =
    BlobDeclaredDeliveryLedger(storage) :> IDeclaredDeliveryLedger