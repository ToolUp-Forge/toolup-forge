// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open Fable.SimpleHttp
open Fable.SimpleJson
open ToolUp.Platform.Consent

// ─── Phase 163 — client-side product-telemetry helper ───────────────────
//
// The browser end of the `ITelemetrySink` seam. `Telemetry.track` POSTs a
// `TelemetryEvent` to `/api/_platform/telemetry`, where the server fans it
// out to the composed sink (`TelemetryApiHandler`). The endpoint is mounted
// only when `ServerConfig.TelemetrySink = CustomTelemetrySink`; on the
// `NoTelemetrySink` default the post 404s and is swallowed here, so a
// deployment that composes no analytics pays nothing at either end (GP 13).
//
// **The consent gate is CLIENT-SIDE, and that is the design, not a
// shortcut.** `IConsentProvider` is a client-tier seam — the browser owns
// the consent decision — so the gate runs *before the event leaves the
// browser*: an event that never ships can never breach consent, and there
// is no window in which un-consented behavioural data sits in a server log
// awaiting deletion. Only `ConsentDecision.Granted` for
// `ConsentCategory.Analytics` dispatches; `Denied` and `NotYetDecided`
// both suppress (opt-in semantics — the pre-banner state is not consent).
// The default `NoOpConsentProvider` grants only `Necessary`, so a
// deployment that has wired no CMP suppresses analytics until it does.
//
// **Best-effort, never throwing.** Every failure path — a provider that
// throws, a network error, a 404 from an unmounted endpoint — resolves to
// `unit`. Telemetry never breaks the surface that emitted it, matching the
// `ITelemetrySink.Track` contract on the other side of the wire.
//
// **No PII by construction.** `TelemetryEvent.Properties` are
// operator-declared keys; this helper adds nothing of its own — no user
// id, no session id, no identity of any kind is attached in transit. The
// server tags the event with the caller's already-resolved scope.

module Telemetry =

    /// Server fan-out endpoint. Mounted only under `CustomTelemetrySink`.
    [<Literal>]
    let EndpointPath = "/api/_platform/telemetry"

    /// The consent gate as a pure predicate: analytics dispatches only on
    /// an explicit `Granted`. Exposed (rather than inlined into `trackVia`)
    /// so the opt-in semantics are assertable off the browser — the
    /// difference between `Denied` and `NotYetDecided` is invisible here on
    /// purpose, since neither is consent.
    ///
    /// Phase 191 — the predicate itself now lives on the shared seam
    /// (`ConsentGated.isPermitted`), which the ad path and any
    /// consumer-registered script are gated by too. This binding stays,
    /// unchanged in signature and meaning: it is Phase 163's public
    /// surface and the name its tests assert through.
    let isPermitted (decision: ConsentDecision) : bool =
        Components.ConsentGatedScript.ConsentGated.isPermitted decision

    /// Ship the event to the server fan-out endpoint. Swallows every
    /// transport failure — an unmounted endpoint (the `NoTelemetrySink`
    /// default) 404s, which is a legitimate steady state, not an error.
    let private postToServer (event: TelemetryEvent) : Async<unit> = async {
        try
            let json = Json.serialize event

            let! _ =
                Http.request EndpointPath
                |> Http.method POST
                |> Http.content (BodyContent.Text json)
                |> Http.header (Headers.contentType "application/json")
                |> Http.send

            return ()
        with _ ->
            return ()
    }

    /// Consent-gate `event` against `provider` and hand it to `send` only
    /// when analytics consent is granted. The seam `track` composes, and
    /// the one tests drive — a suppressed event is observable as `send`
    /// never being reached, which is the whole claim.
    ///
    /// Phase 191 — the gate is `ConsentGated.run`, shared with the ad
    /// path and available to any consumer-registered script.
    /// Behaviour is unchanged and Phase 163's tests pin it: exactly one
    /// `HasConsented Analytics` is asked, only `Granted` dispatches, and
    /// a provider that throws — or a `send` that throws — resolves to
    /// `unit` rather than propagating.
    let trackVia
        (provider: IConsentProvider)
        (send: TelemetryEvent -> Async<unit>)
        (event: TelemetryEvent)
        : Async<unit> =
        Components.ConsentGatedScript.ConsentGated.run provider [ Analytics ] (fun () -> send event)

    /// Track a product-analytics event: consent-gated, then POSTed to the
    /// server fan-out endpoint. Awaitable for callers that want to sequence
    /// after it; `trackNow` is the fire-and-forget shape.
    let track (event: TelemetryEvent) : Async<unit> =
        trackVia (ConsentProvider.current ()) postToServer event

    /// Fire-and-forget `track` — starts the gate + post and returns
    /// immediately. The call site pays a scheduled continuation and nothing
    /// else; nothing it can do would fail.
    let trackNow (event: TelemetryEvent) : unit = track event |> Async.StartImmediate