module ToolUp.Scheduling.BookingScheduler

open System
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.SchedulingEvents
open ToolUp.Scheduling.IBookingScheduler

// ─── Phase 20 — Default IBookingScheduler implementation ─────────
//
// Wraps Phase 19's `IEntityStore` for persistence + Phase 6's
// `IEventStore` for audit. Three entity types are registered:
//   * `BookableResource` — index on `ResourceType`.
//   * `Booking` — indexes on `ResourceId`, `Status`, and a compound
//     `(ResourceId, StartUtc-ticks-as-string)` for date-range
//     scans (`ListBookings` filters in memory after the index hit).
//   * `AvailabilityException` — index on `ResourceId`.
//
// Caller wires the registrations into `ServerApp.withEntity` at
// compose time (handled by `SchedulingCompose.fs` in commit 10).
//
// Audit emission: every state-changing call writes one
// `ModuleEvent` to `IEventStore` under
// `SourceModule = "_scheduling"`. Failures in the event-store
// write surface as exceptions — there's no swallow-and-continue
// because the audit trail is part of the `Book` /
// `Cancel` / `Reschedule` / `MarkNoShow` contract.

// ─── Entity type names ────────────────────────────────────────────

[<Literal>]
let ResourceTypeName = "BookableResource"

[<Literal>]
let BookingTypeName = "Booking"

[<Literal>]
let ExceptionTypeName = "AvailabilityException"

// ─── Entity registrations (caller piped through ServerApp.withEntity) ─

let resourceRegistration: EntityRegistration<BookableResource> =
    EntityRegistration.create<BookableResource> ResourceTypeName
    |> EntityRegistration.withIndex "ResourceType" _.ResourceType

let bookingRegistration: EntityRegistration<Booking> =
    EntityRegistration.create<Booking> BookingTypeName
    |> EntityRegistration.withIndex "ResourceId" _.ResourceId
    |> EntityRegistration.withIndex "Status" (fun b -> string b.Status)
    |> EntityRegistration.withCompoundIndex "ResourceStart" (fun b -> [ b.ResourceId; b.StartUtc.UtcTicks.ToString() ])

let exceptionRegistration: EntityRegistration<AvailabilityException> =
    EntityRegistration.create<AvailabilityException> ExceptionTypeName
    |> EntityRegistration.withIndex "ResourceId" _.ResourceId

// ─── JSON serialisation for event payloads ────────────────────────

let private jsonOptions =
    let o = JsonSerializerOptions()
    o.PropertyNamingPolicy <- null
    o

let private serializePayload (payload: 'T) : string =
    JsonSerializer.Serialize(payload, jsonOptions)

let private writeEvent (eventStore: IEventStore) (scopeId: string) (eventType: string) (payload: 'T) : Async<unit> = async {
    let evt: ModuleEvent = {
        Id = Guid.NewGuid()
        OccurredAt = DateTime.UtcNow
        ScopeId = scopeId
        SourceModule = SourceModule
        EventType = eventType
        Payload = serializePayload payload
    }

    do! eventStore.Write evt
}

let private mapEntityError (err: EntityError) : BookingError = StorageFailure(EntityError.message err)

// ─── Default impl ─────────────────────────────────────────────────

type BookingScheduler(entityStore: IEntityStore, eventStore: IEventStore) =

    let getResourceInternal scopeId (id: ResourceId) : Async<BookableResource option> = async {
        let! r = entityStore.Get<BookableResource>(scopeId, ResourceTypeName, id)

        return
            match r with
            | Ok res -> Some res
            | Error _ -> None
    }

    let getBookingInternal scopeId (id: BookingId) : Async<Booking option> = async {
        let! r = entityStore.Get<Booking>(scopeId, BookingTypeName, id)

        return
            match r with
            | Ok b -> Some b
            | Error _ -> None
    }

    let listBookingsForResource scopeId (resourceId: ResourceId) : Async<Booking list> = async {
        let! refResult = entityStore.FindByIndex<Booking>(scopeId, BookingTypeName, "ResourceId", resourceId)

        match refResult with
        | Error _ -> return []
        | Ok refs ->
            let acc = ResizeArray<Booking>()

            for r in refs do
                let! bookingResult = entityStore.Get<Booking>(scopeId, BookingTypeName, r.Id)

                match bookingResult with
                | Ok b -> acc.Add b
                | Error _ -> ()

            return List.ofSeq acc
    }

    let listExceptionsForResource scopeId (resourceId: ResourceId) : Async<AvailabilityException list> = async {
        let! refResult =
            entityStore.FindByIndex<AvailabilityException>(scopeId, ExceptionTypeName, "ResourceId", resourceId)

        match refResult with
        | Error _ -> return []
        | Ok refs ->
            let acc = ResizeArray<AvailabilityException>()

            for r in refs do
                let! excResult = entityStore.Get<AvailabilityException>(scopeId, ExceptionTypeName, r.Id)

                match excResult with
                | Ok e -> acc.Add e
                | Error _ -> ()

            return List.ofSeq acc
    }

    let detectInternal scopeId (booking: Booking) : Async<BookingConflict list> = async {
        match! getResourceInternal scopeId booking.ResourceId with
        | None -> return []
        | Some resource ->
            let! existing = listBookingsForResource scopeId booking.ResourceId
            let! exceptions = listExceptionsForResource scopeId booking.ResourceId

            // Filter to relevant intersect window for performance — pass
            // a small surround so the detector sees only nearby state.
            let bookingDay = DateOnly.FromDateTime booking.StartUtc.UtcDateTime

            let filteredBookings =
                existing
                |> List.filter (fun b ->
                    b.StartUtc < booking.EndUtc.AddDays(1.0)
                    && b.EndUtc > booking.StartUtc.AddDays(-1.0))

            let filteredExceptions = exceptions |> List.filter (fun e -> e.Date = bookingDay)

            let inputs: BookingConflictDetector.DetectorInputs = {
                Resource = resource
                Bookings = filteredBookings
                Exceptions = filteredExceptions
            }

            return BookingConflictDetector.detect inputs booking
    }

    interface IBookingScheduler with

        member _.RegisterResource(scopeId, resource) = async {
            let! r = entityStore.Save<BookableResource>(scopeId, resource)

            return
                match r with
                | Ok _ -> Ok()
                | Error err -> Error(mapEntityError err)
        }

        member _.GetResource(scopeId, id) = getResourceInternal scopeId id

        member _.ListResources(scopeId) = async {
            let! refs = entityStore.ListAll<BookableResource>(scopeId, ResourceTypeName, 0, 10_000)
            let acc = ResizeArray<BookableResource>()

            for r in refs do
                let! resourceResult = entityStore.Get<BookableResource>(scopeId, ResourceTypeName, r.Id)

                match resourceResult with
                | Ok res -> acc.Add res
                | Error _ -> ()

            return acc |> List.ofSeq |> List.sortBy _.Id
        }

        member this.Book(scopeId, booking, actorUserId) = async {
            match! getResourceInternal scopeId booking.ResourceId with
            | None -> return Error(UnknownResource booking.ResourceId)
            | Some _ ->
                let! conflicts = detectInternal scopeId booking

                if not (List.isEmpty conflicts) then
                    return Error(Conflicts conflicts)
                else
                    let! r = entityStore.Save<Booking>(scopeId, booking)

                    match r with
                    | Error err -> return Error(mapEntityError err)
                    | Ok ref ->
                        let payload: BookingCreatedPayload = {
                            UserId = actorUserId
                            BookingId = booking.Id
                            ResourceId = booking.ResourceId
                            StartUtc = booking.StartUtc
                            EndUtc = booking.EndUtc
                        }

                        do! writeEvent eventStore scopeId BookingCreated payload

                        match! getBookingInternal scopeId booking.Id with
                        | Some saved -> return Ok saved
                        | None -> return Ok { booking with Version = ref.Version }
        }

        member _.GetBooking(scopeId, id) = getBookingInternal scopeId id

        member _.Cancel(scopeId, id, reason, actorUserId) = async {
            match! getBookingInternal scopeId id with
            | None -> return Error(UnknownBooking id)
            | Some b when b.Status = Cancelled -> return Ok() // idempotent
            | Some b ->
                let updated = { b with Status = Cancelled }
                let! r = entityStore.Save<Booking>(scopeId, updated)

                match r with
                | Error err -> return Error(mapEntityError err)
                | Ok _ ->
                    let payload: BookingCancelledPayload = {
                        UserId = actorUserId
                        BookingId = id
                        Reason = reason
                    }

                    do! writeEvent eventStore scopeId BookingCancelled payload
                    return Ok()
        }

        member this.Reschedule(scopeId, id, newStart, newEnd, actorUserId) = async {
            if newEnd <= newStart then
                return Error(InvalidWindow "Reschedule end must be strictly after start")
            else
                match! getBookingInternal scopeId id with
                | None -> return Error(UnknownBooking id)
                | Some b ->
                    let updated = {
                        b with
                            StartUtc = newStart
                            EndUtc = newEnd
                    }

                    let! conflicts = detectInternal scopeId updated

                    if not (List.isEmpty conflicts) then
                        return Error(Conflicts conflicts)
                    else
                        let! r = entityStore.Save<Booking>(scopeId, updated)

                        match r with
                        | Error err -> return Error(mapEntityError err)
                        | Ok _ ->
                            let payload: BookingRescheduledPayload = {
                                UserId = actorUserId
                                BookingId = id
                                OldStart = b.StartUtc
                                OldEnd = b.EndUtc
                                NewStart = newStart
                                NewEnd = newEnd
                            }

                            do! writeEvent eventStore scopeId BookingRescheduled payload

                            match! getBookingInternal scopeId id with
                            | Some saved -> return Ok saved
                            | None -> return Ok updated
        }

        member _.MarkNoShow(scopeId, id, actorUserId) = async {
            match! getBookingInternal scopeId id with
            | None -> return Error(UnknownBooking id)
            | Some b when b.Status = NoShow -> return Ok() // idempotent
            | Some b when b.Status <> Confirmed ->
                return Error(StorageFailure "Only Confirmed bookings can be marked NoShow")
            | Some b ->
                let updated = { b with Status = NoShow }
                let! r = entityStore.Save<Booking>(scopeId, updated)

                match r with
                | Error err -> return Error(mapEntityError err)
                | Ok _ ->
                    let payload: BookingNoShowPayload = { UserId = actorUserId; BookingId = id }

                    do! writeEvent eventStore scopeId BookingNoShow payload
                    return Ok()
        }

        member _.ListBookings(scopeId, resourceId, window) = async {
            let! all = listBookingsForResource scopeId resourceId

            return
                all
                |> List.filter (fun b -> b.StartUtc < window.End && b.EndUtc > window.Start)
                |> List.sortBy _.StartUtc
        }

        member _.AddAvailabilityException(scopeId, exc) = async {
            let invalid =
                match exc.Kind, exc.StartTime, exc.EndTime with
                | PartialBlock, None, _
                | PartialBlock, _, None
                | ExtendedHours, None, _
                | ExtendedHours, _, None ->
                    Some "PartialBlock and ExtendedHours exceptions require StartTime and EndTime"
                | _ -> None

            match invalid with
            | Some msg -> return Error(InvalidWindow msg)
            | None ->
                let! r = entityStore.Save<AvailabilityException>(scopeId, exc)

                return
                    match r with
                    | Ok _ -> Ok()
                    | Error err -> Error(mapEntityError err)
        }

        member _.RemoveAvailabilityException(scopeId, id) = async {
            let! r = entityStore.Delete(scopeId, ExceptionTypeName, id)

            return
                match r with
                | Ok() -> Ok()
                | Error err -> Error(mapEntityError err)
        }

        member _.ListAvailabilityExceptions(scopeId, resourceId, window) = async {
            let! all = listExceptionsForResource scopeId resourceId
            let lower = DateOnly.FromDateTime window.Start.UtcDateTime
            let upper = DateOnly.FromDateTime window.End.UtcDateTime

            return
                all
                |> List.filter (fun e -> e.Date >= lower && e.Date < upper)
                |> List.sortBy _.Date
        }

        member _.FindAvailableSlots(scopeId, resourceId, window, slotDurationMinutes) = async {
            // v1 implementation: walk minute-grain candidates inside the
            // window, ask the detector for each, emit slots that survive.
            // Minute-grain is acceptable for the typical booking-window
            // sizes (minutes to days). For very wide windows callers
            // narrow the window first.
            match! getResourceInternal scopeId resourceId with
            | None -> return []
            | Some resource ->
                let! existingBookings = listBookingsForResource scopeId resourceId
                let! exceptions = listExceptionsForResource scopeId resourceId
                let stepMinutes = max 1 slotDurationMinutes
                let stride = TimeSpan.FromMinutes(float stepMinutes)
                let duration = TimeSpan.FromMinutes(float slotDurationMinutes)

                let slots = ResizeArray<TimeSlot>()
                let mutable cursor = window.Start

                while cursor.Add(duration) <= window.End do
                    let candidate: Booking = {
                        Id = "_probe"
                        Type = BookingTypeName
                        Version = 0
                        ResourceId = resourceId
                        Title = ""
                        StartUtc = cursor
                        EndUtc = cursor.Add(duration)
                        Status = Tentative
                        BookedBy = ""
                        BookedFor = None
                        Recurrence = None
                        ParentBookingId = None
                        Metadata = Map.empty
                    }

                    let bookingDay = DateOnly.FromDateTime candidate.StartUtc.UtcDateTime

                    let filteredBookings =
                        existingBookings
                        |> List.filter (fun b ->
                            b.StartUtc < candidate.EndUtc.AddDays(1.0)
                            && b.EndUtc > candidate.StartUtc.AddDays(-1.0))

                    let filteredExceptions = exceptions |> List.filter (fun e -> e.Date = bookingDay)

                    let inputs: BookingConflictDetector.DetectorInputs = {
                        Resource = resource
                        Bookings = filteredBookings
                        Exceptions = filteredExceptions
                    }

                    if List.isEmpty (BookingConflictDetector.detect inputs candidate) then
                        slots.Add {
                            Start = cursor
                            End = cursor.Add(duration)
                            ResourceId = resourceId
                        }

                    cursor <- cursor.Add(stride)

                return List.ofSeq slots
        }

        member _.DetectConflicts(scopeId, booking) = detectInternal scopeId booking

        member _.ExpandRecurrence(scopeId, booking, within) = async {
            match booking.Recurrence with
            | None -> return [ booking ]
            | Some rule -> return RecurrenceExpander.expand booking rule within
        }