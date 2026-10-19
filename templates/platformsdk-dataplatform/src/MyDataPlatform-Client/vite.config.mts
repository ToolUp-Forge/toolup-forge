import { defineConfig } from "vite";

// Phase 11.G + Phase 16e — build-time defines consumed by the SDK's
// `ToolUp.Platform.BundleConstants` typed accessors. Each value is
// emitted as a literal in the bundle; `JSON.stringify` quotes it so
// the accessor's `typeof X === 'string'` guard holds. Leave an env
// var unset to opt out of the corresponding accessor — the SDK's
// accessor returns `''` (Phase 11.G shape) or `None` (Phase 16e
// shape) and the consumer's fail-loud branch decides what to do.
//
// Phase 11.G — typed-accessor returns raw `string` (empty when unset):
//   __TOOLUP_MODULE__              → BundleConstants.moduleFilter
//   __AG_GRID_LICENSE__            → BundleConstants.agGridLicense
//   __CLERK_PUBLISHABLE_KEY__      → BundleConstants.clerkPublishableKey
//   __TOOLUP_PLATFORM_SURFACES__   → BundleConstants.platformSurfaces
// Phase 58 — typed-accessor returns `bool` (false when unset):
//   __TOOLUP_NOTIFICATIONS_DISABLED__ → BundleConstants.notificationsDisabledExplicitly
// Phase 16e — typed-accessor returns `string option` (None when unset):
//   __ENTRA_TENANT_ID__            → BundleConstants.entraTenantId
//   __ENTRA_CLIENT_ID__            → BundleConstants.entraClientId
//   __OIDC_ISSUER_OVERRIDE__       → BundleConstants.oidcIssuerOverride
//   __OIDC_AUDIENCE_OVERRIDE__     → BundleConstants.oidcAudienceOverride
export default defineConfig({
  server: {
    port: 8080,
    proxy: {
      "/api": "http://localhost:5000"
    }
  },
  define: {
    __TOOLUP_MODULE__: JSON.stringify(process.env.TOOLUP_MODULE ?? ""),
    __AG_GRID_LICENSE__: JSON.stringify(process.env.AG_GRID_LICENSE ?? ""),
    __CLERK_PUBLISHABLE_KEY__: JSON.stringify(process.env.CLERK_PUBLISHABLE_KEY ?? ""),
    __TOOLUP_PLATFORM_SURFACES__: JSON.stringify(process.env.TOOLUP_PLATFORM_SURFACES ?? ""),
    __TOOLUP_NOTIFICATIONS_DISABLED__: JSON.stringify(process.env.TOOLUP_NOTIFICATIONS_DISABLED === "true"),
    __ENTRA_TENANT_ID__: JSON.stringify(process.env.ENTRA_TENANT_ID ?? ""),
    __ENTRA_CLIENT_ID__: JSON.stringify(process.env.ENTRA_CLIENT_ID ?? ""),
    __OIDC_ISSUER_OVERRIDE__: JSON.stringify(process.env.OIDC_ISSUER_OVERRIDE ?? ""),
    __OIDC_AUDIENCE_OVERRIDE__: JSON.stringify(process.env.OIDC_AUDIENCE_OVERRIDE ?? "")
  }
});
