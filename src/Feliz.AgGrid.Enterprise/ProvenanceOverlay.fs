// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module AgGridEnterpriseProvenance

// Phase 12d — Enterprise activation for the value-provenance overlay.
//
// The provenance overlay substrate itself (types, the reusable
// `ProvenanceOverlay` tooltip component, the `ColumnDef.provenance`
// factory, the click seam) lives Community-side in
// `ToolUp.Platform.CellProvenance` — AG Grid tooltips, including custom
// `tooltipComponent`, are a Community feature, and the substrate follows
// the same erased-member pattern as `AgGrid.Enterprise.fs`. What is
// genuinely gated on this companion is whether the overlay *renders*: the
// Community substrate keeps `isProvenanceOverlayEnabled () = false` until
// this companion flips it, so a deployment on AG Grid Community collects
// provenance metadata but shows no overlay (the phase's graceful no-op).
//
// `AgGridEnterprise.fs` calls `activate ()` at module-evaluation time,
// alongside `setGridModulesRegistered` / `setChartsModulesRegistered`, so
// the overlay is live by the time the first grid renders.

/// Enable provenance-overlay rendering. Idempotent — mirrors
/// `AgGrid.setGridModulesRegistered`.
let activate () =
    ToolUp.Platform.CellProvenance.setProvenanceOverlayEnabled ()