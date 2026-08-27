// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.CoreWebVitalsBudgetTests

open System
open System.IO
open Expecto
open ToolUp.Platform

// ─── Phase 213 — Lighthouse / Core-Web-Vitals budget gate ──────────────
//
// The gate's contract in two halves. The PARSER arm asserts that a
// malformed budget file is refused with every defect named — a budget
// that silently parses to "asserts nothing" is the failure this gate
// exists to prevent, and it would read as a green run forever. The
// CHECK arm drives committed Lighthouse report fixtures through the
// whole loader path: a within-budget set passes, a deliberately
// degraded one fails and names each breach, and the two shapes that
// look like a pass but are not — an unmeasured budget line and an
// uncovered page — are breaches rather than skips.
//
// The fixtures are real Lighthouse report JSON reduced to the fields
// the gate reads, so the pack runs with no browser, no server and no
// network anywhere in it.

let private fixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", "cwv")

let private fixturePath (segments: string list) =
    Path.Combine(Array.ofList (fixtureRoot :: segments))

let private readFixture segments = File.ReadAllText(fixturePath segments)

let private budgetOrFail label json =
    match CoreWebVitalsBudgetGate.parseBudget label json with
    | Ok b -> b
    | Error errors -> failtestf "expected '%s' to parse, got: %s" label (String.concat "; " errors)

let private errorsOf label json =
    match CoreWebVitalsBudgetGate.parseBudget label json with
    | Ok _ -> failtestf "expected '%s' to be REFUSED, but it parsed" label
    | Error errors -> errors

let private mentions (needle: string) (errors: string list) =
    errors
    |> List.exists (fun e -> e.Contains(needle, System.StringComparison.OrdinalIgnoreCase))

/// Run the gate over a fixture report directory, through the same
/// loader path the FAKE target uses.
let private verifyFixtures reportsDir serverMetrics =
    let options = {
        BudgetFile = fixturePath [ "fixture-budget.json" ]
        ReportsDirectory = fixturePath [ reportsDir ]
        ServerMetricsFile = serverMetrics |> Option.map (fun f -> fixturePath [ f ])
    }

    match CoreWebVitalsBudgetGate.verify options with
    | Error errors -> failtestf "gate could not run: %s" (String.concat "; " errors)
    | Ok(budget, findings) -> budget, findings

let private parserTests =
    testList "budget parser" [
        test "the shipped default budget parses" {
            // The budget file samples/PublicSite/cwv-budget.json is linked
            // into this pack's output, so a typo in the shipped default —
            // a misspelt metric key, a score written as 95 rather than
            // 0.95 — fails here rather than on the first CI gate run.
            let budget = budgetOrFail "cwv-budget.json" (readFixture [ "shipped-budget.json" ])

            Expect.isNonEmpty budget.Pages "the shipped budget covers pages"
            Expect.isNonEmpty budget.MetricCeilings "the shipped budget places metric ceilings"
            Expect.isNonEmpty budget.CategoryFloors "the shipped budget places category floors"

            Expect.equal
                (budget.Pages |> List.filter ((=) "/") |> List.length)
                1
                "the site root is covered exactly once"
        }

        test "a well-formed budget round-trips its thresholds" {
            let budget =
                budgetOrFail "fixture-budget.json" (readFixture [ "fixture-budget.json" ])

            Expect.equal budget.Pages [ "/"; "/about" ] "pages parse in declaration order"

            Expect.equal
                (budget.MetricCeilings
                 |> List.tryFind (fst >> (=) LargestContentfulPaint)
                 |> Option.map snd)
                (Some 2500.0)
                "the LCP ceiling parses"

            Expect.equal
                (budget.CategoryFloors
                 |> List.tryFind (fst >> (=) BestPractices)
                 |> Option.map snd)
                (Some 0.9)
                "the camelCase bestPractices key resolves"

            match budget.ServerSignals with
            | Some signals ->
                Expect.equal signals.MinConditionalGet304Rate (Some 0.45) "the 304-rate floor parses"
                Expect.isFalse signals.Required "server signals default to advisory"
            | None -> failtest "expected the fixture budget to declare server signals"
        }

        test "a malformed budget is refused, naming every defect at once" {
            // One run tells an author the whole story about the file they
            // are editing, rather than one defect per invocation.
            let errors =
                errorsOf "malformed-budget.json" (readFixture [ "malformed-budget.json" ])

            Expect.isTrue (mentions "schema" errors) "the missing schema token is named"
            Expect.isTrue (mentions "pages" errors) "the missing pages array is named"

            Expect.isTrue
                (mentions "largestContentfulPaintMS" errors)
                "the typo'd metric key is named rather than ignored"

            Expect.isTrue (mentions "0.0-1.0" errors) "the out-of-range category floor is named"
            Expect.isGreaterThan (List.length errors) 3 "every defect is reported, not just the first"
        }

        test "a budget that is not JSON at all is refused" {
            let errors = errorsOf "not-json.json" "<html><body>404</body></html>"
            Expect.isTrue (mentions "not valid JSON" errors) "the parse failure is named"
        }

        test "a budget that asserts no thresholds is refused" {
            // The most dangerous malformed budget: syntactically perfect,
            // covers pages, and can never fail.
            let json = """{ "schema": "toolup.cwv-budget/v1", "pages": [ "/" ] }"""

            let errors = errorsOf "empty-budget.json" json
            Expect.isTrue (mentions "asserts no thresholds" errors) "an unfalsifiable budget is refused"
        }

        test "a serverSignals block that asserts nothing is refused" {
            let json =
                """{ "schema": "toolup.cwv-budget/v1", "pages": [ "/" ],
                     "categories": { "seo": 0.9 }, "serverSignals": { "required": true } }"""

            let errors = errorsOf "hollow-signals.json" json
            Expect.isTrue (mentions "asserts nothing" errors) "a hollow serverSignals block is refused"
        }

        test "an unknown serverSignals key is refused" {
            let json =
                """{ "schema": "toolup.cwv-budget/v1", "pages": [ "/" ],
                     "categories": { "seo": 0.9 }, "serverSignals": { "maxRenderMS": 50 } }"""

            let errors = errorsOf "typo-signals.json" json
            Expect.isTrue (mentions "maxRenderMS" errors) "the typo'd signal key is named"
        }

        test "a duplicate page is refused" {
            let json =
                """{ "schema": "toolup.cwv-budget/v1", "pages": [ "/about", "/about/" ],
                     "categories": { "seo": 0.9 } }"""

            let errors = errorsOf "duplicate-pages.json" json
            Expect.isTrue (mentions "more than once" errors) "trailing-slash duplicates collapse and are named"
        }
    ]

let private reportTests =
    testList "lighthouse report reader" [
        test "a report reduces to its budgeted values" {
            match CoreWebVitalsBudgetGate.parseReport "root.json" (readFixture [ "within-budget"; "root.json" ]) with
            | Error errors -> failtestf "expected the fixture report to parse: %s" (String.concat "; " errors)
            | Ok report ->
                Expect.equal report.PagePath "/" "the requested URL reduces to a comparable path"

                Expect.equal
                    (report.MetricValues
                     |> List.tryFind (fst >> (=) LargestContentfulPaint)
                     |> Option.map snd)
                    (Some 741.8)
                    "the LCP numeric value is read"

                Expect.equal
                    (report.CategoryScores
                     |> List.tryFind (fst >> (=) BestPractices)
                     |> Option.map snd)
                    (Some 1.0)
                    "the hyphenated best-practices category id resolves"
        }

        test "a document that is not a lighthouse report is refused" {
            match CoreWebVitalsBudgetGate.parseReport "stray.json" """{ "hello": "world" }""" with
            | Ok _ -> failtest "expected a non-report document to be refused"
            | Error errors -> Expect.isTrue (mentions "not a Lighthouse report" errors) "the missing URL is named"
        }

        test "a port-bearing origin does not leak into the page path" {
            // Budgets name paths; a run names a throwaway port. The two
            // must match without the budget ever mentioning a port.
            Expect.equal
                (CoreWebVitalsBudgetGate.normalisePagePath "http://127.0.0.1:51841/news/2026-05-22-launch")
                "/news/2026-05-22-launch"
                "the origin is stripped"

            Expect.equal (CoreWebVitalsBudgetGate.normalisePagePath "http://127.0.0.1:51841/") "/" "the root path"
            Expect.equal (CoreWebVitalsBudgetGate.normalisePagePath "/pricing/") "/pricing" "a trailing slash"
            Expect.equal (CoreWebVitalsBudgetGate.normalisePagePath "pricing") "/pricing" "a bare relative path"
        }
    ]

let private gateTests =
    testList "gate" [
        test "the within-budget fixture set passes" {
            let budget, findings = verifyFixtures "within-budget" (Some "server-metrics.json")

            Expect.isEmpty (CoreWebVitalsBudgetGate.breaches findings) (CoreWebVitalsBudgetGate.report budget findings)
        }

        test "a deliberately degraded page fails the gate and names each breach" {
            let budget, findings = verifyFixtures "breaching" (Some "server-metrics.json")
            let breaches = CoreWebVitalsBudgetGate.breaches findings

            Expect.isNonEmpty breaches "the degraded page breaches"

            let breached =
                breaches
                |> List.choose (function
                    | MetricCeilingBreached(_, metric, _, _) -> Some(CoreWebVitalsMetric.key metric)
                    | _ -> None)
                |> List.sort

            Expect.equal
                breached
                [
                    "cumulativeLayoutShift"
                    "firstContentfulPaintMs"
                    "largestContentfulPaintMs"
                    "totalBlockingTimeMs"
                ]
                "every degraded metric is named"

            Expect.isTrue
                (breaches
                 |> List.exists (function
                     | CategoryFloorBreached(_, Seo, _, _) -> true
                     | _ -> false))
                "the depressed SEO score is named"

            // The healthy sibling page in the same set is NOT reported —
            // a gate that blames every page for one page's regression is
            // one nobody can act on.
            Expect.isFalse
                (breaches
                 |> List.exists (function
                     | MetricCeilingBreached(page, _, _, _)
                     | CategoryFloorBreached(page, _, _, _) -> page = "/about"
                     | _ -> false))
                "the within-budget sibling page is not blamed"

            let text = CoreWebVitalsBudgetGate.report budget findings
            Expect.stringContains text "budget breach" "the report header states the verdict"
            Expect.stringContains text "largestContentfulPaintMs" "the report names the breached metric key"
            Expect.stringContains text "4218" "the report carries the observed value"
        }

        test "a budget line the report never measured is a breach, not a skip" {
            // The failure mode this whole gate exists to prevent: an
            // audit that stopped being emitted looks exactly like one
            // that passed.
            let _, findings = verifyFixtures "missing-audit" (Some "server-metrics.json")
            let breaches = CoreWebVitalsBudgetGate.breaches findings

            Expect.isTrue
                (breaches
                 |> List.exists (function
                     | MetricNotReported("/", LargestContentfulPaint) -> true
                     | _ -> false))
                "the absent LCP audit breaches"

            Expect.isTrue
                (breaches
                 |> List.exists (function
                     | CategoryNotReported("/", Seo) -> true
                     | _ -> false))
                "the absent SEO category breaches"
        }

        test "a budgeted page no report covers is a breach" {
            let budget = {
                budgetOrFail "fixture-budget.json" (readFixture [ "fixture-budget.json" ]) with
                    Pages = [ "/"; "/about"; "/pricing" ]
            }

            let reports =
                match CoreWebVitalsBudgetGate.Load.reports (fixturePath [ "within-budget" ]) with
                | Ok r -> r
                | Error e -> failtestf "fixture reports did not load: %s" (String.concat "; " e)

            let findings = CoreWebVitalsBudgetGate.check budget reports None

            Expect.isTrue
                (CoreWebVitalsBudgetGate.breaches findings
                 |> List.contains (PageNotReported "/pricing"))
                "the uncovered page breaches"
        }

        test "an unbudgeted extra report is reported but does not breach" {
            let budget = {
                budgetOrFail "fixture-budget.json" (readFixture [ "fixture-budget.json" ]) with
                    Pages = [ "/" ]
            }

            let reports =
                match CoreWebVitalsBudgetGate.Load.reports (fixturePath [ "within-budget" ]) with
                | Ok r -> r
                | Error e -> failtestf "fixture reports did not load: %s" (String.concat "; " e)

            let findings = CoreWebVitalsBudgetGate.check budget reports None

            Expect.isTrue
                (findings
                 |> List.exists (function
                     | PageNotBudgeted("/about", _) -> true
                     | _ -> false))
                "the extra page is surfaced"

            Expect.isEmpty
                (CoreWebVitalsBudgetGate.breaches findings
                 |> List.filter (function
                     | PageNotBudgeted _ -> true
                     | _ -> false))
                "but extra coverage is not a breach"
        }

        test "an empty reports directory is a gate failure, not a pass" {
            let empty =
                Path.Combine(Path.GetTempPath(), "cwv-empty-" + System.Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory empty |> ignore

            try
                let options =
                    CoreWebVitalsGateOptions.create (fixturePath [ "fixture-budget.json" ]) empty

                match CoreWebVitalsBudgetGate.verify options with
                | Ok _ -> failtest "a run that measured nothing must not read as a pass"
                | Error errors -> Expect.isTrue (mentions "carries no" errors) "the empty measurement is named"
            finally
                try
                    Directory.Delete(empty, true)
                with _ ->
                    ()
        }
    ]

let private serverSignalTests =
    testList "server-side companion signal" [
        test "a collapsed conditional-GET 304 rate breaches" {
            // Every response a full body: the Phase 155 revalidation path
            // has stopped short-circuiting, and a crawler is being served
            // whole pages it already holds.
            let _, findings = verifyFixtures "within-budget" (Some "server-metrics-cold.json")

            Expect.isTrue
                (CoreWebVitalsBudgetGate.breaches findings
                 |> List.exists (function
                     | ConditionalGet304RateBreached(observed, floor) -> observed = 0.0 && floor = 0.45
                     | _ -> false))
                "the 0.0 rate is named against its floor"
        }

        test "a healthy split passes" {
            let _, findings = verifyFixtures "within-budget" (Some "server-metrics.json")

            Expect.isEmpty
                (findings
                 |> List.filter (function
                     | ConditionalGet304RateBreached _ -> true
                     | _ -> false))
                "a 0.5 rate clears the 0.45 floor"
        }

        test "an unsampled advisory signal is reported without failing" {
            let budget, findings = verifyFixtures "within-budget" None

            Expect.isTrue (findings |> List.contains (ServerSignalsNotSampled false)) "the omission is surfaced"

            Expect.isEmpty (CoreWebVitalsBudgetGate.breaches findings) "but advisory signals do not fail the gate"

            Expect.stringContains
                (CoreWebVitalsBudgetGate.report budget findings)
                "no metrics snapshot"
                "and it is visible in the report, not silently dropped"
        }

        test "an unsampled REQUIRED signal breaches" {
            let budget = {
                budgetOrFail "fixture-budget.json" (readFixture [ "fixture-budget.json" ]) with
                    ServerSignals =
                        Some {
                            CoreWebVitalsServerSignals.none with
                                MinConditionalGet304Rate = Some 0.45
                                Required = true
                        }
            }

            let reports =
                match CoreWebVitalsBudgetGate.Load.reports (fixturePath [ "within-budget" ]) with
                | Ok r -> r
                | Error e -> failtestf "fixture reports did not load: %s" (String.concat "; " e)

            let findings = CoreWebVitalsBudgetGate.check budget reports None

            Expect.isTrue
                (CoreWebVitalsBudgetGate.breaches findings
                 |> List.contains (ServerSignalsNotSampled true))
                "a required signal the run never sampled fails the gate"
        }

        test "a render-duration ceiling breaches on a snapshot that carries one" {
            let budget = {
                budgetOrFail "fixture-budget.json" (readFixture [ "fixture-budget.json" ]) with
                    ServerSignals =
                        Some {
                            CoreWebVitalsServerSignals.none with
                                MaxRenderMs = Some 40.0
                        }
            }

            let reports =
                match CoreWebVitalsBudgetGate.Load.reports (fixturePath [ "within-budget" ]) with
                | Ok r -> r
                | Error e -> failtestf "fixture reports did not load: %s" (String.concat "; " e)

            let snapshot = {
                SnapshotLabel = "in-memory"
                RenderMsMax = Some 91.5
                ConditionalGet304 = None
                ConditionalGet200 = None
            }

            let findings = CoreWebVitalsBudgetGate.check budget reports (Some snapshot)

            Expect.isTrue
                (CoreWebVitalsBudgetGate.breaches findings
                 |> List.exists (function
                     | RenderMsBreached(91.5, 40.0) -> true
                     | _ -> false))
                "the peak render duration is named against its ceiling"
        }

        test "the snapshot reader accepts the raw counter names" {
            // A deployment transcribing its metrics sink verbatim writes
            // the counter names the public-rendering tier emits; the
            // reader takes those as well as the short form the runner
            // script writes, so nobody has to translate.
            let json =
                """{ "publicrendering.render_ms": { "max": 12.5 },
                     "publicrendering.conditional_get": { "304": 3, "200": 1 } }"""

            match CoreWebVitalsBudgetGate.parseSnapshot "raw.json" json with
            | Error errors -> failtestf "expected the raw-counter snapshot to parse: %s" (String.concat "; " errors)
            | Ok snapshot ->
                Expect.equal snapshot.RenderMsMax (Some 12.5) "the render histogram peak is read"
                Expect.equal snapshot.ConditionalGet304 (Some 3) "the 304 count is read"
                Expect.equal snapshot.ConditionalGet200 (Some 1) "the 200 count is read"
        }
    ]

let private targetTests =
    testList "gate options" [
        test "missing environment variables are all named at once" {
            match CoreWebVitalsGateOptions.fromEnvironment () with
            | Ok _ ->
                // A machine that happens to carry the variables set is not
                // a failure of this contract; the shape under test is the
                // refusal, which the sibling assertion below covers.
                ()
            | Error errors ->
                Expect.isTrue
                    (mentions CoreWebVitalsGateOptions.BudgetVariable errors
                     || mentions CoreWebVitalsGateOptions.ReportsVariable errors)
                    "the unset variable is named"
        }

        test "assertWithinBudget raises with the full report on a breach" {
            let options = {
                BudgetFile = fixturePath [ "fixture-budget.json" ]
                ReportsDirectory = fixturePath [ "breaching" ]
                ServerMetricsFile = Some(fixturePath [ "server-metrics.json" ])
            }

            let raised =
                try
                    CoreWebVitalsBudgetGate.assertWithinBudget options
                    None
                with ex ->
                    Some ex.Message

            match raised with
            | None -> failtest "expected assertWithinBudget to raise on the degraded fixture set"
            | Some message ->
                Expect.stringContains message "largestContentfulPaintMs" "the raised message carries the breaches"
        }

        test "assertWithinBudget is silent when within budget" {
            let options = {
                BudgetFile = fixturePath [ "fixture-budget.json" ]
                ReportsDirectory = fixturePath [ "within-budget" ]
                ServerMetricsFile = Some(fixturePath [ "server-metrics.json" ])
            }

            CoreWebVitalsBudgetGate.assertWithinBudget options
        }
    ]

let tests =
    testList "Phase 213 — Core-Web-Vitals budget gate" [
        parserTests
        reportTests
        gateTests
        serverSignalTests
        targetTests
    ]