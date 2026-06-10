module ToolUp.Platform.Tests.InProcess.TenantLifecycleAggregatorTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform

// ─── Phase 54 — TenantLifecycleAggregator tests ──────────────────────
//
// Exercises the aggregator with synthetic `ITenantLifecycle` hooks (no
// DI container, no HTTP context — `run` is a pure function of
// (hooks, phase, scope, actor)): parallel execution, per-hook timeout,
// per-hook isolation, audit emission + counts, and the per-scope
// `runGuarded` serialisation.

/// Synthetic hook with caller-supplied provision / deprovision bodies.
type private StubHook(name: string, onProv: Async<LifecycleHookResult>, onDeprov: Async<LifecycleHookResult>) =
    interface ITenantLifecycle with
        member _.Name = name
        member _.OnProvisioned(_scopeId, _actorUserId) = onProv
        member _.OnDeprovisioned(_scopeId, _actorUserId) = onDeprov

let private completed name =
    StubHook(name, async { return LifecycleHookResult.Completed }, async { return LifecycleHookResult.Completed })
    :> ITenantLifecycle

let private skipped name reason =
    StubHook(
        name,
        async { return LifecycleHookResult.Skipped reason },
        async { return LifecycleHookResult.Skipped reason }
    )
    :> ITenantLifecycle

let private failing name err =
    StubHook(name, async { return LifecycleHookResult.Failed err }, async { return LifecycleHookResult.Failed err })
    :> ITenantLifecycle

let private throwing name =
    StubHook(name, async { return failwith "boom" }, async { return failwith "boom" }) :> ITenantLifecycle

/// Audit collector — records (scopeId, event) tuples the aggregator
/// emits, so tests can assert on the emitted family + counts.
type private AuditCollector() =
    let events = ConcurrentBag<string * AuditEvent>()
    member _.Emit (scopeId: string) (event: AuditEvent) = async { events.Add(scopeId, event) }
    member _.Events = events |> List.ofSeq

    member _.TypeNames =
        events |> Seq.map (fun (_, e) -> AuditEvent.eventTypeName e) |> List.ofSeq

let private shortTimeout = TimeSpan.FromMilliseconds 50.0

let tests =
    testList "TenantLifecycleAggregator" [

        testCaseAsync "empty hook list — empty summary + a single phase marker audit"
        <| async {
            let audit = AuditCollector()
            let! summary = TenantLifecycleAggregator.run audit.Emit shortTimeout [] Deprovisioning "team-x" "admin"

            Expect.equal summary.Outcomes [] "no outcomes"
            Expect.equal summary.ScopeId "team-x" "scope preserved"
            Expect.equal summary.Phase Deprovisioning "phase preserved"
            Expect.equal audit.TypeNames [ "TenantDeprovisioned" ] "exactly one end-of-phase marker"
        }

        testCaseAsync "all hooks run + appear in the summary outcomes"
        <| async {
            let audit = AuditCollector()
            let hooks = [ completed "a"; completed "b"; completed "c" ]
            let! summary = TenantLifecycleAggregator.run audit.Emit shortTimeout hooks Provisioning "team-y" "admin"

            Expect.equal summary.Outcomes.Length 3 "every hook produced an outcome"

            let names = summary.Outcomes |> List.map (fun o -> o.HookName) |> Set.ofList
            Expect.equal names (Set.ofList [ "a"; "b"; "c" ]) "all hook names present"
            Expect.equal (LifecycleSummary.completedCount summary) 3 "all completed"
            Expect.equal audit.TypeNames [ "TenantProvisioned" ] "provision marker emitted"
        }

        testCaseAsync "skipped + completed + failed are counted independently"
        <| async {
            let audit = AuditCollector()

            let hooks = [ completed "ok"; skipped "off" "substrate inactive"; failing "bad" "kaboom" ]

            let! summary = TenantLifecycleAggregator.run audit.Emit shortTimeout hooks Deprovisioning "team-z" "admin"

            Expect.equal (LifecycleSummary.completedCount summary) 1 "one completed"
            Expect.equal (LifecycleSummary.skippedCount summary) 1 "one skipped"
            Expect.equal (LifecycleSummary.failedCount summary) 1 "one failed"
            Expect.isTrue (LifecycleSummary.hasFailures summary) "hasFailures true"
        }

        testCaseAsync "a failing hook does NOT abort the run — every other hook still runs"
        <| async {
            let audit = AuditCollector()

            let hooks = [ completed "first"; failing "middle" "deliberate"; completed "last" ]

            let! summary = TenantLifecycleAggregator.run audit.Emit shortTimeout hooks Deprovisioning "team-iso" "admin"

            Expect.equal summary.Outcomes.Length 3 "all three hooks ran despite the middle failure"
            Expect.equal (LifecycleSummary.completedCount summary) 2 "both completers ran"
        }

        testCaseAsync "a throwing hook becomes a Failed outcome (per-hook isolation), others Completed"
        <| async {
            let audit = AuditCollector()
            let hooks = [ completed "safe"; throwing "explodes" ]

            let! summary =
                TenantLifecycleAggregator.run audit.Emit shortTimeout hooks Deprovisioning "team-throw" "admin"

            Expect.equal summary.Outcomes.Length 2 "both hooks resolved"
            Expect.equal (LifecycleSummary.completedCount summary) 1 "the safe hook completed"
            Expect.equal (LifecycleSummary.failedCount summary) 1 "the throwing hook failed (not propagated)"
        }

        testCaseAsync "a slow hook past the per-hook timeout becomes a Failed (timeout) outcome"
        <| async {
            let audit = AuditCollector()

            let slow =
                StubHook(
                    "slow",
                    async { return LifecycleHookResult.Completed },
                    async {
                        do! Async.Sleep 5000
                        return LifecycleHookResult.Completed
                    }
                )
                :> ITenantLifecycle

            let! summary =
                TenantLifecycleAggregator.run audit.Emit shortTimeout [ slow ] Deprovisioning "team-slow" "admin"

            Expect.equal (LifecycleSummary.failedCount summary) 1 "slow hook failed on timeout"

            let outcome = summary.Outcomes.Head

            match outcome.Result with
            | LifecycleHookResult.Failed msg -> Expect.stringContains msg "timeout" "failure cites the timeout"
            | other -> failtestf "expected Failed timeout; got %A" other
        }

        testCaseAsync "one TenantLifecycleHookFailed audit row per failed hook + final marker"
        <| async {
            let audit = AuditCollector()

            let hooks = [ completed "ok"; failing "f1" "e1"; failing "f2" "e2" ]

            let! _ = TenantLifecycleAggregator.run audit.Emit shortTimeout hooks Deprovisioning "team-audit" "admin"

            let failedRows =
                audit.TypeNames |> List.filter (fun n -> n = "TenantLifecycleHookFailed")

            Expect.equal failedRows.Length 2 "one failure row per failed hook"

            Expect.contains audit.TypeNames "TenantDeprovisioned" "final marker still emitted after failures"
        }

        testCaseAsync "TenantDeprovisioned payload carries the correct hook counts"
        <| async {
            let audit = AuditCollector()

            let hooks = [ completed "a"; completed "b"; skipped "c" "off"; failing "d" "x" ]

            let! _ =
                TenantLifecycleAggregator.run audit.Emit shortTimeout hooks Deprovisioning "team-counts" "operator-1"

            let marker =
                audit.Events
                |> List.tryPick (fun (_, e) ->
                    match e with
                    | AuditEvent.TenantDeprovisioned p -> Some p
                    | _ -> None)

            match marker with
            | Some p ->
                Expect.equal p.HooksRun 4 "ran 4"
                Expect.equal p.HooksCompleted 2 "2 completed"
                Expect.equal p.HooksSkipped 1 "1 skipped"
                Expect.equal p.HooksFailed 1 "1 failed"
                Expect.equal p.Actor "operator-1" "actor recorded"
                Expect.equal p.ScopeId "team-counts" "scope recorded"
            | None -> failtest "no TenantDeprovisioned marker emitted"
        }

        testCaseAsync "runGuarded serialises concurrent runs for the same scope"
        <| async {
            // Two concurrent guarded runs against the same scope must not
            // interleave: a hook that records enter/exit timestamps should
            // see strictly nested, non-overlapping windows.
            let audit = AuditCollector()
            let active = ref 0
            let maxConcurrent = ref 0
            let sync = obj ()

            let trackingHook =
                StubHook(
                    "tracker",
                    async { return LifecycleHookResult.Completed },
                    async {
                        lock sync (fun () ->
                            active.Value <- active.Value + 1
                            maxConcurrent.Value <- max maxConcurrent.Value active.Value)

                        do! Async.Sleep 100

                        lock sync (fun () -> active.Value <- active.Value - 1)
                        return LifecycleHookResult.Completed
                    }
                )
                :> ITenantLifecycle

            let runOnce () =
                TenantLifecycleAggregator.runGuarded audit.Emit [ trackingHook ] Deprovisioning "team-guard" "admin"

            let! _ = [ runOnce (); runOnce () ] |> Async.Parallel

            Expect.equal maxConcurrent.Value 1 "same-scope guarded runs never overlapped"
        }

        testCaseAsync "runGuarded allows different scopes to run in parallel"
        <| async {
            let audit = AuditCollector()
            let active = ref 0
            let maxConcurrent = ref 0
            let sync = obj ()

            let trackingHook () =
                StubHook(
                    "tracker",
                    async { return LifecycleHookResult.Completed },
                    async {
                        lock sync (fun () ->
                            active.Value <- active.Value + 1
                            maxConcurrent.Value <- max maxConcurrent.Value active.Value)

                        do! Async.Sleep 100

                        lock sync (fun () -> active.Value <- active.Value - 1)
                        return LifecycleHookResult.Completed
                    }
                )
                :> ITenantLifecycle

            let! _ =
                [
                    TenantLifecycleAggregator.runGuarded audit.Emit [ trackingHook () ] Deprovisioning "team-a" "admin"
                    TenantLifecycleAggregator.runGuarded audit.Emit [ trackingHook () ] Deprovisioning "team-b" "admin"
                ]
                |> Async.Parallel

            Expect.equal maxConcurrent.Value 2 "distinct scopes ran concurrently"
        }
    ]