module ToolUp.Forms.Tests.InProcess.InMemoryAuditLog

open System
open System.Collections.Concurrent
open ToolUp.Platform

// ─── Test-only in-memory IAuditLog stub ──────────────────────────
//
// Records every audit event in an exposed list so tests can assert
// on emission. Keyed only by scopeId for the tests we run; the real
// IAuditLog impl is in ToolUp.Platform and exercised by Phase 9
// tests directly.

type InMemoryAuditLog() =
    let events = ConcurrentBag<string * AuditEvent>()

    /// Snapshot of every event recorded under any scope, in
    /// undefined order (ConcurrentBag).
    member _.AllEvents: (string * AuditEvent) list = events |> List.ofSeq

    /// Snapshot scoped to one scopeId.
    member this.EventsForScope(scopeId: string) : AuditEvent list =
        this.AllEvents
        |> List.choose (fun (s, e) -> if s = scopeId then Some e else None)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { events.Add(scopeId, audit) }

        member this.GetAuditTrail(scopeId, _, eventTypeFilter) = async {
            return
                this.EventsForScope scopeId
                |> List.filter (fun e ->
                    match eventTypeFilter with
                    | None -> true
                    | Some t -> AuditEvent.eventTypeName e = t)
        }