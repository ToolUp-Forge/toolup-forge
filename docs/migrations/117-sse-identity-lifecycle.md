# Phase 117 — Identity-aware SSE lifecycle (consumer migration)

**What changes.** The notification SSE channel is now identity-correct end-to-end. Server: `GET /api/notifications` resolves its subscriber scope through the same shared path as `GET /api/ai/events` (`SseScopeResolution.resolve`), so under `SseAuthMode = CookieRequired` an unauthenticated connect is refused **401** instead of trusting a client-supplied `?userId=` (which let any caller eavesdrop an arbitrary user's live stream under the `AcceptQueryParamSseAuthWhenAuthRequired` escape hatch), and a `?userId=` that mismatches a validated principal is ignored **and audited**. Both SSE routes also gain a per-IP connect-rate window. Client: the `EventSource` follows auth transitions (sign-out → sign-in re-registers under the new user), the permanent give-up latch is replaced by bounded retry-with-backoff, and the handshake prefers cookie auth whenever a token exists.

**Scope.** `ToolUp.Platform.Server` (notification route, validator text, rate limiting), `ToolUp.AI.Server` (shared scope resolution — behaviour unchanged), `ToolUp.Platform.Client` (`NotificationClient`, shell auth watcher). Wire shape of delivered envelopes is unchanged.

**Backward compatibility.**

- **Client API is additive** — `NotificationClient.reset()` / `reconnect()` are new; `subscribe` / `publishLocal` are unchanged. No consumer code needs editing; the shell wires the new lifecycle automatically.
- `notificationHandler` (server) gains a third parameter (`SseAuthMode`). Only consumers that mount the handler manually (instead of via `ServerApp.run`) need to thread `config.SseAuthMode` through — no workspace consumer does this.
- `CookieRequired` deployments: connects that previously *silently registered under a client-asserted identity* now 401 until the JWT cookie is present. The shipped client already writes that cookie on `setAuthToken` and now defers/cycles the stream around token presence, so a stock client sees no regression — only earlier, visible refusals where there used to be a silent wrong-scope stream.
- `QueryParamFallback` + escape-hatch deployments: behaviour for anonymous callers with `?userId=` is unchanged (that is the documented, explicitly-opted-in exposure — the validator now names it as live-stream eavesdropping). New: when a *validated principal* is present, a mismatching `?userId=` produces a `SurfaceDenied` audit row (`DenialCode = "sse_userid_principal_mismatch"`, scope `_platform`) plus a server Warn. Expect new audit rows if clients send stale params; the param was already ignored before — only the visibility is new.
- **Rate limiting:** deployments with `RateLimitConfig.none` (the default) are unaffected — no limiter registers at all. Deployments with rate limiting enabled: `/api/notifications` and `/api/ai/events` were previously exempt; connect attempts are now metered per-IP at 60/minute (`RateLimiting.sseConnectPolicy`), 429 + `Retry-After: 60` beyond that. Held-open streams consume one permit at connect only.
- **Client retry:** a fatal SSE close (404/401/502) now retries 3× over ~60 s (5 s / 15 s / 40 s) before latching for the session; previously the first fatal close latched permanently. `NoNotifications` servers without the `__TOOLUP_NOTIFICATIONS_DISABLED__` Vite constant see three extra connect attempts in the first minute, then silence — wire the bundle constant (Phase 58) to keep zero requests.

## Diff to apply

Nothing for stock consumers. For deployments that mount the notification route by hand:

```fsharp
// Before:
NotificationHandler.notificationHandler channel manager

// After:
NotificationHandler.notificationHandler channel manager config.SseAuthMode
```

## New observability

| Signal | Where | Meaning |
|---|---|---|
| `SurfaceDenied` audit row, `DenialCode = "sse_userid_principal_mismatch"` | `IAuditLog`, scope `_platform` | A validated principal connected with a mismatching `?userId=` — probing indicator. `Hint` carries the asserted id. |
| `notifications-sse-queryparam-fallback` | client `AuthDiagnostics` (once per tab) | Authenticated-kind session had no token and fell back to `?userId=` (dev escape hatch — wire an `IAuthBridge` in production). |
| `notifications-sse-gave-up` | client `AuthDiagnostics` | Bounded retry budget exhausted; notifications off for the session until an auth transition or reload. |

## Verification

1. `dotnet build ToolUp.Forge.sln` (clean tree) — green.
2. Client pack: `cd src/ToolUp.AI.Client.Tests && dotnet fable -o output && node --import ./register-loader.mjs --test output/Program.js` — includes the Phase 117 lifecycle suite (auth transition closes + reopens the stream; same-identity refresh no-ops; fatal close retries instead of latching).
3. Manual, `CookieRequired` deployment: `curl -i "https://<host>/api/notifications?userId=anything"` (no cookie) → **401** plain text.
4. Manual, escape-hatch deployment: connect with a signed-in session and a forged `?userId=` → stream delivers the *principal's* events; one `SurfaceDenied` row appears in the audit trail.
5. Sign out and sign in as a different user in one tab → new user's notifications arrive within one reconnect cycle (no reload).

## Rollback

Revert forge commits `5e08627` (server), `4cfe2a6` (client), `ae70442` (tests). No data migration; the audit rows already written use the long-standing `SurfaceDenied` shape and need no cleanup.
