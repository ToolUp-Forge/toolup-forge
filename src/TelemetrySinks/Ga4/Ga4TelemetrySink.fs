// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.TelemetrySinks.Ga4

open System.Net.Http
open System.Text
open System.Text.Json
open ToolUp.Platform

// ─── Phase 163 — GA4 ITelemetrySink (Measurement Protocol) ──────────────
//
// Reference `ITelemetrySink` over the GA4 Measurement Protocol, using BCL
// `HttpClient` only — no vendor SDK reaches `ToolUp.Platform.*` (GP 1) and
// nothing paid is pulled (GP 2). Each `Track` POSTs one event to
// `https://www.google-analytics.com/mp/collect`. Best-effort by contract:
// a transport failure is swallowed (logged by the deployment's HttpClient
// pipeline if configured), never thrown across the boundary — telemetry
// must not break the request that emitted it.
//
// The per-tenant `scopeId` is sent as the GA4 `client_id` so events are
// partitioned per tenant. `TelemetryEvent.Properties` (operator-declared,
// no PII) become the event `params`.

/// GA4 Measurement Protocol `ITelemetrySink`. `measurementId` (`G-XXXX`)
/// and `apiSecret` are the deployment's GA4 stream credentials (resolve the
/// `apiSecret` from your `ISecretStore`, don't hard-code it); `httpClient`
/// is the deployment's pooled client.
type Ga4TelemetrySink(httpClient: HttpClient, measurementId: string, apiSecret: string) =
    let endpoint =
        $"https://www.google-analytics.com/mp/collect?measurement_id={measurementId}&api_secret={apiSecret}"

    interface ITelemetrySink with
        member _.Name = "ga4"

        member _.Track(scopeId: string, event: TelemetryEvent) = async {
            try
                // GA4 params is a flat object of operator-declared keys.
                let paramsObj = System.Collections.Generic.Dictionary<string, string>()
                event.Properties |> Map.iter (fun k v -> paramsObj[k] <- v)

                let payload = {|
                    client_id = scopeId
                    events = [|
                        {|
                            name = event.Event
                            ``params`` = paramsObj
                        |}
                    |]
                |}

                let json = JsonSerializer.Serialize payload
                use content = new StringContent(json, Encoding.UTF8, "application/json")
                let! _ = httpClient.PostAsync(endpoint, content) |> Async.AwaitTask
                return ()
            with _ ->
                // Best-effort: never throw across the boundary (contract).
                return ()
        }

module Ga4TelemetrySink =
    /// Construct a GA4-backed `ITelemetrySink`. Compose with
    /// `ServerConfig.TelemetrySink = CustomTelemetrySink` +
    /// `services.AddSingleton<ITelemetrySink>(Ga4TelemetrySink.create …)`.
    let create (httpClient: HttpClient) (measurementId: string) (apiSecret: string) : ITelemetrySink =
        Ga4TelemetrySink(httpClient, measurementId, apiSecret) :> ITelemetrySink