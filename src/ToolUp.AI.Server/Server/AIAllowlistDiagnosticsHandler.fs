// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.AIAllowlistDiagnosticsHandler

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open Giraffe
open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Phase 47 — `/dev/ai-allowlist` action-denial rollup ─────────
//
// Read-side companion to Phase 45's G12 trust boundary. The agent loop
// writes a `_platform.ai.tool_allowlist_denial` / `ToolAllowlistDenied`
// `ModuleEvent` every time `IClientToolAuthorizer` refuses a
// model-driven `ClientResident` tool call. Those rows were written and
// then invisible: answering "is something hammering the allowlist right
// now" meant hand-querying `IEventStore`, so a prompt-injection
// campaign and a quiet afternoon produced the same operator experience.
//
// This module is the rollup. It mirrors the shape of its two siblings
// on the same gate — `/dev/ai-fastpath` (Phase 6j.A) and
// `/dev/ai-latency` (Phase 6i.A) — deliberately, so an operator reads
// all three against a comparable 60-minute slice.
//
// **Two readers, one shape.** The report record is
// `ToolUp.Platform.AIDenialRollup`, defined in `Platform.Core` rather
// than here, because the same rollup is rendered by the production-safe
// `HealthMonitorUI` admin panel over Fable.Remoting. Core is the only
// tier that ships its source under `fable/` (GP 10), and a second DTO
// in this tier would be a drift seam for no gain.
//
// **Activation gate:** `ServerConfig.EnableDevEndpoints = true`, applied
// by `composeAI`. There is no `#if DEBUG` half — `ToolUp.AI` stopped
// carrying compile-time gates when the tier went OSS-bound, so the
// runtime flag is the sole gate (same as `/dev/ai-fastpath`). The
// production route to the same data is the admin panel below, which is
// gated on `PlatformRole.PlatformAdmin` instead and needs no dev flag.
//
// **Caller-scope only (GP 4).** `IEventStore.ReadBySource` takes the
// caller's resolved scope; another team's denials are structurally
// unreachable. `ByScopeId` therefore reports the read scope (and, on a
// store that writes scope-less platform rows under `_platform`, that
// one) — it is a completeness axis, not a cross-tenant enumeration.

[<Literal>]
let SourceModule = "_platform.ai.tool_allowlist_denial"

[<Literal>]
let DenialEventType = "ToolAllowlistDenied"

/// Rolling window for the rollup. Matches `/dev/ai-fastpath` and
/// `/dev/ai-latency` so an operator comparing the three is comparing
/// the same slice of time.
let private rollingWindow = TimeSpan.FromMinutes 60.0

/// Cap on `TopToolModulePairs`. Small on purpose — the list answers
/// "what is the campaign hitting", and a hundred rows answers nothing.
[<Literal>]
let private TopPairsCap = 10

/// Cap on `RecentDenials`.
[<Literal>]
let private RecentCap = 20

/// Hard ceiling on a rendered refusal reason. The reason is authored by
/// the deployment's own authorizer, but it may quote what the model
/// asked for, and the model's text is attacker-influenced under prompt
/// injection — so it is truncated as well as control-stripped.
[<Literal>]
let private ReasonMaxChars = 200

/// Rendered in place of an absent `ActiveModule` so the grouping axis
/// has no null key (a `null` group key would collapse silently into
/// whatever the serialiser does with it).
[<Literal>]
let NoModuleLabel = "(none)"

let private payloadJsonOptions = FableConverters.create ()

let private indentedJsonOptions =
    let o = FableConverters.create ()
    o.WriteIndented <- true
    o

/// Mirror of the payload `AIAgentEngine.writeToolDenialAudit` serialises
/// onto the denial event. Kept field-for-field so the deserialise path
/// round-trips; the rollup itself reads only the first three.
type DenialEventPayload = {
    ToolName: string
    Reason: string
    ActiveModule: string option
    ActivePage: string option
    TaskId: Guid
    ConversationId: Guid
}

// ─── Sanitisation ───────────────────────────────────────────────

/// Control-strip, whitespace-collapse and truncate a refusal reason for
/// operator display.
///
/// Three things this defends against, all of them consequences of the
/// reason being adjacent to model-chosen text: a newline or ANSI escape
/// smuggled into a console/log view, an unbounded blob pushed through a
/// JSON response, and an empty string rendering as a blank cell that
/// looks like a UI bug rather than an absent reason.
let sanitiseReason (raw: string) : string =
    if String.IsNullOrWhiteSpace raw then
        "(no reason recorded)"
    else
        let despecialised = raw |> String.map (fun c -> if Char.IsControl c then ' ' else c)

        let collapsed =
            despecialised.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
            |> String.concat " "

        if collapsed.Length <= ReasonMaxChars then
            collapsed
        else
            collapsed.Substring(0, ReasonMaxChars) + "…"

/// `ActiveModule` for grouping and display — never `null`, never empty.
let moduleLabel (activeModule: string option) : string =
    match activeModule with
    | Some m when not (String.IsNullOrWhiteSpace m) -> m
    | _ -> NoModuleLabel

// ─── Rollup (pure) ──────────────────────────────────────────────

let private groupCount (selector: DenialEventPayload -> string) (rows: DenialEventPayload list) =
    rows
    |> List.groupBy selector
    |> List.map (fun (key, items) -> { Key = key; Count = items.Length })
    |> List.sortByDescending _.Count

/// Build the rollup from already-read, already-decoded rows.
///
/// Pure and total — no clock, no DI, no store — so the whole
/// aggregation is exercisable from a test with a synthetic burst, which
/// is what the acceptance criterion "a simulated denial burst crosses
/// the threshold" needs in order to mean anything.
///
/// `all` is every decoded denial in the caller's scope paired with its
/// `OccurredAt`; the window filter is applied here rather than by the
/// caller so `TotalDenialsAllTime` and `TotalDenialsInWindow` cannot
/// disagree about which rows they counted.
let computeRollup
    (scopeId: string)
    (now: DateTime)
    (window: TimeSpan)
    (all: (DenialEventPayload * string * DateTime) list)
    : AIDenialRollup =
    let windowStart = now - window

    let inWindow = all |> List.filter (fun (_, _, at) -> at >= windowStart)

    let rows = inWindow |> List.map (fun (p, _, _) -> p)

    let windowMinutes = int window.TotalMinutes

    {
        GeneratedAt = now
        ScopeId = scopeId
        WindowMinutes = windowMinutes
        TotalDenialsAllTime = all.Length
        TotalDenialsInWindow = inWindow.Length
        DenialsPerMinute =
            if windowMinutes <= 0 then
                0.0
            else
                float inWindow.Length / float windowMinutes
        ByToolName = rows |> groupCount _.ToolName
        ByActiveModule = rows |> groupCount (fun p -> moduleLabel p.ActiveModule)
        ByScopeId =
            inWindow
            |> List.groupBy (fun (_, rowScope, _) -> rowScope)
            |> List.map (fun (key, items) -> { Key = key; Count = items.Length })
            |> List.sortByDescending _.Count
        TopToolModulePairs =
            rows
            |> List.groupBy (fun p -> p.ToolName, moduleLabel p.ActiveModule)
            |> List.map (fun ((tool, activeModule), items) -> {
                ToolName = tool
                ActiveModule = activeModule
                Count = items.Length
            })
            |> List.sortByDescending _.Count
            |> List.truncate TopPairsCap
        RecentDenials =
            inWindow
            |> List.sortByDescending (fun (_, _, at) -> at)
            |> List.truncate RecentCap
            |> List.map (fun (p, _, at) -> {
                ToolName = p.ToolName
                ActiveModule = moduleLabel p.ActiveModule
                Reason = sanitiseReason p.Reason
                OccurredAt = at
            })
    }

// ─── Store read ─────────────────────────────────────────────────

let private decodePayload (evt: ModuleEvent) : (DenialEventPayload * string * DateTime) option =
    try
        let payload =
            JsonSerializer.Deserialize<DenialEventPayload>(evt.Payload, payloadJsonOptions)

        if isNull (box payload) then
            None
        else
            Some(payload, evt.ScopeId, evt.OccurredAt)
    with _ ->
        // A row we cannot decode is a row written by a different
        // producer (or a shape from before this rollup existed). Drop
        // it rather than failing the whole report — an operator
        // watching an injection campaign is worse served by a 500 than
        // by a rollup missing one unreadable row.
        None

/// Read + roll up the denials recorded for `scopeId`. Shared by the dev
/// endpoint and the `IAIDenialRollupProbe` seam so the two surfaces
/// cannot drift.
let rollupFor (storeOpt: IEventStore option) (scopeId: string) : Async<AIDenialRollup> = async {
    let now = DateTime.UtcNow

    let! events =
        match storeOpt with
        | None -> async { return [] }
        | Some store -> store.ReadBySource(scopeId, SourceModule)

    let decoded =
        events
        |> List.filter (fun evt -> evt.EventType = DenialEventType)
        |> List.choose decodePayload

    return computeRollup scopeId now rollingWindow decoded
}

// ─── The `IAIDenialRollupProbe` seam implementation ──────────────

/// Phase 47 — the AI tier's implementation of the Core
/// `IAIDenialRollupProbe` seam, so the platform-tier health-monitor
/// admin surface can render this rollup without `Platform.Server`
/// referencing `ToolUp.AI` (GP 1).
///
/// `resolveStore` is a thunk rather than a captured instance because
/// the probe is registered as a DI singleton at compose time: resolving
/// `IEventStore` eagerly there would pin whatever was registered first,
/// and a composition that registers the store after the AI layer would
/// silently get a probe that reads nothing.
let probe (resolveStore: unit -> IEventStore option) : IAIDenialRollupProbe =
    { new IAIDenialRollupProbe with
        member _.Rollup(scopeId: string) = rollupFor (resolveStore ()) scopeId
    }

// ─── Route handler ──────────────────────────────────────────────

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

let private buildReport (ctx: HttpContext) : Async<AIDenialRollup> =
    let scope = resolveScope ctx

    let storeOpt =
        match ctx.RequestServices.GetService(typeof<IEventStore>) with
        | :? IEventStore as s -> Some s
        | _ -> None

    rollupFor storeOpt scope.ScopeId

/// JSON handler for `/dev/ai-allowlist`. Sets `Cache-Control: no-store`
/// so a refresh always re-reads the store.
let private allowlistHandler: HttpHandler =
    fun next ctx -> task {
        let! report = buildReport ctx
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.Headers["Cache-Control"] <- "no-store"
        do! ctx.Response.WriteAsync(JsonSerializer.Serialize(report, indentedJsonOptions))
        return! next ctx
    }

/// Route for the AI action-denial rollup. Mounted only when
/// `ServerConfig.EnableDevEndpoints = true` by `composeAI`.
let routes: HttpHandler list = [ route "/dev/ai-allowlist" >=> allowlistHandler ]