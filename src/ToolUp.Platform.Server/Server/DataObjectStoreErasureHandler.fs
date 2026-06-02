module ToolUp.Platform.DataObjectStoreErasureHandler

open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.IDataExporter

// ─── Phase 9h — data-object-store DSR adapter ────────────────────────
//
// Bridges `IDataObjectStore.Erase` into the orchestrator's
// composition-time extension points (IDataExporter / IErasureHandler).
// Same additive shape as EventStoreErasureHandler — a deployment opts
// the data-object store into DSR by registering these at compose time;
// the orchestrator and every other store are untouched.

[<Literal>]
let private HandlerName = "data-objects"

// DataObject carries an F# Map + a VersioningPolicy DU; the platform
// standard (DataObjectStore.fs / ConfigStore.fs) round-trips these via
// Newtonsoft + FableJsonConverter. System.Text.Json mangles both.
let private jsonOptions = FableConverters.create ()

/// `IDataExporter` for the data-object store. Emits the latest-version
/// metadata of every object in the caller's scope that names the
/// subject as one JSON segment. Content bytes are not exported (opaque
/// / possibly large); the structured record is the export artefact.
type DataObjectStoreExporter(store: IDataObjectStore) =
    interface IDataExporter with
        member _.Name = HandlerName

        member _.Export(scopeId, subjectUserId) = async {
            if Erasure.isBlankSubject subjectUserId then
                return []
            else
                let! objects = store.ListObjects scopeId

                let matched =
                    objects
                    |> List.filter (fun o ->
                        o.CreatedBy = subjectUserId
                        || (o.Metadata |> Map.exists (fun _ v -> v.Contains subjectUserId)))

                if List.isEmpty matched then
                    return []
                else
                    let json = JsonSerializer.Serialize(matched, jsonOptions)

                    return [
                        {
                            Name = sprintf "data-objects/%s.json" scopeId
                            MimeType = "application/json"
                            Body = Encoding.UTF8.GetBytes json
                        }
                    ]
        }

/// `IErasureHandler` for the data-object store. Delegates to
/// `IDataObjectStore.Erase`; `dryRun = true` serves the preview.
type DataObjectStoreErasureHandler(store: IDataObjectStore) =
    interface IErasureHandler with
        member _.Name = HandlerName

        member _.Erase(scopeId, subjectUserId, policy) =
            store.Erase(scopeId, subjectUserId, policy, false)

        member _.Preview(scopeId, subjectUserId, policy) = async {
            let! result = store.Erase(scopeId, subjectUserId, policy, true)

            return
                match result with
                | Result.Ok summary -> summary
                | Result.Error err -> {
                    HandlerName = HandlerName
                    RecordsAffected = 0
                    Note = Some(ErasureError.toMessage err)
                  }
        }

/// Compose-time registration helpers (the IDataExporter /
/// IErasureHandler extension points — no composition-root edit).
let exporter (store: IDataObjectStore) : IDataExporter =
    DataObjectStoreExporter store :> IDataExporter

let erasureHandler (store: IDataObjectStore) : IErasureHandler =
    DataObjectStoreErasureHandler store :> IErasureHandler