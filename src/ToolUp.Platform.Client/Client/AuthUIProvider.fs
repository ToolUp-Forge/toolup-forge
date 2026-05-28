// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open Feliz

/// Registry for sign-in UI handlers supplied by companion packages.
///
/// Phase 13a — companions (OidcClient, ClerkUI, future providers)
/// export `let handler : string * AuthUIHandler` values; consumers
/// add them to `ClientConfig.Handlers.AuthUIHandlers` at compose
/// time. `SDK.Client.program` reads that list once at boot and calls
/// `setHandlers` here; `gate` then looks up the handler matching the
/// active `AuthUIMode` per render.
///
/// Handler type: see `AuthUIHandler` in `SDK.ClientTypes.fs`.
///
/// Replaces the legacy `register`-at-module-load side effect — the
/// indirection no longer requires the companion to be imported for
/// its side effect; it just exposes a value.
///
/// Thread-safety: the mutable map is written once during boot
/// (single-threaded in the browser JS runtime), and `gate` reads it
/// after initialisation completes.
module AuthUIProvider =
    /// Local alias preserved for source-level back-compat; the
    /// canonical name is `AuthUIHandler` in `SDK.ClientTypes.fs`.
    type Handler = AuthUIHandler

    // Per-tab cache, populated once by `SDK.Client.program` from
    // `config.Handlers.AuthUIHandlers`. Sole writer is the SDK boot
    // path; companions never touch it.
    let mutable private handlers: Map<string, AuthUIHandler> = Map.empty

    /// Called once by `SDK.Client.program` with the value from
    /// `ClientConfig.Handlers.AuthUIHandlers`. Idempotent re-installs
    /// are supported but in practice only happen if a test harness
    /// re-runs the boot sequence. Duplicate tags within the supplied
    /// list collapse to last-wins (the SDK's `Client.run` validator
    /// surfaces duplicates separately).
    let setHandlers (entries: (string * AuthUIHandler) list) : unit = handlers <- entries |> Map.ofList

    /// Snapshot of the cached map — used by `Client.run` validators
    /// and `ClientRequestSeam.snapshot` to surface what's wired.
    let snapshot () : (string * AuthUIHandler) list = handlers |> Map.toList

    /// Wrap `shell` with the sign-in handler that matches `authUI`.
    ///
    /// Phase 66 Stream B.8 — gates on resolved `SubjectKind` rather
    /// than the retiring `PlatformMode`. `AnonymousKind` and
    /// `ClaimBearerKind` are always pass-through — the former has
    /// nothing to sign in to, the latter is already authenticated by
    /// the share-token middleware (the claim IS the credential).
    /// `NoAuthUI` is pass-through for every signed-in kind too — the
    /// app is expected to obtain tokens through some other means
    /// (e.g. a custom external redirect) and hand them to
    /// `UserSession.setAuthToken`.
    ///
    /// `OidcAuthUI` / `ClerkAuthUI` require the matching handler to
    /// be wired into `ClientConfig.Handlers.AuthUIHandlers`; missing
    /// handlers normally fail loud at `Client.run` (validator step),
    /// but the per-render fallback path here also `failwith`s
    /// defensively in case a deployment skipped `Client.run` (e.g.
    /// custom outer program).
    ///
    /// `CustomAuthUI` invokes the caller-supplied wrapper directly,
    /// bypassing the handler registry entirely.
    ///
    /// GP 12 — this is UI shape only; server-side
    /// `SurfaceEnforcementMiddleware` is the authoritative gate.
    let gate (authUI: AuthUIMode) (subjectKind: SubjectKind) (shell: ReactElement) : ReactElement =
        match subjectKind, authUI with
        | AnonymousKind, _ -> shell
        | ClaimBearerKind, _ -> shell
        | _, NoAuthUI -> shell
        | _, CustomAuthUI cfg -> cfg.Wrap shell
        | _, OidcAuthUI cfg ->
            match Map.tryFind "oidc" handlers with
            | Some h -> h (box cfg) shell
            | None ->
                failwith
                    "ClientConfig.AuthUI = OidcAuthUI _ but no handler with tag \"oidc\" is registered \
                     in ClientConfig.Handlers.AuthUIHandlers. Add \
                     `ToolUp.AuthProviders.OidcRegister.handler` from the OidcClient companion \
                     to ClientConfig.Handlers.AuthUIHandlers, or set ClientConfig.AuthUI to \
                     NoAuthUI / a different provider."
        | _, ClerkAuthUI cfg ->
            match Map.tryFind "clerk" handlers with
            | Some h -> h (box cfg) shell
            | None ->
                failwith
                    "ClientConfig.AuthUI = ClerkAuthUI _ but no handler with tag \"clerk\" is registered \
                     in ClientConfig.Handlers.AuthUIHandlers. Add \
                     `ToolUp.AuthProviders.ClerkRegister.handler` from the ClerkUI companion \
                     to ClientConfig.Handlers.AuthUIHandlers, or set ClientConfig.AuthUI to \
                     NoAuthUI / a different provider."