# Migration 272 — hosted-tree action audit emission (`IHostActionAuditHook`, GP 6)

**Status:** additive, opt-in — **no runtime surface change unless composed; no consumer action required.**

## What changes

Phase 113's `IActionAuthorizer` *authorizes* a hosted tree's `Dispatch` / `Call` / `Navigate` /
`Invoke`, but GP 6 mandates "audit everything that changes state" — and an authorized hosted action
left no audit trail. For the regulated / Sovereign buyers in the Vision (provenance is the moat), an
action a user drove through a hosted UI must be traceable. This phase makes every authorized (and
denied) hosted action auditable.

- **New audit event** `HostActionDispatched` (case on the `AuditEvent` DU in
  `ToolUp.Platform.Core/Shared/AuditTypes.fs`) with `HostActionDispatchedPayload`
  (`SubjectKind` / `SubjectId` / `ActionKind` / `ActionTarget` / `ScopeId` / `Allowed` / `Reason` /
  `OccurredAt`). Registered in the Phase 114 codec registry (`AuditLog.auditEventCodecs`), so it is
  **exhaustiveness-tested + externally replicated from birth** — the reflection-based
  `AuditEventRegistryTests` gate covers it automatically.
- **New hook** `IHostActionAuditHook` + default `HostActionAuditHook` (in
  `ToolUp.Platform.Server/Server/Scope/HostActionAuditHook.fs`): emits one `HostActionDispatched` row
  per action decision through the shipped `IAuditLog`, under the action's own scope (GP 4). PII-free
  beyond `SubjectId` (same envelope as the Phase 120 `AuthorizationDenied` row). A **denied** action
  audits the denial (`Allowed = false` + the reason) — the security-relevant case. Best-effort: an
  audit-write failure is logged at `Warn` and swallowed, never failing the primary action.
- **`authorizeAndAudit`** — the authorize-then-audit one-path helper: authorizes the action through
  the Phase 113 authorizer, records the decision (allow OR deny), returns the decision unchanged.
- **`disabled`** — the no-op absent-sink hook; `authorizeAndAudit` with it is a byte-for-byte
  passthrough of `authorizer.Authorize` (GP 13).

Keyed on the neutral `ActionDescriptor` — no tree-language type appears (open-core boundary).

## How to adopt (opt-in)

Compose the hook over the deployment's `IAuditLog` beside the authorizer, and route host-action
decisions through `authorizeAndAudit`:

```fsharp
let hook = HostActionAuditHook(auditLog, logger) :> IHostActionAuditHook   // or `disabled` if no sink

let! decision =
    HostActionAuditHook.authorizeAndAudit actionAuthorizer hook principal descriptor ctx
// decision is returned as usual; the allow/deny is now on the audit trail (GP 6).
```

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "HostActionAudit"
# The Phase 114 exhaustiveness gate covers the new case automatically:
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "audit-event registry exhaustiveness"
```

## Rollback

Remove the `HostActionDispatched` case + `HostActionDispatchedPayload` from `AuditTypes.fs`
(DU case, `eventTypeName` arm) and its codec from `AuditLog.fs`, delete
`Server/Scope/HostActionAuditHook.fs` + its `<Compile>` entry, delete `InProcess/HostActionAuditTests.fs`
+ its `<Compile>` and `Program.fs` registration. No runtime impact on any deployment that never
composed the hook.

## SDK adoption

⛔ **N-A / additive-opt-in across all consumers** — a new opt-in hosted-tree audit hook + audit-event
case. No current matrix consumer hosts a typed-tree UI; a deployment that composes no hook is
byte-for-byte unchanged (GP 11/13). A consumer with an exhaustive wildcard-free `AuditEvent` match
would add a `HostActionDispatched` branch (additive).
