module ToolUp.Platform.Tests.InProcess.TelemetryZeroCostGateTests

open System.IO
open Expecto

// ─── Phase 69l — Telemetry seam zero-cost gate (GP 13 alignment) ──
//
// `Api.make`'s default-bridge composition pairs `defaultMetricsBridge`
// with `defaultBridgeGate` so the per-request `Stopwatch` +
// `MethodTelemetry` allocation is skipped entirely when the registered
// `IMetricsSink` resolves to `NoOpMetricsSink` (the `FeatureMode.Disabled`
// default). Source plan: application-plans/toolup-remoting-hot-path-perf.md,
// Finding F1. Acceptance criterion: the source-level composition is in
// place AND the dispatcher honours the gate before allocation.
//
// These checks are textual — same shape as the WithRequestHeadersPassthrough
// and SubjectWildcardAnalyzer test packs. An integration-shape allocation-pin
// test (compose a tracking `IRemotingTelemetry`, dispatch N requests through
// a TestServer, assert zero `OnMethodCompleted` calls when the gate returns
// false) is the right next step but requires a TestServer scaffold this
// repo does not have today. Tracked as a follow-up TIDY-UP item under
// `Bundle: ToolUp.Remoting per-request cleanups`.
//
// Failing any test here means a future edit either dropped the gate
// composition in `Api.fs` or routed around it in the dispatcher — the
// GP 13 alignment regresses silently otherwise.

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
    // bin/Debug/net10.0/ToolUp.Platform.Tests.dll → repo root
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

[<Tests>]
let tests =
    testList "Phase 69l — Telemetry seam zero-cost gate" [

        test "RemotingOptions carries TelemetryGate field" {
            let typesPath =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Server", "Server", "Remoting", "Types.fs")

            Expect.isTrue (File.Exists typesPath) (sprintf "expected Types.fs at %s" typesPath)
            let contents = File.ReadAllText typesPath

            Expect.stringContains
                contents
                "TelemetryGate: (unit -> bool) option"
                "RemotingOptions must declare the Phase 69l TelemetryGate field. \
                 Removing it would silently re-introduce the per-request \
                 Stopwatch + MethodTelemetry allocation on every Api.make-composed \
                 NoMetrics deployment."
        }

        test "Remoting.withTelemetryGate setter exists" {
            let remotingPath =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Server", "Server", "Remoting", "Remoting.fs")

            let contents = File.ReadAllText remotingPath

            Expect.stringContains
                contents
                "let withTelemetryGate (gate: unit -> bool)"
                "Remoting.withTelemetryGate must remain exposed — it's the seam Api.make \
                 composes against the default bridge. A consumer wanting to pair their \
                 own IRemotingTelemetry with a gate also reaches for this."
        }

        test "Remoting.createApi defaults TelemetryGate to None" {
            let remotingPath =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Server", "Server", "Remoting", "Remoting.fs")

            let contents = File.ReadAllText remotingPath

            Expect.stringContains
                contents
                "TelemetryGate = None"
                "createApi must default TelemetryGate to None so the pre-69l emit-whenever-Telemetry-Some \
                 contract holds for consumers that don't compose a gate."
        }

        test "ApiSeams.defaultBridgeGate exists" {
            let apiPath =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Server", "Server", "Api.fs")

            let contents = File.ReadAllText apiPath

            Expect.stringContains
                contents
                "let defaultBridgeGate () : bool"
                "ApiSeams.defaultBridgeGate is the singleton gate Api.make composes against the \
                 default IMetricsSink-bridging IRemotingTelemetry. Removing it (or renaming \
                 without updating the Api.make composition) reverts GP 13 alignment."
        }

        test "ApiSeams.defaultBridgeGate detects NoOpMetricsSink" {
            let apiPath =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Server", "Server", "Api.fs")

            let contents = File.ReadAllText apiPath

            Expect.stringContains
                contents
                ":? NoOpMetricsSink -> false"
                "The gate must short-circuit when the registered IMetricsSink is NoOpMetricsSink — \
                 that's the entire mechanism by which Api.make + FeatureMode.Disabled becomes zero-cost."
        }

        test "Api.make composes withTelemetryGate only on the default bridge" {
            let apiPath =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Server", "Server", "Api.fs")

            let contents = File.ReadAllText apiPath

            Expect.stringContains
                contents
                "Remoting.withTelemetryGate ApiSeams.defaultBridgeGate"
                "Api.make's pipeline must compose the default-bridge gate. Without this, \
                 the gate field is set but never wired and the dispatcher continues to allocate."

            // Defence-in-depth: the gate composition is conditional on
            // `telemetryIsDefault`. Consumer-supplied sinks must not be
            // gated (the consumer opted in to telemetry).
            Expect.stringContains
                contents
                "if telemetryIsDefault then"
                "Gate composition must be conditional on telemetryIsDefault — \
                 consumer-supplied IRemotingTelemetry sinks emit unconditionally."
        }

        test "the one telemetry scope reads the sink and its gate before allocating" {
            let telemetryPath =
                Path.Combine(
                    repoRoot (),
                    "src",
                    "ToolUp.Platform.Server",
                    "Server",
                    "Remoting",
                    "Stages",
                    "Telemetry.fs"
                )

            let contents = File.ReadAllText telemetryPath
            let startIdx = contents.IndexOf "let start ("

            Expect.isGreaterThan startIdx -1 "the pipeline must start its telemetry in one place"

            let startScope = contents.Substring(startIdx, min 600 (contents.Length - startIdx))

            Expect.stringContains
                startScope
                "| Some isOpen -> isOpen ()"
                "the gate must be consulted — without it the gate field is dead code"

            Expect.stringContains
                startScope
                "| _ -> None"
                "no sink, or a shut gate, starts nothing: the scope is `None` and nothing is allocated (GP 13)"

            Expect.isLessThan
                (startScope.IndexOf "isOpen ()")
                (startScope.IndexOf "Stopwatch.GetTimestamp()")
                "the clock is read only after the gate opens"
        }

        test "every call, streaming included, starts the same scope" {
            let contextPath =
                Path.Combine(
                    repoRoot (),
                    "src",
                    "ToolUp.Platform.Server",
                    "Server",
                    "Remoting",
                    "Stages",
                    "RequestContext.fs"
                )

            Expect.stringContains
                (File.ReadAllText contextPath)
                "Telemetry = TelemetryScope.start mount.Telemetry mount.TelemetryGate"
                "the request context starts the call's one telemetry scope, whatever the method's shape"
        }
    ]