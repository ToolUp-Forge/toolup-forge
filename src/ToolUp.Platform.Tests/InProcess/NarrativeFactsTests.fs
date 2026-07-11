// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.NarrativeFactsTests

open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.PublicRendering
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 521 — fact-referencing narrative `Metric` spans ───────────
//
// Covers the additive `InlineSpan.Metric.factRef` field: wire round-trip
// with / without a ref, renderer pass-through (HTML `data-fact` / Markdown
// annotation / plaintext unchanged), the byte-identity guarantee for the
// fact-less form (GP 11), the fact-reference walk (`NarrativeFacts`), and
// the stale-narrative supersession discovery on a seeded store.

let private norm (s: string) = s.Replace("\r\n", "\n")

let private docOf (elements: NarrativeElement list) : NarrativeDocument = {
    Title = "T"
    Subtitle = None
    Sections = [
        {
            Id = "s"
            Heading = "H"
            Subheading = None
            Elements = elements
        }
    ]
    Provenance = None
    Lang = None
    CanonicalUrl = None
}

let private jsonOptions = FableConverters.create ()

/// Seed a narrative into a store under scope `s1`, returning its id.
let private seed (store: INarrativeStore) (moduleId: string) (doc: NarrativeDocument) : NarrativeId =
    store.Publish("s1", moduleId, None, doc) |> Async.RunSynchronously

let tests =
    testList "Phase 521 fact-referencing narratives" [

        // ─── Wire round-trip (STJ / FableConverters) ─────────────────
        test "a fact-less Metric span round-trips through the wire unchanged" {
            let doc = docOf [ Paragraph [ Metric("Spend", "£21,800", None) ] ]
            let json = JsonSerializer.Serialize(doc, jsonOptions)
            let back = JsonSerializer.Deserialize<NarrativeDocument>(json, jsonOptions)
            Expect.equal back doc "fact-less document round-trips to an equal value"
        }

        test "a fact-bearing Metric span round-trips through the wire carrying its ref" {
            let doc = docOf [ Paragraph [ Metric("Spend", "£21,800", Some "fact-abc123") ] ]
            let json = JsonSerializer.Serialize(doc, jsonOptions)
            let back = JsonSerializer.Deserialize<NarrativeDocument>(json, jsonOptions)
            Expect.equal back doc "fact-bearing document round-trips to an equal value"

            Expect.equal
                (NarrativeFacts.factRefs back |> Set.toList)
                [ "fact-abc123" ]
                "the ref survives the round-trip"
        }

        // ─── Renderer byte-identity when absent (GP 11) ──────────────
        test "renderers are byte-identical to the pre-fact form when factRef is None" {
            let doc = docOf [ Paragraph [ Metric("r", "0.42", None) ] ]

            Expect.stringContains
                (norm (NarrativeHtml.render doc))
                "<span class=\"narrative-metric\"><strong>r</strong> 0.42</span>"
                "HTML metric markup is unchanged (no data-fact attribute)"

            Expect.isFalse ((norm (NarrativeHtml.render doc)).Contains "data-fact") "no data-fact attribute emitted"
            Expect.stringContains (norm (NarrativeMarkdown.render doc)) "**r** 0.42" "Markdown metric is unchanged"
            Expect.isFalse ((NarrativeMarkdown.render doc).Contains "<!--fact") "no annotation comment emitted"
            Expect.stringContains (norm (NarrativePlaintext.render doc)) "r = 0.42" "plaintext metric is unchanged"
        }

        // ─── Renderer pass-through when present ──────────────────────
        test "HTML passes a fact ref through as a data-fact attribute" {
            let doc = docOf [ Paragraph [ Metric("r", "0.42", Some "fact-xyz") ] ]
            let html = norm (NarrativeHtml.render doc)
            Expect.stringContains html "data-fact=\"fact-xyz\"" "the ref rides a data-fact attribute"
            Expect.stringContains html "<strong>r</strong> 0.42" "the visible metric markup is preserved"
        }

        test "Markdown trails a fact ref as an annotation comment; plaintext drops it" {
            let doc = docOf [ Paragraph [ Metric("r", "0.42", Some "fact-xyz") ] ]

            Expect.stringContains
                (NarrativeMarkdown.render doc)
                "**r** 0.42<!--fact:fact-xyz-->"
                "Markdown annotation comment"

            Expect.isFalse ((NarrativePlaintext.render doc).Contains "fact-xyz") "plaintext carries no fact ref"

            Expect.stringContains
                (norm (NarrativePlaintext.render doc))
                "r = 0.42"
                "plaintext metric is the bare labelled value"
        }

        test "an empty-label fact metric drops the strong wrapper but keeps the ref" {
            let doc = docOf [ Paragraph [ Metric("", "£21,800", Some "fact-1") ] ]
            let html = norm (NarrativeHtml.render doc)

            Expect.stringContains
                html
                "<span class=\"narrative-metric\" data-fact=\"fact-1\">£21,800</span>"
                "no empty <strong>"

            Expect.isFalse (html.Contains "<strong></strong>") "empty strong is not emitted"
        }

        // ─── metricGridWithFacts (Phase 521.B) ───────────────────────
        test
            "metricGridWithFacts emits fact-referencing value spans; a None row is byte-identical to the fact-less grid" {
            match
                NarrativeFromData.metricGridWithFacts [
                    "Spend", "£21,800", Some 0.23, Some "fact-spend"
                    "Plain", "5", None, None
                ]
            with
            | KeyValueGrid pairs ->
                let (_, v0) = pairs[0]

                Expect.equal
                    v0
                    [
                        Metric("", "£21,800", Some "fact-spend")
                        Text " "
                        Metric("▲", "+23.0%", None)
                    ]
                    "fact row: value is a fact-referencing metric span + delta"

                let (_, v1) = pairs[1]
                Expect.equal v1 [ Strong "5" ] "None row degrades to the fact-less Strong value"
            | other -> failtestf "expected KeyValueGrid, got %A" other
        }

        // ─── Fact-reference walk (Phase 521.C/D primitive) ───────────
        test "factRefs / cites collect referenced fact ids across spans, links and table cells" {
            let doc =
                docOf [
                    Paragraph [ Text "Revenue is "; Metric("rev", "£1m", Some "fact-a") ]
                    Table(
                        [ "Metric", TableAlignment.Left; "Value", TableAlignment.Right ],
                        [ [ [ Text "Margin" ]; [ Metric("", "12%", Some "fact-b") ] ] ]
                    )
                    Paragraph [ Link("/x", [ Metric("share", "30%", Some "fact-c") ]) ]
                ]

            Expect.equal
                (NarrativeFacts.factRefs doc)
                (Set.ofList [ "fact-a"; "fact-b"; "fact-c" ])
                "all three refs collected"

            Expect.isTrue (NarrativeFacts.cites "fact-b" doc) "cites a referenced fact"
            Expect.isFalse (NarrativeFacts.cites "fact-z" doc) "does not cite an unreferenced fact"
        }

        test "factRefsInSection collects the ids narrative-commit stamps onto a chunk (Phase 521.D)" {
            let section: NarrativeSection = {
                Id = "s"
                Heading = "H"
                Subheading = None
                Elements = [ Paragraph [ Metric("a", "1", Some "fact-a"); Metric("b", "2", None) ] ]
            }

            Expect.equal
                (NarrativeFacts.factRefsInSection section)
                (Set.ofList [ "fact-a" ])
                "only the Some-ref is collected"
        }

        // ─── staleFlags on a seeded supersession chain (Phase 521.C) ──
        test "staleFlags flags exactly the cited-and-superseded facts, carrying the superseding head" {
            let doc =
                docOf [
                    Paragraph [ Metric("rev", "£1m", Some "fact-a"); Metric("cost", "£2m", Some "fact-b") ]
                ]

            // fact-a was superseded by fact-a2; fact-c was superseded but is
            // not cited; fact-b is cited but not superseded.
            let supersededBy = Map.ofList [ "fact-a", Some "fact-a2"; "fact-c", None ]

            let flags = NarrativeFacts.staleFlags supersededBy doc

            Expect.equal
                flags
                [
                    {
                        SupersededFactId = "fact-a"
                        SupersededByFactId = Some "fact-a2"
                    }
                ]
                "only the cited-and-superseded fact is flagged, with its head"
        }

        test "findStaleNarratives surfaces exactly the store's narratives that cite a superseded fact" {
            let store = InMemoryNarrativeStore() :> INarrativeStore

            let citesA = docOf [ Paragraph [ Metric("rev", "£1m", Some "fact-a") ] ]
            let citesB = docOf [ Paragraph [ Metric("cost", "£2m", Some "fact-b") ] ]
            let citesNone = docOf [ Paragraph [ Text "no facts here" ] ]

            let idA = seed store "ModA" citesA
            seed store "ModB" citesB |> ignore
            seed store "ModNone" citesNone |> ignore

            let supersededBy = Map.ofList [ "fact-a", Some "fact-a2" ]

            let flagged =
                NarrativeSupersession.findStaleNarratives store "s1" 50 supersededBy
                |> Async.RunSynchronously

            Expect.equal (List.length flagged) 1 "exactly one narrative flagged"
            let (info, flags) = flagged[0]
            Expect.equal info.Id idA "the flagged narrative is the one citing fact-a"
            Expect.equal (flags |> List.map _.SupersededFactId) [ "fact-a" ] "flagged for fact-a"

            // Empty supersession set ⇒ no work, no flags.
            let none =
                NarrativeSupersession.findStaleNarratives store "s1" 50 Map.empty
                |> Async.RunSynchronously

            Expect.isEmpty none "an empty superseded set flags nothing"
        }
    ]