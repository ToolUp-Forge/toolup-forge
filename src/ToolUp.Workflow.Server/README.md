# ToolUp.Workflow.Server

The advancing **engine** for [`ToolUp.Workflow.Core`](../ToolUp.Workflow.Core/README.md) graphs.
Pure F#, stateless between calls — the `WorkflowInstance` is passed in and returned, so a
distributed runtime can persist it between steps and resume on any node (GP 12 rule 4). The
engine computes *which nodes become runnable*, *which branch is taken*, and the *saga
compensation order* on failure; the actual task work is the consumer's.

## Operations (`WorkflowEngine`)

- **`start graph`** — traverse from `Start`.
- **`complete graph inst nodeId`** — finish an active task, fan out to successors.
- **`choose graph inst gatewayId label`** — pick a branch at an `ExclusiveChoice`.
- **`fireTrigger graph inst name`** — unlock `AwaitTrigger` edges.
- **`fail graph inst nodeId`** — abort + compute the compensation chain (compensable completed
  tasks, reverse order).
- **`status graph inst`** — `Running` / `Finished` / `Aborted`.

`ParallelSplit` fans out automatically; `ParallelJoin` activates its successor only once every
incoming branch has completed.
