// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.UiAwareness

// ─── Phase 537 — UI-awareness companion: the names both tiers share ──
//
// The server tier registers the tool definition under `InspectActiveModuleToolName`
// and the client tier registers its executor under the same name with
// `ClientToolRuntime.register`. A name spelled twice is a dispatch that
// silently answers `UnknownClientTool`, so both tiers read it from here.
//
// The result vocabulary lives here too: the `status` values are part of
// the tool's contract with the model, and a server-side test (or a
// simulator standing in for the browser) must be able to name them
// without referencing the Fable client tier.

/// The client-resident tool that reads the active module's latest
/// `UiStateReport` (Phase 536). The dotted form reaches the provider as
/// `_platform_ui_inspect_active_module` (the registry sanitises `.` to `_`).
[<Literal>]
let InspectActiveModuleToolName = "_platform.ui.inspect_active_module"

/// The SDK-reserved source the tool is registered under. `_`-prefixed,
/// so the per-module RBAC filter treats it as a reserved source: exempt
/// unless a deployment names it in its permission map, in which case the
/// caller needs `Read` on it like on any module.
[<Literal>]
let UiAwarenessSourceModule = "_platform.ui"

/// The `status` values the tool returns instead of a snapshot.
[<RequireQualifiedAccess>]
module InspectStatus =
    /// The request carried no active module: the user is not viewing one.
    [<Literal>]
    let NoActiveModule = "no-active-module"

    /// The active module declares no observable state (it never called
    /// `ClientModule.withInspectState`), has not published since the shell
    /// started, or its projector threw.
    [<Literal>]
    let NoObservableState = "no-observable-state"