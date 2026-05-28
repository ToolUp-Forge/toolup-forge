module ToolUp.AI.AILatencyHandler

open System
open Newtonsoft.Json
open Giraffe
open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Phase 6i.A — `/dev/ai-latency` rolling-window telemetry ────
//
// Mirrors the Phase 6j.A `/dev/ai-fastpath` shape: read from
// `IEventStore.ReadBySource(_, AILatencyRecord.SourceModule)`
// filtered to the caller's scope (cross-team enumeration is
// structurally impossible — GP 4) and roll up p50 / p95 / p99
// latency across three breakdown axes:
//
//   - **Per provider/model** — which (provider, model) pair is
//     slowest? Surfaces "Sonnet's TtftMs is 2× Haiku's" sort of
//     finding without per-deployment instrumentation.
//   - **Per tool name** — which tool is dragging tail latency?
//     Includes error rate so "tools that fail are slow" stands out.
//   - **Server vs client tool** — gross split across Phase 6g.A's
//     server-resident vs client-resident tools. Validates the
//     90s client timeout is sensibly tuned for the long tail.
//
// **Activation gate:** `ServerConfig.EnableDevEndpoints = true`
// (mirrors `/dev/inspect`). The previous compile-time `#if DEBUG` gate
// in `composeWithAI` was removed when ToolUp.AI stopped carrying
// compile-time gates; the runtime flag is now the sole gate.
//
// **Wire format.** Hand-shaped DTO of primitives + lists. No F# DUs
// round-trip; the report is for humans, not Fable.

[<Literal>]
let private SourceModule = "_platform.ai.latency"

/// Rolling window for percentile stats. Matches the 60-minute
/// window `FastPathTelemetryHandler` uses so an operator can read
/// `/dev/ai-fastpath` and `/dev/ai-latency` against a comparable
/// time slice.
let private rollingWindow = TimeSpan.FromMinutes 60.0

// ─── JSON deserialiser (must match `latencyJsonSettings` in
//     AIAgentEngine.fs — both rely on `FableJsonConverter` to
//     round-trip `option` and DUs). ─────────────────────────────

let private payloadJsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(Fable.Remoting.Json.FableJsonConverter())
    s

// ─── Wire shape (kept local — duplicates `AILatencyRecord` /
//     `ToolCallTiming` fields in DTO form so the JSON shape we
//     serialise to clients is decoupled from the `Shared/` record
//     type, mirroring how `FastPathTelemetryHandler` decouples its
//     read DTO from `FastPathBeacon`). ───────────────────────────

type private ProviderModelBreakdown = {
    ProviderName: string
    ProviderModel: string
    Count: int
    P50TurnMs: float
    P95TurnMs: float
    P99TurnMs: float
    P50TtftMs: float option
    P95TtftMs: float option
    /// Phase 6i.B: average per-record `CachedPromptTokens / PromptTokens`
    /// across records in the bucket where both values are populated and
    /// `PromptTokens > 0`. `None` when no record qualifies (e.g., every
    /// record is pre-6i.B and has `None` token fields, or the provider
    /// declares `SupportsPromptCaching = false`). Per-record averaging
    /// (rather than sum-over-sum) keeps a single huge turn from
    /// dominating the ratio.
    CacheHitRate: float option
}

type private ToolNameBreakdown = {
    Name: string
    Count: int
    P50DurationMs: float
    P95DurationMs: float
    P99DurationMs: float
    ErrorRate: float
}

type private LocationBreakdown = {
    Location: string
    Count: int
    P50DurationMs: float
    P95DurationMs: float
    P99DurationMs: float
}

type private LatencyReport = {
    ScopeId: string
    GeneratedAt: DateTime
    WindowMinutes: int
    TotalTurnsAllTime: int
    TotalTurnsInWindow: int
    PerProviderModel: ProviderModelBreakdown list
    PerToolName: ToolNameBreakdown list
    ServerVsClientTool: LocationBreakdown list
}

// ─── Statistics ─────────────────────────────────────────────────

let private percentile (values: float[]) (p: float) : float =
    if values.Length = 0 then
        0.0
    else
        let sorted = values |> Array.sort
        let rank = int (Math.Ceiling(p * float sorted.Length)) - 1
        let clamped = max 0 (min (sorted.Length - 1) rank)
        sorted[clamped]

/// Optional percentile — returns `None` when there are no
/// non-`None` samples (every turn was tool-only). Otherwise computes
/// the percentile across the populated values.
let private percentileOpt (values: float option[]) (p: float) : float option =
    let populated = values |> Array.choose id

    if populated.Length = 0 then
        None
    else
        Some(percentile populated p)

// ─── Event read + decode ────────────────────────────────────────

let private decodePayload (evt: ModuleEvent) : (AILatencyRecord * DateTime) option =
    try
        let payload =
            JsonConvert.DeserializeObject<AILatencyRecord>(evt.Payload, payloadJsonSettings)

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

// ─── Breakdown builders ─────────────────────────────────────────

let private buildPerProviderModel (records: AILatencyRecord list) : ProviderModelBreakdown list =
    records
    |> List.groupBy (fun r -> r.ProviderName, r.ProviderModel)
    |> List.map (fun ((providerName, model), bucket) ->
        let turnMs = bucket |> List.map _.TurnDurationMs |> List.toArray
        let ttftMs = bucket |> List.map _.TtftMs |> List.toArray

        let cachedRatios =
            bucket
            |> List.choose (fun r ->
                match r.PromptTokens, r.CachedPromptTokens with
                | Some pTokens, Some cTokens when pTokens > 0 -> Some(float cTokens / float pTokens)
                | _ -> None)

        {
            ProviderName = providerName
            ProviderModel = model
            Count = bucket.Length
            P50TurnMs = percentile turnMs 0.50
            P95TurnMs = percentile turnMs 0.95
            P99TurnMs = percentile turnMs 0.99
            P50TtftMs = percentileOpt ttftMs 0.50
            P95TtftMs = percentileOpt ttftMs 0.95
            CacheHitRate =
                if cachedRatios.IsEmpty then
                    None
                else
                    Some(List.average cachedRatios)
        })
    |> List.sortByDescending _.Count

let private buildPerToolName (records: AILatencyRecord list) : ToolNameBreakdown list =
    records
    |> List.collect _.ToolCalls
    |> List.groupBy _.Name
    |> List.map (fun (name, bucket) ->
        let durations = bucket |> List.map _.DurationMs |> List.toArray
        let errored = bucket |> List.filter _.Errored |> List.length

        {
            Name = name
            Count = bucket.Length
            P50DurationMs = percentile durations 0.50
            P95DurationMs = percentile durations 0.95
            P99DurationMs = percentile durations 0.99
            ErrorRate =
                if bucket.Length = 0 then
                    0.0
                else
                    float errored / float bucket.Length
        })
    |> List.sortByDescending _.Count

let private locationLabel (loc: ToolExecutionLocation) =
    match loc with
    | ServerSide -> "ServerSide"
    | ClientSide -> "ClientSide"

let private buildServerVsClient (records: AILatencyRecord list) : LocationBreakdown list =
    records
    |> List.collect _.ToolCalls
    |> List.groupBy _.Location
    |> List.map (fun (location, bucket) ->
        let durations = bucket |> List.map _.DurationMs |> List.toArray

        {
            Location = locationLabel location
            Count = bucket.Length
            P50DurationMs = percentile durations 0.50
            P95DurationMs = percentile durations 0.95
            P99DurationMs = percentile durations 0.99
        })
    |> List.sortByDescending _.Count

// ─── Report builder ─────────────────────────────────────────────

let private buildReport (ctx: HttpContext) : Async<LatencyReport> = async {
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
        | Some store -> store.ReadBySource(scope.ScopeId, SourceModule)

    let decoded = events |> List.choose decodePayload

    let inWindow =
        decoded
        |> List.filter (fun (_, occurredAt) -> occurredAt >= windowStart)
        |> List.map fst

    return {
        ScopeId = scope.ScopeId
        GeneratedAt = now
        WindowMinutes = int rollingWindow.TotalMinutes
        TotalTurnsAllTime = decoded.Length
        TotalTurnsInWindow = inWindow.Length
        PerProviderModel = buildPerProviderModel inWindow
        PerToolName = buildPerToolName inWindow
        ServerVsClientTool = buildServerVsClient inWindow
    }
}

// ─── Route handler ──────────────────────────────────────────────

let private renderJson (report: LatencyReport) =
    JsonConvert.SerializeObject(report, Formatting.Indented)

/// JSON handler for `/dev/ai-latency`. Sets `Cache-Control: no-store`
/// so dev-tools sees fresh stats on every refresh.
let private latencyHandler: HttpHandler =
    fun next ctx -> task {
        let! report = buildReport ctx
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.Headers["Cache-Control"] <- "no-store"
        do! ctx.Response.WriteAsync(renderJson report)
        return! next ctx
    }

/// Route for the AI latency telemetry endpoint. Mounted only when
/// `ServerConfig.EnableDevEndpoints = true` by `composeWithAI`.
let routes: HttpHandler list = [ route "/dev/ai-latency" >=> latencyHandler ]