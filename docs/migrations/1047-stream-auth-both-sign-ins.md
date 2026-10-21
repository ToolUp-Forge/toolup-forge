# The event streams authenticate whichever sign-in the user took

**Ships in:** ToolUp.AuthProviders.Oidc (`OidcSsrSignIn`), ToolUp.Platform.Client (`UserSession`) (Phase 1047). No
public signature changes.

**Affected:** an app that composes the server-rendered sign-in (`OidcSsrSignIn.withInteractiveSignIn`, or
`OidcSsrSignIn.routes`) beside the SPA shell, with the event streams authenticated by cookie
(`TOOLUP_SSE_AUTH=cookie`, `TokenLocation = BearerOrCookie "toolup-auth-token"`). An app without the server sign-in
behaves as before.

## What was wrong

Two sign-ins wrote one cookie, `toolup-auth-token`. The server-rendered sign-in wrote it `HttpOnly`, holding an
id_token, with no expiry. The SPA wrote it from script. A browser never lets script replace an `HttpOnly` cookie of the
same name, so after a reader signed in through a page, the SPA's write was dropped without an error. REST calls carry
the SPA's bearer header and stayed authenticated. `EventSource` carries only cookies, so the notification and AI
streams rode the server's token. When that token expired, every stream answered `401`, and the client gave up
reconnecting.

## What changes

**1. The server sign-in's cookie expires with its token.** The callback sets `Max-Age` to the token's remaining life.
A token whose `exp` has already passed when the callback runs ends on an error page with no cookie. A token with no
`exp` keeps a browser-session cookie, as before.

**2. The SPA replaces a server-written cookie through the server.** On the default
`ClientCookieAndLocalStorage` path, `setAuthToken` reads `document.cookie` back after its write. If its token is not
there, it sends the token in the `Authorization` header to `POST /api/auth/session`. That route validates the token
through the registered `IAuthProvider` before it writes the cookie `HttpOnly; SameSite=Strict` (`Secure` on HTTPS). The
latest sign-in therefore holds the cookie. Before a request, `getAuthToken` also restores a held token that is missing
from `document.cookie`, with a script write only. That covers a server sign-in in another tab whose cookie has since
expired. `clearAuthToken` asks `DELETE /api/auth/session` to clear a cookie the script cannot see.

**3. Composing the server sign-in mounts `POST` / `DELETE /api/auth/session`** whenever the sign-in's `CookieName` is
the SDK's `toolup-auth-token`, so the SPA can always reach them. These are the Phase 133 routes. Like every mutating
`/api` call they are behind the CSRF middleware, and the `POST` route refuses a missing or invalid bearer token. A
deployment that already sets `AuthCookieIssuance = EnabledAuthCookieIssuance` mounts the same handlers twice, which
is harmless because the first match answers.

No token is ever put in a URL or written to a log.

## Migrating

Nothing to change. Upgrade the server and the client together. An old client never calls the route. A new client
against an old server gets a `404` from it and behaves as before.

## Verifying

Sign in through a gated page, then load the SPA. In the browser's developer tools the `toolup-auth-token` cookie holds
the SPA's token, marked `HttpOnly`, `SameSite=Strict`. `GET /api/notifications` stays connected after the page token
would have expired. The pinned cases are in `src/ToolUp.Platform.Tests/InProcess/StreamAuthBothSignInsTests.fs`. They
cover the reproduction, both sign-in orders, the cookie lifetime, sign-out, and what the route refuses.
