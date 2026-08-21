module ToolUp.AI.FastPathBeaconHandler

open System
open System.IO
open System.Text
open Microsoft.AspNetCore.Http
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
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
//   * Phase 6j.E — beacon idempotency. A page reload mid-flight, a
//     browser network blip, or any client retry re-POSTs the same
//     beacon, and the append path is not naturally idempotent: the
//     synthetic `User + AIAssistant` pair lands twice and every
//     subsequent agent-loop turn reads the instruction twice over. Each
//     emission now carries a client-minted `BeaconId`; the handler scans
//     the tail of the conversation for it and, on a hit, returns 202
//     having touched neither blob — so the persisted sequence after N
//     duplicate POSTs is byte-for-byte the sequence after one. A beacon
//     with no key is admitted exactly as before.

[<Literal>]
let private FastPathSourceModule = "_platform.ai.fastpath"

[<Literal>]
let private FastPathEventType = "FastPathResolved"

[<Literal>]
let private FastPathRejectedEventType = "FastPathRejected"

// ─── Phase 6j.G — sequenced fast-path event types ───────────────
//
// The multi-clause sequencer (whose client-side substrate ships as a
// separate fast-path-resolver companion outside this repo) emits three
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
//   3. One `sequence-beacon` per sequenced resolution — the aggregate
//      fired from the companion's chat-send hook rather than the
//      executor, carrying `(conversationId, tier, instruction,
//      clauseCount, latencyMs)`. It is the only sequencer beacon that
//      carries the conversation id and the resolution latency, which
//      is what lets offline analysis join the per-clause / outcome
//      streams back to the conversation they ran in.
//
// All three events ride on the existing `_platform.ai.fastpath` source
// so the rolling-window read in `FastPathTelemetryHandler` (which
// already filters by source) picks them up alongside the original
// `FastPathResolved` events. The `EventType` field distinguishes
// shapes at decode time.
//
// These events are pure telemetry — they don't append synthetic turns
// to any conversation blob. The handlers gate on `StorageScope` +
// `UserId` (so unauthenticated callers can't stuff fake events into
// the per-tenant event store) but skip the ownership check the
// conversation beacon performs (nothing here is read back into any
// conversation, so there is no history to forge).

[<Literal>]
let private SequencedClauseEventType = "SequencedFastPathClause"

[<Literal>]
let private SequenceOutcomeEventType = "SequencedFastPathOutcome"

[<Literal>]
let private SequenceResolvedEventType = "SequencedFastPathResolved"

/// Per-field cap on beacon free-text. A legitimate fast-path
/// synthetic reply / instruction is a short confirmation; 16 KB is
/// generous headroom while bounding the prompt-injection blast
/// radius written into provider history.
[<Literal>]
let private MaxBeaconTextLen = 16384

/// Phase 6j.E — how many trailing conversation messages the idempotency
/// scan reads. A beacon appends exactly TWO messages, so 20 covers the
/// last ten fast-path turns.
///
/// Why a window at all, and why this size. The duplicates this exists to
/// stop arrive within seconds of the original — a reload part-way
/// through the fetch, a transport-level retry, a double-fired hook — so
/// they are separated from it by at most a handful of turns, and ten is
/// generous headroom for that. Bounding the scan also bounds its cost:
/// an unbounded scan grows with the conversation, which is the wrong
/// shape for a check that runs on every beacon. And a key resurfacing
/// LONG after its turn is not a retry — it is a replay, which is a
/// different event and should append, not silently vanish.
[<Literal>]
let BeaconDedupWindow = 20

/// Cap on the beacon's idempotency key. The key is client-supplied and
/// PERSISTED on both appended turns, so it inherits the same posture as
/// the free-text fields above: bound it, or a caller can write an
/// unbounded string into the conversation blob through a field that
/// exists only to be compared for equality. 128 is ample for the Guid
/// the shipped client mints (36 chars) and for any other scheme a future
/// client might use.
[<Literal>]
let private MaxBeaconIdLen = 128

// ─── JSON settings (mirrors AIAssistantHandler — must match the
//     conversation blob format so the LLM sees the synthetic turns
//     in the right shape). ────────────────────────────────────────

let private jsonOptions = FableConverters.create ()

let private toJson obj =
    JsonSerializer.Serialize(obj, jsonOptions)

let private fromJson<'T> (bytes: byte[]) =
    let json = Encoding.UTF8.GetString(bytes)
    JsonSerializer.Deserialize<'T>(json, jsonOptions)

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
    /// Phase 6j.E — per-emission idempotency key (a fresh Guid string
    /// minted by the client for each beacon it sends, so a retry of the
    /// SAME emission carries the SAME key). Empty from a pre-6j.E client:
    /// a missing JSON property deserialises to `null` under
    /// `FableConverters`, which the dedup scan reads as "no key" and
    /// admits unchanged — no dedup, no rejection (GP 11).
    BeaconId: string
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
// `FableConverters.create ()` sets `PropertyNameCaseInsensitive = true`,
// so camelCase inputs from the browser deserialise into PascalCase F#
// records without ceremony (`clauseIndex` → `ClauseIndex` / `clauseText`
// → `ClauseText` / etc.).

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

/// Wire shape POSTed to `/api/ai/fastpath/sequence-beacon` by the
/// fast-path-resolver companion's chat-send hook after a sequenced
/// resolution — the whole-sequence aggregate. Unlike the two
/// executor-emitted beacons above it carries the conversation id, the
/// resolution tier and the resolution latency: the join key that lets
/// offline analysis correlate the per-clause / outcome streams with
/// the conversation they ran in.
type SequenceBeacon = {
    ConversationId: Guid
    Tier: int
    Instruction: string
    ClauseCount: int
    LatencyMs: float
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
    elif safeLen b.BeaconId > MaxBeaconIdLen then
        // Phase 6j.E — no pre-6j.E client can trip this: the field did
        // not exist, so it arrives absent (0 chars). Only a client that
        // opts into keying can exceed the cap.
        Error(sprintf "BeaconId exceeds %d-char limit" MaxBeaconIdLen)
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

// ─── Phase 6j.E — beacon idempotency ────────────────────────────
//
// Two normalisations feed one predicate.
//
// `FableConverters` deserialises a MISSING JSON property to `null` for a
// reference type, so a pre-6j.E client's beacon and a pre-6j.E
// conversation blob both surface `null` where a key would be — never
// `""`. Reading every key through `beaconKey` folds absent / null /
// empty into one case, so the "no key ⇒ behave exactly as today" clause
// holds without three branches restating it.

let private beaconKey (s: string) = if isNull s then "" else s

/// True when this beacon's key is already recorded on a message inside
/// the conversation's trailing `BeaconDedupWindow` — i.e. this POST is a
/// repeat of one already applied, and must no-op.
///
/// A beacon carrying NO key is never a duplicate: an older client cannot
/// be deduplicated (it has nothing to deduplicate on) and refusing it
/// would break the fast path on the upgrade, so it is admitted exactly
/// as before (GP 11). Symmetrically, a persisted message with no key
/// never matches — legacy history cannot accidentally suppress a live
/// beacon.
let isDuplicateBeacon (existing: ConversationMessage list) (beaconId: string) : bool =
    let key = beaconKey beaconId

    if key = "" then
        false
    else
        let skip = max 0 (List.length existing - BeaconDedupWindow)

        existing |> List.skip skip |> List.exists (fun m -> beaconKey m.BeaconId = key)

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
    // Phase 6j.E — stamp the emission's idempotency key so a repeat POST
    // finds it in the tail scan. Stamped on BOTH turns of the pair, not
    // just this one: the window is counted in messages, so a pair
    // straddling its edge would otherwise be half-invisible.
    BeaconId = beaconKey beacon.BeaconId
    // Phase 523 — fast-path turns bypass the LLM answer path entirely, so
    // no numeric-fidelity verdict applies.
    Verification = None
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
        let escapedField = JsonSerializer.Serialize(beacon.FieldName, jsonOptions)
        let escapedPattern = JsonSerializer.Serialize(beacon.PatternMatched, jsonOptions)

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
        // Phase 6j.E — see `buildUserMessage`: both turns of the pair
        // carry the key.
        BeaconId = beaconKey beacon.BeaconId
        // Phase 523 — fast-path synthetic replies quote no facts.
        Verification = None
    }

/// Phase 6j.E — the append decision, whole, as one pure function: the
/// turns to append, or `None` when this beacon has already been applied.
///
/// `None` is what makes the acceptance criterion structural rather than
/// incidental. The duplicate path does not compute a second pair and
/// then discard it, and does not append-then-dedupe; it produces nothing
/// at all, so the caller has nothing to write and the persisted sequence
/// after any number of repeat POSTs IS the sequence after the first one.
/// Both blobs are governed by this single decision — the handler returns
/// before either write, so the conversation blob and the provider-history
/// blob cannot disagree about whether the beacon landed.
let planBeaconAppend
    (existing: ConversationMessage list)
    (callerUserId: string)
    (beacon: FastPathBeacon)
    : ConversationMessage list option =
    if isDuplicateBeacon existing beacon.BeaconId then
        None
    else
        Some [ buildUserMessage callerUserId beacon; buildAssistantMessage beacon ]

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

        let info (m: string) =
            match logger with
            | Some l -> l.Info m
            | None -> ()

        try
            use reader = new StreamReader(ctx.Request.Body)
            let! body = reader.ReadToEndAsync()
            let beacon = JsonSerializer.Deserialize<FastPathBeacon>(body, jsonOptions)

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
                            match planBeaconAppend existing callerUserId beacon with
                            | None ->
                                // Phase 6j.E — this beacon is already in the
                                // conversation's tail. Explicit no-op: neither
                                // blob is read further or written, and no
                                // `FastPathResolved` event is emitted (one
                                // resolution happened, so one event exists —
                                // counting the retry would inflate the
                                // rolling-window hit stats the beacon audit
                                // trail exists to measure). 202, same as the
                                // append path: the client is fire-and-forget
                                // and a retry succeeding is the truth.
                                info
                                    $"FastPath beacon {beacon.BeaconId} for conversation {beacon.ConversationId} is a duplicate within the last {BeaconDedupWindow} messages; no-op."

                                ctx.Response.StatusCode <- 202
                                return! next ctx
                            | Some turns ->
                                do! saveMessages storage scope.Container beacon.ConversationId (existing @ turns)

                                let providerUser = buildProviderUser beacon
                                let providerAsst = buildProviderAssistant beacon

                                let! providerExisting =
                                    loadProviderHistory storage scope.Container beacon.ConversationId

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
// Three pure-telemetry endpoints that ride alongside the conversation
// beacon. Shape:
//   POST /api/ai/fastpath/sequenced-clause-beacon  body = SequencedClauseBeacon
//   POST /api/ai/fastpath/sequence-outcome-beacon  body = SequenceOutcomeBeacon
//   POST /api/ai/fastpath/sequence-beacon          body = SequenceBeacon
//
// All gate on `StorageScope` + `UserId` resolution so an
// unauthenticated caller cannot inject events into a tenant's event
// store. All bound the free-text fields to `MaxBeaconTextLen` to
// keep the prompt-injection / log-bloat blast radius matched to the
// conversation beacon. None persists anything to a conversation
// blob — they only emit a `_platform.ai.fastpath` event under the
// distinguishing `EventType` so `FastPathTelemetryHandler` can roll
// the events up into the sequencer keys on `/dev/ai-fastpath` (the
// clause and aggregate events are stored for offline analysis and
// skipped by the current rollup).

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

let private validateSequenceBeacon (b: SequenceBeacon) : Result<unit, string> =
    // The conversation id is the aggregate beacon's whole reason to
    // exist (the offline join key) — an empty Guid is junk telemetry,
    // refused like the conversation beacon refuses it.
    if b.ConversationId = Guid.Empty then
        Error "ConversationId is empty"
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

let private emitSequenceResolvedEvent
    (eventStore: IEventStore option)
    (logger: ILogger option)
    (scope: StorageScope)
    (beacon: SequenceBeacon)
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
                EventType = SequenceResolvedEventType
                Payload = toJson beacon
            }

            try
                do! store.Write evt
            with ex ->
                logWarn logger $"Sequenced fast-path aggregate event write failed: {ex.Message}"
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

            let beacon = JsonSerializer.Deserialize<SequencedClauseBeacon>(body, jsonOptions)

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

            let beacon = JsonSerializer.Deserialize<SequenceOutcomeBeacon>(body, jsonOptions)

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

let sequenceBeaconHandler: HttpHandler =
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

            let beacon = JsonSerializer.Deserialize<SequenceBeacon>(body, jsonOptions)

            if isNull (box beacon) then
                warn "Sequenced fast-path aggregate beacon rejected: unparseable request body."
                ctx.Response.StatusCode <- 400
                return! next ctx
            else
                match tryResolveScope ctx, tryResolveUserId ctx, validateSequenceBeacon beacon with
                | None, _, _ ->
                    warn
                        "Sequenced fast-path aggregate beacon rejected: no resolved StorageScope. ScopeResolutionMiddleware must run before this endpoint."

                    ctx.Response.StatusCode <- 401
                    return! next ctx
                | Some _, None, _ ->
                    warn "Sequenced fast-path aggregate beacon rejected: no resolved UserId."
                    ctx.Response.StatusCode <- 401
                    return! next ctx
                | Some _, Some _, Error reason ->
                    warn $"Sequenced fast-path aggregate beacon rejected ({reason})."
                    ctx.Response.StatusCode <- 400
                    return! next ctx
                | Some scope, Some _, Ok() ->
                    do! emitSequenceResolvedEvent eventStoreOpt logger scope beacon
                    ctx.Response.StatusCode <- 202
                    return! next ctx
        with ex ->
            match logger with
            | Some l -> l.Error("Sequenced fast-path aggregate beacon handler failed.", Some ex)
            | None -> ()

            ctx.Response.StatusCode <- 400
            return! next ctx
    }