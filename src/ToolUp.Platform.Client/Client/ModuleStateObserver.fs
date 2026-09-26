// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ModuleStateObserver

open System.Collections.Generic

let private log = Logger.forCategory "client.module-state"

// ─── Module state observer hook ──────────────────────────────────
//
// Lightweight publish/subscribe hook the shell calls after every
// module state change. Companion packages subscribe to mirror module
// state into their own registries without the shell needing a
// compile-time dependency on the companion. forge ships no
// subscriber out of the box; the seam stays in forge as substrate
// — any companion can plug in.
//
// Sanctioned mutable global — same precedent as
// `NotificationClient.state`. Per-tab singleton with effectively
// final lifetime; subscribers register at Client startup and never
// unsubscribe.
//
// Observers receive the module Id as a string and the (erased)
// Model as an `obj`. The companion is responsible for casting back
// to its known type. A module that wants its state readable without
// any companion declares a projector via `ClientModule.withInspectState`
// (Phase 536) — see `publishState` / `tryInspect` below.
//
// Observer exceptions are swallowed (with a console warning in
// browsers that have one) — a buggy companion must never break the
// shell's update loop.

/// Callback invoked after every module update. Receives the module
/// Id and the (erased) Model.
type ModuleStateObserver = string -> obj -> unit

let private observers = List<ModuleStateObserver>()

/// Register an observer. Call at Client startup from the companion
/// that wants to mirror module state. Re-registration of the same
/// callback is a no-op deliberately; the list grows on every register
/// call and the same callback added twice fires twice.
let register (observer: ModuleStateObserver) = observers.Add(observer)

/// Internal — called by the shell's update loop after each successful
/// module state transition. Iterates observers; swallows exceptions
/// so a single bad observer can't break the update path.
let publish (moduleId: string) (model: obj) =
    for observer in observers do
        try
            observer moduleId model
        with ex ->
            try
                log.Warn $"observer swallowed: {ex.Message}"
            with _ ->
                ()
// ─── Inspectable state (Phase 536) ──────────────────────────────
//
// The latest `UiStateReport` per module, for modules that declared a
// projector via `ClientModule.withInspectState`. Kept beside the
// observer list because the shell's publish points are the moments
// the report changes. The projection is stored LAZILY: the shell pays
// one dictionary write per update of a declared module, and the
// projector runs only when a reader asks — so the AI layer can read
// "what is on the user's screen" without re-projecting on every
// Elmish tick. A module with no projector never enters the table and
// pays nothing (GP 13).
//
// Same sanctioned-mutable-global precedent as `observers` above.

let private reports = Dictionary<string, Lazy<UiStateReport>>()

/// Phase 536 — the shell's publish point: record the module's latest
/// inspect-state report (when it declared a projector), then notify
/// every observer exactly as `publish` does. The report is recorded
/// first so an observer that reads `tryInspect` sees the new state.
let publishState (inspect: (obj -> UiStateReport) option) (moduleId: string) (model: obj) =
    match inspect with
    | Some project -> reports[moduleId] <- lazy (project model)
    | None -> ()

    publish moduleId model

/// Phase 536 — the latest inspect-state report the named module
/// published, or `None` when it declares no observable state (never
/// called `withInspectState`) or has not been updated since the shell
/// started. A projector that throws is reported as `None`, with a
/// warning — a buggy projection must never break its reader.
let tryInspect (moduleId: string) : UiStateReport option =
    match reports.TryGetValue moduleId with
    | true, report ->
        try
            Some report.Value
        with ex ->
            try
                log.Warn $"inspect-state projector for '{moduleId}' threw: {ex.Message}"
            with _ ->
                ()

            None
    | _ -> None