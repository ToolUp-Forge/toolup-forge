module ToolUp.Platform.AuditLogHealthCheck

open System
open ToolUp.Platform
open ToolUp.Platform.HealthChecks

// ─── Phase 6l.B — Audit chain durability probe ───────────────────────
//
// Proves the audit-event chain works end-to-end: write a marker via
// `IEventStore` (the underlying store `EventStoreAuditLog` writes
// through), read it back via `ReadBySource`, verify byte-for-byte. A
// `NoOpAuditLog` deployment is reported `Healthy` (the chain is
// configured off, not broken) — `AuditLogModeValidator` is the path
// that warns when off-in-authenticated-mode is itself a misconfig.
//
// Reading from `IEventStore` directly (bypassing `IAuditLog`) is
// deliberate: the probe is testing the durability of the underlying
// store, not the wrapping. `IAuditLog.GetAuditTrail` filters by
// `SourceModule = "_platform.audit"` and won't return our marker (we
// write under `"_platform.audit_health"` so the audit trail isn't
// polluted by probe noise).

let private probeSourceModule = "_platform.audit_health"
let private probeScopeId = "_platform"

/// Phase 6l.B — `IHealthCheck` that writes a marker through
/// `IEventStore`, reads it back, and verifies. Reports `Healthy` for
/// either a working `EventStoreAuditLog` (write+read round-trips) or
/// a `NoOpAuditLog` (configured off — not a fault). `Unhealthy` when
/// the underlying store fails the round-trip.
type AuditLogHealthCheck(auditLog: IAuditLog, eventStore: IEventStore, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IHealthCheck.defaultTimeout

    interface IHealthCheck with
        member _.Name = "audit-log"
        member _.Kind = Readiness
        member _.Timeout = timeout

        member _.Check() = async {
            // If the configured audit log is the no-op variant, the
            // deployment has explicitly disabled audit. The probe
            // returns Healthy — the validator (`AuditLogModeValidator`)
            // is the path that flags this as a config concern; the
            // probe's job is the durability check, not the policy
            // check.
            let isNoOp = auditLog.GetType().Name.Contains "NoOp"

            if isNoOp then
                return Healthy
            else
                // Write a marker event with a unique nonce + read
                // back via `ReadBySource`. The marker is keyed under
                // `_platform.audit_health` so it doesn't surface in
                // ordinary audit-trail queries (which filter to
                // `_platform.audit`).
                let nonce = Guid.NewGuid().ToString("N")

                let evt: ModuleEvent = {
                    Id = Guid.NewGuid()
                    OccurredAt = DateTime.UtcNow
                    ScopeId = probeScopeId
                    SourceModule = probeSourceModule
                    EventType = "AuditChainProbe"
                    Payload = nonce
                }

                try
                    do! eventStore.Write evt

                    let! readBack = eventStore.ReadBySource(probeScopeId, probeSourceModule)

                    let found = readBack |> List.exists (fun e -> e.Payload = nonce)

                    if found then
                        return Healthy
                    else
                        return
                            Unhealthy
                                "Wrote audit marker via IEventStore but ReadBySource didn't find it. Underlying store is misconfigured or non-durable."
                with ex ->
                    return Unhealthy(sprintf "Audit chain probe threw: %s" ex.Message)
        }