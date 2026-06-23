// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Workflow

// ─── BPMN-shaped workflow graph — Phase 243 ──────────────────────────
//
// The graph/saga companion that complements `ToolUp.Forms`'s linear
// state-machine workflow (the ~80%): parallel split/join, exclusive
// choice, compensating transactions (saga rollback), and externally-
// triggered transitions (the ~20%). Definitions are immutable (GP 5);
// the engine (ToolUp.Workflow.Server) advances them with all state
// passed in, so a distributed runtime can host it (GP 12 rule 4).
//
// `ToolUp.Forms`'s `WorkflowDefinition` is unchanged — a deployment
// reaches for this companion only when it needs gateways/compensation.

type NodeId = string

/// Branching/merging gateway kinds.
type GatewayKind =
    /// Activates every outgoing branch (AND-split).
    | ParallelSplit
    /// Activates its single successor only once EVERY incoming branch
    /// has completed (AND-join).
    | ParallelJoin
    /// Activates exactly one outgoing branch — the one whose edge label
    /// matches the caller's choice (XOR-split).
    | ExclusiveChoice

type NodeKind =
    | StartNode
    | EndNode
    /// A unit of work. `HasCompensation` marks it for saga rollback —
    /// on a downstream failure it is added to the compensation chain.
    | TaskNode of hasCompensation: bool
    | GatewayNode of GatewayKind

type WorkflowNode = { Id: NodeId; Kind: NodeKind }

/// Condition under which an edge may be followed.
type EdgeGuard =
    /// Always followable once the source completes.
    | Always
    /// Followable only when an `ExclusiveChoice` source picked this
    /// branch label.
    | Choice of label: string
    /// Followable only once the named external trigger has fired
    /// (the source must also have completed).
    | AwaitTrigger of triggerName: string

type WorkflowEdge = {
    From: NodeId
    To: NodeId
    Guard: EdgeGuard
}

/// An immutable workflow graph. Validity (single Start, edges reference
/// real nodes, joins reachable) is the author's responsibility; the
/// engine trusts a well-formed graph.
type WorkflowGraph = {
    Nodes: WorkflowNode list
    Edges: WorkflowEdge list
}

module WorkflowGraph =
    let nodeKind (graph: WorkflowGraph) (id: NodeId) : NodeKind option =
        graph.Nodes |> List.tryPick (fun n -> if n.Id = id then Some n.Kind else None)

    let outgoing (graph: WorkflowGraph) (id: NodeId) : WorkflowEdge list =
        graph.Edges |> List.filter (fun e -> e.From = id)

    let incoming (graph: WorkflowGraph) (id: NodeId) : WorkflowEdge list =
        graph.Edges |> List.filter (fun e -> e.To = id)

    let startNode (graph: WorkflowGraph) : NodeId option =
        graph.Nodes
        |> List.tryPick (fun n -> if n.Kind = StartNode then Some n.Id else None)