// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.BudgetSeamTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 689 — the platform budget seam ────────────────────────────────
//
// Phase 451's pack pins the compute path and is deliberately untouched by
// this phase: the extraction is a behavioural no-op, so *its* green run is
// the evidence for that half and any edit to it would have destroyed the
// evidence. This pack asserts the three things that pack cannot see.
//
//   1. **The unification is real, not a rename.** One predicate —
//      `Spent + Requested > Ceiling` — decides a concurrency cap, a
//      per-request duration cap and an accumulating allowance, and the
//      claims Phase 451 projects onto it reproduce the exact denial fields
//      that phase reported before the seam existed. Asserted by
//      constructing the claims through the SHIPPED projection
//      (`ComputeBudgetPolicy.claims`) and reading the numbers back off the
//      denial.
//
//   2. **The adoption is not a data migration.** The shared ledger reads
//      and writes the byte-identical blob, at the byte-identical path,
//      that Phase 451's store has been writing since it shipped —
//      asserted in both directions, because a codec that round-trips with
//      itself proves nothing about the rows already on disk.
//
//   3. **The seam serves a domain Phase 451 is not.** A budget with an
//      hourly window, no concurrency dimension and its own units decides,
//      warns and accounts through the same four parts — which is the claim
//      the phase actually makes, and one no compute test can make for it.
//
// The ledger contract is asserted against BOTH shipped implementations
// over one shared body, for the reason every contract pack in this repo
// exists: an invariant asserted against one implementation is a property
// of that implementation.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// A budget domain that is not compute: hourly window, one accumulating
/// dimension in its own units, no concurrency ceiling.
let private tokens = "ai-tokens"
let private scope = "team-42"

// ── the check ────────────────────────────────────────────────────────────

let private claimTests =
    testList "Phase 689 — one predicate for every ceiling" [

        test "the three compute dimensions and a token cap are the same predicate" {
            // The unification the seam rests on. Each of these is written
            // in its own vocabulary in its own phase, and all four are
            // `Spent + Requested > Ceiling`.
            let concurrency = BudgetClaim.create "concurrency" 3M 3M 1M
            let duration = BudgetClaim.perRequest "run-duration" 600M 660M
            let allowance = BudgetClaim.create "period-allowance" 250M 249M 5M
            let perHour = BudgetClaim.create "tokens-per-hour" 100_000M 99_000M 2_000M

            for claim in [ concurrency; duration; allowance; perHour ] do
                Expect.isTrue (BudgetClaim.wouldExceed claim) $"{claim.Dimension} is over its ceiling"
        }

        test "a request landing exactly ON the ceiling is admitted; the next is not" {
            // What "an allowance of 100" means: the run that takes you to
            // 100 is the last one that runs.
            let landing = BudgetClaim.create "period-allowance" 100M 95M 5M
            let over = BudgetClaim.create "period-allowance" 100M 100M 1M

            Expect.isFalse (BudgetClaim.wouldExceed landing) "reaching the ceiling exactly is admitted"
            Expect.isTrue (BudgetClaim.wouldExceed over) "the next request is refused"
        }

        test "a ceiling of zero or below constrains nothing, at any consumption" {
            for ceiling in [ 0M; -1M ] do
                let claim = BudgetClaim.create "spend" ceiling 10_000M 10_000M

                Expect.isTrue (BudgetClaim.isUnrestricted claim) "a non-positive ceiling is unrestricted"
                Expect.isFalse (BudgetClaim.wouldExceed claim) "…so nothing can breach it"
                Expect.equal (BudgetClaim.remaining claim) 0M "…and it has no meaningful remainder"
        }

        test "the FIRST breached ceiling is reported, in the caller's order" {
            // A refusal naming three problems invites fixing the wrong
            // one. The control is the same list reversed: the answer must
            // change, or the ordering is not doing anything.
            let concurrency = BudgetClaim.create "concurrency" 1M 5M 1M
            let allowance = BudgetClaim.create "period-allowance" 10M 20M 1M
            let subject = BudgetSubject.create tokens scope "agent"

            match BudgetPolicy.check subject "2026-08" [ concurrency; allowance ] with
            | Error denial -> Expect.equal denial.Dimension "concurrency" "the first listed breach is reported"
            | Ok() -> failtest "expected a refusal"

            match BudgetPolicy.check subject "2026-08" [ allowance; concurrency ] with
            | Error denial -> Expect.equal denial.Dimension "period-allowance" "order decides which"
            | Ok() -> failtest "expected a refusal"
        }

        test "a refusal names the scope, the class, the period and both halves of the number" {
            let subject = BudgetSubject.create tokens scope "agent"
            let claim = BudgetClaim.create "tokens-per-hour" 100M 90M 20M

            match BudgetPolicy.check subject "2026-08-05T14" [ claim ] with
            | Error denial ->
                Expect.equal denial.Domain tokens "the domain says WHICH budget refused"
                Expect.equal denial.ScopeId scope "the scope"
                Expect.equal denial.ClassLabel "agent" "the class policy discriminated on"
                Expect.equal denial.Quota 100M "the configured ceiling"
                Expect.equal denial.Spent 90M "what was already consumed"
                Expect.equal denial.Requested 20M "…kept SEPARATE from what this request needs"
                Expect.equal denial.PeriodKey "2026-08-05T14" "the accounting period"
                Expect.stringContains (BudgetDenial.describe denial) scope "the description names the scope"
            | Ok() -> failtest "expected a refusal"
        }
    ]

let private verdictTests =
    testList "Phase 689 — allowed / near-limit / refused" [

        test "an unthreatened budget allows, and records nothing" {
            let subject = BudgetSubject.ofScope tokens scope
            let claim = BudgetClaim.create "tokens-per-day" 1000M 10M 10M

            let verdict =
                BudgetPolicy.verdict subject "2026-08-05" BudgetPolicy.defaultWarnThreshold [ claim ]

            Expect.equal verdict BudgetVerdict.Allowed "well under the ceiling"

            // GP 13: observing an allowed request on an unthreatened
            // budget must cost nothing. `record` is what makes that true
            // at the call site rather than by each caller remembering.
            let recorded = ref 0

            let account = {
                BudgetAccount.silent with
                    OnNearLimit = fun _ -> async { recorded.Value <- recorded.Value + 1 }
                    OnRefused = fun _ -> async { recorded.Value <- recorded.Value + 1 }
            }

            BudgetAccount.record account verdict |> Async.RunSynchronously
            Expect.equal recorded.Value 0 "an allowed verdict records nothing"
        }

        test "crossing the threshold warns on the POST-request consumption" {
            // The leading indicator: the request is admitted, and the
            // warning reports where it left the budget. 79 + 2 = 81 of
            // 100 crosses 0.8; the control 78 + 1 = 79 does not, which is
            // what makes the assertion mean something.
            let subject = BudgetSubject.ofScope tokens scope

            let crossing =
                BudgetPolicy.verdict subject "2026-08-05" 0.8M [ BudgetClaim.create "spend" 100M 79M 2M ]

            let below =
                BudgetPolicy.verdict subject "2026-08-05" 0.8M [ BudgetClaim.create "spend" 100M 78M 1M ]

            match crossing with
            | BudgetVerdict.NearLimit warning ->
                Expect.equal warning.Spent 81M "the warning reports consumption AFTER the admitted request"
                Expect.equal warning.Quota 100M "…against the ceiling"
                Expect.equal warning.Threshold 0.8M "…and the threshold that fired"
            | other -> failtestf "expected a near-limit verdict, got %A" other

            Expect.equal below BudgetVerdict.Allowed "one unit short of the threshold is silent"
        }

        test "a breach refuses rather than warns, however close to the ceiling it is" {
            let subject = BudgetSubject.ofScope tokens scope

            match BudgetPolicy.verdict subject "2026-08-05" 0.8M [ BudgetClaim.create "spend" 100M 99M 2M ] with
            | BudgetVerdict.Refused denial -> Expect.equal denial.Dimension "spend" "refusal wins over warning"
            | other -> failtestf "expected a refusal, got %A" other
        }

        test "an account combines in order, and `silent` is its identity" {
            let order = ResizeArray<string>()

            let named name =
                BudgetAccount.onRefused (fun _ -> async { order.Add name })

            let combined =
                BudgetAccount.combine (BudgetAccount.combine (named "first") BudgetAccount.silent) (named "second")

            let denial =
                BudgetPolicy.deny (BudgetSubject.ofScope tokens scope) "p" (BudgetClaim.create "d" 1M 1M 1M)

            combined.OnRefused denial |> Async.RunSynchronously
            Expect.sequenceEqual order [ "first"; "second" ] "both accounts ran, in order, and silent added nothing"
        }
    ]

// ── the period ───────────────────────────────────────────────────────────

let private periodTests =
    testList "Phase 689 — the period is a storage key" [

        test "keys are UTC and roll at the documented boundary" {
            let lateUtc = DateTime(2026, 8, 5, 23, 30, 0, DateTimeKind.Utc)
            let nextUtc = DateTime(2026, 8, 6, 0, 30, 0, DateTimeKind.Utc)

            Expect.equal (BudgetPeriod.key BudgetPeriod.Hourly lateUtc) "2026-08-05T23" "hourly key"
            Expect.equal (BudgetPeriod.key BudgetPeriod.Hourly nextUtc) "2026-08-06T00" "hourly key rolls"
            Expect.equal (BudgetPeriod.key BudgetPeriod.Daily lateUtc) "2026-08-05" "daily key"
            Expect.equal (BudgetPeriod.key BudgetPeriod.Daily nextUtc) "2026-08-06" "daily key rolls"
            Expect.equal (BudgetPeriod.key BudgetPeriod.Monthly lateUtc) "2026-08" "monthly key"
            Expect.equal (BudgetPeriod.key BudgetPeriod.Perpetual nextUtc) "perpetual" "perpetual never rolls"
        }

        test "a local-time input is normalised to UTC before the key is taken" {
            // Two replicas of one deployment in different periods for an
            // hour twice a year is the kind of thing nobody notices until
            // the bill.
            let utc = DateTime(2026, 8, 5, 23, 30, 0, DateTimeKind.Utc)
            let asLocal = utc.ToLocalTime()

            Expect.equal
                (BudgetPeriod.key BudgetPeriod.Daily asLocal)
                (BudgetPeriod.key BudgetPeriod.Daily utc)
                "the same instant is the same period, whatever kind it carries"
        }

        test "the compute periods project onto the seam's, key for key" {
            // The no-op pin for the delegation: `ComputeBudgetPeriod.key`
            // is now `BudgetPeriod.key` under a projection, so the two
            // must agree on every case and every boundary.
            let instants = [
                DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                DateTime(2026, 8, 5, 23, 59, 59, DateTimeKind.Utc)
                DateTime(2026, 12, 31, 23, 0, 0, DateTimeKind.Utc)
            ]

            for period in
                [
                    ComputeBudgetPeriod.Perpetual
                    ComputeBudgetPeriod.Daily
                    ComputeBudgetPeriod.Monthly
                ] do
                for instant in instants do
                    Expect.equal
                        (ComputeBudgetPeriod.key period instant)
                        (BudgetPeriod.key (ComputeBudgetPeriod.toBudgetPeriod period) instant)
                        $"{ComputeBudgetPeriod.label period} agrees with its seam projection"
        }

        test "every period label parses back to itself" {
            for period in BudgetPeriod.all do
                Expect.equal (BudgetPeriod.parse (BudgetPeriod.label period)) (Some period) "label round-trips"

            Expect.equal (BudgetPeriod.parse "weekly") None "an unknown window is not guessed at"
            Expect.equal (BudgetPeriod.parse null) None "…nor is an absent one"
        }
    ]

// ── the compute re-expression ────────────────────────────────────────────

let private reExpressionTests =
    testList "Phase 689 — Phase 451 re-expressed, field for field" [

        test "the shipped claims carry the exact numbers the shipped denial reports" {
            // The projection is what makes the extraction a no-op, so it
            // is asserted through the SHIPPED function rather than a
            // re-derivation of it: `claims` builds them, `admit` decides
            // on them, and the denial numbers are read back.
            let limits = {
                MaxConcurrent = 3
                MaxRunDuration = Some(TimeSpan.FromMinutes 10.0)
                PeriodAllowance = 250M
            }

            let usage = {
                ComputeBudgetUsage.empty scope "2026-08" with
                    InFlight = 3
                    Spent = 40M
            }

            let built =
                ComputeBudgetPolicy.claims limits usage (Some(TimeSpan.FromMinutes 5.0)) 2M

            Expect.equal
                (built |> List.map _.Dimension)
                [ "concurrency"; "run-duration"; "period-allowance" ]
                "concurrency → duration → allowance, cheapest and most-immediate first"

            match ComputeBudgetPolicy.admit scope SubmitterClass.AgentInitiated limits usage None 2M with
            | Error denial ->
                Expect.equal denial.Dimension "concurrency" "the burst control is hit first"
                Expect.equal denial.Quota 3M "the quota is the configured cap"
                Expect.equal denial.Spent 3M "…the runs already in flight"
                Expect.equal denial.Requested 1M "…and this one submission"
                Expect.equal denial.SubmitterClass "agent" "the declared class rides the refusal"
                Expect.equal denial.PeriodKey "2026-08" "in the row's own period"
            | Ok() -> failtest "expected a concurrency refusal"
        }

        test "an unrestricted dimension contributes no claim at all" {
            // Equivalent to a zero ceiling for the check, and a shorter
            // list for a caller rendering "3 of 10 runs".
            let built =
                ComputeBudgetPolicy.claims
                    ComputeBudgetLimits.unrestricted
                    (ComputeBudgetUsage.empty scope "perpetual")
                    (Some(TimeSpan.FromMinutes 5.0))
                    1M

            Expect.isEmpty built "nothing configured, nothing to check"

            let concurrencyOnly =
                ComputeBudgetPolicy.claims
                    (ComputeBudgetLimits.concurrency 4)
                    (ComputeBudgetUsage.empty scope "perpetual")
                    (Some(TimeSpan.FromMinutes 5.0))
                    1M

            Expect.equal (concurrencyOnly |> List.map _.Dimension) [ "concurrency" ] "only the configured ceiling"
        }

        test "a compute denial round-trips through the seam's shape" {
            let denial: ComputeBudgetDenial = {
                ScopeId = scope
                SubmitterClass = "agent"
                Dimension = "period-allowance"
                Quota = 250M
                Spent = 249M
                Requested = 5M
                PeriodKey = "2026-08"
            }

            let projected = ComputeBudgetDenial.toBudgetDenial denial

            Expect.equal projected.Domain ComputeBudget.Domain "the domain is compute's own label"

            Expect.equal
                (ComputeBudgetDenial.ofBudgetDenial projected)
                denial
                "and the projection loses nothing on the way back"
        }

        test "the usage row round-trips through the seam's shape" {
            let usage = {
                ComputeBudgetUsage.empty scope "2026-08" with
                    InFlight = 2
                    Spent = 7.5M
                    UpdatedAt = DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc)
            }

            Expect.equal
                (ComputeBudgetUsage.ofBudgetUsage (ComputeBudgetUsage.toBudgetUsage usage))
                usage
                "the row survives the projection"
        }

        test "the ledger writes the blob Phase 451 has always written" {
            // The claim that makes this an adoption rather than a
            // migration, asserted at the path AND at the bytes.
            let key = BudgetLedgerKey.create ComputeBudget.Domain scope "2026-08"

            Expect.equal
                (BudgetLedgerLayout.usageBlob key)
                (ComputeBudgetLayout.usageBlob scope "2026-08")
                "the same path"

            Expect.stringStarts
                (BudgetLedgerLayout.usageBlob key)
                ComputeBudgetLayout.BlobPrefix
                "…under the prefix an operator already enumerates"

            let row = {
                BudgetUsage.empty ComputeBudget.Domain scope "2026-08" with
                    InFlight = 1
                    Spent = 12M
            }

            // Both directions. A codec that round-trips with itself proves
            // nothing about the rows already on disk, so the seam's bytes
            // are read by the compute codec and vice versa.
            match ComputeBudgetJson.deserialiseUsage scope "2026-08" (BudgetUsageJson.serialise row) with
            | Some read -> Expect.equal read.Spent 12M "the compute codec reads a ledger row"
            | None -> failtest "the compute codec could not read a row the shared ledger wrote"

            let computeBytes =
                ComputeBudgetJson.serialiseUsage (ComputeBudgetUsage.ofBudgetUsage row)

            match BudgetUsageJson.deserialise key computeBytes with
            | Some read -> Expect.equal read.Spent 12M "…and the ledger reads a compute row"
            | None -> failtest "the shared ledger could not read a row the compute store wrote"
        }

        test "a row for another scope or period is refused, not adopted" {
            // The second half of the GP 4 guarantee: the path partitions,
            // and this makes a mis-derived path degrade to "no consumption
            // recorded" rather than to one tenant spending another's.
            let row = BudgetUsage.empty ComputeBudget.Domain "team-a" "2026-08"
            let bytes = BudgetUsageJson.serialise row

            Expect.isNone
                (BudgetUsageJson.deserialise (BudgetLedgerKey.create ComputeBudget.Domain "team-b" "2026-08") bytes)
                "a row from another scope is not read as this scope's"

            Expect.isNone
                (BudgetUsageJson.deserialise (BudgetLedgerKey.create ComputeBudget.Domain "team-a" "2026-09") bytes)
                "…nor another period's as this period's"

            Expect.isNone
                (BudgetUsageJson.deserialise
                    (BudgetLedgerKey.create ComputeBudget.Domain "team-a" "2026-08")
                    (Text.Encoding.UTF8.GetBytes "{ not json at all"))
                "…and a corrupt blob reads as no consumption rather than throwing"
        }
    ]

// ── the ledger, against both implementations ─────────────────────────────

let private ledgerContract (name: string) (build: (unit -> DateTime) -> IBudgetLedger) =
    let admitAny = fun (_: BudgetUsage) -> Ok()

    let clockNow = DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc)
    let fixedClock = fun () -> clockNow

    testList $"Phase 689 — IBudgetLedger contract ({name})" [

        testCaseAsync
            "an unwritten key reads as zero consumption, never as an error"
            (async {
                let ledger = build fixedClock
                let! row = ledger.ReadUsage(BudgetLedgerKey.create tokens scope "2026-08-05T14")

                Expect.equal row.InFlight 0 "nothing in flight"
                Expect.equal row.Spent 0M "nothing spent"
                Expect.equal row.ScopeId scope "…and the row knows whose it is"
            })

        testCaseAsync
            "a reservation is applied inside the decision, and the caller gets the row back"
            (async {
                let ledger = build fixedClock
                let key = BudgetLedgerKey.create tokens scope "2026-08-05T14"

                match! ledger.Reserve(key, 5M, admitAny) with
                | Ok row ->
                    Expect.equal row.InFlight 1 "one request in flight"
                    Expect.equal row.Spent 5M "…holding its reservation"
                    Expect.equal row.UpdatedAt clockNow "…stamped by the injected clock"
                | Error denial -> failtestf "expected an admission, got %A" denial
            })

        testCaseAsync
            "a refused decision writes NOTHING — a denial costs a read, never a write"
            (async {
                let ledger = build fixedClock
                let key = BudgetLedgerKey.create tokens scope "2026-08-05T14"
                let subject = BudgetSubject.ofScope tokens scope

                do! ledger.Reserve(key, 5M, admitAny) |> Async.Ignore

                let refuse (row: BudgetUsage) =
                    BudgetPolicy.check subject key.PeriodKey [ BudgetClaim.create "tokens" 6M row.Spent 5M ]

                match! ledger.Reserve(key, 5M, refuse) with
                | Error denial -> Expect.equal denial.Dimension "tokens" "the policy's refusal, not the ledger's"
                | Ok row -> failtestf "expected a refusal, got %A" row

                let! after = ledger.ReadUsage key
                Expect.equal after.Spent 5M "the refused request left the row untouched"
                Expect.equal after.InFlight 1 "…and reserved no slot"
            })

        testCaseAsync
            "a burst of concurrent reservations admits exactly the ceiling, never more"
            (async {
                // The race the seam exists to bound: read-then-decide-then-
                // write would admit all twelve, because every one of them
                // reads the same pre-burst row.
                let ledger = build fixedClock
                let key = BudgetLedgerKey.create tokens scope "2026-08-05T14"
                let subject = BudgetSubject.ofScope tokens scope

                let decide (row: BudgetUsage) =
                    BudgetPolicy.check subject key.PeriodKey [
                        BudgetClaim.create "concurrency" 4M (decimal row.InFlight) 1M
                    ]

                let! results = Seq.init 12 (fun _ -> ledger.Reserve(key, 1M, decide)) |> Async.Parallel

                let admitted = results |> Array.filter Result.isOk |> Array.length

                Expect.equal admitted 4 "exactly the ceiling was admitted"

                let! after = ledger.ReadUsage key
                Expect.equal after.InFlight 4 "…and the row agrees"
            })

        testCaseAsync
            "releasing frees the slot and folds the adjustment in; both figures are floored"
            (async {
                let ledger = build fixedClock
                let key = BudgetLedgerKey.create tokens scope "2026-08-05T14"

                do! ledger.Reserve(key, 10M, admitAny) |> Async.Ignore
                do! ledger.Release(key, -4M)

                let! after = ledger.ReadUsage key
                Expect.equal after.InFlight 0 "the slot is free"
                Expect.equal after.Spent 6M "a run that cost less than it reserved is credited the difference"

                // Over-releasing must clamp rather than wrap: a negative
                // in-flight count would silently grant extra concurrency,
                // and crediting a period for more than it recorded would
                // manufacture allowance out of a clock boundary.
                do! ledger.Release(key, -999M)
                let! floored = ledger.ReadUsage key
                Expect.equal floored.InFlight 0 "in-flight never goes negative"
                Expect.equal floored.Spent 0M "spend never goes negative"
            })

        testCaseAsync
            "two periods, two scopes and two DOMAINS are independent rows"
            (async {
                // The period isolation is what makes a reset free — there
                // is nothing to reset, because the next period is a key
                // that does not exist yet. The domain isolation is what
                // lets one ledger serve every budget a deployment runs.
                let ledger = build fixedClock

                let thisHour = BudgetLedgerKey.create tokens scope "2026-08-05T14"
                let nextHour = BudgetLedgerKey.create tokens scope "2026-08-05T15"
                let otherScope = BudgetLedgerKey.create tokens "team-99" "2026-08-05T14"

                let otherDomain = BudgetLedgerKey.create ComputeBudget.Domain scope "2026-08-05T14"

                do! ledger.Reserve(thisHour, 7M, admitAny) |> Async.Ignore

                for key in [ nextHour; otherScope; otherDomain ] do
                    let! row = ledger.ReadUsage key
                    Expect.equal row.Spent 0M $"{key.Domain}/{key.ScopeId}/{key.PeriodKey} is its own row"

                let! original = ledger.ReadUsage thisHour
                Expect.equal original.Spent 7M "…and the written row is undisturbed"
            })
    ]

// ── the latch ────────────────────────────────────────────────────────────

let private latchTests =
    testList "Phase 689 — the warning latch" [

        test "a crossing is reported once per key, and the next period starts fresh" {
            // A warning emitted on every admission after the crossing is a
            // log line an operator filters out, which is the indicator not
            // working. The next-period arm is the other half: the latch
            // self-expires because the new period is a key nobody latched.
            let latch = BudgetWarningLatch()
            let thisHour = BudgetLedgerKey.create tokens scope "2026-08-05T14"
            let nextHour = BudgetLedgerKey.create tokens scope "2026-08-05T15"

            Expect.isTrue (latch.ShouldReport thisHour) "the crossing is reported"
            Expect.isFalse (latch.ShouldReport thisHour) "…and not again in the same period"
            Expect.isTrue (latch.ShouldReport nextHour) "the next period reports again"

            Expect.isTrue
                (latch.ShouldReport(BudgetLedgerKey.create ComputeBudget.Domain scope "2026-08-05T14"))
                "another domain's budget latches separately"
        }
    ]

// ── a domain that is not compute ─────────────────────────────────────────

let private secondDomainTests =
    testList "Phase 689 — the seam serves a budget compute is not" [

        testCaseAsync
            "an hourly token budget declares, checks, accounts and stores through the same four parts"
            (async {
                // The claim the phase actually makes. Nothing here is
                // compute-shaped: an hourly window, one accumulating
                // dimension in token units, per-user class discrimination,
                // and no concurrency ceiling at all.
                let clock = DateTime(2026, 8, 5, 14, 30, 0, DateTimeKind.Utc)
                let ledger = InMemoryBudgetLedger(fun () -> clock) :> IBudgetLedger

                let subject = BudgetSubject.create tokens scope "user:alice"
                let periodKey = BudgetPeriod.key BudgetPeriod.Hourly clock
                let key = BudgetLedgerKey.ofSubject subject periodKey

                let refusals = ResizeArray<BudgetDenial>()
                let warnings = ResizeArray<BudgetWarning>()

                let account = {
                    OnRefused = fun d -> async { refusals.Add d }
                    OnNearLimit = fun w -> async { warnings.Add w }
                }

                let submit (estimate: decimal) = async {
                    let decide (row: BudgetUsage) =
                        BudgetPolicy.check subject periodKey [
                            BudgetClaim.create "tokens-per-hour" 100M row.Spent estimate
                        ]

                    match! ledger.Reserve(key, estimate, decide) with
                    | Error denial ->
                        do! account.OnRefused denial
                        return false
                    | Ok row ->
                        let settled = BudgetClaim.create "tokens-per-hour" 100M row.Spent 0M

                        if BudgetPolicy.crossedThreshold BudgetPolicy.defaultWarnThreshold settled then
                            do! account.OnNearLimit(BudgetPolicy.warn subject periodKey 0.8M settled)

                        return true
                }

                let! first = submit 50M
                let! second = submit 35M
                let! third = submit 30M

                Expect.isTrue first "the first call is well under the cap"
                Expect.isTrue second "the second crosses the warning threshold and is still admitted"
                Expect.isFalse third "the third would cross the cap and is refused"

                Expect.equal warnings.Count 1 "one warning, on the admitted call that crossed"
                Expect.equal warnings[0].Spent 85M "…reporting where it left the budget"
                Expect.equal warnings[0].ClassLabel "user:alice" "…for the class that spent it"

                Expect.equal refusals.Count 1 "one refusal"
                Expect.equal refusals[0].Domain tokens "…named as this domain's, not compute's"
                Expect.equal refusals[0].Requested 30M "…separating what was asked from what was spent"

                let! row = ledger.ReadUsage key
                Expect.equal row.Spent 85M "the refused request consumed nothing"
                Expect.equal row.PeriodKey "2026-08-05T14" "…and the hour is the accounting period"
            })
    ]

[<Tests>]
let tests =
    testList "Phase 689 — platform budget seam" [
        claimTests
        verdictTests
        periodTests
        reExpressionTests
        ledgerContract "in-memory" (fun clock -> InMemoryBudgetLedger(clock) :> IBudgetLedger)
        ledgerContract "blob-backed" (fun clock ->
            BlobBudgetLedger(InMemoryBlobStorage() :> IBlobStorage, silentLogger, clock = clock) :> IBudgetLedger)
        latchTests
        secondDomainTests
    ]