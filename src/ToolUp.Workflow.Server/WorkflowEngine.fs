// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Workflow.Server

open ToolUp.Workflow

/// Coarse run status of a workflow instance.
type WorkflowStatus =
    | Running
    | Finished
    | Aborted

/// The advancing state of one workflow run. Immutable + passed in to
/// every engine call (no in-memory continuity), so a distributed runtime
/// can persist it between steps and resume on any node (GP 12 rule 4).
/// The real task work is the consumer's — the engine only computes which
/// nodes become runnable, which branch is taken, and the saga
/// compensation order on failure.
type WorkflowInstance = {
    /// Nodes that have completed (tasks done, gateways traversed).
    Completed: Set<NodeId>
    /// Nodes awaiting work: tasks ready to run, an `ExclusiveChoice`
    /// awaiting `choose`.
    Active: Set<NodeId>
    /// External triggers that have fired.
    FiredTriggers: Set<string>
    /// The branch label chosen at each `ExclusiveChoice` gateway.
    ChosenBranches: Map<NodeId, string>
    /// True once a task has failed.
    Failed: bool
    /// Completed *task* nodes in completion order (for reverse-order
    /// compensation).
    CompletionLog: NodeId list
    /// On failure, the compensable completed tasks in reverse-completion
    /// order — the saga rollback chain the consumer runs.
    CompensationOrder: NodeId list
}

/// Pure workflow advancement over a `WorkflowGraph`. Gateways are
/// traversed automatically (`ParallelSplit` fans out; `ParallelJoin`
/// waits for all incoming branches); tasks and `ExclusiveChoice` await
/// an explicit `complete` / `choose`; `AwaitTrigger` edges unlock on
/// `fireTrigger`; `fail` computes the compensation chain.
module WorkflowEngine =
    let private empty: WorkflowInstance = {
        Completed = Set.empty
        Active = Set.empty
        FiredTriggers = Set.empty
        ChosenBranches = Map.empty
        Failed = false
        CompletionLog = []
        CompensationOrder = []
    }

    let private guardSatisfied (inst: WorkflowInstance) (edge: WorkflowEdge) : bool =
        match edge.Guard with
        | Always -> true
        | Choice label -> inst.ChosenBranches.TryFind edge.From = Some label
        | AwaitTrigger t -> inst.FiredTriggers.Contains t

    /// Activate a target node, cascading through any gateway it lands on.
    let rec private activate (graph: WorkflowGraph) (inst: WorkflowInstance) (targetId: NodeId) : WorkflowInstance =
        if inst.Completed.Contains targetId || inst.Active.Contains targetId then
            inst
        else
            match WorkflowGraph.nodeKind graph targetId with
            | None -> inst
            | Some(TaskNode _)
            | Some(GatewayNode ExclusiveChoice) ->
                // Awaits an explicit complete / choose.
                {
                    inst with
                        Active = inst.Active.Add targetId
                }
            | Some EndNode -> {
                inst with
                    Completed = inst.Completed.Add targetId
              }
            | Some StartNode
            | Some(GatewayNode ParallelSplit) ->
                // Auto-traverse: complete + fan out every satisfied edge.
                cascade
                    graph
                    {
                        inst with
                            Completed = inst.Completed.Add targetId
                    }
                    targetId
            | Some(GatewayNode ParallelJoin) ->
                let preds = WorkflowGraph.incoming graph targetId |> List.map _.From

                if preds |> List.forall inst.Completed.Contains then
                    cascade
                        graph
                        {
                            inst with
                                Completed = inst.Completed.Add targetId
                        }
                        targetId
                else
                    inst // wait for the remaining branches

    and private cascade (graph: WorkflowGraph) (inst: WorkflowInstance) (fromId: NodeId) : WorkflowInstance =
        WorkflowGraph.outgoing graph fromId
        |> List.filter (guardSatisfied inst)
        |> List.fold (fun acc edge -> activate graph acc edge.To) inst

    /// Begin a run: traverse from the graph's `Start` node.
    let start (graph: WorkflowGraph) : WorkflowInstance =
        match WorkflowGraph.startNode graph with
        | None -> empty
        | Some s -> activate graph empty s

    /// Complete an active task, fanning out to its successors. No-op if
    /// the node is not an active task.
    let complete (graph: WorkflowGraph) (inst: WorkflowInstance) (nodeId: NodeId) : WorkflowInstance =
        if not (inst.Active.Contains nodeId) then
            inst
        else
            match WorkflowGraph.nodeKind graph nodeId with
            | Some(TaskNode _) ->
                cascade
                    graph
                    {
                        inst with
                            Completed = inst.Completed.Add nodeId
                            Active = inst.Active.Remove nodeId
                            CompletionLog = inst.CompletionLog @ [ nodeId ]
                    }
                    nodeId
            | _ -> inst

    /// Pick a branch at an active `ExclusiveChoice` gateway, activating
    /// only the chosen outgoing edge.
    let choose (graph: WorkflowGraph) (inst: WorkflowInstance) (gatewayId: NodeId) (label: string) : WorkflowInstance =
        if not (inst.Active.Contains gatewayId) then
            inst
        else
            match WorkflowGraph.nodeKind graph gatewayId with
            | Some(GatewayNode ExclusiveChoice) ->
                cascade
                    graph
                    {
                        inst with
                            ChosenBranches = inst.ChosenBranches.Add(gatewayId, label)
                            Completed = inst.Completed.Add gatewayId
                            Active = inst.Active.Remove gatewayId
                    }
                    gatewayId
            | _ -> inst

    /// Fire an external trigger, unlocking any `AwaitTrigger` edges whose
    /// source has already completed.
    let fireTrigger (graph: WorkflowGraph) (inst: WorkflowInstance) (triggerName: string) : WorkflowInstance =
        let inst' = {
            inst with
                FiredTriggers = inst.FiredTriggers.Add triggerName
        }

        inst'.Completed |> Set.fold (fun acc nid -> cascade graph acc nid) inst'

    /// Fail a task: mark the run aborted and compute the saga
    /// compensation chain — completed compensable tasks in
    /// reverse-completion order.
    let fail (graph: WorkflowGraph) (inst: WorkflowInstance) (nodeId: NodeId) : WorkflowInstance =
        let compensable =
            inst.CompletionLog
            |> List.rev
            |> List.filter (fun nid ->
                match WorkflowGraph.nodeKind graph nid with
                | Some(TaskNode true) -> true
                | _ -> false)

        {
            inst with
                Failed = true
                Active = inst.Active.Remove nodeId
                CompensationOrder = compensable
        }

    /// Coarse run status. `Finished` once an `End` node is reached (or no
    /// work remains); `Aborted` after a failure.
    let status (graph: WorkflowGraph) (inst: WorkflowInstance) : WorkflowStatus =
        if inst.Failed then
            Aborted
        else
            let ends = graph.Nodes |> List.filter (fun n -> n.Kind = EndNode) |> List.map _.Id

            if ends |> List.exists inst.Completed.Contains then Finished
            elif Set.isEmpty inst.Active then Finished
            else Running