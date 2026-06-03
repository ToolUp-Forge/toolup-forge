# Phase 69o — Client Remoting proxy convention adoption (migration)

## What changes

Phase 69o is a **consumer-side adoption sweep**, not a substrate change. The transport-level per-call header read (Finding F5 in [`application-plans/toolup-remoting-hot-path-perf.md`](../../../ToolUp-Diametrical/application-plans/toolup-remoting-hot-path-perf.md)) was already implemented by [Phase 9j](../../../ToolUp-Diametrical/roadmap/phases/09j-csp-generator-csrf-origin-guard-middleware.md) (send-time `CsrfClient` request-guard at the XHR + fetch seam) and codified as a convention by [Phase 64](../../../ToolUp-Diametrical/roadmap/phases/64-client-remoting-proxy-convention.md). What remained was the consumer-side cleanup: convert the pre-9j defensive `let private api () = ...` per-call shape to module-level values.

See [`docs/platform/client-remoting-proxies.md`](../platform/client-remoting-proxies.md) for the canonical convention statement. This phase doesn't change the convention; it sweeps the remaining sites that hadn't adopted yet.

## Diff to apply (per consumer)

For each `*.Client` module that still declares `let private api () = …`:

```diff
- // Built per call (not module-level): `Api.makeProxy` captures
- // `withRequestHeaders` once and Fable.Remoting freezes the header list,
- // so a module-level value would snapshot an empty `X-CSRF-Token` before
- // the SDK's async CSRF prefetch resolves — 403 on every mutating call
- // under `DefaultSecurityHardening`.
- let private api () =
-     Api.makeProxy<FooApi> (customOptions = UserSession.withRequestHeaders)
+ // Module-level per the SDK convention — see
+ // `toolup-forge/docs/platform/client-remoting-proxies.md`. The
+ // `UserSession.withRequestHeaders` customiser is a passthrough; CSRF /
+ // auth headers attach at send time via `CsrfClient`'s request-guard,
+ // not at proxy-build time.
+ let private api: FooApi =
+     Api.makeProxy<FooApi> (customOptions = UserSession.withRequestHeaders)
```

Then sweep call sites: `(api ()).Method args` → `api.Method args`. If your editor has find/replace, the simplest pair is `(api ())` → `api` (and `(configApi ())` → `configApi` where present). Run Fantomas after.

## Verification

- `dotnet build <your-app>.sln` — clean.
- `dotnet fable -o output --noCache` for the consuming client project — clean. Browser smoke-test that mutating calls still carry the `X-CSRF-Token` header (DevTools → Network → any POST to `/api/*`).
- A diff vs the previous build's Fable JS shows the per-call proxy-construction code removed; the proxy is constructed once during module init.

## Rollback

Revert the diff above. If a future change re-introduces a header-snapshot customiser on `UserSession.withRequestHeaders` (a Phase 9j regression — see the [convention doc's "When per-call would be needed" section](../platform/client-remoting-proxies.md)), the per-call shape becomes correct again — but the right place to relitigate that trade-off is the customiser PR review, not a consumer-module defensive scattering.

## Consumer adoption matrix

| Consumer | Status | Files |
|---|---|---|
| **`toolup-app`** | ✅ swept (commit `bb8915b`) | `Modules/MediaAnalysis/ClientModel.fs` + `Modules/SalesAnalysis/ClientModel.fs`. Template / ChannelAnalysis / CategoryAnalysis / MediaOptimisation already module-level. |
| Concord (Seller + Buyer) | 🟡 pending | Per-consumer audit on adoption PR; the same pattern + convention doc applies. |
| Xcelsys/portal | 🟡 pending | Same. |
| cookbook-apps | 🟡 pending — picked up via "Update Cookbook" pass | Recipe-driven; re-cooking onto the convention is the path. |

## See also

- [Phase 64 — Client Remoting Proxy Convention](../../../ToolUp-Diametrical/roadmap/phases/64-client-remoting-proxy-convention.md) — codified the convention.
- [`docs/platform/client-remoting-proxies.md`](../platform/client-remoting-proxies.md) — canonical convention statement.
- [Phase 9j — CSP generator + CSRF origin guard middleware](../../../ToolUp-Diametrical/roadmap/phases/09j-csp-generator-csrf-origin-guard-middleware.md) — the request-guard that moved header injection to send time.
- Source plan: [`application-plans/toolup-remoting-hot-path-perf.md`](../../../ToolUp-Diametrical/application-plans/toolup-remoting-hot-path-perf.md) (Finding F5).
