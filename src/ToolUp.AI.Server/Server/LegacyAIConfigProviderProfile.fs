// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.LegacyAIConfigProviderProfile

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Providers
open ToolUp.AI

// ─── Phase 43.A — legacy `ai-config.json` migration decorator ────
//
// Phase 42.B introduced the canonical platform `IProviderProfile`
// store behind a behaviour-preserving `IUserAIConfigStore` shim
// (`DefaultUserAIConfigStore`). That shim carried a one-time
// read-through for the pre-42.B `ai-config.json` blob so existing
// users' BYOK config survived the relocation.
//
// Phase 43.A removes the shim and cuts the AI factory / settings
// handler / usage-metering wrapper over to read `IProviderProfile`
// directly. This decorator preserves the *one-time migration path*
// that lived in the deleted shim: it wraps the canonical
// `BlobProviderProfile` and, when a scope has no canonical
// `provider-profile.json` yet, falls through to the legacy
// `ai-config.json` (the old `AIUserConfig` JSON shape). The value
// lazily migrates forward — the next `Set` (or a `SetEntryHealth`
// from the Phase 43.C probe) writes the canonical blob; `Clear`
// purges the legacy blob too so a cleared scope cannot resurrect.
//
// Layering (hard constraint): the AI-specific legacy JSON shape lives
// here in `ToolUp.AI.Server` as a *private* DTO. `ToolUp.Platform`'s
// `BlobProviderProfile` stays AI-free — it never learns about
// `ai-config.json` or `AIUserConfig`. Removing this decorator (once
// no deployment carries pre-42.B blobs) is a clean, isolated delete.
//
// Deployments with no pre-42.B persisted config can wire
// `BlobProviderProfile.create` directly and skip this decorator
// entirely — it is purely a migration affordance.

// ─── Private legacy DTO (the pre-42.B `AIUserConfig` JSON shape) ──
//
// Intentionally a private mirror, NOT the deleted public
// `ToolUp.AI.AIUserConfig` / `AIProviderInstance` types — those are
// gone (Phase 43.A). Only the four original fields that ever appeared
// in a persisted `ai-config.json` are modelled; they map 1:1 onto
// `ProviderEntry` / `ProviderProfile`.

type private LegacyInstance = {
    Label: string
    ProviderId: string
    Model: string option
    SecretKeyName: string
    UpdatedAt: DateTime
}

type private LegacyConfig = {
    ConfiguredProviders: LegacyInstance list
    ActiveProviderLabel: string option
    PlatformModelOverride: string option
    UpdatedAt: DateTime
}

module private LegacyJson =
    let private options = FableConverters.create ()

    let deserialize (json: string) : LegacyConfig =
        JsonSerializer.Deserialize<LegacyConfig>(json, options)

[<Literal>]
let private legacyBlobName = "ai-config.json"

// ─── Lossless legacy → ProviderProfile mapping ───────────────────
//
// Byte-for-byte the mapping the deleted shim applied (its `toProfile`
// / `instanceToEntry`), so a profile migrated by this decorator is
// indistinguishable from one the shim would have produced — the
// Phase 43 byte-identical-persisted-state regression bar.

let private instanceToEntry (i: LegacyInstance) : ProviderEntry = {
    Label = i.Label
    ProviderId = i.ProviderId
    Model = i.Model
    SecretKeyName = i.SecretKeyName
    Tags = []
    Origin = CredentialOrigin.PastedKey
    Health = ProviderHealth.unknown
    UpdatedAt = i.UpdatedAt
}

let private toProfile (cfg: LegacyConfig) : ProviderProfile =
    let baseProfile = {
        Entries = cfg.ConfiguredProviders |> List.map instanceToEntry
        Routing = []
        Fallback = FallbackChain.empty
        SurfaceModelOverrides = []
        SurfaceProviderOverrides = []
        UpdatedAt = cfg.UpdatedAt
    }

    let withRoute =
        match cfg.ActiveProviderLabel with
        | Some label ->
            baseProfile
            |> ProviderProfile.withRoute AIProviderSurface.aiAssistant None label
        | None -> baseProfile

    withRoute
    |> ProviderProfile.withSurfaceModelOverride AIProviderSurface.platformModelKey cfg.PlatformModelOverride

/// Wrap a canonical `IProviderProfile` with the pre-42.B
/// `ai-config.json` read-through. `storage` is the same blob store the
/// canonical `platform` instance is backed by (legacy blobs live in
/// the scope's own already-isolated container).
let wrap (storage: IBlobStorage) (platform: IProviderProfile) : IProviderProfile =
    let readLegacy (scope: StorageScope) : Async<ProviderProfile option> = async {
        let! result = storage.Download(scope.Container, legacyBlobName)

        match result with
        | Ok bytes ->
            try
                let json = Encoding.UTF8.GetString(bytes)
                return Some(toProfile (LegacyJson.deserialize json))
            with _ ->
                // Malformed legacy blob — treat as no configuration so
                // the consumer falls back per its own policy (the same
                // tolerant posture the canonical store and the deleted
                // shim both take).
                return None
        | Error _ -> return None
    }

    /// Canonical-first read with legacy fall-through. Every method
    /// that needs the profile goes through this so legacy-only scopes
    /// resolve consistently (Get / ResolveEntry / SetEntryHealth).
    let readEffective (scope: StorageScope) : Async<ProviderProfile option> = async {
        let! canonical = platform.Get scope

        match canonical with
        | Some _ -> return canonical
        | None -> return! readLegacy scope
    }

    { new IProviderProfile with
        member _.Get scope = readEffective scope

        // Set writes the canonical blob only — the next read prefers
        // it, so the value has migrated forward. The legacy blob is
        // left in place (shadowed) until Clear, exactly as the deleted
        // shim behaved.
        member _.Set(scope, profile) = platform.Set(scope, profile)

        member _.Clear scope = async {
            do! platform.Clear scope
            // Best-effort legacy purge so Clear is total — otherwise a
            // cleared scope could resurrect from the legacy blob via
            // the read-through. Errors (blob absent) are swallowed;
            // Clear is idempotent by contract.
            let! _ = storage.Delete(scope.Container, legacyBlobName)
            return ()
        }

        member _.ResolveEntry(scope, surface, context) = async {
            let! profile = readEffective scope
            return profile |> Option.bind (ProviderProfile.resolveEntry surface context)
        }

        member _.SetEntryHealth(scope, label, health) = async {
            // Legacy-aware: a probe / verification call writing health
            // to a still-legacy scope both attaches the health AND
            // forward-migrates the blob (the canonical Set below). No
            // prior shim behaviour to preserve — `IUserAIConfigStore`
            // had no health surface; this just satisfies the
            // `IProviderProfile` contract (no-op Ok when the scope has
            // no profile or no entry carries the label).
            let! profile = readEffective scope

            match profile with
            | None -> return Ok()
            | Some p ->
                let hasEntry = p.Entries |> List.exists (fun e -> e.Label = label)

                if not hasEntry then
                    return Ok()
                else
                    let entries =
                        p.Entries
                        |> List.map (fun e -> if e.Label = label then { e with Health = health } else e)

                    return!
                        platform.Set(
                            scope,
                            {
                                p with
                                    Entries = entries
                                    UpdatedAt = DateTime.UtcNow
                            }
                        )
        }
    }

/// Convenience: a legacy-migrating `IProviderProfile` over a blob
/// store, wrapping a fresh canonical `BlobProviderProfile`. Mirrors
/// the one-argument shape of the deleted
/// `DefaultUserAIConfigStore.create` so the composition-root change is
/// a one-line type swap.
let create (storage: IBlobStorage) : IProviderProfile =
    wrap storage (BlobProviderProfile.create storage)