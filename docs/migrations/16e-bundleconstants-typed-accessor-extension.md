# Phase 16e — `BundleConstants` typed-accessor extension

## What changes

`ToolUp.Platform.BundleConstants` gains four new typed accessors for Vite build-time-injected values consumers previously wired with their own `[<Emit>]` declarations:

| Vite define | F# accessor | Shape |
|---|---|---|
| `__ENTRA_TENANT_ID__` | `BundleConstants.entraTenantId` | `string option` |
| `__ENTRA_CLIENT_ID__` | `BundleConstants.entraClientId` | `string option` |
| `__OIDC_ISSUER_OVERRIDE__` | `BundleConstants.oidcIssuerOverride` | `string option` |
| `__OIDC_AUDIENCE_OVERRIDE__` | `BundleConstants.oidcAudienceOverride` | `string option` |

Each accessor returns `None` for three failure modes: the define is the empty string, the literal JS `'undefined'` string, or the literal placeholder `__NAME__` (i.e. Vite didn't substitute at build time). Consumers gate fail-loud on `None` rather than `String.IsNullOrEmpty` against a raw `string`.

The `platformsdk-solution` template (`dotnet new platformsdk-solution`) now ships a `vite.config.mts` with a `define:` block pre-declaring every Phase 11.G + Phase 58 + Phase 16e mapping. A deployment that doesn't use Entra simply leaves the two Entra env vars unset; the accessors return `None` and the consumer's existing fail-loud branch decides what to do.

This is additive. No existing accessor's shape changes. The Phase 11.G accessors (`moduleFilter`, `agGridLicense`, `clerkPublishableKey`, `platformSurfaces`) continue to return `string` with empty-string-when-unset semantics — migrating those would be a breaking change, deferred indefinitely.

## Diff to apply

### Consumer's `Client.fs` — replace local `[<Emit>]` accessors

Before — consumer-local `[<Emit>]` accessors paired with `String.IsNullOrEmpty` fail-loud branches:

```fsharp
// Microsoft Entra Workforce ID Vite-injected constants. Consumer-local
// `[<Emit>]` accessors paired with the matching defines in
// `vite.config.mts`. SDK's `ToolUp.Platform.BundleConstants` only ships
// typed accessors for the legacy reference-deployment set (Clerk + AG
// Grid + module filter); the Entra pair is consumer-local until folded
// in by Phase 16e.
[<Emit("(typeof __ENTRA_TENANT_ID__ === 'string' ? __ENTRA_TENANT_ID__ : '')")>]
let private entraTenantId: string = jsNative

[<Emit("(typeof __ENTRA_CLIENT_ID__ === 'string' ? __ENTRA_CLIENT_ID__ : '')")>]
let private entraClientId: string = jsNative

// ... later, in the AuthUI construction branch:
if
    System.String.IsNullOrEmpty entraTenantId
    || System.String.IsNullOrEmpty entraClientId
then
    failwith
        "ENTRA_TENANT_ID / ENTRA_CLIENT_ID were empty when bundling this Release build. Set them in CI and rebuild."

let issuer = sprintf "https://login.microsoftonline.com/%s/v2.0" entraTenantId

OidcAuthUI {
    OidcUIConfig.defaults issuer entraClientId redirectUri with
        Scopes = [
            "openid"; "profile"; "email"; "offline_access"
            sprintf "api://%s/access_as_user" entraClientId
        ]
}
```

After — drop the two `[<Emit>]` `let` bindings entirely; consume the SDK accessors and `match` on `Some`:

```fsharp
open ToolUp.Platform

// (no local [<Emit>] declarations — BundleConstants ships them in 0.4.x+)

// ... later, in the AuthUI construction branch:
match BundleConstants.entraTenantId, BundleConstants.entraClientId with
| Some tenantId, Some clientId ->
    let issuer = sprintf "https://login.microsoftonline.com/%s/v2.0" tenantId
    OidcAuthUI {
        OidcUIConfig.defaults issuer clientId redirectUri with
            Scopes = [
                "openid"; "profile"; "email"; "offline_access"
                sprintf "api://%s/access_as_user" clientId
            ]
    }
| _ ->
    failwith
        "ENTRA_TENANT_ID / ENTRA_CLIENT_ID were empty when bundling this Release build. Set them in CI and rebuild."
```

The same shape applies to `oidcIssuerOverride` / `oidcAudienceOverride` when a deployment overrides the IdP-default issuer URL or access-token audience.

### Consumer's `vite.config.mts` — declare the defines

If the consumer's `vite.config.mts` was hand-rolled, ensure the `define:` block carries the new mappings. The `platformsdk-solution` template's reference shape:

```ts
define: {
  // ... existing Phase 11.G + Phase 58 defines ...
  __ENTRA_TENANT_ID__: JSON.stringify(process.env.ENTRA_TENANT_ID ?? ""),
  __ENTRA_CLIENT_ID__: JSON.stringify(process.env.ENTRA_CLIENT_ID ?? ""),
  __OIDC_ISSUER_OVERRIDE__: JSON.stringify(process.env.OIDC_ISSUER_OVERRIDE ?? ""),
  __OIDC_AUDIENCE_OVERRIDE__: JSON.stringify(process.env.OIDC_AUDIENCE_OVERRIDE ?? "")
}
```

The `?? ""` fallback is load-bearing: without it, `JSON.stringify(undefined)` produces the literal four-character string `"undefined"` which the SDK accessor catches and collapses to `None` — fine for a deployment that means "no Entra", but the explicit empty string is the cleaner default.

### Deployment environment

Set the env vars in the deployment's CI / container env / App Service configuration as before — no change. A consumer that previously set `ENTRA_TENANT_ID` + `ENTRA_CLIENT_ID` for the local `[<Emit>]` accessors continues to set the same env vars; only the F# accessor that reads them changes.

## Verification steps

After applying the diff above:

1. **`dotnet build`** — confirms the consumer compiles against the new SDK accessors. Pattern-match exhaustiveness on `Some, Some | _` is type-checked.
2. **`dotnet fable -o output --noCache`** in the consumer's client project — confirms Fable picks up the typed accessors without `unknown` type leakage. The emitted JS for the accessor reads is functionally identical to the prior `[<Emit>]` pattern, modulo the option wrapping (Fable represents `string option` as the string-or-null at runtime, and the `if String.IsNullOrEmpty` branch in the SDK's `toOption` helper compiles to a JS `=== "" ? null : value` ternary).
3. **Bundle byte-equivalence smoke** — `npm run build` in the consumer's client project; compare the bundle's startup sequence against the pre-migration build under DevTools Network + Console. Behaviour MUST be byte-equivalent when `ENTRA_TENANT_ID` + `ENTRA_CLIENT_ID` are both set: the consumer dispatches into the same `OidcAuthUI` branch with the same issuer / clientId values. With either env var unset, the `_ ->` branch now fires the `failwith` — same as before.
4. **Runtime smoke** — visit the deployed app's sign-in page; the Entra IdP redirect carries the same `client_id` / `redirect_uri` / `scope` params as before.
5. **`(GP 12: …)` six-rule portability audit** on the extended `BundleConstants` surface — additive accessors; no portability surface change; audit clean.

## Rollback

Restore the two consumer-local `[<Emit>]` `let` bindings and the `String.IsNullOrEmpty` fail-loud branch. The SDK accessors stay shipped — they don't conflict with the consumer-local accessors (different identifier names: `BundleConstants.entraTenantId` vs `private let entraTenantId`). No SDK version downgrade needed; the migration is consumer-local code shape only.

If the consumer wants to roll back the `vite.config.mts` define wiring at the same time, remove the four Phase 16e entries from the `define:` block. The Phase 11.G + Phase 58 entries remain.

## Risks

- **Fable's `[<Emit>]` propagation across `<ProjectReference>` boundaries is load-bearing for this migration.** A Fable consumer that imports `ToolUp.Platform.Client` via `<ProjectReference>` (rather than `<PackageReference>` against a source-in-nupkg) must see the `[<Emit>]` accessors substitute the `__ENTRA_TENANT_ID__` literal at build time. The Phase 11.G `MinimalApp` sample empirically verified this works for the existing accessors; the same path is exercised by Phase 16e's additions because the `[<Emit>]` mechanism is unchanged. Consumers on unusual `<ProjectReference>` topologies should verify by grepping the post-Fable JS output for the literal `__ENTRA_TENANT_ID__` — if it survives unsubstituted, the SDK accessor's placeholder-detection branch catches it (`None` rather than a junk string).
- **`JSON.stringify(undefined)` is JS, not JSON.** A consumer's `vite.config.mts` that writes `JSON.stringify(process.env.ENTRA_TENANT_ID)` (no `?? ""` fallback) will, when the env var is unset, produce the literal `undefined` (unquoted) in the JS source. Most JS engines coerce this back to the string `"undefined"` at the `typeof` check site — the SDK accessor catches it. The `?? ""` fallback is cleaner but not strictly required.
- **Pattern-match exhaustiveness when only one of the pair is set.** A consumer wiring `entraTenantId` without `entraClientId` (or vice-versa) hits the `_ ->` branch and fails loud. This is intentional — Entra requires both. If a future deployment shape wants tenantId-only or clientId-only behaviour, branch on the two accessors independently rather than as a tuple.
