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

        // Reads delegate unchanged.
        member _.GetTeamPermissions(teamId) = inner.GetTeamPermissions(teamId)

        member _.GetEffectivePermissions(userId, teamId) =
            inner.GetEffectivePermissions(userId, teamId)