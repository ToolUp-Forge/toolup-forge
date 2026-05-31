module ToolUp.AI.FastPathBeaconHandler

open System
open System.IO
open System.Text
open Microsoft.AspNetCore.Http
open Newtonsoft.Json
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.StorageScopeResolver
open ToolUp.AI

// ─── Phase 6j.A — fast-path audit beacon endpoint ───────────────
//
// `POST /api/ai/fastpath/beacon`. The client's chat-send hook
// fires this after a Tier 1 resolution. Server-side handler:
//
//   1. Persists the synthetic user + assistant turns into the
//      conversation blob (`ai-conversations/{id}.json`) so a
//      page reload still shows the fast-path turns.
//   2. Persists the same pair into the provider-history blob
//      (`ai-conversations/{id}.history.json`) so the LLM sees
//      them in subsequent agent-loop turns. R1 (context
//      continuity) mitigation.
//   3. Emits a `_platform.ai.fastpath` event to `IEventStore`
//      so `/dev/ai-fastpath` can compute rolling-window stats
//      and so the audit trail of fast-path resolutions is
//      preserved.
//   4. Returns 202 Accepted (fire-and-forget on the client).
//
// Failures during steps 1–3 surface as 400 / 500; the client
// already showed the local synthetic turn so a beacon failure
// only affects subsequent-turn context continuity, not the
// user-visible reply.
//
// Trust model. The beacon writes attacker-controlled text
// (`SyntheticReply`) into the provider-history blob the LLM reads
// on the NEXT turn — a prompt-injection / history-forgery surface.
// Hardening (no client-protocol change, doesn't break first-turn
// fast paths which legitimately have no server blob yet):
//   * The scope MUST come from `ScopeResolutionMiddleware`
//     (`ToolUp.StorageScope`). The old code fell back to the
//     `ToolUp.UserId` item or the literal `"anonymous"`, which on
//     any middleware-bypassed route silently bucketed every caller
//     into one shared `user-anonymous` container — a cross-user
//     injection vector. No resolved scope ⇒ 401, no writes.
//   * `Instruction` / `SyntheticReply` / `JsonFragment` are length-
//     bounded so a single beacon can't stuff an unbounded payload
//     into the history the model subsequently reads.
//   * Rejections emit a `FastPathRejected` audit event and log,
//     instead of the previous blanket silent `400`.
//   * Phase 6j.D — per-conversation ownership. The first persisted
//     message of a conversation records `CreatedBy = callerUserId`;
//     subsequent appends from a different caller in the same
//     shared `team-{teamId}` container return 403 and emit a
//     `BeaconRejected` audit event via `IAuditLog` so cross-user
//     history-forgery attempts are observable in the audit trail.
//     Legacy blobs without `CreatedBy` are accepted (pre-6j.D
//     conversations have no recorded owner; locking them out would
//     break existing histories on the upgrade).

[<Literal>]
let private FastPathSourceModule = "_platform.ai.fastpath"

[<Literal>]
let private FastPathEventType = "FastPathResolved"

[<Literal>]
let private FastPathRejectedEventType = "FastPathRejected"

// ─── Phase 6j.G — sequenced fast-path event types ───────────────
//
// The multi-clause sequencer (whose client-side substrate ships as a
// separate fast-path-resolver companion outside this repo) emits two
// additional telemetry beacons per dispatched sequence:
//
//   1. One `sequenced-clause-beacon` per successfully-dispatched
//      clause, carrying `(clauseIndex, clauseText, patternMatched,
//      actionKind, totalClauses)`.
//   2. One `sequence-outcome-beacon` per sequence, carrying
//      `(outcome, instruction, clauseCount, clausesCompleted)` where
//      `outcome` is one of `"all-resolved"` / `"partial-fall-through"`
//      / `"sequence-capped"` / `"handler-failed-mid-sequence"` /
//      `"handler-timed-out-mid-sequence"` / `"paused-mid-sequence"`
//      / `"taken-over-mid-sequence"`.
//
// Both events ride on the existing `_platform.ai.fastpath` source so
// the rolling-window read in `FastPathTelemetryHandler` (which already
// filters by source) picks them up alongside the original
// `FastPathResolved` events. The `EventType` field distinguishes
// shapes at decode time.
//
// These events are pure telemetry — they don't append synthetic turns
// to any conversation blob and carry no conversation id. The handlers
// gate on `StorageScope` + `UserId` (so unauthenticated callers can't
// stuff fake events into the per-tenant event store) but skip the
// ownership check the conversation beacon performs.

[<Literal>]
let private SequencedClauseEventType = "SequencedFastPathClause"

[<Literal>]
let private SequenceOutcomeEventType = "SequencedFastPathOutcome"

/// Per-field cap on beacon free-text. A legitimate fast-path
/// synthetic reply / instruction is a short confirmation; 16 KB is
/// generous headroom while bounding the prompt-injection blast
/// radius written into provider history.
[<Literal>]
let private MaxBeaconTextLen = 16384

// ─── JSON settings (mirrors AIAssistantHandler — must match the
//     conversation blob format so the LLM sees the synthetic turns
//     in the right shape). ────────────────────────────────────────

let private jsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(Fable.Remoting.Json.FableJsonConverter())
    s

let private toJson obj =
    JsonConvert.SerializeObject(obj, jsonSettings)

let private fromJson<'T> (bytes: byte[]) =
    let json = Encoding.UTF8.GetString(bytes)
    JsonConvert.DeserializeObject<'T>(json, jsonSettings)

let private conversationBlobName (conversationId: Guid) =
    $"ai-conversations/{conversationId}.json"

let private providerHistoryBlobName (conversationId: Guid) =
    $"ai-conversations/{conversationId}.history.json"

// ─── Wire shape (must match Client/FastPathHook.fs) ─────────────

type FastPathBeacon = {
    ConversationId: Guid
    Tier: int
    /// `"_navigation"` for module-switch resolutions, otherwise the
    /// active module's id.
    ModuleId: string
    /// `"_navigation"` for module-switch resolutions, otherwise the
    /// resolved field name.
    FieldName: string
    Instruction: string
    SyntheticReply: string
    PatternMatched: string
    LatencyMs: float
    /// JSON fragment dispatched to the field decoder; empty for
    /// navigation. Carries enough info for cross-tier consistency
    /// analysis against Tier 4 `set_field` tool calls.
    JsonFragment: string
}

// ─── Phase 6j.G — sequenced fast-path wire shapes ────────────────
//
// Wire shape POSTed to `/api/ai/fastpath/sequenced-clause-beacon` by
// the fast-path-resolver companion's sequenced executor after each
// successfully-dispatched clause. The same record is what
// `FastPathTelemetryHandler` reads back out of the event store so the
// rolling-window rollup can compute `meanClausesPerSequence` from
// `TotalClauses` and per-clause hit counts.
//
// Newtonsoft.Json deserialisation is case-insensitive by default, so
// PascalCase F# field names round-trip cleanly against the camelCase
// JSON the resolver emits (`clauseIndex` / `clauseText` / etc.).

type SequencedClauseBeacon = {
    ClauseIndex: int
    ClauseText: string
    PatternMatched: string
    /// `"set-field"` / `"navigate"` — mirrors the resolver's per-clause
    /// `ResolvedAction` discriminator so offline analysis can compare
    /// per-action-kind hit rates against the original `FastPathResolved`
    /// stream.
    ActionKind: string
    TotalClauses: int
}

/// Wire shape POSTed to `/api/ai/fastpath/sequence-outcome-beacon` by
/// the sequenced executor on every exit path (success, partial
/// fall-through, cap, mid-sequence pause / take-over, handler failure /
/// timeout). The `Outcome` field carries the discriminator string used
/// by `FastPathTelemetryHandler` to bucket events into the hit /
/// fall-through / interrupt categories.
type SequenceOutcomeBeacon = {
    /// One of `"all-resolved"` / `"partial-fall-through"` /
    /// `"sequence-capped"` / `"handler-failed-mid-sequence"` /
    /// `"handler-timed-out-mid-sequence"` / `"paused-mid-sequence"` /
    /// `"taken-over-mid-sequence"`.
    Outcome: string
    Instruction: string
    ClauseCount: int
    ClausesCompleted: int
}

// ─── Provider-history mirror (mirrors AIAssistantHandler types) ─
//
// `AIProviderMessage` is the wire shape the provider history blob
// carries. Defined locally so we don't pull in the full
// AIAgentEngine dependency tree. Field names match
// `AIProviderMessage` in `AIAgentEngine.fs` exactly so the blob
// round-trips correctly.

type private AIProviderToolCall = {
    Id: string
    Name: string
    Arguments: string
}

type private AIProviderMessage = {
    Role: string
    Content: string
    ToolCalls: AIProviderToolCall list
    ToolResults: (string * string) list
}

// ─── Storage scope resolution ───────────────────────────────────

// Authoritative scope ONLY. No `ToolUp.UserId` / `"anonymous"`
// fallback — that silently funnelled middleware-bypassed callers
// into one shared container. `None` ⇒ the request is rejected.
let private tryResolveScope (ctx: HttpContext) : StorageScope option =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as s) -> Some s
    | _ -> None

// Caller identity for the ownership gate. Populated by
// `ScopeResolutionMiddleware` in the same Items dictionary as the
// storage scope. Returns `None` when the middleware did not run or
// resolved no identity — the gate refuses unauthenticated callers
// rather than admitting `"anonymous"` as a usable owner.
let private tryResolveUserId (ctx: HttpContext) : string option =
    match ctx.Items.TryGetValue "ToolUp.UserId" with
    | true, (:? string as id) when id <> "" -> Some id
    | _ -> None

let private resolveLogger (ctx: HttpContext) : ILogger option =
    match ctx.RequestServices.GetService(typeof<ILogger>) with
    | :? ILogger as l -> Some l
    | _ -> None

let private tryResolveAuditLog (ctx: HttpContext) : IAuditLog option =
    match ctx.RequestServices.GetService(typeof<IAuditLog>) with
    | :? IAuditLog as a -> Some a
    | _ -> None

let private safeLen (s: string) = if isNull s then 0 else s.Length

/// Reject malformed / oversized beacons before any persistence.
/// `Ok` ⇒ shape is plausible; `Error reason` ⇒ 400 + audited.
let private validateBeacon (b: FastPathBeacon) : Result<unit, string> =
    if b.ConversationId = Guid.Empty then
        Error "ConversationId is empty"
    elif safeLen b.Instruction > MaxBeaconTextLen then
        Error(sprintf "Instruction exceeds %d-char limit" MaxBeaconTextLen)
    elif safeLen b.SyntheticReply > MaxBeaconTextLen then
        Error(sprintf "SyntheticReply exceeds %d-char limit" MaxBeaconTextLen)
    elif safeLen b.JsonFragment > MaxBeaconTextLen then
        Error(sprintf "JsonFragment exceeds %d-char limit" MaxBeaconTextLen)
    else
        Ok()

// ─── Phase 6j.D — conversation-ownership gate ───────────────────
//
// The beacon append-path writes attacker-controlled text into the
// provider-history blob the LLM reads on the next turn. In shared-
// container modes (`Team` / `MultiTeam`, where `Container = team-
// {teamId}`), a buggy or hostile member could target another member's
// conversation in the same container and bias its agent loop. The
// ownership gate cross-checks the caller against the conversation's
// `CreatedBy` (the first persisted message's owner field), refusing
// cross-user appends.
//
// Semantics — three accept cases, one refuse:
//   1. `existing = []` — new conversation. The caller establishes
//      themselves as creator on the first persisted message. Accept.
//   2. `existing[0].CreatedBy = ""` — legacy blob (pre-6j.D) with no
//      recorded owner. Accept; the field was added without a
//      backfill migration, so locking these out would break existing
//      conversations on the upgrade.
//   3. `existing[0].CreatedBy = caller` — same user. Accept.
//   4. otherwise — cross-user. Refuse with the owner's id surfaced
//      back to the handler so the `BeaconRejected` audit payload can
//      record it for forensics.
//
// Pure function: takes no IO. The handler resolves the inputs and
// applies the result. Same shape is reused by the symmetric guard in
// `AIAssistantHandler.SubmitMessage`.

let checkOwnership (existing: ConversationMessage list) (callerUserId: string) : Result<unit, string> =
    match existing with
    | [] -> Ok()
    | first :: _ ->
        let owner = first.CreatedBy

        if System.String.IsNullOrEmpty owner then Ok()
        elif owner = callerUserId then Ok()
        else Error owner

// ─── Conversation persistence ───────────────────────────────────

let private loadMessages (storage: IBlobStorage) (container: string) (conversationId: Guid) = async {
    let! result = storage.Download(container, conversationBlobName conversationId)

    match result with
    | Ok bytes ->
        try
            return fromJson<ConversationMessage list> bytes
        with _ ->
            return []
    | Error _ -> return []
}

let private saveMessages
    (storage: IBlobStorage)
    (container: string)
    (conversationId: Guid)
    (messages: ConversationMessage list)
    =
    async {
        let bytes = toJson messages |> Encoding.UTF8.GetBytes
        let! _ = storage.Upload(container, conversationBlobName conversationId, bytes)
        return ()
    }

let private loadProviderHistory (storage: IBlobStorage) (container: string) (conversationId: Guid) = async {
    let! result = storage.Download(container, providerHistoryBlobName conversationId)

    match result with
    | Ok bytes ->
        try
            return fromJson<AIProviderMessage list> bytes
        with _ ->
            return []
    | Error _ -> return []
}

let private saveProviderHistory
    (storage: IBlobStorage)
    (container: string)
    (conversationId: Guid)
    (messages: AIProviderMessage list)
    =
    async {
        let bytes = toJson messages |> Encoding.UTF8.GetBytes
        let! _ = storage.Upload(container, providerHistoryBlobName conversationId, bytes)
        return ()
    }

// ─── Synthetic turn construction ────────────────────────────────

let private buildUserMessage (callerUserId: string) (beacon: FastPathBeacon) : ConversationMessage = {
    Id = Guid.NewGuid()
    ConversationId = beacon.ConversationId
    Participant = User
    Content = beacon.Instruction
    Timestamp = DateTime.UtcNow
    ToolCalls = []
    RetrievedSources = []
    Parts = []
    // Phase 6j.D — establishes conversation ownership when this is the
    // first persisted message; subsequent appends are gated against this
    // by `checkOwnership`. `callerUserId` flows from `ToolUp.UserId`
    // populated by `ScopeResolutionMiddleware`; an empty string only
    // surfaces in degenerate setups (middleware bypassed AND scope
    // resolution somehow succeeded), and is preserved as-is so the gate
    // treats those degenerate first-writes as legacy/unowned rather
    // than locking the conversation to nobody.
    CreatedBy = callerUserId
}

let private buildAssistantMessage (beacon: FastPathBeacon) : ConversationMessage =
    let toolName =
        if beacon.FieldName = "_navigation" then
            "_fastpath.navigate"
        else
            "_fastpath.set_field"

    let argsJson =
        // Mirrors the client-side `toolArgsForAction` shape closely
        // enough that history readers see consistent records. The
        // exact byte layout is not load-bearing — readers only inspect
        // `ToolName`.
        let escapedField = JsonConvert.ToString(beacon.FieldName)
        let escapedPattern = JsonConvert.ToString(beacon.PatternMatched)

        let value =
            if beacon.JsonFragment = "" then
                "null"
            else
                beacon.JsonFragment

        $"""{{"field":{escapedField},"value":{value},"pattern":{escapedPattern}}}"""

    let resultJson =
        let latency = sprintf "%.2f" beacon.LatencyMs
        $"""{{"status":"resolved","tier":{beacon.Tier},"latencyMs":{latency}}}"""

    let toolCall: ToolCallRecord = {
        ToolCallId = Guid.NewGuid()
        ToolName = toolName
        Arguments = argsJson
        Result = Some resultJson
        Status = Completed
    }

    {
        Id = Guid.NewGuid()
        ConversationId = beacon.ConversationId
        Participant = AIAssistant
        Content = beacon.SyntheticReply
        Timestamp = DateTime.UtcNow
        ToolCalls = [ toolCall ]
        RetrievedSources = []
        Parts = []
        // Synthetic assistant turn — only the first persisted message's
        // CreatedBy is authoritative for the ownership gate. Set to the
        // same caller id so the audit / replay shape is consistent.
        CreatedBy = ""
    }

let private buildProviderUser (beacon: FastPathBeacon) : AIProviderMessage = {
    Role = "user"
    Content = beacon.Instruction
    ToolCalls = []
    ToolResults = []
}

let private buildProviderAssistant (beacon: FastPathBeacon) : AIProviderMessage = {
    // Synthetic turn surfaces as plain assistant text in the provider
    // history. The LLM sees "Set country to UK" — sufficient to know
    // the action happened — without the synthetic ToolCall envelope
    // (which would confuse providers' tool-use protocols).
    Role = "assistant"
    Content = beacon.SyntheticReply
    ToolCalls = []
    ToolResults = []
}

// ─── Event-store payload ────────────────────────────────────────

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

let private logWarn (logger: ILogger option) (msg: string) =
    match logger with
    | Some l -> l.Warn msg
    | None -> ()

let private emitEvent
    (eventStore: IEventStore option)
    (logger: ILogger option)
    (scope: StorageScope)
    (beacon: FastPathBeacon)
    : Async<unit> =
    async {
        match eventStore with
        | None -> return ()
        | Some store ->
            let payload: FastPathEventPayload = {
                Tier = beacon.Tier
                ModuleId = beacon.ModuleId
                FieldName = beacon.FieldName
                Instruction = beacon.Instruction
                SyntheticReply = beacon.SyntheticReply
                PatternMatched = beacon.PatternMatched
                LatencyMs = beacon.LatencyMs
                JsonFragment = beacon.JsonFragment
                ConversationId = beacon.ConversationId
            }

            let evt: ModuleEvent = {
                Id = Guid.NewGuid()
                OccurredAt = DateTime.UtcNow
                ScopeId = scope.ScopeId
                SourceModule = FastPathSourceModule
                EventType = FastPathEventType
                Payload = toJson payload
            }

            try
                do! store.Write evt
            with ex ->
                // Non-fatal for the user's chat, but a wedged event
                // store should not be invisible — the fast-path audit
                // trail would silently develop holes otherwise.
                logWarn logger $"FastPath audit event write failed (conversation {beacon.ConversationId}): {ex.Message}"
    }

/// Audit a refused beacon so forged / oversized / unauthenticated
/// attempts are observable rather than a silent 400.
let private emitRejection
    (eventStore: IEventStore option)
    (logger: ILogger option)
    (scopeId: string)
    (conversationId: Guid)
    (reason: string)
    : Async<unit> =
    async {
        match eventStore with
        | None -> return ()
        | Some store ->
            let payload = {|
                ConversationId = conversationId
                Reason = reason
            |}

            let evt: ModuleEvent = {
                Id = Guid.NewGuid()
                OccurredAt = DateTime.UtcNow
                ScopeId = scopeId
                SourceModule = FastPathSourceModule
                EventType = FastPathRejectedEventType
                Payload = toJson payload
            }

            try
                do! store.Write evt
            with ex ->
                logWarn
                    logger
                    $"FastPath rejection audit write failed (conversation {conversationId}, reason '{reason}'): {ex.Message}"
    }

/// Phase 6j.D — record a `BeaconRejected` audit event via the
/// `IAuditLog` substrate. Distinct from `emitRejection` above: that
/// one writes the existing `_platform.ai.fastpath` / `FastPathRejected`
/// event (for malformed / oversized / scope-resolution-missing) via
/// the raw `IEventStore`. This one rides on `IAuditLog` so the
/// cross-user-ownership trail flows through the same retention /
/// replication pipeline as every other audit event, and so SOC 2 /
/// audit-export consumers see it under their existing
/// `IAuditLog.GetAuditTrail` queries. Silent no-op when `IAuditLog`
/// is not in DI (e.g. tests that bypass `compose`).
let private emitBeaconRejected
    (auditLogOpt: IAuditLog option)
    (logger: ILogger option)
    (scopeId: string)
    (conversationId: Guid)
    (caller: string)
    (owner: string)
    (surface: string)
    : Async<unit> =
    async {
        match auditLogOpt with
        | None -> return ()
        | Some auditLog ->
            let payload: BeaconRejectedPayload = {
                ConversationId = conversationId
                Caller = caller
                Owner = owner
                Surface = surface
            }

            try
                do! auditLog.Record(scopeId, BeaconRejected payload)
            with ex ->
                logWarn
                    logger
                    $"BeaconRejected audit write failed (conversation {conversationId}, caller='{caller}', owner='{owner}', surface='{surface}'): {ex.Message}"
    }

// ─── HTTP handler ───────────────────────────────────────────────

[<Literal>]
let BeaconSurfaceLabel = "beacon"

let beaconHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let logger = resolveLogger ctx

        let eventStoreOpt =
            match ctx.RequestServices.GetService(typeof<IEventStore>) with
            | :? IEventStore as s -> Some s
            | _ -> None

        let auditLogOpt = tryResolveAuditLog ctx

        let warn (m: string) =
            match logger with
            | Some l -> l.Warn m
            | None -> ()

        try
            use reader = new StreamReader(ctx.Request.Body)
            let! body = reader.ReadToEndAsync()
            let beacon = JsonConvert.DeserializeObject<FastPathBeacon>(body, jsonSettings)

            if isNull (box beacon) then
                warn "FastPath beacon rejected: unparseable request body."
                ctx.Response.StatusCode <- 400
                return! next ctx
            else
                match tryResolveScope ctx, tryResolveUserId ctx, validateBeacon beacon with
                | None, _, _ ->
                    // No middleware-resolved scope. Refuse rather than
                    // funnel into a shared `user-anonymous` container.
                    warn
                        $"FastPath beacon rejected: no resolved StorageScope (conversation {beacon.ConversationId}). ScopeResolutionMiddleware must run before this endpoint."

                    do!
                        emitRejection
                            eventStoreOpt
                            logger
                            "_unresolved"
                            beacon.ConversationId
                            "no resolved StorageScope"

                    ctx.Response.StatusCode <- 401
                    return! next ctx
                | Some scope, None, _ ->
                    // Scope resolved but no caller identity. The
                    // ownership gate would have nothing to compare
                    // against; refuse rather than treat unauthenticated
                    // callers as "any owner". Mirrors the
                    // `tryResolveScope = None` posture above.
                    warn
                        $"FastPath beacon rejected: no resolved UserId (conversation {beacon.ConversationId}). Caller identity is required for the ownership gate."

                    do! emitRejection eventStoreOpt logger scope.ScopeId beacon.ConversationId "no resolved UserId"
                    ctx.Response.StatusCode <- 401
                    return! next ctx
                | Some scope, Some _, Error reason ->
                    warn
                        $"FastPath beacon rejected ({reason}); scope='{scope.ScopeId}' conversation={beacon.ConversationId}."

                    do! emitRejection eventStoreOpt logger scope.ScopeId beacon.ConversationId reason
                    ctx.Response.StatusCode <- 400
                    return! next ctx
                | Some scope, Some callerUserId, Ok() ->
                    // Storage is required to persist synthetic turns AND
                    // to load `existing` for the ownership gate. Apps
                    // without storage (rare — would mean no conversation
                    // persistence at all) skip the gate and steps 1+2,
                    // only emitting the fast-path event. Without
                    // persistence there's nothing to cross-poison either,
                    // so the gap is vacuous in that configuration.
                    let storageOpt =
                        match ctx.RequestServices.GetService(typeof<IBlobStorage>) with
                        | :? IBlobStorage as s -> Some s
                        | _ -> None

                    match storageOpt with
                    | Some storage ->
                        // Load BEFORE the gate so the same blob read
                        // services both the ownership check and the
                        // append. One round-trip, not two.
                        let! existing = loadMessages storage scope.Container beacon.ConversationId

                        match checkOwnership existing callerUserId with
                        | Error ownerOfRecord ->
                            // Phase 6j.D — cross-user write attempt.
                            // The conversation belongs to someone else
                            // in this shared container. Refuse without
                            // touching either persisted blob.
                            warn
                                $"FastPath beacon rejected: caller '{callerUserId}' is not the owner of conversation {beacon.ConversationId} (owner '{ownerOfRecord}', scope '{scope.ScopeId}')."

                            do!
                                emitBeaconRejected
                                    auditLogOpt
                                    logger
                                    scope.ScopeId
                                    beacon.ConversationId
                                    callerUserId
                                    ownerOfRecord
                                    BeaconSurfaceLabel

                            ctx.Response.StatusCode <- 403
                            return! next ctx
                        | Ok() ->
                            let userMsg = buildUserMessage callerUserId beacon
                            let asstMsg = buildAssistantMessage beacon

                            do!
                                saveMessages
                                    storage
                                    scope.Container
                                    beacon.ConversationId
                                    (existing @ [ userMsg; asstMsg ])

                            let providerUser = buildProviderUser beacon
                            let providerAsst = buildProviderAssistant beacon
                            let! providerExisting = loadProviderHistory storage scope.Container beacon.ConversationId

                            do!
                                saveProviderHistory
                                    storage
                                    scope.Container
                                    beacon.ConversationId
                                    (providerExisting @ [ providerUser; providerAsst ])

                            do! emitEvent eventStoreOpt logger scope beacon

                            ctx.Response.StatusCode <- 202
                            return! next ctx
                    | None ->
                        // No storage configured — emit the fast-path
                        // event for telemetry and bail. No append
                        // happened, so no ownership check is meaningful.
                        do! emitEvent eventStoreOpt logger scope beacon

                        ctx.Response.StatusCode <- 202
                        return! next ctx
        with ex ->
            match logger with
            | Some l -> l.Error("FastPath beacon handler failed.", Some ex)
            | None -> ()

            ctx.Response.StatusCode <- 400
            return! next ctx
    }

// ─── Phase 6j.G — sequenced fast-path beacon handlers ────────────
//
// Two pure-telemetry endpoints that ride alongside the conversation
// beacon. Shape:
//   POST /api/ai/fastpath/sequenced-clause-beacon  body = SequencedClauseBeacon
//   POST /api/ai/fastpath/sequence-outcome-beacon  body = SequenceOutcomeBeacon
//
// Both gate on `StorageScope` + `UserId` resolution so an
// unauthenticated caller cannot inject events into a tenant's event
// store. Both bound the free-text fields to `MaxBeaconTextLen` to
// keep the prompt-injection / log-bloat blast radius matched to the
// conversation beacon. Neither persists anything to a conversation
// blob — they only emit a `_platform.ai.fastpath` event under the
// distinguishing `EventType` so `FastPathTelemetryHandler` can roll
// the events up into the sequencer keys on `/dev/ai-fastpath`.

let private validateSequencedClauseBeacon (b: SequencedClauseBeacon) : Result<unit, string> =
    if safeLen b.ClauseText > MaxBeaconTextLen then
        Error(sprintf "ClauseText exceeds %d-char limit" MaxBeaconTextLen)
    elif safeLen b.PatternMatched > MaxBeaconTextLen then
        Error(sprintf "PatternMatched exceeds %d-char limit" MaxBeaconTextLen)
    elif safeLen b.ActionKind > MaxBeaconTextLen then
        Error(sprintf "ActionKind exceeds %d-char limit" MaxBeaconTextLen)
    else
        Ok()

let private validateSequenceOutcomeBeacon (b: SequenceOutcomeBeacon) : Result<unit, string> =
    if safeLen b.Outcome > MaxBeaconTextLen then
        Error(sprintf "Outcome exceeds %d-char limit" MaxBeaconTextLen)
    elif safeLen b.Instruction > MaxBeaconTextLen then
        Error(sprintf "Instruction exceeds %d-char limit" MaxBeaconTextLen)
    else
        Ok()

let private emitSequencedClauseEvent
    (eventStore: IEventStore option)
    (logger: ILogger option)
    (scope: StorageScope)
    (beacon: SequencedClauseBeacon)
    : Async<unit> =
    async {
        match eventStore with
        | None -> return ()
        | Some store ->
            let evt: ModuleEvent = {
                Id = Guid.NewGuid()
                OccurredAt = DateTime.UtcNow
                ScopeId = scope.ScopeId
                SourceModule = FastPathSourceModule
                EventType = SequencedClauseEventType
                Payload = toJson beacon
            }

            try
                do! store.Write evt
            with ex ->
                logWarn logger $"Sequenced fast-path clause event write failed: {ex.Message}"
    }

let private emitSequenceOutcomeEvent
    (eventStore: IEventStore option)
    (logger: ILogger option)
    (scope: StorageScope)
    (beacon: SequenceOutcomeBeacon)
    : Async<unit> =
    async {
        match eventStore with
        | None -> return ()
        | Some store ->
            let evt: ModuleEvent = {
                Id = Guid.NewGuid()
                OccurredAt = DateTime.UtcNow
                ScopeId = scope.ScopeId
                SourceModule = FastPathSourceModule
                EventType = SequenceOutcomeEventType
                Payload = toJson beacon
            }

            try
                do! store.Write evt
            with ex ->
                logWarn logger $"Sequenced fast-path outcome event write failed: {ex.Message}"
    }

let sequencedClauseBeaconHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let logger = resolveLogger ctx

        let eventStoreOpt =
            match ctx.RequestServices.GetService(typeof<IEventStore>) with
            | :? IEventStore as s -> Some s
            | _ -> None

        let warn (m: string) =
            match logger with
            | Some l -> l.Warn m
            | None -> ()

        try
            use reader = new StreamReader(ctx.Request.Body)
            let! body = reader.ReadToEndAsync()

            let beacon =
                JsonConvert.DeserializeObject<SequencedClauseBeacon>(body, jsonSettings)

            if isNull (box beacon) then
                warn "Sequenced fast-path clause beacon rejected: unparseable request body."
                ctx.Response.StatusCode <- 400
                return! next ctx
            else
                match tryResolveScope ctx, tryResolveUserId ctx, validateSequencedClauseBeacon beacon with
                | None, _, _ ->
                    warn
                        "Sequenced fast-path clause beacon rejected: no resolved StorageScope. ScopeResolutionMiddleware must run before this endpoint."

                    ctx.Response.StatusCode <- 401
                    return! next ctx
                | Some _, None, _ ->
                    warn "Sequenced fast-path clause beacon rejected: no resolved UserId."
                    ctx.Response.StatusCode <- 401
                    return! next ctx
                | Some _, Some _, Error reason ->
                    warn $"Sequenced fast-path clause beacon rejected ({reason})."
                    ctx.Response.StatusCode <- 400
                    return! next ctx
                | Some scope, Some _, Ok() ->
                    do! emitSequencedClauseEvent eventStoreOpt logger scope beacon
                    ctx.Response.StatusCode <- 202
                    return! next ctx
        with ex ->
            match logger with
            | Some l -> l.Error("Sequenced fast-path clause beacon handler failed.", Some ex)
            | None -> ()

            ctx.Response.StatusCode <- 400
            return! next ctx
    }

let sequenceOutcomeBeaconHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let logger = resolveLogger ctx

        let eventStoreOpt =
            match ctx.RequestServices.GetService(typeof<IEventStore>) with
            | :? IEventStore as s -> Some s
            | _ -> None

        let warn (m: string) =
            match logger with
            | Some l -> l.Warn m
            | None -> ()

        try
            use reader = new StreamReader(ctx.Request.Body)
            let! body = reader.ReadToEndAsync()

            let beacon =
                JsonConvert.DeserializeObject<SequenceOutcomeBeacon>(body, jsonSettings)

            if isNull (box beacon) then
                warn "Sequenced fast-path outcome beacon rejected: unparseable request body."
                ctx.Response.StatusCode <- 400
                return! next ctx
            else
                match tryResolveScope ctx, tryResolveUserId ctx, validateSequenceOutcomeBeacon beacon with
                | None, _, _ ->
                    warn
                        "Sequenced fast-path outcome beacon rejected: no resolved StorageScope. ScopeResolutionMiddleware must run before this endpoint."

                    ctx.Response.StatusCode <- 401
                    return! next ctx
                | Some _, None, _ ->
                    warn "Sequenced fast-path outcome beacon rejected: no resolved UserId."
                    ctx.Response.StatusCode <- 401
                    return! next ctx
                | Some _, Some _, Error reason ->
                    warn $"Sequenced fast-path outcome beacon rejected ({reason})."
                    ctx.Response.StatusCode <- 400
                    return! next ctx
                | Some scope, Some _, Ok() ->
                    do! emitSequenceOutcomeEvent eventStoreOpt logger scope beacon
                    ctx.Response.StatusCode <- 202
                    return! next ctx
        with ex ->
            match logger with
            | Some l -> l.Error("Sequenced fast-path outcome beacon handler failed.", Some ex)
            | None -> ()

            ctx.Response.StatusCode <- 400
            return! next ctx
    }