# `aggregate_metric`, and the fact tools' results in the answer gate's scope

**Ships in:** ToolUp.Facts.Core, ToolUp.Facts.Server, ToolUp.AI.Core and ToolUp.AI.Server 0.38.0. Two records gain a
field, which breaks a full record literal of either; everything else is additive.

**Affected:** code that builds an `AnswerVerifier.ScopedFact` or a `VerifiedNumber` with a full record literal adds one
field. A test that pins the fact store's tool names adds `aggregate_metric`. A deployment that composes the fact store
gains the tool with no change, and one that composes the numeric-fidelity gate verifies figures quoted from fact-tool
results with no change.

## What changes

1. **`aggregate_metric`** (`AggregateMetricTool`, in `ToolUp.Facts.Server`) sums one metric by group, on the server,
   from facts the disclosure gate admitted at `FactToolResult`. It is the fifth entry of `NarrativeGrounding.factTools`,
   so `FactsCompose.withFactStore` declares it and the grounded narrative offers it. Only a metric whose `RollUp` is
   declared `Additive` is summed; a group with a withheld member is reported withheld, never summed. The full
   behaviour is in [`docs/platform/facts.md`](../platform/facts.md#the-roll-up-a-total-the-answer-gate-can-check).
2. **`ToolUp.Facts.Core`** gains the client-renderable types: `AggregateQuery`, `AggregateCeiling`, `AggregateInput`,
   `AggregateTotal`, `AggregateGroup`, `AggregateGroupRefusal`, `AggregateResult`, `AggregateRefusal` and the
   `AggregateRefusal.describe` / `describeGroup` wordings.
3. **The answer gate's scope includes the turn's fact-tool results.** `AIAssistantHandler` reads every result, in the
   turn, of a tool declaring `ReadFacts` through the new `AnswerVerifier.toolResultFacts`, and verifies the answer
   through the new `AnswerVerifier.runVerificationStageWithScope` over those facts and the retrieved ones. A figure
   the model quoted from `query_facts` or `query_metric_population` used to read as unmatched (or as
   `NoFactsInScope`) and was withheld under `Strict`; it now verifies. `runVerificationStageWithJoin` keeps its
   signature and runs over the retrieved facts alone, as before.
4. **`AnswerVerifier.ScopedFact` gains `Inputs: string list`** — empty for a fact; for a derived value, the ids of the
   facts it was computed from. A derived value verifies a number only when every input is a fact in the same scope,
   the metric is declared `Additive`, and the number equals it at its apparent precision within the declared
   tolerance.
5. **`VerifiedNumber` (`ToolUp.AI.Core`) gains `DerivedFrom: string list option`** — `Some` the input fact ids when
   the token verified against a derived value, `None` otherwise. An option, so a message persisted before the field
   existed reads back as `None`. The Phase 680 audit row's `CitedFactIds` includes the inputs.

## Migration

```fsharp skip=fragment
// A ScopedFact literal:
{ FactId = id; Rendering = rendering; Metric = metric; Inputs = [] }

// A VerifiedNumber literal:
{ Token = t; Canonical = c; Verdict = v; MatchedFactId = m; DerivedFrom = None }
```

A custom `IAnswerVerifier` keeps its signature. To honour derived values it reads `ScopedFact.Inputs`; one that
ignores them treats a derived entry as a fact with an empty id, which the default verifier never does.

## Verification

`dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "aggregate_metric"`
runs the tool's tests, including a chat turn whose quoted total stays under `Strict`;
`--filter-test-list "Phase 523 numeric-fidelity"` runs the derived-value match.

## Rollback

Pin 0.37.0. The one new persisted shape is `VerifiedNumber.DerivedFrom` on a conversation message's verdict; a
message written under 0.38.0 carries that extra member when 0.37.0 reads it back.
