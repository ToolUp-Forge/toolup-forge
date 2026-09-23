// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 830 - the Google bridge's watch-channel lifecycle: the renewal
/// job (with its polling fallback) and the notification route.
module ToolUp.Calendar.GoogleCalendarChannels

open System
open System.IO
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.IEntityStore
open ToolUp.Scheduling.ICalendarBridge
open ToolUp.Scheduling.CalendarSync
open ToolUp.Scheduling.SchedulingCompose
open ToolUp.Calendar.GoogleCalendar

// ─── Phase 830 — watch channels: renewal and notification ───────────
//
// **Renewal.** A Google watch channel expires (the bridge asks for
// `GoogleCalendarSettings.ChannelTtl`; Google may grant less). The
// renewal job walks every Google link in its scope and re-runs the
// bridge's `LinkResource` — which is idempotent and opens a fresh
// channel only for one inside `ChannelRenewalLead` of its end. A link
// whose renewal FAILS is pulled on the spot: that is the polling
// fallback, so a link that has lost its channel still converges on the
// job's own cadence rather than going silent until someone notices.
//
// A bridge composed WITHOUT a webhook address declares itself
// polling-only, and the sync engine's own poll job covers it — the
// renewal job is for the webhook-driven shape only.
//
// **Notification.** `receive` is the whole route: the channel token names
// the scope (`GoogleCalendar.tryScopeOfChannelToken`), the sync engine is
// asked to handle the notification in that scope, and the bridge verifies
// the token before anything is pulled. `handler` adapts it to Giraffe.
//
// **Why a companion route and not the generic inbound-webhook receiver.**
// That receiver verifies an HMAC over the request BODY under one scheme
// per route, and hands its handler the body alone. A Google channel
// notification has an empty body, is authenticated by the token it
// echoes in a header, and names its channel and calendar only in
// headers — none of which reaches a handler there. Mount `handler`
// wherever the deployment mounts its other unauthenticated provider
// callbacks.

/// Handler name the renewal job registers under.
[<Literal>]
let RenewalHandlerName = "_calendar.google-channel-renewal"

/// Default renewal cadence: every fifteen minutes, which is also the
/// cadence a link whose renewal failed is polled at.
[<Literal>]
let DefaultRenewalCron = "*/15 * * * *"

/// Renews every Google link's watch channel in the job's scope, and
/// pulls a link whose renewal failed. Stateless between invocations
/// (portability rule 4): the links are read from the store on every run.
/// It names no principal — `PullResource` attributes each link's writes
/// to that link's own user.
type GoogleCalendarChannelRenewalJobHandler
    (bridge: ICalendarBridge, sync: ICalendarSync, entityStore: IEntityStore, pageSize: int) =

    /// Default page size for the per-tick link enumeration.
    new(bridge: ICalendarBridge, sync: ICalendarSync, entityStore: IEntityStore) =
        GoogleCalendarChannelRenewalJobHandler(bridge, sync, entityStore, 200)

    interface IJobHandler with
        member _.Execute(ctx: JobContext) = async {
            let links = ResizeArray<CalendarLink>()
            let mutable skip = 0
            let mutable more = true

            while more do
                let! refs = entityStore.ListAll<CalendarLink>(ctx.ScopeId, CalendarLinkTypeName, skip, pageSize)

                if List.isEmpty refs then
                    more <- false
                else
                    for r in refs do
                        match! entityStore.Get<CalendarLink>(ctx.ScopeId, CalendarLinkTypeName, r.Id) with
                        | Ok link when link.Kind = bridge.Kind -> links.Add link
                        | _ -> ()

                    skip <- skip + List.length refs

            let failures = ResizeArray<string>()
            let mutable retryable = false

            let note (resourceId: string) (err: BridgeError) =
                failures.Add(sprintf "%s: %s" resourceId (BridgeError.message err))

                if BridgeError.isRetryable err then
                    retryable <- true

            for link in links do
                let linkRef: CalendarLinkRef = {
                    ScopeId = ctx.ScopeId
                    ResourceId = link.ResourceId
                    ExternalCalendarId = link.ExternalCalendarId
                    UserId = link.UserId
                }

                match! bridge.LinkResource linkRef with
                | Ok() -> ()
                | Error renewal ->
                    note link.ResourceId renewal

                    // The polling fallback: the channel is not trustworthy
                    // any more, so read the calendar now.
                    match! sync.PullResource(ctx.ScopeId, link.ResourceId) with
                    | Error e -> failures.Add(sprintf "%s: %s" link.ResourceId (CalendarSyncError.message e))
                    | Ok outcome ->
                        for _kind, err in outcome.Failures do
                            note link.ResourceId err

            if failures.Count = 0 then
                return JobResult.Success
            elif retryable then
                return JobResult.TransientFailure(String.concat "; " failures)
            else
                return JobResult.PermanentFailure(String.concat "; " failures)
        }

/// The renewal job as a DI-deferred declaration: the sync engine and the
/// entity store are resolved from the built provider. `scopes` are the
/// storage scopes the deployment links resources in — the job visits
/// only those (an empty list means the reserved platform scope, the
/// scheduler's own default).
let renewalDeclaration (bridge: ICalendarBridge) (scopes: string list) (trigger: Trigger) =
    DeferredScheduledJobDeclaration.create (fun sp ->
        let handler =
            match sp.GetService(typeof<ICalendarSync>), sp.GetService(typeof<IEntityStore>) with
            | (:? ICalendarSync as sync), (:? IEntityStore as store) ->
                GoogleCalendarChannelRenewalJobHandler(bridge, sync, store) :> IJobHandler
            | _ ->
                { new IJobHandler with
                    member _.Execute _ = async {
                        return
                            JobResult.PermanentFailure
                                "Google channel renewal needs the calendar sync engine — compose the bridge with SchedulingServerApp.withCalendarBridge"
                    }
                }

        ScheduledJobDeclaration.create RenewalHandlerName handler trigger
        |> ScheduledJobDeclaration.withScopes scopes)

/// Compose the renewal job onto a scheduling app. Pair with
/// `SchedulingServerApp.withCalendarBridge` for the same bridge; a bridge
/// with no webhook address needs neither this nor a route.
let withChannelRenewal
    (bridge: ICalendarBridge)
    (scopes: string list)
    (trigger: Trigger)
    (app: SchedulingServerApp)
    : SchedulingServerApp =
    let register (services: IServiceCollection) =
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
            Func<IServiceProvider, Microsoft.Extensions.Hosting.IHostedService>(fun sp ->
                DeferredScheduledJobDeclaration.hostedService
                    "Phase 830 Google Calendar channel renewal"
                    [ renewalDeclaration bridge scopes trigger ]
                    sp)
        )

    let baseApp = app.Base

    {
        app with
            Base = {
                baseApp with
                    Extensions = {
                        baseApp.Extensions with
                            ServiceConfig =
                                match baseApp.Extensions.ServiceConfig with
                                | None -> Some register
                                | Some baseFn -> Some(fun s -> register (baseFn s))
                    }
            }
    }

// ─── The notification route ─────────────────────────────────────────

/// What the notification route answered, and why.
type WebhookReceipt = {
    /// HTTP status to answer Google with.
    StatusCode: int
    /// The pull the notification drove, when it got that far.
    Outcome: PullOutcome option
    /// One-line reason, safe to return — no secret material.
    Reason: string
}

/// Handle one Google channel notification: address the sync engine in
/// the scope the channel token names, and let the bridge verify it.
/// `401` for a notification that did not verify, `404` when no Google
/// bridge is composed, `400` for a request that is not a channel
/// notification at all, `200` otherwise — including a verified
/// notification whose pull failed, which the renewal job's fallback and
/// the next notification recover.
let receive (sync: ICalendarSync) (headers: Map<string, string>) (body: byte[]) : Async<WebhookReceipt> = async {
    let token =
        headers
        |> Map.toSeq
        |> Seq.tryFind (fun (k, _) -> String.Equals(k, "X-Goog-Channel-Token", StringComparison.OrdinalIgnoreCase))
        |> Option.map snd

    match token |> Option.bind tryScopeOfChannelToken with
    | None ->
        return {
            StatusCode = 400
            Outcome = None
            Reason = "not a Google Calendar channel notification"
        }
    | Some scopeId ->
        match! sync.HandleWebhook(scopeId, KindName, headers, body) with
        | Error e ->
            return {
                StatusCode = 404
                Outcome = None
                Reason = CalendarSyncError.message e
            }
        | Ok outcome ->
            let unverified =
                outcome.Failures
                |> List.exists (fun (_, err) ->
                    match err with
                    | MalformedPayload _ -> true
                    | _ -> false)

            if unverified then
                return {
                    StatusCode = 401
                    Outcome = Some outcome
                    Reason = "notification did not verify"
                }
            else
                return {
                    StatusCode = 200
                    Outcome = Some outcome
                    Reason = "ok"
                }
}

/// `receive` as a Giraffe handler. Resolves the sync engine from the
/// request's services; answers `503` when no calendar bridge is composed.
let handler: HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task {
        match ctx.RequestServices.GetService(typeof<ICalendarSync>) with
        | :? ICalendarSync as sync ->
            let headers =
                ctx.Request.Headers
                |> Seq.map (fun kvp -> kvp.Key, string kvp.Value)
                |> Map.ofSeq

            use buffer = new MemoryStream()
            do! ctx.Request.Body.CopyToAsync buffer
            let! receipt = receive sync headers (buffer.ToArray()) |> Async.StartAsTask
            return! (setStatusCode receipt.StatusCode >=> text receipt.Reason) next ctx
        | _ -> return! (setStatusCode 503 >=> text "calendar sync is not composed") next ctx
    }