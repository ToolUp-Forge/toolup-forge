# Phase 341 — OIDC token-validation hardening (consumer migration)

**What changes.** `OidcAuthProvider` closes four RFC-8725-adjacent gaps in the JWT verifier. Three are
opt-in (max-token-age, fail-closed-on-stale-JWKS, an audience-none preflight warning); one is an
always-on correctness fix (the `azp` multi-audience binding) that only changes behaviour for the
genuinely-dangerous multi-audience-without-`azp` case. **A single-audience, well-configured deployment
is byte-for-byte unchanged** — the opt-in switches default to prior behaviour (GP 11) and the `azp`
rule never fires on a single-audience token.

**Scope.** Server-side only — no client-side change, and **no change to the shared `AuthConfig`
record**. The new switches live on a small OIDC-provider-owned `OidcHardening` record threaded through
new `*Hardened` builder entry points; every existing `fromConfig*` call site keeps compiling unchanged.

## What was added

- **`azp` multi-audience binding (RFC 8725 §3.9), always-on.** When a token's `aud` claim carries
  **more than one** entry, `validateAudience` now additionally requires the authorized-party claim
  (`azp`, falling back to `client_id`) to equal the expected audience. Without it, a token minted for
  `[thisApp, attackerApp]` by a shared issuer previously validated here even though it was authorized
  for a different party. Single-audience tokens are unaffected — the membership check is the whole test,
  exactly as before.
- **`iat`-based maximum token age (opt-in).** `iat` is now parsed and, when
  `OidcHardening.MaxTokenAgeSeconds = Some n`, a token is rejected once `iat + n` (plus the configured
  clock-skew tolerance) is in the past — an absolute age bound independent of `exp`, for IdPs that mint
  long-lived access tokens. When the bound is set, a token with **no** `iat` is rejected (the bound
  cannot be honoured, and must not be bypassable by omitting the claim). Default `None` applies no bound.
- **`Audience = None` preflight warning (opt-in surface).** `OidcAuthValidator.createWithAudience` /
  `tryFromEnv` emit a preflight `Warning` when the issuer is configured but no audience is
  (`TOOLUP_OIDC_AUDIENCE` / `AuthConfig.Audience` unset), so the silent `aud`-check skip is visible.
  This complements — does not replace — the server-side `OidcAudienceBindingValidator`, which still
  hard-**refuses** the auth-required case unless the escape hatch is set; the warning covers the
  remaining cases (e.g. the operator opted into the unbound-audience escape hatch).
- **Fail-closed on stale JWKS (opt-in).** With `OidcHardening.FailClosedOnStaleJwks = true`, a JWKS
  refresh that fails (or is within the refresh cooldown) fails validation **closed** rather than serving
  the cached (possibly-revoked) keys. Default `false` preserves the availability-first stale-fallback.
- **`OidcHardening` record + `fromConfigHardened` / `fromConfigWithHardened` /
  `fromConfigWithMetricsHardened`** builder entry points carrying the two switches.

## When to opt in

- **`MaxTokenAgeSeconds`** — your IdP mints long-lived access tokens (hours/days) but you want a tight
  local age bound, or you are hardening against replay of an old-but-unexpired token.
- **`FailClosedOnStaleJwks`** — high-security deployments that prefer revocation-safety over
  availability: a compromised/rotated signing key must stop validating tokens the moment a JWKS refresh
  fails, rather than continuing to serve cached keys until the next successful fetch. Pair with
  short-lived access tokens / token introspection for the strongest posture.
- Leave both unset (`OidcHardening.defaults`) for the availability-first behaviour every existing
  deployment has today.

## Diff to apply

No change is required for existing consumers — the previous entry points are unchanged. To opt in:

```fsharp
// Before — availability-first, no age bound (unchanged, still valid):
let provider = OidcAuthProvider.fromConfig (Some logger) authConfig

// After — enforce a 15-minute max token age and fail closed on stale JWKS:
let hardening =
    { OidcAuthProvider.OidcHardening.defaults with
        MaxTokenAgeSeconds = Some 900L
        FailClosedOnStaleJwks = true }

let provider = OidcAuthProvider.fromConfigHardened (Some logger) hardening authConfig
```

The `azp` binding needs no opt-in and no code change: it activates automatically for multi-audience
tokens. If a legitimate multi-audience token starts being rejected, its issuer must include an `azp`
(or `client_id`) claim naming this application — that is the RFC-8725 §3.9 requirement the fix enforces.

## Security rationale

1. **`azp` multi-audience binding.** A shared IdP (one Auth0 tenant, Azure AD directory, Keycloak
   realm) can mint a token whose `aud` lists several relying parties. Membership of the expected
   audience alone is insufficient — the token may have been authorized for a *different* party in the
   list. RFC 8725 §3.9 requires `azp` to disambiguate; enforcing it closes a confused-deputy hole while
   leaving the overwhelmingly-common single-audience case untouched.
2. **`iat` max-age.** `exp` bounds a token's validity window but not its absolute age; an IdP that
   issues 24-hour tokens gives a stolen token a 24-hour replay window. A local `iat + maxAge` bound lets
   a deployment enforce a tighter age independently of the issuer's `exp` policy.
3. **Fail-closed on stale JWKS.** The default stale-fallback (serve cached keys when a refresh fails)
   trades revocation-safety for availability — a revoked key keeps validating until a fetch succeeds.
   The opt-in inverts that trade-off for deployments that would rather 401 than honour a possibly-revoked
   key.
4. **Audience-none visibility.** An unset audience silently disables the `aud` check. The preflight
   warning makes that visible at startup even where the hard refusal does not apply.

## Verification

1. `dotnet build ToolUp.Forge.sln` clean.
2. `dotnet run --project Build.fsproj -- VerifyAll` — the `AuthProviders` suite passes, including the
   new Phase 341 cases: multi-audience-without-`azp` rejected; matching-`azp` accepted; mismatched-`azp`
   rejected; single-audience unaffected; over-max-age rejected; fresh-within-max-age accepted;
   no-`iat`-with-max-age rejected; default-ignores-`iat`; strict-JWKS fails closed on a stale window
   while default serves the cached keys; the `Audience = None` preflight warns and a configured audience
   is `Ok`.
3. Operationally: with `OidcHardening.defaults`, confirm existing tokens validate exactly as before.
   Then set `FailClosedOnStaleJwks = true` and confirm validation fails closed when the JWKS endpoint is
   unreachable during a refresh; set `MaxTokenAgeSeconds` and confirm an old-but-unexpired token is
   rejected.

## Rollback

Revert to `OidcHardening.defaults` (or the non-`*Hardened` builders) for the availability-first,
no-age-bound behaviour. The `azp` binding is a correctness fix with no toggle; if a genuinely-needed
multi-audience token lacks `azp`, the correct remedy is to have its issuer add the claim, not to weaken
the check. Fully reversible at the deployment level; no on-disk state is migrated.

## Consumers

**N-A / additive** for every existing consumer: the previous entry points and the `AuthConfig` record
are unchanged, so no consumer code must change. A single-audience, well-configured deployment is
byte-for-byte unchanged. A consumer becomes *affected* only if (a) it relies on a multi-audience token
that carries no `azp` (previously accepted, now rejected — an issuer-side fix), or (b) it chooses to opt
into the new hardening switches.
