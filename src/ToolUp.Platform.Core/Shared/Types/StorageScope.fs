// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// How the platform scopes and persists data.
///
/// `Team` and `MultiTeam` share an identical server-side data model
/// (each scope is a `team-{teamId}` blob container; users may belong
/// to many teams in the underlying store). They differ in **deployment
/// intent and client UX**: `Team` assumes one team per user (no
/// header switcher injected, switching not surfaced as a normal user
/// action), while `MultiTeam` activates the header team-switcher and
/// the shell-level state-reset path (`TeamSwitched`) so a single
/// authenticated user can switch between teams in-session and have
/// the entire UI (files, configs, flags, RBAC, AI conversation) swap
/// to the new team's data without re-auth.
type PlatformMode =
    | Anonymous // No auth, no persistence
    | AuthenticatedEphemeral // Auth required, no persistence
    | Individual // Auth required, persistent per-user
    | Team // Auth required, persistent per-team — single-team-per-user UX
    | MultiTeam // Auth required, persistent per-team — switching is a normal user action

module PlatformMode =
    /// `true` for every mode except `Anonymous` — used to gate the
    /// `AuthEnforcementMiddleware` and any client UI that needs an
    /// authenticated user before rendering.
    let requiresAuth =
        function
        | Anonymous -> false
        | AuthenticatedEphemeral
        | Individual
        | Team
        | MultiTeam -> true

    /// `true` for modes whose `StorageScope.Container` is keyed by
    /// `team-{teamId}` — `Team` and `MultiTeam`. Used by the shell
    /// to decide whether to inject TeamManagerUI / TeamConfigUI and
    /// whether the server registers `TeamScopeResolver` + `ITeamStore`.
    let isTeamScoped =
        function
        | Team
        | MultiTeam -> true
        | Anonymous
        | AuthenticatedEphemeral
        | Individual -> false

/// Resolved storage scope for a request — determines where data is stored
/// and whether it persists beyond the session.
type StorageScope = {
    ScopeId: string // userId, teamId, or sessionId
    Container: string // "user-abc", "team-xyz", "session-abc"
    Persist: bool // false for Anonymous and AuthenticatedEphemeral
}

/// Input to `IStorageScopeResolver.Resolve`. Extracted from `HttpContext`
/// upstream so the resolver interface has no ASP.NET Core coupling and can
/// be implemented outside an ASP.NET process (e.g. in a sidecar grain or
/// actor under Akka/Orleans — the portability contract).
///
/// Resolvers choose which fields they read based on the platform mode they
/// serve. `Anonymous` reads `SessionId`; authenticated modes read `User`;
/// `Team` reads `User` plus makes its own lookup of the active team.
type ScopeResolutionRequest = {
    /// Authenticated user, if one could be extracted from the request.
    /// `None` means no credentials were presented. `Some anonymous` means
    /// credentials were presented but failed validation (for `GetUser`-style
    /// lenient resolution). Authenticated-mode resolvers treat both as
    /// `NotAuthenticated`.
    User: ToolUp.Platform.Auth.AuthenticatedUser option
    /// Session identifier for anonymous tracking, typically taken from the
    /// `X-User-Id` header. `None` means the request did not carry one;
    /// resolvers may generate a fresh value in that case.
    SessionId: string option
    /// Full request header set, lowercased key. Extensibility hatch for
    /// resolvers that need to read beyond the fields above — should be
    /// used sparingly.
    Headers: Map<string, string>
}

/// Reasons a scope resolution can fail. Callers translate these into HTTP
/// responses (401/403/500) at the request boundary.
type ScopeResolutionError =
    /// Authenticated mode, but no valid credentials were presented.
    | NotAuthenticated
    /// Team mode, authenticated user has no active team selected.
    | NoActiveTeam
    /// Team mode, user is not a member of the active team.
    | NotTeamMember of teamId: string
    /// Infrastructure failure — storage backend unreachable, cache corruption,
    /// etc. Message is diagnostic only; do not leak to clients verbatim.
    | ScopeResolutionFailed of message: string