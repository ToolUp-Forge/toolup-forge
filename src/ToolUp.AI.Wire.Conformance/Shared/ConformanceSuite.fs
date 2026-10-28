// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Wire.Conformance.ConformanceSuite

// The ONE dual-run harness. This single source compiles under BOTH hosts:
//   • the Fable host opens the node:test facade (CONFORMANCE_NODE, set by
//     the Fable .fsproj — and FABLE_COMPILER, which Fable defines itself);
//   • the .NET host opens Expecto.
// Both facades expose the same testCase / testList / Expect.* shapes, so the
// assertion logic below is expressed once and provably cannot drift between
// the .NET and Fable backends — the GP-12 portability contract for the AI
// connector wire layer.
#if FABLE_COMPILER || CONFORMANCE_NODE
open ToolUp.AI.Wire.Conformance.NodeTest
#else
open Expecto
#endif

open ToolUp.Platform.AI
open ToolUp.AI.Wire.Tests

let suite =
    testList "ToolUp.AI.Wire conformance (dual-host: .NET ↔ Fable)" [

        // ── 1. Request byte-parity — the uniform cross-provider corpus ──
        //
        // Every provider's request build must emit byte-identical JSON to
        // its hand-authored golden. Run on both hosts, a green pass proves
        // the bytes are identical across the .NET and Fable backends.
        testList "request byte-parity (all providers)" [
            for name, actual, golden in Corpus.requestFixtures do
                testCase name (fun () -> Expect.equal actual golden "request body bytes")
        ]

        // ── 1b. Block parts (Phase 1128) — the node never leaves as a payload ──
        //
        // A block's text stand-ins render identically on both hosts, and no
        // provider request body built from a history holding blocks carries
        // the node (its marker is absent; its summary is present).
        testList "block parts" [
            testCase "the audit summary names the table, metric and fact count, never the node" (fun () ->
                Expect.equal
                    (AIContentPart.redactedSummary BlockPartFixtures.tableBlock)
                    BlockPartFixtures.tableSummary
                    "table block summary"

                Expect.equal
                    (AIContentPart.redactedSummary BlockPartFixtures.trendBlock)
                    BlockPartFixtures.trendSummary
                    "trend block summary")

            testCase "a table block exports as a markdown table of its provenance" (fun () ->
                Expect.equal
                    (AIContentPart.exportPlaceholder 2 BlockPartFixtures.tableBlock)
                    BlockPartFixtures.tableExport
                    "table export")

            testCase "any other block exports as a placeholder naming its request" (fun () ->
                Expect.equal
                    (AIContentPart.exportPlaceholder 3 BlockPartFixtures.trendBlock)
                    BlockPartFixtures.trendExport
                    "trend export")

            testCase "a multipart message's text fallback carries the summaries, not the nodes" (fun () ->
                let assistant = List.last BlockPartFixtures.providerHistory

                Expect.equal
                    assistant.Content
                    (String.concat " " [
                        BlockPartFixtures.Prose
                        BlockPartFixtures.tableSummary
                        BlockPartFixtures.trendSummary
                    ])
                    "Content"

                Expect.isTrue (not (AIProviderMessage.isMultimodal assistant)) "a block is not an image")

            for name, body, _ in Corpus.blockRequestFixtures do
                testCase (name + " carries the summaries and no node payload") (fun () ->
                    Expect.isTrue (not (body.Contains BlockPartFixtures.NodeMarker)) "the node marker is absent"
                    Expect.isTrue (body.Contains "[block: table, table=daily-trade") "the table summary is present"
                    Expect.isTrue (body.Contains "[block: trend, table=daily-trade") "the trend summary is present")
        ]

        // ── 2. Response structural-parity — OpenAI ──
        testList "response parity — openai" [
            for name, parsed, expected in Corpus.openAIResponseFixtures do
                testCase name (fun () -> Expect.equal parsed expected "parsed AIProviderResponse")
        ]

        // ── 3. Response structural-parity — Gemini ──
        testList "response parity — gemini" [
            for name, parsed, expected in Corpus.geminiResponseFixtures do
                testCase name (fun () -> Expect.equal parsed expected "parsed AIProviderResponse")
        ]

        // ── 4. Response structural-parity — Claude (non-streaming) ──
        //
        // Claude's parse returns the response directly (no Result). Assert
        // the documented fields decomposed, mirroring the proven smoke:
        // content + tool call (id/name/re-serialized input) + split usage.
        testCase "response parity — claude/text+tool_use+usage" (fun () ->
            let r = ClaudeWireFixtures.parsedResponse ()
            Expect.equal r.Content "Hello" "content"
            Expect.equal r.StopReason "tool_use" "stop reason"

            match r.ToolCalls with
            | [ tc ] ->
                Expect.equal tc.Id "toolu_1" "tool call id"
                Expect.equal tc.Name "get_time" "tool call name"
                Expect.equal tc.Arguments """{"tz":"UTC"}""" "tool input re-serialized"
            | _ -> Expect.isTrue false "expected exactly one tool call"

            match r.Usage with
            | Some u ->
                Expect.equal u.PromptTokens 15 "prompt tokens"
                Expect.equal u.CachedPromptTokens 2 "cached tokens"
                Expect.equal u.OutputTokens 5 "output tokens"
            | None -> Expect.isTrue false "usage should be populated")

        // ── 5. Streaming assembly parity — Claude SSE state machine ──
        //
        // The pure `ClaudeStreaming` machine folds a canned `data:` chunk
        // sequence identically on both hosts: text accumulation, out-of-band
        // tool-argument assembly, split usage, and the post-stream `{}`
        // default-fill for a zero-input tool call.
        testList "streaming parity — claude" [
            testCase "text + tool-call assembly + split usage" (fun () ->
                let response, emitted =
                    ClaudeWireFixtures.foldStream ClaudeWireFixtures.streamingChunks

                Expect.equal emitted "Hello world" "onStream surfaced the text deltas"
                Expect.equal response.Content "Hello world" "Content accumulated"
                Expect.equal response.StopReason "tool_use" "stop reason"

                match response.ToolCalls with
                | [ tc ] ->
                    Expect.equal tc.Id "toolu_9" "tool call id"
                    Expect.equal tc.Name "lookup" "tool call name"
                    Expect.equal tc.Arguments ClaudeWireFixtures.streamingExpectedArguments "assembled Arguments"
                | _ -> Expect.isTrue false "expected exactly one tool call"

                match response.Usage with
                | Some u ->
                    Expect.equal u.PromptTokens 22 "prompt tokens (12 + 4 + 6)"
                    Expect.equal u.OutputTokens 7 "cumulative output tokens"
                | None -> Expect.isTrue false "usage should be populated")

            testCase "zero-input tool call defaults Arguments to {}" (fun () ->
                let response, emitted =
                    ClaudeWireFixtures.foldStream ClaudeWireFixtures.streamingZeroArgChunks

                Expect.equal emitted "" "no text deltas emitted"

                match response.ToolCalls with
                | [ tc ] -> Expect.equal tc.Arguments "{}" "empty Arguments default-filled"
                | _ -> Expect.isTrue false "expected exactly one tool call"

                Expect.isNone response.Usage "no usage event ⇒ Usage None")
        ]
    ]