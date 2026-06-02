import { defineConfig } from "vite";

// SAFER vite config — matches platformsdk-solution's shape so consumers
// can adopt the production posture without re-learning bundle-constant
// wiring. See toolup-forge/docs/platform/composition-roots.md for the
// full `__TOOLUP_*__` accessor surface.
//
// `__TOOLUP_PLATFORM_SURFACES__` is the load-bearing one for SAFER:
// the SDK shell uses this to pick the client-side auth UI shape at
// boot. Default unset → "anonymous" — single-shape, no auth screens.

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
