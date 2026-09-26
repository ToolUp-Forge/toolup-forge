// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.UiAwareness.Client.ListActiveActions

// ─── Phase 542 — the browser half of `_platform.ui.list_active_actions` ──
//
// The agent loop emits a `ClientToolInvoke` SSE event for the tool; the
// SDK's `ClientToolRuntime` looks the executor up by name and runs it with
// the request's active module. The executor reads that module's latest
// `UiStateReport` (Phase 536) from `ModuleStateObserver` — the same reader
// `InspectActiveModule` uses — and returns only its `Actions` projected to
// a list. Read-only: it touches no module state and dispatches no message.
//
// Result shapes (the contract the tool description states to the model):
//
//   { "moduleId": "...", "actions": [ { "id": "...", "enabled": true|false }, ... ] }
//   { "status": "no-active-module" }
//   { "status": "no-observable-state", "moduleId": "..." }
//
// An `actions` list is empty, never omitted, when the module declares no
// inspectable actions — that is a real answer ("nothing to report"), not a
// missing one.

open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform
open ToolUp.AI.Client
open ToolUp.AI.UiAwareness

// Built as plain JS objects and serialised with `JSON.stringify`, which
// escapes every string it writes (`Fable.SimpleJson.SimpleJson.toString`
// does not escape `JString` text, so a module id carrying a quote would
// break the envelope) — same convention as `InspectActiveModule`.

let private objectOf (entries: (string * obj) seq) : obj = createObj entries

/// `report.Actions`, walked in sorted key order for a deterministic
/// answer, as `[{ id, enabled }]`.
let private actionsJson (report: UiStateReport) : obj =
    [|
        for KeyValue(name, enabled) in report.Actions -> objectOf [ "id", box name; "enabled", box enabled ]
    |]
    |> box

/// The tool's answer for a request context, reading reports through
/// `tryInspect`. Pure over its reader, so a test can supply a table; the
/// installed executor passes `ModuleStateObserver.tryInspect`.
let respond (tryInspect: string -> UiStateReport option) (context: ClientToolRuntime.ClientToolContext) : string =
    let result =
        match context.ActiveModule |> Option.filter (System.String.IsNullOrWhiteSpace >> not) with
        | None -> objectOf [ "status", box InspectStatus.NoActiveModule ]
        | Some moduleId ->
            match tryInspect moduleId with
            | None -> objectOf [ "status", box InspectStatus.NoObservableState; "moduleId", box moduleId ]
            | Some report -> objectOf [ "moduleId", box moduleId; "actions", actionsJson report ]

    JS.JSON.stringify result

/// The executor `ClientToolRuntime` runs. Tuple input, per the runtime's
/// Fable-currying note. The tool takes no arguments, so `argsJson` is
/// ignored: the module inspected is the request's active module, never one
/// the model names.
let executor (context: ClientToolRuntime.ClientToolContext, _argsJson: string) : Async<string> = async {
    return respond ModuleStateObserver.tryInspect context
}

/// Install the executor. Call once at client boot, alongside the
/// server-side `ToolUp.AI.UiAwareness.Server.AICompose.register` and
/// `InspectActiveModule.install`. Idempotent: `ClientToolRuntime.register`
/// overwrites by name.
let install () : unit =
    ClientToolRuntime.register ListActiveActionsToolName executor