module ToolUp.Scheduling.Tests.InProcess.InMemoryCalendarBridge

open System
open System.Collections.Generic
open ToolUp.Scheduling
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.ICalendarBridge

// ─── Test-only in-memory ICalendarBridge ────────────────────────────
//
// The seam's reference implementation, and the first of the two
// bindings the `ICalendarBridgeContract` pack runs over.
//
// It stores the emitted **iCalendar text** rather than the `Booking` it
// was handed, exactly as a real calendar server does. That is the point
// of it: a fake holding the record would pass a round-trip law the
// iCalendar encoding might not, and the law is about what survives the
// wire format.

/// An in-memory calendar server. `calendars` is the set of collections
/// it has — a `LinkResource` against any other id is `CalendarNotFound`,
/// which is what lets the contract pack exercise the failure arm.
type InMemoryCalendarBridge(calendars: string list) =

    // calendarId -> eventId -> (ics, lastModified)
    let store = Dictionary<string, Dictionary<string, string * DateTimeOffset option>>()

    do
        for c in calendars do
            store[c] <- Dictionary<string, string * DateTimeOffset option>()

    let icsOf (booking: Booking) =
        iCalendar.emit {
            Version = "2.0"
            ProdId = iCalendar.CanonicalProdId
            Events = [ iCalendar.bookingToVEvent booking ]
        }

    let bookingOf (defaults: Booking) (ics: string) =
        match iCalendar.parse ics with
        | Ok calendar ->
            match calendar.Events with
            | [] -> None
            | vevent :: _ -> Some(iCalendar.vEventToBooking defaults vevent)
        | Error _ -> None

    let tryCalendar (calendarId: string) =
        match store.TryGetValue calendarId with
        | true, events -> Some events
        | _ -> None

    /// The defaults the out-of-band editor reads events back through.
    /// Only the calendar-visible fields matter to it.
    let editDefaults: Booking = {
        Id = ""
        Type = "Booking"
        Version = 0
        ResourceId = ""
        Title = ""
        StartUtc = DateTimeOffset.MinValue
        EndUtc = DateTimeOffset.MinValue
        Status = Confirmed
        BookedBy = ""
        BookedFor = None
        Recurrence = None
        ParentBookingId = None
        Metadata = Map.empty
    }

    /// Rewrite one stored event as a person editing it in the
    /// provider's own UI would, stamping it modified at `at`.
    member _.ExternalEdit(calendarId: string, eventId: string, transform: Booking -> Booking, at: DateTimeOffset) =
        match tryCalendar calendarId with
        | None -> failwithf "in-memory calendar '%s' does not exist" calendarId
        | Some events ->
            match events.TryGetValue eventId with
            | false, _ -> failwithf "in-memory calendar '%s' has no event '%s'" calendarId eventId
            | true, (ics, _) ->
                match bookingOf editDefaults ics with
                | None -> failwithf "stored event '%s' is not parseable" eventId
                | Some booking -> events[eventId] <- (icsOf (transform booking), Some at)

    interface ICalendarBridge with

        member _.Kind = "InMemory"

        member _.Capabilities = BridgeCapabilities.pollingOnly

        member _.LinkResource(link) = async {
            match tryCalendar link.ExternalCalendarId with
            | Some _ -> return Ok()
            | None -> return Error(CalendarNotFound link.ExternalCalendarId)
        }

        member _.UnlinkResource(_link) = async { return Ok() }

        member _.Push(link, booking, existing) = async {
            match tryCalendar link.ExternalCalendarId with
            | None -> return Error(CalendarNotFound link.ExternalCalendarId)
            | Some events ->
                let eventId =
                    match existing with
                    | Some id when id.Length > 0 -> id
                    | _ -> sprintf "%s/%s.ics" link.ExternalCalendarId booking.Id

                if booking.Status = Cancelled then
                    events.Remove eventId |> ignore
                    return Ok eventId
                else
                    events[eventId] <- (icsOf booking, None)
                    return Ok eventId
        }

        member _.Pull(link, since, defaults) = async {
            match tryCalendar link.ExternalCalendarId with
            | None -> return Error(CalendarNotFound link.ExternalCalendarId)
            | Some events ->
                let acc = ResizeArray<ExternalEvent>()

                for KeyValue(eventId, (ics, lastModified)) in events do
                    match bookingOf defaults ics with
                    | None -> ()
                    | Some booking ->
                        // `since` bounds event TIME, matching what a
                        // CalDAV `time-range` filter can express — the
                        // contract's pull-since law is about that bound.
                        let included =
                            match since with
                            | Some bound -> booking.StartUtc >= bound
                            | None -> true

                        if included then
                            acc.Add {
                                ExternalEventId = eventId
                                Booking = booking
                                LastModifiedUtc = lastModified
                            }

                return Ok(List.ofSeq acc)
        }

        member _.HandleWebhook(_headers, _body) = async { return Ok [] }