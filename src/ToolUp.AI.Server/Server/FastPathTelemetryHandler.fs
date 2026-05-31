module ToolUp.AI.FastPathTelemetryHandler

open System
open Newtonsoft.Json
open Giraffe
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.StorageScopeResolver
open ToolUp.AI.FastPathBeaconHandler

// ─── Phase 6j.A — `/dev/ai-fastpath` telemetry endpoint ─────────
//
// Mirrors the Phase 9a `/dev/inspect` shape. Returns rolling-window
// p50 / p95 / total-count counts broken down by `Tier × ModuleId`.
// Reads from `IEventStore.ReadBySource(_, "_platform.ai.fastpath")`
// filtered to the caller's scope so cross-team enumeration is
// structurally impossible (GP 4).
//
// **Activation gate:** `ServerConfig.EnableDevEndpoints = true`
// (mirrors `/dev/inspect`). The previous compile-time `#if DEBUG` gate
// in `composeWithAI` was removed when ToolUp.AI stopped carrying
// compile-time gates; the runtime flag is now the sole gate.
//
// **Wire format.** Hand-shaped DTO of primitives + lists. No F# DUs
// round-trip through (System.Text.Json, the Giraffe default,
// produces a clean JSON shape for `curl` / browser).

[<Literal>]
let private FastPathSourceModule = "_platform.ai.fastpath"

[<Literal>]
let private FastPathResolvedEventType = "FastPathResolved"

[<Literal>]
let private SequencedClauseEventType = "SequencedFastPathClause"

[<Literal>]
let private SequenceOutcomeEventType = "SequencedFastPathOutcome"

// ─── Phase 6j.G — sequencer outcome vocabulary ───────────────────
//
// The resolver companion's executor emits one outcome string per
// sequence; the telemetry rollup buckets them three ways:
//
//   * `"all-resolved"` → counted in `sequencedHits`.
//   * `"partial-fall-through"` / `"sequence-capped"` /
//     `"handler-failed-mid-sequence"` / `"handler-timed-out-mid-sequence"`
//     → counted in the numerator of `sequencedFallThroughRate`.
//   * `"paused-mid-sequence"` / `"taken-over-mid-sequence"` → counted in
//     the denominator (they're sequence attempts that started
//     dispatching) but NOT in the fall-through numerator (they're user-
//     intent interruptions, not resolver misses). Showing them as
//     fall-throughs would bias the operator's tuning signal — the user
//     paused, the sequencer didn't fail.
//
// `MeanClausesPerSequence` is the rolling-window mean over the
// `ClauseCount` field across every outcome event.

[<Literal>]
let private OutcomeAllResolved = "all-resolved"

let private fallThroughOutcomes =
    Set.ofList [
        "partial-fall-through"
        "sequence-capped"
        "handler-failed-mid-sequence"
        "handler-timed-out-mid-sequence"
    ]

/// Rolling window for p50/p95 latency stats. The endpoint reports
/// both the rolling-window slice and the all-time count so an
/// operator sees both the recent rate and the historical total.
let private rollingWindow = TimeSpan.FromMinutes 60.0

// ─── Wire shape (matches FastPathBeaconHandler.FastPathEventPayload
//     so the deserialise path round-trips). ─────────────────────

type private FastPathEventPayload = {
    Tier: int
    ModuleId: string
    FieldName: string
    Instruction: string
    SyntheticReply: string
    PatternMatched: string
    LatencyMs: float
    JsonFragment: string
    ConversationId: Guid
}

// ─── Report DTO ─────────────────────────────────────────────────

type private TierBreakdown = {
    Tier: int
    ModuleId: string
    Count: int
    P50LatencyMs: float
    P95LatencyMs: float
    MinLatencyMs: float
    MaxLatencyMs: float
}

type private FastPathReport = {
    ScopeId: string
    GeneratedAt: DateTime
    WindowMinutes: int
    TotalCountAllTime: int
    TotalCountInWindow: int
    PerTierModule: TierBreakdown list
    // ── Phase 6j.G sequencer keys ──────────────────────────────
    /// Count of `"all-resolved"` sequence-outcome beacons in the
    /// rolling window. Each one represents a sequenced instruction
    /// the resolver dispatched without falling through to the LLM.
    SequencedHits: int
    /// Share of sequence attempts (in-window) that ended in a
    /// resolver-side miss: partial-fall-through, sequence-capped,
    /// mid-sequence handler failure, or mid-sequence handler timeout.
    /// User-driven interrupts (`paused-mid-sequence` /
    /// `taken-over-mid-sequence`) sit in the denominator but not the
    /// numerator — they aren't resolver failures and shouldn't bias
    /// the tuning signal. `0.0` when no sequence attempts in window.
    SequencedFallThroughRate: float
    /// Rolling-window mean over the `ClauseCount` field on every
    /// sequence-outcome beacon. `0.0` when no sequence attempts in
    /// window. Drives 8-clause-cap tuning from data.
    MeanClausesPerSequence: float
}

/// Public rollup helper — pure function over decoded outcome events,
/// exposed so the Expecto test suite can construct synthetic events
/// and assert the three sequencer keys without spinning up the full
/// Giraffe pipeline. The wire-level decode path stays private.
let computeSequencerRollup
    (outcomes: SequenceOutcomeBeacon list)
    : {|
          SequencedHits: int
          SequencedFallThroughRate: float
          MeanClausesPerSequence: float
      |}
    =
    let total = outcomes.Length

    if total = 0 then
        {|
            SequencedHits = 0
            SequencedFallThroughRate = 0.0
            MeanClausesPerSequence = 0.0
        |}
    else
        let hits =
            outcomes |> List.filter (fun o -> o.Outcome = OutcomeAllResolved) |> List.length

        let fallThroughs =
            outcomes
            |> List.filter (fun o -> Set.contains o.Outcome fallThroughOutcomes)
            |> List.length

        let meanClauses =
            (outcomes |> List.sumBy (fun o -> float o.ClauseCount)) / float total

        {|
            SequencedHits = hits
            SequencedFallThroughRate = float fallThroughs / float total
            MeanClausesPerSequence = meanClauses
        |}

// ─── Statistics ─────────────────────────────────────────────────

let private percentile (values: float[]) (p: float) : float =
    if values.Length = 0 then
        0.0
    else
        let sorted = values |> Array.sort
        let rank = int (System.Math.Ceiling(p * float sorted.Length)) - 1
        let clamped = max 0 (min (sorted.Length - 1) rank)
        sorted[clamped]

let private buildBreakdown (events: (FastPathEventPayload * DateTime) list) : TierBreakdown list =
    events
    |> List.groupBy (fun (p, _) -> p.Tier, p.ModuleId)
    |> List.map (fun ((tier, moduleId), bucket) ->
        let latencies = bucket |> List.map (fun (p, _) -> p.LatencyMs) |> List.toArray

        {
            Tier = tier
            ModuleId = moduleId
            Count = bucket.Length
            P50LatencyMs = percentile latencies 0.50
            P95LatencyMs = percentile latencies 0.95
            MinLatencyMs = if latencies.Length = 0 then 0.0 else Array.min latencies
            MaxLatencyMs = if latencies.Length = 0 then 0.0 else Array.max latencies
        })
    |> List.sortByDescending _.Count

// ─── Event-store read + decode ──────────────────────────────────

let private payloadJsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(Fable.Remoting.Json.FableJsonConverter())
    s

let private decodePayload (evt: ModuleEvent) : (FastPathEventPayload * DateTime) option =
    try
        let payload =
            JsonConvert.DeserializeObject<FastPathEventPayload>(evt.Payload, payloadJsonSettings)

        if isNull (box payload) then
            None
        else
            Some(payload, evt.OccurredAt)
    with _ ->
        None

let private decodeSequenceOutcome (evt: ModuleEvent) : (SequenceOutcomeBeacon * DateTime) option =
    try
        let payload =
            JsonConvert.DeserializeObject<SequenceOutcomeBeacon>(evt.Payload, payloadJsonSettings)

        if isNull (box payload) then
            None
        else
            Some(payload, evt.OccurredAt)
    with _ ->
        None

let private resolveScope (ctx: HttpContext) : StorageScope =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as s) -> s
    | _ ->
        let fallback =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        {
            ScopeId = fallback
            Container = $"user-{fallback}"
            Persist = true
        }

let private buildReport (ctx: HttpContext) : Async<FastPathReport> = async {
    let scope = resolveScope ctx
    let now = DateTime.UtcNow
    let windowStart = now - rollingWindow

    let eventStoreOpt =
        match ctx.RequestServices.GetService(typeof<IEventStore>) with
        | :? IEventStore as s -> Some s
        | _ -> None

    let! events =
        match eventStoreOpt with
        | None -> async { return [] }
        | Some store -> store.ReadBySource(scope.ScopeId, FastPathSourceModule)

    // Phase 6j.G — dispatch by `EventType` so the rollup walks
    // `FastPathResolved` events into the per-tier breakdown and
    // `SequencedFastPathOutcome` events into the sequencer rollup
    // without trying to deserialise either as the other's shape.
    // `SequencedFastPathClause` events are written for offline
    // analysis but don't feed the current `/dev/ai-fastpath` keys;
    // they're skipped here.
    let resolvedDecoded =
        events
        |> List.filter (fun evt -> evt.EventType = FastPathResolvedEventType)
        |> List.choose decodePayload

    let resolvedInWindow =
        resolvedDecoded
        |> List.filter (fun (_, occurredAt) -> occurredAt >= windowStart)

    let outcomeInWindow =
        events
        |> List.filter (fun evt -> evt.EventType = SequenceOutcomeEventType && evt.OccurredAt >= windowStart)
        |> List.choose decodeSequenceOutcome
        |> List.map fst

    let sequencerRollup = computeSequencerRollup outcomeInWindow

    return {
        ScopeId = scope.ScopeId
        GeneratedAt = now
        WindowMinutes = int rollingWindow.TotalMinutes
        TotalCountAllTime = resolvedDecoded.Length
        TotalCountInWindow = resolvedInWindow.Length
        PerTierModule = buildBreakdown resolvedInWindow
        SequencedHits = sequencerRollup.SequencedHits
        SequencedFallThroughRate = sequencerRollup.SequencedFallThroughRate
        MeanClausesPerSequence = sequencerRollup.MeanClausesPerSequence
    }
}

// ─── Route handler ──────────────────────────────────────────────

let private renderJson (report: FastPathReport) =
    JsonConvert.SerializeObject(report, Formatting.Indented)

/// JSON handler for `/dev/ai-fastpath`. Sets `Cache-Control: no-store`
/// so dev-tools sees fresh stats on every refresh.
let private telemetryHandler: HttpHandler =
    fun next ctx -> task {
        let! report = buildReport ctx
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.Headers["Cache-Control"] <- "no-store"
        do! ctx.Response.WriteAsync(renderJson report)
        return! next ctx
    }

/// Route for the fast-path telemetry endpoint. Mounted only when
/// `ServerConfig.EnableDevEndpoints = true` by `composeWithAI`.
let routes: HttpHandler list = [ route "/dev/ai-fastpath" >=> telemetryHandler ]