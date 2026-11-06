# Field classification

A module says which of its entity fields are sensitive. The compose chain does the rest: it registers a classifier
over every module's declarations and an egress gate over the classifier, and the doors that carry entity data to a
model consult that gate. A deployment writes nothing beyond the declaration.

```fsharp skip=fragment
ServerModule.create "Orders"
|> ServerModule.withClassifications [
    FieldClassification.create "Order" "CustomerEmail" Pii
    FieldClassification.create "Order" "Total" Financial
]
```

`withClassifications` **adds**: a generated declaration and an authored one can both call it. Two modules may repeat a
declaration. Two declarations that differ on one field are refused at compose, naming the field, because resolving
them by composition order could weaken a classification.

## What the compose chain registers

| Service | What it is | When |
|---|---|---|
| `IFieldClassifier` | `DefaultFieldClassifier` over every module's declarations | a module declared something; `TryAdd`, so a host's own wins |
| `IEgressGate` | `EgressGate.make` over the classifier that stands, the deployment's policy and `IAuditLog` | a classifier stands, whichever side supplied it; `TryAdd` |
| `ModuleClassificationRegistry` | which module declared which entities | a module declared something |

A composition with neither a declaration nor a host classifier registers none of them, and every door below is the
executor's own text, byte for byte.

## The policy

`EgressGate.declaredDefaultPolicy` applies unless the deployment names another. A sensitive field
(`ClassificationLevel.isSensitive`: `Financial`, `Regulatory`, `Pii`, `Spi`) does not reach a model, and every other
crossing is allowed. `permissiveEgressPolicy` is not the default here, because a declaration that governs nothing is
what the seam exists to prevent. A deployment that wants something else replaces the policy whole:

```fsharp skip=fragment
app
|> ServerApp.withClassificationEgressPolicy (fun level crossing ->
    match level, crossing.Boundary with
    | Financial, boundary when boundary = EgressBoundary.aiToolResult -> EgressDecision.Allow
    | _ -> EgressGate.declaredDefaultPolicy level crossing)
```

## The doors

Each of these crosses `EgressBoundary.aiToolResult`, with the caller as `Actor` and the tool as `Destination`.

| Door | Entities consulted |
|---|---|
| `_platform.ai.query_entity` | the entity asked for, by name. A predicate over a refused field is refused before the read: a filter answers questions about the value. |
| `_platform.ai.query_module` | those the asked module declared |
| `_platform.ai.get_latest_result` | those the asked module declared. The document is gated before the character cap, since a cut document does not parse. |
| a module's AI tools, server-resident, from the agent loop and the MCP host | those the tool's `SourceModule` declared |
| a module's client-resident AI tools, on what the browser posts | those the tool's `SourceModule` declared |

Only `query_entity` knows which entity a row is. The others read every object in the result as a possible row of each
declared entity, matching property paths against field paths and ignoring case, `_` and `-`, so a tool's `order_id`
is the declared `OrderId`. A dotted field path follows nested objects from the object being read.

A refused field is named beside the result, and the refusal is an `EgressBlocked` audit row per crossing:

```json
{
  "withheldFields": ["Order.CustomerEmail"],
  "withheldReason": "This deployment's classification policy keeps these fields from the model. Asking again returns the same; say that they were withheld.",
  "result": { "Id": "o1", "Total": 12 }
}
```

`Block` removes the property. `Redact` replaces its value with `[redacted]`, whatever the value's JSON type.

## What it does not gate

**A module's own routes.** A route's response is the module's own typed shape, read by its own client. The gate
cannot drop a field from it, because the client's decoder requires the field, and it cannot replace a number with a
placeholder for the same reason. A route that must narrow a field by caller does so in its handler, where it holds the
type, by resolving `IEgressGate` or `ClassificationGate.redactFields`.

**The entity store.** `IEntityStore` is not decorated. A read through it may be a job, a render or the first half of a
read-modify-write, and a placeholder read there would be saved back as the value.

**Free text.** A result that does not parse as JSON comes back unchanged, and so does a value written into a string.
This is a gate over fields, not content inspection.

**Attached parts.** `createAnsweringTool`'s parts go to the user's message rather than the model, so only the text is
gated.

## Hand wiring

A host that registers `IFieldClassifier` or `IEgressGate` keeps it, because the seam adds with `TryAdd` after the host's
registrations. A host classifier with no module declaration still gets the gate and the `query_entity` door, but the
module doors have no declared entities to read until a module names them. `EgressGate.make`, `forExport` and
`forResponse` remain the binding points for a boundary of a deployment's own.
