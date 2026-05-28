# ToolUp.AuthProviders.EntraExternalId

Microsoft Entra External ID server-side `IAuthProvider` for `ToolUp.Platform`. An opinionated wrapper around `ToolUp.AuthProviders.Oidc` that bakes in External ID's tenant-aware issuer construction, the `oid > sub` user-id claim convention, the `tid` tenant claim, and sign-up / sign-in user-flow policy routing.

Use this companion when targeting Entra External ID (customer-facing CIAM). For non-Entra OIDC providers, use `ToolUp.AuthProviders.Oidc` directly with raw config.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
