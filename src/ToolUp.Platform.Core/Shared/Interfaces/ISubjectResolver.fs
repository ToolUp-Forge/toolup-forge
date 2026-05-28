// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open ToolUp.Platform.Auth

// ─── ISubjectResolver — Phase 66 Stream A.3 ──────────────────────────
//
// Per-request resolution of the `Subject` for a single deployment.
// Replaces the per-mode `IStorageScopeResolver` dispatch with a single
// stateless resolver that walks the four-step algorithm (design §3.1)
// and returns the resolved subject:
//
//   1. ShareTokenClaim present → `ClaimBearer claim`
//      (claim presence dominates regardless of any other auth state).
//   2. Authenticated user (non-anonymous):
//      a. Deployment supports Team AND user has an active team
//         → `TeamMember (userId, activeTeamId)`
//      b. Else if deployment supports AuthenticatedUser
//         → `AuthenticatedUser userId`
//      c. Else → `Error (UnsupportedSubject UserKind)`.
//   3. Else (anonymous session):
//      a. If deployment supports Anonymous
//         → `AnonymousSession sessionId`
//      b. Else → `Error (UnsupportedSubject AnonymousKind)`.
//
// The resolver is composition-time wired with the deployment's
// `Surfaces: SurfaceProfile list` and any substrate it needs to walk
// step 2a (`ITeamStore` for active-team lookup, `IMemoryCache` for the
// 5-min sliding-TTL cache, `INotificationChannel` for cache
// invalidation on `MembershipChanged.Removed` / `.ActiveTeamSet`).
//
// **Phase 9c portability rules (all six honoured):**
//
//   1. Identity by value. `SubjectResolutionRequest` is a record of
//      strings / records / maps; the result is the `Subject` DU
//      whose payloads are likewise value-typed. No live handles
//      (`IActorRef`, `IGrainReference`, etc.) cross the interface.
//   2. Async at every boundary. `Resolve` returns `Async<_>`. The
//      synchronous extraction of the request from `HttpContext`
//      lives on the consuming side (a server-tier helper) so the
//      interface itself stays runtime-agnostic.
//   3. Retry / supervision as data. Failures returned as
//      `SubjectResolutionError` cases — no `OnFailure` callbacks,
//      no thrown exceptions for expected error paths.
//   4. Stateless handlers between invocations. The interface does
//      not assume the implementation carries state across calls;
//      caches are an implementation choice and must be event-driven
//      (subscribing to `INotificationChannel` for invalidation,
//      not relying on per-instance in-memory truth).
//   5. No cross-shard ordering promises. Each request resolves
//      independently; there is no ordering relationship between
//      `Resolve` calls beyond the implementations' internal
//      decisions.
//   6. Precision N/A — resolution is not a timing primitive.

/// Input to `ISubjectResolver.Resolve`. Extracted from `HttpContext`
/// upstream so the resolver interface itself has no ASP.NET Core
/// coupling — implementable inside a sidecar grain or an Akka actor
/// per Guiding Principle 12.
///
/// Resolvers choose which fields they read based on the deployment's
/// `Surfaces`. `Claim` (the share-token field, populated by
/// `ShareTokenAuthMiddleware`) is checked first per the four-step
/// algorithm and short-circuits the rest of the resolution when
/// present.
type SubjectResolutionRequest = {
    /// Authenticated user, if one could be extracted from the
    /// request. `None` means no credentials were presented. `Some
    /// anonymous` means credentials were presented but failed
    /// validation (lenient `IAuthProvider.GetUser` shape). The
    /// four-step algorithm treats both `None` and `Some anonymous`
    /// as the step-3 anonymous-session branch.
    User: AuthenticatedUser option
    /// Session identifier for anonymous tracking, typically taken
    /// from the `X-User-Id` header. `None` means the request did
    /// not carry one; resolvers may generate a fresh value in that
    /// case.
    SessionId: string option
    /// Validated share-token claim, populated by
    /// `ShareTokenAuthMiddleware` (Phase 66 Stream A.4). When
    /// `Some`, the four-step algorithm returns `ClaimBearer claim`
    /// without consulting `User` / `SessionId` — claim presence
    /// dominates.
    Claim: ShareTokenClaim option
    /// Full request header set, lowercased key. Extensibility
    /// hatch for resolvers that need to read beyond the fields
    /// above — should be used sparingly.
    Headers: Map<string, string>
}

/// Reasons a subject resolution can fail. Callers translate these
/// into HTTP responses (401 / 403 / 500) at the request boundary.
/// Distinct DU from the older `ScopeResolutionError` because the
/// two coexist during the Phase 66 transition; consolidation lands
/// when the rest of Stream A completes.
///
/// `[<RequireQualifiedAccess>]` because the `NotTeamMember` and
/// `SubjectResolutionFailed` cases would otherwise collide with
/// the retiring `ScopeResolutionError`'s `NotTeamMember` /
/// `ScopeResolutionFailed` shapes when both live in
/// `namespace ToolUp.Platform`. Callers say
/// `SubjectResolutionError.NotTeamMember teamId` until the
/// retiring type is removed.
[<RequireQualifiedAccess>]
type SubjectResolutionError =
    /// Step 2c / 3b — the resolved subject kind has no matching
    /// profile in the deployment's `Surfaces`. Should be
    /// unreachable when `SurfaceCoherenceValidator` is wired (it
    /// refuses startup on the upstream config defects); surfaced
    /// here as a defence-in-depth case so the resolver itself
    /// never panics.
    | UnsupportedSubject of SubjectKind
    /// Step 2a — authenticated user, deployment supports Team
    /// scope, the active-team pointer is set, but the membership
    /// probe returns no role (the user was removed from the team
    /// between the cache write and this request). Caller
    /// translates to 403 per the design §3.1 response-code matrix.
    /// Distinct from the "no active team yet" case which falls
    /// through to step 2b (`AuthenticatedUser`) when User is in
    /// `Surfaces` or step 2c (`UnsupportedSubject UserKind`)
    /// otherwise.
    | NotTeamMember of teamId: string
    /// Infrastructure failure — storage backend unreachable, cache
    /// corruption, etc. Message is operator-visible (logs); must
    /// not leak to clients verbatim.
    | SubjectResolutionFailed of message: string

/// Per-request subject resolver. The single seam between request
/// state (auth, session, share-token claim) and the load-bearing
/// `Subject` value that downstream middleware, handlers, and
/// `StorageScope` derivation all consume.
///
/// One stateless component per deployment. The composition root
/// wires the implementation once at startup; per-request inputs
/// arrive via `SubjectResolutionRequest`.
type ISubjectResolver =
    abstract Resolve: SubjectResolutionRequest -> Async<Result<Subject, SubjectResolutionError>>