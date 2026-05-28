module ToolUp.Platform.Tests.InProcess.CitationNormaliserTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.CitationNormaliser

// ─── Phase 6q — Citation normaliser ──────────────────────────────────
//
// Drift-variant detection + per-policy disposition:
//   1. Each variant shape — `(1)`, `[1]`, `Source 1`, `^1`, `¹` —
//      normalises onto `[¹]` when the digit binds to a retrieved
//      source.
//   2. Phantom digits (digit > sources.Length) become `[unverified]`
//      under `Strict`, are stripped silently under `LenientNormalise`,
//      and pass through under `Off`.
//   3. `Off` is byte-for-byte identity (regression-safety gate).
//   4. Counters report the right normalisation / strip / unverified
//      counts.

let private threeSources: RetrievedSource list = [
    {
        DocumentId = "doc-1"
        DocumentName = "Q3-audit.pdf"
        Snippet = "Q3 revenue grew 14% YoY."
        Score = 0.92
        Origin = ChunkOrigin.Document
        LocationHint = None
    }
    {
        DocumentId = "doc-2"
        DocumentName = "UK-retail.pdf"
        Snippet = "UK retail led growth."
        Score = 0.85
        Origin = ChunkOrigin.Document
        LocationHint = None
    }
    {
        DocumentId = "doc-3"
        DocumentName = "Sales-notes.md"
        Snippet = "December was the peak month."
        Score = 0.80
        Origin = ChunkOrigin.Document
        LocationHint = None
    }
]

let private variantNormalisationTests =
    testList "Drift variants normalise onto [¹] for valid source indices" [
        testCase "parenthesised digit (1) → [¹]"
        <| fun _ ->
            let r =
                normalise threeSources Strict "Q3 revenue grew 14% (1) driven by retail strength."

            Expect.stringContains r.Text "[¹]" "canonical marker"
            Expect.isFalse (r.Text.Contains "(1)") "drift form removed"
            Expect.equal r.Normalisations 1 "one normalisation"

        testCase "bracketed [1] → [¹]"
        <| fun _ ->
            let r = normalise threeSources Strict "Q3 revenue grew 14% [1]."
            Expect.stringContains r.Text "[¹]" "canonical marker"
            Expect.equal r.Normalisations 1 "one normalisation"

        testCase "literal Source 2 → [²]"
        <| fun _ ->
            let r = normalise threeSources Strict "Per Source 2, retail led growth."

            Expect.stringContains r.Text "[²]" "canonical marker"
            Expect.isFalse (r.Text.Contains "Source 2") "drift form removed"

        testCase "caret ^3 → [³]"
        <| fun _ ->
            let r = normalise threeSources Strict "December was the peak month ^3."
            Expect.stringContains r.Text "[³]" "canonical marker"

        testCase "bare unicode superscript ² → [²]"
        <| fun _ ->
            let r = normalise threeSources Strict "Retail led growth ²."
            Expect.stringContains r.Text "[²]" "canonical marker"
            Expect.equal r.Normalisations 1 "one normalisation"

        testCase "multiple variants in one message"
        <| fun _ ->
            let r =
                normalise threeSources Strict "Q3 revenue grew 14% (1) driven by UK retail (2); December was peak (3)."

            Expect.stringContains r.Text "[¹]" "marker 1"
            Expect.stringContains r.Text "[²]" "marker 2"
            Expect.stringContains r.Text "[³]" "marker 3"
            Expect.equal r.Normalisations 3 "three normalisations"
    ]

let private phantomTests =
    testList "Phantom digits — strip / unverified per policy" [
        testCase "Strict: phantom (4) → [unverified]"
        <| fun _ ->
            let r = normalise threeSources Strict "Recovery began in 2023 (4)."

            Expect.stringContains r.Text "[unverified]" "unverified tag"
            Expect.isFalse (r.Text.Contains "(4)") "phantom removed"
            Expect.equal r.UnverifiedTags 1 "one unverified tag"
            Expect.equal r.Strips 1 "counted as strip"

        testCase "LenientNormalise: phantom (4) is stripped silently"
        <| fun _ ->
            let r = normalise threeSources LenientNormalise "Recovery began in 2023 (4)."

            Expect.isFalse (r.Text.Contains "(4)") "phantom removed"
            Expect.isFalse (r.Text.Contains "[unverified]") "no tag added"
            Expect.equal r.UnverifiedTags 0 "no unverified tag"

        testCase "Strict: valid + phantom in one message"
        <| fun _ ->
            let r = normalise threeSources Strict "Q3 grew 14% (1); recovery began in 2023 (4)."

            Expect.stringContains r.Text "[¹]" "valid normalised"
            Expect.stringContains r.Text "[unverified]" "phantom tagged"
            Expect.equal r.Normalisations 1 "one normalisation"
            Expect.equal r.UnverifiedTags 1 "one unverified"
    ]

let private policyGuardTests =
    testList "RagCitationPolicy.Off — regression-safety gate" [
        testCase "Off returns identity"
        <| fun _ ->
            let input = "Q3 revenue grew 14% (1) per Source 2; recovery in 2023 (4)."
            let r = normalise threeSources Off input
            Expect.equal r.Text input "byte-for-byte identity"
            Expect.equal r.Normalisations 0 "no normalisations"
            Expect.equal r.Strips 0 "no strips"
            Expect.equal r.UnverifiedTags 0 "no unverified tags"

        testCase "empty input is identity under any policy"
        <| fun _ ->
            let r = normalise threeSources Strict ""
            Expect.equal r.Text "" "empty text"
            Expect.equal r.Normalisations 0 "no counters"

        testCase "no sources + any policy: phantom-by-construction → unverified under Strict"
        <| fun _ ->
            // Zero retrieved sources means EVERY digit is a phantom.
            let r = normalise [] Strict "Per (1), revenue grew."
            Expect.stringContains r.Text "[unverified]" "no sources = unverified"
            Expect.equal r.UnverifiedTags 1 "one unverified"
    ]

let private canonicalMarkerTests =
    testList "canonicalMarker — matches RAGPromptBuilder.formatMatch" [
        testCase "1–9 render as unicode superscript wrapped in brackets"
        <| fun _ ->
            Expect.equal (canonicalMarker 1) "[¹]" "1"
            Expect.equal (canonicalMarker 2) "[²]" "2"
            Expect.equal (canonicalMarker 9) "[⁹]" "9"

        testCase "10+ falls back to bracketed ASCII digits"
        <| fun _ ->
            Expect.equal (canonicalMarker 10) "[10]" "10"
            Expect.equal (canonicalMarker 11) "[11]" "11"
    ]

// ─── Phase 6q follow-up — per-variant Events accumulator ────────────
//
// `NormaliseResult.Events` is the wire-shape audit emission target.
// Each recognised match contributes one event carrying the matched
// substring, the parsed digit, and the action taken. Aggregate
// counters (`Normalisations` / `Strips` / `UnverifiedTags`) are
// derived sums; these tests verify the per-event detail.

let private eventDetailTests =
    testList "Per-variant CitationEvent detail (Phase 6q follow-up)" [
        testCase "valid digit → NormalisedToCanonical event with sourceIndex"
        <| fun _ ->
            let r = normalise threeSources Strict "Q3 grew 14% (1)."

            Expect.equal r.Events.Length 1 "one event"
            let evt = r.Events.Head
            Expect.equal evt.Variant "(1)" "variant substring"
            Expect.equal evt.Digit 1 "parsed digit"

            match evt.Action with
            | NormalisedToCanonical 1 -> ()
            | other -> failtestf "expected NormalisedToCanonical 1, got %A" other

        testCase "phantom under Strict → UnverifiedTagged event"
        <| fun _ ->
            let r = normalise threeSources Strict "Recovery began in 2023 (4)."

            Expect.equal r.Events.Length 1 "one event"
            let evt = r.Events.Head
            Expect.equal evt.Variant "(4)" "variant substring"
            Expect.equal evt.Digit 4 "parsed digit"
            Expect.equal evt.Action UnverifiedTagged "tagged"

        testCase "phantom under LenientNormalise → StrippedPhantom event"
        <| fun _ ->
            let r = normalise threeSources LenientNormalise "Recovery began in 2023 (4)."

            Expect.equal r.Events.Length 1 "one event"
            Expect.equal r.Events.Head.Action StrippedPhantom "stripped"

        testCase "multiple variants produce one event each"
        <| fun _ ->
            let r =
                normalise threeSources Strict "Q3 grew 14% (1); Source 2 saw retail led; recovery in 2023 (4)."

            Expect.equal r.Events.Length 3 "three events"

            let actions = r.Events |> List.map _.Action

            Expect.contains actions (NormalisedToCanonical 1) "event for (1)"

            Expect.contains actions (NormalisedToCanonical 2) "event for Source 2"

            Expect.contains actions UnverifiedTagged "event for phantom (4)"

        testCase "Off policy produces zero events (identity)"
        <| fun _ ->
            let r =
                normalise threeSources Off "Q3 grew 14% (1) per Source 2; recovery in 2023 (4)."

            Expect.isEmpty r.Events "no events under Off"

        testCase "aggregate counters equal derived sums over Events"
        <| fun _ ->
            let r =
                normalise threeSources Strict "Q3 grew 14% (1); recovery in 2023 (4) and (5)."

            let normalisationsFromEvents =
                r.Events
                |> List.sumBy (fun e ->
                    match e.Action with
                    | NormalisedToCanonical _ -> 1
                    | _ -> 0)

            let unverifiedFromEvents =
                r.Events
                |> List.sumBy (fun e ->
                    match e.Action with
                    | UnverifiedTagged -> 1
                    | _ -> 0)

            Expect.equal r.Normalisations normalisationsFromEvents "counter = sum"
            Expect.equal r.UnverifiedTags unverifiedFromEvents "counter = sum"
    ]

let tests =
    testList "Citation normaliser (Phase 6q)" [
        variantNormalisationTests
        phantomTests
        policyGuardTests
        canonicalMarkerTests
        eventDetailTests
    ]