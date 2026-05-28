// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Whether a storage scope persists beyond the request lifetime.
/// Drives `StorageScope.Persist` per the resolved Subject's profile —
/// the per-deployment lever that today's `PlatformMode` collapses
/// into the mode value is here lifted to a per-shape decision.
type Persistence =
    | Ephemeral
    | Persistent

/// Per-deployment UX intent for the `Team` shape. Server-side data
/// model is identical across the two cases (each scope is a
/// `team-{teamId}` container; the store always permits multi-
/// membership) — the distinction is whether the client shell renders
/// the header team-switcher and runs the `TeamSwitched` reset path.
type TeamSwitchingUX =
    /// One team per user; no header switcher in the client shell.
    /// Equivalent to the retiring `PlatformMode.Team` UX intent.
    | NoSwitcher
    /// Users belong to many teams and switch in-session; the shell
    /// renders the header dropdown and runs `TeamSwitched` reset.
    /// Equivalent to the retiring `PlatformMode.MultiTeam` UX intent.
    | HeaderSwitcher

/// Per-shape config for the `Anonymous` surface. `Persistence`
/// defaults `Ephemeral`; the `Persistent` variant closes the door on
/// the workaround pattern for long-lived demo surfaces whose
/// session-keyed data survives server restarts. `SessionEvictionMinutes` applies
/// only when `Persistence = Ephemeral`.
type AnonymousConfig = {
    Persistence: Persistence
    /// Idle timeout for the in-memory anonymous-session store.
    /// `None` = never evict (the long-lived demo case when
    /// `Persistence = Persistent`); `Some n` = evict after `n`
    /// minutes of idleness. Default `Some 60`.
    SessionEvictionMinutes: int option
}

/// Per-shape config for the `AuthenticatedUser` surface.
/// `Persistence = Persistent` is the canonical Individual shape;
/// `Persistence = Ephemeral` is the trial / try-before-you-buy
/// shape that survives the auth flow but discards data on idle.
type AuthenticatedUserConfig = {
    Persistence: Persistence
    /// Idle timeout for the in-memory user-session store. Applies
    /// only when `Persistence = Ephemeral`. Default `Some 60`.
    SessionEvictionMinutes: int option
}

/// Per-shape config for the `Team` surface. `Persistence` is
/// almost always `Persistent`; `Ephemeral` exists for trial-team
/// scenarios. `Switching` carries the client-UX intent.
type TeamConfig = {
    Persistence: Persistence
    Switching: TeamSwitchingUX
}

/// Per-shape config for the `ClaimBearer` surface. Backend choice
/// (the concrete `IShareTokenStore` impl) stays on
/// `ServerConfig.ShareTokenStore` — §3.0 OQ4 resolution. This
/// record carries only the policy fields that influence
/// share-token issuance defaults.
type ClaimBearerConfig = {
    /// Default lifetime when an issuer passes `ExpiresAt = None`.
    /// Today's `ShareTokenTypes.DefaultLifetime` is 30 days; the
    /// `ClaimBearerConfig` value overrides it per-deployment.
    DefaultLifetimeDays: int
    /// Default `UseLimit` when an issuer passes `UseLimit = None`.
    /// `Some n` = at most `n` uses; `None` = unlimited. Today's
    /// default is `Some 1` (surveys want one-and-done semantics).
    DefaultUseLimit: int option
}

/// One supported subject shape in the deployment's surface list.
/// `ServerConfig.Surfaces: SurfaceProfile list` is a non-empty list
/// of these — single-shape deployments carry one entry, mixed-mode
/// two or more. The `SurfaceCoherenceValidator` (Phase 66 Stream
/// B.2) refuses startup on duplicate constructors or an empty list.
///
/// `[<RequireQualifiedAccess>]` because the case names collide
/// with the `Subject` DU (`AuthenticatedUser` / `ClaimBearer`) and
/// with the retiring `PlatformMode` DU (`Anonymous` / `Team`).
/// Callers write `SurfaceProfile.Anonymous c` to disambiguate.
[<RequireQualifiedAccess>]
type SurfaceProfile =
    | Anonymous of AnonymousConfig
    | AuthenticatedUser of AuthenticatedUserConfig
    | Team of TeamConfig
    | ClaimBearer of ClaimBearerConfig

/// Named convenience constructors mapping the old `PlatformMode`
/// values to the new `SurfaceProfile` shape, plus a few additional
/// variants surfaced by the new model (`anonymousPersistent`,
/// `claimBearer`).
module SurfaceProfile =
    /// Ephemeral anonymous sessions, 60-minute idle eviction.
    /// Equivalent to the retiring `PlatformMode.Anonymous`.
    let anonymous =
        SurfaceProfile.Anonymous {
            Persistence = Ephemeral
            SessionEvictionMinutes = Some 60
        }

    /// Persistent anonymous sessions — session-keyed data survives
    /// server restarts; closes the long-lived-demo workaround pattern.
    let anonymousPersistent =
        SurfaceProfile.Anonymous {
            Persistence = Persistent
            SessionEvictionMinutes = None
        }

    /// Authenticated, ephemeral storage. Equivalent to the
    /// retiring `PlatformMode.AuthenticatedEphemeral`.
    let trial =
        SurfaceProfile.AuthenticatedUser {
            Persistence = Ephemeral
            SessionEvictionMinutes = Some 60
        }

    /// Authenticated, persistent per-user storage. Equivalent to
    /// the retiring `PlatformMode.Individual`.
    let individual =
        SurfaceProfile.AuthenticatedUser {
            Persistence = Persistent
            SessionEvictionMinutes = None
        }

    /// Team scope, single team per user (no header switcher).
    /// Equivalent to the retiring `PlatformMode.Team`.
    let team =
        SurfaceProfile.Team {
            Persistence = Persistent
            Switching = NoSwitcher
        }

    /// Team scope, header switcher active. Equivalent to the
    /// retiring `PlatformMode.MultiTeam`.
    let multiTeam =
        SurfaceProfile.Team {
            Persistence = Persistent
            Switching = HeaderSwitcher
        }

    /// Share-token claim bearer with default lifetime (30 days)
    /// and use limit (Some 1). Single-use survey / share-link
    /// semantics out of the box.
    let claimBearer =
        SurfaceProfile.ClaimBearer {
            DefaultLifetimeDays = 30
            DefaultUseLimit = Some 1
        }

    /// Phase 66 Stream A.2 — transitional bridge that maps a retiring
    /// `PlatformMode` to the matching single-shape `SurfaceProfile`.
    /// Used by tests + handlers / validators authored against the
    /// per-mode shape until Stream B.5 rewrites them. Retires alongside
    /// `PlatformMode`.
    let fromLegacyMode (mode: PlatformMode) : SurfaceProfile =
        match mode with
        | Anonymous -> anonymous
        | AuthenticatedEphemeral -> trial
        | Individual -> individual
        | Team -> team
        | MultiTeam -> multiTeam

/// Pre-named one-line `Surfaces` lists. Single-shape deployments
/// use the single-named helper (`Surfaces.individual`); common
/// mixed-mode pairings get their own named helper.
module Surfaces =
    /// Single-shape — anonymous only (e.g. a public portal).
    let anonymous = [ SurfaceProfile.anonymous ]
    /// Single-shape — trial.
    let trial = [ SurfaceProfile.trial ]
    /// Single-shape — individual (e.g. a per-user internal tool).
    let individual = [ SurfaceProfile.individual ]
    /// Single-shape — team (one team per user).
    let team = [ SurfaceProfile.team ]
    /// Single-shape — multi-team (header switcher).
    let multiTeam = [ SurfaceProfile.multiTeam ]

    /// Common mixed-mode — public landing + per-user persistence
    /// (e.g. a public calculator plus a small private admin surface).
    let anonymousAndIndividual = [ SurfaceProfile.anonymous; SurfaceProfile.individual ]

    /// Common mixed-mode — public landing + team dashboards.
    let anonymousAndTeam = [ SurfaceProfile.anonymous; SurfaceProfile.team ]

    /// Common mixed-mode — team scope plus share-token public
    /// embed (e.g. publishable Forms over a team account).
    let teamWithShareTokens = [ SurfaceProfile.team; SurfaceProfile.claimBearer ]

    /// Phase 66 Stream A.2 — transitional one-shape list derived from
    /// a retiring `PlatformMode`. Mirrors `SurfaceProfile.fromLegacyMode`.
    /// Used by `ServerConfig.fromEnv` until the env-var contract retires
    /// `TOOLUP_PLATFORM_MODE` parsing alongside the type.
    let fromLegacyMode (mode: PlatformMode) : SurfaceProfile list = [ SurfaceProfile.fromLegacyMode mode ]