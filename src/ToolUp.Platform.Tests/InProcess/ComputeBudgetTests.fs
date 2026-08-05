// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ComputeBudgetTests

open System
open System.IO
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.Tracing
open ToolUp.Platform.Usage
open ToolUp.InterPlatform
open ToolUp.ModelProviders.Reference
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 451 — compute-budget governance ───────────────────────────────
//
// The claims, and the shape each is asserted in. Throughout, the assertion
// is on **what the inner substrate was handed** rather than on what the
// decorator returned: a returned refusal is equally consistent with a
// decorator that submitted anyway and then refused, and that is the
// failure being excluded.
//
//   1. **A concurrency cap refuses the (N+1)th in-flight run**, and the
//      backend never sees it. Paired with the control that makes the
//      assertion mean something — the same fixture with the cap raised by
//      one admits the same submission.
//
//   2. **Settling a terminal outcome frees the slot.** Otherwise claim 1
//      is indistinguishable from "the second submission always fails".
//
//   3. **An allowance is exhausted and then RESETS at the period
//      boundary**, on an injected clock rather than by waiting for
//      midnight. The reset is the interesting half: the period key is part
//      of the storage key, so this proves there is nothing to reset rather
//      than that a reset ran.
//
//   4. **Per-class differential policy** — the phase's headline: the
//      identical submission is refused as `AgentInitiated` and admitted as
//      `Human`, under one budget, in one fixture, with only the declared
//      class changed.
//
//   5. **Audit emission** — a denial records `ComputeBudgetDenied` with
//      the typed denial verbatim; an admitted submission that crosses the
//      warning threshold records `ComputeBudgetWarning` exactly ONCE
//      across repeated crossings.
//
//   6. **Transparency under budget** — an under-budget deployment gets the
//      inner dispatcher's behaviour unchanged, including the handle it
//      minted and a passthrough `Cancel`.
//
//   7. **The federated-peer path is budgeted.** The one that motivated the
//      second enforcement point: a peer fit submission never touches
//      `IExternalComputeDispatcher.Submit`, so a test that only exercised
//      the decorator would report full coverage of a control an agent
//      arriving over the federation seam walks straight past. Asserted at
//      the real join — `ModelExecutionPeerContract.ofWireSubmission` into
//      the handler-backed `ModelExecutionApi`, which is exactly what the
//      peer contract's `runOperation` calls.
//
//   8. **Metering** — a settled run emits one `compute.units` usage record
//      (the Phase 9d integration), and the quantity is what the run
//      actually cost rather than what admission reserved.

// ── doubles ──────────────────────────────────────────────────────────────

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private silentChannel =
    { new INotificationChannel with
        member _.Publish(_, _) = async { return () }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_) = async { return () }
    }

type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded
    member this.Kinds = this.Events |> List.map (snd >> AuditEvent.eventTypeName)

    member this.CountOf(name: string) =
        this.Kinds |> List.filter ((=) name) |> List.length

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add((scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

/// A dispatcher that records every spec it was handed and mints a handle.
/// The submission COUNT is the load-bearing observation in most of these
/// tests — a refusal that still reached the backend is the bug.
type private RecordingDispatcher() =
    let submitted = ResizeArray<string * ExternalWorkSpec>()

    member _.Submitted = List.ofSeq submitted
    member _.SubmitCount = submitted.Count

    interface IExternalComputeDispatcher with
        member _.Backend = "recording"

        member _.Submit(scopeId, spec) = async {
            submitted.Add((scopeId, spec))

            return
                Ok {
                    HandleId = Guid.NewGuid()
                    Backend = "recording"
                    ScopeId = scopeId
                    NativeRef = $"native-{submitted.Count}"
                    SubmittedAt = DateTime.UtcNow
                }
        }

        member _.Poll _ = async { return ExternalOutcome.Succeeded "result-ref" }
        member _.Cancel _ = async { return () }

/// A dispatcher whose `Poll` outcome the test controls, so a terminal
/// outcome can be produced on demand (claim 2).
type private ControllablePoll(outcome: unit -> ExternalOutcome) =
    let submitted = ResizeArray<ExternalWorkSpec>()
    let cancelled = ResizeArray<Guid>()

    member _.SubmitCount = submitted.Count
    member _.Cancelled = List.ofSeq cancelled

    interface IExternalComputeDispatcher with
        member _.Backend = "controllable"

        member _.Submit(scopeId, spec) = async {
            submitted.Add spec

            return
                Ok {
                    HandleId = Guid.NewGuid()
                    Backend = "controllable"
                    ScopeId = scopeId
                    NativeRef = "native"
                    SubmittedAt = DateTime.UtcNow
                }
        }

        member _.Poll _ = async { return outcome () }
        member _.Cancel handle = async { cancelled.Add handle.HandleId }

/// A mutable clock, so period boundaries and elapsed wall-clock are
/// decided by the test rather than by how long it took to run.
type private TestClock(start: DateTime) =
    let mutable current = start
    member _.Now = current
    member _.Advance(span: TimeSpan) = current <- current + span
    member _.Set(value: DateTime) = current <- value
    member this.Fn: unit -> DateTime = fun () -> this.Now

// ── fixtures ─────────────────────────────────────────────────────────────

let private scope = "team-alpha"

let private spec (kind: string) (submitter: SubmitterClass) =
    ExternalWorkSpec.create kind """{"opaque":"payload"}"""
    |> ExternalWorkSpec.withSubmitterClass submitter

/// A budget store pre-loaded with `budget` for `scope`.
let private storeWith (budget: ComputeBudget) (clock: TestClock) =
    let store = InMemoryComputeBudgetStore(clock = clock.Fn) :> IComputeBudgetStore
    store.SetBudget(scope, budget) |> Async.RunSynchronously |> ignore
    store

let private guardWith (store: IComputeBudgetStore) (audit: RecordingAuditLog) (clock: TestClock) =
    ComputeBudgetGuard(store, audit = (audit :> IAuditLog), clock = clock.Fn)

// ── the pure policy, exhausted without infrastructure ────────────────────

let private policyTests =
    testList "Phase 451 — the admission policy is a total function" [

        test "an unrestricted budget admits everything, whatever the class" {
            for submitter in SubmitterClass.all do
                let usage = ComputeBudgetUsage.empty scope "2026-08"

                let verdict =
                    ComputeBudgetPolicy.admit scope submitter ComputeBudgetLimits.unrestricted usage None 1_000_000M

                Expect.isOk verdict $"{SubmitterClass.label submitter} is unconstrained"
        }

        test "zero is unrestricted on every numeric dimension" {
            Expect.isTrue
                (ComputeBudgetLimits.isUnrestricted ComputeBudgetLimits.unrestricted)
                "the identity limits constrain nothing"

            Expect.isTrue
                (ComputeBudgetLimits.isUnrestricted {
                    MaxConcurrent = 0
                    MaxRunDuration = None
                    PeriodAllowance = 0M
                })
                "an explicitly-zeroed limits record is the same thing"
        }

        test "the first ceiling hit is the one reported, in check order" {
            // Both concurrency AND allowance are breached; concurrency is
            // checked first, so that is what the caller is told. Reporting
            // the other would send an operator to raise the wrong number.
            let limits = {
                MaxConcurrent = 1
                MaxRunDuration = None
                PeriodAllowance = 1M
            }

            let usage = {
                ComputeBudgetUsage.empty scope "2026-08" with
                    InFlight = 5
                    Spent = 99M
            }

            match ComputeBudgetPolicy.admit scope SubmitterClass.Human limits usage None 1M with
            | Error d ->
                Expect.equal
                    d.Dimension
                    (ComputeBudgetDimension.label ComputeBudgetDimension.Concurrency)
                    "concurrency is checked before allowance"

                Expect.equal d.Quota 1M "the quota is the concurrency ceiling"
                Expect.equal d.Spent 5M "spent is the live in-flight count"
            | Ok() -> failtest "expected a denial"
        }

        test "a declared duration over the cap is refused; one under it passes" {
            let limits = ComputeBudgetLimits.runDuration (TimeSpan.FromMinutes 10.0)
            let usage = ComputeBudgetUsage.empty scope "perpetual"

            let over =
                ComputeBudgetPolicy.admit scope SubmitterClass.Human limits usage (Some(TimeSpan.FromMinutes 11.0)) 1M

            let under =
                ComputeBudgetPolicy.admit scope SubmitterClass.Human limits usage (Some(TimeSpan.FromMinutes 9.0)) 1M

            Expect.isError over "an over-long declaration is refused"
            Expect.isOk under "a declaration inside the cap passes"

            match over with
            | Error d ->
                Expect.equal
                    d.Dimension
                    (ComputeBudgetDimension.label ComputeBudgetDimension.RunDuration)
                    "the run-duration dimension is named"

                Expect.equal d.Quota 600M "the quota is the cap in whole seconds"
            | Ok() -> failtest "expected a denial"
        }

        test "an UNDECLARED duration is clamped to the cap, not admitted unbounded" {
            // The half that makes MaxRunDuration worth having: a cap that
            // only refused over-long declarations would bind exactly the
            // callers who were already honest.
            let limits = ComputeBudgetLimits.runDuration (TimeSpan.FromMinutes 10.0)

            Expect.equal
                (ComputeBudgetPolicy.effectiveDuration limits None)
                (Some(TimeSpan.FromMinutes 10.0))
                "no declaration is clamped to the cap"

            Expect.equal
                (ComputeBudgetPolicy.effectiveDuration limits (Some(TimeSpan.FromMinutes 3.0)))
                (Some(TimeSpan.FromMinutes 3.0))
                "a shorter declaration is left alone"

            Expect.equal
                (ComputeBudgetPolicy.effectiveDuration ComputeBudgetLimits.unrestricted None)
                None
                "no cap leaves an undeclared duration undeclared"
        }

        test "a class override REPLACES the default rather than merging into it" {
            let budget =
                ComputeBudget.create ComputeBudgetPeriod.Monthly (ComputeBudgetLimits.concurrency 10)
                |> ComputeBudget.withClassLimits SubmitterClass.AgentInitiated (ComputeBudgetLimits.allowance 5M)

            let agent = ComputeBudget.limitsFor SubmitterClass.AgentInitiated budget

            Expect.equal agent.MaxConcurrent 0 "the agent override does NOT inherit the default concurrency"
            Expect.equal agent.PeriodAllowance 5M "it is governed entirely by its own entry"

            let human = ComputeBudget.limitsFor SubmitterClass.Human budget
            Expect.equal human.MaxConcurrent 10 "a class with no override uses the default"
        }

        test "period keys are UTC and roll at the documented boundary" {
            let lateUtc = DateTime(2026, 8, 5, 23, 30, 0, DateTimeKind.Utc)
            let nextUtc = DateTime(2026, 8, 6, 0, 30, 0, DateTimeKind.Utc)

            Expect.equal (ComputeBudgetPeriod.key ComputeBudgetPeriod.Daily lateUtc) "2026-08-05" "daily key"
            Expect.equal (ComputeBudgetPeriod.key ComputeBudgetPeriod.Daily nextUtc) "2026-08-06" "daily key rolls"
            Expect.equal (ComputeBudgetPeriod.key ComputeBudgetPeriod.Monthly lateUtc) "2026-08" "monthly key"

            Expect.equal
                (ComputeBudgetPeriod.key ComputeBudgetPeriod.Perpetual nextUtc)
                "perpetual"
                "a perpetual budget never rolls"
        }

        test "an unknown or absent submitter-class label reads as Human, not as agent" {
            // The conservative direction, and it is not the obvious one —
            // see `SubmitterClass.ofLabelOrHuman`. Defaulting legacy
            // traffic to the harshest class would refuse work that has
            // always been allowed, at upgrade time.
            Expect.equal (SubmitterClass.ofLabelOrHuman "agent") SubmitterClass.AgentInitiated "a known label parses"

            Expect.equal
                (SubmitterClass.ofLabelOrHuman "AGENT")
                SubmitterClass.AgentInitiated
                "parsing is case-tolerant"

            Expect.equal (SubmitterClass.ofLabelOrHuman "wat") SubmitterClass.Human "an unknown label is Human"
            Expect.equal (SubmitterClass.ofLabelOrHuman null) SubmitterClass.Human "a null label is Human"
            Expect.equal (SubmitterClass.parse "wat") None "…but `parse` still reports it as unknown"
        }
    ]

// ── the dispatcher decorator ─────────────────────────────────────────────

let private dispatcherTests =
    testList "Phase 451 — BudgetedComputeDispatcher" [

        testCaseAsync
            "a concurrency cap refuses the (N+1)th run, and the backend never sees it"
            (async {
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))
                let audit = RecordingAuditLog()

                let store =
                    storeWith
                        (ComputeBudget.create ComputeBudgetPeriod.Monthly (ComputeBudgetLimits.concurrency 2))
                        clock

                let backend = ControllablePoll(fun () -> ExternalOutcome.Pending)

                let budgeted =
                    BudgetedComputeDispatcher(backend, guardWith store audit clock) :> IExternalComputeDispatcher

                let! first = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)
                let! second = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)
                let! third = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)

                Expect.isOk first "the first run is admitted"
                Expect.isOk second "the second run fills the cap"
                Expect.isError third "the third is refused"

                // The load-bearing assertion: the refusal did not reach
                // the backend, so no payload left this process.
                Expect.equal backend.SubmitCount 2 "the backend saw exactly the admitted submissions"

                match third with
                | Error e -> Expect.isFalse e.Retriable "a budget refusal is terminal, never retriable"
                | Ok _ -> failtest "expected a refusal"
            })

        testCaseAsync
            "CONTROL — the same fixture with the cap raised by one admits the third"
            (async {
                // Without this, the test above is equally consistent with
                // "the third submission always fails".
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))
                let audit = RecordingAuditLog()

                let store =
                    storeWith
                        (ComputeBudget.create ComputeBudgetPeriod.Monthly (ComputeBudgetLimits.concurrency 3))
                        clock

                let backend = ControllablePoll(fun () -> ExternalOutcome.Pending)

                let budgeted =
                    BudgetedComputeDispatcher(backend, guardWith store audit clock) :> IExternalComputeDispatcher

                for _ in 1..3 do
                    let! result = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)
                    Expect.isOk result "admitted under the raised cap"

                Expect.equal backend.SubmitCount 3 "all three reached the backend"
            })

        testCaseAsync
            "settling a terminal outcome frees the concurrency slot"
            (async {
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))
                let audit = RecordingAuditLog()

                let store =
                    storeWith
                        (ComputeBudget.create ComputeBudgetPeriod.Monthly (ComputeBudgetLimits.concurrency 1))
                        clock

                let mutable terminal = false

                let backend =
                    ControllablePoll(fun () ->
                        if terminal then
                            ExternalOutcome.Succeeded "done"
                        else
                            ExternalOutcome.Pending)

                let budgeted =
                    BudgetedComputeDispatcher(backend, guardWith store audit clock) :> IExternalComputeDispatcher

                let! first = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)

                let handle =
                    match first with
                    | Ok h -> h
                    | Error e -> failtest $"expected admission: {e.Message}"

                let! blocked = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)
                Expect.isError blocked "the cap of one is full"

                // A NON-terminal poll must not release the slot.
                let! stillRunning = budgeted.Poll handle
                Expect.equal stillRunning ExternalOutcome.Pending "still running"
                let! stillBlocked = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)
                Expect.isError stillBlocked "a Pending poll does not free the slot"

                terminal <- true
                let! done' = budgeted.Poll handle
                Expect.equal done' (ExternalOutcome.Succeeded "done") "terminal"

                let! afterSettle = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)
                Expect.isOk afterSettle "the slot was released by the terminal outcome"

                let! usage = store.ReadUsage(scope, "2026-08")
                Expect.equal usage.InFlight 1 "one run in flight — the one just admitted"
            })

        testCaseAsync
            "an allowance is exhausted, and RESETS at the period boundary"
            (async {
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))
                let audit = RecordingAuditLog()

                let store =
                    storeWith (ComputeBudget.create ComputeBudgetPeriod.Daily (ComputeBudgetLimits.allowance 2M)) clock

                let backend = ControllablePoll(fun () -> ExternalOutcome.Pending)

                let budgeted =
                    BudgetedComputeDispatcher(backend, guardWith store audit clock) :> IExternalComputeDispatcher

                let! a = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)
                let! b = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)
                let! c = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)

                Expect.isOk a "one unit"
                Expect.isOk b "two units — the allowance"
                Expect.isError c "the allowance is exhausted"

                match c with
                | Error e ->
                    Expect.stringContains e.Message "period-allowance" "the allowance dimension is named"
                    Expect.stringContains e.Message "2026-08-05" "the exhausted period is named"
                | Ok _ -> failtest "expected a refusal"

                // Roll the day. Nothing resets a counter — the next period
                // is a different storage key that has never been written.
                clock.Set(DateTime(2026, 8, 6, 0, 30, 0, DateTimeKind.Utc))

                let! afterRoll = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)
                Expect.isOk afterRoll "the new period starts at zero consumption"

                let! oldPeriod = store.ReadUsage(scope, "2026-08-05")
                let! newPeriod = store.ReadUsage(scope, "2026-08-06")
                Expect.equal oldPeriod.Spent 2M "the exhausted period's row is untouched, not cleared"
                Expect.equal newPeriod.Spent 1M "the new period accounts only its own consumption"
            })

        testCaseAsync
            "per-class policy — an agent is denied where a human passes, same request, same budget"
            (async {
                // The phase's headline claim. One fixture, one budget, one
                // submission shape; the ONLY difference between the two
                // calls is the declared class.
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))
                let audit = RecordingAuditLog()

                let budget =
                    ComputeBudget.create ComputeBudgetPeriod.Monthly (ComputeBudgetLimits.concurrency 10)
                    |> ComputeBudget.withClassLimits SubmitterClass.AgentInitiated (ComputeBudgetLimits.concurrency 1)

                let store = storeWith budget clock
                let backend = ControllablePoll(fun () -> ExternalOutcome.Pending)

                let budgeted =
                    BudgetedComputeDispatcher(backend, guardWith store audit clock) :> IExternalComputeDispatcher

                // Fill the agent's cap of one.
                let! agentFirst = budgeted.Submit(scope, spec "fit" SubmitterClass.AgentInitiated)
                Expect.isOk agentFirst "the agent's single slot"

                let! agentSecond = budgeted.Submit(scope, spec "fit" SubmitterClass.AgentInitiated)
                Expect.isError agentSecond "the agent is held to its own tighter ceiling"

                // The same request from a human, against the same live
                // usage row, passes.
                let! human = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)
                Expect.isOk human "a human passes where the agent was refused"

                match agentSecond with
                | Error e -> Expect.stringContains e.Message "'agent'" "the refusal names the class it applied"
                | Ok _ -> failtest "expected a refusal"
            })

        testCaseAsync
            "transparency — an under-budget deployment gets the inner dispatcher unchanged"
            (async {
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))
                let audit = RecordingAuditLog()

                let store =
                    storeWith
                        (ComputeBudget.create ComputeBudgetPeriod.Monthly (ComputeBudgetLimits.concurrency 100))
                        clock

                let backend = RecordingDispatcher()

                let budgeted =
                    BudgetedComputeDispatcher(backend, guardWith store audit clock) :> IExternalComputeDispatcher

                Expect.equal budgeted.Backend "recording" "the decorator does not relabel the backend"

                let submitted = spec "render" SubmitterClass.Human
                let! result = budgeted.Submit(scope, submitted)

                match result with
                | Ok handle ->
                    Expect.equal handle.Backend "recording" "the inner backend's own handle is returned verbatim"
                    Expect.equal handle.NativeRef "native-1" "including its opaque native ref"
                | Error e -> failtest $"expected admission: {e.Message}"

                Expect.equal backend.SubmitCount 1 "the submission reached the backend"

                let scopeId, seenSpec = backend.Submitted |> List.head
                Expect.equal scopeId scope "the scope is passed through"
                Expect.equal seenSpec.Kind "render" "the spec is passed through"
                Expect.equal seenSpec.Payload submitted.Payload "the payload is untouched"

                Expect.isEmpty audit.Kinds "an under-budget submission emits no audit row"
            })

        testCaseAsync
            "an unrestricted budget touches no usage state at all"
            (async {
                // GP 13: the branch every deployment that budgets ONE class
                // takes for the others. If this wrote a usage row, an
                // unconstrained class would be paying for accounting it
                // never uses.
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))
                let audit = RecordingAuditLog()
                let store = storeWith ComputeBudget.unrestricted clock
                let backend = RecordingDispatcher()

                let budgeted =
                    BudgetedComputeDispatcher(backend, guardWith store audit clock) :> IExternalComputeDispatcher

                for _ in 1..5 do
                    let! result = budgeted.Submit(scope, spec "fit" SubmitterClass.AgentInitiated)
                    Expect.isOk result "unconstrained"

                let! usage = store.ReadUsage(scope, "2026-08")
                Expect.equal usage.InFlight 0 "no reservation was taken"
                Expect.equal usage.Spent 0M "and nothing was charged"
                Expect.equal backend.SubmitCount 5 "every submission reached the backend"
            })

        testCaseAsync
            "the run-duration cap CLAMPS the spec the backend is handed"
            (async {
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))
                let audit = RecordingAuditLog()

                let budget =
                    ComputeBudget.create
                        ComputeBudgetPeriod.Monthly
                        (ComputeBudgetLimits.runDuration (TimeSpan.FromMinutes 5.0))

                let store = storeWith budget clock
                let backend = RecordingDispatcher()

                let budgeted =
                    BudgetedComputeDispatcher(backend, guardWith store audit clock) :> IExternalComputeDispatcher

                // Declares no timeout — the case a refusal-only cap would
                // miss entirely.
                let! result = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)
                Expect.isOk result "admitted"

                let _, seen = backend.Submitted |> List.head

                Expect.equal
                    seen.Timeout
                    (Some(TimeSpan.FromMinutes 5.0))
                    "the backend was handed the capped duration, not an unbounded one"

                // And an over-long declaration is refused outright.
                let overLong =
                    spec "fit" SubmitterClass.Human
                    |> ExternalWorkSpec.withTimeout (TimeSpan.FromHours 2.0)

                let! refused = budgeted.Submit(scope, overLong)
                Expect.isError refused "an over-long declaration is refused"
                Expect.equal backend.SubmitCount 1 "and never reached the backend"
            })

        testCaseAsync
            "a backend refusal releases the reservation rather than stranding it"
            (async {
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))
                let audit = RecordingAuditLog()

                let store =
                    storeWith
                        (ComputeBudget.create ComputeBudgetPeriod.Monthly (ComputeBudgetLimits.concurrency 1))
                        clock

                let refusing =
                    { new IExternalComputeDispatcher with
                        member _.Backend = "refusing"
                        member _.Submit(_, _) = async { return Error(ExternalComputeError.retriable "backend busy") }
                        member _.Poll _ = async { return ExternalOutcome.Pending }
                        member _.Cancel _ = async { return () }
                    }

                let budgeted =
                    BudgetedComputeDispatcher(refusing, guardWith store audit clock) :> IExternalComputeDispatcher

                let! first = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)
                Expect.isError first "the backend refused"

                let! usage = store.ReadUsage(scope, "2026-08")

                Expect.equal
                    usage.InFlight
                    0
                    "the reservation was released — a backend refusal must not consume a slot forever"

                Expect.equal
                    usage.Spent
                    0M
                    "and the allowance was given back IN FULL — a backend having a bad afternoon must not exhaust the period's budget in refusals"

                // And the refusal that comes back is the BACKEND's, not a
                // budget denial: the budget admitted this one.
                match first with
                | Error e -> Expect.isTrue e.Retriable "the backend's own retriable error is surfaced verbatim"
                | Ok _ -> failtest "expected the backend refusal"
            })

        testCaseAsync
            "Cancel passes through and does NOT settle — teardown is not a terminal state"
            (async {
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))
                let audit = RecordingAuditLog()

                let store =
                    storeWith
                        (ComputeBudget.create ComputeBudgetPeriod.Monthly (ComputeBudgetLimits.concurrency 2))
                        clock

                let backend = ControllablePoll(fun () -> ExternalOutcome.Pending)

                let budgeted =
                    BudgetedComputeDispatcher(backend, guardWith store audit clock) :> IExternalComputeDispatcher

                let! result = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)

                let handle =
                    match result with
                    | Ok h -> h
                    | Error e -> failtest $"expected admission: {e.Message}"

                do! budgeted.Cancel handle

                Expect.equal backend.Cancelled [ handle.HandleId ] "the cancel reached the backend"

                let! usage = store.ReadUsage(scope, "2026-08")

                Expect.equal
                    usage.InFlight
                    1
                    "the slot is still held — the backend may still be tearing down; Poll settles it"
            })
    ]

// ── audit ────────────────────────────────────────────────────────────────

let private auditTests =
    testList "Phase 451 — audit emission (GP 6)" [

        testCaseAsync
            "a denial records ComputeBudgetDenied carrying the typed denial verbatim"
            (async {
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))
                let audit = RecordingAuditLog()

                let store =
                    storeWith
                        (ComputeBudget.create
                            ComputeBudgetPeriod.Monthly
                            (ComputeBudgetLimits.concurrency 0 |> fun l -> { l with MaxConcurrent = 1 }))
                        clock

                let backend = ControllablePoll(fun () -> ExternalOutcome.Pending)

                let budgeted =
                    BudgetedComputeDispatcher(backend, guardWith store audit clock) :> IExternalComputeDispatcher

                let! _ = budgeted.Submit(scope, spec "fit" SubmitterClass.AgentInitiated)
                let! refused = budgeted.Submit(scope, spec "fit" SubmitterClass.AgentInitiated)
                Expect.isError refused "refused"

                Expect.equal (audit.CountOf "ComputeBudgetDenied") 1 "exactly one denial row"

                match
                    audit.Events
                    |> List.tryPick (fun (s, e) ->
                        match e with
                        | ComputeBudgetDenied p -> Some(s, p)
                        | _ -> None)
                with
                | Some(recordedScope, payload) ->
                    Expect.equal recordedScope scope "recorded under the refused scope"

                    Expect.equal payload.Surface ComputeBudgetSurface.ExternalCompute "the enforcement point is named"

                    Expect.equal payload.Kind "fit" "the work discriminator is recorded"
                    Expect.equal payload.Denial.ScopeId scope "the denial carries the scope"

                    Expect.equal
                        payload.Denial.SubmitterClass
                        "agent"
                        "and the class — so an operator can see WHICH traffic is exhausting the budget"

                    // The audit row and what the caller received must not
                    // be able to disagree.
                    match refused with
                    | Error e ->
                        Expect.equal
                            e.Message
                            (ComputeBudgetDenial.describe payload.Denial)
                            "the audited denial is the one the caller got"
                    | Ok _ -> ()
                | None -> failtest "expected a ComputeBudgetDenied row"
            })

        testCaseAsync
            "the threshold warning is emitted ONCE per period, not per submission"
            (async {
                // A per-submission warning on a nearly-exhausted budget is
                // a log flood operators mute, which is the same as not
                // having the signal.
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))
                let audit = RecordingAuditLog()

                let store =
                    storeWith (ComputeBudget.create ComputeBudgetPeriod.Daily (ComputeBudgetLimits.allowance 10M)) clock

                let backend = ControllablePoll(fun () -> ExternalOutcome.Pending)

                let budgeted =
                    BudgetedComputeDispatcher(backend, guardWith store audit clock) :> IExternalComputeDispatcher

                // 8 of 10 units crosses the 0.8 default threshold; 9 and 10
                // are past it and must NOT re-warn.
                for _ in 1..10 do
                    let! _ = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)
                    ()

                Expect.equal (audit.CountOf "ComputeBudgetWarning") 1 "exactly one crossing was reported"

                match
                    audit.Events
                    |> List.tryPick (fun (_, e) ->
                        match e with
                        | ComputeBudgetWarning p -> Some p
                        | _ -> None)
                with
                | Some payload ->
                    Expect.equal payload.Quota 10M "the quota is reported"
                    Expect.equal payload.Threshold 0.8M "and the threshold that fired"
                    Expect.equal payload.Spent 8M "at the submission that crossed it, not later"
                | None -> failtest "expected a ComputeBudgetWarning row"

                // A new period warns again — the crossing is per-period.
                clock.Set(DateTime(2026, 8, 6, 0, 30, 0, DateTimeKind.Utc))

                for _ in 1..8 do
                    let! _ = budgeted.Submit(scope, spec "fit" SubmitterClass.Human)
                    ()

                Expect.equal (audit.CountOf "ComputeBudgetWarning") 2 "the next period crosses its own threshold"
            })

        test "both audit cases round-trip through the AuditLog registry" {
            // The Phase 114 gate keys the decode registry on the emitted
            // discriminator; a case with no codec silently stops archiving.
            for name in [ "ComputeBudgetDenied"; "ComputeBudgetWarning" ] do
                Expect.isTrue
                    (AuditLog.auditDecoderByType |> Map.containsKey name)
                    $"{name} has a registry decode entry"
        }
    ]

// ── metering (Phase 9d integration) ──────────────────────────────────────

let private meteringTests =
    testList "Phase 451 — metering integration" [

        testCaseAsync
            "a settled run meters its ACTUAL cost, not its reservation"
            (async {
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))
                let store = InMemoryComputeBudgetStore(clock = clock.Fn) :> IComputeBudgetStore

                do!
                    store.SetBudget(
                        scope,
                        ComputeBudget.create ComputeBudgetPeriod.Monthly (ComputeBudgetLimits.allowance 100M)
                    )
                    |> Async.Ignore

                let metered = ResizeArray<UsageRecord>()

                // Reserve 1 unit, settle at one unit per started minute —
                // so a run of 3 minutes costs 3 and the ledger must say 3.
                let guard =
                    ComputeBudgetGuard(
                        store,
                        costModel = ComputeCostModel.perMinute 1M,
                        meter = (fun r -> async { metered.Add r }),
                        clock = clock.Fn
                    )

                let! admitted =
                    guard.Admit(
                        scope,
                        SubmitterClass.Scheduled,
                        "fit",
                        "cron",
                        ComputeBudgetSurface.ExternalCompute,
                        Map.empty,
                        None
                    )

                let reservation =
                    match admitted with
                    | Ok(r, _) -> r
                    | Error d -> failtest (ComputeBudgetDenial.describe d)

                do! guard.Settle(reservation, TimeSpan.FromMinutes 3.0)

                Expect.equal metered.Count 1 "one usage record per settled run"
                let record = metered[0]
                Expect.equal record.ResourceKind ResourceKinds.computeUnits "the compute-units resource kind"
                Expect.equal record.Quantity 3M "the actual cost, not the 1-unit reservation"
                Expect.equal record.ScopeId scope "attributed to the submitting scope"
                Expect.equal record.Unit "units" "abstract units, never a currency (GP 1)"

                // And the store agrees: 1 reserved + (3 - 1) adjustment.
                let! usage = store.ReadUsage(scope, "2026-08")
                Expect.equal usage.Spent 3M "the period spend reflects the settled cost"
                Expect.equal usage.InFlight 0 "and the slot was released"
            })

        testCaseAsync
            "an unrestricted budget meters nothing — there is no reservation to settle"
            (async {
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))
                let store = InMemoryComputeBudgetStore(clock = clock.Fn) :> IComputeBudgetStore
                let metered = ResizeArray<UsageRecord>()

                let guard =
                    ComputeBudgetGuard(store, meter = (fun r -> async { metered.Add r }), clock = clock.Fn)

                let! admitted =
                    guard.Admit(
                        scope,
                        SubmitterClass.Human,
                        "fit",
                        "u1",
                        ComputeBudgetSurface.ExternalCompute,
                        Map.empty,
                        None
                    )

                match admitted with
                | Ok(r, _) -> do! guard.Settle r
                | Error d -> failtest (ComputeBudgetDenial.describe d)

                Expect.isEmpty metered "an unconstrained submission costs nothing and meters nothing"
            })

        test "a caller-declared cost hint cannot make work CHEAPER than the floor" {
            let model = ComputeCostModel.fromHint "cost" 2M

            Expect.equal (model.Admit(Map [ "cost", "10" ])) 10M "a higher declaration is honoured"

            Expect.equal
                (model.Admit(Map [ "cost", "0.5" ]))
                2M
                "a lower declaration is floored — hints are not discounts"

            Expect.equal (model.Admit(Map [ "cost", "not-a-number" ])) 2M "an unparseable hint falls back"
            Expect.equal (model.Admit Map.empty) 2M "an absent hint falls back"
        }

        test "the per-minute model rounds UP, so a flood of short runs is not free" {
            let model = ComputeCostModel.perMinute 1M
            Expect.equal (model.Settle Map.empty (TimeSpan.FromSeconds 1.0)) 1M "a one-second run costs one unit"
            Expect.equal (model.Settle Map.empty (TimeSpan.FromSeconds 61.0)) 2M "just over a minute costs two"
        }
    ]

// ── the blob-backed store ────────────────────────────────────────────────

let private storeTests =
    testList "Phase 451 — BlobComputeBudgetStore" [

        testCaseAsync
            "a budget round-trips, and an unconfigured scope is unrestricted"
            (async {
                let blobs = InMemoryBlobStorage() :> IBlobStorage
                let store = BlobComputeBudgetStore(blobs, silentLogger) :> IComputeBudgetStore

                let! before = store.GetBudget "never-configured"

                Expect.isTrue (ComputeBudget.isUnrestricted before) "an unconfigured scope is unrestricted, not refused"

                let budget =
                    ComputeBudget.create ComputeBudgetPeriod.Daily {
                        MaxConcurrent = 4
                        MaxRunDuration = Some(TimeSpan.FromMinutes 30.0)
                        PeriodAllowance = 250.5M
                    }
                    |> ComputeBudget.withClassLimits SubmitterClass.AgentInitiated (ComputeBudgetLimits.allowance 10M)

                do! store.SetBudget(scope, budget) |> Async.Ignore
                let! read = store.GetBudget scope

                Expect.equal read budget "the whole budget round-trips, including the class override"
            })

        testCaseAsync
            "a corrupt budget blob degrades to unrestricted rather than throwing"
            (async {
                // The failure direction a budget may have: admitting work.
                // Failing closed would make one bad blob a deployment-wide
                // outage, which is how a control gets switched off.
                let blobs = InMemoryBlobStorage() :> IBlobStorage

                do!
                    blobs.Upload(
                        ComputeBudgetLayout.DefaultContainer,
                        ComputeBudgetLayout.budgetBlob scope,
                        Text.Encoding.UTF8.GetBytes "{ not json at all"
                    )
                    |> Async.Ignore

                let store = BlobComputeBudgetStore(blobs, silentLogger) :> IComputeBudgetStore
                let! read = store.GetBudget scope
                Expect.isTrue (ComputeBudget.isUnrestricted read) "unreadable reads as unrestricted"
            })

        testCaseAsync
            "usage is partitioned by scope AND period in the blob path (GP 4)"
            (async {
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))
                let blobs = InMemoryBlobStorage() :> IBlobStorage

                let store =
                    BlobComputeBudgetStore(blobs, silentLogger, clock = clock.Fn) :> IComputeBudgetStore

                let admitAny = fun (_: ComputeBudgetUsage) -> Ok()

                do! store.Admit("team-a", "2026-08", 1M, admitAny) |> Async.Ignore
                do! store.Admit("team-b", "2026-08", 5M, admitAny) |> Async.Ignore

                let! a = store.ReadUsage("team-a", "2026-08")
                let! b = store.ReadUsage("team-b", "2026-08")
                Expect.equal a.Spent 1M "team-a sees only its own consumption"
                Expect.equal b.Spent 5M "team-b sees only its own"

                let! names = blobs.List(ComputeBudgetLayout.DefaultContainer, ComputeBudgetLayout.BlobPrefix)

                Expect.contains
                    names
                    (ComputeBudgetLayout.usageBlob "team-a" "2026-08")
                    "team-a's row is under its own prefix"

                Expect.contains
                    names
                    (ComputeBudgetLayout.usageBlob "team-b" "2026-08")
                    "team-b's row is under its own prefix"
            })

        testCaseAsync
            "a usage row planted under the wrong scope's path is refused, not spent"
            (async {
                // The envelope key cross-check: a mis-derived path degrades
                // to "no consumption recorded" rather than to one tenant
                // spending another's allowance.
                let blobs = InMemoryBlobStorage() :> IBlobStorage

                let foreign: ComputeBudgetUsage = {
                    ScopeId = "team-a"
                    PeriodKey = "2026-08"
                    InFlight = 3
                    Spent = 99M
                    UpdatedAt = DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc)
                }

                do!
                    blobs.Upload(
                        ComputeBudgetLayout.DefaultContainer,
                        // team-a's envelope at team-b's own path
                        ComputeBudgetLayout.usageBlob "team-b" "2026-08",
                        ComputeBudgetJson.serialiseUsage foreign
                    )
                    |> Async.Ignore

                let store = BlobComputeBudgetStore(blobs, silentLogger) :> IComputeBudgetStore
                let! read = store.ReadUsage("team-b", "2026-08")
                Expect.equal read.Spent 0M "the foreign envelope is not read as team-b's spend"
                Expect.equal read.InFlight 0 "nor its in-flight count"
            })

        testCaseAsync
            "Settle never drives the in-flight count below zero"
            (async {
                // A negative in-flight count would silently grant extra
                // concurrency — the one direction this may not fail in.
                let blobs = InMemoryBlobStorage() :> IBlobStorage
                let store = BlobComputeBudgetStore(blobs, silentLogger) :> IComputeBudgetStore

                do! store.Settle(scope, "2026-08", 0M)
                do! store.Settle(scope, "2026-08", 0M)

                let! read = store.ReadUsage(scope, "2026-08")
                Expect.equal read.InFlight 0 "clamped at zero"
                Expect.equal read.Spent 0M "and spend is floored too"
            })

        testCaseAsync
            "concurrent admissions against a cap of one admit exactly one"
            (async {
                // The race the whole seam exists to close: read-then-decide
                // -then-write would admit every one of these, because each
                // reads the same pre-burst count.
                let blobs = InMemoryBlobStorage() :> IBlobStorage
                let store = BlobComputeBudgetStore(blobs, silentLogger) :> IComputeBudgetStore
                let limits = ComputeBudgetLimits.concurrency 1

                let decide (usage: ComputeBudgetUsage) =
                    ComputeBudgetPolicy.admit scope SubmitterClass.AgentInitiated limits usage None 1M

                let! results =
                    Array.init 16 (fun _ -> store.Admit(scope, "2026-08", 1M, decide))
                    |> Async.Parallel

                let admitted = results |> Array.filter Result.isOk |> Array.length
                Expect.equal admitted 1 "exactly one of sixteen concurrent submissions was admitted"

                let! usage = store.ReadUsage(scope, "2026-08")
                Expect.equal usage.InFlight 1 "and exactly one reservation was written"
            })
    ]

// ── the fit-enqueue enforcement point, and the federated peer ────────────

/// The substrate a submitter-facing deployment composes, temp-dir isolated.
type private FitFixture = {
    Scheduler: IJobScheduler
    Registry: IModelRegistry
    Datasets: IDatasetStore
    Audit: RecordingAuditLog
    Providers: ModelFitProviderRegistry
    Budget: IComputeBudgetStore
    Guard: ComputeBudgetGuard
    Root: string
}

let private newFitFixture (budget: ComputeBudget) (clock: TestClock) =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-451-" + Guid.NewGuid().ToString "N")

    Directory.CreateDirectory root |> ignore

    let blob = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let dataObjects = DataObjectStore(blob) :> IDataObjectStore
    let audit = RecordingAuditLog()

    let schedRoot = Path.Combine(root, "sched")
    Directory.CreateDirectory schedRoot |> ignore
    let schedStorage = LocalFileStorage.LocalFileStorage(schedRoot) :> IBlobStorage
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let jobStore = JobStore.create schedStorage eventStore

    let scheduler =
        JobScheduler.create jobStore eventStore silentChannel ServerConfig.defaults silentLogger (NoOpActivitySink())
        :> IJobScheduler

    let providers = ModelFitProviderRegistry [ ReferenceModelFitProvider.create () ]
    let registry = BlobModelRegistry.create dataObjects (audit :> IAuditLog)

    scheduler.RegisterHandler(
        ModelFitBatch.ItemHandlerName,
        ModelFitBatchItemJobHandler.create providers registry (audit :> IAuditLog) silentLogger
    )

    let budgetStore =
        InMemoryComputeBudgetStore(clock = clock.Fn) :> IComputeBudgetStore

    budgetStore.SetBudget(scope, budget) |> Async.RunSynchronously |> ignore

    {
        Scheduler = scheduler
        Registry = registry
        Datasets = BlobDatasetStore.create dataObjects
        Audit = audit
        Providers = providers
        Budget = budgetStore
        Guard = ComputeBudgetGuard(budgetStore, audit = (audit :> IAuditLog), clock = clock.Fn)
        Root = root
    }

/// The handler-backed `ModelExecutionApi` — the value a peer binding holds
/// in `ModelExecutionPeerBinding.Api`, so calling it IS the peer path.
let private apiFor (fixture: FitFixture) (userId: string) : ModelExecutionApi =
    let services = ServiceCollection()
    services.AddSingleton<IJobScheduler> fixture.Scheduler |> ignore
    services.AddSingleton<IModelRegistry> fixture.Registry |> ignore
    services.AddSingleton<IDatasetStore> fixture.Datasets |> ignore
    services.AddSingleton<IAuditLog>(fixture.Audit :> IAuditLog) |> ignore
    services.AddSingleton<ModelFitProviderRegistry> fixture.Providers |> ignore
    services.AddSingleton<ComputeBudgetGuard> fixture.Guard |> ignore

    services.AddSingleton<AccessContext>(AccessContext.unrestricted (AuthenticatedUser userId))
    |> ignore

    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- services.BuildServiceProvider()
    ModelExecutionApiHandler.modelExecutionApi ctx

let private peerSubmission (submitterClass: string) (seed: int64) : ModelExecutionPeerSubmission = {
    Vintage = {
        DatasetId = "weekly-panel"
        Version = 1
    }
    SpecPayload = """{"link":"log"}"""
    SpecHash = $"submitter-minted-{seed}"
    ProviderKind = ReferenceModelFitProvider.Kind
    Seed = seed
    Gates = []
    SubmitterClass = submitterClass
}

let private fitEnqueueTests =
    testList "Phase 451 — the fit-enqueue enforcement point" [

        testCaseAsync
            "a federated peer's AGENT submission is refused where the same submission as HUMAN passes"
            (async {
                // The claim the second enforcement point exists for. A peer
                // `SubmitFit` never touches `IExternalComputeDispatcher.Submit`
                // — it enqueues a fit job that runs in-process — so a
                // decorator-only budget would let exactly the traffic this
                // phase targets straight through.
                //
                // Asserted at the real join: `ofWireSubmission` into the
                // handler-backed `ModelExecutionApi`, which is verbatim
                // what the peer contract's `runOperation` calls.
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))

                let budget =
                    ComputeBudget.create ComputeBudgetPeriod.Monthly ComputeBudgetLimits.unrestricted
                    |> ComputeBudget.withClassLimits SubmitterClass.AgentInitiated (ComputeBudgetLimits.allowance 1M)

                let fixture = newFitFixture budget clock

                try
                    // The handler resolves the caller's config scope from
                    // the `AccessContext`, so the subject id IS the budget
                    // scope — pass the one the fixture configured.
                    let api = apiFor fixture scope

                    // First agent submission consumes the allowance of one.
                    let! first = api.SubmitFit(ModelExecutionPeerContract.ofWireSubmission (peerSubmission "agent" 1L))

                    Expect.isOk first "the peer's first agent fit fits the allowance"

                    // Second is refused — and refused with the TYPED shape,
                    // not flattened into InvalidSubmission (plan D6).
                    let! second = api.SubmitFit(ModelExecutionPeerContract.ofWireSubmission (peerSubmission "agent" 2L))

                    match second with
                    | Error(ModelExecutionRefusal.BudgetDenied denial) ->
                        Expect.equal denial.SubmitterClass "agent" "the refusal names the peer's declared class"

                        Expect.equal
                            denial.Dimension
                            (ComputeBudgetDimension.label ComputeBudgetDimension.PeriodAllowance)
                            "the allowance dimension is named"

                        Expect.equal denial.Quota 1M "the quota is enumerable data, not a string"
                        Expect.equal denial.Spent 1M "and so is the spend"
                    | Error other ->
                        failtest $"expected a typed BudgetDenied, got: {ModelExecutionRefusal.describe other}"
                    | Ok _ -> failtest "expected the agent's second fit to be refused"

                    // The control: the SAME submission declared human is
                    // admitted, under the same budget, in the same fixture.
                    let! human = api.SubmitFit(ModelExecutionPeerContract.ofWireSubmission (peerSubmission "human" 3L))

                    Expect.isOk human "a human-declared peer submission is unconstrained here"

                    // And the refusal was audited under the fit-enqueue
                    // surface, not the external-compute one.
                    match
                        fixture.Audit.Events
                        |> List.tryPick (fun (_, e) ->
                            match e with
                            | ComputeBudgetDenied p -> Some p
                            | _ -> None)
                    with
                    | Some payload ->
                        Expect.equal
                            payload.Surface
                            ComputeBudgetSurface.ModelFitEnqueue
                            "the fit-enqueue enforcement point is named on the audit row"
                    | None -> failtest "expected a ComputeBudgetDenied row"
                finally
                    // The temp root is deliberately NOT deleted: the job
                    // scheduler is still running the item jobs this batch
                    // enqueued, and pulling the directory out from under it
                    // crashes the whole pack with an unhandled write
                    // failure on a background thread. Same posture as
                    // ModelExecutionApiTests — the OS owns TEMP.
                    ()
            })

        testCaseAsync
            "a refused batch leaves the budget exactly as it found it"
            (async {
                // Unwind, not trim: a caller retrying a too-large batch
                // must not burn allowance on every attempt without ever
                // running anything.
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))

                let budget =
                    ComputeBudget.create ComputeBudgetPeriod.Monthly (ComputeBudgetLimits.allowance 2M)

                let fixture = newFitFixture budget clock

                try
                    let request (seed: int64) : FitRequest = {
                        ScopeId = scope
                        DatasetVersion = {
                            ScopeId = scope
                            DatasetId = "weekly-panel"
                            Version = 1
                        }
                        SpecRef = ModelSpecRef.ofPayload """{"opaque":"spec"}"""
                        ProviderKind = ReferenceModelFitProvider.Kind
                        Seed = seed
                        Gates = []
                        SubmitterClass = SubmitterClass.AgentInitiated
                    }

                    let batch: FitRequestBatch = {
                        BatchId = "wave-1"
                        ScopeId = scope
                        // Three items against an allowance of two.
                        Requests = [ request 1L; request 2L; request 3L ]
                    }

                    let! result =
                        ModelFitBatch.submitBudgeted
                            fixture.Scheduler
                            (fixture.Audit :> IAuditLog)
                            (Some fixture.Guard)
                            "agent-1"
                            batch

                    match result with
                    | Error(FitBatchError.BudgetDenied _) -> ()
                    | Error other -> failtest $"expected a budget denial, got: {FitBatchError.describe other}"
                    | Ok _ -> failtest "expected the over-large batch to be refused"

                    let! usage = fixture.Budget.ReadUsage(scope, "2026-08")
                    Expect.equal usage.Spent 0M "every reservation taken before the refusal was released"
                    Expect.equal usage.InFlight 0 "and no slot is stranded"

                    // Nothing was enqueued and nothing was audited as
                    // submitted — a refused batch leaves no residue.
                    Expect.equal
                        (fixture.Audit.CountOf "ModelFitBatchSubmitted")
                        0
                        "no submission row for work that never happened"
                finally
                    // The temp root is deliberately NOT deleted: the job
                    // scheduler is still running the item jobs this batch
                    // enqueued, and pulling the directory out from under it
                    // crashes the whole pack with an unhandled write
                    // failure on a background thread. Same posture as
                    // ModelExecutionApiTests — the OS owns TEMP.
                    ()
            })

        testCaseAsync
            "with no guard composed, the enqueue path is the pre-451 behaviour exactly"
            (async {
                // GP 11 / GP 13: `submit` is `submitBudgeted` with `None`,
                // and a deployment that never enables budgets must not be
                // able to tell this phase shipped.
                let clock = TestClock(DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc))

                let fixture =
                    newFitFixture
                        (ComputeBudget.create ComputeBudgetPeriod.Monthly (ComputeBudgetLimits.allowance 1M))
                        clock

                try
                    let request (seed: int64) : FitRequest = {
                        ScopeId = scope
                        DatasetVersion = {
                            ScopeId = scope
                            DatasetId = "weekly-panel"
                            Version = 1
                        }
                        SpecRef = ModelSpecRef.ofPayload """{"opaque":"spec"}"""
                        ProviderKind = ReferenceModelFitProvider.Kind
                        Seed = seed
                        Gates = []
                        SubmitterClass = SubmitterClass.AgentInitiated
                    }

                    let batch: FitRequestBatch = {
                        BatchId = "wave-2"
                        ScopeId = scope
                        Requests = [ request 1L; request 2L; request 3L ]
                    }

                    // The same batch that was refused above, against the
                    // same budget — but with no guard passed.
                    let! result = ModelFitBatch.submit fixture.Scheduler (fixture.Audit :> IAuditLog) "agent-1" batch

                    match result with
                    | Ok submission -> Expect.equal submission.ItemCount 3 "every item was enqueued"
                    | Error e -> failtest $"expected admission with no budget composed: {FitBatchError.describe e}"

                    let! usage = fixture.Budget.ReadUsage(scope, "2026-08")
                    Expect.equal usage.Spent 0M "the budget store was never touched"
                    Expect.equal (fixture.Audit.CountOf "ComputeBudgetDenied") 0 "and nothing was refused"
                finally
                    // The temp root is deliberately NOT deleted: the job
                    // scheduler is still running the item jobs this batch
                    // enqueued, and pulling the directory out from under it
                    // crashes the whole pack with an unhandled write
                    // failure on a background thread. Same posture as
                    // ModelExecutionApiTests — the OS owns TEMP.
                    ()
            })
    ]

[<Tests>]
let tests =
    testList "Phase 451 — compute-budget governance" [
        policyTests
        dispatcherTests
        auditTests
        meteringTests
        storeTests
        fitEnqueueTests
    ]