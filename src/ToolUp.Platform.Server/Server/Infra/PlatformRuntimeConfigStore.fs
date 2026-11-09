module ToolUp.Platform.Infra.PlatformRuntimeConfigStore

open System
open System.Text
open System.Text.Json
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── The runtime config store ─────────────────────────────────────────
//
// `BlobBackedPlatformRuntimeConfigStore` persists the runtime override
// for `ServerConfig.PlatformKnowledgeBase` at `_platform/runtime-config.json`
// through the document store, and exposes synchronous `Snapshot` reads from
// an in-memory cell initialised at startup. Set calls persist + update the
// cell atomically (write-through cache).
//
// **Wire format:**
//   { "platformKnowledgeBase": "Enabled" | "Disabled" }
//
// String form rather than DU JSON because the persisted blob may
// outlive the SDK version that wrote it; round-tripping through
// strings is more robust than DU case-name reflection.
//
// Until Phase 1061 the document was written as `{}`: its DTO was a
// private record, whose properties F# emits as internal, so the serializer
// saw no members and the override never survived a restart (the read of
// `{}` threw and fell back to the default). The codec now writes the
// format above by hand and reads a `{}` already at rest as the default,
// which is what it has always meant.

let private blobName = DocPath.ofString "runtime-config.json"

[<Literal>]
let private enabledLiteral = "Enabled"

[<Literal>]
let private disabledLiteral = "Disabled"

[<Literal>]
let private property = "platformKnowledgeBase"

/// The document's codec. No property (the `{}` written before Phase 1061)
/// and a mode this SDK does not know (written by a later one) both read as
/// `defaultMode`; bytes that are not a JSON object are `Error`, which the
/// store quarantines.
let internal codec (defaultMode: FeatureMode) : BlobCodec<FeatureMode> = {
    Encode =
        fun mode ->
            let value =
                match mode with
                | FeatureMode.Enabled -> enabledLiteral
                | FeatureMode.Disabled -> disabledLiteral

            Encoding.UTF8.GetBytes $"{{\"{property}\":\"{value}\"}}"
    Decode =
        fun bytes ->
            try
                use doc = JsonDocument.Parse(bytes: byte[])
                let root = doc.RootElement

                if root.ValueKind <> JsonValueKind.Object then
                    Error $"the runtime config document is a JSON {root.ValueKind}, not an object"
                else
                    match root.TryGetProperty property with
                    | true, value when value.ValueKind = JsonValueKind.String ->
                        match value.GetString() with
                        | s when s = enabledLiteral -> Ok FeatureMode.Enabled
                        | s when s = disabledLiteral -> Ok FeatureMode.Disabled
                        | _ -> Ok defaultMode
                    | _ -> Ok defaultMode
            with ex ->
                Error ex.Message
}

/// Default `IPlatformRuntimeConfigStore` impl. Persists overrides to
/// `_platform/runtime-config.json` through the document store; the
/// in-memory cell holds the current value for hot-path `Snapshot`
/// reads. Constructed via the `create` factory below — the factory
/// loads the persisted value at startup so the cell starts in the
/// right state without an extra request-time read.
type private BlobBackedPlatformRuntimeConfigStore(docs: BlobDocumentStore<FeatureMode>, initialValue: FeatureMode) =
    let mutable cell = initialValue
    let writeLock = new SemaphoreSlim(1, 1)

    interface IPlatformRuntimeConfigStore with
        member _.Snapshot() = cell
        member _.GetPlatformKnowledgeBase() = async { return cell }

        member _.SetPlatformKnowledgeBase mode = async {
            do! writeLock.WaitAsync() |> Async.AwaitTask

            try
                match! docs.Update(ScopeKey.platform, blobName, (fun _ -> BlobUpdate.Write(mode, ()))) with
                | Ok() ->
                    cell <- mode
                    return Ok()
                | Error e -> return Error(BlobMapStoreError.describe e)
            finally
                writeLock.Release() |> ignore
        }

/// Construct a runtime config store, loading the persisted override
/// (if any) at startup — `compose` calls this once via
/// `Async.RunSynchronously` before the request pipeline starts. No
/// document is `defaultMode`; a document that cannot be read or does not
/// decode RAISES `DocumentUnavailableException`, which composition turns
/// into a refusal at preflight: an override the operator set is never
/// silently replaced by the static default.
let create (storage: IBlobStorage) (defaultMode: FeatureMode) : Async<IPlatformRuntimeConfigStore> = async {
    let docs =
        BlobDocumentStore<FeatureMode>(storage, codec defaultMode, JsonConsoleLogger.JsonConsoleLogger())

    let! loaded = docs.Read(ScopeKey.platform, blobName)

    let initial =
        loaded |> DocumentUnavailable.orRaise |> Option.defaultValue defaultMode

    return BlobBackedPlatformRuntimeConfigStore(docs, initial) :> IPlatformRuntimeConfigStore
}