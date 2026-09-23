module ToolUp.Scheduling.Tests.Contracts.ICalendarBridgeContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Scheduling
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.ICalendarBridge
open ToolUp.Scheduling.IBookingScheduler
open ToolUp.Scheduling.BookingScheduler
open ToolUp.Scheduling.CalendarSync
open ToolUp.Scheduling.Tests.InProcess.InMemoryEntityStore
open ToolUp.Scheduling.Tests.InProcess.InMemoryEventStore

// ─── ICalendarBridge contract pack ──────────────────────────────────
//
// Framework-agnostic pack over the Phase 20a calendar-bridge seam. A
// binding supplies a `BridgeHarness` — a constructed bridge, a link
// pointing at a calendar the provider really has, an id it does not,
// and a way to edit an event OUT OF BAND, as a person would in the
// provider's own web UI. Everything else is the contract.
//
// Two bindings ship: an in-memory fake (`InMemoryCalendarBridgeTests`)
// and the real CalDAV bridge over a stub `HttpMessageHandler` serving
// canned `PROPFIND` / `REPORT` / `PUT` / `DELETE` responses
// (`CalDAVCalendarBridgeTests`). The point of the pack is that both
// must satisfy it: a law only one of them passes is a law about an
// implementation, not about the seam.
//
// The last three cases drive the SYNC ENGINE over the harness's bridge
// rather than the bridge alone. Conflict policy is the engine's
// behaviour, but it is only observable THROUGH a bridge — so the pack
// binds them here, where every provider gets held to the same
// resolution rather than each binding asserting its own.

/// What a binding supplies.
type BridgeHarness = {
    Bridge: ICalendarBridge
    /// Scope, resource and a calendar the provider can reach.
    Link: CalendarLinkRef
    /// A calendar id the provider does NOT have.
    MissingCalendarId: ExternalCalendarId
    /// Rewrite one external event out of band and stamp it modified at
    /// the supplied instant — the simulated external edit.
    ExternalEdit: ExternalEventId -> (Booking -> Booking) -> DateTimeOffset -> unit
}

type BridgeFactory = unit -> BridgeHarness

let private utc (y: int) (mo: int) (d: int) (h: int) : DateTimeOffset =
    DateTimeOffset(y, mo, d, h, 0, 0, TimeSpan.Zero)

/// A booking carrying the metadata the seam promises to round-trip:
/// the three reserved keys plus a non-reserved entry that has to ride
/// an `X-TOOLUP-META-*` property, including characters that force the
/// key encoding and the text escaping.
let makeBooking (id: BookingId) (resourceId: ResourceId) (start: DateTimeOffset) : Booking = {
    Id = id
    Type = "Booking"
    Version = 0
    ResourceId = resourceId
    Title = "Quarterly review"
    StartUtc = start
    EndUtc = start.AddHours 1.0
    Status = Confirmed
    BookedBy = "alice"
    BookedFor = None
    Recurrence = None
    ParentBookingId = None
    Metadata =
        Map.ofList [
            "Location", "Room 3; second floor, east"
            "Organizer", "mailto:alice@example.com"
            "Attendees", "mailto:bob@example.com,mailto:carol@example.com"
            "cost centre", "R&D / 42"
        ]
}

let private defaultsFor (link: CalendarLinkRef) : Booking = {
    makeBooking "" link.ResourceId DateTimeOffset.MinValue with
        Title = ""
        Metadata = Map.empty
        BookedBy = link.UserId
}

let private expectOk (label: string) (result: Result<'T, BridgeError>) : 'T =
    match result with
    | Ok v -> v
    | Error e -> failtestf "%s: %s" label (BridgeError.message e)

/// The calendar-visible projection — what a round-trip must preserve.
/// `Version` / `Status` / `BookedBy` are deliberately excluded: no
/// calendar carries them, and the seam says so.
let private visible (b: Booking) =
    b.Title, b.StartUtc, b.EndUtc, b.Recurrence, b.Metadata

let private icsOf (b: Booking) : string =
    iCalendar.emit {
        Version = "2.0"
        ProdId = iCalendar.CanonicalProdId
        Events = [ iCalendar.bookingToVEvent b ]
    }

// ─── The sync-engine fixture the conflict cases run over ────────────

type private SyncFixture = {
    Sync: ICalendarSync
    Scheduler: IBookingScheduler
    ScopeId: string
    Actor: EntityPrincipal
}

let private buildSync (harness: BridgeHarness) (now: unit -> DateTimeOffset) = async {
    let entityStore = InMemoryEntityStore() :> IEntityStore
    let eventStore = InMemoryEventStore() :> IEventStore

    let scheduler = BookingScheduler(entityStore, eventStore) :> IBookingScheduler

    let sync =
        CalendarSync(entityStore, scheduler, [ harness.Bridge ], now) :> ICalendarSync

    let scopeId = harness.Link.ScopeId

    // Available around the clock: the conflict detector refuses a
    // booking outside every declared window, and the pack is about
    // mirroring, not about availability.
    let resource: BookableResource = {
        Id = harness.Link.ResourceId
        Type = "BookableResource"
        Version = 0
        ResourceType = "Room"
        DisplayName = harness.Link.ResourceId
        Timezone = "UTC"
        DefaultAvailability = [
            {
                DayOfWeek = None
                StartTime = TimeOnly(0, 0)
                EndTime = TimeOnly(23, 59)
                EffectiveFrom = None
                EffectiveTo = None
            }
        ]
        Metadata = Map.empty
    }

    let actor = EntityPrincipal.ofPrincipal "calendar-sync-test"
    let! _ = scheduler.RegisterResource(scopeId, actor, resource)

    return {
        Sync = sync
        Scheduler = scheduler
        ScopeId = scopeId
        Actor = actor
    }
}

/// Book through the scheduler the fixture already registered the
/// resource with.
let private bookLocally (fixture: SyncFixture) (booking: Booking) = async {
    match! fixture.Scheduler.Book(fixture.ScopeId, booking, fixture.Actor.Principal) with
    | Ok saved -> return saved
    | Error e -> return failtestf "local booking refused: %A" e
}

let private linkAndPush (fixture: SyncFixture) (harness: BridgeHarness) (booking: Booking) = async {
    match!
        fixture.Sync.LinkResource(
            fixture.ScopeId,
            harness.Link.ResourceId,
            harness.Bridge.Kind,
            harness.Link.ExternalCalendarId,
            harness.Link.UserId,
            LatestModifiedWins,
            fixture.Actor
        )
    with
    | Error e -> return failtestf "link refused: %s" (CalendarSyncError.message e)
    | Ok _ ->
        let! saved = bookLocally fixture booking

        match! fixture.Sync.PushBooking(fixture.ScopeId, saved, fixture.Actor) with
        | Error e -> return failtestf "push refused: %s" (CalendarSyncError.message e)
        | Ok outcome ->
            Expect.isEmpty outcome.Failures "push reported a bridge failure"
            Expect.equal outcome.Pushed 1 "one external event pushed"
            return saved
}

/// Re-link with a different policy, keeping everything else — the
/// conflict cases differ only in this one value.
let private relinkWithPolicy (fixture: SyncFixture) (harness: BridgeHarness) (policy: ConflictPolicy) = async {
    match!
        fixture.Sync.LinkResource(
            fixture.ScopeId,
            harness.Link.ResourceId,
            harness.Bridge.Kind,
            harness.Link.ExternalCalendarId,
            harness.Link.UserId,
            policy,
            fixture.Actor
        )
    with
    | Error e -> return failtestf "re-link refused: %s" (CalendarSyncError.message e)
    | Ok _ -> return ()
}

let private externalEventIdOf (harness: BridgeHarness) (booking: Booking) = async {
    match! harness.Bridge.Pull(harness.Link, None, defaultsFor harness.Link) with
    | Error e -> return failtestf "pull refused: %s" (BridgeError.message e)
    | Ok events ->
        match events |> List.tryFind (fun e -> e.Booking.Id = booking.Id) with
        | Some e -> return e.ExternalEventId
        | None -> return failtestf "the pushed booking %s is not in the external calendar" booking.Id
}

// ─── The pack ───────────────────────────────────────────────────────

let tests (label: string) (factory: BridgeFactory) =
    testList (sprintf "ICalendarBridge contract — %s" label) [

        test "Kind is non-empty and stable across reads" {
            let h = factory ()
            Expect.isNotEmpty h.Bridge.Kind "Kind is a persisted wire value and cannot be blank"
            Expect.equal h.Bridge.Kind h.Bridge.Kind "Kind does not vary between reads"
        }

        testAsync "LinkResource succeeds on a calendar the provider has" {
            let h = factory ()
            let! result = h.Bridge.LinkResource h.Link
            expectOk "link" result |> ignore
        }

        testAsync "LinkResource reports a calendar the provider does not have" {
            let h = factory ()

            let missing = {
                h.Link with
                    ExternalCalendarId = h.MissingCalendarId
            }

            match! h.Bridge.LinkResource missing with
            | Ok() -> failtest "linking a non-existent calendar reported success"
            | Error(CalendarNotFound id) -> Expect.equal id h.MissingCalendarId "names the calendar it could not find"
            | Error e -> failtestf "expected CalendarNotFound, got %s" (BridgeError.message e)
        }

        testAsync "UnlinkResource is idempotent" {
            let h = factory ()
            let! first = h.Bridge.UnlinkResource h.Link
            expectOk "first unlink" first |> ignore
            let! second = h.Bridge.UnlinkResource h.Link
            expectOk "second unlink" second |> ignore
        }

        testAsync "push then pull round-trips the booking, attendee metadata included" {
            let h = factory ()
            let booking = makeBooking "bk-round-trip" h.Link.ResourceId (utc 2026 10 1 9)
            let! _ = h.Bridge.LinkResource h.Link
            let! pushed = h.Bridge.Push(h.Link, booking, None)
            let externalId = expectOk "push" pushed

            let! pulled = h.Bridge.Pull(h.Link, None, defaultsFor h.Link)
            let events = expectOk "pull" pulled
            Expect.isNotEmpty externalId "push returned an external id"

            // Found by UID rather than by the id the push returned: a
            // provider may legitimately spell the same event's id
            // differently between a write and a read (an absolute URL
            // versus a collection-relative href), and the round-trip
            // law is about the PAYLOAD, not the spelling.
            match events |> List.tryFind (fun e -> e.Booking.Id = booking.Id) with
            | None -> failtest "the pushed event did not come back from the pull"
            | Some e ->
                Expect.equal (visible e.Booking) (visible booking) "every calendar-visible field round-tripped"

                Expect.equal
                    (Map.tryFind "Attendees" e.Booking.Metadata)
                    (Map.tryFind "Attendees" booking.Metadata)
                    "attendee metadata survived the round trip"

                Expect.equal
                    (Map.tryFind "cost centre" e.Booking.Metadata)
                    (Map.tryFind "cost centre" booking.Metadata)
                    "a non-reserved metadata key survived on an X-TOOLUP-META-* property"

                Expect.equal (icsOf e.Booking) (icsOf booking) "the re-emitted ICS is byte-identical"
        }

        testAsync "push is idempotent — the same booking pushed twice is one external event" {
            let h = factory ()
            let booking = makeBooking "bk-idempotent" h.Link.ResourceId (utc 2026 10 2 9)
            let! _ = h.Bridge.LinkResource h.Link
            let! first = h.Bridge.Push(h.Link, booking, None)
            let firstId = expectOk "first push" first

            let! second = h.Bridge.Push(h.Link, { booking with Title = "Renamed" }, Some firstId)
            let secondId = expectOk "second push" second
            Expect.equal secondId firstId "the second push addressed the same external event"

            let! pulled = h.Bridge.Pull(h.Link, None, defaultsFor h.Link)
            let events = expectOk "pull" pulled

            let matching = events |> List.filter (fun e -> e.Booking.Id = booking.Id)

            Expect.equal (List.length matching) 1 "exactly one external event carries the booking id"
            Expect.equal matching.Head.Booking.Title "Renamed" "the update was applied, not appended"
        }

        // `since` means two different things, and the seam says which by
        // capability (`ICalendarBridge.Pull`): a lower bound on event TIME
        // for a bridge without a modification cursor, the cursor itself
        // for one declaring `SupportsIncrementalPull`. Each law binds only
        // the bridges it is true of (Phase 830 — the first incremental
        // bridge — split what had been one time-bound law).
        testAsync "pull honours the since bound" {
            let h = factory ()

            if h.Bridge.Capabilities.SupportsIncrementalPull then
                skiptest "bridge declares a modification cursor; see the incremental since law"
            else
                let early = makeBooking "bk-early" h.Link.ResourceId (utc 2026 10 3 9)
                let late = makeBooking "bk-late" h.Link.ResourceId (utc 2026 11 20 9)
                let! _ = h.Bridge.LinkResource h.Link
                let! _ = h.Bridge.Push(h.Link, early, None)
                let! _ = h.Bridge.Push(h.Link, late, None)

                let! pulled = h.Bridge.Pull(h.Link, Some(utc 2026 11 1 0), defaultsFor h.Link)
                let events = expectOk "bounded pull" pulled
                let ids = events |> List.map _.Booking.Id |> Set.ofList

                Expect.isTrue (Set.contains "bk-late" ids) "an event after the bound is returned"
                Expect.isFalse (Set.contains "bk-early" ids) "an event before the bound is not"
        }

        testAsync "an incremental pull's since is a modification cursor" {
            let h = factory ()

            if not h.Bridge.Capabilities.SupportsIncrementalPull then
                skiptest "bridge declares no modification cursor; see the time-bound since law"
            else
                // Relative to the wall clock rather than pinned: the
                // provider stamps a push with ITS clock, so the cursor has
                // to sit after any plausible push stamp and before the
                // simulated edit, whenever the pack runs.
                let cursor = DateTimeOffset.UtcNow.AddYears 50
                let untouched = makeBooking "bk-untouched" h.Link.ResourceId (utc 2026 10 3 9)
                let edited = makeBooking "bk-edited" h.Link.ResourceId (utc 2026 10 3 11)
                let! _ = h.Bridge.LinkResource h.Link
                let! _ = h.Bridge.Push(h.Link, untouched, None)
                let! pushed = h.Bridge.Push(h.Link, edited, None)
                let editedId = expectOk "push" pushed

                h.ExternalEdit
                    editedId
                    (fun b -> {
                        b with
                            Title = "Edited after the cursor"
                    })
                    (cursor.AddHours 1.0)

                let! pulled = h.Bridge.Pull(h.Link, Some cursor, defaultsFor h.Link)
                let events = expectOk "incremental pull" pulled
                let ids = events |> List.map _.Booking.Id |> Set.ofList

                Expect.isTrue (Set.contains "bk-edited" ids) "an event modified after the cursor is returned"
                Expect.isFalse (Set.contains "bk-untouched" ids) "an event not modified since the cursor is not"
        }

        testAsync "cancelling a booking removes the external event" {
            let h = factory ()
            let booking = makeBooking "bk-cancelled" h.Link.ResourceId (utc 2026 10 4 9)
            let! _ = h.Bridge.LinkResource h.Link
            let! pushed = h.Bridge.Push(h.Link, booking, None)
            let externalId = expectOk "push" pushed

            let! removed = h.Bridge.Push(h.Link, { booking with Status = Cancelled }, Some externalId)

            expectOk "cancel push" removed |> ignore

            let! pulled = h.Bridge.Pull(h.Link, None, defaultsFor h.Link)
            let events = expectOk "pull" pulled

            Expect.isFalse
                (events |> List.exists (fun e -> e.Booking.Id = booking.Id))
                "the cancelled booking is gone from the external calendar"
        }

        testAsync "HandleWebhook is a no-op on a polling bridge" {
            let h = factory ()

            if h.Bridge.Capabilities.SupportsWebhooks then
                skiptest "bridge declares webhook support; the no-op law does not apply"
            else
                let! result = h.Bridge.HandleWebhook(Map.empty, [||])

                match result with
                | Ok notifications -> Expect.isEmpty notifications "a polling bridge reports no inbound changes"
                | Error e -> failtestf "a polling bridge's webhook no-op failed: %s" (BridgeError.message e)
        }

        testAsync "MinimumPollInterval is a positive declared floor" {
            let h = factory ()

            Expect.isGreaterThan
                h.Bridge.Capabilities.MinimumPollInterval
                TimeSpan.Zero
                "a poll floor of zero promises a cadence no provider tolerates"
        }

        // ─── Conflict policy, over the sync engine ──────────────

        testAsync "ExternalWins applies the external edit to the local booking" {
            let h = factory ()
            let pinned = utc 2026 10 5 12
            let! fixture = buildSync h (fun () -> pinned)
            let booking = makeBooking "bk-external-wins" h.Link.ResourceId (utc 2026 10 6 9)
            let! saved = linkAndPush fixture h booking
            do! relinkWithPolicy fixture h ExternalWins

            let! externalId = externalEventIdOf h saved

            h.ExternalEdit
                externalId
                (fun b -> {
                    b with
                        Title = "Moved by the calendar owner"
                })
                (pinned.AddHours 1.0)

            match! fixture.Sync.PullResource(fixture.ScopeId, h.Link.ResourceId) with
            | Error e -> failtestf "pull refused: %s" (CalendarSyncError.message e)
            | Ok outcome ->
                Expect.isEmpty outcome.Failures "pull reported a bridge failure"
                Expect.equal outcome.AppliedLocally 1 "the external change was applied locally"

                match! fixture.Scheduler.GetBooking(fixture.ScopeId, saved.Id) with
                | None -> failtest "the local booking vanished"
                | Some local -> Expect.equal local.Title "Moved by the calendar owner" "the local title now matches"
        }

        testAsync "LocalWins re-pushes the local booking over the external edit" {
            let h = factory ()
            let pinned = utc 2026 10 7 12
            let! fixture = buildSync h (fun () -> pinned)
            let booking = makeBooking "bk-local-wins" h.Link.ResourceId (utc 2026 10 8 9)
            let! saved = linkAndPush fixture h booking
            do! relinkWithPolicy fixture h LocalWins

            let! externalId = externalEventIdOf h saved
            h.ExternalEdit externalId (fun b -> { b with Title = "Hijacked externally" }) (pinned.AddHours 1.0)

            match! fixture.Sync.PullResource(fixture.ScopeId, h.Link.ResourceId) with
            | Error e -> failtestf "pull refused: %s" (CalendarSyncError.message e)
            | Ok outcome ->
                Expect.isEmpty outcome.Failures "pull reported a bridge failure"
                Expect.equal outcome.RePushed 1 "the local booking was re-pushed"
                Expect.equal outcome.AppliedLocally 0 "nothing external was applied locally"

                match! fixture.Scheduler.GetBooking(fixture.ScopeId, saved.Id) with
                | None -> failtest "the local booking vanished"
                | Some local -> Expect.equal local.Title booking.Title "the local title is untouched"

                match! h.Bridge.Pull(h.Link, None, defaultsFor h.Link) with
                | Error e -> failtestf "verification pull refused: %s" (BridgeError.message e)
                | Ok pulled ->
                    match pulled |> List.tryFind (fun e -> e.Booking.Id = saved.Id) with
                    | None -> failtest "the re-pushed booking is missing externally"
                    | Some e -> Expect.equal e.Booking.Title booking.Title "the external copy was overwritten"
        }

        testAsync "LatestModifiedWins prefers the side that changed last" {
            let h = factory ()
            let pinned = utc 2026 10 9 12
            let! fixture = buildSync h (fun () -> pinned)
            let booking = makeBooking "bk-latest-wins" h.Link.ResourceId (utc 2026 10 10 9)
            let! saved = linkAndPush fixture h booking
            let! externalId = externalEventIdOf h saved

            // An edit stamped BEFORE our last push loses.
            h.ExternalEdit externalId (fun b -> { b with Title = "Stale external edit" }) (pinned.AddHours -1.0)

            match! fixture.Sync.PullResource(fixture.ScopeId, h.Link.ResourceId) with
            | Error e -> failtestf "pull refused: %s" (CalendarSyncError.message e)
            | Ok outcome -> Expect.equal outcome.AppliedLocally 0 "a stale external edit does not win"

            // An edit stamped AFTER it wins.
            h.ExternalEdit externalId (fun b -> { b with Title = "Fresh external edit" }) (pinned.AddHours 1.0)

            match! fixture.Sync.PullResource(fixture.ScopeId, h.Link.ResourceId) with
            | Error e -> failtestf "second pull refused: %s" (CalendarSyncError.message e)
            | Ok outcome ->
                Expect.equal outcome.AppliedLocally 1 "a fresh external edit wins"

                match! fixture.Scheduler.GetBooking(fixture.ScopeId, saved.Id) with
                | None -> failtest "the local booking vanished"
                | Some local -> Expect.equal local.Title "Fresh external edit" "the fresher side is the one stored"
        }

        testAsync "unlink through the engine drops the link and its event links" {
            let h = factory ()
            let pinned = utc 2026 10 11 12
            let! fixture = buildSync h (fun () -> pinned)
            let booking = makeBooking "bk-unlinked" h.Link.ResourceId (utc 2026 10 12 9)
            let! _ = linkAndPush fixture h booking

            match! fixture.Sync.UnlinkResource(fixture.ScopeId, h.Link.ResourceId, h.Bridge.Kind, fixture.Actor) with
            | Error e -> failtestf "unlink refused: %s" (CalendarSyncError.message e)
            | Ok() ->
                let! links = fixture.Sync.ListLinks(fixture.ScopeId, h.Link.ResourceId)
                Expect.isEmpty links "no link survives the unlink"

                // A push after unlinking mirrors nowhere.
                match! fixture.Sync.PushBooking(fixture.ScopeId, booking, fixture.Actor) with
                | Error e -> failtestf "post-unlink push refused: %s" (CalendarSyncError.message e)
                | Ok outcome -> Expect.equal outcome.Pushed 0 "an unlinked resource mirrors nothing"
        }
    ]