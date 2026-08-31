// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.TelemetrySinkTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net.Http
open System.Text
open Expecto
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Consent
open ToolUp.Platform.Tests.Contracts
open ToolUp.TelemetrySinks.Ga4

// ─── Phase 163 — ITelemetrySink conformance + behaviour ─────────────────
//
// Binds the contract pack to the no-op default + an in-test recording sink
// (always-on) and the GA4 companion (env-gated). Plus the behavioural
// acceptance assertions: the no-op default is a true no-op, a composed sink
// receives `Track` calls tagged with the per-tenant scope, the server
// fan-out endpoint reaches the composed sink, `NoTelemetrySink` mounts no
// endpoint at all, and the client-side consent gate suppresses an
// un-consented event before it can leave the browser.

/// In-test sink that records every `Track` call for assertion.
type private RecordingTelemetrySink() =
    let calls = ConcurrentQueue<string * TelemetryEvent>()
    member _.Calls = calls |> Seq.toList

    interface ITelemetrySink with
        member _.Name = "recording"
        member _.Track(scopeId: string, event: TelemetryEvent) = async { calls.Enqueue(scopeId, event) }

// ─── Server fan-out harness ─────────────────────────────────────────────

/// Drive the mounted telemetry route once against a composed sink and an
/// optional `AccessContext`, returning the status code. Goes through
/// `routesFor CustomTelemetrySink` + Giraffe's `choose` rather than the
/// handler directly, so the mount and the routing are part of what is
/// under test.
let private postTelemetry (sink: ITelemetrySink option) (accessContext: AccessContext option) (body: string) : int =
    let services = ServiceCollection()

    match sink with
    | Some s -> services.AddSingleton<ITelemetrySink>(s) |> ignore
    | None -> ()

    match accessContext with
    | Some ac -> services.AddSingleton<AccessContext>(ac) |> ignore
    | None -> ()

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx.Request.Method <- "POST"
    ctx.Request.Path <- PathString "/api/_platform/telemetry"
    ctx.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes body)
    ctx.Response.Body <- new MemoryStream()

    let router = choose (TelemetryApiHandler.routesFor CustomTelemetrySink)
    let next: HttpFunc = fun c -> System.Threading.Tasks.Task.FromResult(Some c)

    router next ctx |> _.GetAwaiter().GetResult() |> ignore
    ctx.Response.StatusCode

// ─── Client consent-gate stub ───────────────────────────────────────────

/// `IConsentProvider` that answers every category with one fixed decision.
/// Enough to drive `Telemetry.trackVia`'s gate: the helper asks exactly one
/// question (`HasConsented Analytics`) and nothing else on the interface.
let private fixedConsent (decision: ConsentDecision) : IConsentProvider =
    { new IConsentProvider with
        member _.GetCurrentState() = async { return ConsentState.initial }
        member _.RequestConsent(_categories) = async { return ConsentState.initial }
        member _.HasConsented(_category) = async { return decision }

        member _.OnStateChanged(_handler) =
            { new IDisposable with
                member _.Dispose() = ()
            }
    }

[<Tests>]
let tests =
    testList "Phase 163 — ITelemetrySink" [
        ITelemetrySinkContract.tests "NoOpTelemetrySink" (fun () -> NoOpTelemetrySink() :> ITelemetrySink)
        ITelemetrySinkContract.tests "RecordingTelemetrySink" (fun () -> RecordingTelemetrySink() :> ITelemetrySink)

        testCaseAsync "NoOpTelemetrySink is a true no-op"
        <| async {
            let sink = NoOpTelemetrySink() :> ITelemetrySink
            Expect.equal sink.Name "noop" "no-op sink names itself"
            // Completes, records nothing, never throws — there is nothing to
            // observe, which is the point (zero cost at the emission site).
            do!
                sink.Track(
                    "scope-a",
                    {
                        Event = "anything"
                        Properties = Map [ "k", "v" ]
                    }
                )
        }

        testCaseAsync "A composed sink receives Track calls tagged with the per-tenant scope"
        <| async {
            let recording = RecordingTelemetrySink()

            do!
                (recording :> ITelemetrySink)
                    .Track(
                        "tenant-7",
                        {
                            Event = "page_view"
                            Properties = Map [ "path", "/home" ]
                        }
                    )

            let calls = recording.Calls
            Expect.equal calls.Length 1 "exactly one Track recorded"
            let scope, ev = calls[0]
            Expect.equal scope "tenant-7" "the call is tagged with the scope"
            Expect.equal ev.Event "page_view" "the event name is carried"
            Expect.equal ev.Properties["path"] "/home" "the properties are carried"
        }

        // ── Server fan-out endpoint ──────────────────────────────────────

        test "The fan-out endpoint hands the posted event to the composed sink" {
            let recording = RecordingTelemetrySink()

            let status =
                postTelemetry
                    (Some(recording :> ITelemetrySink))
                    None
                    """{"Event":"page_view","Properties":{"path":"/home"}}"""

            Expect.equal status 204 "a well-formed post is accepted"

            let calls = recording.Calls
            Expect.equal calls.Length 1 "the composed sink saw exactly one Track"
            let scope, ev = calls[0]
            Expect.equal ev.Event "page_view" "the event name crossed the wire"
            Expect.equal ev.Properties["path"] "/home" "the property bag crossed the wire"

            Expect.equal scope "_platform" "with no resolved caller scope the event lands in the deployment-wide bucket"
        }

        test "The fan-out endpoint tags the event with the caller's resolved scope" {
            let recording = RecordingTelemetrySink()
            let accessContext = AccessContext.unrestricted (TeamMember("u-1", "team-7"))

            let status =
                postTelemetry
                    (Some(recording :> ITelemetrySink))
                    (Some accessContext)
                    """{"Event":"report_exported","Properties":{"format":"pdf"}}"""

            Expect.equal status 204 "accepted"
            let scope, _ = recording.Calls[0]
            Expect.equal scope "team-7" "the sink is called under the caller's team scope"
        }

        // A post carrying only an event name is legitimate — a bare
        // `page_view` has nothing to say. `FableConverters` deserialises the
        // absent `Properties` to a NULL Map, which NREs on the first read, so
        // the handler coerces before validating. Deleting that coercion turns
        // the commonest possible event into a 500.
        test "A bare event with no properties is accepted, not NREd" {
            let recording = RecordingTelemetrySink()

            let status =
                postTelemetry (Some(recording :> ITelemetrySink)) None """{"Event":"page_view"}"""

            Expect.equal status 204 "a bare event is accepted"
            let _, ev = recording.Calls[0]
            Expect.isTrue ev.Properties.IsEmpty "the absent property bag reads as empty, never null"
        }

        test "A malformed or invalid payload is refused before the sink is reached" {
            let recording = RecordingTelemetrySink()
            let sink = Some(recording :> ITelemetrySink)

            Expect.equal (postTelemetry sink None "not json at all") 400 "malformed body refused"
            Expect.equal (postTelemetry sink None """{"Event":"  ","Properties":{}}""") 400 "blank event name refused"

            Expect.equal
                (postTelemetry sink None (sprintf """{"Event":"%s","Properties":{}}""" (String('e', 600))))
                400
                "an over-long event name is refused"

            Expect.isEmpty recording.Calls "no refused payload ever reached the sink"
        }

        // GP 13, and the acceptance criterion the whole seam rests on: the
        // default composes no route at all, so there is nothing to allocate,
        // nothing to authorise and nothing to 404 through a handler.
        test "NoTelemetrySink (the default) mounts no endpoint" {
            Expect.isEmpty
                (TelemetryApiHandler.routesFor NoTelemetrySink)
                "the default mode contributes no routes to the router"

            Expect.equal
                (List.length (TelemetryApiHandler.routesFor CustomTelemetrySink))
                1
                "opting in mounts exactly the one fan-out route"

            Expect.equal
                ServerConfig.defaults.TelemetrySink
                NoTelemetrySink
                "and the unmounted mode is what a stock deployment gets"
        }

        // ── Client-side consent gate ─────────────────────────────────────

        // The acceptance criterion "when an IConsentProvider is composed and
        // analytics consent is absent, Track is suppressed" — enforced in the
        // browser, before the event can leave it. Suppression is observable
        // as the transport never being reached.
        testCaseAsync "Analytics consent absent suppresses the event client-side"
        <| async {
            for decision in [ NotYetDecided; Denied ] do
                let sent = ResizeArray<TelemetryEvent>()

                do!
                    Telemetry.trackVia (fixedConsent decision) (fun ev -> async { sent.Add ev }) {
                        Event = "page_view"
                        Properties = Map.empty
                    }

                Expect.isEmpty sent (sprintf "an event must not leave the browser under %A consent" decision)
        }

        testCaseAsync "Granted analytics consent dispatches the event to the transport"
        <| async {
            let sent = ResizeArray<TelemetryEvent>()

            do!
                Telemetry.trackVia (fixedConsent Granted) (fun ev -> async { sent.Add ev }) {
                    Event = "page_view"
                    Properties = Map [ "path", "/home" ]
                }

            Expect.equal sent.Count 1 "a consented event is dispatched"
            Expect.equal sent[0].Event "page_view" "unmodified — the helper adds nothing of its own"
        }

        // The default provider grants only `Necessary`, so a deployment that
        // has wired no CMP suppresses analytics rather than defaulting open.
        testCaseAsync "The default NoOpConsentProvider suppresses analytics"
        <| async {
            let sent = ResizeArray<TelemetryEvent>()

            do!
                Telemetry.trackVia (NoOpConsentProvider() :> IConsentProvider) (fun ev -> async { sent.Add ev }) {
                    Event = "page_view"
                    Properties = Map.empty
                }

            Expect.isEmpty sent "opt-in semantics: no CMP wired means no analytics"
        }

        // A provider that throws is not consent. Fail-closed, matching
        // `IConsentProvider`'s own "unknown / errored folds into
        // NotYetDecided" rule — and the helper still never throws at its
        // own call site.
        testCaseAsync "A throwing consent provider suppresses rather than propagating"
        <| async {
            let sent = ResizeArray<TelemetryEvent>()

            let throwing =
                { new IConsentProvider with
                    member _.GetCurrentState() = async { return ConsentState.initial }
                    member _.RequestConsent(_categories) = async { return ConsentState.initial }
                    member _.HasConsented(_category) = async { return failwith "CMP unavailable" }

                    member _.OnStateChanged(_handler) =
                        { new IDisposable with
                            member _.Dispose() = ()
                        }
                }

            do!
                Telemetry.trackVia throwing (fun ev -> async { sent.Add ev }) {
                    Event = "page_view"
                    Properties = Map.empty
                }

            Expect.isEmpty sent "a broken CMP must not open the gate"
        }

        test "The gate predicate admits only an explicit Granted" {
            Expect.isTrue (Telemetry.isPermitted Granted) "granted dispatches"
            Expect.isFalse (Telemetry.isPermitted Denied) "denied suppresses"
            Expect.isFalse (Telemetry.isPermitted NotYetDecided) "not-yet-decided is not consent"
        }

        // GA4 companion — env-gated live arm (mirrors the storage / AI-provider
        // companions; skipped without GA4 stream credentials).
        match
            Environment.GetEnvironmentVariable "TOOLUP_GA4_MEASUREMENT_ID",
            Environment.GetEnvironmentVariable "TOOLUP_GA4_API_SECRET"
        with
        | (null | ""), _
        | _, (null | "") ->
            ptestCase "Ga4TelemetrySink — skipped (TOOLUP_GA4_MEASUREMENT_ID / _API_SECRET not set)"
            <| fun _ -> ()
        | mid, secret ->
            ITelemetrySinkContract.tests "Ga4TelemetrySink" (fun () ->
                Ga4TelemetrySink.create (new HttpClient()) mid secret)
    ]