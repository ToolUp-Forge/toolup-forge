// Ambient context for `docs/scheduling/getting-started.md`.
//
// The page walks one deployment end to end, so every block after the
// first is an excerpt from a program it never shows in full: the
// composition root's `authProvider` / `modules`, the `ISchedulingApi`
// proxy the client tier builds for ITSELF (there is no shipped
// `ToolUp.Scheduling.Client` and no shipped proxy value — see
// `api-reference.md` "Client tier"), the ids and windows a caller
// already holds at the call site, and the two page-local helpers a
// later section reads back (`bookSlot`, `ics`). None of these is SDK
// surface — they are what the page tells a reader to have beside them.
//
// The BCL / Giraffe / DI / Feliz ceremony is hoisted here for the same
// reason the base preamble hoists `open ToolUp.Platform`: a reader
// writing a Giraffe route or a Feliz view already has those opens, and
// repeating them in the markdown is ceremony a copy-paste would carry
// into a file that does not need it.
open Feliz
open Giraffe
open Microsoft.Extensions.DependencyInjection
open ToolUp.Scheduling.SchedulingApi
open ToolUp.Scheduling.IBookingScheduler

[<AutoOpen>]
module PageAmbient =

    // ─── The deployment's composition root ────────────────────────

    let authProvider: IAuthProvider = failwith "ambient"

    let modules: ServerModule list = failwith "ambient"

    // ─── The client-side proxy ────────────────────────────────────

    /// The `ISchedulingApi` proxy a consumer builds itself with
    /// `Remoting.buildProxy<ISchedulingApi>` over
    /// `SchedulingApi.routeBuilder`. Every booking call on the page
    /// goes through it.
    let schedulingApi: ISchedulingApi = failwith "ambient"

    // ─── Values the caller already holds ──────────────────────────

    /// The acting principal, as the module resolved it client-side.
    let currentUserId: string = failwith "ambient"

    /// A booking reference the caller is acting on (from a prior
    /// `Book`, or a row the user clicked).
    let bookingId: BookingId = failwith "ambient"

    /// The resolved storage scope, server-side. Every
    /// `IBookingScheduler` method is scope-first.
    let scopeId: string = failwith "ambient"

    /// The half-open window a server-side read is bounded by. Named
    /// `exportWindow` rather than `window` because the page's FIRST
    /// block declares its own `window` helper, and an ambient a block
    /// shadows is one a reader cannot tell apart from a mistake.
    let exportWindow: DateRange = failwith "ambient"

    /// The seed the recurrence section varies — the deployment's own
    /// already-assembled booking, rather than re-spelling all thirteen
    /// fields at every occurrence.
    let baseBooking: Booking = failwith "ambient"

    // ─── Page-local helpers ───────────────────────────────────────

    /// What the module does when a free slot is clicked — the calendar
    /// block's own handler, not SDK surface.
    let bookSlot (slot: TimeSlot) : unit = failwith "ambient"

    /// The `.ics` emitter built in "Export to iCalendar" and served by
    /// the route block after it. Declared here because each block
    /// compiles on its own; the block that teaches it shadows this.
    let ics (bookings: Booking list) : string = failwith "ambient"