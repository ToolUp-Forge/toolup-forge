// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.SampleClientTool

// ─── Sample client-resident-tool shared types ────────────────────────
//
// Phase 46.B — reference companion. The sample's "tool" is a trivial
// arithmetic operation: the model proposes `{"op":"+","a":1,"b":2}`,
// the server emits `ClientToolInvoke` over SSE, the browser-side
// handler computes the result and POSTs `{"result":3}` back.
//
// Shape kept deliberately small so the sample's job is unambiguous:
// exercise the IClientToolAuthorizer + ClientToolDispatch seam, not
// model any real domain. Anything richer (state, persistence, error
// envelopes) would crowd out the seam's behaviour with sample
// semantics. ≤ 150 LOC per project is the explicit phase bound.

/// Tool name the sample registers under. Picked so the dotted form
/// survives Claude's tool-name sanitiser (`.` → `_`) cleanly and is
/// unambiguously sample-scoped (`_sample.` prefix mirrors the
/// `_platform.` convention for SDK-built-in tools).
[<Literal>]
let SampleCalcToolName = "_sample.calc"

/// Operation the model selects on each call. Plain string so the
/// JSON wire shape is trivial — a richer DU shape would be more
/// idiomatic F# but would require Fable.SimpleJson DU encoding which
/// would distract from the seam-exercise intent.
type CalcRequest = {
    /// One of `"+"`, `"-"`, `"*"`, `"/"`. Unrecognised values
    /// produce `nan` rather than throwing; the handler's contract
    /// is "never throw on malformed input" (mirroring the seam's
    /// own never-throw rule).
    Op: string
    A: float
    B: float
}

type CalcResponse = { Result: float }

module CalcOps =
    /// Pure arithmetic. Shared between server-side preview / docs
    /// and the client-side handler so both sides agree on division-
    /// by-zero behaviour (`infinity` / `nan` per IEEE 754, no
    /// exception).
    let compute (req: CalcRequest) : CalcResponse =
        let result =
            match req.Op with
            | "+" -> req.A + req.B
            | "-" -> req.A - req.B
            | "*" -> req.A * req.B
            | "/" -> req.A / req.B
            | _ -> nan

        { Result = result }