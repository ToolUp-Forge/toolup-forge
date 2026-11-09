// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.FastPathBridge

open System
open ToolUp.AI

// ─── Chat-send fast-path bridge ──────────────────────────────────
//
// Both chat surfaces ask "is this a trivial instruction that resolves
// without an LLM round-trip?" before a message leaves the page. This module
// is the registration seam a downstream resolver plugs into; with none
// registered every chat message goes straight to the API call. forge ships
// no resolver; consumers register their own (typically a companion that
// drives AI tool dispatch), so forge stays free of orchestration imports.
//
// A resolver may answer synchronously (`setResolver`) or asynchronously
// (`setAsyncResolver`), for a tier whose answer needs I/O such as an
// embedding lookup — under Fable a synchronous function cannot wait on an
// `Async`. The surfaces consult `resolve`, which awaits either shape without
// blocking the UI and gives up after `ResolveTimeoutMs`.
//
// Sanctioned mutable global — same precedent as
// `ClientToolRuntime.registry` and `NotificationClient.state`.

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

/// How long the surfaces wait for a resolver before the message takes the
/// model path. A resolver that answers later is ignored: the turn has
/// already gone to the agent.
[<Literal>]
let ResolveTimeoutMs = 2_000

// Per-tab resolver, installed once at boot by the composition that
// registers a fast-path companion. `None` = no resolver wired (the agent
// loop runs unchanged). Held in its asynchronous form; a synchronous
// resolver is lifted on the way in.
let mutable private resolver: (FastPathRequest -> Async<ResolveOutcome>) option =
    None

/// Install a synchronous resolver (or `None` to remove it). Idempotent
/// re-installs are supported but in practice only happen if a test
/// harness re-runs the boot sequence.
let setResolver (r: (FastPathRequest -> ResolveOutcome) option) : unit =
    resolver <- r |> Option.map (fun sync -> fun request -> async { return sync request })

/// Install an asynchronous resolver (or `None` to remove it): one whose
/// answer needs I/O. It replaces any resolver installed before it.
let setAsyncResolver (r: (FastPathRequest -> Async<ResolveOutcome>) option) : unit = resolver <- r

/// The chat surfaces consult this before submitting. `NoResolution` when no
/// resolver is installed, when the resolver declines, when it fails (a
/// resolver failure must not break the chat flow), or when it has not
/// answered within `ResolveTimeoutMs`.
let resolve (request: FastPathRequest) : Async<ResolveOutcome> = async {
    match resolver with
    | None -> return NoResolution
    | Some r ->
        try
            let! answer = Async.StartChild(r request, ResolveTimeoutMs)
            return! answer
        with _ ->
            return NoResolution
}