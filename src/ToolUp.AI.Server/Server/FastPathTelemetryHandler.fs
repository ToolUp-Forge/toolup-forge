module ToolUp.AI.FastPathTelemetryHandler

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
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

// ─── Phase 6j.B — Tier-3 triage rollup ───────────────────────────
//
// The triage resolver writes one row per ATTEMPT under its own
// `EventType` on this same source, carrying the outcome token. Rolling
// it up here rather than on a new endpoint is deliberate: an operator
// tuning the fast path is asking one question — "how much of the
// instruction traffic is resolving below the agent loop, and where is
// the rest going" — and Tier 1 and Tier 3 are two answers to it.
// Splitting them across two endpoints would make the comparison a
// manual join.
//
// The keys are chosen so a *failing* tier is legible, not just a
// working one. `TriageHits / TriageAttempts` is the value the tier
// buys; `TriageOutcomes` is where the rest went, and the difference
// between a healthy `needs-full-agent` majority and a pile of
// `unparseable` / `unknown-field` is the difference between "triage is
// correctly declining" and "triage is broken and silently costing a
// call per turn". A single hit-rate number cannot tell those apart.

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
    // ── Phase 6j.B Tier-3 triage keys ──────────────────────────
    /// Triage attempts in the rolling window — turns that actually
    /// reached the triage provider call. Turns that were never
    /// candidates (triage disabled, no declared fields, a question
    /// rather than a command) are NOT counted: they cost nothing and
    /// including them would understate the hit rate of the tier as
    /// configured.
    TriageAttempts: int
    /// Attempts that resolved to a field set, so the full agent loop
    /// did not run.
    TriageHits: int
    /// `TriageHits / TriageAttempts`. `0.0` when no attempts in window.
    TriageHitRate: float
    /// Mean attempt duration in the window, provider call included —
    /// the number the tier's ~500 ms premise is checked against.
    /// `0.0` when no attempts in window.
    TriageMeanLatencyMs: float
    /// Every outcome token with its in-window count, in the resolver's
    /// declared order, including zeros. Zeros are included on purpose:
    /// a missing row and a zero row are the same shape to a reader who
    /// does not know the vocabulary, and the failure classes are worth
    /// showing as present-and-zero.
    TriageOutcomes: (string * int) list
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

/// Public rollup helper for the Phase 6j.B triage rows — pure over
/// decoded attempt payloads, exposed for the same reason
/// `computeSequencerRollup` is: the Expecto pack can construct
/// synthetic attempts and assert the keys without a Giraffe pipeline.
///
/// Counts every declared outcome token, including the ones with no
/// rows, so the shape of the report does not change as failure modes
/// come and go.
let computeTriageRollup
    (attempts: FastPathTriageResolver.TriageEventPayload list)
    : {|
          TriageAttempts: int
          TriageHits: int
          TriageHitRate: float
          TriageMeanLatencyMs: float
          TriageOutcomes: (string * int) list
      |}
    =
    let total = attempts.Length

    let counts =
        FastPathTriageResolver.outcomes
        |> List.map (fun token -> token, attempts |> List.filter (fun a -> a.Outcome = token) |> List.length)

    let hits =
        counts
        |> List.tryFind (fun (token, _) -> token = FastPathTriageResolver.OutcomeHit)
        |> Option.map snd
        |> Option.defaultValue 0

    {|
        TriageAttempts = total
        TriageHits = hits
        TriageHitRate = if total = 0 then 0.0 else float hits / float total
        TriageMeanLatencyMs =
            if total = 0 then
                0.0
            else
                (attempts |> List.sumBy _.LatencyMs) / float total
        TriageOutcomes = counts
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

let private payloadJsonOptions = FableConverters.create ()

let private indentedJsonOptions =
    let o = FableConverters.create ()
    o.WriteIndented <- true
    o

let private decodePayload (evt: ModuleEvent) : (FastPathEventPayload * DateTime) option =
    try
        let payload =
            JsonSerializer.Deserialize<FastPathEventPayload>(evt.Payload, payloadJsonOptions)

        if isNull (box payload) then
            None
        else
            Some(payload, evt.OccurredAt)
    with _ ->
        None

let private decodeTriageAttempt (evt: ModuleEvent) : (FastPathTriageResolver.TriageEventPayload * DateTime) option =
    try
        let payload =
            JsonSerializer.Deserialize<FastPathTriageResolver.TriageEventPayload>(evt.Payload, payloadJsonOptions)

        if isNull (box payload) then
            None
        else
            Some(payload, evt.OccurredAt)
    with _ ->
        None

let private decodeSequenceOutcome (evt: ModuleEvent) : (SequenceOutcomeBeacon * DateTime) option =
    try
        let payload =
            JsonSerializer.Deserialize<SequenceOutcomeBeacon>(evt.Payload, payloadJsonOptions)

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
    // `SequencedFastPathClause` and `SequencedFastPathResolved` (the
    // whole-sequence aggregate) events are written for offline
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

    let triageInWindow =
        events
        |> List.filter (fun evt ->
            evt.EventType = FastPathTriageResolver.TriageEventType
            && evt.OccurredAt >= windowStart)
        |> List.choose decodeTriageAttempt
        |> List.map fst

    let triageRollup = computeTriageRollup triageInWindow

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
        TriageAttempts = triageRollup.TriageAttempts
        TriageHits = triageRollup.TriageHits
        TriageHitRate = triageRollup.TriageHitRate
        TriageMeanLatencyMs = triageRollup.TriageMeanLatencyMs
        TriageOutcomes = triageRollup.TriageOutcomes
    }
}

// ─── Route handler ──────────────────────────────────────────────

let private renderJson (report: FastPathReport) =
    JsonSerializer.Serialize(report, indentedJsonOptions)

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