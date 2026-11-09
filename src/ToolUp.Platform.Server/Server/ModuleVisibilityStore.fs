module ToolUp.Platform.Scope.ModuleVisibilityStore

open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra

// ─── Blob layout ─────────────────────────────────────────────────

/// One blob per scope. `FlagScope.slug` encodes the scope kind
/// (`user-` / `team-` / `_platform`) into the path, so a user id and a
/// team id that happen to match never collide — the same isolation
/// argument `FeatureFlagStore.blobName` makes, and the reason both
/// stores key off the DU rather than a bare id.
let private path (scope: FlagScope) =
    DocPath.ofString $"module-visibility/{FlagScope.slug scope}.json"

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
    let docs =
        BlobDocumentStore<ModuleVisibilityProfile>(
            storage,
            BlobCodec.json<ModuleVisibilityProfile>,
            ToolUp.Platform.Config.ConsoleLogger.ConsoleLogger() :> ILogger
        )

    interface IModuleVisibilityStore with
        // A profile that cannot be read or decoded REFUSES the read
        // (`DocumentUnavailableException`): it governs which modules a scope
        // sees, so it is never answered as "no profile".
        member _.GetProfile scope = async {
            let! loaded = docs.Read(ScopeKey.platform, path scope)
            return DocumentUnavailable.orRaise loaded
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
            let! written = docs.Update(ScopeKey.platform, path scope, (fun _ -> BlobUpdate.Write(normalised, ())))
            return written |> Result.mapError BlobMapStoreError.describe
        }

        member _.ClearProfile scope = async {
            // Deleting an absent profile is `Ok`, so an admin UI can offer
            // "reset" unconditionally. Phase 971 — a refused delete raises
            // rather than reading as cleared while the profile still
            // governs the scope.
            match! docs.Delete(ScopeKey.platform, path scope) with
            | Ok() -> return ()
            | Error e ->
                return
                    failwith
                        $"ModuleVisibilityStore: clearing the profile for {FlagScope.slug scope} was refused: {BlobMapStoreError.describe e}"
        }


/// Convenience factory — construct and upcast. Mirrors the `create`
/// pattern used by every other store in the SDK.
let create (storage: IBlobStorage) : IModuleVisibilityStore =
    BlobModuleVisibilityStore(storage) :> IModuleVisibilityStore