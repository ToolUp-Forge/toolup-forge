// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module MixedMode.Server

open ToolUp.Platform
open ToolUp.Platform.Server

// ─── Phase 66 Stream F.5 — mixed-mode worked example ─────────────
//
// A single deployment serving three concurrent subject shapes:
//
//   - Anonymous visitors reach the landing surface (public_).
//   - Authenticated TeamMembers reach the team dashboard (teamScoped).
//   - Share-token bearers reach the share portal (claimBearerOnly).
//
// Substrate auto-wiring (per design §3.4 + Stream A.7):
//   - `Team _` in Surfaces  → ITeamStore registered (default
//     BlobTeamStore).
//   - `ClaimBearer _` in Surfaces → IShareTokenStore auto-promotes
//     to BlobShareTokenStore (since ServerConfig.ShareTokenStore
//     stays at its NoShareTokenStore default).
//   - `Anonymous _` in Surfaces → no extra substrate.
//
// SurfaceCoherenceValidator (Stream B.2) verifies each module's
// DefaultSurfaceRequirement is reachable by at least one declared
// SurfaceProfile.

let private config = {
    ServerConfig.defaults with
        Surfaces = [ SurfaceProfile.anonymous; SurfaceProfile.team; SurfaceProfile.claimBearer ]
}

// ─── Module 1 — Landing (public, anonymous-accessible) ───────────

let private landingModule =
    ServerModule.create "Landing"
    |> ServerModule.withDefaultSurfaceRequirement SurfaceRequirement.public_
    |> ServerModule.withRoutePrefix "/api/landing/"

// ─── Module 2 — TeamDashboard (team-scoped only) ─────────────────

let private teamDashboardModule =
    ServerModule.create "TeamDashboard"
    |> ServerModule.withDefaultSurfaceRequirement SurfaceRequirement.teamScoped
    |> ServerModule.withRoutePrefix "/api/team-dashboard/"

// ─── Module 3 — SharePortal (share-token bearers only) ───────────

let private sharePortalModule =
    ServerModule.create "SharePortal"
    |> ServerModule.withDefaultSurfaceRequirement SurfaceRequirement.claimBearerOnly
    |> ServerModule.withRoutePrefix "/api/share-portal/"

// ─── Composition root ────────────────────────────────────────────
//
// `ServerApp.empty |> withConfig |> withLogger |> addModule × 3
// |> run` is the canonical mixed-mode shape. `withAuth` is required
// here because the Surfaces list contains shapes other than
// Anonymous (per Stream A.7's substrate auto-wiring contract); a
// real deployment chains `ServerApp.withAuth (AuthProvider.fromEnv
// logger OidcAuthProvider.fromConfig)` after `withLogger`. This
// sample stays auth-provider-agnostic so the build verifies without
// requiring environment variables — the composition-root pattern is
// the demonstration, not a live boot.

[<EntryPoint>]
let main _ =
    let logger = ConsoleLogger.fromEnv ()

    ServerApp.empty
    |> ServerApp.withConfig config
    |> ServerApp.withLogger logger
    |> ServerApp.addModule landingModule
    |> ServerApp.addModule teamDashboardModule
    |> ServerApp.addModule sharePortalModule
    |> ServerApp.run