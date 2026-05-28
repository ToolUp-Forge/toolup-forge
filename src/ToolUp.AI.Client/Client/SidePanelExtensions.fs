// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.SidePanelExtensions

open System.Collections.Generic
open Feliz

// ─── Side-panel toolbar extension point ──────────────────────────
//
// Companion packages call `registerStreamingAction` from a module-
// load `do` block to add a button to the AI side-panel toolbar.
// The button renders alongside the built-in "✕ Cancel" whenever the
// AI is streaming — same gate as Cancel, no per-button visibility
// plumbing needed at this layer (the registered component decides
// what to render and when, e.g. a pause-style button can return
// `Html.none` once its companion's status flips to a paused phase).
//
// Sanctioned mutable global — same precedent as
// `ClientToolRuntime.registry` and `NotificationClient.handlers`.
// Per-tab singleton with effectively-final lifetime: registrations
// happen once at module load, the list is read on every render of
// the streaming-state toolbar.

let private streamingActions = ResizeArray<unit -> ReactElement>()

/// Register a thunk that ConversationPanel will invoke to render a
/// button in the side-panel toolbar while the AI is streaming.
let registerStreamingAction (thunk: unit -> ReactElement) : unit = streamingActions.Add(thunk)

/// Materialise the registered streaming actions for the current
/// render. Invoked from ConversationPanel; returns a fresh list so
/// React diffs stably across renders.
let getStreamingActions () : ReactElement list = [ for thunk in streamingActions -> thunk () ]