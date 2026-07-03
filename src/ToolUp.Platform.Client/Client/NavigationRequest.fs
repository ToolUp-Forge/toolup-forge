// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.NavigationRequest

open System.Collections.Generic

let private log = Logger.forCategory "client.navigation"

// ─── Shell-navigation request hook ──────────────────────────────
//
// Phase 6g.C: lightweight publish/subscribe hook that lets
// companion packages (e.g. a client-resident `navigate_to_page`
// AI tool) ask the shell to navigate without depending on the
// shell's internal `Msg` type. The shell subscribes once at init
// and translates each request into a `ModuleSelected` dispatch.
//
// Sanctioned mutable global — same precedent as
// `ModuleStateObserver`, `NotificationClient.handlers`. Per-tab
// singleton; subscribers register at Client startup and never
// unsubscribe (the shell's subscription lives for the page's
// lifetime).
//
// SidebarId format mirrors what the shell expects from a sidebar
// click: single-page modules use the bare module Id; multi-page
// modules use the composite `"{moduleId}{pageRoute}"` (the page
// route starts with `/` which acts as the separator). Callers
// build this from their `{ moduleId, pageRoute }` args.

/// Sidebar Id as produced by `ClientModule.Definition.Id` for
/// single-page modules, or `"{moduleId}{pageRoute}"` for multi-page.
type SidebarId = string

let private listeners = List<SidebarId -> unit>()

// `gate` guards every read/write of `listeners`. In the browser the
// bus is single-threaded (Fable compiles `lock` to a plain call of the
// body), so this costs nothing at runtime; on .NET it makes the
// registry safe under concurrent (un)subscription + `request` — the
// case Expecto's parallel test runner exercises, where one test
// enumerated the list while another mutated it ("Collection was
// modified…"). Symmetry with `ModuleEvents.fire`, which snapshots for
// the same reason.
let private gate = obj ()

/// Shell-side subscription. Returns a dispose thunk that removes
/// the callback (the shell never disposes — subscription lives
/// for the lifetime of the React tree).
let subscribe (callback: SidebarId -> unit) : unit -> unit =
    lock gate (fun () -> listeners.Add(callback))
    fun () -> lock gate (fun () -> listeners.Remove(callback) |> ignore)

/// Public — call from companion code to ask the shell to navigate.
/// Fires every subscribed callback in registration order. The
/// shell's subscription runs `dispatch (ModuleSelected sidebarId)`,
/// going through the same path a sidebar click takes.
///
/// No-op when no subscribers are registered — this is the case in
/// test harnesses that don't mount the full shell.
///
/// Fires against a snapshot so a callback that (un)subscribes during
/// delivery — or a concurrent test mutating the shared registry —
/// can't disturb the in-flight iteration.
let request (sidebarId: SidebarId) : unit =
    let snapshot = lock gate (fun () -> listeners.ToArray())

    for cb in snapshot do
        try
            cb sidebarId
        with ex ->
            try
                log.Warn $"subscriber swallowed: {ex.Message}"
            with _ ->
                ()