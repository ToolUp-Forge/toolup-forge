module ToolUp.Platform.Tests.InProcess.MultimodalAIProviderTests

open System
open System.Text
open System.Text.Json
open Expecto
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.AI
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI

// ─── Phase 6o — Multimodal IAIProvider content blocks ────────────────
//
// Covers the new wire-format surface in `IAIProvider.fs`:
//   1. `AIContentPart.redactedSummary` — image bytes never enter the
//      returned string (audit / latency safety gate).
//   2. `AIContentPart.exportPlaceholder` — conversation export
//      renders `![image #N]`, not embedded base64.
//   3. `AIProviderMessage.multipart` constructor + `isMultimodal`
//      predicate.
//   4. `AIProviderError.UnsupportedCapability` round-trips through
//      `toMessage` and is non-retryable.
//   5. Claude wire builder vision-capable model classifier.
//   6. OpenAI wire builder vision-capable model classifier.

let private fakeJpegBytes = Array.create 152340 0uy

let private redactionTests =
    testList "AIContentPart.redactedSummary — image bytes never enter the string" [
        testCase "Base64Bytes image redacts to metadata only"
        <| fun _ ->
            let part =
                ImagePart {
                    MediaType = "image/jpeg"
                    Source = Base64Bytes fakeJpegBytes
                }

            let summary = AIContentPart.redactedSummary part
            Expect.equal summary "[image: 152340 bytes, type=image/jpeg]" "metadata-only summary"
            Expect.isFalse (summary.Contains "AAAA") "no base64 leaked"

        testCase "URL image redacts to host + media type"
        <| fun _ ->
            let part =
                ImagePart {
                    MediaType = "image/png"
                    Source = Url "https://images.example.com/receipt-12345.png"
                }

            let summary = AIContentPart.redactedSummary part
            Expect.stringContains summary "images.example.com" "host preserved"
            Expect.stringContains summary "type=image/png" "media type preserved"
            Expect.isFalse (summary.Contains "receipt-12345") "path component dropped"

        testCase "Text part is preserved verbatim if short"
        <| fun _ ->
            let part = TextPart "Hello"
            Expect.equal (AIContentPart.redactedSummary part) "Hello" "verbatim"

        testCase "Long text part is truncated to 200 chars"
        <| fun _ ->
            let part = TextPart(String.replicate 500 "x")
            let summary = AIContentPart.redactedSummary part
            Expect.equal summary.Length 201 "200 chars + ellipsis"
            Expect.stringEnds summary "…" "ellipsis suffix"
    ]

let private exportTests =
    testList "AIContentPart.exportPlaceholder — Markdown export safety" [
        testCase "Image renders as numbered placeholder"
        <| fun _ ->
            let part =
                ImagePart {
                    MediaType = "image/jpeg"
                    Source = Base64Bytes fakeJpegBytes
                }

            let placeholder = AIContentPart.exportPlaceholder 3 part
            Expect.equal placeholder "![image #3]" "Markdown placeholder"

        testCase "Text renders verbatim"
        <| fun _ ->
            let part = TextPart "Please find the receipt below."

            let placeholder = AIContentPart.exportPlaceholder 1 part

            Expect.equal placeholder "Please find the receipt below." "verbatim"
    ]

let private constructorTests =
    testList "AIProviderMessage — multipart + isMultimodal" [
        testCase "text constructor sets Parts = []"
        <| fun _ ->
            let msg = AIProviderMessage.text "user" "hello"
            Expect.isEmpty msg.Parts "no parts"
            Expect.isFalse (AIProviderMessage.isMultimodal msg) "not multimodal"

        testCase "multipart constructor concatenates text parts into Content"
        <| fun _ ->
            let parts = [
                TextPart "Please inspect"
                ImagePart {
                    MediaType = "image/jpeg"
                    Source = Base64Bytes fakeJpegBytes
                }
                TextPart "and report"
            ]

            let msg = AIProviderMessage.multipart "user" parts
            Expect.equal msg.Content "Please inspect and report" "concatenated text"
            Expect.isTrue (AIProviderMessage.isMultimodal msg) "is multimodal"

        testCase "redactedSummary aggregates per-part summaries"
        <| fun _ ->
            let parts = [
                TextPart "Please inspect"
                ImagePart {
                    MediaType = "image/jpeg"
                    Source = Base64Bytes fakeJpegBytes
                }
            ]

            let msg = AIProviderMessage.multipart "user" parts
            let summary = AIProviderMessage.redactedSummary msg
            Expect.stringContains summary "Please inspect" "text fragment"
            Expect.stringContains summary "[image: 152340 bytes" "image metadata"
            Expect.isFalse (summary.Contains "AAAA") "no bytes leaked"
    ]

let private errorTests =
    testList "AIProviderError.UnsupportedCapability — diagnostic + retry classification" [
        testCase "toMessage renders feature + detail"
        <| fun _ ->
            let err =
                UnsupportedCapability("vision", "Model 'claude-haiku-3-5' does not accept image input.")

            let rendered = AIProviderError.toMessage err
            Expect.stringContains rendered "vision" "feature label"
            Expect.stringContains rendered "claude-haiku-3-5" "detail preserved"

        testCase "UnsupportedCapability is NOT retryable"
        <| fun _ ->
            let err = UnsupportedCapability("vision", "anything")
            Expect.isFalse (AIProviderError.isRetryable err) "non-retryable"
    ]

// Per-provider vision-capable model classifier tests (`isVisionCapable`)
// are intentionally NOT in this file — both wire builders are `module
// internal ...` so the classifiers are not accessible from the test
// project. Their behaviour is exercised through `SendMessage`'s
// capability-rejection branch when a future integration test wires a
// stubbed `HttpMessageHandler`. Listed here as a Tidy-Up follow-up.

// ─── Phase 6o follow-up B — ConversationMessage persistence round-trip ──
//
// `ConversationMessage.Parts: AIContentPart list` widens the persisted
// shape so a multipart turn survives save → load via the same STJ +
// FableConverters pipeline `AIAssistantHandler` uses for the
// `ai-conversations/{id}.json` blob. Plain-text turns continue to
// round-trip with `Parts = []` byte-for-byte.

let private jsonOptions = FableConverters.create ()

let private roundTrip (msg: ConversationMessage) : ConversationMessage =
    let json = JsonSerializer.Serialize(msg, jsonOptions)
    JsonSerializer.Deserialize<ConversationMessage>(json, jsonOptions)

let private persistenceTests =
    testList "ConversationMessage persistence round-trip (Phase 6o follow-up B)" [
        testCase "plain-text turn round-trips with empty Parts"
        <| fun _ ->
            let msg: ConversationMessage = {
                Id = Guid.NewGuid()
                ConversationId = Guid.NewGuid()
                Participant = User
                Content = "Hello"
                Timestamp = DateTime.UtcNow
                ToolCalls = []
                RetrievedSources = []
                Parts = []
                CreatedBy = ""
                BeaconId = ""
                Verification = None
            }

            let replayed = roundTrip msg
            Expect.equal replayed.Content "Hello" "content preserved"
            Expect.isEmpty replayed.Parts "no parts on plain-text turn"

        testCase "multipart turn round-trips with Parts intact"
        <| fun _ ->
            let parts = [
                TextPart "What's on this receipt?"
                ImagePart {
                    MediaType = "image/jpeg"
                    Source = Base64Bytes fakeJpegBytes
                }
            ]

            let msg: ConversationMessage = {
                Id = Guid.NewGuid()
                ConversationId = Guid.NewGuid()
                Participant = User
                Content = "What's on this receipt?"
                Timestamp = DateTime.UtcNow
                ToolCalls = []
                RetrievedSources = []
                Parts = parts
                CreatedBy = ""
                BeaconId = ""
                Verification = None
            }

            let replayed = roundTrip msg
            Expect.equal replayed.Parts.Length 2 "two parts preserved"

            match replayed.Parts with
            | [ TextPart t; ImagePart payload ] ->
                Expect.equal t "What's on this receipt?" "text part survives"
                Expect.equal payload.MediaType "image/jpeg" "media type survives"

                match payload.Source with
                | Base64Bytes bytes -> Expect.equal bytes.Length fakeJpegBytes.Length "image bytes survive"
                | _ -> failtest "expected Base64Bytes source after round-trip"
            | other -> failtestf "expected [TextPart; ImagePart], got %A" other
    ]

let tests =
    testList "Multimodal IAIProvider (Phase 6o)" [
        redactionTests
        exportTests
        constructorTests
        errorTests
        persistenceTests
    ]