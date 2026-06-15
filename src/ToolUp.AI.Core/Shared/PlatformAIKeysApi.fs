// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open ToolUp.Platform

// ─── Phase 70 — Platform Admin AI keys Fable.Remoting contract ───
//
// Wire surface for the Platform Admin keys module. Every method gates
// server-side on `AccessContext.canModifyPlatformConfig`; non-admin
// callers receive `Error "platform admin role required"` from every
// method without the underlying store being touched.
//
// **The current key value is never returned by any read path.** The
// admin UI uses `HasKey` booleans to render "key configured" badges;
// rotation goes through `SetKey` with a fresh value. There is no
// "show me the current key" affordance by design — once a key is
// stored, the only way to see it again is at the source (Anthropic /
// OpenAI / Google console).

// ─── Wire-side records ──────────────────────────────────────────

/// One row of the Platform Admin keys table: which provider, and
/// whether a platform-scope key is stored for it.
type PlatformAIKeyStatus = { ProviderId: string; HasKey: bool }

/// One row of the per-team key status table: which (team, provider)
/// combination, and whether a team-scope override is stored. The UI
/// renders one of these per (team × provider) cell.
type TeamAIKeyStatus = {
    TeamId: string
    ProviderId: string
    HasKey: bool
}

/// Save-key request shape. Single field for the value so the wire
/// payload's intent is unambiguous — and so the value never appears
/// in URL path / query positions where logging middleware could
/// capture it. `ApiKey` is deliberately NOT `[<PiiSafe>]` — the
/// dispatcher's audit snapshot redacts it.
type SetPlatformAIKeyRequest = {
    [<PiiSafe>]
    [<NotEmpty>]
    ProviderId: string
    // Phase 69e.H — the dispatcher rejects a blank / whitespace-only key
    // with a structured validation envelope. The handler's own
    // empty-key guard is retained as defence-in-depth for direct callers
    // (the attribute fires on the HTTP path only).
    [<NotEmpty>]
    ApiKey: string
}

type SetTeamAIKeyRequest = {
    [<PiiSafe>]
    [<NotEmpty>]
    TeamId: string
    [<PiiSafe>]
    [<NotEmpty>]
    ProviderId: string
    [<NotEmpty>]
    ApiKey: string
}

/// Minimal team-projection surface for the admin UI's "pick a team"
/// dropdown. Re-projected from `ITeamStore` server-side so the admin
/// UI doesn't need a second proxy.
type PlatformAIKeysTeamView = { TeamId: string; DisplayName: string }

// ─── API contract ───────────────────────────────────────────────

/// Fable.Remoting contract for the Platform Admin keys module
/// (Phase 70 Stream D). Every method is gated on
/// `canModifyPlatformConfig` server-side; non-admins receive
/// `Error "platform admin role required"`.
type PlatformAIKeysApi = {
    /// List the descriptors the deployment has wired. Drives the
    /// provider dropdown in the admin form. Empty when no platform
    /// providers are configured — the admin UI surfaces "no platform
    /// providers wired; nothing to manage" in that case.
    [<RequiresRole "PlatformAdmin">]
    ListPlatformDescriptors: unit -> Async<AIProviderDescriptor list>

    /// One status row per wired provider. `HasKey = true` when a
    /// platform-scope key is recorded; `false` otherwise. Refreshed
    /// after every `SetPlatformKey` / `DeletePlatformKey`.
    [<RequiresRole "PlatformAdmin">]
    ListPlatformKeyStatuses: unit -> Async<PlatformAIKeyStatus list>

    /// Store (or overwrite) a platform-scope key. Idempotent — re-
    /// applying the same value is byte-identical. Validation: the
    /// `ProviderId` must match a wired descriptor. Returns
    /// `Error "platform admin role required"` for non-admins.
    [<RequiresRole "PlatformAdmin">]
    [<Audit "PolicyChanged">]
    SetPlatformKey: SetPlatformAIKeyRequest -> Async<Result<unit, string>>

    /// Remove a platform-scope key. Idempotent. The string argument
    /// is the provider id. Returns `Error "platform admin role
    /// required"` for non-admins.
    [<RequiresRole "PlatformAdmin">]
    [<Audit "PolicyChanged">]
    DeletePlatformKey: string -> Async<Result<unit, string>>

    /// Verify the stored platform-scope key by issuing a minimal ping
    /// against the provider's `Build`-curried adapter. Returns
    /// `Ok ()` on any successful provider response; the model output
    /// doesn't matter. Returns `Error msg` when the key is missing,
    /// invalid, or the call fails. Issues a billed provider request.
    [<RequiresRole "PlatformAdmin">]
    TestPlatformKey: string -> Async<Result<unit, string>>

    /// List teams visible to the admin — drives the per-team key
    /// table. Thin re-projection over `ITeamStore` so the admin UI
    /// doesn't need a separate proxy. Empty list in deployments
    /// without teams configured.
    [<RequiresRole "PlatformAdmin">]
    ListTeams: unit -> Async<PlatformAIKeysTeamView list>

    /// One status row per (teamId × providerId) combination the
    /// admin asks about. Returns a row for every (team, provider)
    /// pair the admin's filter matches; `HasKey = true` when a
    /// team-scope override is recorded for that pair. The string
    /// argument is the team id.
    [<RequiresRole "PlatformAdmin">]
    ListTeamKeyStatuses: string -> Async<TeamAIKeyStatus list>

    /// Store (or overwrite) a team-scope override key. Idempotent.
    /// Validation: `ProviderId` must match a wired descriptor;
    /// `TeamId` must reference an existing team. Returns
    /// `Error "platform admin role required"` for non-admins.
    [<RequiresRole "PlatformAdmin">]
    [<Audit "PolicyChanged">]
    SetTeamKey: SetTeamAIKeyRequest -> Async<Result<unit, string>>

    /// Remove a team-scope override key. Idempotent. Argument is
    /// `(teamId, providerId)`. Returns `Error "platform admin role
    /// required"` for non-admins.
    [<RequiresRole "PlatformAdmin">]
    [<Audit "PolicyChanged">]
    DeleteTeamKey: string * string -> Async<Result<unit, string>>

    /// Verify the stored team-scope key. Same shape as
    /// `TestPlatformKey` but resolves the team-scope key first; falls
    /// back with `Error "team-scope key not configured"` when none is
    /// recorded for the pair (the admin should test the platform-scope
    /// key separately in that case). Argument is `(teamId, providerId)`.
    [<RequiresRole "PlatformAdmin">]
    TestTeamKey: string * string -> Async<Result<unit, string>>
}