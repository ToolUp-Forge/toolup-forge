// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.OidcStateMachine

open ToolUp.Platform
open ToolUp.AuthProviders.Oidc.OidcTypes
open ToolUp.AuthProviders.Oidc.OidcTokenStore
open ToolUp.AuthProviders.Oidc.AuthTracer

// ─── Auth state machine ──────────────────────────────────────────────
//
// The OIDC sign-in / refresh / classify flow walks a small graph of
// named states. Until this module landed the graph was implicit —
// `handleCallback` was a 100-line nested match spanning callback-param
// parsing, PKCE-state validation, discovery, code exchange, nonce
// binding, id_token validation, persistence, and the post-callback
// reboot, with no name attached to any intermediate node. The same
// graph showed up on the cold-start side too: `OidcShell.useEffectOnce`
// dispatches between `classifyStoredToken` outcomes and an optional
// `refreshAccessToken` rescue.
//
// `Stage` makes the graph data. Each edge `From → To` emits a
// `walk` line to the configured `AuthTracer`; the existing
// `emitOk` / `emitErr` per-event surface stays in place for finer-
// grained sub-step telemetry (`token-exchange-start` etc.). `walk`
// adds the named transition above that.
//
// The shell's `AuthState` (Checking/SignedIn/SignedOut/Failed) is the
// projection of `Stage` to a render target. The projection is
// one-to-many: e.g. every non-terminal Stage projects to `Checking`.
// `toAuthState` is the canonical projection — used by the shell when
// it adopts this module as its source of truth (deferred behind the
// unified-config / coherence-validator change shipping with the
// `0.X.0` minor bump).
//
// The graph is deliberately small. Every Stage is reachable on one
// of the two top-level entries (`Booting` for cold start,
// `InCallback` for redirect return) and every terminal state
// (`Established`, `Unauthenticated`, `Failed`) is reachable from the
// non-terminal frontier — no orphan nodes.

type Stage =
    /// Initial render before the shell decides between cold-start
    /// and callback handling.
    | Booting

    // ─── Callback flow (came back from the issuer) ──────────────
    /// The shell detected `isCallbackUrl` and entered the callback
    /// handler. Will read URL params + the PKCE stash next.
    | InCallback
    /// The token endpoint is being POSTed with the authorization
    /// code + PKCE verifier.
    | ExchangingCode
    /// Optional id_token signature + iss + aud + exp validation
    /// (when `OidcUIConfig.ValidateIdToken = Some true`). Also
    /// covers the nonce-binding check, which is unconditional when
    /// a nonce was sent on the authorize request.
    | ValidatingIdToken
    /// Persisting access + refresh tokens to local storage.
    | PersistingTokens
    /// The named edge from `PersistingTokens` — full-document
    /// navigation to the app root. Documented explicitly rather
    /// than buried as a navigation side-effect inside
    /// `handleCallback`, so the post-callback Elmish-init race is
    /// part of the visible state graph.
    | RebootingToRoot

    // ─── Cold-start flow (no callback; existing token in storage) ─
    /// `classifyStoredToken` is reading the stored access token.
    | ClassifyingStored
    /// `classifyStoredToken` returned `StaleJwt` — the shell is
    /// attempting an issuer refresh to recover.
    | Refreshing

    // ─── Terminal states ────────────────────────────────────────
    /// Signed in with a valid access token. The shell renders.
    | Established
    /// Not signed in. The shell renders the sign-in screen.
    | Unauthenticated
    /// An error occurred. The shell renders the error screen.
    | Failed of AuthError

/// Stable short label for `Stage` values, used as the `Stage`
/// component of an emitted `AuthTransition`. Pinned by tests so
/// downstream metric tags don't silently rename when the Stage DU
/// grows new cases.
let stageLabel (stage: Stage) : string =
    match stage with
    | Booting -> "booting"
    | InCallback -> "in-callback"
    | ExchangingCode -> "exchanging-code"
    | ValidatingIdToken -> "validating-id-token"
    | PersistingTokens -> "persisting-tokens"
    | RebootingToRoot -> "rebooting-to-root"
    | ClassifyingStored -> "classifying-stored"
    | Refreshing -> "refreshing"
    | Established -> "established"
    | Unauthenticated -> "unauthenticated"
    | Failed _ -> "failed"

/// Project a `Stage` to the view-model `AuthState` the shell
/// renders. Non-terminal nodes collapse to `Checking` (loading
/// spinner); terminals are 1:1 with the view states the shell has
/// today. Used by the shell when it adopts `Stage` as its source of
/// truth (deferred).
let toAuthState (stage: Stage) : AuthState =
    match stage with
    | Booting
    | InCallback
    | ExchangingCode
    | ValidatingIdToken
    | PersistingTokens
    | RebootingToRoot
    | ClassifyingStored
    | Refreshing -> Checking
    | Established -> SignedIn
    | Unauthenticated -> SignedOut
    | Failed e -> AuthState.Failed e

/// Predicate: is this Stage terminal? True for `Established`,
/// `Unauthenticated`, `Failed _`. False for every transient state.
let isTerminal (stage: Stage) : bool =
    match stage with
    | Established
    | Unauthenticated
    | Failed _ -> true
    | _ -> false

/// Emit a `From -> To` transition to the ambient `AuthTracer`. The
/// `Detail` field carries `"<from>→<to>"` so a downstream consumer
/// can recover the graph from the trace stream without correlating
/// adjacent lines. Reads the current correlation id from the token
/// store ambient slot so handleCallback doesn't have to thread it
/// through every step.
let walk (fromStage: Stage) (toStage: Stage) : unit =
    let detail = sprintf "%s→%s" (stageLabel fromStage) (stageLabel toStage)

    let outcome =
        match toStage with
        | Failed e -> Some(diagnose e)
        | _ -> None

    emit {
        CorrelationId = readCorrelationId ()
        Stage = sprintf "transition:%s" (stageLabel toStage)
        Detail = Some detail
        Outcome = outcome
    }

// ─── Pure decision functions ─────────────────────────────────────────
//
// The two ladders inside `handleCallback` that are pure F# — they
// take values the orchestration code has already extracted from the
// browser (URL query params, sessionStorage stash, id_token nonce
// claim) and decide which next Stage / `AuthError` follows. Pulled
// out as pure functions so .NET-side Expecto can exercise every
// branch without a Fable browser shim.

/// Inputs `decideCallbackStart` consumes — already-extracted by the
/// browser-side helpers (`readCallbackParams`, `readPendingSignIn`).
type CallbackInputs = {
    UrlCode: string option
    UrlState: string option
    UrlError: string option
    UrlErrorDescription: string option
    StashedVerifier: string option
    StashedState: string option
    StashedNonce: string option
}

/// Result of the initial callback validation — either an `AuthError`
/// surfaced for the UI, or the payload `handleCallback` needs to
/// proceed to the token exchange.
type CallbackStart = {
    Code: string
    Verifier: string
    ExpectedNonce: string option
}

/// Decide whether a callback URL + PKCE stash is well-formed enough
/// to proceed to the token exchange. Pure: every input is plain F#
/// `option<string>`. Encodes:
///
///   * Issuer-returned error (priority over any local check —
///     surfaces the issuer's `error` / `error_description`).
///   * Missing authorization code on the callback URL.
///   * Missing or mismatched OAuth `state` (CSRF / replay defence).
///   * Missing PKCE verifier (sessionStorage was cleared between the
///     authorize redirect and the callback — equivalent to a missing
///     state for our purposes).
let decideCallbackStart (inputs: CallbackInputs) : Result<CallbackStart, AuthError> =
    match inputs.UrlError with
    | Some err -> Error(IssuerError(err, inputs.UrlErrorDescription))
    | None ->
        match inputs.UrlCode, inputs.UrlState with
        | None, _ -> Error MissingCode
        | Some _, None -> Error InvalidState
        | Some code, Some returnedState ->
            match inputs.StashedVerifier, inputs.StashedState with
            | None, _
            | _, None -> Error InvalidState
            | Some _, Some stashedState when stashedState <> returnedState -> Error InvalidState
            | Some verifier, Some _ ->
                Ok {
                    Code = code
                    Verifier = verifier
                    ExpectedNonce = inputs.StashedNonce
                }

/// Inputs `decideNonceValidity` consumes — the stashed nonce + the
/// id_token's `nonce` claim (already extracted by the browser-side
/// `readIdTokenNonce`, or `None` when the id_token itself was
/// absent / unparseable).
type NonceInputs = {
    StashedNonce: string option
    IdTokenPresent: bool
    IdTokenNonce: string option
}

/// Decide whether the id_token's `nonce` claim matches what we
/// stashed at the authorize redirect. Pure. Three meaningful
/// outcomes encoded in the docstring of the OIDC handler:
///
///   * No stashed nonce (substrate-defensive — shouldn't happen) →
///     accept; nothing to bind to.
///   * Stashed nonce + id_token absent → reject (spec violation by
///     the issuer; we requested `openid` scope, an id_token is
///     mandatory).
///   * Stashed nonce + id_token nonce missing or mismatched →
///     reject.
///   * Stashed + matching id_token nonce → accept.
///
/// `equals` is injected so production can pass the existing
/// fixed-time string comparator (`fixedTimeStringEquals`); tests
/// pass plain `(=)`.
let decideNonceValidity (equals: string -> string -> bool) (inputs: NonceInputs) : Result<unit, AuthError> =
    match inputs.StashedNonce, inputs.IdTokenPresent with
    | None, _ -> Ok() // substrate-defensive — nothing to bind to.
    | Some _, false -> Error NonceMismatch // issuer returned no id_token despite our `openid` scope.
    | Some stashed, true ->
        match inputs.IdTokenNonce with
        | None -> Error NonceMismatch // id_token present but no `nonce` claim.
        | Some returned ->
            if equals stashed returned then
                Ok()
            else
                Error NonceMismatch

// ─── Bearer-token selection ──────────────────────────────────────────
//
// A token-endpoint response — from the authorization-code exchange or
// from a refresh — carries an `access_token` (mandatory) and, when the
// `openid` scope was requested, an `id_token`. Which of the two becomes
// this session's HTTP bearer is the deployment's declared
// `BearerTokenKind`, resolved from `OidcUIConfig.BearerToken`.
//
// Pure, so .NET-side Expecto covers every branch without a browser.

/// The token-endpoint fields the bearer decision reads. Both the
/// code-exchange and the refresh response project into this shape, so
/// one decision covers both call sites and cannot drift between them.
type BearerInputs = {
    /// The `access_token` field. Always present — the token endpoint's
    /// response is rejected upstream without it.
    AccessToken: string
    /// The `id_token` field, when the issuer returned one.
    IdToken: string option
}

/// Choose the token to store and send as the session's bearer.
///
/// `AccessTokenBearer` is total: the access token is always present by
/// the time this runs.
///
/// `IdTokenBearer` **fails** when the response carried no id_token,
/// rather than falling back to the access token. The fallback would be
/// the worse outcome in both directions: on the callback path the
/// deployment selected this strategy precisely because its access token
/// cannot be validated, so falling back stores a credential guaranteed
/// to 401; on the refresh path it would silently swap the session's
/// bearer to a different token class mid-session, which no server-side
/// validator is expecting. A typed error instead drops the session to
/// the sign-in screen — recoverable, and visible.
let decideBearerToken (kind: BearerTokenKind) (inputs: BearerInputs) : Result<string, AuthError> =
    match kind with
    | AccessTokenBearer -> Ok inputs.AccessToken
    | IdTokenBearer ->
        match inputs.IdToken with
        | Some idToken -> Ok idToken
        | None ->
            Error(
                TokenExchangeFailed
                    "bearer strategy is `id-token` but the token-endpoint response carried no `id_token`. Confirm the `openid` scope is requested at the authorize endpoint (the OIDC spec makes an id_token mandatory when it is), and that the issuer reissues one on a refresh_token grant."
            )