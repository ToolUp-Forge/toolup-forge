// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module UserSession

open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Remoting.Client
open ToolUp.Platform

// ─── Storage keys ───────────────────────────────────────────────────

/// User ID key in browser storage
let private storageKey = "toolup-user-id"

/// Auth token key in browser storage
let private tokenKey = "toolup-auth-token"

/// Token-derived user-id key. Set when `setAuthToken` decodes a JWT and
/// extracts the `sub` claim; cleared on `clearAuthToken`. Read by
/// `getUserId` so the same canonical id flows through the
/// `Authorization: Bearer <jwt>` POST path AND the EventSource
/// `?userId=` query-param path. Without this, the auth provider
/// resolves one userId on POSTs (from the JWT sub) while SSE
/// connections register under a different localStorage userId, and
/// every server-side broadcast misses every connection.
let private tokenUserIdKey = "toolup-token-user-id"

/// Cookie name used by the SSE-handshake auth path. The server-side
/// auth provider reads this cookie when configured with
/// `TokenLocation.Cookie "toolup-auth-token"`. Matches `tokenKey` so
/// localStorage-driven flows and cookie-driven flows agree on the
/// key name.
let private authCookieName = "toolup-auth-token"

// ─── Subject kind ──────────────────────────────────────────────────

/// Phase 66 Stream B.8 — current resolved `SubjectKind` for storage
/// selection + identity-header assembly. The retiring `PlatformMode`
/// shape (`Anonymous` / `Individual` / `Team` / `MultiTeam` / …)
/// collapses into the four `SubjectKind` cases (`AnonymousKind` /
/// `UserKind` / `TeamMemberKind` / `ClaimBearerKind`) — single
/// per-deployment dimension; runtime subject upgrade is the server's
/// responsibility, the client mirrors the latest known kind.
///
/// Set by `SDK.Client.run` before any API call; defaults to
/// `AnonymousKind` until `configure` runs.
let mutable private currentSubjectKind = AnonymousKind

/// Configure the resolved subject kind. Called once during client
/// initialisation; updates the cached value the storage / header
/// helpers branch on. Idempotent — subsequent calls overwrite.
let configure (kind: SubjectKind) = currentSubjectKind <- kind

/// Read the configured subject kind. Returns `AnonymousKind` until
/// `configure` runs.
let getSubjectKind () = currentSubjectKind

/// Phase 4b dev convenience — when set, `getUserId` seeds an empty
/// `toolup-user-id` localStorage entry with this value instead of an
/// auto-generated GUID. Set by `SDK.Client.run` from
/// `ClientConfig.DevDefaultUserId`. Stays `None` in production.
let mutable private devDefaultUserId: string option = None

/// Configure the dev-default user-id. Called once during client
/// initialisation by `SDK.Client.run`. `None` (default) preserves the
/// auto-generated-GUID behaviour; `Some` overrides only the first-visit
/// generation path (existing localStorage values are preserved either
/// way).
let configureDevDefault (id: string option) = devDefaultUserId <- id

// ─── JWT decode (sub claim only — server validates signature) ──────

[<Emit("atob($0)")>]
let private atob (s: string) : string = jsNative

[<Emit("JSON.parse($0)")>]
let private jsonParse (s: string) : obj = jsNative

let private base64UrlToB64 (s: string) =
    // JWT uses URL-safe base64 without padding; convert to standard base64.
    let replaced = s.Replace('-', '+').Replace('_', '/')

    match replaced.Length % 4 with
    | 0 -> replaced
    | 2 -> replaced + "=="
    | 3 -> replaced + "="
    | _ -> replaced

let private decodeJwtSub (token: string) : string option =
    try
        let parts = token.Split('.')

        if parts.Length < 2 then
            None
        else
            let payloadJson = atob (base64UrlToB64 parts[1])
            let payload = jsonParse payloadJson

            match payload?sub with
            | null -> None
            | sub ->
                let s = string sub
                if System.String.IsNullOrEmpty s then None else Some s
    with _ ->
        None

// ─── Cookie helpers (Phase 6k Workstream A) ────────────────────────

/// Phase 6k Workstream A. When an auth token is available, mirror it
/// into a cookie so EventSource handshakes carry it automatically
/// (EventSource cannot send custom headers — only cookies travel).
/// Cookies are scoped to `Path=/`, `SameSite=Strict`, `Secure` (when
/// the page is on https — browsers reject `Secure` on plain http,
/// so dev over `http://localhost` falls back to no `Secure`).
///
/// Not `HttpOnly` — that flag can only be set server-side via
/// `Set-Cookie`, and the JWT is JS-readable from localStorage anyway.
/// The cookie is functionally equivalent to localStorage for security
/// purposes; its only role is making EventSource auth work.
let private setAuthCookie (token: string) =
    let isHttps =
        try
            Browser.Dom.window.location.protocol = "https:"
        with _ ->
            false

    let secureFlag = if isHttps then "; Secure" else ""

    Browser.Dom.document.cookie <- $"{authCookieName}={token}; Path=/; SameSite=Strict{secureFlag}"

/// Phase 6k Workstream A. Clear the auth cookie on sign-out by setting
/// `Max-Age=0`. Browsers honour this regardless of whether the original
/// cookie had `Secure` set, so a dev-mode http session can clear a
/// previously-set cookie.
let private clearAuthCookie () =
    Browser.Dom.document.cookie <- $"{authCookieName}=; Path=/; Max-Age=0; SameSite=Strict"

// ─── User ID ───────────────────────────────────────────────────────

/// Get or create a user/session ID.
///
/// Always reads from localStorage so the SSE EventSource `?userId=`
/// query-param path and the Fable.Remoting `X-User-Id` POST path
/// resolve the same value regardless of when each is called or what
/// `currentSubjectKind` was at the time. The pre-fix design forked
/// storage on the mode (sessionStorage for Anonymous, localStorage
/// for authenticated), which produced two parallel id pools any time
/// `configure` ran between two `getUserId` calls — and the Cmd-driven
/// SSE subscribe vs the eager POST proxy made that race trivial to
/// hit. Result: the SSE channel registered under one id while the
/// agent loop broadcast under another, every event was dropped, and
/// the user saw a 60-second watchdog.
///
/// Authenticated subject kinds (`UserKind` / `TeamMemberKind` /
/// `ClaimBearerKind`) prefer the token-derived `sub` claim (set by
/// `setAuthToken` when a JWT is stored) so the SDK's id matches the
/// server-side auth-resolved id.
///
/// `AnonymousKind` loses the per-tab-uniqueness sessionStorage gave
/// — two tabs of an Anonymous deployment now share an id. That's
/// fine: scope isolation still works, and per-tab differentiation
/// can be reintroduced as an explicit feature without coupling it to
/// the SSE / POST agreement.
let getUserId () =
    let storage = Browser.Dom.window.localStorage

    let isAuth =
        match currentSubjectKind with
        | AnonymousKind -> false
        | UserKind
        | TeamMemberKind
        | ClaimBearerKind -> true

    let tokenSub =
        if isAuth then
            match storage.getItem tokenUserIdKey with
            | null
            | "" -> None
            | s -> Some s
        else
            None

    match tokenSub with
    | Some s -> s
    | None ->
        match storage.getItem storageKey with
        | null
        | "" ->
            // Phase 4b — dev composition roots can opt in to a stable
            // seed via `ClientConfig.DevDefaultUserId` so the local-run
            // `X-User-Id` matches `ServerConfig.AutoBootstrapDevAdmin`
            // end-to-end. Production leaves it `None` and gets a fresh
            // GUID per first-visit (existing localStorage values are
            // always preserved regardless of this setting).
            let newId =
                match devDefaultUserId with
                | Some id when not (System.String.IsNullOrWhiteSpace id) -> id
                | _ -> System.Guid.NewGuid().ToString()

            storage.setItem (storageKey, newId)
            newId
        | id -> id

// ─── Auth token storage ────────────────────────────────────────────

/// Store an auth token (called by auth provider UI after sign-in or
/// by the bridge refresh loop). Also extracts the JWT `sub` claim and
/// persists it as the canonical userId — `getUserId` returns it on
/// subsequent calls so SSE `?userId=` matches the server-side
/// auth-resolved userId.
///
/// Phase 6k Workstream A: also writes the token to `document.cookie`
/// so EventSource handshakes can authenticate when
/// `ServerConfig.SseAuthMode = CookieRequired`. The cookie path is
/// silent overhead in `QueryParamFallback` deployments — the server
/// just doesn't read it.
let setAuthToken (token: string) =
    Browser.Dom.window.localStorage.setItem (tokenKey, token)
    setAuthCookie token

    match decodeJwtSub token with
    | Some sub -> Browser.Dom.window.localStorage.setItem (tokenUserIdKey, sub)
    | None ->
        // Opaque or malformed token — leave any previously-stored value
        // in place. `getUserId` falls back to the localStorage Guid if
        // tokenUserIdKey is missing.
        ()

/// Clear the auth token (called on sign-out). Also clears the
/// token-derived userId so the next sign-in resolves a fresh subject,
/// AND the SSE auth cookie so a stale cookie doesn't outlive the
/// session.
let clearAuthToken () =
    Browser.Dom.window.localStorage.removeItem tokenKey
    Browser.Dom.window.localStorage.removeItem tokenUserIdKey
    clearAuthCookie ()

/// Get the current auth token, if any.
let getAuthToken () =
    match Browser.Dom.window.localStorage.getItem tokenKey with
    | null
    | "" -> None
    | token -> Some token

// ─── Auth bridge (Phase 6k Workstream A) ───────────────────────────

/// Phase 6k Workstream A. Optional bridge to a deployment-chosen
/// identity SDK (Clerk, Microsoft Entra via MSAL, Auth0, WorkOS).
/// When installed, the bridge's `GetJwt` is polled periodically and
/// the resulting JWT is mirrored through `setAuthToken` — so the
/// existing synchronous `withRequestHeaders` path continues to work,
/// and the JWT cookie + localStorage stay current as the provider
/// SDK refreshes the token silently in the background.
///
/// `setAuthToken` continues to work without a bridge — the bridge is
/// the *preferred* path for production but deployments without one can
/// drive `setAuthToken` directly from their auth UI.
let mutable private currentBridge: IAuthBridge option = None

/// Refresh interval for the installed bridge. JWT lifetimes are
/// typically 1 hour; checking every 60 s catches expiry without
/// burning cycles. The bridge's own SDK cache absorbs most calls.
let private bridgeRefreshIntervalMs = 60_000

let private refreshFromBridgeOnce () = async {
    match currentBridge with
    | None -> return ()
    | Some bridge ->
        try
            let! jwt = bridge.GetJwt()

            match jwt with
            | Some token ->
                // Avoid a needless cookie write if the bridge returned
                // the same value we already have. JWT strings are
                // short — the equality check is cheap.
                let current = Browser.Dom.window.localStorage.getItem tokenKey

                if current <> token then
                    setAuthToken token
            | None ->
                // Bridge says signed out — clear local state if we
                // were holding any. Idempotent — no-op when already
                // signed out.
                if (Browser.Dom.window.localStorage.getItem tokenKey) <> null then
                    clearAuthToken ()
        with _ ->
            // Bridge errors are non-fatal — leave the cached token
            // in place. The deployment's auth UI is responsible for
            // surfacing visible failures via its own UI affordances.
            ()
}

[<Emit("setInterval($0, $1)")>]
let private setInterval (cb: unit -> unit) (ms: int) : int = jsNative

/// Install a deployment-specific auth bridge. Called once during
/// `SDK.Client.run` if `ClientConfig.AuthBridge` is `Some`. Kicks off
/// an immediate JWT fetch + a periodic refresh loop. Idempotent — a
/// re-install replaces the previous bridge but does not stop the
/// existing refresh interval (in practice, deployments install once).
let installBridge (bridge: IAuthBridge) =
    currentBridge <- Some bridge

    // Immediate fetch so the cookie + localStorage are populated
    // before the first request flies. Subsequent refreshes happen on
    // the interval below.
    Async.StartImmediate(refreshFromBridgeOnce ())

    setInterval (fun () -> Async.StartImmediate(refreshFromBridgeOnce ())) bridgeRefreshIntervalMs
    |> ignore

/// Read the installed bridge, if any. Used by trace logging in
/// `withRequestHeaders` and the dev panel's auth surface.
let getBridge () = currentBridge

/// Force an immediate JWT refresh from the installed bridge. Useful
/// for deployments that know a sign-in/sign-out just happened (e.g.
/// after a Clerk sign-in callback) and want the SSE cookie updated
/// without waiting for the periodic interval.
let refreshFromBridge () = refreshFromBridgeOnce ()

// ─── Auth-token transition observer (Phase 3d.A) ───────────────────

[<Emit("clearInterval($0)")>]
let private clearInterval (handle: int) : unit = jsNative

/// Phase 3d.A — observe `getAuthToken ()` transitions. Returns a
/// dispose callback the caller invokes to unsubscribe. Internally
/// runs a `setInterval` poll at `transitionPollMs` (2 s — short
/// enough that a sign-in feels immediate but long enough not to
/// burn cycles) and fires `callback` once per transition between
/// `None` and `Some _` (or vice-versa). No-change polls do not
/// fire.
///
/// Mirrors the bridge's polling pattern: the bridge already polls
/// every `bridgeRefreshIntervalMs` to push token updates into
/// localStorage; this observer polls the *result* of that to surface
/// the transition to interested components. The cheapest possible
/// signal — no new event-emitter substrate, no auth-provider
/// callback contract to maintain across the OIDC / Clerk / Entra
/// companions.
///
/// Use case: `InviteAccept` watches for an unauthenticated → signed-in
/// transition so it can drain a stashed invitation token and re-fire
/// `AcceptInvite` without the user re-opening the invite link.
let private transitionPollMs = 2_000

let onAuthTokenChange (callback: string option -> unit) : unit -> unit =
    let mutable previous = getAuthToken ()

    let tick () =
        let current = getAuthToken ()

        if current <> previous then
            previous <- current

            try
                callback current
            with _ ->
                // Caller-supplied callback errors are non-fatal — the
                // observer continues firing on subsequent transitions.
                ()

    let handle = setInterval tick transitionPollMs
    fun () -> clearInterval handle

// ─── Request headers ───────────────────────────────────────────────

/// Custom header name for user identification
let userIdHeader = "X-User-Id"

/// Identity header pairs for the current subject kind.
/// `AnonymousKind` → `X-User-Id` (session id);
/// `UserKind` / `TeamMemberKind` / `ClaimBearerKind` →
/// `Authorization: Bearer <jwt>` with an `X-User-Id` fallback until
/// the auth provider UI / share-token middleware supplies a token.
/// NO CSRF — the request-guard adds that. The guard calls this fresh
/// on every request, so the auth header tracks the bridge's periodic
/// JWT refresh instead of freezing at proxy-build time (the bug that
/// `Remoting.withCustomHeader` caused for module-level proxies).
let identityHeaderPairs () : (string * string)[] =
    match currentSubjectKind with
    | AnonymousKind -> [| userIdHeader, getUserId () |]
    | UserKind
    | TeamMemberKind
    | ClaimBearerKind ->
        match getAuthToken () with
        | Some token -> [| "Authorization", $"Bearer {token}" |]
        | None -> [| userIdHeader, getUserId () |]

// Phase 13a — `identityHeaderPairs` is now exposed as a public value
// the SDK boot path (`SDK.Client.program`) composes into the
// `installRequestGuard` `identityGetter` argument alongside
// `config.RequestSeam.HeaderProviders`. The legacy
// `do CsrfClient.setIdentityProvider identityHeaderPairs`
// module-load side effect has been retired — there is no longer a
// `setIdentityProvider` seam to write into; the guard reads the
// composed getter parameter at send time.

/// Phase 9j — kept for source compatibility (every `Api.makeProxy
/// (customOptions = UserSession.withRequestHeaders)` call site). Both
/// the identity headers AND `X-CSRF-Token` are now attached at *send*
/// time by the `CsrfClient` request-guard (the single seam, over XHR +
/// fetch), reading the live caches per request. This no longer splices
/// a frozen `Remoting.withCustomHeader` list — that proxy-build-time
/// freeze is exactly what caused 401/403 under
/// `DefaultSecurityHardening` for proxies built before sign-in / the
/// CSRF prefetch. Passthrough.
let withRequestHeaders (options: RemoteBuilderOptions) = options