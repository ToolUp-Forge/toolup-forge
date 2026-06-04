# Phase 16d — Forwarded-headers default-on (consumer migration)

**What changes.** `ServerConfig.TrustForwardedHeaders` defaults flip from `false` to `true`. The SDK now registers `app.UseForwardedHeaders(...)` (honouring `X-Forwarded-Proto` / `X-Forwarded-For` from any peer, with `KnownIPNetworks` / `KnownProxies` cleared) on every deployment unless the consumer explicitly opts out. Containerised and serverless deploys are almost always behind a TLS-terminating ingress (Cloud Run, ALB, App Service Front Door, AKS Ingress, function gateway); the prior opt-in default silently misreported client IPs in audit logs and broke HTTPS redirects until the operator remembered to set `TOOLUP_TRUST_FORWARDED_HEADERS=1`. As container rollout scales across siblings via Phase 16b, opt-in became the highest-frequency footgun in the deployment substrate.

The env-var parser also flips to **fail-loud** on unrecognised values. Pre-Phase-16d, `TOOLUP_TRUST_FORWARDED_HEADERS=tru` (or any typo) silently parsed as `false` and the deployment booted with forwarded-headers trust off. Post-Phase-16d, unrecognised values throw at startup, naming the offending value and the accepted set (`1` / `true` / `yes` / `on` / `0` / `false` / `no` / `off`, case-insensitive).

**Scope.** Server-side single-knob default flip. **Breaking-change classification: default behaviour change on an env var that already exists.** Consumers explicitly setting `TOOLUP_TRUST_FORWARDED_HEADERS=1` (or pinning the field to `true` in code) see no change. Consumers relying on the implicit-off default see new behaviour — at most a startup warning from `ForwardedHeadersTrustValidator` when paired with `RequireHttps = false` in an authenticated surface, plus client-IP / scheme values in audit logs that reflect the originating request rather than the proxy hop.

## Diff to apply

### Most consumers (deploys behind any TLS-terminating ingress)

**No change required.** The new default matches what the deployment needs. If you previously set `TOOLUP_TRUST_FORWARDED_HEADERS=1` in a Dockerfile, compose file, App Service config, or CI pipeline, you can drop it — it's redundant but harmless. The same applies to in-code pins:

```fsharp
// Before — defensive pin, redundant from 0.4.x onwards:
ServerApp.empty
|> ServerApp.withConfig (fun cfg -> { cfg with TrustForwardedHeaders = true })

// After — drop the pin, the default is already true:
ServerApp.empty
```

### Direct-bind dev shells (no proxy hop)

Set the env var or the field explicitly to opt out:

```bash
# Local dev with Kestrel bound directly:
export TOOLUP_TRUST_FORWARDED_HEADERS=0
```

Or in code:

```fsharp
ServerApp.empty
|> ServerApp.withConfig (fun cfg -> { cfg with TrustForwardedHeaders = false })
```

Without the opt-out a dev shell still works, but any handler that branches on `Request.IsHttps` or reads `Request.Scheme` will trust whatever an attacker (or a malformed test fixture) sends in the `X-Forwarded-Proto` header. That's the spoof surface `ForwardedHeadersTrustValidator` warns about; the new default trades it for the much higher-frequency footgun of "production deploy ships with client IPs mis-attributed."

### Pair with `RequireHttps` in authenticated surfaces

`ForwardedHeadersTrustValidator` (Phase 6l.K) emits a `Warning` at startup when `TrustForwardedHeaders = true` is paired with `RequireHttps = false` in an authenticated surface (Individual / Team / MultiTeam). The validator behaviour is unchanged from Phase 6l.K; what changes is the frequency — every authenticated deployment that previously left both knobs at the default now sees the warning unless it sets `TOOLUP_REQUIRE_HTTPS=1`. The fix is to pair the two:

```bash
export TOOLUP_TRUST_FORWARDED_HEADERS=1   # now the default — can drop this line
export TOOLUP_REQUIRE_HTTPS=1             # close the X-Forwarded-Proto: https spoof surface
```

Staging deployments that legitimately run plaintext Kestrel behind upstream TLS can ignore the warning; the validator is `Warning`, not `Error`, so startup proceeds.

## Verification

1. `dotnet build` clean.
2. Boot the deployment behind a TLS-terminating proxy with no `TOOLUP_TRUST_FORWARDED_HEADERS` env var set. Confirm in audit logs (`ModuleEvent.UserId` / `RemoteIpAddress` / `Scheme`) that requests carry the client's IP and the originating scheme rather than the proxy hop's IP and `http`.
3. Set `TOOLUP_TRUST_FORWARDED_HEADERS=0` and reboot. Confirm audit logs revert to the proxy's IP / `http` — i.e. the pre-Phase-16d behaviour is reachable explicitly.
4. Set `TOOLUP_TRUST_FORWARDED_HEADERS=garbage` and confirm the process exits non-zero at startup with a message naming the offending value and the accepted set. Pre-migration this would have parsed as `false` and the deployment would have booted.
5. Authenticated-surface check: with `Surfaces = individual` (or team / multiTeam), `TrustForwardedHeaders = true`, and `RequireHttps = false`, confirm a single `Warning` line from `ForwardedHeadersTrustValidator` at startup. Setting `TOOLUP_REQUIRE_HTTPS=1` clears the warning.

## Rollback

Set `TOOLUP_TRUST_FORWARDED_HEADERS=0` (or pin `TrustForwardedHeaders = false` in code) to revert to the pre-default behaviour for the deployment. The env var is still honoured; the default is the only thing that changed. No server-side or client-side bump is required to roll back — the consumer-side knob is the rollback path.

## Consumers

Consumer-side adoption work is small — drop any defensive `=1` setting from `DEPLOYMENT.md` / per-environment config / Dockerfile ENV lines, and consider pairing with `RequireHttps = true` in authenticated production surfaces.

The migration is **N-A** for any consumer that has no production deploy behind a proxy and runs only against direct-bind localhost (e.g. a standalone demo with no audit-log emission). Such consumers may still want to set `TOOLUP_TRUST_FORWARDED_HEADERS=0` explicitly to keep the validator quiet, but the runtime impact is zero.
