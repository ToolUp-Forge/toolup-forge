// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Per-route gate declaring which `SubjectKind` values may invoke
/// the route. The `SurfaceEnforcementMiddleware` (Phase 66 Stream
/// A.5) reads this from a per-process registry populated at
/// composition time and rejects subjects whose `kind` is not in
/// `AcceptedSubjects` — 401 for `AnonymousKind`, 403 for the
/// remaining mismatches (see design §3.1 response-code matrix).
///
/// Set-shaped over a closed DU so consumers can compose their own
/// admit sets (e.g. `userOrClaimBearer`) and the SDK can render the
/// set in error-message hints. The named module helpers cover the
/// common cases.
///
/// Module-level default lives on `ServerModule.DefaultSurfaceRequirement`
/// (Stream B.3); per-endpoint overrides on
/// `RouteHandler.SurfaceRequirement: SurfaceRequirement option`.
/// The global fallback for handlers without any declaration is
/// `userOrTeam` (strict / fail-closed per §3.0 OQ6 resolution).
type SurfaceRequirement = {
    /// Subject kinds admitted by this route. The middleware
    /// rejects subjects whose `Subject.kind` is not in this set.
    AcceptedSubjects: Set<SubjectKind>
}

module SurfaceRequirement =
    /// Every subject kind admitted — anonymous, users, team
    /// members, claim bearers. Use for landing pages, public
    /// health endpoints, public status boards, etc.
    let public_: SurfaceRequirement = {
        AcceptedSubjects = Set.ofList [ AnonymousKind; UserKind; TeamMemberKind; ClaimBearerKind ]
    }

    /// Any authenticated subject — rejects anonymous sessions but
    /// admits users, team members, and claim bearers (a share-token
    /// claim is treated as authenticated for the bounded scope the
    /// claim grants).
    let authenticated: SurfaceRequirement = {
        AcceptedSubjects = Set.ofList [ UserKind; TeamMemberKind; ClaimBearerKind ]
    }

    /// User or team member only — rejects anonymous AND claim
    /// bearers. The strict global fallback for routes that declare
    /// no `SurfaceRequirement` and whose module declares no
    /// `DefaultSurfaceRequirement`. Use for sign-in-lifecycle and
    /// sensitive admin actions.
    let userOrTeam: SurfaceRequirement = {
        AcceptedSubjects = Set.ofList [ UserKind; TeamMemberKind ]
    }

    /// Team-scoped only — must have an active team. Use for
    /// team-CRUD, team-data, team-config endpoints. Rejects
    /// `UserKind` with 403 + `hint = "select_team"` so the client
    /// can render an actionable "Select a team to continue" panel.
    let teamScoped: SurfaceRequirement = {
        AcceptedSubjects = Set.ofList [ TeamMemberKind ]
    }

    /// Anonymous sessions only. Use for sign-up flows that should
    /// 302 / 403 authenticated subjects away.
    let anonymousOnly: SurfaceRequirement = {
        AcceptedSubjects = Set.ofList [ AnonymousKind ]
    }

    /// Claim-bearer only. Use for share-token-gated submit
    /// endpoints (e.g. publishable Forms public-submit) where a
    /// caller landing without a valid token should be rejected.
    let claimBearerOnly: SurfaceRequirement = {
        AcceptedSubjects = Set.ofList [ ClaimBearerKind ]
    }

    /// True iff `subject`'s `kind` is in this requirement's
    /// `AcceptedSubjects` set. The single predicate the middleware
    /// consults per request after looking up the route's
    /// `SurfaceRequirement`.
    let admits (subject: Subject) (req: SurfaceRequirement) : bool =
        req.AcceptedSubjects |> Set.contains (Subject.kind subject)