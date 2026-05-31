module ToolUp.Platform.DataSourceConfigStore

open System.Text
open Newtonsoft.Json
open ToolUp.Remoting.Json
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

let private platformContainer = "_platform"

let private configBlob (scopeId: string) (sourceId: DataSourceId) =
    $"data-sources/{scopeId}/configs/{sourceId}.json"

let private configsPrefix (scopeId: string) = $"data-sources/{scopeId}/configs/"

// ─── JSON ────────────────────────────────────────────────────────
//
// `DataSourceConfig` round-trips to the Fable admin UI, so use
// `FableJsonConverter` (DU-aware shape compatible with `Fable.SimpleJson`
// on the client). Same pattern as `WebhookRegistry`, `ConfigStore`,
// `BlobJobStore`.

module private Json =
    let private settings =
        let s = JsonSerializerSettings()
        s.Converters.Add(FableJsonConverter())
        s

    let serialize (value: 'T) : byte[] =
        JsonConvert.SerializeObject(value, settings) |> Encoding.UTF8.GetBytes

    let tryDeserialize<'T> (bytes: byte[]) : 'T option =
        try
            let json = Encoding.UTF8.GetString bytes
            Some(JsonConvert.DeserializeObject<'T>(json, settings))
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
            // Idempotent — Delete on a missing blob returns Ok in
            // every shipped IBlobStorage; we treat any failure as
            // a no-op so the contract stays "deleting a non-existent
            // id is a no-op".
            let! _ = storage.Delete(platformContainer, configBlob scopeId sourceId)
            return ()
        }

let create (storage: IBlobStorage) : IDataSourceConfigStore =
    BlobDataSourceConfigStore(storage) :> IDataSourceConfigStore