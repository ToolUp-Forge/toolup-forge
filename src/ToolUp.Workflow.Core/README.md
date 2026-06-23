# ToolUp.Workflow.Core

A **BPMN-shaped** workflow graph model — the graph/saga complement to `ToolUp.Forms`' linear
state-machine workflow. Use this companion when a flow needs the ~20% `ToolUp.Forms` doesn't
cover: **parallel split/join**, **exclusive choice**, **compensating (saga) tasks**, and
**externally-triggered transitions**. Definitions are immutable (GP 5); pure F#.

`ToolUp.Forms`' `WorkflowDefinition` is unchanged — reach for this only when you need gateways
or compensation.

## What's here

- **`WorkflowGraph`** — `Nodes` + `Edges`.
- **`NodeKind`** — `StartNode` / `EndNode` / `TaskNode of hasCompensation` / `GatewayNode of GatewayKind`.
- **`GatewayKind`** — `ParallelSplit` (AND-split) / `ParallelJoin` (AND-join) / `ExclusiveChoice` (XOR).
- **`EdgeGuard`** — `Always` / `Choice of label` / `AwaitTrigger of triggerName`.

The advancing engine (`WorkflowEngine` — `start` / `complete` / `choose` / `fireTrigger` / `fail` /
`status`) lives in [`ToolUp.Workflow.Server`](../ToolUp.Workflow.Server/README.md).
