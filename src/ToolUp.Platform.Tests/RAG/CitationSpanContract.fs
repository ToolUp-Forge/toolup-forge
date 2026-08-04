// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.RAG.CitationSpanContract

open System
open System.Text.Json
open Expecto
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.RAG.Chunking

// ─── Phase 505 — citation character-offset spans ─────────────────────
//
// A citation used to point at a chunk. A chunk is hundreds of characters,
// so "open at the exact spot" was not expressible: the anchor a preview
// surface needs — which characters of the source document — was thrown away
// at chunking time and could not be recovered afterwards, because a chunk's
// text is whitespace-normalised and a substring search over the original is
// ambiguous the moment the document repeats a sentence.
//
// This pack pins three things, in descending order of how badly getting
// them wrong would hurt:
//
//   1. **A span resolves.** `sourceText.Substring(start, len) = span.Text`,
//      for every piece the chunkers emit, on prose and on tabular input.
//      This is the load-bearing assertion — a span that does not resolve is
//      worse than no span, because a highlight lands confidently on the
//      wrong region and the reader has no way to tell.
//   2. **Absence degrades to exactly the old behaviour (GP 11).** A
//      producer with no offsets, a pre-505 chunk, a wire payload predating
//      the field, and a malformed span all reach the same place: a
//      chunk-granular citation identical to the pre-505 one. Four separate
//      routes, one destination, asserted separately because each has its
//      own way of going wrong.
//   3. **The text-only chunkers did not move.** `splitByTokens` and
//      `chunkSpreadsheet` are now projections of the span-aware
//      implementations. They had no unit tests at all before this pack, so
//      the behaviours they were relied on for — overlap, oversized-unit
//      splitting, the `MinTokens` floor — are pinned here rather than
//      trusted to survive the rewrite unobserved.
//
// Fixed vocabulary and hand-checkable arithmetic throughout: every offset
// asserted below can be counted off the fixture by a reader, and nothing
// depends on `String.GetHashCode()` (process-randomised, so any assertion
// resting on it is a coin flip across runs).

let private jsonOptions = FableConverters.create ()

// ── Fixtures ──
//
// Deliberately shaped so the interesting cases are reachable with the
// heuristic counter (≈4 chars/token): `tiny` forces multi-chunk splitting
// at a small MaxTokens without needing a wall of prose.

let private tiny = {
    ChunkingConfig.defaults with
        MaxTokens = 12
        OverlapTokens = 0
        MinTokens = 1
}

let private prose =
    "The widget tolerance is tight. The sprocket calibration drifted last quarter. \
     Quarterly review follows the calibration."

/// Leading and trailing whitespace on purpose: the single-chunk path trims,
/// and a naive implementation that trims the string *before* computing
/// offsets reports positions shifted by the length of the leading run.
let private padded = "   Short body of text here.   "

let private spanOf (piece: ChunkPiece) =
    Expect.wantSome piece.Span "the piece must carry a span"

// ── The chunkers ──

let private prosePack =
    testList "token splitter" [
        test "every emitted span resolves to its source text" {
            let pieces = splitByTokensWithSpans tiny prose

            Expect.isGreaterThan pieces.Length 1 "the fixture must actually split, or this asserts nothing"

            for piece in pieces do
                let span = spanOf piece

                Expect.isTrue
                    (SourceSpan.resolvesIn prose span)
                    (sprintf
                        "span [%d,%d) must resolve in the source; it claims %A but the source reads %A"
                        span.StartOffset
                        span.EndOffset
                        span.Text
                        (if span.EndOffset <= prose.Length then
                             prose.Substring(span.StartOffset, span.EndOffset - span.StartOffset)
                         else
                             "<out of bounds>"))
        }

        test "a span brackets the chunk it was emitted with" {
            // The chunk text is the source region with whitespace
            // normalised, so it is not necessarily a substring of it — but
            // the region's first and last words must be the chunk's.
            let pieces = splitByTokensWithSpans tiny prose

            for piece in pieces do
                let span = spanOf piece
                let firstWord (s: string) = s.Split(' ') |> Array.head
                let lastWord (s: string) = s.Split(' ') |> Array.last

                Expect.equal
                    (firstWord span.Text)
                    (firstWord piece.Text)
                    "the span must start where the chunk starts, not somewhere upstream"

                Expect.equal (lastWord span.Text) (lastWord piece.Text) "…and end where the chunk ends"
        }

        test "spans advance monotonically through the document" {
            let starts =
                splitByTokensWithSpans tiny prose |> List.map (fun p -> (spanOf p).StartOffset)

            Expect.equal starts (List.sort starts) "chunk order must follow document order"
            Expect.equal (List.head starts) 0 "the first chunk starts at the top of the document"
        }

        test "offsets are stable across re-chunking (505.D)" {
            // Same input, same config: a second ingest of an unchanged
            // document must not move a citation that was already issued.
            let a = splitByTokensWithSpans tiny prose |> List.map (fun p -> p.Span)
            let b = splitByTokensWithSpans tiny prose |> List.map (fun p -> p.Span)
            Expect.equal b a "re-chunking an unchanged document must reproduce identical spans"
        }

        test "offsets stay anchored to the source under a different chunk budget" {
            // The chunk BOUNDARIES legitimately move when MaxTokens changes.
            // What must not move is the frame of reference: every span still
            // resolves against the same unmodified source.
            let wider = { tiny with MaxTokens = 40 }
            let pieces = splitByTokensWithSpans wider prose

            Expect.notEqual
                (pieces |> List.map _.Text)
                (splitByTokensWithSpans tiny prose |> List.map _.Text)
                "the fixture must actually re-chunk differently, or this asserts nothing"

            for piece in pieces do
                Expect.isTrue (SourceSpan.resolvesIn prose (spanOf piece)) "a re-chunked span still resolves"
        }

        test "the single-chunk path reports trimmed offsets, not zero" {
            let pieces = splitByTokensWithSpans ChunkingConfig.tabular padded

            let piece =
                Expect.wantSome (List.tryExactlyOne pieces) "input under MaxTokens is one chunk"

            let span = spanOf piece
            Expect.equal piece.Text "Short body of text here." "the chunk text is trimmed"
            Expect.equal span.StartOffset 3 "the span skips the three leading spaces"
            Expect.equal span.EndOffset 27 "…and stops before the trailing ones"
            Expect.isTrue (SourceSpan.resolvesIn padded span) "and it resolves"
        }

        test "input below MinTokens yields nothing at all" {
            let floored = {
                ChunkingConfig.defaults with
                    MinTokens = 500
            }

            Expect.isEmpty (splitByTokensWithSpans floored prose) "below the floor, no chunk and no span"
        }

        test "an oversized unit is word-split and each piece still resolves" {
            // One sentence far over budget with no internal punctuation: the
            // hard word-split path, which computes offsets inside a unit
            // rather than between units.
            let long = String.replicate 60 "alpha " + "omega."
            let pieces = splitByTokensWithSpans tiny long

            Expect.isGreaterThan pieces.Length 2 "the oversized-unit path must have run"

            for piece in pieces do
                Expect.isTrue (SourceSpan.resolvesIn long (spanOf piece)) "a word-split span resolves too"
        }
    ]

let private textOnlyParity =
    testList "text-only projection" [
        test "splitByTokens is exactly the span-aware chunker's text" {
            for cfg in [ tiny; ChunkingConfig.defaults; ChunkingConfig.tabular ] do
                Expect.equal
                    (splitByTokens cfg prose)
                    (splitByTokensWithSpans cfg prose |> List.map _.Text)
                    "the two chunkers must never disagree on text"
        }

        // The overlap unit is a SENTENCE, not a word — the packer's units
        // are whatever `splitBySentence` returned, and the seed is built by
        // walking those back until `OverlapTokens` is covered. Asserting on
        // words instead is how the first draft of this case failed: it
        // expected chunk 1 to open with `"tight."` and got `"The"`, which
        // looks like a broken overlap and is in fact a correct one.
        test "overlap carries the trailing SENTENCE into the next chunk" {
            let overlapping = { tiny with OverlapTokens = 6 }
            let chunks = splitByTokens overlapping prose

            Expect.isGreaterThan chunks.Length 1 "the fixture must split"

            let opening = splitBySentence chunks[1] |> List.head

            Expect.isTrue
                (chunks[0].EndsWith opening)
                (sprintf
                    "chunk 1 opens with %A, which must be the sentence chunk 0 ended on — that is what OverlapTokens buys"
                    opening)
        }

        test "zero overlap shares nothing" {
            let chunks = splitByTokens tiny prose
            let opening = splitBySentence chunks[1] |> List.head
            Expect.isFalse (chunks[0].EndsWith opening) "OverlapTokens = 0 must not seed the next chunk"
        }

        test "splitBySentence still segments on terminators" {
            Expect.equal
                (splitBySentence "One. Two! Three?")
                [ "One."; "Two!"; "Three?" ]
                "sentence segmentation keeps its terminators and drops the separators"
        }
    ]

// ── Spreadsheet row groups ──

let private sheet = {
    SheetName = "Sales"
    Headers = [| "Region"; "Units" |]
    Rows = [ 2, [| "North"; "10" |]; 3, [| "South"; "20" |] ]
}

/// The raw CSV the sheet was parsed from, with each data row's `[start,
/// end)` recorded — the shape a producer that holds its source text can
/// supply. Offsets counted off the literal: "Region,Units\n" is 13 chars,
/// so row 2 occupies [13, 22) and row 3 [23, 32).
let private csv = "Region,Units\nNorth,10\nSouth,20\n"

let private csvContext = {
    SourceText = csv
    RowOffsets = Map [ 2, (13, 21); 3, (22, 30) ]
}

let private spreadsheetPack =
    testList "spreadsheet row groups" [
        test "no span context ⇒ no spans, and identical text (GP 11)" {
            let pieces = chunkSpreadsheetWithSpans ChunkingConfig.tabular None sheet

            Expect.isNonEmpty pieces "the fixture must produce chunks"

            for piece in pieces do
                Expect.isNone piece.Span "a producer with no source text must not invent offsets"

            Expect.equal
                (pieces |> List.map _.Text)
                (chunkSpreadsheet ChunkingConfig.tabular sheet)
                "chunk text is byte-identical to the pre-505 chunker"
        }

        test "a span context anchors each row group into the raw source" {
            let pieces =
                chunkSpreadsheetWithSpans ChunkingConfig.tabular (Some csvContext) sheet

            let piece = Expect.wantSome (List.tryExactlyOne pieces) "both rows fit one group"

            let span = spanOf piece
            Expect.equal span.StartOffset 13 "the group starts at the first row it packed"
            Expect.equal span.EndOffset 30 "…and ends at the last"
            Expect.equal span.Text "North,10\nSouth,20" "and resolves to those rows verbatim"
            Expect.isTrue (SourceSpan.resolvesIn csv span) "and it resolves"
        }

        test "rows with no recorded offset contribute none, and never widen the span" {
            // Row 3 absent from the map: the group still anchors to row 2
            // alone rather than stretching to cover unknown territory.
            let partial = {
                csvContext with
                    RowOffsets = Map [ 2, (13, 21) ]
            }

            let pieces = chunkSpreadsheetWithSpans ChunkingConfig.tabular (Some partial) sheet
            let span = spanOf (List.head pieces)
            Expect.equal span.EndOffset 21 "the span must stop at the last KNOWN row, not guess past it"
        }

        test "a context whose rows are all unknown degrades to no span" {
            let blind = {
                csvContext with
                    RowOffsets = Map.empty
            }

            for piece in chunkSpreadsheetWithSpans ChunkingConfig.tabular (Some blind) sheet do
                Expect.isNone piece.Span "an empty offset index is the same as no context"
        }
    ]

// ── SourceSpan itself ──

let private spanTypePack =
    testList "SourceSpan" [
        test "create refuses an out-of-bounds or empty range" {
            Expect.isNone (SourceSpan.create "abc" 0 4) "past the end"
            Expect.isNone (SourceSpan.create "abc" 2 2) "zero width anchors nothing"
            Expect.isNone (SourceSpan.create "abc" -1 2) "negative start"
            Expect.isSome (SourceSpan.create "abc" 1 3) "a legitimate range is cut"
        }

        test "resolvesIn rejects a span whose source moved under it" {
            let span = Expect.wantSome (SourceSpan.create "the widget is fine" 4 10) "fixture"
            Expect.equal span.Text "widget" "fixture sanity"
            Expect.isTrue (SourceSpan.resolvesIn "the widget is fine" span) "unchanged source resolves"

            // Same length, different content at those offsets — the shape a
            // re-ingested / edited document takes. Bounds alone would accept
            // it; carrying the text is what catches it.
            Expect.isFalse
                (SourceSpan.resolvesIn "the sprock! is fine" span)
                "an edited document must refuse the anchor, not highlight the wrong six characters"

            Expect.isFalse (SourceSpan.resolvesIn "short" span) "a truncated document is out of bounds"
        }

        test "sanitise downgrades a malformed span to chunk-granular" {
            let ok = Expect.wantSome (SourceSpan.create "abcdef" 1 4) "fixture"
            Expect.equal (SourceSpan.sanitise (Some ok)) (Some ok) "a well-formed span survives"
            Expect.isNone (SourceSpan.sanitise None) "absence stays absence"

            Expect.isNone
                (SourceSpan.sanitise (
                    Some {
                        StartOffset = 5
                        EndOffset = 2
                        Text = "x"
                    }
                ))
                "inverted offsets"

            Expect.isNone
                (SourceSpan.sanitise (
                    Some {
                        StartOffset = -1
                        EndOffset = 3
                        Text = "abcd"
                    }
                ))
                "negative start"

            Expect.isNone
                (SourceSpan.sanitise (
                    Some {
                        StartOffset = 0
                        EndOffset = 6
                        Text = "abc"
                    }
                ))
                "a Text whose length disagrees with the offsets is not trustworthy in either direction"
        }

        test "length is the half-open width" {
            let span = Expect.wantSome (SourceSpan.create "abcdef" 1 4) "fixture"
            Expect.equal (SourceSpan.length span) 3 "1..4 is three characters"
        }
    ]

// ── The citation contract ──

let private matchWith (metadata: (string * string) list) : VectorMatch = {
    ChunkId = "chunk-1"
    Content = "The widget tolerance is tight."
    Score = 0.9
    Scope = Deployment
    Metadata = Map.ofList (("_origin", "Document") :: metadata)
}

let private project (m: VectorMatch) =
    ToolUp.RAG.RAGPromptBuilder.toRetrievedSource 240 m

let private serialisedSpan (span: SourceSpan) =
    JsonSerializer.Serialize(span, jsonOptions)

let private citationPack =
    testList "citation contract" [
        test "a stamped span rides through to the citation" {
            let span =
                Expect.wantSome (SourceSpan.create "prefix The widget tolerance is tight." 7 37) "fixture"

            let src = project (matchWith [ ChunkMetadata.SpanKey, serialisedSpan span ])

            Expect.equal src.Span (Some span) "the projection must preserve the producer's span exactly"
        }

        test "no span metadata ⇒ chunk-granular citation, everything else unchanged (GP 11)" {
            let withSpan =
                let span =
                    Expect.wantSome (SourceSpan.create "The widget tolerance is tight." 0 29) "fixture"

                project (matchWith [ ChunkMetadata.SpanKey, serialisedSpan span ])

            let without = project (matchWith [])

            Expect.isNone without.Span "a document with no offset metadata still cites, at chunk granularity"

            // The rest of the citation must be identical — the span is
            // enrichment, not a different citation.
            Expect.equal { withSpan with Span = None } without "no field other than Span may move"
        }

        test "a malformed stamped span is dropped, never passed through" {
            let inverted = {
                StartOffset = 40
                EndOffset = 10
                Text = "nope"
            }

            let lengthMismatch = {
                StartOffset = 0
                EndOffset = 100
                Text = "short"
            }

            let zeroWidth = {
                StartOffset = 5
                EndOffset = 5
                Text = ""
            }

            let malformed = [
                "inverted", inverted
                "length mismatch", lengthMismatch
                "zero width", zeroWidth
            ]

            for label, span in malformed do
                let src = project (matchWith [ ChunkMetadata.SpanKey, serialisedSpan span ])

                Expect.isNone
                    src.Span
                    (sprintf "%s: a bad span must degrade to chunk-granular, not become a dangling anchor" label)
        }

        test "unparseable span metadata is dropped rather than throwing" {
            for junk in [ "not json at all"; "{"; "null"; "" ] do
                let src = project (matchWith [ ChunkMetadata.SpanKey, junk ])
                Expect.isNone src.Span (sprintf "%A must not produce a span" junk)
        }

        test "pre-505 wire payloads deserialise with Span = None" {
            // The persisted-conversation replay path: a payload written
            // before the field existed must absorb to None rather than
            // materialising a null record that NREs at the first read.
            let legacy = {|
                DocumentId = "doc-1"
                DocumentName = "report.pdf"
                Snippet = "…"
                Score = 0.5
                Origin = ChunkOrigin.Document
                LocationHint = (None: string option)
                OriginalRef = (None: OriginalDocumentRef option)
                Scope = (None: VectorScope option)
                ChunkId = (None: string option)
                FactId = (None: string option)
                FactRendering = (None: string option)
                FactFreshness = (None: FactFreshnessInfo option)
                FactSupersededBy = (None: string option)
            |}

            let json = JsonSerializer.Serialize(legacy, jsonOptions)
            let back = JsonSerializer.Deserialize<RetrievedSource>(json, jsonOptions)

            Expect.equal back.DocumentId "doc-1" "legacy fields intact"
            Expect.isNone back.Span "the absent field must absorb to None, never a null span"
        }

        test "a span round-trips through the wire unchanged" {
            let span = Expect.wantSome (SourceSpan.create "abcdefghij" 2 7) "fixture"

            let src = {
                project (matchWith []) with
                    Span = Some span
            }

            let json = JsonSerializer.Serialize(src, jsonOptions)
            let back = JsonSerializer.Deserialize<RetrievedSource>(json, jsonOptions)
            Expect.equal back.Span (Some span) "the span survives serialisation"
        }
    ]

// ── End to end ──

let private endToEndPack =
    testList "chunk → citation" [
        test "a span survives chunking, metadata stamping, and projection" {
            // The whole path in one case: chunk a document, stamp the span a
            // producer would stamp, project it as a citation, and check the
            // citation's span still resolves against the ORIGINAL document.
            // Each step preserved the span in isolation above; this is the
            // one that fails if any pair of them disagrees.
            let pieces = splitByTokensWithSpans tiny prose
            let piece = List.head pieces
            let span = spanOf piece

            let src =
                project {
                    matchWith [ ChunkMetadata.SpanKey, serialisedSpan span ] with
                        Content = piece.Text
                }

            let carried = Expect.wantSome src.Span "the citation carries the chunker's span"

            Expect.isTrue
                (SourceSpan.resolvesIn prose carried)
                "and it still resolves to the quoted text in the source document"

            Expect.stringContains prose carried.Text "the resolved text is genuinely from the document"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 505 — citation character-offset spans" [
        prosePack
        textOnlyParity
        spreadsheetPack
        spanTypePack
        citationPack
        endToEndPack
    ]