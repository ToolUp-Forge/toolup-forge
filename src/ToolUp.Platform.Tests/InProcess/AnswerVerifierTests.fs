// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AnswerVerifierTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI
open ToolUp.AI.AnswerVerifier

// ─── Phase 523 — numeric-fidelity answer gate ────────────────────────
//
// Covered: the canonicalisation table (unicode minus / percent-vs-fraction
// / currency / thousands / parenthesised negatives / rounding-aware match),
// verdicts across the three states (Verified / Unmatched / NoFactsInScope),
// the RetrievedSource → ScopedFact projection, and the gate modes —
// `Off`-mode byte-parity plus `Annotate` footnote / `Strict` withholding.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// A metric registered with a percent display format (P1) — exercises the
/// registry-driven precision path in `verify`.
let private shareMetric: MetricDefinition = {
    Id = "share"
    Name = "Share of Voice"
    Unit = "%"
    Dimensionality = "ratio"
    Direction = HigherIsBetter
    DisplayFormat = "P1"
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = None
    RecomputePolicy = None
    RollUp = None
    Context = None
}

let private registry: IMetricRegistry =
    MetricRegistry.build [
        {
            Module = "test"
            Definition = shareMetric
        }
    ] []

let private factSource (factId: string) (rendering: string) : RetrievedSource = {
    DocumentId = ""
    DocumentName = ""
    Snippet = rendering
    Score = 1.0
    Origin = Fact
    LocationHint = None
    OriginalRef = None
    Scope = None
    ChunkId = None
    FactId = Some factId
    FactRendering = Some rendering
    FactFreshness = Some FactFresh
    FactSupersededBy = None
    Span = None
}

let private docSource (snippet: string) : RetrievedSource = {
    DocumentId = "d1"
    DocumentName = "notes.txt"
    Snippet = snippet
    Score = 0.4
    Origin = Note
    LocationHint = None
    OriginalRef = None
    Scope = None
    ChunkId = Some "c1"
    FactId = None
    FactRendering = None
    FactFreshness = None
    FactSupersededBy = None
    Span = None
}

/// Build a `ScopedFact` directly (metric id supplied so the registry path
/// is exercised — the runtime projection leaves `Metric = ""`).
let private scoped (factId: string) (rendering: string) (metric: string) : ScopedFact = {
    FactId = factId
    Rendering = rendering
    Metric = metric
    Inputs = []
}

/// A metric that sums across subjects, within one unit.
let private revenueMetric: MetricDefinition = {
    shareMetric with
        Id = "revenue"
        Name = "Revenue"
        Unit = "GBP"
        Dimensionality = "currency"
        DisplayFormat = "N0"
        RollUp = Some(Additive 1m)
}

let private derivedRegistry: IMetricRegistry =
    MetricRegistry.build [
        {
            Module = "test"
            Definition = revenueMetric
        }
        {
            Module = "test"
            Definition = {
                shareMetric with
                    RollUp = Some NonAdditive
            }
        }
    ] []

/// A total a fact tool computed from `inputs`.
let private derivedTotal (rendering: string) (metric: string) (inputs: string list) : ScopedFact = {
    FactId = ""
    Rendering = rendering
    Metric = metric
    Inputs = inputs
}

/// Two sites' figures and the total a fact tool summed from them.
let private twoSites = [
    scoped "site-a" "1,200" "revenue"
    scoped "site-b" "800" "revenue"
    derivedTotal "2,000" "revenue" [ "site-a"; "site-b" ]
]

let tests =
    testList "Phase 523 numeric-fidelity answer gate" [

        // ── 523.A — canonicalisation table ────────────────────────────
        testList "canonicalisation" [
            testCase "unicode minus folds to ASCII"
            <| fun _ -> Expect.equal (Canonical.parse "−1.34") (Some(-1.34m, 2)) "U+2212 minus parses as -1.34"

            testCase "percent folds to its fraction"
            <| fun _ -> Expect.equal (Canonical.parse "-134%") (Some(-1.34m, 2)) "-134% is the fraction -1.34"

            testCase "currency + thousands separators strip"
            <| fun _ -> Expect.equal (Canonical.parse "£21,800") (Some(21800m, 0)) "£21,800 is 21800"

            testCase "parenthesised value is negative"
            <| fun _ -> Expect.equal (Canonical.parse "(1,234)") (Some(-1234m, 0)) "accountancy negative"

            testCase "percent with a decimal"
            <| fun _ -> Expect.equal (Canonical.parse "15.0%") (Some(0.15m, 3)) "15.0% is 0.15 (fraction precision 3)"

            testCase "non-numeric parses to None"
            <| fun _ -> Expect.isNone (Canonical.parse "n/a") "no numeric core"

            testCase "a rounded quote matches the fuller fact value at the quoted precision"
            <| fun _ ->
                // -1.3 (1 dp) is a faithful rounding of -1.34.
                Expect.isTrue (Canonical.valuesMatch 1 -1.3m -1.34m) "−1.3 matches −1.34 at 1 dp"

            testCase "-134% matches a stored -1.34"
            <| fun _ -> Expect.isTrue (Canonical.valuesMatch 2 -1.34m -1.34m) "percent form matches the fraction"

            testCase "a genuinely different number does not match"
            <| fun _ -> Expect.isFalse (Canonical.valuesMatch 0 25000m 21800m) "£25,000 ≠ £21,800"

            testCase "format precision: P1 is three fraction decimals"
            <| fun _ -> Expect.equal (Canonical.formatPrecision "P1") (Some 3) "percent scales by 100"

            testCase "format precision: C0 is zero"
            <| fun _ -> Expect.equal (Canonical.formatPrecision "C0") (Some 0) "currency, no decimals"

            testCase "empty format has no precision"
            <| fun _ -> Expect.isNone (Canonical.formatPrecision "") "verbatim"
        ]

        // ── 523.B — verdicts across the three states ──────────────────
        testList "verdicts" [
            testCase "a verbatim fact quote verifies"
            <| fun _ ->
                let v =
                    NumericFidelity.verify
                        "Revenue was £21,800 this quarter."
                        [ scoped "f1" "£21,800" "" ]
                        None
                        "Annotate"

                Expect.equal v.Verified 1 "one verified"
                Expect.equal v.Unmatched 0 "none unmatched"
                Expect.equal (v.Numbers |> List.head).Verdict NumberVerified "verdict is Verified"
                Expect.equal (v.Numbers |> List.head).MatchedFactId (Some "f1") "matched fact id surfaced"

            testCase "an alternative legal rendering still verifies"
            <| fun _ ->
                // The fact renders as "£21,800"; the answer quotes "21800".
                let v =
                    NumericFidelity.verify "It came to 21800." [ scoped "f1" "£21,800" "" ] None "Annotate"

                Expect.equal v.Verified 1 "21800 verifies against £21,800"

            testCase "an invented number is unmatched"
            <| fun _ ->
                let v =
                    NumericFidelity.verify "Revenue was £25,000." [ scoped "f1" "£21,800" "" ] None "Annotate"

                Expect.equal v.Unmatched 1 "one unmatched"
                Expect.equal (v.Numbers |> List.head).Verdict NumberUnmatched "verdict is Unmatched"

            testCase "no facts in scope ⇒ NoFactsInScope, never Unmatched"
            <| fun _ ->
                let v = NumericFidelity.verify "Revenue was £21,800." [] None "Annotate"
                Expect.equal v.Unverifiable 1 "one unverifiable"
                Expect.equal v.Unmatched 0 "never flagged as unmatched without facts"
                Expect.equal (v.Numbers |> List.head).Verdict NoFactsInScope "verdict is NoFactsInScope"

            testCase "percent fact verifies a fraction-quoted token (registry-driven)"
            <| fun _ ->
                // Fact renders "15.0%" (0.15); the answer quotes the fraction.
                let v =
                    NumericFidelity.verify
                        "Its share is 0.15 of the market."
                        [ scoped "f2" "15.0%" "share" ]
                        (Some registry)
                        "Annotate"

                Expect.equal v.Verified 1 "0.15 verifies against 15.0%"

            testCase "citation markers are not treated as quantities"
            <| fun _ ->
                let v =
                    NumericFidelity.verify "Revenue rose [1] sharply." [ scoped "f1" "£21,800" "" ] None "Annotate"

                Expect.equal v.Numbers.Length 0 "the [1] citation marker is skipped"
        ]

        // ── ScopedFact projection ─────────────────────────────────────
        testList "scopedFacts projection" [
            testCase "fact-origin sources project; non-fact sources drop"
            <| fun _ ->
                let facts =
                    scopedFacts [ factSource "f1" "£21,800"; docSource "some prose with 500 in it" ]

                Expect.equal (facts |> List.map _.FactId) [ "f1" ] "only the fact-origin source projects"
                Expect.equal (facts |> List.map _.Rendering) [ "£21,800" ] "rendering carried"
        ]

        // ── A derived total, checked against the facts it was summed from ──
        testList "derived-value match" [
            testCase "a two-site total verifies, and its verdict names both inputs"
            <| fun _ ->
                let v =
                    NumericFidelity.verify "Both sites together took £2,000." twoSites (Some derivedRegistry) "Strict"

                Expect.equal v.Verified 1 "the total verifies"
                Expect.equal v.Unmatched 0 "nothing unmatched"
                let number = List.exactlyOne v.Numbers
                Expect.equal number.DerivedFrom (Some [ "site-a"; "site-b" ]) "the verdict names the facts"
                Expect.isNone number.MatchedFactId "and no tool, and no single fact"

            testCase "a total within the declared tolerance verifies"
            <| fun _ ->
                let v =
                    NumericFidelity.verify "Together: 2,001." twoSites (Some derivedRegistry) "Strict"

                Expect.equal v.Verified 1 "one unit of slack is the metric's declared tolerance"

            testCase "a total off by more than the tolerance does not verify"
            <| fun _ ->
                let v =
                    NumericFidelity.verify "Together: 2,005." twoSites (Some derivedRegistry) "Strict"

                Expect.equal v.Unmatched 1 "five units out is a different number"
                Expect.isNone (List.exactlyOne v.Numbers).DerivedFrom "and names no inputs"

            testCase "a total quoted when one input is out of scope does not verify"
            <| fun _ ->
                let facts = [
                    scoped "site-a" "1,200" "revenue"
                    derivedTotal "2,000" "revenue" [ "site-a"; "site-b" ]
                ]

                let v =
                    NumericFidelity.verify "Together: 2,000." facts (Some derivedRegistry) "Strict"

                Expect.equal v.Unmatched 1 "site-b is not in scope, so the total stands on nothing"

            testCase "a non-additive metric's total never verifies"
            <| fun _ ->
                let facts = [
                    scoped "a" "10.0%" "share"
                    scoped "b" "20.0%" "share"
                    derivedTotal "30.0%" "share" [ "a"; "b" ]
                ]

                let v =
                    NumericFidelity.verify "Combined share 30.0%." facts (Some derivedRegistry) "Strict"

                Expect.equal v.Unmatched 1 "a share does not add up, whatever a tool printed"

            testCase "without a registry no total verifies"
            <| fun _ ->
                let v = NumericFidelity.verify "Together: 2,000." twoSites None "Strict"
                Expect.equal v.Unmatched 1 "no declaration, no tolerance, no match"

            testCase "the inputs' own figures still verify as facts"
            <| fun _ ->
                let v =
                    NumericFidelity.verify "Site A took 1,200 and B 800." twoSites (Some derivedRegistry) "Strict"

                Expect.equal v.Verified 2 "each site's figure is a fact in scope"
                Expect.isTrue (v.Numbers |> List.forall (fun n -> n.DerivedFrom.IsNone)) "matched as facts"
        ]

        // ── The facts a fact tool's result put in front of the model ───
        testList "toolResultFacts" [
            testCase "a fact entry carries its id, rendering and metric"
            <| fun _ ->
                let facts =
                    toolResultFacts
                        """{"ranked":[{"rank":1,"factId":"f1","metric":"revenue","rendering":"1,200"}],"withheldCount":0}"""

                Expect.equal facts [ scoped "f1" "1,200" "revenue" ] "one fact"

            testCase "a withheld marker contributes nothing"
            <| fun _ ->
                let facts =
                    toolResultFacts
                        """{"facts":[],"withheld":[{"factId":"f9","policyRef":"licence-x","status":"withheld"}]}"""

                Expect.isEmpty facts "an id and a policy are not a value"

            testCase "a derived total and its inputs"
            <| fun _ ->
                let facts =
                    toolResultFacts
                        """{"totals":[{"group":"bakery","metric":"revenue","rendering":"2,000","inputFactIds":["a","b"],"inputs":[{"factId":"a","metric":"revenue","rendering":"1,200"},{"factId":"b","metric":"revenue","rendering":"800"}]}]}"""

                Expect.equal
                    facts
                    [
                        derivedTotal "2,000" "revenue" [ "a"; "b" ]
                        scoped "a" "1,200" "revenue"
                        scoped "b" "800" "revenue"
                    ]
                    "the total, then each input"

            testCase "a result that is not JSON contributes nothing"
            <| fun _ -> Expect.isEmpty (toolResultFacts "The tool was cancelled.") "no facts"
        ]

        // ── 523.C — gate modes ────────────────────────────────────────
        testList "gate modes" [
            testCase "Off (absent gate) is byte-identical, no verdict"
            <| fun _ ->
                let answer = "Revenue was £25,000."

                let text, verdict =
                    runVerificationStage
                        None
                        None
                        [ factSource "f1" "£21,800" ]
                        answer
                        None
                        None
                        "scope"
                        (System.Guid.NewGuid())
                        (System.Guid.NewGuid())
                        "prov"
                        "model"
                        silentLogger
                    |> Async.RunSynchronously

                Expect.equal text answer "answer returned verbatim"
                Expect.isNone verdict "no verdict when Off"

            testCase "explicit Off mode is also byte-identical"
            <| fun _ ->
                let answer = "Revenue was £25,000."

                let gate =
                    Some {
                        Mode = AnswerGateOff
                        Verifier = NumericFidelityVerifier()
                    }

                let text, verdict =
                    runVerificationStage
                        gate
                        None
                        [ factSource "f1" "£21,800" ]
                        answer
                        None
                        None
                        "scope"
                        (System.Guid.NewGuid())
                        (System.Guid.NewGuid())
                        "prov"
                        "model"
                        silentLogger
                    |> Async.RunSynchronously

                Expect.equal text answer "answer returned verbatim under Off"
                Expect.isNone verdict "no verdict under Off"

            testCase "Annotate appends a footnote and returns the verdict"
            <| fun _ ->
                let answer = "Revenue was £25,000 last year."

                let gate =
                    Some {
                        Mode = AnswerGateAnnotate
                        Verifier = NumericFidelityVerifier()
                    }

                let text, verdict =
                    runVerificationStage
                        gate
                        None
                        [ factSource "f1" "£21,800" ]
                        answer
                        None
                        None
                        "scope"
                        (System.Guid.NewGuid())
                        (System.Guid.NewGuid())
                        "prov"
                        "model"
                        silentLogger
                    |> Async.RunSynchronously

                Expect.stringContains text "£25,000" "the body is not rewritten under Annotate"
                Expect.stringContains text "Unverified figures" "a footnote is appended"
                Expect.equal (verdict |> Option.map _.Unmatched) (Some 1) "verdict reports one unmatched"

            testCase "Strict withholds the unverified sentence behind an inline flag"
            <| fun _ ->
                let answer = "Revenue was £25,000 last year. Margins held steady."

                let gate =
                    Some {
                        Mode = AnswerGateStrict
                        Verifier = NumericFidelityVerifier()
                    }

                let text, verdict =
                    runVerificationStage
                        gate
                        None
                        [ factSource "f1" "£21,800" ]
                        answer
                        None
                        None
                        "scope"
                        (System.Guid.NewGuid())
                        (System.Guid.NewGuid())
                        "prov"
                        "model"
                        silentLogger
                    |> Async.RunSynchronously

                Expect.isFalse (text.Contains "£25,000") "the unverified figure is withheld"
                Expect.stringContains text "figure withheld" "an explicit inline flag replaces the sentence"
                Expect.stringContains text "Margins held steady." "the verified sentence survives"
                Expect.equal (verdict |> Option.map _.Unmatched) (Some 1) "verdict still reports the unmatched figure"

            testCase "Strict leaves a fully-verified answer intact"
            <| fun _ ->
                let answer = "Revenue was £21,800 this quarter."

                let gate =
                    Some {
                        Mode = AnswerGateStrict
                        Verifier = NumericFidelityVerifier()
                    }

                let text, verdict =
                    runVerificationStage
                        gate
                        None
                        [ factSource "f1" "£21,800" ]
                        answer
                        None
                        None
                        "scope"
                        (System.Guid.NewGuid())
                        (System.Guid.NewGuid())
                        "prov"
                        "model"
                        silentLogger
                    |> Async.RunSynchronously

                Expect.equal text answer "a verified answer is untouched"
                Expect.equal (verdict |> Option.map _.Verified) (Some 1) "one verified figure"
        ]

        // ── Gate-mode parse round-trip ────────────────────────────────
        testList "AnswerGateMode.parse" [
            testCase "recognised modes"
            <| fun _ ->
                Expect.equal (AnswerGateMode.parse "annotate") AnswerGateAnnotate "annotate"
                Expect.equal (AnswerGateMode.parse "Strict") AnswerGateStrict "case-insensitive"

            testCase "unknown / null resolves to Off"
            <| fun _ ->
                Expect.equal (AnswerGateMode.parse "nonsense") AnswerGateOff "unknown → Off"
                Expect.equal (AnswerGateMode.parse null) AnswerGateOff "null → Off"
        ]
    ]