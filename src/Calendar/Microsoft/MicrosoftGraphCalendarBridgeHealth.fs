// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 831 - the Microsoft Graph calendar bridge's readiness probe.
module ToolUp.Calendar.MicrosoftGraphHealth

open System
open System.Net.Http
open System.Net.Http.Headers
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets
open ToolUp.Scheduling.ICalendarBridge
open ToolUp.Calendar.MicrosoftGraphOAuth

// ─── Phase 831 — the Microsoft Graph bridge's readiness probe ───────
//
// `GET /me/calendars?$top=1&$select=id` with the stored token of one
// named connection: the cheapest request that proves Graph is reachable
// AND the connection's credential still works. It never enumerates
// events — `/ready` is polled on every load-balancer interval (the
// `IHealthCheck` cost rule).
//
// The token comes through the same per-call resolution the bridge uses
// (`MicrosoftGraphOAuth.accessToken`), so a probe that finds the cached
// token stale refreshes it through the Phase 10h refresher exactly as a
// bridge call would. That is deliberate: a probe that bypassed the
// refresher would report a working connection the bridge cannot use, or
// the reverse.
//
// **Readiness, not liveness.** A Graph outage must not restart the
// process: bookings still work, they just stop mirroring. So an
// unreachable Graph is `Degraded`; only a credential the platform
// REFUSES — a state no waiting fixes and an operator must act on (the
// user reconnects, the client secret is rotated) — is `Unhealthy`.

/// Probes Microsoft Graph with the connection of `userId` in `scopeId` —
/// conventionally a service account whose connection the deployment
/// keeps for exactly this purpose, since the probe answers for the
/// deployment rather than for one tenant's user.
type MicrosoftGraphCalendarBridgeHealth
    (
        secretStore: ISecretStore,
        refresher: IOAuthTokenRefresher,
        settings: MicrosoftGraphCalendarSettings,
        scopeId: string,
        userId: string,
        handler: HttpMessageHandler
    ) =

    let client = PlatformHttpClient.createWith EgressSurface.Other handler

    /// The production constructor — the platform's egress-policy-wrapped
    /// transport. Explicit rather than an optional argument, so the
    /// narrow shape stays its own token in the approval baseline.
    new
        (
            secretStore: ISecretStore,
            refresher: IOAuthTokenRefresher,
            settings: MicrosoftGraphCalendarSettings,
            scopeId: string,
            userId: string
        ) =
        MicrosoftGraphCalendarBridgeHealth(secretStore, refresher, settings, scopeId, userId, new HttpClientHandler())

    interface IHealthCheck with

        member _.Name = "calendar_bridge:microsoft"

        member _.Kind = Readiness

        member _.Timeout = TimeSpan.FromSeconds 5.0

        member _.Check() = async {
            if String.IsNullOrWhiteSpace settings.ClientId then
                return Degraded "Microsoft Graph calendar bridge has no client id configured"
            else
                match! accessToken secretStore refresher settings (fun () -> DateTimeOffset.UtcNow) scopeId userId with
                | Error(AuthenticationFailed message) -> return Unhealthy message
                | Error other -> return Degraded(BridgeError.message other)
                | Ok token ->
                    try
                        let url =
                            MicrosoftGraphCalendarSettings.graphBase settings
                            + "/v1.0/me/calendars?$top=1&$select=id"

                        use message = new HttpRequestMessage(HttpMethod.Get, url)
                        message.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
                        let! response = client.SendAsync message |> Async.AwaitTask
                        let status = int response.StatusCode

                        if status >= 200 && status < 300 then
                            return Healthy
                        elif status = 401 || status = 403 then
                            return Unhealthy(sprintf "Microsoft Graph refused the connection's token (%d)" status)
                        else
                            return Degraded(sprintf "Microsoft Graph calendars probe returned %d" status)
                    with ex ->
                        return Degraded(sprintf "Microsoft Graph unreachable: %s" ex.Message)
        }