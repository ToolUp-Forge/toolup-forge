# Phase 5e — Per-tenant branding

**Status:** additive, backward-compatible, **no consumer action required.**

## What changes

Team-scoped deployments gain four per-team app-chrome overrides — application name, primary colour, logo, favicon — editable by an Owner/Admin on the **existing Platform Defaults tab** of the built-in Team Config UI. The SDK shell renders them with no deployment-side patching.

- **Shared (`ToolUp.Platform.Core`):** `ConfigKeys.BrandingKeys` (the four field keys `appName` / `primaryColor` / `logoUrl` / `faviconUrl`) and a new `Branding` record + `Branding.resolve` in `Shared/Types/BrandingTypes.fs`.
- **Server (`ToolUp.Platform.Server`):** `sdkBrandingFields` + `mergeBrandingSchema` in `PlatformSchema.fs`; `compose` merges them onto the reserved `_platform` schema **only when `DeploymentConfig.hasTeamScope config`** is true (any `Team` / `MultiTeam` surface).
- **Client (`ToolUp.Platform.Client`):** `BrandingProvider` (React context + `useBranding` hook) and `Components.BrandedHeader` (applies the favicon + `--brand-primary` CSS custom property). `SDK.Client.run` resolves `Branding` from the prefetched `_platform` config (`Model.PlatformConfig`) against `ClientConfig` defaults and feeds app name + logo into `Layout.AppShell`.

## Who is affected

| Deployment surface | Effect |
|---|---|
| `Team` / `MultiTeam` | Four new optional fields appear on the Platform Defaults config tab. Unset → renders exactly as before. |
| `Anonymous` / `Individual` / `AuthenticatedEphemeral` | **Nothing changes** — the schema merge is skipped; no fields, no cost (GP 13). |

## Consumer action

**None.** Every field is `Required = false` with a blank default; blank/absent resolves to the composition-root `ClientConfig` defaults (`AppName`, `AppLogo`), so an upgraded deployment that sets no overrides renders byte-for-byte identically.

To opt in, a team Owner/Admin edits the fields on the Platform Defaults tab — no redeploy, no code. The change is reflected on the next render and live on team switch.

## Notes

- `primaryColor` is validated as a `#RGB` / `#RRGGBB` hex string; a malformed stored value degrades to the default rather than emitting an invalid CSS variable.
- `--brand-primary` is a forward-looking design token — nothing in the SDK shell currently styles against it. Custom views / themes can consume it; future SDK theming knobs extend `ConfigKeys.BrandingKeys` + `sdkBrandingFields` + the `Branding` record rather than introduce a parallel surface.
- Any module view can read the active team's branding via `BrandingProvider.useBranding ()`.

## Rollback

Revert the phase commit. The four fields disappear from the config tab; any persisted `_platform` branding values become inert (ignored by the resolver, harmless in storage). No data migration required.

## Verification

- `dotnet build ToolUp.Forge.sln` — 0 errors.
- `cd samples/MinimalClient && dotnet fable -o output --noCache` — clean (validates the client tier incl. the `BrandedHeader` `[<Emit>]` shims).
- Manual: on a `Team` deployment, edit `appName` / `primaryColor` / `logoUrl` / `faviconUrl` on the Platform Defaults tab → next render reflects the sidebar name + logo, the browser-tab favicon, and the `--brand-primary` `:root` variable.
