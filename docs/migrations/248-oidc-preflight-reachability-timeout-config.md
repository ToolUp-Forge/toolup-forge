# Phase 248 — Configurable OIDC discovery-reachability preflight timeout

**Ships in:** `ToolUp.AuthProviders.Oidc` (`OidcAuthValidator`). **SDK 0.9.4.** Additive, opt-in.

## What changes

`OidcAuthValidator` probes the configured issuer's discovery document
(`{issuer}/.well-known/openid-configuration`) once at preflight. The probe deadline was a hardcoded
`IConfigValidator.defaultTimeout` (5s). A constrained tier whose first outbound HTTPS call after a
cold start exceeds 5s (TLS handshake + DNS + `HttpClient` JIT on, e.g., Azure App Service Linux B1)
previously had only one lever: **drop the reachability probe entirely** — at which point a genuinely
misconfigured or unreachable issuer ships green and surfaces only at first user sign-in as a 401,
not at deploy.

This phase wires a single env override:

| Env var | Effect |
|---|---|
| `TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS` | Positive integer (milliseconds, `(0, 300000]`) that replaces the 5s probe deadline. **Unset ⇒ byte-for-byte the prior 5s default.** |

- **Set + valid** → the validator probes with the given deadline.
- **Set + invalid** (non-numeric, ≤ 0, or above the 300 000 ms sanity bound) → the validator fails
  preflight with a clear message naming the var and the bad value — it is **rejected, not silently
  defaulted**, so a typo'd budget is caught at boot rather than ignored.
- A **timeout** failure message now names `TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS` as the lever (extend the
  budget) instead of leaving "drop the probe" as the operator's only obvious move.

Note the SDK aggregator independently clamps every validator to its 10s global preflight budget, so
an override above ~10s is effectively capped there at runtime; the 300 000 ms parse bound only
guards against an obviously-wrong value (seconds keyed as ms, an extra zero).

## Diff to apply

**Nothing** for any deployment that doesn't set the var — behaviour is unchanged.

A deployment that previously **disabled** `OidcAuthValidator` to dodge a cold-start timeout should
re-enable the probe and extend the budget instead:

```bash
# CD / app settings — restore boot-time issuer-reachability validation
TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS=15000   # or a measured cold-start budget
```

and drop the probe-disabled / `withConfigValidator`-omitted block that was the workaround.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- Full Expecto suite — green, including the new `OIDC preflight timeout knob (Phase 248)` pack:
  `=15000` ⇒ a 15s validator `Timeout`; unset ⇒ the 5s default; non-numeric / non-positive / absurd
  ⇒ a preflight `Error` naming the var.

## Rollback

Unset `TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS` — the validator reverts to the hardcoded 5s default with no
code change. The feature is inert unless the var is set.
