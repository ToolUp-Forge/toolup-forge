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

/// Phase 54c — a hook that ALSO implements `ITenantLifecyclePreview`. Its
/// `OnDeprovisioned` flips `mutated` so a preview-mutates-nothing canary
/// can assert the destructive path was never taken.
type private PreviewStub(name: string, wouldAffect: int, mutated: bool ref) =
    interface ITenantLifecycle with
        member _.Name = name
        member _.OnProvisioned(_scopeId, _actorUserId) = async { return LifecycleHookResult.Completed }

        member _.OnDeprovisioned(_scopeId, _actorUserId) = async {
            mutated.Value <- true
            return LifecycleHookResult.Completed
        }

    interface ITenantLifecyclePreview with
        member _.OnDeprovisionPreview(_scopeId, _actorUserId) = async {
            return LifecyclePreviewItem.affecting name wouldAffect (sprintf "%d would be affected" wouldAffect)
        }

/// Phase 305 — a hook that ALSO implements
/// `ITenantLifecycleProvisionContext`. It records the context of every
/// provisioning invocation, so a threading test can assert the aggregator
/// forwarded the `ProvisioningRequest` to `OnProvisionedWith` (and passed
/// `None` on the request-free path).
type private RecordingProvisionHook(name: string) =
    let received = ResizeArray<TenantProvisioningContext>()
    member _.Received = List.ofSeq received

    interface ITenantLifecycle with
        member _.Name = name

        member _.OnProvisioned(scopeId, actorUserId) = async {
            received.Add {
                ScopeId = scopeId
                ActorUserId = actorUserId
                Request = None
            }

            return LifecycleHookResult.Completed
        }

        member _.OnDeprovisioned(_scopeId, _actorUserId) = async {
            return LifecycleHookResult.Skipped "provision-only recording hook"
        }

    interface ITenantLifecycleProvisionContext with
        member _.OnProvisionedWith(context) = async {
            received.Add context
            return LifecycleHookResult.Completed
        }

let private sampleRequest: ProvisioningRequest = {
    Slug = "acme"
    OwnerUserId = "owner-42"
    Region = "eu-west"
    Tier = "pro"
    DisplayName = "Acme Corp"
}

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

        // ─── Phase 54h — runGuardedWith under an explicit ILifecycleLock ─

        testCaseAsync
            "runGuardedWith returns AlreadyInProgress when the lock refuses (loser sees offboard already in progress)"
        <| async {
            let audit = AuditCollector()

            // A lock that refuses every acquire — models a peer replica
            // already holding the scope under a distributed lock.
            let refusingLock =
                { new ILifecycleLock with
                    member _.Acquire(_scopeId) = async { return None }
                }

            let! outcome =
                TenantLifecycleAggregator.runGuardedWith
                    refusingLock
                    audit.Emit
                    [ completed "h" ]
                    Deprovisioning
                    "team-busy"
                    "admin"

            Expect.equal
                outcome
                TenantLifecycleAggregator.GuardedRun.AlreadyInProgress
                "a refused acquire yields AlreadyInProgress — the offboard never starts"

            Expect.isEmpty audit.Events "no audit emitted — the run never started, so no hooks ran and no marker fired"
        }

        testCaseAsync "runGuardedWith over the in-process default runs (Ran) — single-instance behaviour preserved"
        <| async {
            let audit = AuditCollector()
            let lock = InProcessLifecycleLock.create ()

            let! outcome =
                TenantLifecycleAggregator.runGuardedWith
                    lock
                    audit.Emit
                    [ completed "h" ]
                    Deprovisioning
                    "team-ok"
                    "admin"

            match outcome with
            | TenantLifecycleAggregator.GuardedRun.Ran summary ->
                Expect.equal (LifecycleSummary.completedCount summary) 1 "the hook ran under the in-process lock"
            | TenantLifecycleAggregator.GuardedRun.AlreadyInProgress ->
                failtest "the in-process lock blocks rather than refusing a free scope"
        }

        // ─── Phase 54a — runResumable (background offboard sweep) ────────

        testCaseAsync "runResumable with no prior progress runs every hook + emits the marker"
        <| async {
            let audit = AuditCollector()
            let store = System.Collections.Generic.HashSet<string>()
            let read () = async { return Set.ofSeq store }

            let record (outcome: LifecycleHookOutcome) = async {
                lock store (fun () -> store.Add outcome.HookName |> ignore)
            }

            let onProgress _ = async { return () }

            let hooks = [ completed "a"; completed "b"; completed "c" ]

            let! summary =
                TenantLifecycleAggregator.runResumable
                    audit.Emit
                    read
                    record
                    onProgress
                    LifecycleRetryPolicy.noRetry
                    shortTimeout
                    hooks
                    Deprovisioning
                    "team-fresh"
                    "admin"

            Expect.equal summary.Outcomes.Length 3 "every hook produced an outcome"
            Expect.equal (LifecycleSummary.completedCount summary) 3 "all completed"
            Expect.equal (Set.ofSeq store) (Set.ofList [ "a"; "b"; "c" ]) "all three recorded as done"
            Expect.contains audit.TypeNames "TenantDeprovisioned" "end-of-offboard marker emitted"
        }

        testCaseAsync "runResumable streams partial progress after each hook"
        <| async {
            let audit = AuditCollector()
            let store = System.Collections.Generic.HashSet<string>()
            let read () = async { return Set.ofSeq store }

            let record (outcome: LifecycleHookOutcome) = async {
                lock store (fun () -> store.Add outcome.HookName |> ignore)
            }

            let progressSizes = ResizeArray<int>()
            let onProgress (s: LifecycleSummary) = async { progressSizes.Add s.Outcomes.Length }

            let hooks = [ completed "a"; completed "b"; completed "c" ]

            let! _ =
                TenantLifecycleAggregator.runResumable
                    audit.Emit
                    read
                    record
                    onProgress
                    LifecycleRetryPolicy.noRetry
                    shortTimeout
                    hooks
                    Deprovisioning
                    "team-progress"
                    "admin"

            Expect.equal (List.ofSeq progressSizes) [ 1; 2; 3 ] "progress streamed N-of-M after each hook"
        }

        testCaseAsync "runResumable resumes after a simulated crash — completed hooks are NOT re-invoked"
        <| async {
            // Restart-survival: pass 1 crashes partway (a hook fails, so it
            // is not recorded as done); pass 2 (the re-dispatched job) must
            // skip the already-completed hooks and re-run only the failed
            // one, reaching the TenantDeprovisioned marker.
            let audit = AuditCollector()

            // Durable progress store shared across the two passes.
            let store = System.Collections.Generic.HashSet<string>()
            let read () = async { return Set.ofSeq store }

            let record (outcome: LifecycleHookOutcome) = async {
                lock store (fun () -> store.Add outcome.HookName |> ignore)
            }

            let onProgress _ = async { return () }

            // Per-hook invocation counters.
            let counts = System.Collections.Concurrent.ConcurrentDictionary<string, int>()

            let bump name =
                counts.AddOrUpdate(name, 1, (fun _ n -> n + 1)) |> ignore

            // `b` fails on the first pass, succeeds on the second.
            let bShouldFail = ref true

            let counting name (resultFor: unit -> LifecycleHookResult) =
                StubHook(
                    name,
                    async { return LifecycleHookResult.Completed },
                    async {
                        bump name
                        return resultFor ()
                    }
                )
                :> ITenantLifecycle

            let hooks = [
                counting "a" (fun () -> LifecycleHookResult.Completed)
                counting "b" (fun () ->
                    if bShouldFail.Value then
                        LifecycleHookResult.Failed "transient"
                    else
                        LifecycleHookResult.Completed)
                counting "c" (fun () -> LifecycleHookResult.Completed)
            ]

            // Pass 1 — crash simulation: b fails, so only a + c are recorded.
            let! pass1 =
                TenantLifecycleAggregator.runResumable
                    audit.Emit
                    read
                    record
                    onProgress
                    LifecycleRetryPolicy.noRetry
                    shortTimeout
                    hooks
                    Deprovisioning
                    "team-resume"
                    "admin"

            Expect.equal (LifecycleSummary.failedCount pass1) 1 "b failed on the first pass"
            Expect.equal (Set.ofSeq store) (Set.ofList [ "a"; "c" ]) "only the completed hooks recorded"
            Expect.equal counts["a"] 1 "a invoked once"
            Expect.equal counts["b"] 1 "b invoked once"
            Expect.equal counts["c"] 1 "c invoked once"

            // Pass 2 — the re-dispatched job. b now succeeds; a + c must be
            // skipped (their counters stay at 1).
            bShouldFail.Value <- false

            let! pass2 =
                TenantLifecycleAggregator.runResumable
                    audit.Emit
                    read
                    record
                    onProgress
                    LifecycleRetryPolicy.noRetry
                    shortTimeout
                    hooks
                    Deprovisioning
                    "team-resume"
                    "admin"

            Expect.equal counts["a"] 1 "a NOT re-invoked on resume"
            Expect.equal counts["c"] 1 "c NOT re-invoked on resume"
            Expect.equal counts["b"] 2 "only b re-invoked on resume"
            Expect.equal pass2.Outcomes.Length 3 "summary reaches all three hooks"
            Expect.equal (LifecycleSummary.completedCount pass2) 3 "all three now completed (a/c resumed, b fresh)"
            Expect.equal (Set.ofSeq store) (Set.ofList [ "a"; "b"; "c" ]) "b now recorded too"

            let deprovMarkers =
                audit.TypeNames |> List.filter (fun n -> n = "TenantDeprovisioned")

            Expect.equal deprovMarkers.Length 2 "each pass emits a TenantDeprovisioned marker"
        }

        // ─── Phase 54c — offboard preview / dry-run ─────────────────────

        testCaseAsync "previewDeprovision aggregates per-hook items and mutates nothing (canary)"
        <| async {
            let mutated = ref false
            let previewable = PreviewStub("previewable", 7, mutated) :> ITenantLifecycle
            // `completed` is a plain StubHook — no ITenantLifecyclePreview.
            let plain = completed "plain"

            let! preview = TenantLifecycleAggregator.previewDeprovision [ previewable; plain ] "team-preview" "admin"

            Expect.isFalse mutated.Value "preview never invoked OnDeprovisioned — nothing mutated"
            Expect.equal preview.ScopeId "team-preview" "scope preserved"
            Expect.equal preview.Items.Length 2 "one item per hook"
            Expect.equal preview.TotalWouldAffect 7 "total sums only preview-capable hooks"

            let plainItem = preview.Items |> List.find (fun i -> i.HookName = "plain")
            Expect.isFalse plainItem.HasPreview "a hook without ITenantLifecyclePreview surfaces no-preview"
            Expect.stringContains plainItem.Detail "no preview" "the gap is explicit"

            let previewItem = preview.Items |> List.find (fun i -> i.HookName = "previewable")
            Expect.isTrue previewItem.HasPreview "the preview-capable hook produced a real item"
            Expect.equal previewItem.WouldAffect 7 "its would-affect count is surfaced"
        }

        // ─── Phase 54b — per-hook retry ─────────────────────────────────

        testCaseAsync "runResumable retries a transient Failed hook per policy until it succeeds"
        <| async {
            let audit = AuditCollector()
            let store = System.Collections.Generic.HashSet<string>()
            let read () = async { return Set.ofSeq store }

            let record (outcome: LifecycleHookOutcome) = async {
                lock store (fun () -> store.Add outcome.HookName |> ignore)
            }

            let onProgress _ = async { return () }

            // Fast policy so the test doesn't actually sleep seconds.
            let policy: LifecycleRetryPolicy = {
                MaxAttempts = 3
                InitialBackoff = TimeSpan.FromMilliseconds 1.0
                MaxBackoff = TimeSpan.FromMilliseconds 5.0
            }

            // Fails twice, then completes (a transient store outage).
            let attempts = ref 0

            let flaky =
                StubHook(
                    "flaky",
                    async { return LifecycleHookResult.Completed },
                    async {
                        attempts.Value <- attempts.Value + 1

                        if attempts.Value < 3 then
                            return LifecycleHookResult.Failed "transient"
                        else
                            return LifecycleHookResult.Completed
                    }
                )
                :> ITenantLifecycle

            let! summary =
                TenantLifecycleAggregator.runResumable
                    audit.Emit
                    read
                    record
                    onProgress
                    policy
                    shortTimeout
                    [ flaky ]
                    Deprovisioning
                    "team-retry"
                    "admin"

            Expect.equal attempts.Value 3 "invoked 3 times (2 fails + 1 success)"
            Expect.equal (LifecycleSummary.completedCount summary) 1 "terminal outcome is Completed"
            Expect.equal (Set.ofSeq store) (Set.ofList [ "flaky" ]) "recorded as done after eventual success"
        }

        testCaseAsync "runResumable gives up after MaxAttempts — terminal Failed, NOT recorded as done"
        <| async {
            let audit = AuditCollector()
            let store = System.Collections.Generic.HashSet<string>()
            let read () = async { return Set.ofSeq store }

            let record (outcome: LifecycleHookOutcome) = async {
                lock store (fun () -> store.Add outcome.HookName |> ignore)
            }

            let onProgress _ = async { return () }

            let policy: LifecycleRetryPolicy = {
                MaxAttempts = 2
                InitialBackoff = TimeSpan.FromMilliseconds 1.0
                MaxBackoff = TimeSpan.FromMilliseconds 5.0
            }

            let attempts = ref 0

            let broken =
                StubHook(
                    "broken",
                    async { return LifecycleHookResult.Completed },
                    async {
                        attempts.Value <- attempts.Value + 1
                        return LifecycleHookResult.Failed "always"
                    }
                )
                :> ITenantLifecycle

            let! summary =
                TenantLifecycleAggregator.runResumable
                    audit.Emit
                    read
                    record
                    onProgress
                    policy
                    shortTimeout
                    [ broken ]
                    Deprovisioning
                    "team-giveup"
                    "admin"

            Expect.equal attempts.Value 2 "invoked MaxAttempts times"
            Expect.equal (LifecycleSummary.failedCount summary) 1 "terminal Failed"
            Expect.isFalse (store.Contains "broken") "a Failed hook is NOT recorded — it re-runs next sweep"
        }

        // ─── Phase 54j — export-then-erase bundle ───────────────────────

        testCaseAsync "exportThenDeprovision aborts before any erasure when the export fails (fail-closed)"
        <| async {
            let audit = AuditCollector()
            let erased = ref false

            let hook =
                StubHook(
                    "h",
                    async { return LifecycleHookResult.Completed },
                    async {
                        erased.Value <- true
                        return LifecycleHookResult.Completed
                    }
                )
                :> ITenantLifecycle

            let runExport () = async { return Error "blob store unreachable" }

            let! result =
                TenantLifecycleAggregator.exportThenDeprovision audit.Emit runExport [ hook ] "team-export" "admin"

            match result with
            | Error msg -> Expect.stringContains msg "no data erased" "the error explains the fail-closed abort"
            | Ok _ -> failtest "expected Error when the export fails"

            Expect.isFalse erased.Value "no erasure hook ran — the tenant's data is intact"
            Expect.isFalse (List.contains "TenantDataExported" audit.TypeNames) "no export audit on a failed export"
            Expect.isFalse (List.contains "TenantDeprovisioned" audit.TypeNames) "no offboard marker on abort"
        }

        testCaseAsync "exportThenDeprovision exports then erases, auditing TenantDataExported + the offboard"
        <| async {
            let audit = AuditCollector()
            let erased = ref false

            let hook =
                StubHook(
                    "h",
                    async { return LifecycleHookResult.Completed },
                    async {
                        erased.Value <- true
                        return LifecycleHookResult.Completed
                    }
                )
                :> ITenantLifecycle

            let archive = {
                Container = "_platform"
                BlobPath = "tenant-export/team-export/export-abc.json"
                ContentHash = "abc"
                SegmentCount = 3
            }

            let runExport () = async { return Ok archive }

            let! result =
                TenantLifecycleAggregator.exportThenDeprovision audit.Emit runExport [ hook ] "team-export" "admin"

            match result with
            | Ok r ->
                Expect.equal r.Archive.SegmentCount 3 "the archive reference is carried on the result"
                Expect.isTrue erased.Value "the erasure ran after the export committed"
            | Error e -> failtestf "expected Ok; got %s" e

            Expect.contains audit.TypeNames "TenantDataExported" "the export was audited"
            Expect.contains audit.TypeNames "TenantDeprovisioned" "the offboard marker was emitted"
        }

        // ─── Phase 54h — exportThenDeprovisionWith cross-replica exclusion ─

        testCaseAsync
            "exportThenDeprovisionWith returns AlreadyInProgress under a refusing lock — no export, no erasure (cross-replica exclusion)"
        <| async {
            let audit = AuditCollector()
            let exported = ref false
            let erased = ref false

            // A lock that refuses every acquire — models a peer replica
            // already mid-bundle for this scope under a distributed lock.
            let refusingLock =
                { new ILifecycleLock with
                    member _.Acquire(_scopeId) = async { return None }
                }

            let hook =
                StubHook(
                    "h",
                    async { return LifecycleHookResult.Completed },
                    async {
                        erased.Value <- true
                        return LifecycleHookResult.Completed
                    }
                )
                :> ITenantLifecycle

            // Would succeed if ever called — the assertion is that it isn't.
            let runExport () = async {
                exported.Value <- true

                return
                    Ok {
                        Container = "_platform"
                        BlobPath = "tenant-export/team-busy/export.json"
                        ContentHash = "abc"
                        SegmentCount = 1
                    }
            }

            let! outcome =
                TenantLifecycleAggregator.exportThenDeprovisionWith
                    refusingLock
                    audit.Emit
                    runExport
                    [ hook ]
                    "team-busy"
                    "admin"

            Expect.equal
                outcome
                TenantLifecycleAggregator.GuardedExportThenDeprovision.AlreadyInProgress
                "a refused acquire yields AlreadyInProgress — the whole bundle is skipped"

            Expect.isFalse exported.Value "the export never ran — the loser aborts before exporting (data untouched)"
            Expect.isFalse erased.Value "no erasure hook ran"
            Expect.isEmpty audit.Events "no audit — neither the export row nor the offboard marker fired"
        }

        testCaseAsync
            "exportThenDeprovisionWith over the in-process default runs the whole bundle (Ran) — single-instance behaviour preserved"
        <| async {
            let audit = AuditCollector()
            let erased = ref false
            let lock = InProcessLifecycleLock.create ()

            let hook =
                StubHook(
                    "h",
                    async { return LifecycleHookResult.Completed },
                    async {
                        erased.Value <- true
                        return LifecycleHookResult.Completed
                    }
                )
                :> ITenantLifecycle

            let archive = {
                Container = "_platform"
                BlobPath = "tenant-export/team-ok/export.json"
                ContentHash = "abc"
                SegmentCount = 2
            }

            let runExport () = async { return Ok archive }

            let! outcome =
                TenantLifecycleAggregator.exportThenDeprovisionWith lock audit.Emit runExport [ hook ] "team-ok" "admin"

            match outcome with
            | TenantLifecycleAggregator.GuardedExportThenDeprovision.Ran result ->
                Expect.equal result.Archive.SegmentCount 2 "the archive reference is carried on the result"
                Expect.isTrue erased.Value "the erasure ran after the export committed"
                Expect.contains audit.TypeNames "TenantDataExported" "the export was audited under the lease"
                Expect.contains audit.TypeNames "TenantDeprovisioned" "the offboard marker was emitted"
            | other -> failtestf "the in-process lock blocks a free scope rather than refusing; got %A" other
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

        // ─── Phase 305 — ProvisioningRequest threading ───────────────

        testCaseAsync "runWithRequest forwards the request to a request-aware hook's OnProvisionedWith"
        <| async {
            let audit = AuditCollector()
            let hook = RecordingProvisionHook("rec")

            let! summary =
                TenantLifecycleAggregator.runWithRequest
                    audit.Emit
                    (Some sampleRequest)
                    shortTimeout
                    [ hook :> ITenantLifecycle ]
                    Provisioning
                    "team-req"
                    "admin"

            Expect.equal (LifecycleSummary.completedCount summary) 1 "the request-aware hook completed"
            Expect.equal hook.Received.Length 1 "the hook was invoked once"
            Expect.equal hook.Received.Head.Request (Some sampleRequest) "the ProvisioningRequest was threaded through"
            Expect.equal hook.Received.Head.ScopeId "team-req" "scope threaded through"
            Expect.equal hook.Received.Head.ActorUserId "admin" "actor threaded through"
        }

        testCaseAsync "the request-free run path dispatches OnProvisionedWith with Request = None"
        <| async {
            let audit = AuditCollector()
            let hook = RecordingProvisionHook("rec")

            // `run` (no request) still routes a request-aware hook through
            // OnProvisionedWith, but with `None` — byte-identical semantics
            // to the base OnProvisioned.
            let! _ =
                TenantLifecycleAggregator.run
                    audit.Emit
                    shortTimeout
                    [ hook :> ITenantLifecycle ]
                    Provisioning
                    "team-noreq"
                    "admin"

            Expect.equal hook.Received.Length 1 "the hook was invoked once"
            Expect.equal hook.Received.Head.Request None "no request rides the request-free path"
        }

        testCaseAsync "Deprovisioning never carries a provisioning request"
        <| async {
            let audit = AuditCollector()
            let hook = RecordingProvisionHook("rec")

            let! summary =
                TenantLifecycleAggregator.run
                    audit.Emit
                    shortTimeout
                    [ hook :> ITenantLifecycle ]
                    Deprovisioning
                    "team-off"
                    "admin"

            // OnDeprovisioned Skips (provision-only recording hook), so no
            // provisioning context is recorded at all.
            Expect.equal hook.Received.Length 0 "offboard did not touch the provisioning surface"
            Expect.equal (LifecycleSummary.skippedCount summary) 1 "the offboard skipped"
        }
    ]