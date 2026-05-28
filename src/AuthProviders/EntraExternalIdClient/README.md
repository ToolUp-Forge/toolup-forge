# ToolUp.AuthProviders.EntraExternalId.Client

Client-side Microsoft Entra External ID sign-in UI for `ToolUp.Platform`. A thin Fable / Feliz wrapper around `ToolUp.AuthProviders.Oidc.Client` that bakes in External ID's tenant-aware issuer construction, the `offline_access` scope default (required for refresh tokens against External ID), and optional sign-up / sign-in user-flow policy routing.

Wired into a deployment as a `CustomAuthUI` provider:

```fsharp
ClientConfig.AuthUI = CustomAuthUI {
    Wrap =
        EntraExternalIdAuthUI.wrap
            (EntraExternalIdClientConfig.create "<tenant>" "<client-id>" "<redirect-uri>")
}
```

Use this companion when targeting Entra External ID (customer-facing CIAM). For non-Entra OIDC providers, use `ToolUp.AuthProviders.Oidc.Client` directly.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
