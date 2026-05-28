// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.RegisteredModules

// ─── Per-tab snapshot of the modules registered with the shell ──
//
// Phase 6g.C: companion packages (e.g. a client-resident
// `navigate_to_page` AI tool) need to validate that a requested
// `(moduleId, pageRoute)` pair actually maps to something the shell
// recognises. The shell's own `ModuleSelected` handler silently
// no-ops on an unknown sidebar Id; without a snapshot here, a
// navigate executor would return success for any string the AI
// invented.
//
// Sanctioned mutable global — same precedent as
// `NavigationRequest.listeners` and `NotificationClient.handlers`.
// Per-tab singleton; the shell's `init` calls `publish` once with
// the full module list, and companions read via `snapshot ()` from
// the moment any navigation tool fires.

/// One module's published entry. `PageRoutes` is the list of valid
/// `pageRoute` values for multi-page modules (e.g. `["/dataset";
/// "/sku-analysis"; "/price-elasticity"]`); empty for single-page.
type ModuleEntry = {
    ModuleId: string
    ModuleName: string
    PageRoutes: string list
}

let mutable private entries: ModuleEntry list = []

/// Shell-side publish. Called once from `Client.init` with the
/// resolved `ErasedModule` list (post-RBAC / module-filter). Re-
/// publish is supported (e.g. when team membership changes the
/// accessible-modules set).
let publish (modules: ModuleEntry list) : unit = entries <- modules

/// Companion-side read. Returns the most recently published list,
/// or `[]` when the shell hasn't initialised yet (test harnesses
/// that don't mount the full shell).
let snapshot () : ModuleEntry list = entries