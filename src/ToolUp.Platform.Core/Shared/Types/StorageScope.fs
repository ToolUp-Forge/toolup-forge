// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// `StorageScope` itself lives in ResolvedScope.fs since Phase 797 (it is
// compiled ahead of the disclosure gate's contract); this file keeps the
// resolver's request and error types, which need `AuthenticatedUser`.

/// Input to `IStorageScopeResolver.Resolve`. Extracted from `HttpContext`
/// upstream so the resolver interface has no ASP.NET Core coupling and can
/// be implemented outside an ASP.NET process (e.g. in a sidecar grain or
/// actor under Akka/Orleans — the portability contract).
///
/// Resolvers choose which fields they read based on the surface shape they
/// serve. Anonymous resolvers read `SessionId`; authenticated resolvers read
/// `User`; team resolvers read `User` plus make their own lookup of the
/// active team.
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
    /// Authenticated surface, but no valid credentials were presented.
    | NotAuthenticated
    /// Team surface, authenticated user has no active team selected.
    | NoActiveTeam
    /// Team surface, user is not a member of the active team.
    | NotTeamMember of teamId: string
    /// Infrastructure failure — storage backend unreachable, cache corruption,
    /// etc. Message is diagnostic only; do not leak to clients verbatim.
    | ScopeResolutionFailed of message: string