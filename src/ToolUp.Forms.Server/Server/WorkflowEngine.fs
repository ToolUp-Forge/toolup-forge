module ToolUp.Forms.WorkflowEngine

open System
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.Workflow
open ToolUp.Forms.IFormStore
open ToolUp.Forms.IWorkflowEngine
open ToolUp.Forms.IActionLedger
open ToolUp.Forms.ActionLedgerMetrics

// Phase 1h — guards and actions now take a `WorkflowContext` carrying
// the resolved `IServiceProvider`. The engine receives an
// `IServiceProvider` at construction (from the compose-time DI
// factory) and builds the context per invocation. Actions resolving
// `IEntityStore` / `INotificationChannel` no longer need a request-
// side workaround.

// ─── Phase 21 / 21d — Default IWorkflowEngine implementation ───────
//
// Pure transition resolver over an immutable workflow registry +
// guard / action registries + per-action `ActionFailurePolicy` map
// (all populated at compose time, never mutated at runtime).
// Persistence flows through `IFormStore`; audit emission flows
// through `IAuditLog`; action invocation exactly-once semantics
// flow through `IActionLedger` (Phase 21d); per-action-outcome
// telemetry flows through `IMetricsSink`.
//
// **Apply flow (21d):**
//
//   1. Resolve the submission via `IFormStore.GetSubmission`.
//   2. Resolve the workflow definition via the workflow registry.
//   3. Look up the transition for `(currentState, event)`.
//   4. Run the optional guard inside `try/with`. A guard that
//      *returns* `Error reason` surfaces as `TransitionDenied`
//      (the operator-defined denial path); a guard that *throws*
//      surfaces as `GuardEvaluationFailed` (an unexpected fault,
//      callers can choose retry vs give-up).
//   5. If the transition declares an action, consult the ledger:
//        * `Some { Status = Succeeded }` — a prior attempt
//          completed the action. The state is *already* the
//          target state (or some later state) on a previous
//          successful apply. Persist the state again is a no-op
//          re-write; emit the skipped_replay metric + audit; skip
//          the action invocation; return Ok.
//        * `Some { Status = Pending }` — branch on policy.
//          `DeadLetter` retries (writes a fresh `Pending` and
//          invokes); `FailSubmission` aborts with
//          `ActionPendingFromPriorAttempt`; `LogOnly` proceeds
//          (preserves pre-21d posture).
//        * `Some { Status = Failed _ }` — proceed with retry under
//          the policy.
//        * `None` — first attempt for this triple; proceed.
//   6. Persist the new state via `IFormStore.SaveSubmission`.
//   7. Emit `WorkflowTransitioned` audit event.
//   8. If the transition declares an action: write a `Pending`
//      ledger entry, invoke the action inside `try/with`, then
//      on success `MarkSucceeded` + emit success metric/audit,
//      on exception `MarkFailed` + apply `OnFailure` policy:
//        * `FailSubmission` — roll back the state write (revert
//          the entity-store version) and return `ActionFailed`.
//        * `DeadLetter` — leave the state committed; the failed
//          ledger entry + metric + audit are the operator's
//          three independent surfaces.
//        * `LogOnly` — warn-log; same observable surfaces minus
//          the dead-letter intent.

/// Lightweight warn callback, decoupled from `Microsoft.Extensions.Logging`
/// so the test project can construct an engine without depending on
/// the logging-abstractions package. Compose roots wire this to
/// `logger.LogWarning(msg)`; tests pass `ignore`.
type WorkflowWarn = string -> unit

type WorkflowEngine
    (
        formStore: IFormStore,
        auditLog: IAuditLog,
        ledger: IActionLedger,
        metricsSink: IMetricsSink,
        warn: WorkflowWarn,
        workflows: Map<WorkflowId, WorkflowDefinition>,
        guards: Map<string, WorkflowGuard>,
        actions: Map<string, WorkflowAction>,
        actionPolicies: Map<string, ActionFailurePolicy>,
        services: IServiceProvider
    ) =

    let buildContext (submission: Submission) (accessContext: AccessContext) : WorkflowContext = {
        Submission = submission
        AccessContext = accessContext
        Services = services
    }

    let resolveCurrentState (submission: Submission) : WorkflowState =
        match submission.State with
        | Draft -> "Draft"
        | Submitted -> "Submitted"
        | Custom s -> s

    let updateSubmissionState (submission: Submission) (newState: WorkflowState) : Submission = {
        submission with
            State = SubmissionState.fromIndexValue newState
    }

    let resolvePolicy (actionName: string) : ActionFailurePolicy =
        Map.tryFind actionName actionPolicies |> Option.defaultValue DeadLetter

    let recordAuditAndMetric
        (scopeId: string)
        (submissionId: SubmissionId)
        (transitionId: string)
        (actionName: string)
        (status: string)
        (reason: string)
        : Async<unit> =
        async {
            metricsSink.Increment(ActionOutcomeTotal, Map.ofList [ "actionName", actionName; "status", status ])

            let payload: WorkflowActionExecutedPayload = {
                SubmissionId = submissionId
                TransitionId = transitionId
                ActionName = actionName
                Status = status
                Reason = reason
            }

            do! auditLog.Record(scopeId, AuditEvent.WorkflowActionExecuted payload)
        }

    interface IWorkflowEngine with

        member _.Apply(scopeId, submissionId, event, accessContext) = async {
            // 1. Load current submission.
            let! submissionResult = formStore.GetSubmission(scopeId, submissionId)

            match submissionResult with
            | Error err -> return Error err
            | Ok submission ->
                // 2. Resolve workflow.
                match submission.WorkflowId with
                | None -> return Error(FormError.WorkflowNotFound "")
                | Some wfId ->
                    match Map.tryFind wfId workflows with
                    | None -> return Error(FormError.WorkflowNotFound wfId)
                    | Some workflow ->
                        // 3. Find applicable transition.
                        let currentState = resolveCurrentState submission
                        let transitionOpt = WorkflowDefinition.tryFindTransition currentState event workflow

                        match transitionOpt with
                        | None -> return Error(FormError.InvalidTransition(currentState, event))
                        | Some transition ->
                            // 4. Run optional guard inside try/with. A
                            //    return-Error path is TransitionDenied;
                            //    a throw is GuardEvaluationFailed.
                            let! guardResult =
                                match transition.Guard with
                                | None -> async.Return(Ok())
                                | Some guardName ->
                                    match Map.tryFind guardName guards with
                                    | None ->
                                        warn (
                                            sprintf
                                                "Workflow guard '%s' not found in registry; treating as no-op"
                                                guardName
                                        )

                                        async.Return(Ok())
                                    | Some guard -> async {
                                        try
                                            return! guard (buildContext submission accessContext)
                                        with ex ->
                                            // Surface as a typed
                                            // error sentinel by encoding
                                            // the exception message
                                            // inside Result.Error; the
                                            // caller distinguishes
                                            // throw vs deny by remembering
                                            // which guard ran.
                                            return Error("__throw__:" + ex.Message)
                                      }

                            match guardResult, transition.Guard with
                            | Error reason, Some guardName when reason.StartsWith "__throw__:" ->
                                let exMessage = reason.Substring "__throw__:".Length
                                return Error(FormError.GuardEvaluationFailed(guardName, exMessage))
                            | Error reason, _ -> return Error(FormError.TransitionDenied reason)
                            | Ok(), _ ->
                                // 5. Consult ledger before deciding whether to
                                //    apply the state change or short-circuit on
                                //    replay.
                                let transitionId = ActionLedger.transitionId transition
                                let savedSubmissionId = submission.Id

                                let! priorLedger =
                                    match transition.Action with
                                    | None -> async.Return(Ok None)
                                    | Some actionName -> ledger.Lookup(savedSubmissionId, transitionId, actionName)

                                match priorLedger with
                                | Error(LedgerError.StorageFailed s) ->
                                    return Error(FormError.StorageFailed("Action ledger lookup failed: " + s))
                                | Error LedgerError.EntryMissing ->
                                    // Lookup never returns EntryMissing
                                    // by contract (Ok None instead) —
                                    // surface as StorageFailed so a
                                    // contract-violating impl is loud
                                    // rather than silent.
                                    return Error(FormError.StorageFailed "Action ledger Lookup returned EntryMissing")
                                | Ok prior ->
                                    let actionName = transition.Action

                                    // Replay-skip path. A prior Succeeded
                                    // ledger entry means we ran the action
                                    // once and persisted the state. Emit
                                    // the skipped_replay observability
                                    // signal and return the current
                                    // submission unchanged.
                                    let shouldSkipReplay =
                                        match prior with
                                        | Some { Status = Succeeded } -> true
                                        | _ -> false

                                    // Pending-from-prior-attempt path. The
                                    // prior attempt persisted state but
                                    // never resolved the action. Decision
                                    // is policy-driven.
                                    let pendingActionName =
                                        match prior, actionName with
                                        | Some { Status = Pending }, Some name -> Some name
                                        | _ -> None

                                    if shouldSkipReplay then
                                        // Action already succeeded in a
                                        // prior attempt; emit metric+audit
                                        // and return without re-persisting
                                        // or re-invoking.
                                        match actionName with
                                        | Some name ->
                                            do!
                                                recordAuditAndMetric
                                                    scopeId
                                                    savedSubmissionId
                                                    transitionId
                                                    name
                                                    StatusSkippedReplay
                                                    ""
                                        | None -> ()

                                        return Ok submission
                                    else
                                        // Pending-policy branch.
                                        let pendingDecision =
                                            match pendingActionName with
                                            | None -> Ok()
                                            | Some name ->
                                                match resolvePolicy name with
                                                | FailSubmission ->
                                                    Error(
                                                        FormError.ActionPendingFromPriorAttempt(savedSubmissionId, name)
                                                    )
                                                | DeadLetter
                                                | LogOnly -> Ok()

                                        match pendingDecision with
                                        | Error e ->
                                            match pendingActionName with
                                            | Some name ->
                                                do!
                                                    recordAuditAndMetric
                                                        scopeId
                                                        savedSubmissionId
                                                        transitionId
                                                        name
                                                        StatusSkippedPending
                                                        "FailSubmission policy on Pending prior attempt"
                                            | None -> ()

                                            return Error e
                                        | Ok() ->
                                            // 6. Persist new state.
                                            let updated = updateSubmissionState submission transition.To
                                            let! saveResult = formStore.SaveSubmission(scopeId, updated)

                                            match saveResult with
                                            | Error err -> return Error err
                                            | Ok savedSubmission ->
                                                // 7. Emit transition audit.
                                                let payload: WorkflowTransitionedPayload = {
                                                    UserId = accessContext.UserId
                                                    FormId = savedSubmission.FormId
                                                    SubmissionId = savedSubmission.Id
                                                    WorkflowId = wfId
                                                    FromState = currentState
                                                    Event = event
                                                    ToState = transition.To
                                                }

                                                do! auditLog.Record(scopeId, AuditEvent.WorkflowTransitioned payload)

                                                // 8. Action invocation
                                                //    with ledger guard.
                                                match transition.Action with
                                                | None -> return Ok savedSubmission
                                                | Some actionName ->
                                                    match Map.tryFind actionName actions with
                                                    | None ->
                                                        warn (
                                                            sprintf
                                                                "Workflow action '%s' not found in registry; skipping"
                                                                actionName
                                                        )

                                                        return Ok savedSubmission
                                                    | Some action ->
                                                        let policy = resolvePolicy actionName

                                                        // Write Pending
                                                        // ledger entry
                                                        // BEFORE invoking
                                                        // — replay-safety
                                                        // hinges on this
                                                        // ordering.
                                                        let pendingEntry: ActionLedgerEntry = {
                                                            SubmissionId = savedSubmission.Id
                                                            TransitionId = transitionId
                                                            ActionName = actionName
                                                            Status = Pending
                                                        }

                                                        let! recordResult = ledger.Record pendingEntry

                                                        match recordResult with
                                                        | Error(LedgerError.StorageFailed s) ->
                                                            return
                                                                Error(
                                                                    FormError.StorageFailed(
                                                                        "Action ledger Record failed: " + s
                                                                    )
                                                                )
                                                        | Error LedgerError.EntryMissing ->
                                                            return
                                                                Error(
                                                                    FormError.StorageFailed
                                                                        "Action ledger Record returned EntryMissing"
                                                                )
                                                        | Ok() ->
                                                            let! invocation = async {
                                                                try
                                                                    do!
                                                                        action (
                                                                            buildContext savedSubmission accessContext
                                                                        )

                                                                    return Ok()
                                                                with ex ->
                                                                    return Error ex.Message
                                                            }

                                                            match invocation with
                                                            | Ok() ->
                                                                let! _ =
                                                                    ledger.MarkSucceeded(
                                                                        savedSubmission.Id,
                                                                        transitionId,
                                                                        actionName
                                                                    )

                                                                do!
                                                                    recordAuditAndMetric
                                                                        scopeId
                                                                        savedSubmission.Id
                                                                        transitionId
                                                                        actionName
                                                                        StatusSucceeded
                                                                        ""

                                                                return Ok savedSubmission
                                                            | Error exMessage ->
                                                                let! _ =
                                                                    ledger.MarkFailed(
                                                                        savedSubmission.Id,
                                                                        transitionId,
                                                                        actionName,
                                                                        exMessage
                                                                    )

                                                                do!
                                                                    recordAuditAndMetric
                                                                        scopeId
                                                                        savedSubmission.Id
                                                                        transitionId
                                                                        actionName
                                                                        StatusFailed
                                                                        exMessage

                                                                match policy with
                                                                | FailSubmission ->
                                                                    // Roll back the
                                                                    // state write.
                                                                    let! _ =
                                                                        formStore.SaveSubmission(scopeId, submission)

                                                                    return
                                                                        Error(
                                                                            FormError.ActionFailed(
                                                                                actionName,
                                                                                exMessage
                                                                            )
                                                                        )
                                                                | DeadLetter -> return Ok savedSubmission
                                                                | LogOnly ->
                                                                    warn (
                                                                        sprintf
                                                                            "Workflow action '%s' threw on transition %s for submission %s: %s"
                                                                            actionName
                                                                            transitionId
                                                                            savedSubmission.Id
                                                                            exMessage
                                                                    )

                                                                    return Ok savedSubmission
        }

        member _.ListPossibleTransitions(scopeId, submissionId) = async {
            let! submissionResult = formStore.GetSubmission(scopeId, submissionId)

            match submissionResult with
            | Error _ -> return []
            | Ok submission ->
                match submission.WorkflowId with
                | None -> return []
                | Some wfId ->
                    match Map.tryFind wfId workflows with
                    | None -> return []
                    | Some workflow ->
                        let currentState = resolveCurrentState submission
                        return WorkflowDefinition.listFromState currentState workflow
        }