# ToolUp.Scheduling — Technical Guide

Internals, design decisions, and the deferred set for the Phase 20 Scheduling Companion. Read [`README.md`](README.md) first for the overview + how-to-enable.

## Entity layout

Three entity types are registered via `ServerApp.withEntity` at compose time. Each rides Phase 19's `IEntityStore` for typed persistence + indexed lookup.

| Entity type | Indexes | Key shape |
|---|---|---|
| `BookableResource` | `ResourceType` | `Id: ResourceId` (caller-supplied string) |
| `Booking` | `ResourceId`, `Status`, compound `(ResourceId, StartUtc-ticks-as-string)` | `Id: BookingId` (caller-supplied string) |
| `AvailabilityException` | `ResourceId` | `Id: string` (caller-supplied) |

The compound `(ResourceId, StartUtc)` index is registered for future range-query optimisation. Today `ListBookings` uses the simple `ResourceId` index then filters in memory by date — acceptable for typical resource sizes (< 10k bookings). Phase 19a's range predicates (`Gt` / `Lt`) on the compound key are a future optimisation; currently the compound index matches only `And`-of-`Eq` predicates.

## Conflict-detection algorithm

`BookingConflictDetector.detect` is pure. The caller (default `BookingScheduler`) pre-fetches inputs and passes them in:

```fsharp
type DetectorInputs = {
    Resource: BookableResource
    Bookings: Booking list      // pre-filtered to the resource + intersect window
    Exceptions: AvailabilityException list  // pre-filtered to the booking's local date
}
```

Checks run in order and accumulate every reason (no short-circuit — UI shows all conflicts at once):

1. **Overlapping bookings.** Iterate `Bookings`. Skip the proposed booking's own `Id` (so `Reschedule` doesn't conflict with itself). Skip `Cancelled` and `NoShow` (invisible to scheduling). UTC-range intersect emits `OverlappingBooking existingId`.
2. **Local-time conversion.** Resource timezone resolved via `TimeZoneInfo.FindSystemTimeZoneById`. Bad TZID → UTC fallback (no exception, benign drift).
3. **Availability exceptions on the local date.** `FullDay` always emits `ResourceUnavailable`. `PartialBlock` whose local-time window overlaps the booking's local-time window emits `ResourceUnavailable`. `ExtendedHours` adds availability — never produces a conflict by itself.
4. **Default availability + ExtendedHours.** Filter the resource's `DefaultAvailability` list by `DayOfWeek` (matches local day) and `EffectiveFrom`/`EffectiveTo` (date is in range). Add any `ExtendedHours` exceptions on the local date. The booking's local-time window must fit fully inside at least one resulting window — otherwise emit `OutsideAvailability (closestWindow)` for diagnostic display.

## Recurrence expansion

`RecurrenceExpander.expand` returns concrete `Booking` occurrences cloned from a seed, with `StartUtc` / `EndUtc` shifted. Termination order: whichever fires first of `rule.Until` (exclusive), `rule.Count` (total including seed), `window.End` (caller bound), or the **hard cap of 10,000 occurrences**.

| Frequency | Step |
|---|---|
| `Daily` | `+Interval` days |
| `Weekly`, `ByWeekday = []` | `+(7 * Interval)` days |
| `Weekly`, `ByWeekday = [...]` | walk day-by-day; emit when `(weekIndex % Interval = 0) && (dayOfWeek ∈ ByWeekday)` |
| `Monthly` | `DateTimeOffset.AddMonths Interval` |
| `Yearly` | `DateTimeOffset.AddYears Interval` |

UTC arithmetic preserves time-of-day across DST boundaries. `Monthly` on Jan 31 → Feb 28/29 follows BCL `AddMonths` semantics.

## iCalendar coverage matrix

Parser supports the **VEVENT subset**:

| Property | Parse | Emit | Notes |
|---|---|---|---|
| `VERSION` | ✓ | always `2.0` | |
| `PRODID` | ✓ | preserved on round-trip | `CanonicalProdId = "-//ToolUp//Scheduling 1.0//EN"` for new emissions |
| `CALSCALE` | parsed/ignored | always `GREGORIAN` | |
| `METHOD` | parsed/ignored | not emitted | |
| `UID` | required | required | |
| `SUMMARY` | ✓ | ✓ | |
| `DESCRIPTION` | ✓ | ✓ | |
| `DTSTART` | ✓ (UTC / floating+TZID / date-only) | UTC form | floating-with-TZID lifted to UTC via `TimeZoneInfo.FindSystemTimeZoneById` |
| `DTEND` | same as DTSTART | UTC form | |
| `DTSTAMP` | ✓ | ✓ | falls back to `DtStart` if absent |
| `RRULE.FREQ` | DAILY / WEEKLY / MONTHLY / YEARLY | same | unsupported FREQ → parse `Error` |
| `RRULE.INTERVAL` | ✓ | omitted when `1` | |
| `RRULE.BYDAY` | MO / TU / WE / TH / FR / SA / SU | same | positional offsets (`1MO`, `-1FR`) stripped on parse, ignored |
| `RRULE.UNTIL` | ✓ | ✓ | |
| `RRULE.COUNT` | ✓ | ✓ | |
| Anything else | silently dropped | not emitted | LOCATION / ATTENDEE / ORGANIZER / X-* |

Line discipline:
- Parser unfolds CRLF or LF + leading-whitespace continuations per RFC 5545 §3.1.
- Emitter folds at 75 octets, continuation lines start with `\r\n `.

## Audit emission

Every state-changing call writes one `ModuleEvent` to `IEventStore` under `SourceModule = "_scheduling"` (literal in `SchedulingEvents.SourceModule`). Failures in the event-store write surface as exceptions — there's no swallow-and-continue because the audit trail is part of the contract.

| Method | EventType | Payload type |
|---|---|---|
| `Book` (success path) | `BookingCreated` | `BookingCreatedPayload` |
| `Cancel` (first transition only — idempotent) | `BookingCancelled` | `BookingCancelledPayload` |
| `Reschedule` (success path) | `BookingRescheduled` | `BookingRescheduledPayload` |
| `MarkNoShow` (first transition only — idempotent) | `BookingNoShow` | `BookingNoShowPayload` |

Payloads are plain F# records of primitives — `System.Text.Json` serialises with no extra dep. Resource registration is NOT audited at this layer; it's already audited via Phase 19's `EntityCreated` / `EntityUpdated` audit events under `_platform.audit`.

## `IEventStore` integration with `_platform.audit`

`SourceModule = "_scheduling"` deliberately differs from the Platform's `_platform.audit` namespace: `_platform.*` is reserved for core-SDK events. Scheduling is a companion, so it gets its own underscore-prefixed namespace. Callers query the trail with `IEventStore.ReadBySource(scopeId, "_scheduling")`.

## Six-rule portability audit (GP-12 / Phase 9c)

`IBookingScheduler` is the only interface introduced by Phase 20. Audited against the six rules:

| Rule | Verdict | Evidence |
|---|---|---|
| 1. Identity by value | ✓ | `ResourceId` / `BookingId` are `string`; `scopeId: string`. No live handles in any signature. |
| 2. Async at every boundary | ✓ | Every method returns `Async<_>`. No fire-and-forget `Tell` shapes; no synchronous reads. |
| 3. Retry / supervision as data | ✓ | Failure surfaces through `BookingError` (typed DU) and `BookingConflict` (typed DU). No callbacks; no supervision-strategy objects. Retries are caller-side; the interface is policy-free. |
| 4. Stateless between calls | ✓ | Default impl caches nothing between calls. State lives in `IEntityStore` (Phase 19). A grain that deactivates and re-activates between `Book` and `DetectConflicts` behaves identically. |
| 5. No cross-shard ordering | ✓ | Per-`ResourceId` ordering is implementation-defined (the default uses `IEntityStore` semantics, which serialise per-key writes). Across resources: no ordering. Across scopes: no ordering. |
| 6. Precision at lower bound | ✓ | `DateTimeOffset` (BCL precision: 100 ns ticks). Booking semantics are minute-grain in the conflict detector; sub-minute precision is preserved on save but conflict detection rounds at the minute boundary in practice (window comparisons use `TimeOnly`, which is sub-second). The interface itself promises only `DateTimeOffset` precision. |

A future distributed companion (Akka.NET / Orleans / Hangfire) binds `IBookingSchedulerContract` (15 tests in `ToolUp.Scheduling.Tests/Contracts/`) without modification.

## Simplifications + deferrals (v1)

These are the known boundaries. Each is a single-bullet "follow-up" rather than a hidden bug:

- **Multi-day bookings** — `BookingConflictDetector` checks only the start date's local window. Bookings that span midnight are not v1.
- **Wrapping availability windows** (night shifts, `EndTime <= StartTime`) — treated as "fits any time on this day". No false-positive conflicts but no enforcement on true mid-night-gap bookings either.
- **Owner/Admin gating on writes** — the v1 handler treats every authenticated user as a writer. `AccessContext` carries the role bits; tightening the handler to consult them is a single-line change once a deployment concretely needs it.
- **Reminder fusion** — `SchedulingServerApp.withReminders` is a no-op flag in v1. The plumbing for one-off `IJobScheduler` jobs exists already; wiring is a follow-up.
- **Range queries on `(ResourceId, StartUtc)` compound** — the compound index is registered but `ListBookings` filters in memory after the `ResourceId` index hit. Phase 19a's relational layer would need range predicates on compound keys to lift this; today it only matches `And`-of-`Eq`.
- **iCalendar X-property preservation** — silently dropped on parse, never emitted. A round-trip-stable encoding for `Booking.Metadata` via `X-TOOLUP-META-*` is a follow-up.
- **Multi-resource booking** — one booking → many resources is not v1. Caller composes via N parallel single-resource bookings; if many fail, caller handles rollback.
- **Full RFC 5545** — VTODO / VJOURNAL / VFREEBUSY / VTIMEZONE definitions / positional `BYDAY` / `BYMONTHDAY` / `BYSETPOS` / sub-day frequencies are out of scope. CalDAV companion can extend later.
- **`ServerConfig.EntityStore = EnabledEntityStore` enforcement** — `SchedulingServerApp.run` doesn't validate that the user enabled the entity store. Forgetting yields a null-reference at first dispatch. Explicit fail-loud-at-compose-time is a follow-up.

## Test surface

| File | Tests | What it exercises |
|---|---|---|
| `Tests/InProcess/RecurrenceExpanderTests.fs` | 13 | Pure expander — every frequency, Interval, ByWeekday filter, Until/Count ordering, hard cap, invalid rule |
| `Tests/InProcess/iCalendarTests.fs` | 19 | Parser + emitter — every RRULE shape, datetime forms, line discipline, vendor fixtures, error paths, byte-stable round-trip on canonical emissions |
| `Tests/InProcess/BookingConflictDetectorTests.fs` | 13 | Pure detector — every conflict case, Cancelled/NoShow ignored, self-id excluded, DayOfWeek + EffectiveFrom/To, bad-timezone fallback |
| `Tests/Contracts/IBookingSchedulerContract.fs` | 15 | Framework-agnostic contract pack — every public method on `IBookingScheduler`, audit emission |
| `Tests/InProcess/BookingSchedulerTests.fs` | (binds the contract pack) | Default impl over in-memory `IEntityStore` + `IEventStore` stubs |

Total: 60 tests. All pass.
