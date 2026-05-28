// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.FastPathBridge

open System
open ToolUp.AI

// ─── Phase 6j.A — chat-send fast-path bridge ────────────────────
//
// The chat-send paths in `AIAssistantUI` and `AIClientConfig` need
// to ask "is this a trivial instruction that resolves without an
// LLM round-trip?" This module exposes a registration seam for any
// downstream resolver to plug in via `setResolver`; if no resolver
// registers, every chat message goes straight to the existing API
// call path. forge ships no resolver out of the box; consumers
// register their own (typically a companion that drives AI tool
// dispatch). forge stays free of orchestration imports.
//
// Sanctioned mutable global — same precedent as
// `ClientToolRuntime.registry` and `NotificationClient.handlers`.

/// Outcome the bridge surfaces to the chat-send call sites.
/// Carries everything the call site needs to short-circuit the
/// API call: the synthetic assistant turn to append, plus thunks
/// for the side effects (dispatching the typed Msg locally and
/// firing the audit beacon).
type ResolveOutcome =
    | NoResolution
    | Resolved of syntheticAssistant: ConversationMessage * dispatchAction: (unit -> unit) * sendBeacon: (unit -> unit)

/// Request shape — mirrors the resolver's `ChatRequest` but stays
/// in ToolUp.AI's namespace so the bridge surface is self-
/// contained.
type FastPathRequest = {
    ConversationId: Guid
    Instruction: string
    ActiveModule: string option
    ActivePage: string option
}

// Per-tab resolver cache, populated once at boot by
// `AIClientConfig.run` (and by extension `Client.run` via the AI
// wrapper) from `AIClientConfig.FastPathResolver`. A downstream
// fast-path companion exports a `FastPathRequest -> ResolveOutcome`
// value; consumers add it to `AIClientConfig.FastPathResolver` to
// enable fast-path resolution. `None` = no resolver wired (the
// agent loop runs unchanged). Sole writer is the AI-tier boot path;
// downstream companions never touch this directly.
let mutable private resolver: (FastPathRequest -> ResolveOutcome) option = None

/// Called once by `AIClientConfig.run` with the value from
/// `AIClientConfig.FastPathResolver`. Idempotent re-installs are
/// supported but in practice only happen if a test harness re-runs
/// the boot sequence.
let setResolver (r: (FastPathRequest -> ResolveOutcome) option) : unit = resolver <- r

/// Chat-send call sites invoke this. Returns `NoResolution` when no
/// companion has registered a resolver, OR when the registered
/// resolver returns `NoResolution` (no pattern match, ambiguous,
/// paused).
let tryResolve (request: FastPathRequest) : ResolveOutcome =
    match resolver with
    | Some r ->
        try
            r request
        with _ ->
            // Resolver failure must not break the chat flow. Fall
            // through to the existing API call.
            NoResolution
    | None -> NoResolution