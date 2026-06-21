// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.BrandingProvider

open Feliz

// ─── Per-tenant branding React context (Phase 5e) ─────────────────
//
// Surfaces the active team's resolved `Branding` (app name, primary
// colour, logo, favicon) to the shell chrome and any module view. The
// shell resolves `Branding` once per render from the prefetched
// `_platform` config (`Model.PlatformConfig`) against the composition
// root's `ClientConfig` defaults, then provides it here. Resolution is
// live on team switch because `TeamSwitched` clears `PlatformConfig`
// and the `ConfigsLoaded` reload repopulates it.
//
// Mirrors the idiomatic context path used by `FeatureFlags` /
// `LoadingIndicatorContext` / `ProcessedDataContext`: a React context
// the shell provides at the view-tree root and components read via a
// hook, rather than threading `Branding` through props or the Feliz-
// free `ClientModuleContext`.

/// Neutral default used when a component reads the context outside any
/// provider (isolated render, tests). The shell always provides a real
/// value resolved from `ClientConfig`, so this is only a degraded
/// fallback — it names nothing brand-specific.
let private fallback: Branding = {
    AppName = "App"
    PrimaryColor = Branding.DefaultPrimaryColor
    LogoUrl = "favicon.png"
    FaviconUrl = "favicon.png"
    PaletteOverrides = []
}

let private context = React.createContext<Branding> (defaultValue = fallback)

/// Wrap children in a provider supplying the resolved branding. Used by
/// the shell; deep views read it via `useBranding`.
let provider (branding: Branding) (children: ReactElement) = context.Provider(branding, children)

/// Hook — returns the active team's resolved `Branding`. Must be called
/// from a React component body (e.g. a `[<ReactComponent>]`-attributed
/// function); returns the neutral fallback outside a provider.
let useBranding () : Branding = React.useContext context