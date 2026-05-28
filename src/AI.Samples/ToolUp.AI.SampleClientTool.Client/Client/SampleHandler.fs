// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.SampleClientTool.Client.SampleHandler

// ─── Phase 46.B — Sample client-side handler ─────────────────────────
//
// Browser-side counterpart to `ToolUp.AI.SampleClientTool.Server`.
// When the agent loop emits a `ClientToolInvoke` SSE event with
// `toolName = "_sample.calc"`, the SDK's `ClientToolRuntime` looks up
// the registered executor and dispatches the JSON args through it.
// The handler decodes a `CalcRequest`, computes the arithmetic
// in-process via `CalcOps.compute` (shared with the server-side
// preview), serialises the `CalcResponse`, and the runtime POSTs the
// result to `/api/ai/tool-result` where the suspended agent loop
// resumes.
//
// Reference-only — see `ToolUp.AI.SampleClientTool.Core/README.md`
// for the motivation. The handler intentionally has zero state and
// no network calls; a real client-resident companion would typically
// dispatch a typed `Msg` into a module's MVU here.

open Fable.SimpleJson
open ToolUp.AI.Client
open ToolUp.AI.SampleClientTool

/// The handler the runtime invokes per call. Tuple form
/// (`ClientToolContext * string`) is non-negotiable — Fable v5's
/// `register` mis-curries 2-arg functions stored in a `Dictionary`,
/// so tuple-input is required to survive the registry's storage
/// round-trip (see `ClientToolRuntime.fs` for the underlying
/// rationale).
///
/// Never throws: a malformed `argsJson` falls through to
/// `CalcOps.compute`'s `_ -> nan` fallback via the `Op` field default.
/// The runtime's outer `try / with` would catch any escape, but the
/// contract is to keep the handler total so `nan` reaches the model
/// (which then sees a non-finite value and recovers textually) rather
/// than a `ToolThrew` error envelope.
let private handler (_ctx: ClientToolRuntime.ClientToolContext, argsJson: string) : Async<string> = async {
    let request =
        try
            Json.parseAs<CalcRequest> argsJson
        with _ -> { Op = "?"; A = nan; B = nan }

    let response = CalcOps.compute request
    return Json.serialize response
}

/// One-call install — companions call this once during client boot
/// (typically alongside `AIClientConfig.run`). Idempotent: the
/// underlying `ClientToolRuntime.register` overwrites on re-entry,
/// so multiple calls are harmless (the same handler is re-installed).
let install () : unit =
    ClientToolRuntime.register SampleCalcToolName handler