# ToolUp.AuthProviders.Oidc.Client

Client-side OIDC sign-in UI for `ToolUp.Platform`. Implements the OAuth 2.0 Authorization Code flow with PKCE against any OIDC-compliant issuer. Registers via the `AuthUIProvider` delegate registry; deployments select it through `ClientConfig.AuthUI`.

## Automatic pre-expiry token refresh

A signed-in shell renews its own bearer, with nothing to wire. `OidcAuthUI.OidcShell` arms one browser timer at sign-in (and on mount over a restored session), refreshes at `exp − 60 s`, re-arms against the new expiry, and cancels on unmount.

**It is on by default, and that is a decision rather than an oversight.** An authenticated shell that quietly lets its bearer lapse and then fails the next API call is the worse default; long-lived sessions are the norm with offline / PWA support and co-editing. The expiry is read from the bearer the session is *actually* sending — the `id_token` under `IdTokenBearer`, the access token under `AccessTokenBearer` — so the bearer strategy and the timer agree by construction. A bearer whose `exp` cannot be read (an opaque access token, an encrypted-payload JWT) falls back to a fixed 300 s cadence.

Three behaviours cover ways a session used to die quietly:

- **A woken background tab catches up.** Browsers throttle timers in background tabs, so a tab parked for an hour wakes with a timer that has not fired and a bearer that has already expired. The shell listens for `visibilitychange` and `online` while a timer is armed and refreshes at once when the session is inside its margin. A wake that is *not* inside the margin deliberately leaves the armed timer alone — a re-arm on every tab-focus would push an opaque-token refresh out indefinitely.
- **Offline, no request is made.** A refresh with no link cannot succeed, and the failure it would produce is indistinguishable from an issuer refusing the grant. The timer re-checks in 30 s; a reconnect triggers an immediate check.
- **A transport failure is a retry, not a sign-out.** The grant is intact, so the session survives an outage of any length. Any other failure means the issuer answered and refused, and the shell drops to sign-in with no half-authenticated state.

Concurrent triggers coalesce to a single `refresh_token` request — which matters against issuers that **rotate** refresh tokens, where a second concurrent POST presents a token the first has already consumed.

All of it is policy, adjustable on `OidcAppConfig.RefreshPolicy`; `None` (the default everywhere) reproduces the above byte for byte (GP 11):

```fsharp
// Slow or rate-limited token endpoint — start the refresh earlier.
OidcPresets.entraExternalId tenant clientId redirectUri
|> OidcPresets.withRefreshMargin 120.0

// Opaque-token provider with a short lifetime — the fallback cadence
// is the only lifetime the client can know about.
OidcPresets.google clientId redirectUri
|> OidcPresets.withRefreshFallback 600.0

// A host app renewing the bearer itself.
OidcPresets.generic issuer clientId redirectUri
|> OidcPresets.withoutAutoRefresh
```

| Knob | Default | What it is for |
|---|---|---|
| `Enabled` | on | The deliberate opt-out. |
| `SafetyMarginSeconds` | `60.0` | Seconds ahead of `exp`; the refresh must *complete* before it. |
| `FallbackSeconds` | `300.0` | Cadence when the bearer carries no readable `exp`. |
| `RefreshOnWake` | on | The `visibilitychange` / `online` catch-up. |

`withRefreshPolicy` sets all four at once; `withoutRefreshOnWake` disables just the catch-up. A non-positive or non-finite value falls back to the default rather than being honoured — a `nan` margin would arm a timer that never fires. The 5 s floor on a computed delay is a safety invariant, not a knob.

Consumers driving renewal manually call `OidcClient.refreshAccessToken` directly; that entry point is unchanged and is what the timer calls.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
