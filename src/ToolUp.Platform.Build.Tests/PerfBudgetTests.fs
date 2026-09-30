// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.PerfBudgetTests

open System
open System.IO
open Expecto
open ToolUp.Platform

// ─── Phase 192 — cold-start / hot-path perf-budget gate ────────────────
//
// The gate's contract in three arms.
//
// The PARSER arm asserts that a malformed budget is refused with every
// defect named at once. A budget that silently parses to "asserts
// nothing" is the failure this gate exists to prevent, and it would
// read as a green run forever.
//
// The CHECK arm drives committed measurement fixtures through the whole
// loader path. A within-budget run passes; a regressed one fails and
// names both numbers; and the three shapes that LOOK like a pass and
// are not — a run that never observed the thing it timed, a statistic
// drawn from too few observations, and a budgeted metric the run
// quietly stopped measuring — are breaches rather than skips. The
// unobserved case is the important one: a boot that never became ready
// produces a very small number, so the failure mode of a broken
// measuring half is a SUSPICIOUSLY GOOD result.
//
// The SHIPPED-BUDGET arm parses the repo's own perf-budgets.json, so a
// typo in it fails this pack rather than the first CI gate run.
//
// Every fixture is committed JSON, so the pack runs with no process, no
// port and no clock anywhere in it — which is the whole reason the
// deciding half is separate from dev-scripts/perf-budget-gate.ps1.

let private fixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", "perf")

let private fixturePath (name: string) = Path.Combine(fixtureRoot, name)

let private readFixture name = File.ReadAllText(fixturePath name)

let private budgetOrFail label json =
    match PerfBudgetGate.parseBudget label json with
    | Ok b -> b
    | Error errors -> failtestf "expected '%s' to parse, got: %s" label (String.concat "; " errors)

let private runOrFail label json =
    match PerfBudgetGate.parseMeasurements label json with
    | Ok r -> r
    | Error errors -> failtestf "expected '%s' to parse, got: %s" label (String.concat "; " errors)

let private budgetErrors label json =
    match PerfBudgetGate.parseBudget label json with
    | Ok _ -> failtestf "expected '%s' to be REFUSED, but it parsed" label
    | Error errors -> errors

let private mentions (needle: string) (errors: string list) =
    errors
    |> List.exists (fun e -> e.Contains(needle, StringComparison.OrdinalIgnoreCase))

/// The shipped budget, read from the pack's own output directory (the
/// fsproj links it in). One budget file, so the tests and CI decide
/// against the same bytes.
let private shippedBudgetJson () = readFixture "shipped-budget.json"

/// A minimal hand-built budget for the arms that need to vary one thing.
let private budgetJson (body: string) =
    $"""{{ "schema": "toolup.perf-budget/v1", "subject": "test", "statistic": "min", {body} }}"""

let private defaultCeilings =
    """ "ceilings": { "coldStartMs": 2000, "hotPathMs": 5 }, "minimumSamples": { "coldStartMs": 5, "hotPathMs": 100 } """

let private parserTests =
    testList "budget parser" [
        test "a document with no schema token is refused rather than read as a budget" {
            let errors =
                budgetErrors "no-schema" """{ "subject": "x", "statistic": "min", "ceilings": { "coldStartMs": 1 } }"""

            Expect.isTrue (mentions "declares no 'schema'" errors) $"expected a schema complaint, got %A{errors}"
        }

        test "the measurement schema token is refused as a budget" {
            // Pointing TOOLUP_PERF_BUDGET at the measurements file is the
            // easy mistake; the two tokens differ so it cannot pass.
            let errors = budgetErrors "wrong-schema" (readFixture "within-budget.json")
            Expect.isTrue (mentions "this gate reads" errors) $"expected a schema mismatch, got %A{errors}"
        }

        test "every defect in one malformed budget is named in one run" {
            let errors =
                budgetErrors "malformed-budget.json" (readFixture "malformed-budget.json")

            Expect.isTrue (mentions "toolup.perf-budget/v1" errors) "the wrong schema token must be named"
            Expect.isTrue (mentions "warmStartMs" errors) "the unknown metric must be named"
            Expect.isTrue (mentions "coldStartMs' must be a number" errors) "the non-numeric ceiling must be named"
            Expect.isTrue (mentions "'median'" errors) "the unsupported statistic must be named"
            Expect.isTrue (mentions "no 'subject'" errors) "the missing subject must be named"
            Expect.isTrue (mentions "minimumSamples" errors) "the missing sample floor must be named"
            Expect.isTrue (mentions "absentAssemblies" errors) "the empty assembly name must be named"

            Expect.isGreaterThanOrEqual
                errors.Length
                6
                "one run must tell the author the whole story, not stop at the first defect"
        }

        test "an empty ceilings object is refused — a budget with no ceiling asserts nothing" {
            let errors =
                budgetErrors "empty" (budgetJson """ "ceilings": {}, "minimumSamples": {} """)

            Expect.isTrue (mentions "asserts nothing" errors) $"expected an empty-ceilings complaint, got %A{errors}"
        }

        test "a ceiling with no matching minimumSamples is refused" {
            let errors =
                budgetErrors
                    "no-floor"
                    (budgetJson """ "ceilings": { "coldStartMs": 2000 }, "minimumSamples": { "hotPathMs": 100 } """)

            Expect.isTrue
                (mentions "minimumSamples.coldStartMs" errors)
                $"expected the unfloored ceiling to be named, got %A{errors}"
        }

        test "a statistic other than min is refused, and the refusal says why" {
            let errors =
                budgetErrors
                    "median"
                    $"""{{ "schema": "toolup.perf-budget/v1", "subject": "x", "statistic": "median", {defaultCeilings} }}"""

            Expect.isTrue (mentions "one-sided" errors) $"the refusal must carry the reason, got %A{errors}"
        }
    ]

let private checkTests =
    let budget = budgetOrFail "shipped-budget.json" (shippedBudgetJson ())

    let findingsFor fixture =
        PerfBudgetGate.check budget (runOrFail fixture (readFixture fixture))

    testList "check" [
        test "a within-budget run passes, and still reports every ceiling with its headroom" {
            let findings = findingsFor "within-budget.json"

            Expect.isEmpty (PerfBudgetGate.breaches findings) $"expected no breach, got %A{findings}"

            Expect.hasLength
                findings
                (List.length budget.Ceilings)
                "every budget line must produce a finding, so a green report can never omit one"

            let text = PerfBudgetGate.report budget findings
            Expect.stringContains text "headroom" "a green run must show its slack without anyone asking"
            Expect.stringContains text "within budget" "the verdict line must be present"
        }

        test "a regressed run breaches, and the message carries the measured value AND the budget" {
            let findings = findingsFor "breaching.json"
            let breaches = PerfBudgetGate.breaches findings

            Expect.isNonEmpty breaches "the regression fixture must breach — this is the gate having teeth"

            let text = PerfBudgetGate.report budget findings
            Expect.stringContains text "11840.6" "the MEASURED value must be in the failure"
            Expect.stringContains text "6000" "the BUDGET must be in the failure beside it"
            Expect.stringContains text "41.77" "the measured hot path must be in the failure"

            Expect.isTrue
                (findings
                 |> List.exists (fun f ->
                     match f with
                     | CeilingBreached(ColdStartMs, _, _) -> true
                     | _ -> false))
                "cold start must be reported as breached"
        }

        test "a run that never observed what it timed is REFUSED, not read as a fast boot" {
            // The failure mode of a broken measuring half is a very small
            // number, so this is the case that decides whether the gate is
            // worth anything.
            let findings = findingsFor "never-started.json"

            Expect.isNonEmpty (PerfBudgetGate.breaches findings) "an unobserved measurement must never pass"

            Expect.isTrue
                (findings
                 |> List.exists (fun f ->
                     match f with
                     | MeasurementUnobserved _ -> true
                     | _ -> false))
                "the finding must say the run observed nothing, not that a ceiling was breached"

            let text = PerfBudgetGate.report budget findings
            Expect.stringContains text "never confirmed" "the report must say the number is not evidence"
        }

        test "a statistic drawn from too few observations is refused" {
            let findings = findingsFor "undersampled.json"

            Expect.isNonEmpty (PerfBudgetGate.breaches findings) "a 'min' over one run must not pass as a measurement"

            Expect.isTrue
                (findings
                 |> List.exists (fun f ->
                     match f with
                     | TooFewSamples(ColdStartMs, 1, 5) -> true
                     | _ -> false))
                "the finding must name both the observed count and the required one"
        }

        test "a budgeted metric the run stopped measuring is a breach, not a skip" {
            let findings = findingsFor "hot-path-missing.json"

            Expect.isTrue
                (findings |> List.contains (MetricNotMeasured HotPathMs))
                "an unmeasured budget line is indistinguishable from a passing one unless it fails"
        }

        test "an assembly the budget requires to be absent, present, is a breach" {
            // The zero-cost-when-unused invariant, byte-level: the
            // regression fixture's output carries the OTel companion.
            let findings = findingsFor "breaching.json"

            Expect.isTrue
                (findings
                 |> List.contains (ZeroCostAssemblyPresent "ToolUp.Metrics.OpenTelemetry"))
                "a companion nobody registered must not be in the deployment at all"
        }

        test "the within-budget run's output carries no OpenTelemetry assembly" {
            // The positive half of the same law — and a guard on the
            // fixture itself, so the arm above cannot pass vacuously by
            // both fixtures happening to be clean.
            let findings = findingsFor "within-budget.json"

            Expect.isFalse
                (findings
                 |> List.exists (fun f ->
                     match f with
                     | ZeroCostAssemblyPresent _ -> true
                     | _ -> false))
                "the baseline minimal shape must satisfy the zero-cost rule"
        }

        test "an empty output inventory cannot satisfy an absence rule" {
            let run = {
                runOrFail "within-budget.json" (readFixture "within-budget.json") with
                    OutputAssemblies = []
            }

            let findings = PerfBudgetGate.check budget run

            Expect.isTrue
                (findings
                 |> List.exists (fun f ->
                     match f with
                     | AssemblyInventoryEmpty _ -> true
                     | _ -> false))
                "absence asserted against an empty inventory is not evidence of absence"
        }

        test "a run reporting a different statistic than the budget declares is refused" {
            let run = runOrFail "within-budget.json" (readFixture "within-budget.json")

            let skewed = {
                run with
                    Samples = run.Samples |> List.map (fun s -> { s with Statistic = "median" })
            }

            let findings = PerfBudgetGate.check budget skewed

            Expect.isTrue
                (findings
                 |> List.exists (fun f ->
                     match f with
                     | StatisticMismatch _ -> true
                     | _ -> false))
                "a ceiling checked against a different statistic means something else entirely"
        }
    ]

let private shippedBudgetTests =
    testList "shipped budget" [
        test "perf-budgets.json parses, and covers both server metrics the gate knows" {
            let budget = budgetOrFail "shipped-budget.json" (shippedBudgetJson ())

            for metric in PerfMetric.server do
                Expect.isTrue
                    (budget.Ceilings |> List.exists (fun (m, _) -> m = metric))
                    $"the shipped budget must place a ceiling on '{PerfMetric.key metric}' — a metric the gate can measure but nothing budgets is measured and then discarded"
        }

        test "no shipped ceiling is more than 20x its recorded baseline" {
            // The anti-slack law, and the reason this gate does not carry
            // a "tighten it later" note anywhere. Its ceilings are
            // deliberately generous — they have to be, on a shared runner
            // whose loaded minimum was 3x its quiet one — but generous is
            // not unbounded. A widening that made the gate vacuous fails
            // HERE, in a pack that runs on every push, rather than being
            // noticed by whoever eventually reads the headroom line.
            let budget = budgetOrFail "shipped-budget.json" (shippedBudgetJson ())

            for metric, ceiling in budget.Ceilings do
                let baseline = budget.Baselines |> List.find (fun (m, _) -> m = metric) |> snd

                Expect.isLessThanOrEqual
                    ceiling
                    (baseline * 20.0)
                    $"'{PerfMetric.key metric}' allows {ceiling} ms against a {baseline} ms baseline — a ceiling that far above what the thing actually costs has stopped defending anything. Re-measure and lower it, or justify the new baseline in the budget's notes."
        }

        test "the shipped budget requires the OpenTelemetry companion to be absent" {
            let budget = budgetOrFail "shipped-budget.json" (shippedBudgetJson ())

            Expect.contains
                budget.AbsentAssemblies
                "ToolUp.Metrics.OpenTelemetry"
                "the Phase 9y zero-cost-when-unused conclusion is what this budget line defends"
        }

        test "every shipped ceiling has a recorded baseline, so its headroom means something" {
            let budget = budgetOrFail "shipped-budget.json" (shippedBudgetJson ())

            for metric, _ in budget.Ceilings do
                Expect.isTrue
                    (budget.Baselines |> List.exists (fun (m, _) -> m = metric))
                    $"'{PerfMetric.key metric}' has a ceiling but no baseline — nothing then says whether the ceiling has gone slack"
        }
    ]

// ─── Phase 849 — the client block ──────────────────────────────────────
//
// The browser-runtime ceilings ClientBench measures live in the SAME
// budget file, under `client`, in the server block's shape. The laws are
// the server block's laws — nothing passes silently, widening is a file
// change, slack is bounded — plus one of its own: the two blocks'
// metric sets are disjoint, so a ceiling placed in the wrong block is an
// unknown metric there rather than a budget checked against the other
// half's measurements.

let private clientBudgetOrFail label json =
    match PerfBudgetGate.parseClientBudget label json with
    | Ok b -> b
    | Error errors -> failtestf "expected the client block of '%s' to parse, got: %s" label (String.concat "; " errors)

let private clientBudgetErrors label json =
    match PerfBudgetGate.parseClientBudget label json with
    | Ok _ -> failtestf "expected the client block of '%s' to be REFUSED, but it parsed" label
    | Error errors -> errors

let private clientBlock =
    """ "client": { "subject": "bench", "statistic": "min", "ceilings": { "bootMs": 3000, "decodePerResponseUs": 100, "viewPerDispatchUs": 5000 }, "minimumSamples": { "bootMs": 5, "decodePerResponseUs": 9, "viewPerDispatchUs": 9 }, "baselines": { "bootMs": 300, "decodePerResponseUs": 7, "viewPerDispatchUs": 400 } } """

let private clientRun (boot: float) (decode: float) (view: float) =
    let sample metric (value: float) samples =
        let v = value.ToString(Globalization.CultureInfo.InvariantCulture)

        $"""{{ "metric": "{metric}", "statistic": "min", "value": {v}, "samples": {samples}, "observed": true, "evidence": "fixture" }}"""

    let samples =
        String.Join(
            ", ",
            [
                sample "bootMs" boot 6
                sample "decodePerResponseUs" decode 9
                sample "viewPerDispatchUs" view 9
            ]
        )

    $"""{{ "schema": "toolup.perf-measurements/v1", "appDirectory": "output", "samples": [ {samples} ] }}"""

/// The shipped document's `client.notes`, read directly: the parser does
/// not model notes (they are for a reviewer), so the law that every
/// ceiling carries one is asserted on the JSON itself.
let private shippedClientNotes () =
    use document = Text.Json.JsonDocument.Parse(shippedBudgetJson ())

    match document.RootElement.TryGetProperty "client" with
    | true, client ->
        match client.TryGetProperty "notes" with
        | true, notes when notes.ValueKind = Text.Json.JsonValueKind.Object -> [
            for p in notes.EnumerateObject() do
                if p.Value.ValueKind = Text.Json.JsonValueKind.String then
                    yield p.Name, p.Value.GetString()
          ]
        | _ -> []
    | _ -> []

let private clientTests =
    testList "client block" [
        test "a budget with no client block is refused by the client parser, not read as asserting nothing" {
            let errors = clientBudgetErrors "no-client.json" (budgetJson defaultCeilings)
            Expect.isTrue (mentions "declares no 'client' block" errors) $"got: %A{errors}"
        }

        test "the server parser ignores the client block, so the server budget is unchanged by it" {
            let budget =
                budgetOrFail "both.json" (budgetJson (defaultCeilings + "," + clientBlock))

            Expect.equal
                (budget.Ceilings |> List.map fst)
                [ ColdStartMs; HotPathMs ]
                "the client block must not leak into the server block's ceilings"
        }

        test "a server metric placed in the client block is an unknown metric there" {
            let misplaced =
                """ "client": { "subject": "bench", "statistic": "min", "ceilings": { "coldStartMs": 3000 }, "minimumSamples": { "coldStartMs": 5 } } """

            let errors =
                clientBudgetErrors "misplaced.json" (budgetJson (defaultCeilings + "," + misplaced))

            Expect.isTrue (mentions "unknown metric 'coldStartMs'" errors) $"got: %A{errors}"
        }

        test "a client metric placed at the top level is an unknown metric there" {
            let errors =
                budgetErrors
                    "misplaced.json"
                    (budgetJson """ "ceilings": { "bootMs": 3000 }, "minimumSamples": { "bootMs": 5 } """)

            Expect.isTrue (mentions "unknown metric 'bootMs'" errors) $"got: %A{errors}"
        }

        test "a within-budget client run passes, and its headroom lines carry the metric's own unit" {
            let budget =
                clientBudgetOrFail "client.json" (budgetJson (defaultCeilings + "," + clientBlock))

            let run = runOrFail "client-run.json" (clientRun 1200.0 8.5 900.0)
            let findings = PerfBudgetGate.check budget run

            Expect.isEmpty (PerfBudgetGate.breaches findings) "every client measurement is within its ceiling"
            let text = PerfBudgetGate.report budget findings
            Expect.stringContains text "decodePerResponseUs 8.5 us / 100 us" "microsecond metrics render in us"
            Expect.stringContains text "bootMs 1200 ms / 3000 ms" "the boot metric renders in ms"
        }

        test "a regressed client run breaches, naming the value and the ceiling in the metric's unit" {
            let budget =
                clientBudgetOrFail "client.json" (budgetJson (defaultCeilings + "," + clientBlock))

            let run = runOrFail "client-run.json" (clientRun 1200.0 250.0 900.0)
            let findings = PerfBudgetGate.check budget run

            Expect.equal
                (PerfBudgetGate.breaches findings)
                [ CeilingBreached(ClientDecodePerResponseUs, 250.0, 100.0) ]
                "exactly the regressed metric breaches"

            Expect.stringContains
                (PerfFinding.render (CeilingBreached(ClientDecodePerResponseUs, 250.0, 100.0)))
                "was 250 us, budget allows at most 100 us"
                "the breach line names both numbers in microseconds"
        }

        test "perf-budgets.json carries a client block covering every client metric" {
            let budget = clientBudgetOrFail "shipped-budget.json" (shippedBudgetJson ())

            for metric in PerfMetric.client do
                Expect.isTrue
                    (budget.Ceilings |> List.exists (fun (m, _) -> m = metric))
                    $"the shipped client block must place a ceiling on '{PerfMetric.key metric}' — ClientBench measures it, and a measured number nothing budgets is discarded"
        }

        test "no shipped client ceiling is more than 20x its recorded baseline" {
            // The same anti-slack law the server block is held to, for the
            // same reason: generous is not unbounded.
            let budget = clientBudgetOrFail "shipped-budget.json" (shippedBudgetJson ())

            for metric, ceiling in budget.Ceilings do
                match budget.Baselines |> List.tryFind (fun (m, _) -> m = metric) with
                | None -> failtestf "'%s' has a client ceiling but no baseline" (PerfMetric.key metric)
                | Some(_, baseline) ->
                    Expect.isLessThanOrEqual
                        ceiling
                        (baseline * 20.0)
                        $"'client.{PerfMetric.key metric}' allows {ceiling} {PerfMetric.unit metric} against a {baseline} {PerfMetric.unit metric} baseline — re-measure and lower it, or justify the new baseline in the client block's notes."
        }

        test "every shipped client ceiling carries a note saying why it is where it is" {
            let budget = clientBudgetOrFail "shipped-budget.json" (shippedBudgetJson ())
            let notes = shippedClientNotes ()

            for metric, _ in budget.Ceilings do
                Expect.isTrue
                    (notes
                     |> List.exists (fun (name, text) ->
                         name = PerfMetric.key metric && not (String.IsNullOrWhiteSpace text)))
                    $"'client.{PerfMetric.key metric}' has no entry in client.notes — a ceiling nobody can explain is one nobody can safely raise"
        }

        // Phase 909 — the two bundle sizes. A size is not a clock: one
        // build of one tree against one lockfile gives one number, so the
        // wallclock metrics' order-of-magnitude slack would let a bundle
        // double unseen. Their headroom law is the tight one below.
        test "an over-budget bundle breaches in KiB, and a bundle the run did not measure is unmeasured, not a pass" {
            let bundleBlock =
                """ "client": { "subject": "bundles", "statistic": "min", "ceilings": { "minimalBundleKiB": 2800, "shellBundleKiB": 4000 }, "minimumSamples": { "minimalBundleKiB": 1, "shellBundleKiB": 1 }, "baselines": { "minimalBundleKiB": 2700, "shellBundleKiB": 3900 } } """

            let budget =
                clientBudgetOrFail "bundles.json" (budgetJson (defaultCeilings + "," + bundleBlock))

            // A synthetic over-budget build: the shell grew by a generated
            // module, and the minimal sample's bundle was never built.
            let run =
                runOrFail
                    "bundle-run.json"
                    """{ "schema": "toolup.perf-measurements/v1", "appDirectory": "output", "samples": [ { "metric": "shellBundleKiB", "statistic": "min", "value": 4120.5, "samples": 1, "observed": true, "evidence": "fixture" } ] }"""

            let findings = PerfBudgetGate.check budget run

            Expect.equal
                (PerfBudgetGate.breaches findings)
                [
                    MetricNotMeasured ClientMinimalBundleKiB
                    CeilingBreached(ClientShellBundleKiB, 4120.5, 4000.0)
                ]
                "the grown shell breaches and the unbuilt minimal bundle is refused"

            Expect.stringContains
                (PerfFinding.render (CeilingBreached(ClientShellBundleKiB, 4120.5, 4000.0)))
                "was 4120.5 KiB, budget allows at most 4000 KiB"
                "the breach line names both sizes in KiB"
        }

        test "no shipped bundle ceiling is more than 1.1x its recorded baseline" {
            let budget = clientBudgetOrFail "shipped-budget.json" (shippedBudgetJson ())

            for metric in [ ClientMinimalBundleKiB; ClientShellBundleKiB ] do
                let ceiling = budget.Ceilings |> List.tryFind (fun (m, _) -> m = metric)
                let baseline = budget.Baselines |> List.tryFind (fun (m, _) -> m = metric)

                match ceiling, baseline with
                | Some(_, c), Some(_, b) ->
                    Expect.isLessThanOrEqual
                        c
                        (b * 1.1)
                        $"'client.{PerfMetric.key metric}' allows {c} KiB against a {b} KiB baseline — a size has no noise to absorb, so slack beyond a tenth is growth nobody reviews. Re-measure, or justify the new baseline in the client block's notes."
                | _ ->
                    failtestf
                        "'%s' needs both a ceiling and a baseline in the shipped client block"
                        (PerfMetric.key metric)
        }
    ]

// ─── Phase 886 — the load block ────────────────────────────────────────
//
// The fact and retrieval paths under concurrent load, as the load harness
// in src/ToolUp.RAG.Benchmarks measures them. Same file, same shape, same
// laws — and the same disjointness: a load metric is unknown in the other
// two blocks and theirs are unknown here. The two COUNT metrics (blob
// reads) are held to the same laws as the clocks: a count ceiling too far
// above its baseline defends nothing either.

let private loadBudgetOrFail label json =
    match PerfBudgetGate.parseLoadBudget label json with
    | Ok b -> b
    | Error errors -> failtestf "expected the load block of '%s' to parse, got: %s" label (String.concat "; " errors)

let private loadBudgetErrors label json =
    match PerfBudgetGate.parseLoadBudget label json with
    | Ok _ -> failtestf "expected the load block of '%s' to be REFUSED, but it parsed" label
    | Error errors -> errors

let private loadBlock =
    """ "load": { "subject": "load harness", "statistic": "min", "ceilings": { "retrievalP95Ms": 100, "retrievalBlobReadsPerQuery": 20 }, "minimumSamples": { "retrievalP95Ms": 5, "retrievalBlobReadsPerQuery": 5 }, "baselines": { "retrievalP95Ms": 10, "retrievalBlobReadsPerQuery": 2 } } """

let private loadRun (p95: float) (reads: float) =
    let sample metric (value: float) =
        let v = value.ToString(Globalization.CultureInfo.InvariantCulture)

        $"""{{ "metric": "{metric}", "statistic": "min", "value": {v}, "samples": 5, "observed": true, "evidence": "fixture" }}"""

    let samples =
        String.Join(", ", [ sample "retrievalP95Ms" p95; sample "retrievalBlobReadsPerQuery" reads ])

    $"""{{ "schema": "toolup.perf-measurements/v1", "appDirectory": "bench", "samples": [ {samples} ] }}"""

let private shippedLoadNotes () =
    use document = Text.Json.JsonDocument.Parse(shippedBudgetJson ())

    match document.RootElement.TryGetProperty "load" with
    | true, load ->
        match load.TryGetProperty "notes" with
        | true, notes when notes.ValueKind = Text.Json.JsonValueKind.Object -> [
            for p in notes.EnumerateObject() do
                if p.Value.ValueKind = Text.Json.JsonValueKind.String then
                    yield p.Name, p.Value.GetString()
          ]
        | _ -> []
    | _ -> []

let private loadTests =
    testList "load block" [
        test "a budget with no load block is refused by the load parser, not read as asserting nothing" {
            let errors = loadBudgetErrors "no-load.json" (budgetJson defaultCeilings)
            Expect.isTrue (mentions "declares no 'load' block" errors) $"got: %A{errors}"
        }

        test "the server and client parsers ignore the load block" {
            let budget =
                budgetOrFail "all.json" (budgetJson (defaultCeilings + "," + clientBlock + "," + loadBlock))

            Expect.equal (budget.Ceilings |> List.map fst) [ ColdStartMs; HotPathMs ] "the server block is unchanged"

            let client =
                clientBudgetOrFail "all.json" (budgetJson (defaultCeilings + "," + clientBlock + "," + loadBlock))

            Expect.equal
                (client.Ceilings |> List.map fst)
                [ ClientBootMs; ClientDecodePerResponseUs; ClientViewPerDispatchUs ]
                "the client block is unchanged"
        }

        test
            "a client metric placed in the load block is an unknown metric there, and a load metric at the top level too" {
            let misplaced =
                """ "load": { "subject": "load harness", "statistic": "min", "ceilings": { "bootMs": 3000 }, "minimumSamples": { "bootMs": 5 } } """

            let errors =
                loadBudgetErrors "misplaced.json" (budgetJson (defaultCeilings + "," + misplaced))

            Expect.isTrue (mentions "unknown metric 'bootMs'" errors) $"got: %A{errors}"

            let topLevel =
                budgetErrors
                    "misplaced.json"
                    (budgetJson
                        """ "ceilings": { "retrievalP95Ms": 3000 }, "minimumSamples": { "retrievalP95Ms": 5 } """)

            Expect.isTrue (mentions "unknown metric 'retrievalP95Ms'" topLevel) $"got: %A{topLevel}"
        }

        test "a within-budget load run passes, and a count metric renders in reads" {
            let budget =
                loadBudgetOrFail "load.json" (budgetJson (defaultCeilings + "," + loadBlock))

            let findings =
                PerfBudgetGate.check budget (runOrFail "load-run.json" (loadRun 12.5 2.0))

            Expect.isEmpty (PerfBudgetGate.breaches findings) "every load measurement is within its ceiling"
            let text = PerfBudgetGate.report budget findings
            Expect.stringContains text "retrievalBlobReadsPerQuery 2 reads / 20 reads" "a count renders in reads"
            Expect.stringContains text "retrievalP95Ms 12.5 ms / 100 ms" "a clock renders in ms"
        }

        test "an order-of-magnitude regression on the retrieval path breaches, naming both numbers" {
            let budget =
                loadBudgetOrFail "load.json" (budgetJson (defaultCeilings + "," + loadBlock))

            let findings =
                PerfBudgetGate.check budget (runOrFail "load-run.json" (loadRun 125.0 2.0))

            Expect.equal
                (PerfBudgetGate.breaches findings)
                [ CeilingBreached(RetrievalP95Ms, 125.0, 100.0) ]
                "exactly the regressed metric breaches"
        }

        test "a read-count regression breaches even when the clock does not move" {
            let budget =
                loadBudgetOrFail "load.json" (budgetJson (defaultCeilings + "," + loadBlock))

            let findings =
                PerfBudgetGate.check budget (runOrFail "load-run.json" (loadRun 12.5 200.0))

            Expect.equal
                (PerfBudgetGate.breaches findings)
                [ CeilingBreached(RetrievalBlobReadsPerQuery, 200.0, 20.0) ]
                "the count is gated on its own"
        }

        test "perf-budgets.json carries a load block covering every load metric" {
            let budget = loadBudgetOrFail "shipped-budget.json" (shippedBudgetJson ())

            for metric in PerfMetric.load do
                Expect.isTrue
                    (budget.Ceilings |> List.exists (fun (m, _) -> m = metric))
                    $"the shipped load block must place a ceiling on '{PerfMetric.key metric}' — the harness measures it, and a measured number nothing budgets is discarded"
        }

        test "no shipped load ceiling is more than 20x its recorded baseline" {
            let budget = loadBudgetOrFail "shipped-budget.json" (shippedBudgetJson ())

            for metric, ceiling in budget.Ceilings do
                match budget.Baselines |> List.tryFind (fun (m, _) -> m = metric) with
                | None -> failtestf "'%s' has a load ceiling but no baseline" (PerfMetric.key metric)
                | Some(_, baseline) ->
                    Expect.isLessThanOrEqual
                        ceiling
                        (baseline * 20.0)
                        $"'load.{PerfMetric.key metric}' allows {ceiling} {PerfMetric.unit metric} against a {baseline} {PerfMetric.unit metric} baseline — re-measure and lower it, or justify the new baseline in the load block's notes."
        }

        test "every shipped load ceiling carries a note saying why it is where it is" {
            let budget = loadBudgetOrFail "shipped-budget.json" (shippedBudgetJson ())
            let notes = shippedLoadNotes ()

            for metric, _ in budget.Ceilings do
                Expect.isTrue
                    (notes
                     |> List.exists (fun (name, text) ->
                         name = PerfMetric.key metric && not (String.IsNullOrWhiteSpace text)))
                    $"'load.{PerfMetric.key metric}' has no entry in load.notes — a ceiling nobody can explain is one nobody can safely raise"
        }
    ]

// ─── Phase 929 — the load harness's two storage arms ───────────────────
//
// The same measurements as the `load` block, taken with the blob arm on the
// local Azure Blob emulator (`loadAzurite`) and with the fact store on a
// local PostgreSQL (`loadPostgres`: the pgvector store for retrieval, the
// database-backed fact store for facts). Each is its own block, so a CI job
// that cannot start either never reads a ceiling it cannot meet, and each
// is held to the `load` block's laws.

let private shippedArmNotes (property: string) =
    use document = Text.Json.JsonDocument.Parse(shippedBudgetJson ())

    match document.RootElement.TryGetProperty property with
    | true, block ->
        match block.TryGetProperty "notes" with
        | true, notes when notes.ValueKind = Text.Json.JsonValueKind.Object -> [
            for p in notes.EnumerateObject() do
                if p.Value.ValueKind = Text.Json.JsonValueKind.String then
                    yield p.Name, p.Value.GetString()
          ]
        | _ -> []
    | _ -> []

let private armLaws
    (property: string)
    (parse: string -> string -> Result<PerfBudget, string list>)
    (required: PerfMetric list)
    =
    let shipped () =
        match parse "shipped-budget.json" (shippedBudgetJson ()) with
        | Ok b -> b
        | Error errors ->
            failtestf
                "expected the '%s' block of perf-budgets.json to parse, got: %s"
                property
                (String.concat "; " errors)

    testList property [
        test "a budget with no such block is refused, not read as asserting nothing" {
            match parse "no-arm.json" (budgetJson (defaultCeilings + "," + loadBlock)) with
            | Ok _ -> failtestf "a document with no '%s' block parsed" property
            | Error errors -> Expect.isTrue (mentions $"declares no '{property}' block" errors) $"got: %A{errors}"
        }

        test "perf-budgets.json budgets every metric the arm measures" {
            let budget = shipped ()

            for metric in required do
                Expect.isTrue
                    (budget.Ceilings |> List.exists (fun (m, _) -> m = metric))
                    $"the shipped '{property}' block must place a ceiling on '{PerfMetric.key metric}' — the arm measures it, and a measured number nothing budgets is discarded"
        }

        test "no shipped ceiling is more than 20x its recorded baseline" {
            let budget = shipped ()

            for metric, ceiling in budget.Ceilings do
                match budget.Baselines |> List.tryFind (fun (m, _) -> m = metric) with
                | None -> failtestf "'%s.%s' has a ceiling but no baseline" property (PerfMetric.key metric)
                | Some(_, baseline) ->
                    Expect.isLessThanOrEqual
                        ceiling
                        (baseline * 20.0)
                        $"'{property}.{PerfMetric.key metric}' allows {ceiling} {PerfMetric.unit metric} against a {baseline} {PerfMetric.unit metric} baseline — re-measure and lower it, or justify the new baseline in the block's notes."
        }

        test "every shipped ceiling carries a note saying why it is where it is" {
            let budget = shipped ()
            let notes = shippedArmNotes property

            for metric, _ in budget.Ceilings do
                Expect.isTrue
                    (notes
                     |> List.exists (fun (name, text) ->
                         name = PerfMetric.key metric && not (String.IsNullOrWhiteSpace text)))
                    $"'{property}.{PerfMetric.key metric}' has no entry in its notes — a ceiling nobody can explain is one nobody can safely raise"
        }
    ]

let private armTests =
    testList "load arms" [
        armLaws PerfBudgetGate.LoadAzuriteBlockProperty PerfBudgetGate.parseLoadAzuriteBudget PerfMetric.load
        armLaws PerfBudgetGate.LoadPostgresBlockProperty PerfBudgetGate.parseLoadPostgresBudget PerfMetric.load

        test "the load parser and the arm parsers each read only their own block" {
            let azurite = loadBlock.Replace("\"load\"", "\"loadAzurite\"").Replace("100", "900")

            let json = budgetJson (defaultCeilings + "," + loadBlock + "," + azurite)

            match
                PerfBudgetGate.parseLoadBudget "both.json" json, PerfBudgetGate.parseLoadAzuriteBudget "both.json" json
            with
            | Ok load, Ok arm ->
                Expect.equal (load.Ceilings |> List.find (fun (m, _) -> m = RetrievalP95Ms) |> snd) 100.0 "load"
                Expect.equal (arm.Ceilings |> List.find (fun (m, _) -> m = RetrievalP95Ms) |> snd) 900.0 "arm"
            | a, b -> failtestf "expected both blocks to parse, got %A / %A" a b
        }

        // The Phase 909 bundle ceilings sit 1.025x above their baselines;
        // at one decimal the headroom printed "1.0x", indistinguishable from
        // a ceiling already reached.
        test "headroom prints enough precision to tell 1.025x from 1.0x" {
            let line =
                PerfFinding.render (WithinCeiling(ClientMinimalBundleKiB, 2663.43, 2730.0, None))

            Expect.stringContains line "(1.025x headroom" $"got: {line}"

            let reached =
                PerfFinding.render (WithinCeiling(ClientMinimalBundleKiB, 2730.0, 2730.0, None))

            Expect.stringContains reached "(1.0x headroom" $"got: {reached}"

            let clock = PerfFinding.render (WithinCeiling(HotPathMs, 0.5, 7.0, None))
            Expect.stringContains clock "(14.0x headroom" $"a clock's ratio reads as it did: {clock}"
        }
    ]

let tests =
    testList "PerfBudget" [
        parserTests
        checkTests
        shippedBudgetTests
        clientTests
        loadTests
        armTests
    ]