// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 830 - the Google Calendar bridge's readiness probe.
module ToolUp.Calendar.GoogleCalendarHealth

open System
open System.Net.Http
open System.Net.Http.Headers
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open ToolUp.Scheduling.ICalendarBridge
open ToolUp.Calendar.GoogleCalendarOAuth
open ToolUp.Calendar.GoogleCalendar

// ─── Phase 830 — the Google Calendar bridge's readiness probe ───────
//
// `calendarList.list` with `maxResults=1`, under the stored credential of
// one named connection: the cheapest call that proves both that Google
// is reachable and that the credential still works. It reads no events —
// `/ready` is polled on every load-balancer interval, and a probe that
// enumerated a calendar would compound at scale (the `IHealthCheck` cost
// rule).
//
// **Readiness, not liveness**, graded as the CalDAV probe grades: a
// credential Google refuses (or none at all) is `Unhealthy`, because no
// amount of waiting fixes it and an operator must reconnect; an
// unreachable Google is `Degraded`, because bookings still work — they
// just stop mirroring — and a calendar outage must not restart the
// process.

/// Probes Google Calendar with the credential of `connectionId` in
/// `scopeId` — conventionally the shared connection a deployment
/// configured, or the account an operator watches on behalf of all users.
type GoogleCalendarBridgeHealth
    (
        tokens: GoogleCalendarTokenSource,
        settings: GoogleCalendarSettings,
        scopeId: string,
        connectionId: string,
        handler: HttpMessageHandler
    ) =

    let client = PlatformHttpClient.createWith EgressSurface.Other handler

    /// The production constructor — the platform's egress-policy-wrapped
    /// transport. Explicit rather than an optional argument, so the
    /// narrow shape stays its own token in the approval baseline.
    new(tokens: GoogleCalendarTokenSource, settings: GoogleCalendarSettings, scopeId: string, connectionId: string) =
        GoogleCalendarBridgeHealth(tokens, settings, scopeId, connectionId, new HttpClientHandler())

    interface IHealthCheck with

        member _.Name = "calendar_bridge:google"

        member _.Kind = Readiness

        member _.Timeout = TimeSpan.FromSeconds 5.0

        member _.Check() = async {
            match! tokens.AccessToken(scopeId, connectionId) with
            | Error(AuthenticationFailed message) -> return Unhealthy message
            | Error e -> return Degraded(BridgeError.message e)
            | Ok token ->
                try
                    let url =
                        GoogleCalendarSettings.effectiveBase settings
                        + "/users/me/calendarList?maxResults=1"

                    use request = new HttpRequestMessage(HttpMethod.Get, url)
                    request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
                    use! response = client.SendAsync request |> Async.AwaitTask
                    let status = int response.StatusCode

                    if status >= 200 && status < 300 then
                        return Healthy
                    elif status = 401 || status = 403 then
                        return Unhealthy(sprintf "Google Calendar refused the stored credential (%d)" status)
                    else
                        return Degraded(sprintf "Google Calendar calendarList returned %d" status)
                with ex ->
                    return Degraded(sprintf "Google Calendar unreachable: %s" ex.Message)
        }