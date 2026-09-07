module ToolUp.Platform.Tests.InProcess.AdAnalyticsObservabilityTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Metrics

// ─── Phase 466 — ad-analytics silent-degradation observability ───────
//
// The two endpoints degrade WITHOUT failing: a rate-limit-store outage
// fail-opens (admitting every caller with the per-IP budget switched
// off) and a malformed payload is dropped to a bare 400. Neither shows
// up in the request/error metrics, so pre-466 an operator had nothing
// to look at.
//
// The pack is built so the claim is falsifiable rather than merely
// exercised:
//
//   * every assertion on the counter counts the OBSERVABLE EMISSION and
//     its `endpoint` tag, so a counter wired to the wrong metric name
//     or fired on the healthy path fails here;
//   * each degradation is paired with a CONTROL on the identical
//     construction where the thing is healthy, which must observe
//     nothing — a test that only asserted "the counter fired" cannot
//     tell an observability signal from an unconditional one;
//   * the throttle is asserted in BOTH directions: the repeat must
//     suppress the second WARNING while still counting the second
//     OCCURRENCE. That split is the whole design (the log is bounded so
//     the failure cannot become the denial of service; the counter is
//     unconditional so the trail stays complete precisely when the log
//     has gone quiet), and a test that checked only the log would pass
//     against a throttle that suppressed the metric too;
//   * fail-open is asserted as a STATUS CODE, not inferred — the phase
//     changes what is recorded, and must not change what is answered.

let private jsonOptions = FableConverters.create ()

/// Records every `Increment(name, tags)`. Mirrors the Phase 114
/// audit-write-failure pack's sink double.
type private CapturingMetricsSink() =
    let increments = ConcurrentQueue<string * Map<string, string>>()

    member _.Increments = increments |> List.ofSeq

    member this.CountOf(name: string) =
        this.Increments |> List.filter (fun (n, _) -> n = name) |> List.length

    member this.TagsFor(name: string) =
        this.Increments |> List.filter (fun (n, _) -> n = name) |> List.map snd

    interface IMetricsSink with
        member _.Record(_name, _value, _tags) = ()
        member _.Increment(name, tags) = increments.Enqueue(name, tags)
        member _.SetGauge(_name, _value, _tags) = ()

/// Records warnings so the rate-limited signal can be asserted present
/// — and asserted SUPPRESSED on the repeat, which is the half a "the
/// warning fired" test would miss.
type private CapturingLogger() =
    let warns = ConcurrentQueue<string>()

    member _.Warnings = warns |> List.ofSeq

    member this.WarningsContaining(fragment: string) =
        this.Warnings |> List.filter (fun w -> w.Contains fragment)

    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn msg = warns.Enqueue msg
        member _.Error(_, _) = ()

/// An `IRateLimitStore` whose write side answers with a scripted
/// result. The faulting form is the double the acceptance criterion
/// calls for; the allowing form is its control.
type private ScriptedRateLimitStore(answer: Result<InboundRateLimitDecision, RateLimitStoreError>) =
    interface IRateLimitStore with
        member _.GetCurrent(_key, _window) = async { return 0 }
        member _.IncrementAndCheck(_key, _window, _threshold) = async { return answer }
        member _.GetRecentDecisions(_keyFilter, _count) = async { return [] }

let private failingStore () =
    ScriptedRateLimitStore(Error(StoreUnavailable "simulated rate-limit store outage")) :> IRateLimitStore

let private allowingStore () =
    ScriptedRateLimitStore(Ok(AllowWithRemaining 119)) :> IRateLimitStore

type private Harness = {
    Client: HttpClient
    Metrics: CapturingMetricsSink
    Logger: CapturingLogger
    Dispose: unit -> unit
}

let private build (store: IRateLimitStore option) : Harness =
    // The warning throttle is module-level (it is a per-process
    // suppressor, which is what the handler documents), so it must be
    // reset per harness or the tests inherit each other's windows. The
    // slot snapshot is a `CacheReset` (b)-class cache for the same
    // reason.
    AdAnalyticsApiHandler.resetObservabilityState ()

    ToolUp.Platform.Tests.Support.CacheReset.invalidateAll ()
    |> Async.RunSynchronously

    let metrics = CapturingMetricsSink()
    let logger = CapturingLogger()

    let host =
        Host
            .CreateDefaultBuilder()
            .ConfigureWebHostDefaults(fun webHost ->
                webHost
                    .UseTestServer()
                    .ConfigureServices(fun (services: IServiceCollection) ->
                        services.AddGiraffe() |> ignore
                        services.AddSingleton<ILogger>(logger :> ILogger) |> ignore
                        services.AddSingleton<IMetricsSink>(metrics :> IMetricsSink) |> ignore

                        store
                        |> Option.iter (fun s -> services.AddSingleton<IRateLimitStore>(s) |> ignore))
                    .Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe(choose AdAnalyticsApiHandler.routes))
                |> ignore)
            .Build()

    host.Start()

    {
        Client = host.GetTestClient()
        Metrics = metrics
        Logger = logger
        Dispose = fun () -> host.Dispose()
    }

let private post (h: Harness) (path: string) (body: string) =
    let req =
        new HttpRequestMessage(
            HttpMethod.Post,
            path,
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        )

    h.Client.SendAsync req |> Async.AwaitTask |> Async.RunSynchronously

let private impressionPath = "/api/_platform/ads/impression"
let private clickPath = "/api/_platform/ads/click"

/// A well-formed payload, serialised through the SAME converters the
/// handler deserialises with — so the "valid body" fixture cannot drift
/// away from the wire format and quietly turn every fail-open test into
/// a parse-drop test.
let private validImpressionBody () =
    let impression: AdImpression = {
        SlotId = "sidebar-top"
        AdClientId = "ca-pub-test"
        OccurredAt = DateTimeOffset.UtcNow
        PathAtImpression = "/docs/getting-started"
    }

    JsonSerializer.Serialize(impression, jsonOptions)

let private malformedBody = "{ this is not json"

/// The Phase 763 fixture. `null` is VALID JSON, which is the whole
/// reason the pre-763 `try Some(Deserialize) with _ -> None` shape
/// never saw it: nothing threw, the deserialiser handed back a null
/// record typed as `AdImpression`, and the first field access in
/// `validImpression` threw a `NullReferenceException` outside the
/// `try` — a 500, with the parse-drop counter silent. Written as a
/// literal rather than serialised, because the point is the exact
/// four bytes a client sends when it posts an absent value.
let private nullLiteralBody = "null"

let private storeFailures =
    AdAnalyticsApiHandler.AdAnalyticsMetrics.RateLimitStoreFailuresTotal

let private malformedPayloads =
    AdAnalyticsApiHandler.AdAnalyticsMetrics.MalformedPayloadsTotal

[<Tests>]
let tests =
    testList "Phase 466 — ad-analytics silent-degradation observability" [

        // ─── B — observable fail-open ────────────────────────────────

        test "a rate-limit-store failure is counted and warned, and the request is still admitted" {
            let h = build (Some(failingStore ()))

            try
                let response = post h impressionPath (validImpressionBody ())

                // Fail-open is the contract, and this phase does not
                // change it: the caller must not be able to tell the
                // store was down.
                Expect.equal response.StatusCode HttpStatusCode.NoContent "the request is admitted despite the outage"

                Expect.equal (h.Metrics.CountOf storeFailures) 1 "exactly one store-failure increment"

                Expect.equal
                    (h.Metrics.TagsFor storeFailures |> List.map (Map.tryFind "endpoint"))
                    [ Some "impression" ]
                    "tagged with the endpoint that fail-opened"

                Expect.equal
                    (h.Logger.WarningsContaining "event=rate_limit_store_failed" |> List.length)
                    1
                    "one Warn names the fail-open"

                Expect.stringContains
                    (h.Logger.WarningsContaining "event=rate_limit_store_failed" |> List.head)
                    "simulated rate-limit store outage"
                    "and carries the store's own reason"
            finally
                h.Dispose()
        }

        test "the store-failure WARNING is throttled while the COUNTER keeps counting" {
            let h = build (Some(failingStore ()))

            try
                for _ in 1..4 do
                    let response = post h impressionPath (validImpressionBody ())
                    Expect.equal response.StatusCode HttpStatusCode.NoContent "every request stays admitted"

                // The half that matters: suppression bounds the log
                // volume a sustained outage can produce, and must NOT
                // bound the metric an operator alerts on.
                Expect.equal (h.Metrics.CountOf storeFailures) 4 "every occurrence is counted"

                Expect.equal
                    (h.Logger.WarningsContaining "event=rate_limit_store_failed" |> List.length)
                    1
                    "while only the first occurrence in the window is logged"
            finally
                h.Dispose()
        }

        test "a healthy rate-limit store emits no failure signal at all" {
            let h = build (Some(allowingStore ()))

            try
                let response = post h impressionPath (validImpressionBody ())
                Expect.equal response.StatusCode HttpStatusCode.NoContent "admitted"
                Expect.equal (h.Metrics.CountOf storeFailures) 0 "no store-failure increment on the healthy path"
                Expect.isEmpty (h.Logger.WarningsContaining "event=rate_limit_store_failed") "and no warning"
            finally
                h.Dispose()
        }

        test "no composed store at all is not reported as a store failure" {
            // The no-store deployment is a DIFFERENT condition — there
            // is no gate to fail — and `AdAnalyticsRateLimitValidator`
            // is what reports it, at startup. Counting it here would
            // make the outage counter fire permanently on every
            // deployment that never composed a store.
            let h = build None

            try
                let response = post h impressionPath (validImpressionBody ())
                Expect.equal response.StatusCode HttpStatusCode.NoContent "admitted"
                Expect.equal (h.Metrics.CountOf storeFailures) 0 "absence of a store is not a store failure"
            finally
                h.Dispose()
        }

        // ─── C — parse-drop observability ────────────────────────────

        test "a malformed impression payload is counted and warned, and still answers 400" {
            let h = build (Some(allowingStore ()))

            try
                let response = post h impressionPath malformedBody

                Expect.equal response.StatusCode HttpStatusCode.BadRequest "the caller still sees 400"

                Expect.equal (h.Metrics.CountOf malformedPayloads) 1 "exactly one parse-drop increment"

                Expect.equal
                    (h.Metrics.TagsFor malformedPayloads |> List.map (Map.tryFind "endpoint"))
                    [ Some "impression" ]
                    "tagged with the endpoint that dropped it"

                Expect.equal
                    (h.Logger.WarningsContaining "event=malformed_payload" |> List.length)
                    1
                    "one Warn names the drop"
            finally
                h.Dispose()
        }

        test "a malformed click payload is counted under the click endpoint tag" {
            let h = build (Some(allowingStore ()))

            try
                let response = post h clickPath malformedBody

                Expect.equal response.StatusCode HttpStatusCode.BadRequest "400"

                Expect.equal
                    (h.Metrics.TagsFor malformedPayloads |> List.map (Map.tryFind "endpoint"))
                    [ Some "click" ]
                    "the two endpoints are distinguishable on the dashboard"
            finally
                h.Dispose()
        }

        test "the parse-drop WARNING is throttled while the COUNTER keeps counting" {
            let h = build (Some(allowingStore ()))

            try
                for _ in 1..3 do
                    post h impressionPath malformedBody |> ignore

                Expect.equal (h.Metrics.CountOf malformedPayloads) 3 "every dropped event is counted"

                Expect.equal
                    (h.Logger.WarningsContaining "event=malformed_payload" |> List.length)
                    1
                    "while a flood from one address produces one line per window"
            finally
                h.Dispose()
        }

        test "a well-formed payload emits no parse-drop signal" {
            let h = build (Some(allowingStore ()))

            try
                let response = post h impressionPath (validImpressionBody ())
                Expect.equal response.StatusCode HttpStatusCode.NoContent "recorded"
                Expect.equal (h.Metrics.CountOf malformedPayloads) 0 "no parse-drop increment on the happy path"
                Expect.isEmpty (h.Logger.WarningsContaining "event=malformed_payload") "and no warning"
            finally
                h.Dispose()
        }

        // ─── Phase 763 — the null-literal body ───────────────────────

        test "a null-literal impression body answers 400 rather than 500" {
            let h = build (Some(allowingStore ()))

            try
                let response = post h impressionPath nullLiteralBody

                // The assertion the phase exists for. Pre-763 this was
                // `InternalServerError`, and asserting merely "not 204"
                // would have passed against that — so the status is
                // pinned exactly, and the 500 is named in its own right
                // below so a regression cannot read as a near-miss.
                Expect.equal
                    response.StatusCode
                    HttpStatusCode.BadRequest
                    "a null body is the caller's error, answered 400"

                Expect.notEqual
                    response.StatusCode
                    HttpStatusCode.InternalServerError
                    "and never a 500 — the pre-763 behaviour was an unhandled NullReferenceException"
            finally
                h.Dispose()
        }

        test "a null-literal impression body is counted as a parse drop, exactly as a malformed one is" {
            let h = build (Some(allowingStore ()))

            try
                post h impressionPath nullLiteralBody |> ignore

                // The second half of the defect: the drop was invisible.
                // 466's counter sat on the `None` arm, and a null body
                // never reached it.
                Expect.equal (h.Metrics.CountOf malformedPayloads) 1 "exactly one parse-drop increment"

                Expect.equal
                    (h.Metrics.TagsFor malformedPayloads |> List.map (Map.tryFind "endpoint"))
                    [ Some "impression" ]
                    "under the same endpoint tag a malformed body uses"

                Expect.equal
                    (h.Logger.WarningsContaining "event=malformed_payload" |> List.length)
                    1
                    "one Warn names the drop"

                Expect.stringContains
                    (h.Logger.WarningsContaining "event=malformed_payload" |> List.head)
                    "reason=null-body"
                    "and distinguishes the cause from a truncated body, which the counter deliberately does not"
            finally
                h.Dispose()
        }

        test "a null-literal click body answers 400 and counts under the click endpoint tag" {
            let h = build (Some(allowingStore ()))

            try
                let response = post h clickPath nullLiteralBody

                Expect.equal response.StatusCode HttpStatusCode.BadRequest "400, not 500"

                Expect.equal
                    (h.Metrics.TagsFor malformedPayloads |> List.map (Map.tryFind "endpoint"))
                    [ Some "click" ]
                    "the click endpoint carries the identical fix, not a copy that drifted"
            finally
                h.Dispose()
        }

        test "an empty body is a parse drop too, and is reported as the null class rather than as malformed" {
            // `Deserialize("")` throws, so pre-763 an empty body was
            // already a 400 — but it was reported to the operator as a
            // MALFORMED payload, which sends whoever reads the log
            // hunting a wire-format skew that does not exist. The
            // status is unchanged (GP 11); the story is not.
            let h = build (Some(allowingStore ()))

            try
                let response = post h impressionPath ""

                Expect.equal response.StatusCode HttpStatusCode.BadRequest "unchanged: still a 400"
                Expect.equal (h.Metrics.CountOf malformedPayloads) 1 "unchanged: still counted"

                Expect.stringContains
                    (h.Logger.WarningsContaining "event=malformed_payload" |> List.head)
                    "reason=null-body"
                    "no body and a null body are the same story for an operator"
            finally
                h.Dispose()
        }

        test "a genuinely malformed body still reports the malformed reason, not the null one" {
            // The control for the pair above. Without it, a bind that
            // collapsed every failure to `BodyNull` would pass every
            // other assertion in this section.
            let h = build (Some(allowingStore ()))

            try
                post h impressionPath malformedBody |> ignore

                Expect.stringContains
                    (h.Logger.WarningsContaining "event=malformed_payload" |> List.head)
                    "reason=malformed-body"
                    "the two causes stay distinguishable on the log line"
            finally
                h.Dispose()
        }

        // ─── The registration, without which the sink drops the series ──

        test "both counters are registered, so the sink does not silently drop them" {
            // An unregistered series is dropped by the sink's tag-key
            // allowlist — which would reinstate exactly the silence this
            // phase closes, with the emission code present and correct.
            let registered =
                StandardMetrics.registrations
                |> List.map (fun r -> r.Definition.Name, r.Definition.Tags)
                |> Map.ofList

            Expect.equal (Map.tryFind storeFailures registered) (Some [ "endpoint" ]) "store-failure counter registered"

            Expect.equal
                (Map.tryFind malformedPayloads registered)
                (Some [ "endpoint" ])
                "parse-drop counter registered, with the tag key it emits"
        }
    ]
    |> testSequenced