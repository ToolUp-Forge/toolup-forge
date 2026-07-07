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

/// Token-derived user-id key. Set when `setAuthToken` decodes a JWT
/// and extracts the resolved identity (pre-0.5.6: `sub` only; 0.5.6+:
/// `oid` when present, falling back to `sub` — mirrors the server-side
/// `OidcAuthProvider.userFromPayload` for Entra issuers, so client
/// and server agree on the canonical user-id wire shape). Cleared on
/// `clearAuthToken`. Read by `getUserId` so the same canonical id
/// flows through the `Authorization: Bearer <jwt>` POST path AND the
/// EventSource `?userId=` query-param path. Without this, the auth
/// provider resolves one userId on POSTs while SSE connections
/// register under a different localStorage userId, and every server-
/// side broadcast misses every connection.
let private tokenUserIdKey = "toolup-token-user-id"

/// 0.5.6 — token-derived display name (Entra `name` claim, or any
/// OIDC provider's `name`). Cleared on `clearAuthToken`. Read by
/// `getDisplayName` for rendering "(you)" rows, member lists, and any
/// "show the signed-in user's name" affordance — preferred over the
/// raw `UserId` so users don't see their own `oid` UUID in the UI.
let private tokenDisplayNameKey = "toolup-token-display-name"

/// 0.5.6 — token-derived email address. Reads JWT `email` first, then
/// `preferred_username` as a fallback (Microsoft Entra v2 omits
/// `email` by default and surfaces the address on `preferred_username`
/// instead). Cleared on `clearAuthToken`. Read by `getEmail` for
/// member-row rendering.
let private tokenEmailKey = "toolup-token-email"

/// Cookie name used by the SSE-handshake auth path. The server-side
/// auth provider reads this cookie when configured with
/// `TokenLocation.Cookie "toolup-auth-token"`. Matches `tokenKey` so
/// localStorage-driven flows and cookie-driven flows agree on the
/// key name.
let private authCookieName = "toolup-auth-token"

// ─── Ambient session state (Phase 496 consolidation) ───────────────
//
// This module's ambient state lives in TWO mutable records split by
// lifetime — the subject / auth-bridge state (`session`) and the
// bearer-credential storage state (`tokenState`) — instead of the ten
// scattered `let mutable` bindings it previously carried. Each record
// is the concern's single documented module-level-mutable exception
// (per the "No new side effects" principle); consolidating means the
// related fields cannot be updated inconsistently and the audit
// surface stays one binding per concern.

/// Subject + auth-bridge ambient state — configured at boot by
/// `SDK.Client.run` (`SubjectKind` / `DevDefaultUserId`), then evolved
/// by the bridge install / refresh / health machinery over the life of
/// the page. Phase 496 consolidated the previous eight scattered
/// mutables into this record.
type private SubjectAndBridgeState = {
    /// Phase 66 Stream B.8 — current resolved `SubjectKind` for
    /// storage selection + identity-header assembly. The retiring
    /// per-deployment shape collapsed into the four `SubjectKind`
    /// cases (`AnonymousKind` / `UserKind` / `TeamMemberKind` /
    /// `ClaimBearerKind`); runtime subject upgrade is the server's
    /// responsibility, the client mirrors the latest known kind. Set
    /// by `SDK.Client.run` before any API call; defaults to
    /// `AnonymousKind` until `configure` runs.
    SubjectKind: SubjectKind
    /// Phase 4b dev convenience — when set, `getUserId` seeds an empty
    /// `toolup-user-id` localStorage entry with this value instead of
    /// an auto-generated GUID. Set by `SDK.Client.run` from
    /// `ClientConfig.DevDefaultUserId`. Stays `None` in production.
    DevDefaultUserId: string option
    /// Phase 6k Workstream A — optional bridge to a deployment-chosen
    /// identity SDK (Clerk, Microsoft Entra via MSAL, Auth0, WorkOS).
    /// When installed, the bridge's `GetJwt` is polled periodically
    /// and the resulting JWT is mirrored through `setAuthToken`.
    Bridge: IAuthBridge option
    /// Phase 121 — consecutive bridge-refresh failures since the last
    /// clean round-trip; surfaces the degradation once it crosses
    /// `bridgeFailureThreshold`.
    BridgeFailureStreak: int
    /// Phase 121 — latch so the degradation is announced once per
    /// transition, not once per failing poll.
    BridgeDegradationNotified: bool
    /// Monotonic id seed for `onBridgeHealthChange` subscriptions so
    /// the returned dispose thunk can find its slot.
    NextBridgeHealthHandlerId: int
    /// Registered bridge-health observers, rebuilt on subscribe /
    /// dispose.
    BridgeHealthHandlers: (int * (string option -> unit)) list
    /// Phase 121 — handle of the live bridge-refresh interval, so a
    /// re-install (or `uninstallBridge`) can stop the prior loop
    /// instead of leaking one interval per install.
    BridgeRefreshIntervalHandle: int option
}

let mutable private session = {
    SubjectKind = AnonymousKind
    DevDefaultUserId = None
    Bridge = None
    BridgeFailureStreak = 0
    BridgeDegradationNotified = false
    NextBridgeHealthHandlerId = 0
    BridgeHealthHandlers = []
    BridgeRefreshIntervalHandle = None
}

/// Bearer-credential storage state — which storage strategy the
/// deployment configured, plus the transient in-memory token the
/// `ServerSetHttpOnlyCookie` path holds. Phase 496 consolidated the
/// two token-storage mutables into this record.
type private TokenStorageState = {
    /// Phase 133 — where the bearer JWT lives once acquired. Set once
    /// during client initialisation by `SDK.Client.run` from
    /// `ClientConfig.AuthTokenStorage`.
    ///
    /// `ClientCookieAndLocalStorage` (default): `setAuthToken` writes
    /// the JWT to `localStorage` + a JS-readable `document.cookie` —
    /// the legacy path, XSS-reachable from either store.
    ///
    /// `ServerSetHttpOnlyCookie`: `setAuthToken` POSTs the JWT to
    /// `POST /api/auth/session` (the server reflects it into an
    /// `HttpOnly; Secure; SameSite=Strict` cookie) and keeps it only
    /// in transient in-memory state — never in `localStorage`, never
    /// in a JS-readable cookie. Requires
    /// `ServerConfig.AuthCookieIssuance = EnabledAuthCookieIssuance`.
    Strategy: AuthTokenStorage
    /// In-memory bearer token for the `ServerSetHttpOnlyCookie` path.
    /// Lost on reload (the durable credential is the HttpOnly cookie;
    /// the bridge re-populates this on boot). Never persisted.
    ServerCookieToken: string option
}

let mutable private tokenState = {
    Strategy = ClientCookieAndLocalStorage
    ServerCookieToken = None
}

// ─── Subject kind ──────────────────────────────────────────────────

/// Configure the resolved subject kind. Called once during client
/// initialisation; updates the cached value the storage / header
/// helpers branch on. Idempotent — subsequent calls overwrite.
let configure (kind: SubjectKind) =
    session <- { session with SubjectKind = kind }

/// Read the configured subject kind. Returns `AnonymousKind` until
/// `configure` runs.
let getSubjectKind () = session.SubjectKind

/// Configure the dev-default user-id (Phase 4b). Called once during
/// client initialisation by `SDK.Client.run`. `None` (default) preserves
/// the auto-generated-GUID behaviour; `Some` overrides only the
/// first-visit generation path (existing localStorage values are
/// preserved either way).
let configureDevDefault (id: string option) =
    session <- { session with DevDefaultUserId = id }

/// Configure the auth-token storage strategy (Phase 133). Idempotent —
/// subsequent calls overwrite. Called once during client initialisation.
let configureAuthTokenStorage (storage: AuthTokenStorage) =
    tokenState <- { tokenState with Strategy = storage }

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

/// 0.5.6 — decoded identity claims the SDK cares about for client-
/// side identity continuity. The `UserId` field MIRRORS the server-
/// side identity resolution shape (see `OidcAuthProvider.userFromPayload`
/// in 0.5.4): prefer the tenant-stable `oid` claim over the pairwise
/// pseudonymous `sub` when both are present (Microsoft Entra v2),
/// fall back to `sub` for non-Entra IdPs. Mismatch between client
/// and server identity sources silently breaks `TeamMembership`
/// self-lookups (member row `UserId` is the server's resolved id;
/// `UserSession.getUserId ()` was the client's local decode — under
/// the pre-0.5.6 `sub`-only client decode against a 0.5.4+ `oid`-
/// preferring server, these never matched, `canManage` stayed false,
/// and the "Add member" / "Pending invites" affordances stayed
/// hidden).
///
/// `DisplayName` reads Entra's `name` claim (also present on most
/// non-Microsoft OIDC providers). `Email` reads `email` first, then
/// falls back to `preferred_username` — Microsoft Entra v2 omits
/// `email` by default and surfaces the email address on
/// `preferred_username` instead. Both are `None` for tokens that
/// don't carry either claim; the rendering layer falls back to
/// `UserId` in that case.
type private DecodedIdentity = {
    UserId: string
    DisplayName: string option
    Email: string option
}

let private tryGetString (payload: obj) (key: string) : string option =
    let v = payload?(key)

    match box v with
    | null -> None
    | _ ->
        let s = string v
        if System.String.IsNullOrEmpty s then None else Some s

let private decodeJwtIdentity (token: string) : DecodedIdentity option =
    try
        let parts = token.Split('.')

        if parts.Length < 2 then
            None
        else
            let payloadJson = atob (base64UrlToB64 parts[1])
            let payload = jsonParse payloadJson

            // Prefer oid over sub — mirrors server-side
            // `OidcAuthProvider.userFromPayload` when
            // `AuthConfig.PreferOidWhenPresent = Some true`. For non-
            // Entra tokens (no `oid` claim), the SDK server-side and
            // client-side both fall through to `sub`, so identity
            // continuity is preserved regardless of IdP.
            let userId =
                match tryGetString payload "oid" with
                | Some _ as oid -> oid
                | None -> tryGetString payload "sub"

            match userId with
            | None -> None
            | Some uid ->
                Some {
                    UserId = uid
                    DisplayName = tryGetString payload "name"
                    Email =
                        match tryGetString payload "email" with
                        | Some _ as e -> e
                        | None -> tryGetString payload "preferred_username"
                }
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
/// **This cookie is NOT `HttpOnly`** — that flag can only be set
/// server-side via `Set-Cookie`. So the JWT it carries is fully
/// JS-readable: any injected script can read it from `document.cookie`
/// (and from `localStorage`, where the same token also lives on this
/// path), which means an XSS escalates directly to bearer-token theft
/// with no HttpOnly barrier. This is the **dev / SPA-without-server-
/// session** trade-off: acceptable only for the dev EventSource-
/// handshake path, or for a SPA that accepts the XSS exposure and ships
/// a strict CSP (Phase 9j / Phase 129 header baseline) as the
/// mitigation. The production-shape alternative is
/// `AuthTokenStorage = ServerSetHttpOnlyCookie`, where the server sets
/// an `HttpOnly` cookie and the JWT never enters JS-readable storage
/// (Phase 133).
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

// ─── Phase 133 — server-set HttpOnly cookie path ───────────────────
//
// On the `ServerSetHttpOnlyCookie` path the JWT is never written to
// `localStorage` or `document.cookie`. It is held only in transient
// in-memory state (`tokenState.ServerCookieToken`) and reflected into a
// server-set `HttpOnly; Secure; SameSite=Strict` cookie via
// `POST /api/auth/session`. `getAuthToken` reads this in-memory value so
// the OIDC `exp` reader, `onAuthTokenChange`, and the bridge-dedup keep
// working without the token ever touching JS-readable storage.

/// POST the JWT to the server reflection endpoint. `credentials:
/// 'same-origin'` so the response `Set-Cookie` is honoured and the CSRF
/// double-submit cookie rides along; the request-guard attaches the
/// `X-CSRF-Token` header on this state-changing `/api/*` fetch. The
/// `Authorization: Bearer` header is set EXPLICITLY here because on this
/// path `getAuthToken` feeds the request-guard the in-memory token only
/// after this call — and the guard never overrides a header already
/// present. Fire-and-forget: a failed reflect leaves the in-memory
/// token in place (requests still carry the Bearer header) and the next
/// bridge refresh retries. Never throws.
[<Emit("fetch('/api/auth/session', { method: 'POST', credentials: 'same-origin', headers: { 'Authorization': 'Bearer ' + $0 } }).catch(function(){})")>]
let private reflectTokenToServer (token: string) : unit = jsNative

/// DELETE the server-set cookie on sign-out (`Max-Age=0` server-side).
/// `credentials: 'same-origin'` so the clearing `Set-Cookie` applies.
/// Never throws.
[<Emit("fetch('/api/auth/session', { method: 'DELETE', credentials: 'same-origin' }).catch(function(){})")>]
let private clearServerCookie () : unit = jsNative

// ─── User ID ───────────────────────────────────────────────────────

/// Get or create a user/session ID.
///
/// Always reads from localStorage so the SSE EventSource `?userId=`
/// query-param path and the Fable.Remoting `X-User-Id` POST path
/// resolve the same value regardless of when each is called or what
/// `session.SubjectKind` was at the time. The pre-fix design forked
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
        match session.SubjectKind with
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
                match session.DevDefaultUserId with
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
/// Phase 6k Workstream A: on the default `ClientCookieAndLocalStorage`
/// path also writes the token to `localStorage` + `document.cookie` so
/// EventSource handshakes can authenticate when
/// `ServerConfig.SseAuthMode = CookieRequired`. The cookie path is
/// silent overhead in `QueryParamFallback` deployments — the server
/// just doesn't read it.
///
/// Phase 133: on the `ServerSetHttpOnlyCookie` path the JWT is held only
/// in transient in-memory state and reflected to `POST /api/auth/session`
/// (the server sets the durable `HttpOnly` cookie). It is NOT written to
/// `localStorage` or `document.cookie`, so an XSS cannot dump it from
/// either store. The non-secret identity claims (userId / name / email)
/// are still persisted on BOTH paths — they drive `getUserId` /
/// `getDisplayName` and are not the bearer credential.
let setAuthToken (token: string) =
    // Identity claims (non-secret) persist on both paths.
    match decodeJwtIdentity token with
    | Some identity ->
        Browser.Dom.window.localStorage.setItem (tokenUserIdKey, identity.UserId)

        match identity.DisplayName with
        | Some name -> Browser.Dom.window.localStorage.setItem (tokenDisplayNameKey, name)
        | None -> Browser.Dom.window.localStorage.removeItem tokenDisplayNameKey

        match identity.Email with
        | Some email -> Browser.Dom.window.localStorage.setItem (tokenEmailKey, email)
        | None -> Browser.Dom.window.localStorage.removeItem tokenEmailKey
    | None ->
        // Opaque or malformed token — leave any previously-stored
        // identity values in place. `getUserId` falls back to the
        // localStorage GUID if `tokenUserIdKey` is missing; display
        // helpers return None and the rendering layer falls back to
        // showing the raw UserId.
        ()

    // The bearer credential itself: storage depends on the configured
    // strategy.
    match tokenState.Strategy with
    | ClientCookieAndLocalStorage ->
        tokenState <- {
            tokenState with
                ServerCookieToken = None
        }

        Browser.Dom.window.localStorage.setItem (tokenKey, token)
        setAuthCookie token
    | ServerSetHttpOnlyCookie ->
        // Never touch localStorage / document.cookie for the token.
        // Hold it in memory only and reflect it into the server-set
        // HttpOnly cookie.
        tokenState <- {
            tokenState with
                ServerCookieToken = Some token
        }

        reflectTokenToServer token

/// Clear the auth token (called on sign-out). Also clears the
/// token-derived userId, display name, email, the in-memory token, AND
/// the auth cookie — both the JS-readable cookie and (on the
/// `ServerSetHttpOnlyCookie` path) the server-set `HttpOnly` cookie via
/// `DELETE /api/auth/session` — so the next sign-in resolves a fresh
/// subject and no usable token survives in either store.
let clearAuthToken () =
    Browser.Dom.window.localStorage.removeItem tokenKey
    Browser.Dom.window.localStorage.removeItem tokenUserIdKey
    Browser.Dom.window.localStorage.removeItem tokenDisplayNameKey
    Browser.Dom.window.localStorage.removeItem tokenEmailKey
    // Clear the JS-readable cookie unconditionally (cheap, belt-and-
    // suspenders) and reset the in-memory token.
    clearAuthCookie ()

    tokenState <- {
        tokenState with
            ServerCookieToken = None
    }

    // On the server-cookie path also clear the HttpOnly cookie the
    // server set — the client cannot touch it directly.
    match tokenState.Strategy with
    | ClientCookieAndLocalStorage -> ()
    | ServerSetHttpOnlyCookie -> clearServerCookie ()

/// 0.5.6 — read the token-derived display name (JWT `name` claim).
/// `None` when no JWT has been persisted, when the JWT didn't carry a
/// `name` claim, or after `clearAuthToken`. The rendering layer
/// (TeamManagerUI member rows, "(you)" badges, header chrome) reads
/// this and falls back to the user-id when None.
let getDisplayName () : string option =
    match Browser.Dom.window.localStorage.getItem tokenDisplayNameKey with
    | null
    | "" -> None
    | name -> Some name

/// 0.5.6 — read the token-derived email address (JWT `email` claim,
/// or `preferred_username` fallback for Entra). `None` when no JWT
/// has been persisted, when the JWT didn't carry either claim, or
/// after `clearAuthToken`.
let getEmail () : string option =
    match Browser.Dom.window.localStorage.getItem tokenEmailKey with
    | null
    | "" -> None
    | email -> Some email

/// Get the current auth token, if any.
///
/// Phase 133: on the `ServerSetHttpOnlyCookie` path the token is never
/// in `localStorage`, so this reads the transient in-memory value. Keeps
/// the OIDC `exp` reader, `onAuthTokenChange`, and the bridge-dedup
/// working identically on both paths without the token ever touching
/// JS-readable storage.
let getAuthToken () =
    match tokenState.ServerCookieToken with
    | Some _ as t -> t
    | None ->
        match Browser.Dom.window.localStorage.getItem tokenKey with
        | null
        | "" -> None
        | token -> Some token

// ─── Auth bridge (Phase 6k Workstream A) ───────────────────────────
//
// The bridge itself (`session.Bridge`) is optional and deployment-
// chosen (Clerk, Microsoft Entra via MSAL, Auth0, WorkOS). When
// installed, the bridge's `GetJwt` is polled periodically and the
// resulting JWT is mirrored through `setAuthToken` — so the existing
// synchronous `withRequestHeaders` path continues to work, and the JWT
// cookie + localStorage stay current as the provider SDK refreshes the
// token silently in the background.
//
// `setAuthToken` continues to work without a bridge — the bridge is
// the *preferred* path for production but deployments without one can
// drive `setAuthToken` directly from their auth UI.

/// Refresh interval for the installed bridge. JWT lifetimes are
/// typically 1 hour; checking every 60 s catches expiry without
/// burning cycles. The bridge's own SDK cache absorbs most calls.
let private bridgeRefreshIntervalMs = 60_000

// ─── Phase 121 — bridge-refresh health ───────────────────────────────
//
// `refreshFromBridgeOnce` used to swallow every bridge failure
// (`with _ -> ()`): a persistently failing bridge (Clerk misconfig,
// expired MSAL session, CSP blocking the IdP iframe) silently let the
// cached JWT expire and degraded the user to an effectively-anonymous
// subject with zero observability. The streak counter
// (`session.BridgeFailureStreak`) surfaces the failure once it is
// clearly persistent rather than transient: at
// `bridgeFailureThreshold` consecutive failures it emits through
// `AuthDiagnostics` + the `client.auth.bridge` category logger and
// notifies `onBridgeHealthChange` subscribers (the shell maps the
// notification to a boot-degradation banner entry). A subsequent
// clean round-trip resets the streak and notifies recovery.

let private bridgeFailureThreshold = 3

let private bridgeLog = Logger.forCategory "client.auth.bridge"

/// Phase 121 — observe bridge-refresh health transitions. The callback
/// receives `Some message` when the bridge has failed
/// `bridgeFailureThreshold` consecutive refreshes (degraded), and
/// `None` when a later refresh succeeds (recovered). Fires once per
/// transition, not per failing poll. Returns a dispose callback.
let onBridgeHealthChange (handler: string option -> unit) : unit -> unit =
    let id = session.NextBridgeHealthHandlerId

    session <- {
        session with
            NextBridgeHealthHandlerId = id + 1
            BridgeHealthHandlers = (id, handler) :: session.BridgeHealthHandlers
    }

    fun () ->
        session <- {
            session with
                BridgeHealthHandlers = session.BridgeHealthHandlers |> List.filter (fun (hid, _) -> hid <> id)
        }

let private notifyBridgeHealth (health: string option) =
    for _, handler in session.BridgeHealthHandlers do
        try
            handler health
        with _ ->
            ()

let private refreshFromBridgeOnce () = async {
    match session.Bridge with
    | None -> return ()
    | Some bridge ->
        try
            let! jwt = bridge.GetJwt()

            // Phase 121 — a clean round-trip (token present OR a
            // definitive signed-out answer) resets the failure streak;
            // if a degradation had been surfaced, announce recovery.
            session <- { session with BridgeFailureStreak = 0 }

            if session.BridgeDegradationNotified then
                session <- {
                    session with
                        BridgeDegradationNotified = false
                }

                bridgeLog.Info "auth-bridge refresh recovered"
                AuthDiagnostics.emitOk None "bridge-refresh-recovered" None
                notifyBridgeHealth None

            match jwt with
            | Some token ->
                // Avoid a needless write/reflect if the bridge returned
                // the same value we already have. JWT strings are
                // short — the equality check is cheap. Reads via
                // `getAuthToken` so the dedup works on both the
                // localStorage path and the in-memory server-cookie path
                // (Phase 133).
                match getAuthToken () with
                | Some current when current = token -> ()
                | _ -> setAuthToken token
            | None ->
                // Bridge says signed out — clear local state if we
                // were holding any. Idempotent — no-op when already
                // signed out.
                if (getAuthToken ()).IsSome then
                    clearAuthToken ()
        with ex ->
            // Bridge errors are non-fatal — leave the cached token in
            // place (the deployment's auth UI surfaces sign-in
            // failures via its own affordances). Phase 121: count the
            // consecutive failures instead of discarding them, and
            // surface the streak once it crosses the threshold.
            session <- {
                session with
                    BridgeFailureStreak = session.BridgeFailureStreak + 1
            }

            if
                session.BridgeFailureStreak >= bridgeFailureThreshold
                && not session.BridgeDegradationNotified
            then
                session <- {
                    session with
                        BridgeDegradationNotified = true
                }

                bridgeLog.Warn(
                    sprintf
                        "auth-bridge GetJwt failed %d times in a row (%s) — the cached JWT will expire and this session will degrade to effectively-anonymous. Check the identity-provider SDK configuration (Clerk/MSAL/Auth0), the provider session validity, and any CSP rule blocking the IdP iframe."
                        session.BridgeFailureStreak
                        ex.Message
                )

                AuthDiagnostics.emitErr None "bridge-refresh-failing" {
                    Kind = "BridgeRefreshFailing"
                    SubCause = Some ex.Message
                }

                notifyBridgeHealth (Some "Session refresh is failing — you may be signed out shortly")
}

[<Emit("setInterval($0, $1)")>]
let private setInterval (cb: unit -> unit) (ms: int) : int = jsNative

[<Emit("clearInterval($0)")>]
let private clearInterval (handle: int) : unit = jsNative

/// Install a deployment-specific auth bridge. Called once during
/// `SDK.Client.run` if `ClientConfig.AuthBridge` is `Some`. Kicks off
/// an immediate JWT fetch + a periodic refresh loop. Idempotent — a
/// re-install replaces the previous bridge AND stops the previous
/// refresh interval (pre-121 the old interval leaked and kept polling
/// the replaced bridge).
let installBridge (bridge: IAuthBridge) =
    match session.BridgeRefreshIntervalHandle with
    | Some handle -> clearInterval handle
    | None -> ()

    session <- {
        session with
            Bridge = Some bridge
            BridgeRefreshIntervalHandle = None
    }

    // Immediate fetch so the cookie + localStorage are populated
    // before the first request flies. Subsequent refreshes happen on
    // the interval below.
    Async.StartImmediate(refreshFromBridgeOnce ())

    let handle =
        setInterval (fun () -> Async.StartImmediate(refreshFromBridgeOnce ())) bridgeRefreshIntervalMs

    session <- {
        session with
            BridgeRefreshIntervalHandle = Some handle
    }

/// Phase 121 — remove the installed bridge and stop its refresh
/// interval. Sign-out cleanup affordance (and test teardown); the
/// bridge-health streak resets so a later install starts clean.
let uninstallBridge () =
    match session.BridgeRefreshIntervalHandle with
    | Some handle -> clearInterval handle
    | None -> ()

    session <- {
        session with
            BridgeRefreshIntervalHandle = None
            Bridge = None
            BridgeFailureStreak = 0
            BridgeDegradationNotified = false
    }

/// Read the installed bridge, if any. Used by trace logging in
/// `withRequestHeaders` and the dev panel's auth surface.
let getBridge () = session.Bridge

/// Force an immediate JWT refresh from the installed bridge. Useful
/// for deployments that know a sign-in/sign-out just happened (e.g.
/// after a Clerk sign-in callback) and want the SSE cookie updated
/// without waiting for the periodic interval.
let refreshFromBridge () = refreshFromBridgeOnce ()

// ─── Auth-token transition observer (Phase 3d.A) ───────────────────
// (`clearInterval` is bound once above, next to `setInterval`.)

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
    match session.SubjectKind with
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