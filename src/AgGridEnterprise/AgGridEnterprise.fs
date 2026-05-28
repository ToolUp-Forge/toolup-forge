// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module AgGridEnterprise

open Fable.Core
open Fable.Core.JsInterop

let mutable private registered = false

/// Whether AG Grid Enterprise has been registered.
let isRegistered () = registered

/// Empty / whitespace license key surfaces as a `console.warn` rather
/// than a hard `failwith`. Vite-injected license keys (see the
/// reference app's `vite.config.mts` `__AG_GRID_LICENSE__` define) may
/// resolve empty when the env var is unset — typically a fresh clone
/// or a deployment that hasn't wired its CI secret store. Crashing
/// app boot in that case is hostile; the cleaner degraded path lets
/// the app boot in Community-tier mode and shows AG Grid's own
/// "License Required" overlay only on Enterprise-using grids.
let private validateLicenseKey (licenseKey: string) =
    if System.String.IsNullOrWhiteSpace licenseKey then
        Browser.Dom.console.warn (
            "AgGridEnterprise called with an empty license key. The app will boot, but \
             Enterprise features (Server-side Row Model, Master/Detail, Set Filter, etc.) \
             will show 'License Required' overlays on grids that use them. \
             To fix: set the AG_GRID_LICENSE_KEY env var (or `.env.local` for local dev), \
             or remove the AgGridEnterprise companion package to use Community features only."
        )

// ─── Module-level Enterprise setup ───────────────────────────────────────────
// Mirrors the working main branch App.fs top-level pattern exactly.
//
// Fable (ESM output) always hoists `import "X" "Y"` to the top of the
// compiled file regardless of where it appears in F# source, so placing them
// inside function bodies gives no tree-shaking benefit and only defers the
// *setup calls* (.with, registerModules). AG Charts Enterprise animations
// depend on hooks installed during AgChartsEnterpriseModule.setup() (called
// internally by IntegratedChartsModule.with()). Those hooks must be in place
// before the first AgCharts component is created; deferring them to a function
// that runs "just before Client.run" is not guaranteed to be early enough for
// all AG Charts internal initialization paths.
//
// By this point in the compiled bundle, ToolUp.Platform modules (AgGrid.fs,
// AgChart.fs) have already been evaluated, so setGridModulesRegistered and
// setChartsModulesRegistered are available.

let private allEnterpriseModules: obj =
    import "AllEnterpriseModule" "ag-grid-enterprise"

let private integratedChartsModule: obj =
    import "IntegratedChartsModule" "ag-grid-enterprise"

let private licenseManager: obj = import "LicenseManager" "ag-grid-enterprise"

let private agChartsEnterpriseModule: obj =
    import "AgChartsEnterpriseModule" "ag-charts-enterprise"

let private gridModuleRegistry: obj = import "ModuleRegistry" "ag-grid-community"

// .with() calls AgChartsEnterpriseModule.setup() which installs Enterprise
// animation hooks and sets Integrated mode in the AG Charts core registry.
let private integratedChartsWithCharts: obj =
    emitJsExpr (integratedChartsModule, agChartsEnterpriseModule) "$0.with($1)"

// Register AG Grid Enterprise modules (ag-grid-react checks the Community registry).
do gridModuleRegistry?registerModules ([| allEnterpriseModules; integratedChartsWithCharts |])

// Suppress Community fallback registrations in Platform SDK (AgGrid.grid and
// AgChart.chart each guard their own ensureXxxModulesRegistered calls with these flags).
do ToolUp.Platform.AgGrid.setGridModulesRegistered ()
do ToolUp.Platform.AgChart.setChartsModulesRegistered ()

// ─── Variant B (recommended): AgGridProvider ─────────────────────
// Returns an AgGridModuleConfig for use with ClientConfig.GridModules.
// Module registration already happened above; this just captures the module
// list for AgGridProvider's React Context and sets the license key.

/// Get an AgGridModuleConfig with Enterprise grid modules.
/// Pass the result to ClientConfig.GridModules.
let gridModuleConfig (licenseKey: string) : ToolUp.Platform.AgGrid.AgGridModuleConfig =
    validateLicenseKey licenseKey
    licenseManager?setLicenseKey licenseKey

    {
        Modules = [| allEnterpriseModules; integratedChartsWithCharts |]
        LicenseKey = Some licenseKey
    }

/// No-op — chart modules are now registered at module evaluation time.
/// Kept for backward compatibility with code that calls registerCharts() after gridModuleConfig().
let registerCharts () = ()

// ─── Variant A (classic): Global registration ────────────────────

/// Set the AG Grid + AG Charts Enterprise license key. Call once before the Elmish program mounts.
///
/// Module registration (AllEnterpriseModule, IntegratedChartsModule.with(AgChartsEnterpriseModule))
/// now happens at module evaluation time rather than inside this function, matching the working
/// main branch pattern. This function only sets the license key and idempotency flag.
let register (licenseKey: string) =
    if registered then
        ()
    else
        validateLicenseKey licenseKey
        licenseManager?setLicenseKey licenseKey
        registered <- true