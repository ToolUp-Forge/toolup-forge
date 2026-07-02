# Phase 325 — Forwarded-headers CIDR trust allowlist + auth-mode escalation

`ServerConfig.TrustForwardedHeaders` defaults `true` and, pre-325, always registered
`UseForwardedHeaders` with `KnownIPNetworks` / `KnownProxies` cleared — `X-Forwarded-For` /
`X-Forwarded-Proto` were honoured from **any** peer. A caller rotating `X-Forwarded-For` bypasses
IP rate limiting and poisons audit / access logs; with `RequireHttps = false` a spoofed
`X-Forwarded-Proto: https` flips `Request.IsHttps`, fooling cookie-secure flags and OIDC
`RedirectUri` generation. Phase 325 scopes the trust and escalates the unscoped posture.

## What changes

| Surface | Change |
|---|---|
| `ServerConfig.TrustedProxyCidrs : string list` | **New, default `[]`.** When non-empty, `ForwardedHeadersOptions.KnownIPNetworks` is populated from the parsed CIDRs, so forwarded headers are trusted only from in-range peers. Env: `TOOLUP_TRUSTED_PROXY_CIDRS` (comma-separated, e.g. `10.0.0.0/8,192.168.1.0/24`; IPv6 supported). A malformed entry (including non-zero host bits, e.g. `10.0.0.1/8`) fails loud at startup — preflight `Error`, plus a throw in the pipeline builder as the `SkipPreflight` backstop. |
| `ServerConfig.AcceptForwardedHeadersFromAnyProxy : bool` | **New escape hatch, default `false`.** Env: `TOOLUP_ACCEPT_FORWARDED_HEADERS_FROM_ANY_PROXY=1`. Attests that a single trusted proxy that strips client-supplied `X-Forwarded-*` headers fronts every request path. |
| `ForwardedHeadersTrustValidator` | **Escalated.** `TrustForwardedHeaders = true` + empty `TrustedProxyCidrs` + no escape hatch is now a preflight **`Error` in auth-requiring modes** (any non-Anonymous surface) — the deployment refuses to start, naming both remedies. Anonymous-only deployments keep the Phase 6l.K `Warning`. A populated allowlist or the escape hatch validates `Ok`. |
| Startup log | Trust-any-peer posture keeps the `Warn`; a scoped allowlist logs the declared networks at `Info`. |

## Migration

- **Auth-requiring deployment behind a known terminator (the common production shape):** set
  `TOOLUP_TRUSTED_PROXY_CIDRS` to the terminator's network(s), e.g. `TOOLUP_TRUSTED_PROXY_CIDRS=10.0.0.0/8`.
  Forwarded headers are then honoured only from those peers.
- **Auth-requiring deployment whose proxy strips client-supplied `X-Forwarded-*` (ALB / Cloudflare
  as the only hop):** set `TOOLUP_ACCEPT_FORWARDED_HEADERS_FROM_ANY_PROXY=1` — behaviour is then
  byte-for-byte the pre-325 posture (GP 11).
- **Anonymous-only or `TrustForwardedHeaders = false` deployments:** nothing to do; behaviour and
  preflight outcome are unchanged.

This is deliberately a **fail-loud default change for auth-requiring modes only**: a pre-325
auth-mode deployment that upgrades without setting either knob refuses to start with an actionable
error rather than silently trusting spoofable headers.

## Verification

`src/ToolUp.Platform.Tests/InProcess/ForwardedHeadersTrustTests.fs` (12 cases: escalation matrix,
escape hatch, CIDR scope in/out-of-range incl. IPv6, malformed / host-bits fail-loud, GP 11 default
shape) plus the updated `ForwardedHeadersTrustValidatorTests.fs` (Phase 6l.K matrix, auth arms now
`Error`). Run: `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj`.

## Rollback

Revert the `SDK.Shared.fs` / `ForwardedHeadersTrustValidator.fs` / `ConfigurePipeline.fs` edits, or
operationally set `TOOLUP_ACCEPT_FORWARDED_HEADERS_FROM_ANY_PROXY=1` per deployment to restore the
pre-325 posture without a rebuild.
