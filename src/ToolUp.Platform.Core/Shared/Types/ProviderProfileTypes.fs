// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Canonical platform-wide multi-provider BYOK configuration ───
//
// Relocated + superset of ToolUp.AI's AIProviderInstance /
// AIUserConfig (which become a behaviour-preserving shim over this;
// the full AI-module cutover + shim removal follows later).
// Lives on the no-dep Platform.Core floor so any BYOK consumer — the
// AI assistant or any other consumer — resolves "which provider /
// which key" without taking a dependency on the AI assistant companion.

/// How the credential for a ProviderEntry was supplied. PastedKey is
/// the S0 path (user pastes an API key; stored under SecretKeyName in
/// ISecretStore). OAuthConnected is the S1 path — the entry
/// is bound to an IOAuthCredentialFlow correlation instead of a static
/// key.
[<RequireQualifiedAccess>]
type CredentialOrigin =
    | PastedKey
    | OAuthConnected

/// Liveness classification for a configured provider. Written by the
/// background probe and the verification-on-add call.
[<RequireQualifiedAccess>]
type ProviderHealthStatus =
    /// Verified working at LastVerifiedAt; no recent failures.
    | Healthy
    /// Reachable but flagged — elevated error rate or low rate-limit
    /// headroom. Advisory; resolution still routes here.
    | Degraded
    /// Recent call(s) failed (bad / expired key, quota exhausted). The
    /// UI surfaces a warning before the next request fails.
    | Unhealthy
    /// OAuth refresh failed; the user must reconnect.
    | NeedsReauthorization
    /// Never verified yet (freshly added; probe not yet run).
    | Unknown

/// Advisory live status for one ProviderEntry. Never load-bearing for
/// correctness — resolution does not gate on it; it drives the
/// dashboard warning badge so a user fixes a failing provider before
/// it surfaces as a failed request.
type ProviderHealth = {
    LastVerifiedAt: DateTime option
    /// Rolling count of recent failures observed by the probe. Reset
    /// to 0 on a successful verification.
    RecentErrorCount: int
    /// Fraction of the provider's rate limit still available (0.0–1.0)
    /// when the provider's API exposes it; None when it does not.
    RateLimitHeadroom: float option
    Status: ProviderHealthStatus
}

module ProviderHealth =
    /// Health of a freshly-added entry before the first probe.
    let unknown = {
        LastVerifiedAt = None
        RecentErrorCount = 0
        RateLimitHeadroom = None
        Status = ProviderHealthStatus.Unknown
    }

/// One configured provider instance in a user's (or team's) catalogue.
/// Superset of ToolUp.AI.AIProviderInstance — the extra fields (Tags,
/// Origin, Health) are additive; the original four (Label, ProviderId,
/// Model, SecretKeyName) keep identical meaning so the AI shim maps
/// 1:1 and round-trips losslessly.
type ProviderEntry = {
    /// User-chosen label, unique within a ProviderProfile.Entries.
    Label: string
    /// Provider descriptor id (matches an AIProviderDescriptor.Id in
    /// the consuming factory's catalogue). Stale ids surface at
    /// resolve time in the consumer, not here.
    ProviderId: string
    /// Optional model override; None → the descriptor's default model.
    Model: string option
    /// ISecretStore key under which this entry's API key lives. Per-
    /// entry (not per-provider) so a user can hold several keys for
    /// the same provider. Unused for OAuthConnected entries.
    SecretKeyName: string
    /// User-assigned free-form tags ("fast", "cheap", "premium",
    /// "eu-resident", "local"). Feed optional cost-class hints; not
    /// interpreted by the store.
    Tags: string list
    /// How the credential was supplied.
    Origin: CredentialOrigin
    /// Advisory live status. Defaults to ProviderHealth.unknown.
    Health: ProviderHealth
    /// Last mutation; advisory ("added N days ago").
    UpdatedAt: DateTime
}

/// One routing decision: for a named Surface (and optionally a finer
/// Context within it), use the entry with EntryLabel. Replaces the
/// single AIUserConfig.ActiveProviderLabel with per-surface / per-
/// context selection. The AI shim represents "active for AI" as the
/// rule { Surface = "ai.assistant"; Context = None }.
type RoutingRule = {
    /// Coarse routing key, e.g. "ai.assistant", "rental.gateway".
    Surface: string
    /// Optional finer key within the surface (e.g. a specific bank id
    /// for the rental gateway). None = the surface default.
    Context: string option
    /// Target entry's Label. A stale label (no matching entry)
    /// resolves to None, mirroring AIUserConfig.activeInstance.
    EntryLabel: string
}

/// Ordered fallback by entry label, tried left-to-right when the
/// resolved primary fails. Opt-in: empty means fail-fast (some users
/// prefer that for cost control). Not advertised in any SKU.
type FallbackChain = { Ordered: string list }

module FallbackChain =
    let empty = { Ordered = [] }

/// Per-user (or per-team) multi-provider BYOK configuration. Canonical
/// superset of ToolUp.AI.AIUserConfig. Persisted one blob per
/// StorageScope by the default IProviderProfile implementation; secret
/// material never lives here (only SecretKeyName references do).
type ProviderProfile = {
    /// Configured provider instances. Empty = nothing configured;
    /// consumers fall back per their own policy.
    Entries: ProviderEntry list
    /// Per-surface / per-context routing. Empty = no selection.
    Routing: RoutingRule list
    /// Optional ordered fallback by label.
    Fallback: FallbackChain
    /// Per-surface model override for a surface's *platform-configured*
    /// provider (distinct from a per-entry Model). Assoc list rather
    /// than Map for FableJsonConverter round-trip safety + codebase
    /// consistency. The AI shim stores AIUserConfig.PlatformModelOverride
    /// under key "ai.platform".
    SurfaceModelOverrides: (string * string) list
    /// Phase 70: per-surface provider-id override carrying the user's
    /// preferred provider among a deployment's wired multi-platform-
    /// provider list. Sibling of SurfaceModelOverrides; same assoc-
    /// list shape for the same FableJsonConverter round-trip reason.
    /// The AI surface stores the user's PlatformProviderOverride under
    /// key "ai.platform.provider"; the factory consults this when
    /// PlatformOnly mode wires multiple platform providers.
    SurfaceProviderOverrides: (string * string) list
    UpdatedAt: DateTime
}

module ProviderProfile =
    /// A fresh, empty profile. UpdatedAt set at call time.
    let empty () = {
        Entries = []
        Routing = []
        Fallback = FallbackChain.empty
        SurfaceModelOverrides = []
        SurfaceProviderOverrides = []
        UpdatedAt = DateTime.UtcNow
    }

    /// The entry a (surface, context) pair resolves to, if any. A more
    /// specific rule (matching Context) wins over the surface default
    /// (Context = None). Returns None when no rule matches or the
    /// matched rule's EntryLabel is stale (no such entry) — identical
    /// None-on-stale semantics to ToolUp.AI.AIUserConfig.activeInstance.
    let resolveEntry (surface: string) (context: string option) (profile: ProviderProfile) : ProviderEntry option =
        let ruleLabel =
            let exactMatch =
                context
                |> Option.bind (fun c ->
                    profile.Routing
                    |> List.tryFind (fun r -> r.Surface = surface && r.Context = Some c))

            match exactMatch with
            | Some r -> Some r.EntryLabel
            | None ->
                profile.Routing
                |> List.tryFind (fun r -> r.Surface = surface && r.Context = None)
                |> Option.map _.EntryLabel

        ruleLabel
        |> Option.bind (fun label -> profile.Entries |> List.tryFind (fun e -> e.Label = label))

    /// Upsert a routing rule for (surface, context) → label, replacing
    /// any existing rule for the same (Surface, Context) key. Used by
    /// the AI shim to map ActiveProviderLabel onto the
    /// ("ai.assistant", None) rule.
    let withRoute
        (surface: string)
        (context: string option)
        (label: string)
        (profile: ProviderProfile)
        : ProviderProfile =
        let kept =
            profile.Routing
            |> List.filter (fun r -> not (r.Surface = surface && r.Context = context))

        {
            profile with
                Routing =
                    kept
                    @ [
                        {
                            Surface = surface
                            Context = context
                            EntryLabel = label
                        }
                    ]
        }

    /// Read a surface model override.
    let surfaceModelOverride (surface: string) (profile: ProviderProfile) : string option =
        profile.SurfaceModelOverrides
        |> List.tryFind (fun (k, _) -> k = surface)
        |> Option.map snd

    /// Upsert a surface model override; None clears it.
    let withSurfaceModelOverride (surface: string) (model: string option) (profile: ProviderProfile) : ProviderProfile =
        let kept = profile.SurfaceModelOverrides |> List.filter (fun (k, _) -> k <> surface)

        let updated =
            match model with
            | Some m -> kept @ [ (surface, m) ]
            | None -> kept

        {
            profile with
                SurfaceModelOverrides = updated
        }

    /// Phase 70 — Read a surface provider-id override.
    let surfaceProviderOverride (surface: string) (profile: ProviderProfile) : string option =
        profile.SurfaceProviderOverrides
        |> List.tryFind (fun (k, _) -> k = surface)
        |> Option.map snd

    /// Phase 70 — Upsert a surface provider-id override; None clears it.
    let withSurfaceProviderOverride
        (surface: string)
        (providerId: string option)
        (profile: ProviderProfile)
        : ProviderProfile =
        let kept =
            profile.SurfaceProviderOverrides |> List.filter (fun (k, _) -> k <> surface)

        let updated =
            match providerId with
            | Some id -> kept @ [ (surface, id) ]
            | None -> kept

        {
            profile with
                SurfaceProviderOverrides = updated
        }