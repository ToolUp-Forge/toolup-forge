module ToolUp.Forms.Tests.InProcess.WorkflowEngineTests

open System
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.IEntityStore
open ToolUp.Forms.Workflow
open ToolUp.Forms.IFormStore
open ToolUp.Forms.IWorkflowEngine
open ToolUp.Forms.IActionLedger
open ToolUp.Forms.FormStore
open ToolUp.Forms.WorkflowEngine
open ToolUp.Forms.InMemoryActionLedger
open ToolUp.Forms.Tests.InProcess.InMemoryEntityStore
open ToolUp.Forms.Tests.InProcess.InMemoryAuditLog
open ToolUp.Forms.Tests.Contracts
open ToolUp.Forms.Tests.Contracts.IWorkflowEngineContract

/// Minimal `IServiceProvider` returning `null` for every type. The
/// engine constructor stores the provider for use by guards / actions
/// (Phase 1h); these tests don't exercise that path so an empty
/// provider keeps the suite hermetic without pulling in
/// Microsoft.Extensions.DependencyInjection.
type private EmptyServiceProvider() =
    interface IServiceProvider with
        member _.GetService(_serviceType: Type) = null

module private ServiceProviders =
    let empty: IServiceProvider = EmptyServiceProvider() :> IServiceProvider

/// Bind the IWorkflowEngine contract pack to the default
/// WorkflowEngine over the in-memory IEntityStore + IAuditLog stubs.
/// Workflow definition: New -> Submitted -> Approved (with
/// "always-deny" guard on reject) + side-effect-counter action on
/// approve.
let tests =
    let factory () =
        let entityStore = InMemoryEntityStore() :> IEntityStore
        let formStore = FormStore(entityStore) :> IFormStore
        let auditLog = InMemoryAuditLog()
        let counter = ref 0

        let workflow: WorkflowDefinition = {
            Id = "test-workflow"
            InitialState = "new"
            Transitions = [
                {
                    From = "new"
                    Event = "submit"
                    To = "submitted"
                    Guard = None
                    Action = None
                }
                {
                    From = "submitted"
                    Event = "approve"
                    To = "approved"
                    Guard = None
                    Action = Some "side-effect-counter"
                }
                {
                    From = "submitted"
                    Event = "reject"
                    To = "rejected"
                    Guard = Some "always-deny"
                    Action = None
                }
            ]
        }

        let workflows = Map [ workflow.Id, workflow ]

        let guards: Map<string, WorkflowGuard> =
            Map [ "always-deny", (fun _ctx -> async { return Error "always-deny rejected" }) ]

        let actions: Map<string, WorkflowAction> =
            Map [
                "side-effect-counter", (fun _ctx -> async { counter.Value <- counter.Value + 1 })
            ]

        let warn: WorkflowWarn = ignore
        let ledger = InMemoryActionLedger() :> IActionLedger
        let metricsSink = NoOpMetricsSink() :> IMetricsSink
        // Phase 21d — Phase 21 contract suite ran against the pre-21d
        // best-effort posture (action exceptions warn-logged and
        // swallowed). The contract suite does not exercise throwing
        // actions, so binding `LogOnly` here preserves byte-for-byte
        // Phase 21 behaviour for the existing tests; the new
        // durability suite (`WorkflowEngineDurabilityTests`) covers
        // every other policy explicitly.
        let actionPolicies: Map<string, ActionFailurePolicy> =
            Map [ "side-effect-counter", LogOnly ]

        let engine =
            WorkflowEngine(
                formStore,
                auditLog :> IAuditLog,
                ledger,
                metricsSink,
                warn,
                workflows,
                guards,
                actions,
                actionPolicies,
                ServiceProviders.empty
            )
            :> IWorkflowEngine

        let scope = "team-wf-" + Guid.NewGuid().ToString("N").Substring(0, 8)

        {
            Engine = engine
            FormStore = formStore
            AuditLog = auditLog
            ScopeId = scope
            WorkflowId = workflow.Id
            ActionCounter = counter
        }

    IWorkflowEngineContract.tests "WorkflowEngine (in-memory)" factory