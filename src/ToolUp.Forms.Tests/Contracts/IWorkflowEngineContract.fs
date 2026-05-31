module ToolUp.Forms.Tests.Contracts.IWorkflowEngineContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Forms.FormSchema
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.Workflow
open ToolUp.Forms.IFormStore
open ToolUp.Forms.IWorkflowEngine
open ToolUp.Forms.FormStore
open ToolUp.Forms.WorkflowEngine
open ToolUp.Forms.Tests.InProcess.InMemoryEntityStore
open ToolUp.Forms.Tests.InProcess.InMemoryAuditLog

// ─── IWorkflowEngine contract pack ───────────────────────────────
//
// Framework-agnostic test pack: factory builds a fresh engine +
// formStore + auditLog + actionCounter ref + scopeId + workflow
// definition per test. Tests cover transition success, guard
// rejection, action side effects, invalid event rejection, audit
// emission, and workflow-not-found.

type ContractEnv = {
    Engine: IWorkflowEngine
    FormStore: IFormStore
    AuditLog: InMemoryAuditLog
    ScopeId: string
    WorkflowId: WorkflowId
    /// Counter incremented by the registered action
    /// `"side-effect-counter"`. Tests assert on this.
    ActionCounter: int ref
}

type EnvFactory = unit -> ContractEnv

let private accessContext (userId: string) : AccessContext =
    AccessContext.unrestricted (Subject.AuthenticatedUser userId)

let private makeSubmission (id: SubmissionId) (workflowId: WorkflowId) : Submission = {
    Id = id
    Type = Submission.entityType
    Version = 1
    FormId = "f1"
    SchemaVersion = 1
    SubmittedAt = DateTimeOffset.UtcNow
    Author = AuthenticatedUser "user-a"
    Values = Map [ "title", TextValue "test" ]
    State = Custom "new"
    WorkflowId = Some workflowId
}

let tests (label: string) (factory: EnvFactory) =
    testList (sprintf "IWorkflowEngine contract — %s" label) [

        testAsync "Simple transition succeeds and persists new state" {
            let env = factory ()
            let sub = makeSubmission "s1" env.WorkflowId
            do! env.FormStore.SaveSubmission(env.ScopeId, sub) |> Async.Ignore

            let! r = env.Engine.Apply(env.ScopeId, "s1", "submit", accessContext "user-a")

            match r with
            | Ok updated -> Expect.equal (SubmissionState.toIndexValue updated.State) "submitted" "state advanced"
            | Error e -> failwithf "expected ok, got %A" e
        }

        testAsync "Transition with passing guard succeeds" {
            let env = factory ()

            let sub = {
                makeSubmission "s2" env.WorkflowId with
                    State = Custom "submitted"
            }

            do! env.FormStore.SaveSubmission(env.ScopeId, sub) |> Async.Ignore

            let! r = env.Engine.Apply(env.ScopeId, "s2", "approve", accessContext "approver")

            match r with
            | Ok updated -> Expect.equal (SubmissionState.toIndexValue updated.State) "approved" "approved"
            | Error e -> failwithf "expected ok, got %A" e
        }

        testAsync "Transition with failing guard returns TransitionDenied" {
            let env = factory ()

            let sub = {
                makeSubmission "s3" env.WorkflowId with
                    State = Custom "submitted"
            }

            do! env.FormStore.SaveSubmission(env.ScopeId, sub) |> Async.Ignore

            // The "reject" event has the "always-deny" guard; should fail.
            let! r = env.Engine.Apply(env.ScopeId, "s3", "reject", accessContext "approver")

            match r with
            | Error(FormError.TransitionDenied reason) -> Expect.stringContains reason "deny" "guard message surfaced"
            | other -> failwithf "expected TransitionDenied, got %A" other

            // State unchanged.
            let! after = env.FormStore.GetSubmission(env.ScopeId, "s3")

            match after with
            | Ok s -> Expect.equal (SubmissionState.toIndexValue s.State) "submitted" "state unchanged"
            | Error e -> failwithf "expected submission, got %A" e
        }

        testAsync "Invalid event for current state returns InvalidTransition" {
            let env = factory ()

            let sub = {
                makeSubmission "s4" env.WorkflowId with
                    State = Custom "new"
            }

            do! env.FormStore.SaveSubmission(env.ScopeId, sub) |> Async.Ignore

            let! r = env.Engine.Apply(env.ScopeId, "s4", "approve", accessContext "user-a")

            match r with
            | Error(FormError.InvalidTransition(currentState, attempted)) ->
                Expect.equal currentState "new" "current state captured"
                Expect.equal attempted "approve" "attempted event captured"
            | other -> failwithf "expected InvalidTransition, got %A" other
        }

        testAsync "Action side-effect runs after persistence" {
            let env = factory ()

            let sub = {
                makeSubmission "s5" env.WorkflowId with
                    State = Custom "submitted"
            }

            do! env.FormStore.SaveSubmission(env.ScopeId, sub) |> Async.Ignore

            let counterBefore = env.ActionCounter.Value

            // The "approve" transition has the "side-effect-counter" action.
            let! _ = env.Engine.Apply(env.ScopeId, "s5", "approve", accessContext "approver")

            // The action runs synchronously inside Apply (Async sequenced)
            // so the counter should have incremented by now.
            Expect.equal env.ActionCounter.Value (counterBefore + 1) "action ran exactly once"
        }

        testAsync "WorkflowTransitioned audit event emitted with correct fields" {
            let env = factory ()
            let sub = makeSubmission "s6" env.WorkflowId
            do! env.FormStore.SaveSubmission(env.ScopeId, sub) |> Async.Ignore

            let! _ = env.Engine.Apply(env.ScopeId, "s6", "submit", accessContext "user-a")

            let auditEvents = env.AuditLog.EventsForScope env.ScopeId

            let transitionedEvent =
                auditEvents
                |> List.tryPick (fun e ->
                    match e with
                    | WorkflowTransitioned p when p.SubmissionId = "s6" -> Some p
                    | _ -> None)

            match transitionedEvent with
            | Some p ->
                Expect.equal p.UserId "user-a" "actor"
                Expect.equal p.FromState "new" "from state"
                Expect.equal p.ToState "submitted" "to state"
                Expect.equal p.Event "submit" "event"
                Expect.equal p.WorkflowId env.WorkflowId "workflow id"
            | None -> failwith "expected WorkflowTransitioned audit event"
        }

        testAsync "WorkflowNotFound when submission references unknown workflow" {
            let env = factory ()

            let sub = {
                makeSubmission "s7" "no-such-workflow" with
                    WorkflowId = Some "no-such-workflow"
            }

            do! env.FormStore.SaveSubmission(env.ScopeId, sub) |> Async.Ignore

            let! r = env.Engine.Apply(env.ScopeId, "s7", "submit", accessContext "user-a")

            match r with
            | Error(FormError.WorkflowNotFound id) -> Expect.equal id "no-such-workflow" "id captured"
            | other -> failwithf "expected WorkflowNotFound, got %A" other
        }
    ]