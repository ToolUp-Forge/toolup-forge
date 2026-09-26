// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module MinimalClient.UiAwarenessSample

// ─── Phase 539 — a small module wired for UI awareness ────────────────
//
// Companion worked example to `ToolUp.AI.SampleClientTool` (Phase 46.B):
// that companion shows the ARGUMENT-taking `ClientResident` tool shape —
// the model sends `{op, a, b}`, the browser computes and answers. This
// module shows the READ-ONLY, no-argument shape: a module declares its
// live UI state with `ClientModule.withInspectState` (Phase 536), and
// the `ToolUp.AI.UiAwareness` companion's `_platform.ui.inspect_active_module`
// tool answers "what is on the user's screen right now" from that
// declaration — no module-specific tool, no argument, no DOM walk.
//
// Installing both companions' client executors here — this file and
// `ToolUp.AI.SampleClientTool.Client.SampleHandler` (referenced from
// `ToolUp.AI.SampleClientTool.Server/Server/Compose.fs`'s header
// comment) — puts the two `ClientResident` shapes side by side for a
// reader comparing them. See `docs/ai/ui-awareness.md` for the full
// walkthrough of the seam this module exercises.
//
// This file is compiled by MinimalClient's `dotnet fable -o output`
// smoke test (see `MinimalClient.fsproj`), which is what proves the
// companion's client executor — `InspectActiveModule.install` below,
// and the `withInspectState` projection it reads — actually transpiles
// through the real Fable + Feliz + ToolUp.Elmish toolchain. There is no
// live LLM or browser session in this environment to drive the tool
// end-to-end (that would additionally need a running module shell +
// AI server + model), so this sample proves the compile-time half of
// the contract; `docs/ai/ui-awareness.md` documents the runtime half.

open Feliz
open ToolUp.Elmish
open ToolUp.Platform

/// A deliberately tiny model: one text field a user might filter by,
/// and a running count of how many times they refreshed. Enough to
/// give `withInspectState` two real fields and one enabled/disabled
/// action to project — not a working search, just the shape.
type Model = { Filter: string; RefreshCount: int }

type Msg =
    | SetFilter of string
    | Refresh

let private init () : Model * Cmd<Msg> =
    { Filter = ""; RefreshCount = 0 }, Cmd.none

let private update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | SetFilter text -> { model with Filter = text }, Cmd.none
    | Refresh ->
        {
            model with
                RefreshCount = model.RefreshCount + 1
        },
        Cmd.none

/// The `'Model -> UiStateReport` projection `withInspectState` takes.
/// Fields are JSON-encoded leaves (Phase 536's contract) — a string
/// field carries its own quotes via `JSON.stringify`, a number is its
/// bare decimal text. `refresh` is enabled once the user has typed a
/// filter, matching the button's own `disabled` state in `view` below
/// (Fable.Core.JS.JSON reused so the two never drift independently).
let private project (model: Model) : UiStateReport =
    UiStateReport.empty
    |> UiStateReport.withField "filter" (Fable.Core.JS.JSON.stringify model.Filter)
    |> UiStateReport.withField "refreshCount" (string model.RefreshCount)
    |> UiStateReport.withAction "refresh" (not (System.String.IsNullOrWhiteSpace model.Filter))

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement =
    let left =
        Html.div [
            Html.h2 [ prop.text "UI awareness sample" ]
            Html.input [
                prop.value model.Filter
                prop.placeholder "Type a filter…"
                prop.onChange (fun (v: string) -> dispatch (SetFilter v))
            ]
            Html.button [
                prop.text "Refresh"
                prop.disabled (System.String.IsNullOrWhiteSpace model.Filter)
                prop.onClick (fun _ -> dispatch Refresh)
            ]
        ]

    let right =
        Html.div [
            prop.text (
                sprintf
                    "filter=%s refreshCount=%d — inspectable via _platform.ui.inspect_active_module"
                    model.Filter
                    model.RefreshCount
            )
        ]

    left, right

/// Registers as a `DebugOnly` module — reference-only, like
/// `ToolUp.AI.SampleClientTool`, never meant to ship in a production
/// sidebar. Not wired into this sample's `Program.mkProgram` shell
/// (`Client.fs` runs a bare Elmish loop, not the `ClientConfig` module
/// shell `withInspectState` reports through) — `register ()` exists so
/// the module-registration + `withInspectState` chain compiles and is
/// reachable, matching how `ToolUp.AI.SampleClientTool.Server.Compose`
/// documents its own reference-only, not-wired-by-default posture.
let register () : ErasedModule =
    ClientModule.create {
        Init = init
        Update = update
        Name = "UI Awareness Sample"
        Icon = Html.none
    }
    |> ClientModule.withView view
    |> ClientModule.withAvailability DebugOnly
    |> ClientModule.withInspectState project
    |> ClientModule.register

/// Install the UI-awareness companion's client executor — the sibling
/// call to `ToolUp.AI.SampleClientTool.Client.SampleHandler.install ()`
/// a real deployment composing both companions would also make at
/// client boot. Referencing it here is what drives
/// `ToolUp.AI.UiAwareness.Client` through this sample's Fable compile.
let installed = ToolUp.AI.UiAwareness.Client.InspectActiveModule.install ()