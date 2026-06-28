# Phase 188 — Field-classification egress / DLP gate

**Status:** additive. Zero action required for an existing deployment — the
default policy is permissive, so boot path and wire are byte-for-byte
unchanged until a deployment opts in.

## What changed

Phase 41 shipped field *tagging* (`FieldClassification` / `ClassificationLevel`)
and a read-time gate (`ClassificationGate.redactFields`). Nothing enforced on
the tags at an **output boundary** — a classified field could leave the process
in an export, an RPC response, or an audit/log sink by a path that never went
through the read gate. Phase 188 closes that DLP gap with a terminal egress
gate that reuses the same policy shape.

New surface (all in `ToolUp.Platform`, server tier):

| Symbol | Kind | Notes |
|---|---|---|
| `EgressDecision` | DU (`[<RequireQualifiedAccess>]`) | `Allow` / `Redact` / `Block`. Qualified to avoid colliding with `ClassificationDecision.Allow`/`Redact`. |
| `EgressBoundary` | DU (`[<RequireQualifiedAccess>]`) | `ExportPayload` / `RpcResponse` / `AuditSink` / `LogSink` / `CustomBoundary of label`. |
| `EgressContext` | record | `{ Boundary; Actor; Destination }`. |
| `EgressPolicy` | type alias | `ClassificationLevel -> EgressContext -> EgressDecision`. |
| `IEgressGate` | interface | `Apply(ctx, entityName, fields) : Async<Map<string,string>>`. |
| `EgressGate.permissiveEgressPolicy` | value | The default — `Allow` for every level. |
| `EgressGate.make` / `apply` / `forExport` / `forResponse` | functions | Compose + binding points. |
| `EgressBlockedPayload` + `AuditEvent.EgressBlocked` | Core audit | Value-free deny row under `_platform.classification`. |

## Default behaviour (no action needed)

```fsharp
// Already byte-for-byte identical to "no gate": permissive default allows
// every level, so this never redacts and never audits.
let gate = EgressGate.make classifier EgressGate.permissiveEgressPolicy auditLog
```

A deployment that never constructs an `EgressGate` is wholly unaffected — the
new types are inert until composed.

## Opting in to enforcement

Supply an `EgressPolicy` with deny rules and thread the gate into your export
or RPC-response path:

```fsharp
// Block Pii / Spi from leaving on any export; allow everything else.
let dlpPolicy : EgressPolicy =
    fun level _ctx ->
        match level with
        | Pii | Spi -> EgressDecision.Block
        | Financial -> EgressDecision.Redact
        | _ -> EgressDecision.Allow

// Inside an IDataExporter, before emitting a segment's field view:
let! safe =
    EgressGate.forExport classifier dlpPolicy auditLog recipientId "Customer" fieldMap
// `safe` has Pii/Spi keys dropped and Financial values replaced with
// "[redacted]"; one EgressBlocked audit row per non-Allow decision.
```

- `Redact` → value replaced with `EgressGate.RedactedPlaceholder` (`"[redacted]"`).
- `Block` → key removed from the result map entirely.
- `Allow` → field passes through untouched, no audit.

Every `Redact` / `Block` emits exactly one `EgressBlocked` audit record
(`SourceModule = _platform.classification`) carrying actor, entity, field path,
level, decision, boundary, and optional destination — **value-free** (the field
value is never audited). `Allow` is never audited, so a permissive policy adds
zero audit noise.

## Verification

`dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj`
runs `IEgressGateContract` — permissive pass-through + zero audit, Block drops
the matching level, Redact substitutes the placeholder, one audit row per
decision, and boundary/destination fidelity.

## Rollback

Remove the `EgressGate` composition from the export / RPC path. The types are
additive and inert; no schema or wire migration is involved.
