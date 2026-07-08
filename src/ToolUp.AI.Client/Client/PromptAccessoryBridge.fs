// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.PromptAccessoryBridge

open Feliz

// ─── Phase 518 — chat prompt-box accessory bridge ────────────────
//
// The chat input row in `ConversationPanel` can host one optional
// *accessory* alongside the textarea + Send button — a mic toggle, an
// attach-file button, whatever a companion wants to plug in. This module
// exposes a registration seam, mirroring the `FastPathBridge` idiom: a
// companion registers a renderer via `setAccessory`; if none registers,
// the input row renders exactly as before (zero visual change, GP 13).
//
// The seam keeps `ToolUp.AI.Client` free of any dependency on the
// companion that supplies the accessory — the coupling is one-directional
// (the accessory package references AI.Client for this bridge, never the
// reverse). forge ships no accessory out of the box; the Voice client
// companion registers its mic here via a one-line opt-in.
//
// Sanctioned mutable global — same precedent as `FastPathBridge.resolver`,
// `ClientToolRuntime.registry`, and `NotificationClient.handlers`.

/// Everything a registered accessory needs to read and drive the chat
/// input without owning its `React.useState`. The input text lives in
/// `ConversationPanel`; the accessory reads the live draft, replaces it
/// (e.g. to stream interim transcription in), or submits it (as if the
/// user pressed Enter).
type PromptAccessoryContext = {
    /// The current draft text in the prompt input.
    CurrentText: string
    /// Replace the draft text — used to stream provisional recognised
    /// text into the input before it is committed.
    SetText: string -> unit
    /// Commit and send the given text (identical to pressing Enter). The
    /// caller passes the text to send (usually the committed transcript
    /// appended to whatever was already typed).
    Submit: string -> unit
    /// Whether an AI response is currently streaming. An accessory may
    /// disable itself while the assistant is busy.
    IsBusy: bool
}

// Populated once by a companion's opt-in (e.g.
// `VoiceInput.registerPromptMic`). `None` = no accessory wired, the
// default — the input row renders unchanged.
let mutable private accessory: (PromptAccessoryContext -> ReactElement) option =
    None

/// Register the prompt-input accessory renderer. Idempotent re-installs
/// are supported (a test harness re-running boot, or a later opt-in
/// replacing an earlier one).
let setAccessory (renderer: (PromptAccessoryContext -> ReactElement) option) : unit = accessory <- renderer

/// Whether an accessory is currently registered — lets a call site skip
/// laying out the accessory slot entirely when none is wired.
let hasAccessory () : bool = accessory.IsSome

/// The input row calls this to render the accessory. Returns `None` when
/// no companion has registered one. A renderer that throws collapses to
/// `None` so a misbehaving accessory can never break the chat input.
let render (ctx: PromptAccessoryContext) : ReactElement option =
    match accessory with
    | Some renderer ->
        try
            Some(renderer ctx)
        with _ ->
            None
    | None -> None