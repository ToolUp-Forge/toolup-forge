# Migration — OIDC refresh policy (margins, wake catch-up, opt-out)

`OidcAppConfig` and `OidcUIConfig` each gain `RefreshPolicy: OidcRefreshPolicy option` — the knobs for the client companion's automatic pre-expiry refresh timer. The timer itself already shipped; this phase makes its margins configurable, adds an opt-out, and closes two ways a session could still die quietly.

**Default behaviour is unchanged (GP 11).** `RefreshPolicy = None` arms the timer with the margins it shipped with — 60 s ahead of `exp`, a 5 s floor, a 300 s fallback cadence when the bearer carries no readable `exp`. Every `OidcPresets.*` constructor, `OidcAppConfig.create`, `OidcUIConfig.defaults` and every companion projection supplies `None`.

**The timer stays ALWAYS-ON by default, and that is the decision this phase records.** An authenticated shell that lets its bearer lapse and then fails the next API call is the worse default; long-lived sessions are the norm with offline / PWA support and co-editing. Opting out is available and explicit, but it is not where a deployment starts.

## What you have to change

**Only if you construct `OidcUIConfig` or `OidcAppConfig` as a full record literal.** Add one field:

```fsharp skip=fragment
let cfg: OidcUIConfig = {
    Issuer = "https://your-issuer.example.com"
    ClientId = "<client-id>"
    RedirectUri = "https://your-app.example.com/auth/callback"
    Scopes = [ "openid"; "profile"; "email" ]
    PostLogoutRedirectUri = None
    ValidateIdToken = Some true
    BearerToken = None
    SecondaryFlow = None
    RefreshPolicy = None        // ← new; None = the margins the timer already ran on
}
```

Consumers using `OidcUIConfig.defaults`, `OidcAppConfig.create`, a preset, or a `{ cfg with … }` record update need no change at all.

It is **one nested-record field rather than a field per knob**, deliberately: a later refresh knob widens `OidcRefreshPolicy` and leaves the two records every consumer literal names alone.

## What changes even if you change nothing

Three behaviour changes ride the default, because each is a way the pre-755 timer could end a live session or miss an expired one. None of them is opt-in, and none of them is reachable by a deployment that was working correctly before.

1. **A refresh that never reached the issuer no longer signs the user out.** The old code treated every failure as expiry, so a single timer tick during a tunnel, a closed laptop lid or a dropped wifi hop dropped the shell to the sign-in screen. A `NetworkError` is now a retry (30 s), unbounded — an offline-first session can be away for hours and come back live. Every other failure comes from a response the issuer actually sent (a refused or rotated-away refresh token, a missing `access_token`, a bearer the strategy cannot honour) and still means sign-in.

2. **The timer makes no request while the browser reports itself offline.** It re-checks in 30 s instead, and a reconnect triggers an immediate check.

3. **A woken background tab catches up.** Browsers throttle timers in background tabs, so a tab parked for an hour wakes with a timer that has not fired and a bearer that has already expired — the first API call after the wake is the one that 401s. The shell now listens for `visibilitychange` and `online` while a timer is armed, and refreshes at once when the session is inside its safety margin. A wake that is *not* inside the margin deliberately does nothing rather than re-arming: a re-arm on every tab-focus would restart the delay, and for a bearer with no readable `exp` (where the cadence is a fixed fallback rather than a real deadline) a user who switches tabs often would push the refresh out indefinitely.

Concurrent triggers are coalesced. Before this phase the timer was single-flight by construction — one handle, cancelled before every re-arm — and the wake path breaks that, because both wake events can land while the armed timer's own refresh is still awaiting the token endpoint. Two concurrent `refresh_token` POSTs against an issuer that **rotates** refresh tokens is worse than wasteful: the second presents a token the first has already consumed and is refused, which under the classification above would end a perfectly good session.

## What you can now do

```fsharp skip=fragment
// A slow or rate-limited token endpoint: start the refresh earlier.
// The refresh has to COMPLETE before `exp`, not merely start.
let entraCfg =
    OidcPresets.entraExternalId "<tenant-subdomain>" "<client-id>" "<redirect-uri>"
    |> OidcPresets.withRefreshMargin 120.0

// An opaque-token provider with a short token lifetime. The client
// cannot read a lifetime off an opaque bearer, so the fallback cadence
// is the only one it can know about.
let googleCfg =
    OidcPresets.google "<client-id>" "<redirect-uri>"
    |> OidcPresets.withRefreshFallback 600.0

// A host app that renews the bearer itself — `OidcClient.refreshAccessToken`
// is unchanged and is what the timer calls.
let manualCfg =
    OidcPresets.generic "<issuer>" "<client-id>" "<redirect-uri>"
    |> OidcPresets.withoutAutoRefresh

// A token endpoint that cannot absorb a check per tab-focus. The armed
// timer still fires — late — so this trades promptness for volume
// rather than disabling refresh.
let quietCfg =
    OidcPresets.auth0 "<domain>" "<client-id>" "<redirect-uri>"
    |> OidcPresets.withoutRefreshOnWake

// All four at once. Build from `none ()` and a record update so a later
// knob does not break the call.
let tunedCfg =
    OidcPresets.generic "<issuer>" "<client-id>" "<redirect-uri>"
    |> OidcPresets.withRefreshPolicy
        { OidcRefreshPolicy.none () with
            SafetyMarginSeconds = Some 120.0
            FallbackSeconds = Some 600.0 }
```

| Knob | Default | What it is for |
|---|---|---|
| `Enabled` | on | The deliberate opt-out. With the timer off, nothing else in the record has any effect. |
| `SafetyMarginSeconds` | `60.0` | Seconds ahead of `exp`. |
| `FallbackSeconds` | `300.0` | Cadence when the bearer carries no readable `exp`. |
| `RefreshOnWake` | on | The `visibilitychange` / `online` catch-up. |

A non-positive or non-finite value is **rejected in favour of the default**, not honoured: `nan` propagates through every comparison as `false` and would arm a timer that never fires. A deployment that wants no timer says `Enabled = Some false`, which is unambiguous.

The 5 s floor on any computed delay is not a knob — it is a safety invariant (a zero or negative delay turns the timer into a refresh loop against the issuer), so it is resolved to a fixed value rather than offered.

## Verification

1. **`dotnet build`** — a full-record-literal construction that has not been updated fails to compile (FS0764, missing field). There is no silent path.
2. **Sign-in smoke test** on a deployment that declares nothing: the sign-in screen, the authorize request and the stored session are unchanged, and the first refresh still lands one margin ahead of `exp`.
3. **Offline test**: disconnect the network across a scheduled refresh. The session must survive and resume on reconnect rather than dropping to sign-in.
4. **Background-tab test**: leave a tab in the background past `exp`, then focus it. The first API call after the focus must succeed.
5. **Revocation test** (unchanged from before): revoke the grant at the issuer. The next scheduled refresh must land the shell on sign-in with no half-authenticated state.

## Rollback

Set `RefreshPolicy = None` (or drop the `|> OidcPresets.with…` pipe) to return to the built-in margins. The three default-riding behaviour changes above are not individually reversible by configuration — they are corrections to the timer's failure handling, not features — but `OidcPresets.withoutAutoRefresh` disables the timer entirely, which is the pre-Phase-746 posture. No stored state is involved either way.

## Related

- [Automatic pre-expiry token refresh](../companions/auth-providers.md#automatic-pre-expiry-token-refresh) — the reference documentation.
- [`746-oidc-bearer-token-strategy.md`](746-oidc-bearer-token-strategy.md) — the bearer strategy the timer reads its expiry through, and the phase the timer shipped with.
- [`748-oidc-secondary-flow.md`](748-oidc-secondary-flow.md) — the previous widening of the same two records.
