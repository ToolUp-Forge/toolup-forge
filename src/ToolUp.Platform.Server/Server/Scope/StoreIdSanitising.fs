// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.StoreIdSanitising

open ToolUp.Platform.Auth
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.PermissionStore

// ─── Phase 131 — identity sanitisation at the store seam ────────────
//
// `ITeamStore` / `IPermissionStore` interpolate `teamId` / `userId`
// straight into blob keys (`teams/{teamId}.json`,
// `memberships/{userId}.json`, `permissions/{teamId}.json`,
// `active-team/{userId}.txt`). A caller-supplied id of
// `../../_platform/permissions/foo` (or the reserved `_platform` scope
// itself) would let a write land at a chosen path — cross-scope
// corruption / privilege escalation — on the local backend (and any
// backend that path-concats).
//
// These decorators apply the wave-wide `IdentitySanitiser` policy at
// every **write** method (the integrity-critical path the acceptance
// criteria name: `AddMember` / `SetMemberPermissions` /
// `CreateTeam`-with-owner), plus an explicit rejection of the reserved
// `_platform` scope (which passes the charset check — it is all valid
// id characters — but must never be addressable as a team/user id).
// Reads pass through unchanged: they are gated by the caller's resolved
// scope upstream, and a malformed id simply misses; the high-value
// attack is the write-to-chosen-path, which these close before any
// blob write. (Read-seam sanitisation is a documented defence-in-depth
// follow-up.)
//
// Decorator, not in-store guards: uniform at the interface boundary,
// no change to the (varied) method bodies, and it wraps any
// consumer-supplied store implementation too — the SDK composition root
// applies it unconditionally (ComposeTeamRuntime).

/// Reserved scopes a team/user id must never collide with.
let private reservedScopes = Set.ofList [ "_platform" ]

/// Validate a caller-supplied id destined to become a blob-key segment.
/// `Error` carries a categorised reason (never the raw attacker value —
/// `IdentitySanitiser` already avoids echoing it, and the reserved-scope
/// branch names the label, not the value).
let private validate (label: string) (id: string) : Result<unit, string> =
    match IdentitySanitiser.sanitiseScopeId id with
    | Error reason -> Error(sprintf "%s failed identity validation: %s" label reason)
    | Ok value when reservedScopes.Contains value -> Error(sprintf "%s may not be the reserved platform scope" label)
    | Ok _ -> Ok()

let private both (a: Result<unit, string>) (b: Result<unit, string>) : Result<unit, string> =
    match a, b with
    | Error e, _
    | _, Error e -> Error e
    | Ok(), Ok() -> Ok()

/// Run `inner` only when validation passed; short-circuit the
/// pre-validated `Error` into the async result otherwise. The explicit
/// per-method `validate` / `both` call stays at every call site (so it
/// remains auditable that *every* write is guarded) — only the
/// duplicated error-arm collapses here.
let private guard
    (validation: Result<unit, string>)
    (inner: unit -> Async<Result<'a, string>>)
    : Async<Result<'a, string>> =
    match validation with
    | Error e -> async { return Error e }
    | Ok() -> inner ()

/// `ITeamStore` decorator sanitising every id that becomes a blob-key
/// segment on a write. Reads delegate unchanged.
type SanitisingTeamStore(inner: ITeamStore) =
    interface ITeamStore with
        member _.CreateTeam(teamId, name) =
            guard (validate "teamId" teamId) (fun () -> inner.CreateTeam(teamId, name))

        member _.DeleteTeam(teamId) =
            guard (validate "teamId" teamId) (fun () -> inner.DeleteTeam(teamId))

        member _.AddMember(teamId, userId, role) =
            guard (both (validate "teamId" teamId) (validate "userId" userId)) (fun () ->
                inner.AddMember(teamId, userId, role))

        member _.RemoveMember(teamId, userId) =
            guard (both (validate "teamId" teamId) (validate "userId" userId)) (fun () ->
                inner.RemoveMember(teamId, userId))

        member _.ChangeMemberRole(teamId, userId, newRole) =
            guard (both (validate "teamId" teamId) (validate "userId" userId)) (fun () ->
                inner.ChangeMemberRole(teamId, userId, newRole))

        member _.SetActiveTeam(userId, teamId) =
            guard (both (validate "userId" userId) (validate "teamId" teamId)) (fun () ->
                inner.SetActiveTeam(userId, teamId))

        member _.SetArchived(teamId, archived) =
            guard (validate "teamId" teamId) (fun () -> inner.SetArchived(teamId, archived))

        member _.PurgeTeam(teamId) =
            guard (validate "teamId" teamId) (fun () -> inner.PurgeTeam(teamId))

        // Reads delegate unchanged.
        member _.GetTeam(teamId) = inner.GetTeam(teamId)
        member _.ListTeams() = inner.ListTeams()
        member _.GetTeamsForUser(userId) = inner.GetTeamsForUser(userId)
        member _.GetTeamMembers(teamId) = inner.GetTeamMembers(teamId)
        member _.GetMemberRole(teamId, userId) = inner.GetMemberRole(teamId, userId)
        member _.GetActiveTeam(userId) = inner.GetActiveTeam(userId)

/// `IPermissionStore` decorator sanitising every id that becomes a
/// blob-key segment on a write. Reads delegate unchanged.
type SanitisingPermissionStore(inner: IPermissionStore) =
    interface IPermissionStore with
        member _.SetTeamPermissions(teamId, permissions) =
            guard (validate "teamId" teamId) (fun () -> inner.SetTeamPermissions(teamId, permissions))

        member _.SetMemberPermissions(teamId, userId, moduleName, permissions) =
            guard (both (validate "teamId" teamId) (validate "userId" userId)) (fun () ->
                inner.SetMemberPermissions(teamId, userId, moduleName, permissions))

        member _.SetTeamDefaults(teamId, defaults) =
            guard (validate "teamId" teamId) (fun () -> inner.SetTeamDefaults(teamId, defaults))

        member _.SetModuleExposure(teamId, moduleName, state) =
            guard (validate "teamId" teamId) (fun () -> inner.SetModuleExposure(teamId, moduleName, state))

        // Reads delegate unchanged.
        member _.GetTeamPermissions(teamId) = inner.GetTeamPermissions(teamId)

        member _.GetEffectivePermissions(userId, teamId) =
            inner.GetEffectivePermissions(userId, teamId)

        member _.GetModuleExposure(teamId) = inner.GetModuleExposure(teamId)

// ─── Phase 131 (remainder) — IShareTokenStore resource/scope seam ────
//
// `BlobShareTokenStore` interpolates `scopeId` and `tokenId` straight
// into the persisted-claim key
// (`_platform/share-tokens/{scopeId}/{tokenId}.json`) and into the
// `List` prefix the read-seam methods scan. A caller-supplied
// `scopeId` / `tokenId` of `../../jobs/foo` would let a write land at —
// or a read enumerate — a chosen `_platform/...` path, exactly the
// cross-scope sink Phase 131 closes at the team/permission seam above.
//
// The same `validate` policy is reused so the share-token seam rejects
// the identical id shapes (traversal, NUL, control chars, the reserved
// `_platform` scope, Windows reserved names) the team/permission seam
// does. `resourceKind` / `resourceId` are NOT validated here: the store
// SHA-256-hashes `{kind}|{id}` into the path (`resourceHash`)
// specifically so they may carry arbitrary characters — validating
// them would reject inputs the store was designed to accept, and they
// cannot traverse a path once hashed.
//
// Unlike the team/permission decorator (writes-only), this one also
// guards the two `List*` reads: their `scopeId` becomes a `List`
// prefix, so a traversal `scopeId` is an information-disclosure read,
// not just a missed point-lookup. A rejected read degrades to an empty
// result (read-seam defence-in-depth). `Validate(token)` delegates
// unchanged — its `scopeId` / `tokenId` are re-derived from the
// HMAC-verified signed payload, never a raw caller string, and were
// sanitised at `Issue` time.
type SanitisingShareTokenStore(inner: IShareTokenStore) =

    /// Mutating methods return `Result<_, ShareTokenError>`; a rejected
    /// id surfaces as `StorageFailed` (there is no dedicated invalid-id
    /// case, and the categorised reason never echoes the raw value).
    let guardResult
        (validation: Result<unit, string>)
        (run: unit -> Async<Result<'a, ShareTokenError>>)
        : Async<Result<'a, ShareTokenError>> =
        match validation with
        | Error e -> async { return Error(ShareTokenError.StorageFailed e) }
        | Ok() -> run ()

    /// `List*` reads return a bare `ShareTokenClaim list`; a rejected id
    /// degrades to an empty result so a traversal `scopeId` can never
    /// enumerate a chosen `_platform/share-tokens/...` prefix.
    let guardList
        (validation: Result<unit, string>)
        (run: unit -> Async<ShareTokenClaim list>)
        : Async<ShareTokenClaim list> =
        match validation with
        | Error _ -> async { return [] }
        | Ok() -> run ()

    interface IShareTokenStore with
        member _.Issue(request) =
            guardResult (validate "scopeId" request.ScopeId) (fun () -> inner.Issue request)

        member _.MarkUsed(scopeId, tokenId) =
            guardResult (both (validate "scopeId" scopeId) (validate "tokenId" tokenId)) (fun () ->
                inner.MarkUsed(scopeId, tokenId))

        member _.Revoke(scopeId, tokenId, actorUserId) =
            guardResult (both (validate "scopeId" scopeId) (validate "tokenId" tokenId)) (fun () ->
                inner.Revoke(scopeId, tokenId, actorUserId))

        member _.ListByResource(scopeId, resourceKind, resourceId) =
            guardList (validate "scopeId" scopeId) (fun () -> inner.ListByResource(scopeId, resourceKind, resourceId))

        member _.ListByIssuer(scopeId, issuerUserId) =
            guardList (validate "scopeId" scopeId) (fun () -> inner.ListByIssuer(scopeId, issuerUserId))

        // `Validate` re-derives ids from the HMAC-verified payload.
        member _.Validate(token) = inner.Validate token