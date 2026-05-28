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
// `NotificationClient.handlers`. Per-tab singleton with effectively
// final lifetime; subscribers register at Client startup and never
// unsubscribe.
//
// Observers receive the module Id as a string and the (erased)
// Model as an `obj`. The companion is responsible for casting back
// to its known type — a projector with signature `Model -> Snapshot`
// can be wrapped by `withAIObservableState` so the cast happens once
// per registration.
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