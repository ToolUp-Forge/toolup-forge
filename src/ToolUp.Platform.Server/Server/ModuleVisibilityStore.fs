module ToolUp.Platform.ModuleVisibilityStore

open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── JSON helpers ────────────────────────────────────────────────

/// Shared `FableConverters` setup — the same pattern `ConfigStore` and
/// `FeatureFlagStore` use, and required here for the same reason:
/// `ModuleVisibilityProfile` carries two DUs (`FlagScope`,
/// `ModuleVisibilityRule`) plus an `option`, and the bare-STJ default
/// contract emits a shape that does not round-trip.
module private Json =
    let private options = FableConverters.create ()

    let serialize (p: ModuleVisibilityProfile) : byte[] =
        JsonSerializer.Serialize(p, options) |> Encoding.UTF8.GetBytes

    let tryDeserialize (bytes: byte[]) : ModuleVisibilityProfile option =
        try
            let json = Encoding.UTF8.GetString bytes
            Some(JsonSerializer.Deserialize<ModuleVisibilityProfile>(json, options))
        with _ ->
            None

// ─── Blob layout ─────────────────────────────────────────────────

let private platformContainer = "_platform"

/// One blob per scope. `FlagScope.slug` encodes the scope kind
/// (`user-` / `team-` / `_platform`) into the path, so a user id and a
/// team id that happen to match never collide — the same isolation
/// argument `FeatureFlagStore.blobName` makes, and the reason both
/// stores key off the DU rather than a bare id.
let private blobName (scope: FlagScope) =
    $"module-visibility/{FlagScope.slug scope}.json"

// ─── IModuleVisibilityStore — blob-backed implementation ─────────

/// Blob-backed `IModuleVisibilityStore`. Thread-safe via the underlying
/// storage; no caching layer.
///
/// The read path is hot — `computeAccessibleModules` resolves a profile
/// per accessible-modules call — but that call happens at boot and on
/// team switch, not per request, so up to three blob reads (User, Team,
/// Platform) is the right trade against a cache that would have to be
/// invalidated by every admin write across every node.
///
/// Named class (rather than an object expression) for consistency with
/// `BlobConfigStore` / `BlobFeatureFlagStore` — matching the store
/// family's shape keeps the cost of the next implementation low.
type BlobModuleVisibilityStore(storage: IBlobStorage) =

    interface IModuleVisibilityStore with
        member _.GetProfile scope = async {
            let! result = storage.Download(platformContainer, blobName scope)

            match result with
            | Ok bytes -> return Json.tryDeserialize bytes
            | Error _ -> return None
        }

        member _.SetProfile(scope, profile) = async {
            // The `scope` parameter — not `profile.Scope` — decides the
            // document path, so a profile whose `Scope` field disagrees
            // (a hand-edited blob, a client-supplied record that slipped
            // past a handler) can never redirect a write at another
            // scope's document. The stored record is normalised to the
            // resolved scope for the same reason, so a later read cannot
            // hand back a profile that lies about where it lives.
            let normalised = { profile with Scope = scope }
            let! result = storage.Upload(platformContainer, blobName scope, Json.serialize normalised)
            return result |> Result.map ignore
        }

        member _.ClearProfile scope = async {
            let! existing = storage.Download(platformContainer, blobName scope)

            match existing with
            | Ok _ ->
                let! _ = storage.Delete(platformContainer, blobName scope)
                return ()
            | Error _ ->
                // Nothing stored — clearing is a no-op rather than an
                // error, so an admin UI can offer "reset" unconditionally.
                return ()
        }

/// Convenience factory — construct and upcast. Mirrors the `create`
/// pattern used by every other store in the SDK.
let create (storage: IBlobStorage) : IModuleVisibilityStore =
    BlobModuleVisibilityStore(storage) :> IModuleVisibilityStore