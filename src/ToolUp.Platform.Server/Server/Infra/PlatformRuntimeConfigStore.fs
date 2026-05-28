module ToolUp.Platform.PlatformRuntimeConfigStore

open System
open System.Text
open System.Text.Json
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 4b deferred follow-up — runtime config store ───────────────
//
// `BlobBackedPlatformRuntimeConfigStore` persists the runtime override
// for `ServerConfig.PlatformKnowledgeBase` at `_platform/runtime-config.json`
// and exposes synchronous `Snapshot` reads from an in-memory cell
// initialised at startup. Set calls persist + update the cell
// atomically (write-through cache).
//
// **Wire format:**
//   { "platformKnowledgeBase": "Enabled" | "Disabled" }
//
// String form rather than DU JSON because the persisted blob may
// outlive the SDK version that wrote it; round-tripping through
// strings is more robust than DU case-name reflection.

let private platformContainer = "_platform"
let private blobName = "runtime-config.json"

[<Literal>]
let private enabledLiteral = "Enabled"

[<Literal>]
let private disabledLiteral = "Disabled"

module private Json =
    let private options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    type private Dto = { PlatformKnowledgeBase: string }

    let serialize (mode: PlatformKnowledgeBaseMode) : byte[] =
        let value =
            match mode with
            | EnabledPlatformKnowledgeBase -> enabledLiteral
            | NoPlatformKnowledgeBase -> disabledLiteral

        JsonSerializer.Serialize({ PlatformKnowledgeBase = value }, options)
        |> Encoding.UTF8.GetBytes

    let deserialize (defaultMode: PlatformKnowledgeBaseMode) (bytes: byte[]) : PlatformKnowledgeBaseMode =
        try
            let json = Encoding.UTF8.GetString bytes
            let dto = JsonSerializer.Deserialize<Dto>(json, options)

            match dto.PlatformKnowledgeBase with
            | s when s = enabledLiteral -> EnabledPlatformKnowledgeBase
            | s when s = disabledLiteral -> NoPlatformKnowledgeBase
            | _ -> defaultMode
        with _ ->
            defaultMode

/// Default `IPlatformRuntimeConfigStore` impl. Persists overrides to
/// `_platform/runtime-config.json` via the injected `IBlobStorage`;
/// in-memory cell holds the current value for hot-path `Snapshot`
/// reads. Constructed via the `create` factory below — the factory
/// loads the persisted value at startup so the cell starts in the
/// right state without an extra request-time read.
type private BlobBackedPlatformRuntimeConfigStore
    (storage: IBlobStorage, defaultMode: PlatformKnowledgeBaseMode, initialValue: PlatformKnowledgeBaseMode) =
    let mutable cell = initialValue
    let writeLock = new SemaphoreSlim(1, 1)

    let save (mode: PlatformKnowledgeBaseMode) = async {
        let bytes = Json.serialize mode
        let! result = storage.Upload(platformContainer, blobName, bytes)

        match result with
        | Ok _ -> return Ok()
        | Error e -> return Error e
    }

    interface IPlatformRuntimeConfigStore with
        member _.Snapshot() = cell
        member _.GetPlatformKnowledgeBase() = async { return cell }

        member _.SetPlatformKnowledgeBase mode = async {
            do! writeLock.WaitAsync() |> Async.AwaitTask

            try
                let! result = save mode

                match result with
                | Ok() ->
                    cell <- mode
                    return Ok()
                | Error e -> return Error e
            finally
                writeLock.Release() |> ignore
        }

/// Construct a runtime config store, loading the persisted override
/// (if any) from blob. Falls back to `defaultMode` when no blob exists
/// or the blob is unreadable. Async because the load happens at
/// startup — `compose` calls this once via `Async.RunSynchronously`
/// before the request pipeline starts.
let create (storage: IBlobStorage) (defaultMode: PlatformKnowledgeBaseMode) : Async<IPlatformRuntimeConfigStore> = async {
    let! result = storage.Download(platformContainer, blobName)

    let initial =
        match result with
        | Ok bytes -> Json.deserialize defaultMode bytes
        | Error _ -> defaultMode

    return BlobBackedPlatformRuntimeConfigStore(storage, defaultMode, initial) :> IPlatformRuntimeConfigStore
}