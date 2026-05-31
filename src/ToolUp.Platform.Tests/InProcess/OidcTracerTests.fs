// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.OidcTracerTests

open Expecto
open ToolUp.AuthProviders.Oidc.OidcTypes
open ToolUp.AuthProviders.Oidc.AuthTracer

// ─── AuthTracer: format + writingTracer + select / selectWith ────────
//
// The tracer ships three concrete implementations:
//   nullTracer     — discards every transition (default; zero cost)
//   writingTracer  — formats and hands each line to a supplied writer
//   consoleTracer  — `writingTracer console.log`; the production wiring
//
// Tests exercise `formatTransition` (pure), `writingTracer` (pure,
// driven by a capturing writer), and `selectWith` (pure, gates either
// `writingTracer` or `nullTracer`). `consoleTracer` itself + `fromEnv`
// reach for browser-only APIs (`[<Emit>]` `console.log`, the Vite
// build-time `__TOOLUP_AUTH_TRACE__` define) and aren't exercised
// from .NET; the testable surface covers everything except the thin
// production wrapper.

/// Capturing writer used by every test that drives `writingTracer`.
/// Returns the list ref + the writer function to pass in.
let private capture () =
    let buf = ResizeArray<string>()
    buf, (fun (line: string) -> buf.Add line)

let private dummyDiagnostic: AuthDiagnostic = {
    Kind = "TEST_KIND"
    SubCause = Some "test sub-cause"
    Hint = Some "test hint"
}

let tests: Test =
    testList "OidcClient.AuthTracer" [

        // ─── formatTransition ────────────────────────────────────────

        testCase "formatTransition renders correlation id, stage, no detail, no outcome"
        <| fun () ->
            let t = {
                CorrelationId = Some "abc-123"
                Stage = "begin-sign-in"
                Detail = None
                Outcome = None
            }

            Expect.equal (formatTransition t) "[auth] abc-123 begin-sign-in" ""

        testCase "formatTransition renders `-` when correlation id is None"
        <| fun () ->
            let t = {
                CorrelationId = None
                Stage = "sign-out"
                Detail = None
                Outcome = None
            }

            Expect.equal (formatTransition t) "[auth] - sign-out" ""

        testCase "formatTransition appends detail when present"
        <| fun () ->
            let t = {
                CorrelationId = Some "x"
                Stage = "classify-stored:fresh-jwt"
                Detail = Some "issuer ok exp +3550s"
                Outcome = None
            }

            Expect.equal (formatTransition t) "[auth] x classify-stored:fresh-jwt issuer ok exp +3550s" ""

        testCase "formatTransition appends outcome kind + sub-cause when failure"
        <| fun () ->
            let t = {
                CorrelationId = Some "abc-123"
                Stage = "token-exchange-failed"
                Detail = None
                Outcome = Some dummyDiagnostic
            }

            Expect.equal (formatTransition t) "[auth] abc-123 token-exchange-failed err=TEST_KIND sub=test sub-cause" ""

        // ─── nullTracer ──────────────────────────────────────────────

        testCase "nullTracer.Emit is a no-op (idempotent + non-throwing)"
        <| fun () ->
            // The contract: nullTracer.Emit must NOT throw under any
            // input shape. We exercise it for several variants to
            // confirm — any exception fails the test.
            nullTracer.Emit {
                CorrelationId = None
                Stage = "x"
                Detail = None
                Outcome = None
            }

            nullTracer.Emit {
                CorrelationId = Some "y"
                Stage = "z"
                Detail = Some "d"
                Outcome = Some dummyDiagnostic
            }

        // ─── writingTracer ───────────────────────────────────────────

        testCase "writingTracer routes every transition through the writer once"
        <| fun () ->
            let buf, write = capture ()
            let tracer = writingTracer write

            tracer.Emit {
                CorrelationId = Some "a"
                Stage = "begin-sign-in"
                Detail = None
                Outcome = None
            }

            tracer.Emit {
                CorrelationId = Some "a"
                Stage = "token-exchange-ok"
                Detail = None
                Outcome = None
            }

            tracer.Emit {
                CorrelationId = Some "a"
                Stage = "token-exchange-failed"
                Detail = None
                Outcome = Some dummyDiagnostic
            }

            Expect.equal buf.Count 3 "three emits, three writes"
            Expect.equal buf[0] "[auth] a begin-sign-in" ""
            Expect.equal buf[1] "[auth] a token-exchange-ok" ""
            Expect.equal buf[2] "[auth] a token-exchange-failed err=TEST_KIND sub=test sub-cause" ""

        // ─── selectWith ──────────────────────────────────────────────

        testCase "selectWith false produces a silent tracer regardless of writer"
        <| fun () ->
            let buf, write = capture ()
            let tracer = selectWith false write

            tracer.Emit {
                CorrelationId = Some "a"
                Stage = "ignored"
                Detail = None
                Outcome = None
            }

            Expect.equal buf.Count 0 "writer must not be called when disabled"

        testCase "selectWith true routes through the supplied writer"
        <| fun () ->
            let buf, write = capture ()
            let tracer = selectWith true write

            tracer.Emit {
                CorrelationId = Some "x"
                Stage = "begin-sign-in"
                Detail = None
                Outcome = None
            }

            Expect.equal buf.Count 1 ""
            Expect.equal buf[0] "[auth] x begin-sign-in" ""

        // ─── install / active / emit / emitOk / emitErr ──────────────
        //
        // The ambient tracer slot is module-level mutable state — by
        // design, since production composition wants a single install
        // at startup. That makes the ambient-mechanism tests sensitive
        // to Expecto's default parallel execution: a sibling test that
        // calls `install` or `emit` between this test's setup and
        // assertion races on the shared `current`. Wrap in
        // `testSequenced` to serialise the whole sub-list.

        testSequenced (
            testList "ambient install/emit (sequenced — shared mutable slot)" [

                testCase "install replaces the ambient tracer; emit routes through it"
                <| fun () ->
                    let prior = active ()

                    try
                        let buf, write = capture ()
                        install (writingTracer write)

                        emit {
                            CorrelationId = Some "y"
                            Stage = "test"
                            Detail = None
                            Outcome = None
                        }

                        Expect.equal buf.Count 1 ""
                        Expect.equal buf[0] "[auth] y test" ""
                    finally
                        install prior

                testCase "emitOk / emitErr produce the same lines as direct emit"
                <| fun () ->
                    let prior = active ()

                    try
                        let buf, write = capture ()
                        install (writingTracer write)

                        emitOk (Some "c") "happy-stage" (Some "yep")
                        emitErr (Some "c") "sad-stage" dummyDiagnostic

                        Expect.equal buf.Count 2 ""
                        Expect.equal buf[0] "[auth] c happy-stage yep" ""
                        Expect.equal buf[1] "[auth] c sad-stage err=TEST_KIND sub=test sub-cause" ""
                    finally
                        install prior
            ]
        )
    ]