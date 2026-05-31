# ToolUp.AuthProviders.EntraExternalId

> [!WARNING]
> **Deprecated in 0.4.0** — for new deployments the generic `ToolUp.AuthProviders.Oidc` server-side provider, paired with an `OidcAppConfig` produced by `OidcPresets.entraExternalId` on the client side, is the recommended path. The unified-config + preset model from 0.4.0 replaces the per-companion server/client split for Entra External ID.
>
> This companion stays compiling for one minor cycle (consumer migration window) and is scheduled for removal at `0.Y.0`. Continue using it when you specifically need the **`oid` → `UserId` / `tid` → `TenantId` claim-mapping decorator** that the generic `OidcAuthProvider` does not yet expose as a first-class option — the External ID single-call preset assumes the consumer's downstream code is happy with the generic OIDC `sub` → `UserId` shape.
>
> See [`docs/migrations/0.4.0-entra-external-id-deprecation.md`](../../docs/migrations/0.4.0-entra-external-id-deprecation.md) for the migration walk-through.

Microsoft Entra External ID server-side `IAuthProvider` for `ToolUp.Platform`. An opinionated wrapper around `ToolUp.AuthProviders.Oidc` that bakes in External ID's tenant-aware issuer construction, the `oid > sub` user-id claim convention, the `tid` tenant claim, and sign-up / sign-in user-flow policy routing.

Use this companion when targeting Entra External ID (customer-facing CIAM) and requiring the `oid`/`tid` claim-mapping surface. For sign-in-only flows that can tolerate the generic OIDC `sub` → `UserId` shape, prefer the 0.4.0 `OidcPresets.entraExternalId` + generic `OidcAuthProvider` path.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
