module ToolUp.Scheduling.SchedulingApiHandler

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Server
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.SchedulingApi
open ToolUp.Scheduling.IBookingScheduler

// ─── Phase 20 — Fable.Remoting handler ──────────────────────────
//
// Builds a per-request `ISchedulingApi` from the resolved
// `AccessContext` (scopeId / userId) and an `IBookingScheduler`
// pulled from DI. `makeApi` is the SDK's standard Fable.Remoting
// adapter (`SDK.Server.makeApi`) — it carries the Fable-Remoting
// JSON pipeline and the SDK's error classifier.
//
// `ScopeResolutionMiddleware` populates `HttpContext.Items` with
// `ToolUp.UserId` / `ToolUp.StorageScope` ahead of the handler, so
// resolution is synchronous; we just read the items and delegate.
//
// **Authorisation in v1**: every authenticated user can call every
// method. Owner/Admin gating on writes is a follow-up — the
// `AccessContext` carries the role bits already; tightening the
// handler to consult them is a single-line change once a deployment
// concretely needs it. Documented in the companion's
// TECHNICAL_GUIDE.

let private resolveScope (ctx: HttpContext) : string * string =
    let userId =
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) -> id
        | _ -> "anonymous"

    let scopeId =
        match ctx.Items.TryGetValue "ToolUp.StorageScope" with
        | true, (:? StorageScope as scope) -> scope.ScopeId
        | _ -> userId

    scopeId, userId

let schedulingApi (scheduler: IBookingScheduler) (ctx: HttpContext) : ISchedulingApi =
    let scopeId, userId = resolveScope ctx

    {
        RegisterResource = fun resource -> scheduler.RegisterResource(scopeId, resource)
        GetResource = fun id -> scheduler.GetResource(scopeId, id)
        ListResources = fun () -> scheduler.ListResources scopeId

        Book = fun booking -> scheduler.Book(scopeId, booking, userId)
        GetBooking = fun id -> scheduler.GetBooking(scopeId, id)
        Cancel = fun (id, reason) -> scheduler.Cancel(scopeId, id, reason, userId)
        Reschedule = fun req -> scheduler.Reschedule(scopeId, req.Id, req.NewStart, req.NewEnd, userId)
        MarkNoShow = fun id -> scheduler.MarkNoShow(scopeId, id, userId)
        ListBookings = fun (resourceId, window) -> scheduler.ListBookings(scopeId, resourceId, window)

        AddAvailabilityException = fun exc -> scheduler.AddAvailabilityException(scopeId, exc)
        RemoveAvailabilityException = fun id -> scheduler.RemoveAvailabilityException(scopeId, id)
        ListAvailabilityExceptions =
            fun (resourceId, window) -> scheduler.ListAvailabilityExceptions(scopeId, resourceId, window)

        FindAvailableSlots =
            fun req -> scheduler.FindAvailableSlots(scopeId, req.ResourceId, req.Window, req.SlotDurationMinutes)
        DetectConflicts = fun booking -> scheduler.DetectConflicts(scopeId, booking)
        ExpandRecurrence = fun (booking, within) -> scheduler.ExpandRecurrence(scopeId, booking, within)
    }