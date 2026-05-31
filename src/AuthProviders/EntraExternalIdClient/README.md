# ToolUp.AuthProviders.EntraExternalId.Client

> [!WARNING]
> **Deprecated in 0.4.0** — for new deployments use `OidcPresets.entraExternalId` (or `OidcPresets.entraExternalIdWithDomain` for a custom CIAM host) from `ToolUp.AuthProviders.Oidc.Client`. The single-call preset produces an `OidcAppConfig` that the SDK's standard `OidcAuthUI` consumes — no `CustomAuthUI` wrapping required.
>
> This companion stays compiling for one minor cycle (consumer migration window) and is scheduled for removal at `0.Y.0`. Continue using it when you specifically need the **sign-up / sign-in user-flow policy routing** surface (`SignUpPolicyId` / `SignInPolicyId`) — the single-call preset does not expose that affordance.
>
> See [`docs/migrations/0.4.0-entra-external-id-deprecation.md`](../../docs/migrations/0.4.0-entra-external-id-deprecation.md) for the migration walk-through.

Client-side Microsoft Entra External ID sign-in UI for `ToolUp.Platform`. A thin Fable / Feliz wrapper around `ToolUp.AuthProviders.Oidc.Client` that bakes in External ID's tenant-aware issuer construction, the `offline_access` scope default (required for refresh tokens against External ID), and optional sign-up / sign-in user-flow policy routing.

Wired into a deployment as a `CustomAuthUI` provider:

```fsharp
ClientConfig.AuthUI = CustomAuthUI {
    Wrap =
        EntraExternalIdAuthUI.wrap
            (EntraExternalIdClientConfig.create "<tenant>" "<client-id>" "<redirect-uri>")
}
```

Use this companion when targeting Entra External ID (customer-facing CIAM) and requiring the sign-up affordance. For sign-in-only flows (no policy-routing), prefer the 0.4.0 single-call `OidcPresets.entraExternalId` path. For non-Entra OIDC providers, use `ToolUp.AuthProviders.Oidc.Client` directly via `OidcPresets.generic` or another preset.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
