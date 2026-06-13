# Phase 133 — server-set HttpOnly auth cookie

## What changes

Before this phase the client kept the bearer JWT in **JS-readable storage**: it
wrote the token to `localStorage` *and* mirrored it into a `document.cookie`
that — structurally — cannot be `HttpOnly` (only a server `Set-Cookie` can be).
A single injected script could read the token from either store and exfiltrate a
usable `Authorization: Bearer` credential. The in-code comment conceded the JS
cookie was "functionally equivalent to localStorage for security purposes."

Phase 133 adds an opt-in **BFF-style server-set cookie** path. When a deployment
enables it:

- The server mounts `POST` / `DELETE /api/auth/session`
  (`ServerConfig.AuthCookieIssuance = EnabledAuthCookieIssuance`).
- The client POSTs the JWT it just acquired (in the `Authorization` header)
  once; the server **validates it through the registered `IAuthProvider`** and
  reflects it into an `HttpOnly; Secure; SameSite=Strict; Path=/` cookie
  (`ClientConfig.AuthTokenStorage = ServerSetHttpOnlyCookie`).
- On this path the JWT **never enters `localStorage` or a JS-readable cookie**.
  It lives only in transient in-memory JS state (lost on reload, re-acquired via
  the `IAuthBridge`); the durable session credential is the HttpOnly cookie the
  browser sends automatically for SSE (`EventSource`) and same-origin XHR.

Both defaults are unchanged (`NoAuthCookieIssuance` /
`ClientCookieAndLocalStorage`), so an existing deployment is byte-for-byte
identical until it opts in (GP 11).

## When does this apply to me?

| Your deployment | Action |
|---|---|
| `IAuthBridge`-based (Clerk / MSAL / Auth0 / WorkOS), production-shape | **Adopt** — see diff below. The bridge refresh model is what re-populates the cookie. |
| OidcClient PKCE (SDK-built sign-in, no bridge) | **Stay on the default.** The localStorage-based pre-expiry refresh timer in `OidcTokenStore` is not compatible with `ServerSetHttpOnlyCookie` in v1 — the token must be in `localStorage` for the timer to read `exp`. A full BFF (server-side refresh) is the longer-term path. Pair the JS path with a strict CSP (below). |
| Pure `Authorization: Bearer` SPA that must keep a JS-readable token | **Stay on the default and ship a CSP.** The XSS exposure is accepted-risk; the mitigation is the Phase 9j / Phase 129 header baseline (`SecurityHeaders = StrictSecurityHeaders` with a CSP that blocks injected script). |
| Clerk / OIDC hosted-UI where the IdP already issues an HttpOnly session | **N-A** — the SDK isn't minting the cookie. |

## Diff to apply (bridge-based production)

### Server composition root

```fsharp
// before
ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withAuth oidcAuthProvider          // TokenLocation = BearerHeader
|> ...

// after
let config =
    { config with
        AuthCookieIssuance = EnabledAuthCookieIssuance
        // SameSite=Strict cookie alone is the CSRF defence on the cookie
        // path; keep SecurityHardening on for the double-submit token too.
        SecurityHardening = DefaultSecurityHardening }

// The auth provider must read the bearer header on the /api/auth/session
// reflect call AND the cookie on every later request + SSE handshake.
let authConfig =
    { authConfig with TokenLocation = BearerOrCookie "toolup-auth-token" }
```

`AuthCookieIssuance` can also be set from the environment:
`TOOLUP_AUTH_COOKIE_ISSUANCE=enabled`.

### Client composition root

```fsharp
// before
Client.run
    { ClientConfig.defaults with
        AppName = "MyApp"
        Mode = MultiTeam
        AuthUI = CustomAuthUI (... bridge wrapper ...) }
    modules

// after
Client.run
    { ClientConfig.defaults with
        AppName = "MyApp"
        Mode = MultiTeam
        AuthUI = CustomAuthUI (... bridge wrapper ...)
        AuthTokenStorage = ServerSetHttpOnlyCookie }   // ← move JWT off JS storage
    modules
```

## Verification steps

1. `dotnet build` — both composition roots compile.
2. Sign in. In browser devtools:
   - **Application → Local Storage** — no `toolup-auth-token` entry (identity
     keys `toolup-token-user-id` / `-display-name` / `-email` remain; those are
     not the bearer credential).
   - **Application → Cookies** — a `toolup-auth-token` cookie with **HttpOnly ✓,
     Secure ✓ (on HTTPS), SameSite = Strict**.
   - **Console** — `document.cookie` does **not** contain `toolup-auth-token`.
3. Open a page that uses SSE (`/api/notifications`) — the stream authenticates
   off the cookie with no JS-readable token (works under
   `SseAuthMode = CookieRequired`).
4. Sign out — the `toolup-auth-token` cookie is gone (server `Max-Age=0`) and no
   token remains in `localStorage` or `document.cookie`.
5. Reload while signed in — still authenticated (the HttpOnly cookie persists);
   the bridge re-fetches the JWT and re-reflects it on boot.

## Rollback

Set `ServerConfig.AuthCookieIssuance = NoAuthCookieIssuance` (unmounts the
endpoint) and `ClientConfig.AuthTokenStorage = ClientCookieAndLocalStorage`
(restores the legacy `localStorage` + JS-cookie writes). No data migration —
the change is purely where the live token is held; existing sessions re-acquire
on next sign-in / bridge refresh.

## Security note — CSP is the companion control

For any path that keeps a JS-readable bearer (the default, or the
`Authorization: Bearer` SPA), the residual XSS-to-token-theft exposure is
mitigated by a Content Security Policy that blocks script injection — the
Phase 9j CSP generator / Phase 129 header baseline. The HttpOnly path reduces
*reliance* on that CSP; it does not remove the value of shipping one.
