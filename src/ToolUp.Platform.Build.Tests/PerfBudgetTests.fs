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
        test "perf-budgets.json parses, and covers both metrics the gate knows" {
            let budget = budgetOrFail "shipped-budget.json" (shippedBudgetJson ())

            for metric in PerfMetric.all do
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

let tests = testList "PerfBudget" [ parserTests; checkTests; shippedBudgetTests ]