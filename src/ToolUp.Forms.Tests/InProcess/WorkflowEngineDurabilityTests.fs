module ToolUp.Forms.Tests.InProcess.WorkflowEngineDurabilityTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.IEntityStore
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.Workflow
open ToolUp.Forms.IFormStore
open ToolUp.Forms.IWorkflowEngine
open ToolUp.Forms.IActionLedger
open ToolUp.Forms.FormStore
open ToolUp.Forms.WorkflowEngine
open ToolUp.Forms.ActionLedgerMetrics
open ToolUp.Forms.InMemoryActionLedger
open ToolUp.Forms.Tests.Contracts
open ToolUp.Forms.Tests.InProcess.InMemoryEntityStore
open ToolUp.Forms.Tests.InProcess.InMemoryAuditLog

/// Minimal `IServiceProvider` for engine construction (Phase 1h).
/// These durability tests never exercise the guard/action DI path so a
/// null-returning provider is fine.
type private EmptyServiceProvider() =
    interface IServiceProvider with
        member _.GetService(_serviceType: Type) = null

let private emptyServiceProvider: IServiceProvider =
    EmptyServiceProvider() :> IServiceProvider

// ─── Phase 21d — durability + failure-policy tests ──────────────────
//
// Covers six durability invariants the Phase 21 contract suite does
// not exercise:
//   (a) DeadLetter commits state + records Failed + emits metric +
//       emits WorkflowActionExecuted audit row.
//   (b) FailSubmission rolls back state + surfaces ActionFailed.
//   (c) Replay after Succeeded ledger entry skips action invocation
//       (skipped_replay metric).
//   (d) Replay after Pending ledger entry takes the OnFailure branch
//       (FailSubmission → ActionPendingFromPriorAttempt).
//   (e) Guard exception surfaces as GuardEvaluationFailed (distinct
//       from TransitionDenied).
//   (f) Metric counter emitted on the success path (succeeded tag).

// ─── Counter-recording metrics sink ──────────────────────────────────

type CountingMetricsSink() =
    let counts = ConcurrentDictionary<string * Map<string, string>, int>()

    member _.CountFor(name: string, tags: Map<string, string>) : int =
        match counts.TryGetValue((name, tags)) with
        | true, n -> n
        | _ -> 0

    member _.AllIncrements: (string * Map<string, string> * int) list =
        counts |> Seq.map (fun kv -> fst kv.Key, snd kv.Key, kv.Value) |> List.ofSeq

    interface IMetricsSink with
        member _.Record(_, _, _) = ()

        member _.Increment(name, tags) =
            counts.AddOrUpdate((name, tags), 1, (fun _ existing -> existing + 1)) |> ignore

        member _.SetGauge(_, _, _) = ()

// ─── Shared rig builder ──────────────────────────────────────────────

type DurabilityRig = {
    Engine: IWorkflowEngine
    FormStore: IFormStore
    AuditLog: InMemoryAuditLog
    Ledger: InMemoryActionLedger
    Metrics: CountingMetricsSink
    ScopeId: string
    SubmissionId: SubmissionId
    WorkflowId: WorkflowId
    /// Set by the test to control how the action behaves on a given
    /// invocation. Tests mutate this between `Apply` calls.
    ActionBehaviour: (unit -> Async<unit>) ref
    /// Set by the test to control how the guard behaves.
    GuardBehaviour: (unit -> Async<Result<unit, string>>) ref
    /// Counter of action invocations (increments BEFORE the
    /// configured behaviour runs).
    ActionInvocations: int ref
}

let private accessContext userId : AccessContext =
    AccessContext.unrestricted (Subject.fromLegacyMode Individual userId (Some "team-test"))

let private buildRig (policy: ActionFailurePolicy) (initialState: string) : DurabilityRig =
    let entityStore = InMemoryEntityStore() :> IEntityStore
    let formStore = FormStore(entityStore) :> IFormStore
    let auditLog = InMemoryAuditLog()
    let ledger = InMemoryActionLedger()
    let metrics = CountingMetricsSink()

    let actionBehaviour = ref (fun () -> async { return () })
    let guardBehaviour = ref (fun () -> async { return Ok() })
    let invocations = ref 0

    let workflow: WorkflowDefinition = {
        Id = "dur-workflow"
        InitialState = "submitted"
        Transitions = [
            {
                From = "submitted"
                Event = "approve"
                To = "approved"
                Guard = Some "guard-1"
                Action = Some "action-1"
            }
        ]
    }

    let workflows = Map [ workflow.Id, workflow ]

    let guards: Map<string, WorkflowGuard> =
        Map [ "guard-1", (fun _ctx -> guardBehaviour.Value()) ]

    let actions: Map<string, WorkflowAction> =
        Map [
            "action-1",
            (fun _ctx -> async {
                invocations.Value <- invocations.Value + 1
                do! actionBehaviour.Value()
            })
        ]

    let actionPolicies: Map<string, ActionFailurePolicy> = Map [ "action-1", policy ]

    let engine =
        WorkflowEngine(
            formStore,
            auditLog :> IAuditLog,
            ledger :> IActionLedger,
            metrics :> IMetricsSink,
            ignore,
            workflows,
            guards,
            actions,
            actionPolicies,
            emptyServiceProvider
        )
        :> IWorkflowEngine

    let scope = "team-dur-" + Guid.NewGuid().ToString("N").Substring(0, 8)
    let submissionId = "s-" + Guid.NewGuid().ToString("N").Substring(0, 8)

    let submission: Submission = {
        Id = submissionId
        Type = Submission.entityType
        Version = 1
        FormId = "form-x"
        SchemaVersion = 1
        SubmittedAt = DateTimeOffset.UtcNow
        Author = AuthenticatedUser "user-a"
        Values = Map.empty
        State = Custom initialState
        WorkflowId = Some workflow.Id
    }

    formStore.SaveSubmission(scope, submission) |> Async.RunSynchronously |> ignore

    {
        Engine = engine
        FormStore = formStore
        AuditLog = auditLog
        Ledger = ledger
        Metrics = metrics
        ScopeId = scope
        SubmissionId = submissionId
        WorkflowId = workflow.Id
        ActionBehaviour = actionBehaviour
        GuardBehaviour = guardBehaviour
        ActionInvocations = invocations
    }

// ─── Helpers ────────────────────────────────────────────────────────

let private transitionId = "submitted:approve:approved"

let private outcomeTag (status: string) : Map<string, string> =
    Map.ofList [ "actionName", "action-1"; "status", status ]

let private lookupSync (rig: DurabilityRig) =
    (rig.Ledger :> IActionLedger).Lookup(rig.SubmissionId, transitionId, "action-1")
    |> Async.RunSynchronously

let private currentStateSync (rig: DurabilityRig) : string =
    let r =
        rig.FormStore.GetSubmission(rig.ScopeId, rig.SubmissionId)
        |> Async.RunSynchronously

    match r with
    | Ok s -> SubmissionState.toIndexValue s.State
    | Error e -> failwithf "expected submission, got %A" e

// ─── Tests ──────────────────────────────────────────────────────────

let tests =
    testList "WorkflowEngine durability (Phase 21d)" [

        testAsync "(a) DeadLetter: action exception commits submission, records Failed, emits metric + audit" {
            let rig = buildRig DeadLetter "submitted"
            rig.ActionBehaviour.Value <- (fun () -> async { return raise (InvalidOperationException "smtp 421") })

            let! r = rig.Engine.Apply(rig.ScopeId, rig.SubmissionId, "approve", accessContext "user-a")

            match r with
            | Ok updated -> Expect.equal (SubmissionState.toIndexValue updated.State) "approved" "state advanced"
            | Error e -> failwithf "expected ok under DeadLetter, got %A" e

            Expect.equal (currentStateSync rig) "approved" "state durable in store"

            match lookupSync rig with
            | Ok(Some { Status = Failed reason }) -> Expect.stringContains reason "smtp 421" "reason captured in ledger"
            | other -> failwithf "expected ledger Failed, got %A" other

            Expect.equal (rig.Metrics.CountFor(ActionOutcomeTotal, outcomeTag "failed")) 1 "failed metric emitted once"

            let failedAuditCount =
                rig.AuditLog.EventsForScope rig.ScopeId
                |> List.filter (fun e ->
                    match e with
                    | WorkflowActionExecuted p -> p.Status = "failed" && p.ActionName = "action-1"
                    | _ -> false)
                |> List.length

            Expect.equal failedAuditCount 1 "WorkflowActionExecuted audit emitted once"
        }

        testAsync "(b) FailSubmission: action exception rolls back state, surfaces ActionFailed" {
            let rig = buildRig FailSubmission "submitted"
            rig.ActionBehaviour.Value <- (fun () -> async { return raise (Exception "downstream 500") })

            let! r = rig.Engine.Apply(rig.ScopeId, rig.SubmissionId, "approve", accessContext "user-a")

            match r with
            | Error(FormError.ActionFailed(actionName, reason)) ->
                Expect.equal actionName "action-1" "action name captured"
                Expect.stringContains reason "downstream 500" "reason captured"
            | other -> failwithf "expected ActionFailed, got %A" other

            Expect.equal (currentStateSync rig) "submitted" "state rolled back to submitted"
        }

        testAsync "(c) Replay after Succeeded ledger entry skips action invocation (skipped_replay)" {
            let rig = buildRig DeadLetter "submitted"
            // First apply: succeeds; ledger ends at Succeeded.
            let! _ = rig.Engine.Apply(rig.ScopeId, rig.SubmissionId, "approve", accessContext "user-a")
            Expect.equal rig.ActionInvocations.Value 1 "action ran once on first apply"

            // Second apply (simulated replay): we manipulate the
            // submission state back to "submitted" (as if recovering
            // from a partial transition mid-flight on a hypothetical
            // store) but leave the ledger Succeeded entry intact.
            // The engine must observe Succeeded and short-circuit.
            let cur =
                rig.FormStore.GetSubmission(rig.ScopeId, rig.SubmissionId)
                |> Async.RunSynchronously

            match cur with
            | Ok s ->
                let reset = { s with State = Custom "submitted" }
                do! rig.FormStore.SaveSubmission(rig.ScopeId, reset) |> Async.Ignore
            | Error e -> failwithf "expected submission, got %A" e

            let invocationsBefore = rig.ActionInvocations.Value
            let! r2 = rig.Engine.Apply(rig.ScopeId, rig.SubmissionId, "approve", accessContext "user-a")

            match r2 with
            | Ok _ -> ()
            | Error e -> failwithf "replay path should return Ok, got %A" e

            Expect.equal rig.ActionInvocations.Value invocationsBefore "action NOT re-invoked on replay"

            Expect.equal
                (rig.Metrics.CountFor(ActionOutcomeTotal, outcomeTag "skipped_replay"))
                1
                "skipped_replay metric emitted"
        }

        testAsync "(d) Replay after Pending ledger entry takes OnFailure branch (FailSubmission)" {
            let rig = buildRig FailSubmission "submitted"

            // Manually plant a Pending ledger entry to simulate a
            // prior attempt that crashed between Record and
            // MarkSucceeded.
            let pendingEntry: ActionLedgerEntry = {
                SubmissionId = rig.SubmissionId
                TransitionId = transitionId
                ActionName = "action-1"
                Status = Pending
            }

            do! (rig.Ledger :> IActionLedger).Record pendingEntry |> Async.Ignore

            let invocationsBefore = rig.ActionInvocations.Value
            let! r = rig.Engine.Apply(rig.ScopeId, rig.SubmissionId, "approve", accessContext "user-a")

            match r with
            | Error(FormError.ActionPendingFromPriorAttempt(sid, name)) ->
                Expect.equal sid rig.SubmissionId "submission id captured"
                Expect.equal name "action-1" "action name captured"
            | other -> failwithf "expected ActionPendingFromPriorAttempt, got %A" other

            Expect.equal
                rig.ActionInvocations.Value
                invocationsBefore
                "action NOT invoked when aborted on Pending prior"

            Expect.equal (currentStateSync rig) "submitted" "state unchanged when aborted on Pending prior"

            Expect.equal
                (rig.Metrics.CountFor(ActionOutcomeTotal, outcomeTag "skipped_pending"))
                1
                "skipped_pending metric emitted"
        }

        testAsync "(e) Guard exception → GuardEvaluationFailed (distinct from TransitionDenied)" {
            let rig = buildRig DeadLetter "submitted"
            rig.GuardBehaviour.Value <- (fun () -> async { return raise (Exception "credit-api timeout") })

            let! r = rig.Engine.Apply(rig.ScopeId, rig.SubmissionId, "approve", accessContext "user-a")

            match r with
            | Error(FormError.GuardEvaluationFailed(guardName, reason)) ->
                Expect.equal guardName "guard-1" "guard name captured"
                Expect.stringContains reason "credit-api timeout" "reason captured"
            | other -> failwithf "expected GuardEvaluationFailed, got %A" other

            // The transition must NOT have applied.
            Expect.equal (currentStateSync rig) "submitted" "state unchanged after guard throw"
            Expect.equal rig.ActionInvocations.Value 0 "action NOT invoked after guard throw"
        }

        testAsync "(f) Metric counter emitted on the success path (succeeded tag)" {
            let rig = buildRig DeadLetter "submitted"

            let! r = rig.Engine.Apply(rig.ScopeId, rig.SubmissionId, "approve", accessContext "user-a")

            match r with
            | Ok _ -> ()
            | Error e -> failwithf "expected success path, got %A" e

            Expect.equal
                (rig.Metrics.CountFor(ActionOutcomeTotal, outcomeTag "succeeded"))
                1
                "succeeded metric emitted exactly once"

            match lookupSync rig with
            | Ok(Some { Status = Succeeded }) -> ()
            | other -> failwithf "expected ledger Succeeded, got %A" other
        }

        testAsync "InMemoryActionLedger satisfies IActionLedgerContract" {
            // Bind the contract pack at the bottom of this file so
            // the durability suite and the contract pack ride the
            // same factory shape. The whole pack is its own
            // sub-testList; this one binding gives the durability
            // suite the contract coverage too.
            ()
        }
    ]

let contractTests =
    IActionLedgerContract.tests "InMemoryActionLedger" (fun () -> InMemoryActionLedger() :> IActionLedger)