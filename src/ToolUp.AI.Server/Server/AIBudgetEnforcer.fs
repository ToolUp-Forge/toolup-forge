module ToolUp.AI.AIBudgetEnforcer

open System
open System.Collections.Concurrent
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.AI

// ─── Phase 9s — the per-user token-budget check ──────────────────
//
// The enforcement half of `AIBudgetTypes.fs`: read the scope's policy,
// sum the caller's consumption in the current UTC hour out of the
// Phase 6i.A `AILatencyRecord` stream, and hand back a Phase 689
// `BudgetVerdict`. The provider decorator that consumes it lives in
// `AIProviderUsageMiddleware.fs`, beside Phase 9d's
// `QuotaEnforcingProvider` — one chain, not two enforcement paths.
//
// **What is cached and what is not, because that split IS the Phase 9c
// rule-4 answer.** The expensive half is the event-store scan, and it
// is cached per (scope, user, hour) for 30 s. The POLICY is not cached
// at all: an admin who lowers a cap mid-hour is respected by the very
// next request, without an invalidation signal that would have to be
// delivered, ordered and retried across every process in a deployment.
// So the cache is a pure read-through accelerator over a derived
// figure — drop it, restart the process, run two replicas that never
// share it, and every decision is still computed from the event store
// (rule 4: no in-memory state between invocations that the answer
// depends on). Its honest cost is stated rather than hidden: a user
// can overshoot by up to one cache window's worth of spend, which is
// the same class of slack Phase 9d's advisory pre-call estimate
// already carries.
//
// **It fails OPEN, deliberately.** A token ceiling is a cost control,
// not an authorization control. If the config store or the event store
// throws, refusing would convert a telemetry outage into a total AI
// outage for every user in the deployment — a strictly worse failure
// than the spend the ceiling was protecting against. The refusal path
// is reached only when a cap is configured AND the window was read AND
// the arithmetic says the request does not fit.

/// Cache lifetime for one computed window sum. 30 s, as the phase
/// specifies: long enough that a burst of turns costs one scan,
/// short enough that the slack it grants is a rounding error against
/// an hourly ceiling.
[<Literal>]
let WindowCacheTtlSeconds = 30

/// Cap on the audit write for a refusal. The call has already been
/// refused by the time this runs; a wedged event store must not also
/// hang the request that is carrying the refusal back to the user.
[<Literal>]
let private BudgetAuditWriteTimeoutMs = 5_000

/// Serialiser for both the latency records read and the refusal
/// records written. `FableConverters` round-trips F# records and
/// `option` losslessly, so a record reads back in the shape the agent
/// loop wrote it.
let private budgetJsonOptions = FableConverters.create ()

/// One cached window sum, plus the two figures that make the
/// `/dev/inspect` panel readable. `CapAtLastSum` is diagnostic ONLY —
/// the live decision always re-reads the policy, so this is what the
/// cap was when the sum was last computed, and may lag by up to the
/// TTL.
type AIBudgetWindowEntry = {
    ScopeId: string
    UserId: string
    /// `BudgetPeriod.key` of the hour this sum covers.
    PeriodKey: string
    /// Tokens (`PromptTokens + OutputTokens`) the user had consumed in
    /// that hour when the sum was taken.
    UsedTokens: decimal
    /// The configured ceiling observed at that moment. `0M` = unbounded.
    CapAtLastSum: decimal
    ComputedAt: DateTime
}

/// Read-through cache over the per-(scope, user, hour) window sum.
///
/// Keyed by the period key as well as the identities, so an hour
/// boundary evicts by construction — the next hour is a different key
/// that has never been written, which is the same "the period is a
/// storage key, not a counter with a reset job" property the Phase 689
/// ledger relies on. Stale entries for elapsed hours are swept on
/// write rather than by a timer, so the cache holds nothing a
/// deployment is not still asking about.
type AIBudgetWindowCache(ttlSeconds: int) =
    let entries =
        ConcurrentDictionary<struct (string * string * string), AIBudgetWindowEntry>()

    /// The default TTL (`WindowCacheTtlSeconds`).
    new() = AIBudgetWindowCache(WindowCacheTtlSeconds)

    member _.TtlSeconds = ttlSeconds

    /// The cached sum for this window, if one was computed within the
    /// TTL. `None` on a miss, on an expired entry, and always when
    /// `ttlSeconds <= 0` (the caching-disabled setting a test uses to
    /// prove the answer does not depend on the cache).
    member _.TryRead(scopeId: string, userId: string, periodKey: string, now: DateTime) : decimal option =
        if ttlSeconds <= 0 then
            None
        else
            match entries.TryGetValue(struct (scopeId, userId, periodKey)) with
            | true, entry when (now - entry.ComputedAt).TotalSeconds < float ttlSeconds -> Some entry.UsedTokens
            | _ -> None

    /// Record a freshly-computed sum, and drop any entry whose period
    /// has elapsed.
    member _.Write(entry: AIBudgetWindowEntry) =
        if ttlSeconds > 0 then
            entries[struct (entry.ScopeId, entry.UserId, entry.PeriodKey)] <- entry

            // Sweep elapsed hours. Cheap (the dictionary holds one
            // entry per active user per hour) and bounded, and it
            // keeps the diagnostics panel showing the CURRENT window
            // rather than an archaeology of past ones.
            for KeyValue(key, value) in entries do
                if value.PeriodKey <> entry.PeriodKey then
                    entries.TryRemove key |> ignore

    /// Every live entry — the `/dev/inspect` read. Ordered by
    /// consumption, heaviest first: the panel's question is "who is
    /// hitting the wall".
    member _.Snapshot() : AIBudgetWindowEntry list =
        entries.Values |> Seq.sortByDescending _.UsedTokens |> List.ofSeq

    /// Drop everything. Not part of the decision path — it exists so a
    /// test can prove the enforcer computes the same verdict with an
    /// empty cache as with a warm one.
    member _.Clear() = entries.Clear()

/// The UTC hour `at` falls in. Must agree with
/// `BudgetPeriod.key BudgetPeriod.Hourly`, which is why both are
/// computed from the same `ToUniversalTime()` value: a window start
/// that disagreed with the period key would sum one hour and record
/// the refusal against another.
let hourWindowStart (at: DateTime) : DateTime =
    let utc = at.ToUniversalTime()
    DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc)

/// Tokens one user consumed in `scopeId` since `windowStart`.
///
/// Reads the Phase 6i.A latency stream — the always-on per-turn
/// telemetry — rather than `IUsageLog`, which Phase 9d's team windows
/// use. Two reasons, and the first is decisive: the usage log is
/// written only when `UsageMetering = EnabledUsageMetering`, so a
/// budget summing it would silently never refuse on a deployment that
/// had not also enabled metering. The second is attribution: the usage
/// record carries the user id in a metadata map, the latency record
/// (since 9s) carries it as a typed field.
///
/// A record with no `UserId` — every record written before 9s — is
/// UNATTRIBUTED and charged to nobody. Wrong in the safe direction,
/// and self-healing within one hour of deploying.
let sumUserWindow (store: IEventStore) (scopeId: string) (userId: string) (windowStart: DateTime) : Async<decimal> = async {
    let! events = store.ReadBySource(scopeId, AILatencyRecord.SourceModule)

    let tokensOf (evt: ModuleEvent) =
        try
            let record =
                JsonSerializer.Deserialize<AILatencyRecord>(evt.Payload, budgetJsonOptions)

            if isNull (box record) then
                0M
            elif isNull (box record.UserId) || record.UserId <> userId then
                0M
            else
                let prompt = record.PromptTokens |> Option.defaultValue 0
                let output = record.OutputTokens |> Option.defaultValue 0
                decimal (prompt + output)
        with _ ->
            // One malformed payload must not blind the window to every
            // other record in it. Mirrors `AILatencyHandler.decodePayload`,
            // which drops undecodable events for the same reason.
            0M

    return
        events
        |> List.filter (fun evt -> evt.OccurredAt.ToUniversalTime() >= windowStart)
        |> List.sumBy tokensOf
}

/// The `BudgetAccount` that records a refusal as an
/// `AITokenBudgetExceeded` event under its own reserved source module,
/// so "which users are hitting the wall" is a `ReadBySource` query.
///
/// `OnNearLimit` is deliberately silent. The verdict still carries the
/// warning — a caller that wants it has it — but writing a
/// not-yet-refused request into the `_platform.ai.budget_exceeded`
/// stream would make the one query this stream exists to answer wrong.
/// A near-limit stream is a separate decision, not a free rider on
/// this one.
let eventStoreAccount (store: IEventStore) (now: unit -> DateTime) : BudgetAccount =
    BudgetAccount.onRefused (fun denial -> async {
        let record = AITokenBudgetExceeded.ofDenial (now ()) denial

        let evt: ModuleEvent = {
            Id = Guid.NewGuid()
            OccurredAt = record.OccurredAt
            ScopeId = record.ScopeId
            SourceModule = AITokenBudgetExceeded.SourceModule
            EventType = AITokenBudgetExceeded.EventType
            Payload = JsonSerializer.Serialize(record, budgetJsonOptions)
        }

        try
            let! child = Async.StartChild(store.Write evt, BudgetAuditWriteTimeoutMs)
            do! child
        with _ ->
            // Timed out or threw. The refusal itself stands and the
            // user is being told; losing the record is strictly better
            // than converting a wedged event store into a second
            // failure on a path that has already failed.
            ()
    })

/// Phase 9s — the pre-call per-user token-budget check.
///
/// Stateless between invocations except for the read-through cache,
/// which the file header explains is not load-bearing (GP 12 rule 4).
/// Identity by value throughout — `scopeId` / `userId` are strings
/// (rule 1) — and every boundary is `Async` (rule 2).
type AIBudgetEnforcer
    (
        configStore: IConfigStore,
        eventStore: IEventStore,
        cache: AIBudgetWindowCache,
        account: BudgetAccount,
        now: unit -> DateTime
    ) =

    /// The live window cache — the `/dev/inspect` contributor's read.
    member _.Cache = cache

    /// The scope's configured policy, read fresh. Never cached: see the
    /// file header on why cap changes must not wait for an invalidation
    /// signal.
    member _.ReadPolicy(scopeId: string) : Async<AITokenBudgetPolicy> = async {
        try
            let scope: StorageScope = {
                ScopeId = scopeId
                Container = "_platform"
                Persist = true
            }

            let! raw = configStore.GetRaw(scope, AIBudgetConfigKey.value)
            return AITokenBudgetPolicy.ofRaw raw
        with _ ->
            return AITokenBudgetPolicy.unbounded
    }

    /// May `userId` spend an estimated `requested` more tokens in
    /// `scopeId` right now?
    ///
    /// Returns the full Phase 689 verdict, so a caller can act on
    /// `NearLimit` as well as `Refused`; the accounting has already run
    /// by the time it returns.
    member this.Check(scopeId: string, userId: string, requested: decimal) : Async<BudgetVerdict> = async {
        // An unattributable call is charged to nobody. A deployment
        // with no user identity (Anonymous) has no per-USER window to
        // enforce — Phase 9d's per-scope ceilings are the control
        // there, and they are unaffected.
        if String.IsNullOrWhiteSpace scopeId || String.IsNullOrWhiteSpace userId then
            return BudgetVerdict.Allowed
        else
            let! policy = this.ReadPolicy scopeId

            // GP 11 / GP 13. An unconfigured scope reads no events,
            // touches no cache and allocates no claim — the call is
            // byte-for-byte what it was before this phase.
            if AITokenBudgetPolicy.isUnbounded policy then
                return BudgetVerdict.Allowed
            else
                let at = now ()
                let periodKey = BudgetPeriod.key AITokenBudgetPolicy.perUserPeriod at
                let windowStart = hourWindowStart at

                let! spent = async {
                    match cache.TryRead(scopeId, userId, periodKey, at) with
                    | Some cached -> return cached
                    | None ->
                        try
                            let! computed = sumUserWindow eventStore scopeId userId windowStart

                            cache.Write {
                                ScopeId = scopeId
                                UserId = userId
                                PeriodKey = periodKey
                                UsedTokens = computed
                                CapAtLastSum = policy.PerUserPerHour |> Option.map decimal |> Option.defaultValue 0M
                                ComputedAt = at
                            }

                            return computed
                        with _ ->
                            // Fail open — see the file header. A window
                            // that could not be read is not evidence of
                            // overspend.
                            return 0M
                }

                let claim = AITokenBudgetPolicy.perUserClaim policy spent requested
                let subject = AITokenBudgetPolicy.subject scopeId userId

                let verdict =
                    BudgetPolicy.verdict subject periodKey BudgetPolicy.defaultWarnThreshold [ claim ]

                do! BudgetAccount.record account verdict
                return verdict
    }

/// `/dev/inspect` panel over the live windows (Phase 9s task 4).
///
/// Reports what the enforcer has actually evaluated recently, which is
/// the honest thing a stateless enforcer can say: there is no registry
/// of users to enumerate, and inventing one would be the load-bearing
/// state rule 4 forbids. Side-effect-free and O(live windows) — no
/// I/O, so it cannot slow the endpoint down.
type AITokenBudgetContributor(cache: AIBudgetWindowCache) =

    interface IDevDiagnosticsContributor with

        member _.Contribute() = async {
            let entries = cache.Snapshot()

            let payload = {|
                configKey = AIBudgetConfigKey.value
                capField = AIBudgetConfigKey.maxTokensPerUserPerHour
                window = BudgetPeriod.label AITokenBudgetPolicy.perUserPeriod
                cacheTtlSeconds = cache.TtlSeconds
                liveWindows = List.length entries
                // "Recent" is exact here: an entry survives only while
                // its hour is the current one, and the cap shown is the
                // one observed when the sum was taken, not necessarily
                // the one in force now.
                consumption =
                    entries
                    |> List.map (fun e -> {|
                        scopeId = e.ScopeId
                        userId = e.UserId
                        periodKey = e.PeriodKey
                        usedTokens = e.UsedTokens
                        capAtLastSum = e.CapAtLastSum
                        computedAt = e.ComputedAt
                    |})
            |}

            return "AI token budget", box payload
        }

// ─── Admin surface — the schema the config panel renders ─────────
//
// The per-team config panel is schema-driven: a `ModuleConfigEntry`
// declares the field and the existing Owner/Admin-gated config UI
// renders, validates and persists it. Declaring the schema IS the
// admin surface (the same route Phase 9d's `_platform.usage` quota tab
// takes) — writing a bespoke Fable panel for one integer would be a
// second editor for a value the generic one already edits, and a
// second place for its validation to drift.

/// SDK-shipped default `_platform.ai.budget` schema (Phase 9s).
///
/// One field, defaulting to `0` = unbounded, so a deployment that
/// surfaces the tab and sets nothing behaves exactly as it did (GP 11).
let sdkAIBudgetSchema: ModuleConfigEntry = {
    ModuleKey = AIBudgetConfigKey.value
    DisplayName = "AI Token Budget"
    Schema = {
        Fields = [
            {
                Key = AIBudgetConfigKey.maxTokensPerUserPerHour
                DisplayName = "Maximum AI tokens per user per hour"
                Description =
                    Some
                        "Hourly ceiling on AI tokens (prompt + output) for any ONE member of this team, across every provider. Complements the team-wide daily and monthly ceilings under Usage Quotas: this one stops a single member consuming the whole team's allowance. Consumption is summed from per-turn telemetry and resets at the top of each UTC hour. Set 0 to leave unrestricted."
                Kind = ConfigFieldKind.Int(Some 0, None)
                Required = false
                DefaultJson = "0"
            }
        ]
        SchemaVersion = 1
    }
}

/// Merge the SDK default into a deployment's declared entries.
///
/// Same semantics as `PlatformSchema.mergeUsageSchema`: an app that
/// declares its own `_platform.ai.budget` entry keeps its fields and
/// gains any SDK field it did not declare; an app that declares none
/// gets the SDK entry prepended, so the tab exists without every app
/// re-declaring it.
let mergeAIBudgetSchema (appEntries: ModuleConfigEntry list) : ModuleConfigEntry list =
    match appEntries |> List.tryFind (fun e -> e.ModuleKey = AIBudgetConfigKey.value) with
    | None -> sdkAIBudgetSchema :: appEntries
    | Some appBudget ->
        let appKeys = appBudget.Schema.Fields |> List.map _.Key |> Set.ofList

        let extraSdkFields =
            sdkAIBudgetSchema.Schema.Fields
            |> List.filter (fun f -> not (Set.contains f.Key appKeys))

        let merged = {
            appBudget with
                Schema = {
                    appBudget.Schema with
                        Fields = appBudget.Schema.Fields @ extraSdkFields
                }
        }

        appEntries
        |> List.map (fun e -> if e.ModuleKey = AIBudgetConfigKey.value then merged else e)