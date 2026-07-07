module ToolUp.Platform.SseTraceContributor

open System

/// Phase 6h follow-up — Workstream B. `IDevDiagnosticsContributor`
/// implementation that surfaces `SSEConnectionManager.Snapshot()` on
/// `/dev/inspect` under the panel name `"SSE trace"`.
///
/// The panel shows two views the operator needs to diagnose the
/// "events go nowhere" class of bug:
///
/// 1. **Recent broadcasts** — last 100 entries from the ring buffer:
///    timestamp, scopeId, event kind, payload size, connection count,
///    dropped flag. A `Dropped = true` entry tells the operator
///    instantly that a publish hit zero subscribers.
///
/// 2. **Registered scopes** — current scopeId → connection count map.
///    Cross-referenced with `Recent broadcasts`, the operator can see
///    immediately when a broadcast targets a scopeId that no
///    connection has registered for. This is exactly the symptom of
///    the userId / scopeId mismatch bug fixed in commit `2b34202` —
///    that bug would have been a 5-second diagnosis instead of a
///    9-commit archaeology dig if this panel had existed.
type SseTraceContributor(manager: SSEConnectionManager) =
    interface IDevDiagnosticsContributor with
        member _.Contribute() = async {
            let snapshot = manager.Snapshot()

            // Render an anonymous record so the JSON shape is
            // self-describing (field names appear in the wire
            // payload). FableConverters handles anonymous
            // records, lists, Maps, and DateTime cleanly.
            let payload: obj =
                box {|
                    Broadcasts =
                        snapshot.Broadcasts
                        |> List.map (fun e -> {|
                            Timestamp = e.Timestamp.ToString("o")
                            ScopeId = e.ScopeId
                            EventKind = e.EventKind
                            PayloadBytes = e.PayloadBytes
                            ConnectionCount = e.ConnectionCount
                            Dropped = e.Dropped
                        |})
                    RegisteredScopes =
                        snapshot.RegisteredScopes
                        |> Map.toList
                        |> List.map (fun (scopeId, count) -> {|
                            ScopeId = scopeId
                            ConnectionCount = count
                        |})
                    // Phase 6l.D — running totals of scope-at-capacity
                    // refusals per scope. Operators see at a glance
                    // when a scope is hitting MaxSseConnectionsPerScope
                    // and may need an explicit cap raise.
                    RefusalCounts =
                        snapshot.RefusalCounts
                        |> Map.toList
                        |> List.map (fun (scopeId, count) -> {|
                            ScopeId = scopeId
                            RefusalCount = count
                        |})
                    // Headline counts so an operator can scan the
                    // top of the panel for "is anything dropped?"
                    // without reading every entry.
                    Summary = {|
                        TotalBroadcasts = snapshot.Broadcasts.Length
                        DroppedBroadcasts = snapshot.Broadcasts |> List.filter _.Dropped |> List.length
                        RegisteredScopeCount = snapshot.RegisteredScopes.Count
                        TotalConnections = snapshot.RegisteredScopes |> Map.toSeq |> Seq.sumBy snd
                        TotalRefusals = snapshot.RefusalCounts |> Map.toSeq |> Seq.sumBy snd
                    |}
                |}

            return ("SSE trace", payload)
        }