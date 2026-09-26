// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.UiAwareness.Client.InspectActiveModule

// ─── Phase 537 — the browser half of `_platform.ui.inspect_active_module` ──
//
// The agent loop emits a `ClientToolInvoke` SSE event for the tool; the
// SDK's `ClientToolRuntime` looks the executor up by name and runs it
// with the request's active module and page. The executor reads that
// module's latest `UiStateReport` from `ModuleStateObserver` (Phase 536)
// and returns it. Read-only: it touches no module state and dispatches no
// message.
//
// Result shapes (the contract the tool description states to the model):
//
//   { "moduleId": "...", "activePage": "..." | null,
//     "snapshot": { "fields": {..}, "selections": {..}, "actions": {..} } }
//   { "status": "no-active-module" }
//   { "status": "no-observable-state", "moduleId": "..." }
//
// A report's field values are JSON-encoded leaves by contract, so they are
// embedded as JSON rather than as strings-of-JSON; a value that does not
// parse is embedded as the string it is, never dropped and never able to
// break the envelope.

open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform
open ToolUp.AI.Client
open ToolUp.AI.UiAwareness

// Built as plain JS objects and serialised with `JSON.stringify`, which
// escapes every string it writes. (`Fable.SimpleJson.SimpleJson.toString`
// does not escape `JString` text, so a field value or module id carrying a
// quote would break the envelope.) Key order is insertion order; each map
// is walked in its sorted key order, so the output is deterministic.

let private leaf (encoded: string) : obj =
    try
        JS.JSON.parse encoded
    with _ ->
        box encoded

let private objectOf (entries: (string * obj) seq) : obj = createObj entries

let private snapshotJson (report: UiStateReport) : obj =
    objectOf [
        "fields", objectOf [ for KeyValue(name, value) in report.Fields -> name, leaf value ]
        "selections",
        objectOf [
            for KeyValue(name, keys) in report.Selections -> name, box (List.toArray keys)
        ]
        "actions", objectOf [ for KeyValue(name, enabled) in report.Actions -> name, box enabled ]
    ]

/// The tool's answer for a request context, reading reports through
/// `tryInspect`. Pure over its reader, so a test can supply a table;
/// the installed executor passes `ModuleStateObserver.tryInspect`.
let respond (tryInspect: string -> UiStateReport option) (context: ClientToolRuntime.ClientToolContext) : string =
    let result =
        match context.ActiveModule |> Option.filter (System.String.IsNullOrWhiteSpace >> not) with
        | None -> objectOf [ "status", box InspectStatus.NoActiveModule ]
        | Some moduleId ->
            match tryInspect moduleId with
            | None -> objectOf [ "status", box InspectStatus.NoObservableState; "moduleId", box moduleId ]
            | Some report ->
                objectOf [
                    "moduleId", box moduleId
                    "activePage",
                    (match context.ActivePage with
                     | Some page -> box page
                     | None -> null)
                    "snapshot", snapshotJson report
                ]

    JS.JSON.stringify result

/// The executor `ClientToolRuntime` runs. Tuple input, per the runtime's
/// Fable-currying note. The tool takes no arguments, so `argsJson` is
/// ignored: the module inspected is the request's active module, never
/// one the model names.
let executor (context: ClientToolRuntime.ClientToolContext, _argsJson: string) : Async<string> = async {
    return respond ModuleStateObserver.tryInspect context
}

/// Install the executor. Call once at client boot, alongside the
/// server-side `ToolUp.AI.UiAwareness.Server.AICompose.register`.
/// Idempotent: `ClientToolRuntime.register` overwrites by name.
let install () : unit =
    ClientToolRuntime.register InspectActiveModuleToolName executor