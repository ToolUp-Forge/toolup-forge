module ToolUp.Scheduling.SchedulingEvents

open System
open ToolUp.Scheduling.SchedulingTypes

// ─── Phase 20 — Booking lifecycle event payloads ─────────────────
//
// `BookingScheduler` emits these to `IEventStore.Write` whenever a
// booking transitions state. Wire format:
//
//   ModuleEvent {
//     SourceModule = SchedulingEvents.SourceModule  // "_scheduling"
//     EventType    = SchedulingEvents.BookingCreated // case name
//     Payload      = JSON-serialised payload record
//   }
//
// The reserved `SourceModule = "_scheduling"` value (note: NOT
// `_platform.scheduling` — `_platform.*` is the core SDK's reserved
// namespace per CLAUDE.md). Callers query the trail with
// `IEventStore.ReadBySource(scopeId, SchedulingEvents.SourceModule)`.
//
// Payload records are plain F# records of primitives (no DUs, no
// options of DUs), so the default impl serialises with
// `System.Text.Json` — no external dep needed.

[<Literal>]
let SourceModule = "_scheduling"

[<Literal>]
let BookingCreated = "BookingCreated"

[<Literal>]
let BookingCancelled = "BookingCancelled"

[<Literal>]
let BookingRescheduled = "BookingRescheduled"

[<Literal>]
let BookingNoShow = "BookingNoShow"

type BookingCreatedPayload = {
    UserId: string
    BookingId: BookingId
    ResourceId: ResourceId
    StartUtc: DateTimeOffset
    EndUtc: DateTimeOffset
}

type BookingCancelledPayload = {
    UserId: string
    BookingId: BookingId
    Reason: string
}

type BookingRescheduledPayload = {
    UserId: string
    BookingId: BookingId
    OldStart: DateTimeOffset
    OldEnd: DateTimeOffset
    NewStart: DateTimeOffset
    NewEnd: DateTimeOffset
}

type BookingNoShowPayload = { UserId: string; BookingId: BookingId }