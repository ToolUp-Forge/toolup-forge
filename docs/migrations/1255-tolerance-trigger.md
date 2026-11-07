# 1255 — A grounded narrative can be triggered by a fact moving beyond a tolerance

**Who:** a module that enqueues its own change narrative after a delivery, comparing figures and scheduling a job of
`GroundedNarrativeTrigger.HandlerName`; and an evaluator that reduces a composition. Nothing changes until you opt in.

**What changes:** `FactsCompose.withToleranceTriggers` takes the standing question as data. A unit of work that moved
the table asks it, and the run is enqueued when a selected subject moved beyond the tolerance. `groundedNarrative` gains
the optional `triggers` member at `server-app-host` 7.3, so the composition's record shows each trigger. The change set
names each move and its two facts, and the run records its trigger as `tolerance`.

## The composition

Add the member to the narrative (§P3.4 of the profile). A 7.2 composition without it encodes byte for byte as before.

```diff
   "onCommit": [],
+  "triggers": [
+    {
+      "table": "sensor.reading",
+      "metric": "temperature",
+      "comparison": "previousDelivery",
+      "tolerance": "relative:0.1"
+    }
+  ],
   "id": "reading-change",
```

A trigger written wrongly, or naming a table or metric the composition does not declare, is `narrative-malformed`
(§P4.24), and so is an empty list or one trigger written twice. A unit of work enqueues the run at most once, however
many of its triggers fire.

## The composition root

An evaluator reduces each entry to one `ToleranceTrigger`. Written by hand, it is one line after the writer:

```diff
 app
 |> FactsCompose.withFactTableWriter
+|> FactsCompose.withToleranceTriggers [
+    {
+        RunKey = "reading-change"
+        Table = "sensor.reading"
+        Metric = "temperature"
+        Subjects = None
+        Comparison = ToleranceComparison.PreviousDelivery
+        Tolerance = Tolerance.Relative 0.10m
+    }
+]
```

## The module

Delete the hand-scheduled change narrative: the comparison over `IFactStore.Query`, the job registration and its
trigger, and the completed-delivery hook if it did nothing else.

```diff
-let withChangeNarrative (m: ServerModule) : ServerModule =
-    m |> ServerModule.withDeliveryCompleted ("change-narrative", onDelivery)
```

Two meanings to check against what you delete. A move exactly at the tolerance is not beyond it. Under a relative
tolerance, any move from a compared figure of zero is beyond it, and zero to zero is not.

## Verify

Deliver two periods with a figure past the tolerance: one job runs, and its payload's `InvalidatedInputs` holds a
`move:<table>:<metric>:<subject>:<from fact>-><to fact>` entry. Deliver the same file again: no job. A table or metric
no module declares fails the `fact-tolerance-triggers` preflight at startup. Requires a composed `IJobScheduler`;
without one each move is a Warning naming the run and the table.

## Rollback

Remove the compose line and the member, and restore the module's own scheduling.
