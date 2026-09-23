// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 831 - the Microsoft Graph bridge's change-notification side: the
/// notification route (with Graph's validation handshake), the
/// subscription-renewal job, and the compose helpers that wire them.
module ToolUp.Calendar.MicrosoftGraphSubscriptions

open System
open System.IO
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Scheduling.ICalendarBridge
open ToolUp.Scheduling.CalendarSync
open ToolUp.Scheduling.SchedulingCompose
open ToolUp.Calendar.MicrosoftGraph

// ─── Phase 831 — subscriptions: the inbound half ────────────────────
//
// Graph delivers change notifications to one public HTTPS URL — the
// bridge's `NotificationUrl`, with the link's scope and calendar in the
// query string. Two different things arrive there:
//
//   * **The validation handshake.** When `LinkResource` creates a
//     subscription, Graph POSTs `?validationToken=…` and refuses to
//     create the subscription unless the answer is `200 text/plain` with
//     the token, decoded, as the body — within ten seconds. It carries
//     no `clientState` and needs none: echoing a token proves only that
//     the endpoint is live, which is all the handshake asks, and the
//     `text/plain` echo carries nothing a browser would render.
//   * **Notifications.** A JSON body of `{ "value": [ … ] }`, each item
//     echoing the subscription's `clientState`. `receive` forwards the
//     query's scope and calendar to the bridge as headers and has the
//     bridge VERIFY first — `HandleWebhook` recomputes the `clientState`
//     HMAC over exactly those two values, so a forged body, or a genuine
//     one replayed against another link's URL, does not verify — and
//     only then asks the sync engine to pull (a delta round through the
//     bridge's stored cursor). Nothing unverified reaches a pull.
//
// **Why a companion route and not the generic inbound-webhook receiver.**
// That receiver verifies an HMAC over the request body under one scheme
// per route and hands its handler the body alone. Graph signs nothing a
// scheme could check (the proof is the `clientState` echoed INSIDE the
// body), the handshake has to be answered IN the response body, and the
// link the notification names travels in the query string — none of
// which that receiver can express. Mount `handler` wherever the
// deployment mounts its other unauthenticated provider callbacks, at the
// path of `NotificationUrl`.
//
// The pull runs inline, before the `202`. A delta round is one page for a
// notification's worth of change, well inside Graph's three-second
// budget for a healthy deployment; a slow endpoint makes Graph retry and,
// persistently, drop the subscription — which the renewal job re-creates,
// pulling in the meantime.
//
// ─── Phase 831 — subscriptions: the lifecycle half ──────────────────
//
// Graph subscriptions on calendar events expire after at most about
// three days. `MicrosoftGraphSubscriptionRenewalJobHandler` runs on a
// cron (hourly by default) and re-links every Microsoft link in its
// scope: `LinkResource` is idempotent and renews in place, re-creating a
// subscription Graph has dropped. A link whose renewal FAILS is pulled on
// the same tick instead — the delta-polling fallback — so a deployment
// whose public endpoint is down, or whose webhook secret was removed,
// still mirrors, at the job's cadence, until renewal succeeds.
//
// Every write the fallback pull makes is attributed by the sync engine
// to the link's own user (Phase 20a / Phase 814): the job names no
// principal, because a cron tick has no caller.

/// The `IJobHandler` name the renewal job registers under.
[<Literal>]
let RenewalHandlerName = "_calendar.microsoft.subscription-renewal"

/// Hourly. Far inside the subscription lifetime (two days by default),
/// and also the cadence the polling fallback runs at while renewal is
/// failing.
[<Literal>]
let DefaultRenewalCron = "0 * * * *"

/// What the notification route answered, and why.
type NotificationReceipt = {
    /// HTTP status to answer Graph with.
    StatusCode: int
    /// Content type of `Body`.
    ContentType: string
    /// Response body: the echoed token for the handshake, a one-line
    /// reason otherwise — never secret material.
    Body: string
    /// The pull a verified notification drove, when it got that far.
    Outcome: PullOutcome option
}

let private reply (status: int) (reason: string) : NotificationReceipt = {
    StatusCode = status
    ContentType = "text/plain"
    Body = reason
    Outcome = None
}

/// Handle one request at the notification URL. `sync` is the composed
/// sync engine (`None` when no calendar bridge is composed).
///
/// `200 text/plain` echoing the token for the validation handshake;
/// `202` for a verified notification, including one whose pull then
/// failed (the renewal job's fallback recovers it, and a non-2xx would
/// only make Graph re-deliver what already verified); `401` for a
/// notification that did not verify — nothing is pulled; `400` for a
/// request that names no link; `503` when calendar sync is not composed.
let receive
    (bridge: ICalendarBridge)
    (sync: ICalendarSync option)
    (query: Map<string, string>)
    (headers: Map<string, string>)
    (body: byte[])
    : Async<NotificationReceipt> =
    async {
        match Map.tryFind "validationToken" query with
        | Some token ->
            return {
                StatusCode = 200
                ContentType = "text/plain"
                Body = token
                Outcome = None
            }
        | None ->
            match Map.tryFind NotificationQuery.Scope query, Map.tryFind NotificationQuery.CalendarId query with
            | Some scopeId, Some calendarId when scopeId <> "" && calendarId <> "" ->
                // The query values override any same-named header a caller
                // sent: these are the values the clientState proves.
                let forwarded =
                    headers
                    |> Map.add NotificationHeaders.Scope scopeId
                    |> Map.add NotificationHeaders.CalendarId calendarId

                match! bridge.HandleWebhook(forwarded, body) with
                | Error err -> return reply 401 (sprintf "notification did not verify: %s" (BridgeError.message err))
                | Ok [] -> return reply 200 "no change named"
                | Ok _ ->
                    match sync with
                    | None -> return reply 503 "calendar sync is not composed"
                    | Some engine ->
                        match! engine.HandleWebhook(scopeId, bridge.Kind, forwarded, body) with
                        | Error e -> return reply 404 (CalendarSyncError.message e)
                        | Ok outcome ->
                            return {
                                reply 202 "accepted" with
                                    Outcome = Some outcome
                            }
            | _ -> return reply 400 "not a Microsoft Graph calendar notification: no scope / calendar in the URL"
    }

/// `receive` as a Giraffe handler for `bridge`. Resolves the sync engine
/// from the request's services, so it can be mounted at compose time.
let handler (bridge: ICalendarBridge) : HttpHandler =
    fun (_next: HttpFunc) (ctx: HttpContext) -> task {
        let sync =
            match ctx.RequestServices.GetService(typeof<ICalendarSync>) with
            | :? ICalendarSync as s -> Some s
            | _ -> None

        let query =
            ctx.Request.Query |> Seq.map (fun kvp -> kvp.Key, string kvp.Value) |> Map.ofSeq

        let headers =
            ctx.Request.Headers
            |> Seq.map (fun kvp -> kvp.Key, string kvp.Value)
            |> Map.ofSeq

        use buffer = new MemoryStream()
        do! ctx.Request.Body.CopyToAsync buffer
        let! receipt = receive bridge sync query headers (buffer.ToArray()) |> Async.StartAsTask
        ctx.SetStatusCode receipt.StatusCode
        ctx.SetContentType receipt.ContentType
        return! ctx.WriteStringAsync receipt.Body
    }

/// Renews every Microsoft link's subscription in the job's scope, and
/// pulls any link whose renewal failed (the polling fallback).
/// Stateless between invocations (portability rule 4): the links are
/// read from the store on every run.
type MicrosoftGraphSubscriptionRenewalJobHandler
    (bridge: ICalendarBridge, sync: ICalendarSync, entityStore: IEntityStore) =

    interface IJobHandler with
        member _.Execute(ctx: JobContext) = async {
            match! entityStore.FindByIndex<CalendarLink>(ctx.ScopeId, CalendarLinkTypeName, "Kind", bridge.Kind) with
            | Error err -> return TransientFailure(EntityError.message err)
            | Ok refs ->
                let failures = ResizeArray<string>()
                let mutable retryable = false

                for r in refs do
                    match! entityStore.Get<CalendarLink>(ctx.ScopeId, CalendarLinkTypeName, r.Id) with
                    | Error _ -> ()
                    | Ok link ->
                        let linkRef: CalendarLinkRef = {
                            ScopeId = ctx.ScopeId
                            ResourceId = link.ResourceId
                            ExternalCalendarId = link.ExternalCalendarId
                            UserId = link.UserId
                        }

                        match! bridge.LinkResource linkRef with
                        | Ok() -> ()
                        | Error renewal ->
                            // Renewal failed: poll now, so the mirror keeps
                            // moving while notifications cannot arrive.
                            if BridgeError.isRetryable renewal then
                                retryable <- true

                            failures.Add(sprintf "%s: renewal failed: %s" link.ResourceId (BridgeError.message renewal))

                            match! sync.PullResource(ctx.ScopeId, link.ResourceId) with
                            | Error e ->
                                failures.Add(
                                    sprintf "%s: fallback pull: %s" link.ResourceId (CalendarSyncError.message e)
                                )
                            | Ok outcome ->
                                for kind, err in outcome.Failures do
                                    if BridgeError.isRetryable err then
                                        retryable <- true

                                    failures.Add(
                                        sprintf
                                            "%s/%s: fallback pull: %s"
                                            link.ResourceId
                                            kind
                                            (BridgeError.message err)
                                    )

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
let renewalDeclaration
    (bridge: ICalendarBridge)
    (scopes: string list)
    (trigger: Trigger)
    : DeferredScheduledJobDeclaration =
    DeferredScheduledJobDeclaration.create (fun sp ->
        let jobHandler =
            match sp.GetService(typeof<ICalendarSync>), sp.GetService(typeof<IEntityStore>) with
            | (:? ICalendarSync as sync), (:? IEntityStore as store) ->
                MicrosoftGraphSubscriptionRenewalJobHandler(bridge, sync, store) :> IJobHandler
            | _ ->
                { new IJobHandler with
                    member _.Execute _ = async {
                        return
                            JobResult.PermanentFailure
                                "Microsoft Graph subscription renewal needs the calendar sync engine — compose the bridge with SchedulingServerApp.withCalendarBridge"
                    }
                }

        ScheduledJobDeclaration.create RenewalHandlerName jobHandler trigger
        |> ScheduledJobDeclaration.withScopes scopes)

/// Compose the subscription-renewal job onto a scheduling app. Pair with
/// `SchedulingServerApp.withCalendarBridge` for the same bridge — or use
/// `compose`, which does both.
let withSubscriptionRenewal
    (bridge: ICalendarBridge)
    (scopes: string list)
    (trigger: Trigger)
    (app: SchedulingServerApp)
    : SchedulingServerApp =
    let register (services: IServiceCollection) =
        services.AddSingleton<IHostedService>(
            Func<IServiceProvider, IHostedService>(fun sp ->
                DeferredScheduledJobDeclaration.hostedService
                    "Phase 831 Microsoft Graph subscription renewal"
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

/// Compose the Microsoft Graph bridge and — when it declares change
/// notifications — the hourly subscription-renewal job over `scopes`. A
/// polling-only bridge needs no renewal: the sync engine's own poll job
/// drives it, so nothing extra is registered (GP 13). The notification
/// route is NOT mounted here; see `handler`.
let compose
    (bridge: MicrosoftGraphCalendarBridge)
    (scopes: string list)
    (app: SchedulingServerApp)
    : SchedulingServerApp =
    let asBridge = bridge :> ICalendarBridge
    let withBridge = SchedulingServerApp.withCalendarBridge asBridge app

    if asBridge.Capabilities.SupportsWebhooks then
        withSubscriptionRenewal asBridge scopes (Trigger.CronTrigger DefaultRenewalCron) withBridge
    else
        withBridge