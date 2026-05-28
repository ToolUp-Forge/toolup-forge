module ToolUp.Platform.Tests.Contracts.IMetricsSinkContract

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Metrics

// ─── IMetricsSink contract pack ──────────────────────────────────────
//
// Parametrised tests for any `IMetricsSink` implementation. Each
// test asks the factory for a fresh `(sink, render)` pair where
// `render : unit -> string` returns the implementation-specific
// representation of the recorded metrics. For the default
// `PrometheusMetricsSink` this is OpenMetrics text; for an in-memory
// test stub it could be a JSON dump. The contract asserts behaviour
// on the rendered representation rather than re-introspecting the
// sink's internal state, so any future companion (StatsD, OTLP-only)
// can bind by choosing what to render.
//
// Coverage:
//   1. Counter Increment + render round-trip
//   2. Gauge SetGauge round-trip
//   3. Histogram Record bucket attribution
//   4. Tag-key allowlist drops unsanctioned tags
//   5. Per-metric series-count cap → `_overflow="true"`
//   6. Per-metric override raises the cap on a single metric
//   7. Concurrent emission safety
//   8. Module namespace prefixing (`Module = Some "MyMod"`)
//   9. Reserved-prefix protection (module declaring `toolup.*` rejected)

let private noopLogger =
    { new ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(_: string) = ()
        member _.Error(_: string, _: exn option) = ()
    }

/// Simple recording logger so test 5 can assert "exactly one Warn
/// per cardinality overflow."
type private RecordingLogger() =
    let warnings = ResizeArray<string>()

    member _.Warnings = warnings |> List.ofSeq

    interface ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()

        member _.Warn(message: string) =
            lock warnings (fun () -> warnings.Add message)

        member _.Error(_: string, _: exn option) = ()

let tests
    (name: string)
    (factory: MetricsSinkConfig -> MetricRegistration list -> ILogger -> IMetricsSink * (unit -> string))
    =

    testList $"{name} — IMetricsSink contract" [

        // ─── 1. Counter Increment round-trip ────────────────────

        testCase "1. Increment renders the counter value"
        <| fun _ ->
            let regs = [
                {
                    Module = None
                    Definition = {
                        Name = "toolup.foo.total"
                        Kind = Counter
                        Description = "test counter"
                        Unit = "1"
                        Tags = []
                    }
                }
            ]

            let sink, render = factory MetricsSinkConfig.defaults regs noopLogger
            sink.Increment("toolup.foo.total", Map.empty)
            sink.Increment("toolup.foo.total", Map.empty)
            sink.Increment("toolup.foo.total", Map.empty)
            let output = render ()

            Expect.stringContains output "toolup_foo_total" "rendered counter name with dot→underscore"
            Expect.stringContains output "3" "counter shows three increments"

        // ─── 2. Gauge SetGauge round-trip ────────────────────────

        testCase "2. SetGauge renders the gauge value"
        <| fun _ ->
            let regs = [
                {
                    Module = None
                    Definition = {
                        Name = "toolup.foo.gauge"
                        Kind = Gauge
                        Description = "test gauge"
                        Unit = "1"
                        Tags = []
                    }
                }
            ]

            let sink, render = factory MetricsSinkConfig.defaults regs noopLogger
            sink.SetGauge("toolup.foo.gauge", 42.0, Map.empty)
            sink.SetGauge("toolup.foo.gauge", 17.0, Map.empty)
            let output = render ()

            Expect.stringContains output "toolup_foo_gauge" "rendered gauge name"
            Expect.stringContains output "17" "latest SetGauge wins"
            Expect.isFalse (output.Contains " 42") "earlier value not retained"

        // ─── 3. Histogram bucket attribution ─────────────────────

        testCase "3. Record attributes observation to correct bucket"
        <| fun _ ->
            let regs = [
                {
                    Module = None
                    Definition = {
                        Name = "toolup.foo.dur_ms"
                        Kind = Histogram [ 10.0; 50.0; 100.0; 500.0; 1000.0 ]
                        Description = "test histogram"
                        Unit = "ms"
                        Tags = []
                    }
                }
            ]

            let sink, render = factory MetricsSinkConfig.defaults regs noopLogger
            sink.Record("toolup.foo.dur_ms", 75.0, Map.empty)
            let output = render ()

            Expect.stringContains output "toolup_foo_dur_ms_bucket" "histogram emitted bucket lines"
            Expect.stringContains output "toolup_foo_dur_ms_sum" "histogram emitted _sum"
            Expect.stringContains output "toolup_foo_dur_ms_count" "histogram emitted _count"
            // 75.0 falls into the (50, 100] bucket — every bucket >= 100 should have count 1.
            Expect.stringContains output "le=\"100\"" "100ms bucket present"

        // ─── 4. Tag-key allowlist ────────────────────────────────

        testCase "4. Tags not in the allowlist are silently dropped"
        <| fun _ ->
            let regs = [
                {
                    Module = None
                    Definition = {
                        Name = "toolup.foo.tagged"
                        Kind = Counter
                        Description = "test tagged counter"
                        Unit = "1"
                        Tags = [ "allowed" ]
                    }
                }
            ]

            let sink, render = factory MetricsSinkConfig.defaults regs noopLogger
            sink.Increment("toolup.foo.tagged", Map.ofList [ "allowed", "yes"; "forbidden", "no" ])
            let output = render ()

            Expect.stringContains output "allowed=\"yes\"" "allowed tag rendered"
            Expect.isFalse (output.Contains "forbidden") "disallowed tag dropped"

        // ─── 5. Cardinality cap → overflow series ───────────────

        testCase "5. Series-count cap routes overflow tag-sets to _overflow=true"
        <| fun _ ->
            let cap = 3

            let cfg = {
                MaxSeriesPerMetric = cap
                PerMetricMaxSeries = Map.empty
            }

            let regs = [
                {
                    Module = None
                    Definition = {
                        Name = "toolup.cap.total"
                        Kind = Counter
                        Description = "test"
                        Unit = "1"
                        Tags = [ "id" ]
                    }
                }
            ]

            let logger = RecordingLogger()
            let sink, render = factory cfg regs (logger :> ILogger)

            for i in 1 .. (cap + 5) do
                sink.Increment("toolup.cap.total", Map.ofList [ "id", string i ])

            let output = render ()
            Expect.stringContains output "_overflow=\"true\"" "overflow series rendered"
            Expect.equal (List.length logger.Warnings) 1 "exactly one Warn for cardinality overflow"

        // ─── 6. Per-metric override raises the cap ──────────────

        testCase "6. Per-metric override applies"
        <| fun _ ->
            let cfg = {
                MaxSeriesPerMetric = 2
                PerMetricMaxSeries = Map.ofList [ "toolup.high.total", 100 ]
            }

            let regs = [
                {
                    Module = None
                    Definition = {
                        Name = "toolup.low.total"
                        Kind = Counter
                        Description = "low cap"
                        Unit = "1"
                        Tags = [ "id" ]
                    }
                }
                {
                    Module = None
                    Definition = {
                        Name = "toolup.high.total"
                        Kind = Counter
                        Description = "high cap"
                        Unit = "1"
                        Tags = [ "id" ]
                    }
                }
            ]

            let sink, render = factory cfg regs noopLogger

            for i in 1..10 do
                sink.Increment("toolup.low.total", Map.ofList [ "id", string i ])
                sink.Increment("toolup.high.total", Map.ofList [ "id", string i ])

            let output = render ()
            Expect.stringContains output "_overflow" "low cap triggered overflow"

            // Count "toolup_high_total{id=\"" occurrences — should be at least 10
            // (one per distinct id). A simple substring count works because
            // the labels render is deterministic.
            let prefix = "toolup_high_total{id=\""

            let highSeriesCount =
                let mutable count = 0
                let mutable i = 0

                while i < output.Length do
                    let idx = output.IndexOf(prefix, i)

                    if idx < 0 then
                        i <- output.Length
                    else
                        count <- count + 1
                        i <- idx + prefix.Length

                count

            Expect.equal highSeriesCount 10 "high-cap metric retained all 10 distinct series"

        // ─── 7. Concurrent emission safety ──────────────────────

        testCase "7. Concurrent Increment is thread-safe"
        <| fun _ ->
            let regs = [
                {
                    Module = None
                    Definition = {
                        Name = "toolup.concurrent.total"
                        Kind = Counter
                        Description = "thread test"
                        Unit = "1"
                        Tags = []
                    }
                }
            ]

            let sink, render = factory MetricsSinkConfig.defaults regs noopLogger
            let threads = 8
            let perThread = 1000

            let tasks = [|
                for _ in 1..threads ->
                    Task.Run(fun () ->
                        for _ in 1..perThread do
                            sink.Increment("toolup.concurrent.total", Map.empty))
            |]

            Task.WaitAll(tasks)
            let output = render ()
            let expected = threads * perThread
            Expect.stringContains output (sprintf "toolup_concurrent_total %d" expected) "all increments accounted for"

        // ─── 8. Module namespace prefixing ──────────────────────

        testCase "8. Module-scoped metric name is auto-namespaced"
        <| fun _ ->
            let regs = [
                {
                    Module = Some "MyMod"
                    Definition = {
                        Name = "foo.total"
                        Kind = Counter
                        Description = "module scope"
                        Unit = "1"
                        Tags = []
                    }
                }
            ]

            let sink, render = factory MetricsSinkConfig.defaults regs noopLogger
            // The post-namespace name is `toolup.mymod.foo.total`.
            sink.Increment("toolup.mymod.foo.total", Map.empty)
            let output = render ()
            Expect.stringContains output "toolup_mymod_foo_total" "name auto-prefixed with toolup.mymod.*"

        // ─── 9. Reserved-prefix protection ──────────────────────
        //
        // A module-scoped registration whose `Name` already begins
        // with the reserved `toolup.` prefix is rejected at sink
        // construction (the registration helper auto-namespaces to
        // `toolup.{module}.{name}`; a module declaring `toolup.*`
        // itself would collide with the SDK namespace). The
        // implementation enforces this in its registration-build path
        // (`RegisteredMetric.create` for the Prometheus default; the
        // registry loop for the OTel companion).

        testCase "9. Module declaring `toolup.*` is rejected at construction"
        <| fun _ ->
            let regs = [
                {
                    Module = Some "MyMod"
                    Definition = {
                        Name = "toolup.foo.bar"
                        Kind = Counter
                        Description = "reserved-prefix abuse"
                        Unit = "1"
                        Tags = []
                    }
                }
            ]

            Expect.throws
                (fun () -> factory MetricsSinkConfig.defaults regs noopLogger |> ignore)
                "module-scoped registration with the reserved toolup. prefix must be rejected at construction"

            // A None-module (SDK-owned) registration legitimately
            // carries the `toolup.` prefix — it must NOT be rejected.
            let sdkRegs = [
                {
                    Module = None
                    Definition = {
                        Name = "toolup.sdk.ok"
                        Kind = Counter
                        Description = "SDK-owned, prefix allowed"
                        Unit = "1"
                        Tags = []
                    }
                }
            ]

            let _sink, _render = factory MetricsSinkConfig.defaults sdkRegs noopLogger
            ()
    ]