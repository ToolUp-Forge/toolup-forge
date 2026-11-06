# Phase 1251 — field classification is a seam

**What changes.** A module declares its classifications and forge gates them. Until now a `FieldClassification` list
governed nothing unless a host built an egress gate by hand and called it, and no door in forge called one. Now the
compose chain registers `IFieldClassifier`, `IEgressGate` and `ModuleClassificationRegistry`, and the doors to a model
consult them. See [`docs/platform/classification.md`](../platform/classification.md).

**Adoption.** One line on the declaring module:

```fsharp skip=fragment
ServerModule.create "Orders"
|> ServerModule.withClassifications Privacy.classifications
```

It replaces the hand-wired form, which you can delete along with any call to it:

```fsharp skip=fragment
let classifier = DefaultFieldClassifier.create Privacy.classifications
let gate = EgressGate.make classifier policy auditLog
```

If you passed your own policy, keep it with `ServerApp.withClassificationEgressPolicy policy`. A host's own registration
of either service still wins (`TryAdd`), so the hand-wired form keeps working.

**What a deployment sees.** The default policy keeps `Financial`, `Regulatory`, `Pii` and `Spi` fields from a model.
A tool result that carried one now arrives without it, under `withheldFields`. A deployment that **already registers an
`IFieldClassifier`**, for example for an evidence pack, gets the gate over it, and `_platform.ai.query_entity` applies it.
To keep the old behaviour there, compose `ServerApp.withClassificationEgressPolicy EgressGate.permissiveEgressPolicy`.

**Breaking, by this repo's convention.** `ServerModule` gains `Classifications` and `ServerApp` gains
`ModuleClassifications` and `ClassificationEgressPolicy`, so a full record literal of either adds them.
`ServerModule.create` and `ServerApp.empty` are unchanged to call.

**Verify.** Ask the assistant for a classified field, or run a test over `ServerApp.serviceConfig`: the result names
`<Entity>.<Field>` under `withheldFields`, and the audit trail holds an `EgressBlocked` row with boundary `AiToolResult`.

**Rollback.** Remove the `withClassifications` call. With no declaration and no host classifier nothing is registered.
