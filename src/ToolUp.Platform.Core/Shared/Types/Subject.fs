// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Who/what a request is acting AS. Resolved per-request by the
/// scope-resolution middleware (`ISubjectResolver`) from auth state
/// + the share-token middleware + deployment config. The Subject is
/// the load-bearing pivot: storage scope, persistence, permissions,
/// and audit attribution all derive from it.
///
/// A single deployment whose `Surfaces` includes multiple shapes
/// resolves a different `Subject` per request, without the per-feature
/// exemption-list workarounds the prior per-deployment-wide mode model
/// required.
///
/// Case constructors stay unqualified — every handler that
/// pattern-matches on the resolved subject reads at the natural
/// `match ctx.Subject with TeamMember (uid, tid) -> …` shape.
/// Where a name collides with `SurfaceProfile` (`AuthenticatedUser`,
/// `ClaimBearer`), the `SurfaceProfile` cases carry
/// `[<RequireQualifiedAccess>]`.
type Subject =
    /// Unauthenticated session-scoped subject. `sessionId` is the
    /// `X-User-Id` cookie value (or a freshly-generated GUID when
    /// the request did not carry one).
    | AnonymousSession of sessionId: string
    /// Authenticated user without active team scope. The
    /// "Individual" / "trial" shape — distinguished from
    /// `TeamMember` because a deployment may serve both.
    | AuthenticatedUser of userId: string
    /// Authenticated user acting within a team scope. The retiring
    /// `Team` / `MultiTeam` shapes collapse here — the difference
    /// is UX-only (carried on `TeamConfig.Switching`).
    | TeamMember of userId: string * teamId: string
    /// Anonymous reach into a persistent scope, gated by a
    /// validated `ShareTokenClaim`. The claim defines both
    /// identity (via `AttributedHandle`) and authority bounds (via
    /// `ResourceKind` / `ResourceId` / `UseLimit`). Today's
    /// `IPublicFormApi` claim-as-identity pattern (Phase 1 §1.5)
    /// promoted to a first-class subject kind.
    | ClaimBearer of claim: ShareTokenClaim

/// Lightweight kind tag for declarative use — `SurfaceRequirement`
/// admit sets, capability checks, audit attribution, log/metrics
/// tagging. One case per `Subject` constructor; pattern matches on
/// `SubjectKind` stay exhaustive across model evolution because
/// adding a new `Subject` case forces a new `SubjectKind`.
type SubjectKind =
    | AnonymousKind
    | UserKind
    | TeamMemberKind
    | ClaimBearerKind

module Subject =
    /// Project a `Subject` to its lightweight `SubjectKind` tag.
    /// The canonical way to ask "what kind of subject is this?"
    /// without inspecting the payload.
    let kind =
        function
        | AnonymousSession _ -> AnonymousKind
        | AuthenticatedUser _ -> UserKind
        | TeamMember _ -> TeamMemberKind
        | ClaimBearer _ -> ClaimBearerKind