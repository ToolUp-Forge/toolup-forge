namespace ToolUp.Remoting.Server.Stages

open System.Diagnostics
open ToolUp.Remoting.Server

/// What a call settled as: the one telemetry event it emits.
[<NoComparison; NoEquality>]
type internal Settlement = {
    /// The routed method name (`<method>/status` for a companion).
    Method: string
    Outcome: MethodOutcome
    /// The chunks a streaming call wrote before its terminal frame.
    Chunks: int option
}

/// A call's telemetry, started when the request arrives and emitted once,
/// when the call settles.
[<NoComparison; NoEquality>]
type internal TelemetryScope = {
    Sink: IRemotingTelemetry
    Started: int64
}

[<RequireQualifiedAccess>]
module internal TelemetryScope =

    /// `None` when no sink is composed or its gate is shut: a deployment that
    /// does not observe calls allocates nothing for them (GP 13).
    let start (sink: IRemotingTelemetry option) (gate: (unit -> bool) option) : TelemetryScope option =
        match sink with
        | Some sink when
            (match gate with
             | Some isOpen -> isOpen ()
             | None -> true)
            ->
            Some {
                Sink = sink
                Started = Stopwatch.GetTimestamp()
            }
        | _ -> None

    /// Emit a settlement. A rate-limited call reports no elapsed time: its
    /// handler never ran.
    let emit (correlation: string) (settlement: Settlement) (scope: TelemetryScope) : unit =
        let elapsed =
            match settlement.Outcome with
            | MethodOutcome.RateLimited _ -> 0
            | _ -> int (Stopwatch.GetElapsedTime scope.Started).TotalMilliseconds

        scope.Sink.OnMethodCompleted {
            MethodName = settlement.Method
            ElapsedMs = elapsed
            Outcome = settlement.Outcome
            CorrelationId = Some correlation
            ChunkCount = settlement.Chunks
        }