module ToolUp.Scheduling.Tests.InProcess.Phase971SchedulingTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.ICalendarBridge
open ToolUp.Scheduling.IBookingScheduler
open ToolUp.Scheduling.BookingScheduler
open ToolUp.Scheduling.CalendarSync
open ToolUp.Scheduling.Tests.InProcess.InMemoryEntityStore
open ToolUp.Scheduling.Tests.InProcess.InMemoryEventStore
open ToolUp.Scheduling.Tests.InProcess.InMemoryCalendarBridge

// ─── Phase 971 — Scheduling: a refused link delete is reported ───────
//
// `IEntityStore.Delete` answers `Ok` on a missing entity, so an `Error` is
// a refusal. The calendar sync deletes the per-booking event link of a
// cancelled booking (`PushBooking`), and every event link and then the
// resource's calendar link itself (`UnlinkResource`). A refusal that
// answered `Ok` claimed the link gone while it was still there; the
// calendar link — how a re-run finds the resource's links — must outlive
// any event link that could not be deleted.

[<Literal>]
let private CalendarId = "cal-971"

[<Literal>]
let private ScopeId = "team-971"

let private actor = EntityPrincipal.ofPrincipal "calendar-sync-971"

/// `Delete` answers `Error` for every entity of a type in `refusedTypes`;
/// every other operation passes through to `inner`.
type private DeleteRefusingEntityStore(inner: IEntityStore, refusedTypes: Set<string> ref) =
    interface IEntityStore with
        member _.Save<'T>(scopeId, principal, entity: 'T) =
            inner.Save<'T>(scopeId, principal, entity)

        member _.SaveIfVersion<'T>(scopeId, principal, entity: 'T, expectedVersion) =
            inner.SaveIfVersion<'T>(scopeId, principal, entity, expectedVersion)

        member _.Get<'T>(scopeId, entityType, entityId) =
            inner.Get<'T>(scopeId, entityType, entityId)

        member _.GetVersion<'T>(scopeId, entityType, entityId, version) =
            inner.GetVersion<'T>(scopeId, entityType, entityId, version)

        member _.ListVersions<'T>(scopeId, entityType, entityId) =
            inner.ListVersions<'T>(scopeId, entityType, entityId)

        member _.Delete(scopeId, principal, entityType, entityId) =
            if refusedTypes.Value.Contains entityType then
                async { return Error(EntityError.StorageFailure "simulated storage delete refusal") }
            else
                inner.Delete(scopeId, principal, entityType, entityId)

        member _.DeleteIfVersion(scopeId, principal, entityType, entityId, expectedVersion) =
            inner.DeleteIfVersion(scopeId, principal, entityType, entityId, expectedVersion)

        member _.FindByIndex<'T>(scopeId, entityType, indexName, value) =
            inner.FindByIndex<'T>(scopeId, entityType, indexName, value)

        member _.Count(scopeId, entityType) = inner.Count(scopeId, entityType)

        member _.ListAll<'T>(scopeId, entityType, skip, take) =
            inner.ListAll<'T>(scopeId, entityType, skip, take)

        member _.Query<'T>(scopeId, query) = inner.Query<'T>(scopeId, query)

let private booking (status: BookingStatus) : Booking = {
    Id = "bk-971"
    Type = "Booking"
    Version = 3
    ResourceId = "room-101"
    Title = "Standup"
    StartUtc = DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero)
    EndUtc = DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero)
    Status = status
    BookedBy = "alice"
    BookedFor = None
    Recurrence = None
    ParentBookingId = None
    Metadata = Map.empty
}

/// A sync over a refusing store with `room-101` linked and one Confirmed
/// booking already pushed, so an event link exists.
let private linkedAndPushed (refusedTypes: Set<string> ref) = async {
    let inner = InMemoryEntityStore() :> IEntityStore
    let entityStore = DeleteRefusingEntityStore(inner, refusedTypes) :> IEntityStore

    let scheduler =
        BookingScheduler(entityStore, InMemoryEventStore() :> IEventStore) :> IBookingScheduler

    let sync =
        CalendarSync(
            entityStore,
            scheduler,
            [ InMemoryCalendarBridge [ CalendarId ] :> ICalendarBridge ],
            (fun () -> DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero))
        )
        :> ICalendarSync

    match! sync.LinkResource(ScopeId, "room-101", "InMemory", CalendarId, "alice", LocalWins, actor) with
    | Error e -> failtestf "link refused: %s" (CalendarSyncError.message e)
    | Ok _ -> ()

    match! sync.PushBooking(ScopeId, booking Confirmed, actor) with
    | Ok outcome -> Expect.equal outcome.Pushed 1 "the confirmed booking was pushed"
    | Error e -> failtestf "push refused: %s" (CalendarSyncError.message e)

    return sync, inner
}

let private eventLinkPresent (store: IEntityStore) = async {
    match!
        store.Get<CalendarEventLink>(ScopeId, CalendarEventLinkTypeName, eventLinkId "room-101" "InMemory" "bk-971")
    with
    | Ok _ -> return true
    | Error _ -> return false
}

let private calendarLinkPresent (store: IEntityStore) = async {
    match! store.Get<CalendarLink>(ScopeId, CalendarLinkTypeName, linkId "room-101" "InMemory") with
    | Ok _ -> return true
    | Error _ -> return false
}

let tests =
    testList "Phase 971 - Scheduling" [
        testAsync "pushing a cancelled booking answers SyncStorageFailure when its event-link delete is refused" {
            let refused = ref Set.empty
            let! sync, inner = linkedAndPushed refused
            refused.Value <- Set.ofList [ CalendarEventLinkTypeName ]

            match! sync.PushBooking(ScopeId, booking Cancelled, actor) with
            | Error(SyncStorageFailure _) -> ()
            | other -> failtestf "expected Error SyncStorageFailure, got %A" other

            let! present = eventLinkPresent inner
            Expect.isTrue present "the event link is still there"
        }

        testAsync "UnlinkResource answers an Error and keeps the calendar link when an event-link delete is refused" {
            let refused = ref Set.empty
            let! sync, inner = linkedAndPushed refused
            refused.Value <- Set.ofList [ CalendarEventLinkTypeName ]

            match! sync.UnlinkResource(ScopeId, "room-101", "InMemory", actor) with
            | Error(SyncStorageFailure _) -> ()
            | other -> failtestf "expected Error SyncStorageFailure, got %A" other

            let! linkKept = calendarLinkPresent inner
            Expect.isTrue linkKept "the calendar link is kept, so a re-run finds the event link it left"

            // The re-run, once storage accepts the deletes, completes.
            refused.Value <- Set.empty

            match! sync.UnlinkResource(ScopeId, "room-101", "InMemory", actor) with
            | Ok() -> ()
            | Error e -> failtestf "the re-run should complete, got %s" (CalendarSyncError.message e)

            let! eventLinkLeft = eventLinkPresent inner
            let! linkLeft = calendarLinkPresent inner
            Expect.isFalse eventLinkLeft "the re-run removed the event link"
            Expect.isFalse linkLeft "and then the calendar link"
        }

        testAsync "UnlinkResource answers an Error when the calendar-link delete is refused" {
            let refused = ref Set.empty
            let! sync, inner = linkedAndPushed refused
            refused.Value <- Set.ofList [ CalendarLinkTypeName ]

            match! sync.UnlinkResource(ScopeId, "room-101", "InMemory", actor) with
            | Error(SyncStorageFailure _) -> ()
            | other -> failtestf "expected Error SyncStorageFailure, got %A" other

            let! linkKept = calendarLinkPresent inner
            Expect.isTrue linkKept "the calendar link is still there"
        }
    ]