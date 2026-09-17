// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of SDK.Shared.fs: the canonical predicates over a
// deployment's `Surfaces` list. Same namespace, same names — only the file
// boundary moved.

/// Canonical predicates over the per-deployment `Surfaces` list.
/// Validators / handlers / composition-root branches consult these
/// instead of pattern-matching directly on `config.Surfaces`.
module DeploymentConfig =
    /// True iff any declared surface requires the request to carry
    /// authenticated credentials. Anonymous is the only surface
    /// shape that admits unauthenticated requests; every other
    /// shape derives an authenticated `Subject`. A mixed-mode
    /// deployment containing both an `Anonymous` profile AND any
    /// authenticated profile still admits authenticated routes, so
    /// the predicate is "any non-Anonymous surface present".
    let requiresAnyAuth (config: ServerConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.Anonymous _ -> false
            | _ -> true)

    /// True when the deployment is reachable over the public internet —
    /// either it enforces HTTPS itself (`RequireHttps`) or it trusts a
    /// TLS-terminating proxy's forwarded headers (`TrustForwardedHeaders`).
    /// The broad "is this exposed?" signal; the rate-limit and
    /// security-headers preflights use it. Named here (not recomputed at
    /// each call site) so the four startup validators that reason about
    /// internet exposure pick an *intent* rather than re-deriving a
    /// boolean that could silently drift apart — cf. the deliberately
    /// stricter `isHttpsTerminatedHere`.
    let isInternetFacing (config: ServerConfig) : bool =
        config.RequireHttps || config.TrustForwardedHeaders

    /// The stricter "HTTPS is explicitly enforced at this layer"
    /// (`RequireHttps`) signal — deliberately NOT `isInternetFacing`. The
    /// auto-bootstrap-dev-admin and max-request-body preflights use this
    /// narrower check because `TrustForwardedHeaders` defaults to `true`
    /// (Phase 16d) and would otherwise flag a plain local-dev shell as
    /// internet-facing. Centralised so the intended divergence from
    /// `isInternetFacing` is explicit, not buried in per-validator
    /// comments.
    let isHttpsTerminatedHere (config: ServerConfig) : bool = config.RequireHttps

    /// True iff the deployment supports the `Team` subject shape
    /// (single-team or multi-team UX). Used by team-store wiring +
    /// team-CRUD validators.
    let hasTeamScope (config: ServerConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.Team _ -> true
            | _ -> false)

    /// True iff the deployment carries any `AuthenticatedUser` surface
    /// — `Individual` (`Persistence = Persistent`) or
    /// `AuthenticatedEphemeral` (`Persistence = Ephemeral`). The
    /// non-team half of "this deployment serves real, identified
    /// users", deliberately excluding `Team` so callers can pick the
    /// two apart; `isProductionShapedForStatefulEmbedder` is the union.
    let hasAuthenticatedUserScope (config: ServerConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.AuthenticatedUser _ -> true
            | _ -> false)

    /// Phase 9m.B — true iff the deployment is one of the four shapes a
    /// process-stateful embedder (the dev-only `LocalEmbeddingProvider`)
    /// should not be serving: `Individual`, `AuthenticatedEphemeral`,
    /// `Team`, `MultiTeam`. `Anonymous`-only and `ClaimBearer`-only
    /// deployments are excluded — a public demo or a share-token surface
    /// has no per-user corpus whose retrieval quality could drift, so
    /// flagging them would be noise.
    ///
    /// Named here rather than re-derived per call site because three
    /// consumers must agree on it exactly:
    /// `LocalEmbeddingProviderInProductionModeValidator` (non-team half),
    /// `TeamModeLocalEmbedderValidator` (team half), and the companion's
    /// `LocalEmbeddingProviderHealth` probe (the union) — a split-brain
    /// between the preflight warning and the health probe is precisely
    /// the confusion the probe exists to remove.
    let isProductionShapedForStatefulEmbedder (config: ServerConfig) : bool =
        hasAuthenticatedUserScope config
        || config.Surfaces
           |> List.exists (function
               | SurfaceProfile.Team _ -> true
               | _ -> false)

    /// True iff the deployment supports multi-team switching — any
    /// `Team` surface whose `Switching = HeaderSwitcher`. Used by
    /// `NotificationMode.resolve`: membership-change events feed the
    /// client team-switch reset path, which only exists when the
    /// switcher is present.
    let hasMultiTeamSwitcher (config: ServerConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.Team { Switching = HeaderSwitcher } -> true
            | _ -> false)

    /// True iff the deployment supports the `ClaimBearer` subject
    /// shape. Used by `IShareTokenStore` auto-promotion + decorator
    /// coherence checks.
    let hasClaimBearer (config: ServerConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.ClaimBearer _ -> true
            | _ -> false)

    /// True iff the deployment supports the `Anonymous` subject
    /// shape. The complement of "auth-required everywhere".
    let hasAnonymous (config: ServerConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.Anonymous _ -> true
            | _ -> false)

    /// True iff at least one surface in the deployment carries
    /// persistent authenticated storage — any `AuthenticatedUser`
    /// with `Persistence = Persistent` (the canonical Individual
    /// shape) or any `Team` profile (`Team` scope is persistent by
    /// design). The complement is "deployment is ephemeral / public-
    /// only by design". Used by validators that escalate severity
    /// when persistent data would be at stake under the
    /// configuration being checked (publishable-form unsigned-token
    /// gap, RAG vector-store durability, etc.).
    let hasPersistentAuthenticatedStorage (config: ServerConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.AuthenticatedUser { Persistence = Persistent } -> true
            | SurfaceProfile.Team _ -> true
            | _ -> false)

    /// One-line label for diagnostic / error messages naming the
    /// deployment shape. Single-surface deployments produce a single
    /// name (`"Individual"` / `"Team"` / `"MultiTeam"` /
    /// `"AuthenticatedEphemeral"` / `"Anonymous"`); mixed-mode
    /// deployments produce a `+`-joined list (e.g.
    /// `"Anonymous + Individual"`).
    let surfacesLabel (config: ServerConfig) : string =
        let labelOne =
            function
            | SurfaceProfile.Anonymous _ -> "Anonymous"
            | SurfaceProfile.AuthenticatedUser { Persistence = Persistent } -> "Individual"
            | SurfaceProfile.AuthenticatedUser { Persistence = Ephemeral } -> "AuthenticatedEphemeral"
            | SurfaceProfile.Team { Switching = NoSwitcher } -> "Team"
            | SurfaceProfile.Team { Switching = HeaderSwitcher } -> "MultiTeam"
            | SurfaceProfile.ClaimBearer _ -> "ClaimBearer"

        config.Surfaces |> List.map labelOne |> String.concat " + "