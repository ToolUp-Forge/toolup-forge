# Phase 69h.tail — `[<Audit>]` annotation sweep + dispatcher-emitted audit (consumer migration)

**What changes.** Compliance-sensitive methods opt into **dispatcher-emitted** audit via `[<Audit "kind">]` on the API record field, replacing inside-handler `IAuditLog.Write` calls. The dispatcher emits one uniform-shape row after each successful invocation — subject id, correlation id, configured kind, and a **PII-redacted** snapshot of the input record (`[<PiiSafe>]` whitelists fields; everything else renders `<redacted:TypeName>` — forgetting the attribute keeps PII *out* of audit rows, fail-safe). `Api.make` bridges the emission to the DI-registered `IAuditLog` as `RemotingMethodAudited` rows under the request's config scope. Idempotency replays never double-audit: the replay early-return precedes the audit-emitting Success branch.

**Scope.** Additive, off-per-method by default (GP 11): a record with no `[<Audit>]` annotations behaves byte-for-byte as before. Every compliance-sensitive forge SDK method ships annotated.

**Optional compliance gate.** `TOOLUP_AUDIT_ADMIN_REQUIRED=true` composes the admin-must-be-audited startup gate: any method carrying `[<RequiresRole>]` (Phase 69d) without an `[<Audit>]` annotation refuses startup, naming the record + method. Off by default.

## Diff to apply

```fsharp
open ToolUp.Platform // tier-shared mirrors, Fable-safe

type GrantInput = {
    [<PiiSafe>]               // whitelisted — appears in the audit row
    SubjectUserId: string
    Justification: string     // unmarked — rendered <redacted:String>
}

type MyAdminApi = {
    [<RequiresRole "Admin">]
    [<Audit "PermissionGranted">]   // well-known kind, or "Custom:<name>"
    Grant: GrantInput -> Async<unit>
}
```

Then **delete the inside-handler `IAuditLog.Write` call** for that method — the attribute is the emission now (keeping both double-audits).

Well-known kinds: `MoneyMoved`, `PolicyChanged`, `PiiAccessed`, `DataExported`, `PermissionGranted`, `PermissionRevoked`, `TenantCreated`, `TenantDeleted`; anything else via `"Custom:<name>"`. The string encoding exists because F# attributes can't take DU values.

## Verification

1. Invoke an annotated method with curl, then inspect the audit tail: one `RemotingMethodAudited` row with the configured kind, your subject id, and the redacted payload.
2. Replay the same call with the same idempotency key: no second row.
3. Set `TOOLUP_AUDIT_ADMIN_REQUIRED=true` with one role-gated, unaudited method: startup must refuse.
4. Contract pack: `InProcess/AuditTests.fs` in `ToolUp.Platform.Tests` (kind decoding both families, PII redaction, gate on/off, the replay-precedes-emission structural pin, and the `IAuditLog` bridge round-trip).

## Rollback

Remove the `[<Audit>]` annotations (and restore handler-internal writes if you deleted them), or revert forge commit `986584d`. Already-written `RemotingMethodAudited` rows are ordinary audit events and need no cleanup.
