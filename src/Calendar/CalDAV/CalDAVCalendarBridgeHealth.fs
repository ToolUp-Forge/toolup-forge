/// Phase 20a - the CalDAV bridge's readiness probe.
module ToolUp.Calendar.CalDAVHealth

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets
open ToolUp.Calendar.CalDAV

// ─── Phase 20a — the CalDAV bridge's readiness probe ────────────────
//
// `PROPFIND Depth: 0` on the configured principal / base URL: the
// cheapest request that proves the server is reachable AND the
// credential is accepted. It deliberately does NOT read a calendar —
// `/ready` is polled on every load-balancer interval, and a probe that
// enumerated events would compound at scale (the `IHealthCheck` cost
// rule).
//
// **Readiness, not liveness.** A CalDAV outage must not restart the
// process: bookings still work, they just stop mirroring. So an
// unreachable server reports `Degraded` rather than `Unhealthy`, and
// only a REFUSED CREDENTIAL — a state no amount of waiting fixes, and
// one an operator must act on — reports `Unhealthy`.

/// Probes the configured CalDAV server with a `PROPFIND` on the base
/// URL. `scopeId` is the scope whose `ISecretStore` entry holds the
/// password — conventionally the reserved platform scope, since the
/// probe answers for the deployment rather than for a tenant.
type CalDAVCalendarBridgeHealth
    (secretStore: ISecretStore, settings: CalDAVSettings, scopeId: string, handler: HttpMessageHandler) =

    let client = PlatformHttpClient.createWith EgressSurface.Other handler

    let body =
        """<?xml version="1.0" encoding="utf-8" ?><D:propfind xmlns:D="DAV:"><D:prop><D:resourcetype/></D:prop></D:propfind>"""

    /// The production constructor — the platform's egress-policy-wrapped
    /// transport. Explicit rather than an optional argument, so the
    /// narrow shape stays its own token in the approval baseline.
    new(secretStore: ISecretStore, settings: CalDAVSettings, scopeId: string) =
        CalDAVCalendarBridgeHealth(secretStore, settings, scopeId, new HttpClientHandler())

    interface IHealthCheck with

        member _.Name = "calendar_bridge:caldav"

        member _.Kind = Readiness

        member _.Timeout = TimeSpan.FromSeconds 5.0

        member _.Check() = async {
            let url = CalDAVSettings.effectiveBase settings

            if String.IsNullOrWhiteSpace url then
                return Degraded "CalDAV bridge has no base URL configured"
            else
                match! secretStore.GetSecret(scopeId, CalDAVSettings.SecretKey) with
                | None
                | Some "" -> return Unhealthy(sprintf "no '%s' secret in scope '%s'" CalDAVSettings.SecretKey scopeId)
                | Some password ->
                    try
                        use message = new HttpRequestMessage(HttpMethod "PROPFIND", url)

                        message.Headers.Authorization <-
                            AuthenticationHeaderValue(
                                "Basic",
                                Convert.ToBase64String(
                                    Encoding.UTF8.GetBytes(sprintf "%s:%s" settings.Username password)
                                )
                            )

                        message.Headers.TryAddWithoutValidation("Depth", "0") |> ignore
                        message.Content <- new StringContent(body, Encoding.UTF8, "application/xml")

                        let! response = client.SendAsync message |> Async.AwaitTask
                        let status = int response.StatusCode

                        if status >= 200 && status < 300 then
                            return Healthy
                        elif status = 401 || status = 403 then
                            return Unhealthy(sprintf "CalDAV refused the configured credential (%d)" status)
                        else
                            return Degraded(sprintf "CalDAV PROPFIND returned %d" status)
                    with ex ->
                        return Degraded(sprintf "CalDAV unreachable: %s" ex.Message)
        }