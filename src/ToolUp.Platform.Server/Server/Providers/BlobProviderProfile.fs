// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Infra.BlobProviderProfile

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Providers

// Default IProviderProfile implementation — one JSON blob per scope
// ("provider-profile.json"). Phase 42.B landed this behind a
// behaviour-preserving IUserAIConfigStore shim; Phase 43.A removed
// the shim, making this the sole canonical BYOK store (the AI
// assistant's legacy "ai-config.json" one-time migration now lives in
// ToolUp.AI's LegacyAIConfigProviderProfile decorator, not here —
// this store stays AI-free). The profile reads and writes through
// `BlobDocumentStore` with `BlobCodec.json` (the profile carries F# DUs —
// ProviderEntry.Model: string option, CredentialOrigin,
// ProviderHealthStatus — which `PlatformJson` round-trips).

/// Document path within a scope's (already scope-isolated) container.
let private path = DocPath.ofString "provider-profile.json"

/// The key whose container is `scope.Container`: the one it was rendered
/// from, or — for a claim scope, whose id is its container — that claim.
let private keyOf (scope: StorageScope) : ScopeKey =
    StorageScope.tryKey scope
    |> Option.defaultValue (ScopeKey.Claim scope.Container)

/// Build an IProviderProfile backed by an IBlobStorage. Persists one
/// JSON document per StorageScope. A profile that cannot be read or
/// decoded is treated as missing configuration so a consumer falls back
/// per its own policy rather than surfacing an opaque parse error
/// (tolerant-read posture — preserved from the pre-43.A shim it
/// superseded); it is logged, and an undecodable one set aside by the
/// store, so the fallback is never silent. `Set` replaces the profile
/// whole; `SetEntryHealth` is one guarded update, so a profile it cannot
/// read is refused rather than rewritten.
let create (storage: IBlobStorage) : IProviderProfile =
    let logger = JsonConsoleLogger.JsonConsoleLogger() :> ILogger

    let docs =
        BlobDocumentStore<ProviderProfile>(storage, BlobCodec.json<ProviderProfile>, logger)

    let read (scope: StorageScope) : Async<ProviderProfile option> = async {
        match! docs.Read(keyOf scope, path) with
        | Ok profile -> return profile
        | Error e ->
            logger.Warn($"[ProviderProfile] {BlobMapStoreError.describe e}; treating the scope as unconfigured")
            return None
    }

    { new IProviderProfile with
        member _.Get scope = read scope

        member _.Set(scope, profile) = async {
            let! written = docs.Replace(keyOf scope, path, profile)
            return written |> Result.mapError BlobMapStoreError.describe
        }

        member _.Clear scope = async {
            // Clear is idempotent by contract, and `Delete` already answers
            // Ok on an absent document — so an Error is a REFUSAL, not "not
            // found": the profile is still there. Phase 971 — `Clear` has no
            // failure channel, so a refusal raises rather than reading as
            // cleared.
            match! docs.Delete(keyOf scope, path) with
            | Ok() -> return ()
            | Error e ->
                return
                    failwithf
                        "provider profile %s in %s could not be cleared: %s"
                        (DocPath.value path)
                        scope.Container
                        (BlobMapStoreError.describe e)
        }

        member _.ResolveEntry(scope, surface, context) = async {
            let! profile = read scope

            return profile |> Option.bind (ProviderProfile.resolveEntry surface context)
        }

        member _.SetEntryHealth(scope, label, health) = async {
            let! written =
                docs.Update(
                    keyOf scope,
                    path,
                    function
                    // No profile for the scope — nothing to attach health
                    // to. No-op success per contract.
                    | None -> BlobUpdate.Keep()
                    // Stale / unknown label — no-op success per contract
                    // (the probe may race entry deletion).
                    | Some p when not (p.Entries |> List.exists (fun e -> e.Label = label)) -> BlobUpdate.Keep()
                    | Some p ->
                        let entries =
                            p.Entries
                            |> List.map (fun e -> if e.Label = label then { e with Health = health } else e)

                        BlobUpdate.Write(
                            {
                                p with
                                    Entries = entries
                                    UpdatedAt = DateTime.UtcNow
                            },
                            ()
                        )
                )

            return written |> Result.mapError BlobMapStoreError.describe
        }
    }