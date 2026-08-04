module ToolUp.RAG.RetrievalTracers

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.IRetrievalTracer

// ─── Tracer helpers ───────────────────────────────────────────────

/// SHA256 hex digest of `input` (lowercase, 64 chars). Used so the
/// retrieval-trace event can identify recurring queries (cache-like
/// patterns, hot queries, replay clusters) without ever storing
/// plaintext. Hashing is deterministic — equal inputs produce equal
/// digests across processes — so an admin UI can join historical
/// traces by hash even though the original strings are unrecoverable.
let hashQuery (input: string) =
    let bytes =
        if isNull input then
            Array.empty<byte>
        else
            Encoding.UTF8.GetBytes(input)

    use sha = SHA256.Create()
    let digest = sha.ComputeHash(bytes)
    let sb = StringBuilder(digest.Length * 2)

    for b in digest do
        sb.AppendFormat("{0:x2}", b) |> ignore

    sb.ToString()

// ─── No-op tracer ─────────────────────────────────────────────────

/// Default `IRetrievalTracer`. Discards every emission. Wired by
/// `composeWithRAG` when no concrete tracer is registered, so
/// `IRetrievalTracer` can be resolved unconditionally inside the pipeline.
type NoOpRetrievalTracer() =
    interface IRetrievalTracer with
        member _.Trace _ _ = async.Return()
        member _.Miss _ _ = async.Return()

// ─── Event-store tracer (default for Phase 14j) ───────────────────

/// `KnowledgeRetrieved` payload as serialised by `EventStoreRetrievalTracer`.
/// Distinct from `RetrievalTrace` because it must round-trip through JSON
/// — `VectorScope` is a DU (`FableConverters`-compatible) but we flatten
/// it here to a stable string form (`"platform"`, `"deployment"`, `"team:<id>"`)
/// so consumers don't need the SDK to read traces.
type KnowledgeRetrievedPayload = {
    QueryHash: string
    QueryLength: int
    RequestedScopes: string list
    PermittedScopes: string list
    TopK: int
    AdaptiveK: bool
    CandidatePoolSize: int
    TopScore: float
    DenseUsed: bool
    SparseUsed: bool
    RerankerName: string option
    LatencyMs: int64
    Stages: string list
    ResultCount: int
    /// Per-stage `(stageName, elapsedMs)` timings (Phase 122). Additive:
    /// pre-122 events lack the property and old readers ignore it.
    StageTimings: (string * float) list
    /// Query-rewrite decision (Phase 506) — one of the
    /// `QueryRewriteDecision` literals, or absent when no rewrite stage
    /// ran. Additive on the same terms as `StageTimings`: pre-506 events
    /// lack the property and old readers ignore it.
    RewriteDecision: string option
    /// SHA256 of the substituted query when the decision was `Rewritten`;
    /// absent otherwise. Same privacy contract as `QueryHash` — an
    /// operator sees that a rewrite happened, never what it said.
    RewrittenQueryHash: string option
}

/// `KnowledgeRetrievalMiss` payload — fired when post-filter retrieval
/// returns fewer than the configured `MissThreshold` matches. Distinct
/// from `KnowledgeRetrieved` so admin UIs can surface miss patterns
/// without scanning every trace.
type KnowledgeRetrievalMissPayload = {
    QueryHash: string
    QueryLength: int
    Scopes: string list
    MatchesAboveMinScore: int
    MinScoreThreshold: float
    TopScore: float
}

let private scopeToString (scope: VectorKnowledgeTypes.VectorScope) =
    match scope with
    | VectorKnowledgeTypes.Platform -> "platform"
    | VectorKnowledgeTypes.Deployment -> "deployment"
    | VectorKnowledgeTypes.Team teamId -> sprintf "team:%s" teamId
    | VectorKnowledgeTypes.User userId -> sprintf "user:%s" userId

let private traceJsonOptions = FableConverters.create ()

let private toTraceJson (value: 'T) =
    JsonSerializer.Serialize(value, traceJsonOptions)

/// Emits each trace as a `KnowledgeRetrieved` event under
/// `SourceModule = "_platform.retrieval"` via the configured `IEventStore`.
/// Failures are swallowed at `Warn` — tracing must never fail retrieval.
type EventStoreRetrievalTracer(eventStore: IEventStore, logger: ILogger) =
    interface IRetrievalTracer with
        member _.Trace trace ctx = async {
            try
                let payload: KnowledgeRetrievedPayload = {
                    QueryHash = trace.QueryHash
                    QueryLength = trace.QueryLength
                    RequestedScopes = trace.RequestedScopes |> List.map scopeToString
                    PermittedScopes = trace.PermittedScopes |> List.map scopeToString
                    TopK = trace.TopK
                    AdaptiveK = trace.AdaptiveK
                    CandidatePoolSize = trace.CandidatePoolSize
                    TopScore = trace.TopScore
                    DenseUsed = trace.DenseUsed
                    SparseUsed = trace.SparseUsed
                    RerankerName = trace.RerankerName
                    LatencyMs = trace.LatencyMs
                    Stages = trace.Stages
                    ResultCount = trace.ResultCount
                    StageTimings = trace.StageTimings
                    RewriteDecision = trace.RewriteDecision
                    RewrittenQueryHash = trace.RewrittenQueryHash
                }

                // Persist to the caller's resolved scope when there is one;
                // anonymous / unscoped requests fall back to `_platform`.
                let scopeId =
                    match ctx.TeamId with
                    | Some teamId -> sprintf "team-%s" teamId
                    | None -> "_platform"

                let evt =
                    Events.create scopeId RetrievalTraceSourceModule KnowledgeRetrievedEventType (toTraceJson payload)

                do! eventStore.Write evt
            with ex ->
                logger.Warn(sprintf "[RetrievalTracer] write failed: %s" ex.Message)
        }

        member _.Miss miss ctx = async {
            try
                let payload: KnowledgeRetrievalMissPayload = {
                    QueryHash = miss.QueryHash
                    QueryLength = miss.QueryLength
                    Scopes = miss.Scopes |> List.map scopeToString
                    MatchesAboveMinScore = miss.MatchesAboveMinScore
                    MinScoreThreshold = miss.MinScoreThreshold
                    TopScore = miss.TopScore
                }

                let scopeId =
                    match ctx.TeamId with
                    | Some teamId -> sprintf "team-%s" teamId
                    | None -> "_platform"

                let evt =
                    Events.create
                        scopeId
                        RetrievalTraceSourceModule
                        KnowledgeRetrievalMissEventType
                        (toTraceJson payload)

                do! eventStore.Write evt
            with ex ->
                logger.Warn(sprintf "[RetrievalTracer] miss write failed: %s" ex.Message)
        }

let createNoOp () : IRetrievalTracer = NoOpRetrievalTracer() :> _

let createEventStore (eventStore: IEventStore) (logger: ILogger) : IRetrievalTracer =
    EventStoreRetrievalTracer(eventStore, logger) :> _