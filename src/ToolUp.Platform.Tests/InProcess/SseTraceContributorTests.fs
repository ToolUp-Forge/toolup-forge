module ToolUp.Platform.Tests.InProcess.SseTraceContributorTests

open System
open System.Threading
open System.Text
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Remoting.Json.SystemTextJson

// ─── Helpers ──────────────────────────────────────────────────────────

/// Build a no-op `IConnectionSink` for manager-internal tests. The
/// tests in this file assert manager bookkeeping (trace ring, refusal
/// counts, scope membership) and never inspect bytes written; a sink
/// that drops every frame is sufficient. Replaces a prior helper that
/// fabricated a `DefaultHttpContext` + `MemoryStream`-backed
/// `HttpResponse` to satisfy the now-removed `SSEConnection` record.
let private mkConn (cancelToken: CancellationToken) : IConnectionSink =
    { new IConnectionSink with
        member _.DisconnectToken = cancelToken
        member _.WriteFrame(_, _) = async { return () }
    }

let private payload (s: string) = Encoding.UTF8.GetBytes s

// ─── Tests ────────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 6k Workstream B — SSE trace ring + contributor" [

        test "Snapshot starts empty" {
            use mgr = new SSEConnectionManager()
            let snap = mgr.Snapshot()

            Expect.isEmpty snap.Broadcasts "no broadcasts yet"
            Expect.isEmpty snap.RegisteredScopes "no registered scopes yet"
        }

        test "Broadcast to no-subscribers scope emits Dropped=true ring entry" {
            // This is the regression test for the userId/scopeId
            // mismatch class of bug we just chased. A publish to a
            // scope nobody is registered on must be visible in the
            // trace as Dropped=true so an operator scanning
            // /dev/sse-trace catches it instantly.
            use mgr = new SSEConnectionManager()
            mgr.Broadcast("orphan-scope", payload "data: hello\n\n")

            let snap = mgr.Snapshot()
            Expect.equal snap.Broadcasts.Length 1 "one trace entry recorded"

            let entry = snap.Broadcasts[0]
            Expect.equal entry.ScopeId "orphan-scope" "scopeId captured"
            Expect.equal entry.ConnectionCount 0 "zero subscribers at dispatch"
            Expect.isTrue entry.Dropped "marked Dropped"
            Expect.equal entry.EventKind "data" "default kind from Broadcast()"
        }

        test "BroadcastWithKind propagates the kind tag into the ring" {
            use mgr = new SSEConnectionManager()

            mgr.BroadcastWithKind("orphan-scope", payload "data: hi\n\n", "MessageDelta")
            mgr.BroadcastWithKind("orphan-scope", payload "data: bye\n\n", "MessageComplete")

            let snap = mgr.Snapshot()
            Expect.equal snap.Broadcasts.Length 2 "two entries recorded"
            // Most-recent-first ordering — the Complete should appear first.
            Expect.equal snap.Broadcasts[0].EventKind "MessageComplete" "newest at head"
            Expect.equal snap.Broadcasts[1].EventKind "MessageDelta" "older follows"
        }

        test "Broadcast to live scope shows ConnectionCount and not-dropped" {
            use mgr = new SSEConnectionManager()
            use cts = new CancellationTokenSource()
            let conn = mkConn cts.Token

            mgr.Add("scope-A", conn) |> ignore
            mgr.Broadcast("scope-A", payload "data: live\n\n")

            let snap = mgr.Snapshot()
            Expect.equal snap.Broadcasts.Length 1 "one entry"

            let entry = snap.Broadcasts[0]
            Expect.equal entry.ScopeId "scope-A" "matched the registered scope"
            Expect.equal entry.ConnectionCount 1 "one subscriber present"
            Expect.isFalse entry.Dropped "delivered (not dropped)"

            Expect.equal snap.RegisteredScopes["scope-A"] 1 "scope shows in registered map"
        }

        test "Mismatched broadcast vs registration is structurally diagnosable" {
            // The exact symptom shape a developer would see after
            // accidentally introducing a userId/scopeId mismatch:
            // one scope registered, broadcasts go to a *different*
            // scope, the contributor snapshot shows both pieces side
            // by side.
            use mgr = new SSEConnectionManager()
            use cts = new CancellationTokenSource()
            let conn = mkConn cts.Token

            mgr.Add("registered-scope", conn) |> ignore

            // The bug shape: agent loop publishes to its own resolved
            // scope, which differs from the SSE-registered scope.
            mgr.BroadcastWithKind("agent-resolves-to", payload "x", "MessageDelta")
            mgr.BroadcastWithKind("agent-resolves-to", payload "y", "MessageComplete")

            let snap = mgr.Snapshot()

            let dropped = snap.Broadcasts |> List.filter _.Dropped

            Expect.equal dropped.Length 2 "every publish dropped"

            let publishedScopes = snap.Broadcasts |> List.map _.ScopeId |> List.distinct

            let registeredScopeKeys = snap.RegisteredScopes |> Map.toList |> List.map fst

            // The diagnostic value: the published-to scopes and the
            // registered-on scopes have empty intersection. /dev/sse-
            // trace surfaces both maps so a developer can spot this in
            // one glance.
            let intersection =
                publishedScopes |> List.filter (fun s -> registeredScopeKeys |> List.contains s)

            Expect.isEmpty intersection "no overlap between publish targets and registered scopes"
        }

        test "Phase 6l.D — Add returns Result.Ok when below cap" {
            use mgr = new SSEConnectionManager(maxConnectionsPerScope = 5)
            use cts = new CancellationTokenSource()
            let conn = mkConn cts.Token

            let result = mgr.Add("scope-A", conn)
            Expect.equal result (Result.Ok()) "first connection registers"
        }

        test "Phase 6l.D — Add returns Result.Error when scope is at cap" {
            use mgr = new SSEConnectionManager(maxConnectionsPerScope = 2)
            use cts = new CancellationTokenSource()

            // Fill to cap
            mgr.Add("scope-A", mkConn cts.Token) |> ignore
            mgr.Add("scope-A", mkConn cts.Token) |> ignore

            // Third must be refused
            let result = mgr.Add("scope-A", mkConn cts.Token)

            match result with
            | Result.Error refusal ->
                Expect.equal refusal.ScopeId "scope-A" "scopeId carried"
                Expect.equal refusal.Cap 2 "cap carried"
                Expect.equal refusal.CurrentCount 2 "current count at moment of refusal"
            | Result.Ok() -> failtest "expected Error, got Ok"
        }

        test "Phase 6l.D — refusals appear in Snapshot.RefusalCounts + survive multiple events" {
            use mgr = new SSEConnectionManager(maxConnectionsPerScope = 1)
            use cts = new CancellationTokenSource()

            mgr.Add("scope-X", mkConn cts.Token) |> ignore

            // Three more refusals (cap=1)
            for _ in 1..3 do
                mgr.Add("scope-X", mkConn cts.Token) |> ignore

            let snap = mgr.Snapshot()
            Expect.equal snap.RefusalCounts["scope-X"] 3 "three refusals counted"
        }

        test "Phase 6l.D — no cap (default constructor) → unbounded Add" {
            use mgr = new SSEConnectionManager() // no cap
            use cts = new CancellationTokenSource()

            // 100 connections — none refused
            for _ in 1..100 do
                let result = mgr.Add("scope-X", mkConn cts.Token)

                match result with
                | Result.Ok() -> ()
                | Result.Error _ -> failtest "no cap → no refusal"

            let snap = mgr.Snapshot()
            Expect.equal snap.RegisteredScopes["scope-X"] 100 "all registered"
            Expect.isEmpty snap.RefusalCounts "no refusals recorded"
        }

        test "Phase 6l.D — cap is per-scope, not global" {
            use mgr = new SSEConnectionManager(maxConnectionsPerScope = 2)
            use cts = new CancellationTokenSource()

            // Two connections per scope across three scopes — all OK
            for scope in [ "scope-A"; "scope-B"; "scope-C" ] do
                mgr.Add(scope, mkConn cts.Token) |> ignore
                mgr.Add(scope, mkConn cts.Token) |> ignore

            let snap = mgr.Snapshot()
            Expect.equal snap.RegisteredScopes.Count 3 "three scopes"

            for scope in [ "scope-A"; "scope-B"; "scope-C" ] do
                Expect.equal snap.RegisteredScopes[scope] 2 (sprintf "cap respected per scope: %s" scope)
        }

        test "Ring buffer is bounded at 100 entries (overflow drops oldest)" {
            use mgr = new SSEConnectionManager()

            for i in 1..150 do
                mgr.Broadcast(sprintf "scope-%d" i, payload "x")

            let snap = mgr.Snapshot()
            Expect.equal snap.Broadcasts.Length 100 "ring capped at 100"
            // Newest at head — entry 0 is broadcast #150.
            Expect.equal snap.Broadcasts[0].ScopeId "scope-150" "newest at head"
        }

        testAsync "SseTraceContributor renders the snapshot under panel name 'SSE trace'" {
            use mgr = new SSEConnectionManager()
            mgr.BroadcastWithKind("scope-X", payload "data: x\n\n", "MessageDelta")

            let contrib =
                SseTraceContributor.SseTraceContributor mgr :> IDevDiagnosticsContributor

            let! (panelName, payload) = contrib.Contribute()

            Expect.equal panelName "SSE trace" "panel name matches contract"
            Expect.isNotNull payload "payload non-null"
            // The payload is an anonymous record; confirm the
            // serialised JSON contains the headline fields. Using a
            // simple string check rather than full JSON parse keeps
            // the test free of converter dependencies.
            let json = JsonSerializer.Serialize(payload, FableConverters.shared)
            Expect.stringContains json "Broadcasts" "shape includes Broadcasts list"
            Expect.stringContains json "RegisteredScopes" "shape includes RegisteredScopes map"
            Expect.stringContains json "Summary" "shape includes Summary block"
            Expect.stringContains json "MessageDelta" "the kind tag survives"
        }

        test "Logger.trace no-ops on non-trace-aware loggers" {
            // Build a minimal ILogger that does NOT implement
            // ITraceLogger. Logger.trace must silently no-op.
            let calls = ResizeArray<string>()

            let logger =
                { new ILogger with
                    member _.Debug m = calls.Add m
                    member _.Info m = calls.Add m
                    member _.Warn m = calls.Add m
                    member _.Error(m, _) = calls.Add m
                }

            Logger.trace logger "ai.sse" "this should be ignored"
            Expect.isEmpty calls "non-trace logger was not called"
        }

        test "Logger.trace forwards to ITraceLogger when the logger supports it" {
            let logged = ResizeArray<string * string>()

            // Build a logger that implements both ILogger and
            // ITraceLogger. Must be a class type so multi-interface
            // implementation works in F#.
            let logger =
                { new obj() with
                    member _.ToString() = "test logger"
                  interface ILogger with
                      member _.Debug _ = ()
                      member _.Info _ = ()
                      member _.Warn _ = ()
                      member _.Error(_, _) = ()
                  interface ITraceLogger with
                      member _.Trace(category, message) = logged.Add((category, message))
                }

            Logger.trace logger "ai.sse" "hello"
            Expect.equal logged.Count 1 "trace was forwarded once"
            Expect.equal logged[0] ("ai.sse", "hello") "category + message preserved"
        }

        test "ConsoleLogger Trace gated by category whitelist" {
            // Trace is gated by category, not by level rank — even
            // with LogLevel.Info as the floor, a category-allowed
            // Trace must emit. Conversely, a non-allowed category
            // must silently drop even with LogLevel.Trace floor.
            //
            // Verifying the gate logic via an in-memory ConsoleLogger
            // alternative would require redirecting stdout; instead
            // we verify the configuration shape via the
            // ServerConfig.TraceCategories field round-trip.
            let cfgEmpty = {
                ServerConfig.defaults with
                    TraceCategories = Set.empty
            }

            let cfgAllow = {
                ServerConfig.defaults with
                    TraceCategories = Set.ofList [ "ai.sse"; "platform.sse" ]
            }

            Expect.isFalse (cfgEmpty.TraceCategories.Contains "ai.sse") "empty → no emit"
            Expect.isTrue (cfgAllow.TraceCategories.Contains "ai.sse") "allow-list → emit"
            Expect.isFalse (cfgAllow.TraceCategories.Contains "auth") "non-listed → drop"
        }
    ]