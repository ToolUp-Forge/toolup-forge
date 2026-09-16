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

/// Phase 499 — the monetary ceilings, appended to the same
/// `_platform.ai.budget` schema Phase 9s declares.
///
/// Four fields, defaulting to `0` / `"daily"` so a deployment that
/// surfaces the tab and sets nothing behaves exactly as it did (GP 11).
/// The ceilings are `Float`-kinded because that is the shipped
/// precedent for a decimal-valued ceiling in this codebase
/// (`PlatformSchema`'s `MaxAITokensPerDay`): the persisted layer is
/// `Map<string, string>` and the validator preserves the wire text via
/// `GetRawText`, so the stored value never makes a float round-trip and
/// the read parses it as `decimal`.
let spendBudgetFields: ConfigFieldSchema list = [
    {
        Key = AIBudgetConfigKey.maxSpendPerUser
        DisplayName = "Maximum AI spend per user"
        Description =
            Some
                "Ceiling on what any ONE member of this team may spend on AI within the window below, in the currency of the rate card this deployment registered. Spend is priced from per-turn telemetry using that rate card; a model with no declared price is counted only and never blocks. Set 0 to leave unrestricted."
        Kind = ConfigFieldKind.Float(Some 0.0, None)
        Required = false
        DefaultJson = "0"
    }
    {
        Key = AIBudgetConfigKey.spendPerUserPeriod
        DisplayName = "Per-user spend window"
        Description = Some "The window the per-user spend ceiling is measured over, and refills at (UTC)."
        Kind = ConfigFieldKind.Choice(BudgetPeriod.all |> List.map BudgetPeriod.label)
        Required = false
        DefaultJson = "\"daily\""
    }
    {
        Key = AIBudgetConfigKey.maxSpendPerScope
        DisplayName = "Maximum AI spend for this team"
        Description =
            Some
                "Ceiling on this team's TOTAL AI spend within the window below, in the currency of the registered rate card. Complements the per-user ceiling above: this one bounds the bill, that one stops a single member consuming it. Set 0 to leave unrestricted."
        Kind = ConfigFieldKind.Float(Some 0.0, None)
        Required = false
        DefaultJson = "0"
    }
    {
        Key = AIBudgetConfigKey.spendPerScopePeriod
        DisplayName = "Team spend window"
        Description = Some "The window the team spend ceiling is measured over, and refills at (UTC)."
        Kind = ConfigFieldKind.Choice(BudgetPeriod.all |> List.map BudgetPeriod.label)
        Required = false
        DefaultJson = "\"daily\""
    }
]

/// SDK-shipped default `_platform.ai.budget` schema (Phase 9s; the
/// monetary ceilings appended by Phase 499).
///
/// Every field defaults to `0` (= unbounded) or to the default window,
/// so a deployment that surfaces the tab and sets nothing behaves
/// exactly as it did (GP 11).
///
/// `DisplayName` says "AI Budgets" rather than 9s's "AI Token Budget"
/// because the tab now holds two units. It is an operator-facing label
/// on a generated panel, not a contract: nothing keys on it.
let sdkAIBudgetSchema: ModuleConfigEntry = {
    ModuleKey = AIBudgetConfigKey.value
    DisplayName = "AI Budgets"
    Schema = {
        Fields =
            [
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
            @ spendBudgetFields
        // Bumped from 9s's `1`: the schema gained four fields. Every
        // one of them has a default, so a document persisted under
        // version 1 stays valid and reads identically — the version
        // records that the shape moved, it does not gate anything.
        SchemaVersion = 2
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
// ─── Phase 499 — the monetary spend-budget check ─────────────────
//
// The enforcement half of the Phase 499 types: price the scope's
// recent turns off the same always-on latency stream Phase 9s sums,
// compare against the operator's monetary ceilings, and hand back a
// Phase 689 `BudgetVerdict`. The provider decorator that consumes it
// lives beside 9s's in `AIProviderUsageMiddleware.fs` — one chain,
// three windows, not three enforcement paths.
//
// **Why the same stream and not `IUsageLog`.** Phase 9s's reasoning
// carries over unchanged and is decisive: the usage log is written
// only when `UsageMetering = EnabledUsageMetering`, so a spend ceiling
// summing it would silently never refuse on a deployment that set a
// budget but had not also enabled metering — a cost control that is
// off when you think it is on. The latency stream is always-on and,
// since Phase 6i.A / 6i.B, already carries the four token counts AND
// the `(ProviderName, ProviderModel)` pair a price needs. Phase
// 499.C's `IUsageLog` cost record is the BILLING artefact, written
// beside the token records by the metering decorator; it is not what
// the ceiling reads.
//
// **Reserve an estimate, settle the actual — on this substrate.** The
// pre-call figure is the `Requested` half of the claim and is an
// estimate (nobody can know a turn's output before the model writes
// it). `Spent` is never an estimate: it is the priced sum of what
// providers actually reported. So the true-up is not a second write
// that has to be reconciled — the next call's window sum IS the
// settled figure, which is the shape Phase 689's substrate note calls
// "the ledger's `Release` cost adjustment" on a domain whose
// consumption is derived rather than reserved.
//
// **Unpriced degrades to count-only and warns once (GP 11).** A turn
// on a `(providerId, model)` the operator has not priced contributes
// `0` to the window rather than a guessed rate, and the pair is
// reported once through the injected `warn` sink and permanently on
// the `/dev/inspect` panel. It is never a refusal: a budget that
// blocked on an unpriced model would turn adding a model into an
// outage.
//
// **It fails OPEN**, for the reason 9s states: a spend ceiling is a
// cost control, not an authorization control.

/// Output tokens the PRE-CALL estimate assumes a turn will generate.
///
/// Nobody can know a turn's output before the model writes it, so the
/// estimate has to assume something, and the choice is between erring
/// high (refuse marginally early) and erring low (admit one call that
/// overshoots the ceiling). `1000` errs high against a typical
/// assistant turn — it is at the upper end of what a chat answer
/// generates — because a ceiling that admits a call it should have
/// refused has failed at the one job it has, while one that refuses a
/// call it could have admitted costs the user a wait they were within
/// a thousand tokens of anyway.
///
/// The honest cost, stated rather than hidden: on a deployment whose
/// turns are mostly short, the last call before a ceiling is refused
/// slightly sooner than a perfect estimator would refuse it. Nothing
/// downstream depends on the figure — `Spent` is always the PRICED
/// ACTUAL from the telemetry stream, so an estimate that is wrong
/// affects one admission decision and never the recorded spend.
[<Literal>]
let AssumedOutputTokens = 1_000

/// Relative drift the pre-call estimate is held to against the
/// post-call true-up, for a turn within the estimator's stated
/// assumptions (English prose at roughly 4 characters per token, no
/// cache hit, output at or under `AssumedOutputTokens`).
///
/// `0.5` — fifty percent. Deliberately loose, and loose for a reason
/// that is worth stating rather than tightening away: the character
/// estimator is Phase 9d's advisory 4-chars-per-token ratio, which
/// drifts materially on code-heavy or non-English prompts, and the
/// output term is an assumption rather than a measurement. A tolerance
/// tight enough to look impressive would be a test that fails on a
/// realistic prompt, which teaches a future session to widen the
/// tolerance rather than to look at the estimator. What the bound
/// genuinely asserts is that the two figures are the same ORDER — i.e.
/// that both sides price through `ModelPriceTable.cost` and neither has
/// silently acquired a second price model, which is the failure this
/// phase can actually have.
let EstimateDriftTolerance = 0.5M

/// One cached spend window, plus the figures that make the
/// `/dev/inspect` panel readable.
type AISpendWindowEntry = {
    ScopeId: string
    UserId: string
    /// `BudgetPeriod.key` of the window the USER figure covers.
    UserPeriodKey: string
    /// `BudgetPeriod.key` of the window the SCOPE figure covers.
    ScopePeriodKey: string
    /// Priced spend this user had booked in `UserPeriodKey`.
    UserSpend: decimal
    /// Priced spend the whole scope had booked in `ScopePeriodKey`.
    ScopeSpend: decimal
    /// Turns in the scanned windows whose `(provider, model)` had no
    /// declared price and therefore contributed nothing.
    UnpricedTurns: int
    ComputedAt: DateTime
}

/// Read-through cache over the per-(scope, user) spend sums.
///
/// Keyed by the identities only, with BOTH period keys stored in the
/// entry and checked on read: the two ceilings can be measured over
/// different windows (a daily per-user cap under a monthly team cap),
/// so a key built from one of them would serve a stale figure for the
/// other. Either window rolling misses the entry, which is the same
/// "the period is a storage key, not a counter with a reset job"
/// property Phase 9s's cache relies on.
///
/// Exactly as in Phase 9s, the cache is a read-through accelerator
/// over a DERIVED figure and is not part of the answer (Phase 9c rule
/// 4): drop it, restart, or run two replicas that never share it and
/// every verdict is still computed from the event store. The policy is
/// never cached, so lowering a ceiling mid-window is respected by the
/// very next request.
type AISpendWindowCache(ttlSeconds: int) =
    let entries = ConcurrentDictionary<struct (string * string), AISpendWindowEntry>()
    let unpriced = ConcurrentDictionary<string, unit>()

    /// The default TTL (`WindowCacheTtlSeconds`, shared with Phase 9s —
    /// the two caches accelerate scans of the same stream and a
    /// different staleness budget for each would be arbitrary).
    new() = AISpendWindowCache(WindowCacheTtlSeconds)

    member _.TtlSeconds = ttlSeconds

    /// The cached sums for these windows, if computed within the TTL
    /// and still covering BOTH period keys. `None` on a miss, an
    /// expired entry, a rolled window, and always when `ttlSeconds <=
    /// 0` (the caching-disabled setting a test uses to prove the answer
    /// does not depend on the cache).
    member _.TryRead
        (scopeId: string, userId: string, userPeriodKey: string, scopePeriodKey: string, now: DateTime)
        : AISpendWindowEntry option =
        if ttlSeconds <= 0 then
            None
        else
            match entries.TryGetValue(struct (scopeId, userId)) with
            | true, entry when
                entry.UserPeriodKey = userPeriodKey
                && entry.ScopePeriodKey = scopePeriodKey
                && (now - entry.ComputedAt).TotalSeconds < float ttlSeconds
                ->
                Some entry
            | _ -> None

    /// Record freshly-computed sums, dropping any entry whose windows
    /// have elapsed so the diagnostics panel shows the CURRENT windows
    /// rather than an archaeology of past ones.
    member _.Write(entry: AISpendWindowEntry) =
        if ttlSeconds > 0 then
            entries[struct (entry.ScopeId, entry.UserId)] <- entry

            for KeyValue(key, value) in entries do
                if
                    value.UserPeriodKey <> entry.UserPeriodKey
                    || value.ScopePeriodKey <> entry.ScopePeriodKey
                then
                    entries.TryRemove key |> ignore

    /// Every live entry, heaviest spender first — the `/dev/inspect`
    /// read.
    member _.Snapshot() : AISpendWindowEntry list =
        entries.Values |> Seq.sortByDescending _.UserSpend |> List.ofSeq

    /// Drop everything. Not part of the decision path — it exists so a
    /// test can prove the enforcer computes the same verdict with an
    /// empty cache as with a warm one. Deliberately does NOT clear the
    /// unpriced latch below: "warn once" means once, and a test that
    /// empties the cache to prove the verdict is cache-independent must
    /// not thereby re-arm a warning.
    member _.Clear() = entries.Clear()

    /// `(providerId, model)` keys already warned about.
    ///
    /// The "warn once" latch lives on the CACHE rather than on the
    /// enforcer because the cache is the singleton: `wrapFactoryForDI`
    /// builds an enforcer per resolution, so a per-enforcer latch would
    /// warn once per composition rather than once per process — which
    /// is not what "once" means to whoever is reading the log.
    member _.UnpricedKeys = unpriced.Keys |> List.ofSeq

    /// Claim the right to warn about `key`. `true` for the FIRST caller
    /// only; every later caller, including a concurrent one, gets
    /// `false`. `TryAdd` is the whole mechanism — no lock, and no
    /// window in which two turns on a newly-added model both warn.
    member _.TryMarkUnpriced(key: string) : bool = unpriced.TryAdd(key, ())

/// The start of the `period` that `at` falls in.
///
/// Must agree with `BudgetPeriod.key`, which is why both are computed
/// from the same `ToUniversalTime()` value: a window start that
/// disagreed with the period key would sum one window and record the
/// refusal against another. `Perpetual` has no start, so it reads as
/// `DateTime.MinValue` in UTC — every record ever written is inside a
/// perpetual window, which is what "never refills" means.
let periodWindowStart (period: BudgetPeriod) (at: DateTime) : DateTime =
    let utc = at.ToUniversalTime()

    match period with
    | BudgetPeriod.Perpetual -> DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    | BudgetPeriod.Hourly -> DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc)
    | BudgetPeriod.Daily -> DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc)
    | BudgetPeriod.Monthly -> DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc)

/// One turn's priced contribution to the two windows.
type private PricedTurn = {
    /// `AILatencyRecord.UserId`, `""` when the record predates Phase 9s.
    UserId: string
    OccurredAt: DateTime
    /// The turn's cost under the rate card, or `None` when its
    /// `(provider, model)` is unpriced.
    Cost: decimal option
    /// `ModelPriceTable.key` of the turn — reported when unpriced.
    PriceKey: string
}

/// Price every turn in the scope's latency stream from `earliest`
/// onward, once, and return the priced turns plus the unpriced keys
/// seen.
///
/// One `ReadBySource` scan serves BOTH ceilings: the per-user figure
/// filters the same list the per-scope figure sums, so a deployment
/// with two ceilings pays for one scan rather than two. `earliest` is
/// the earlier of the two window starts.
let private priceWindow
    (store: IEventStore)
    (table: ModelPriceTable)
    (scopeId: string)
    (earliest: DateTime)
    : Async<PricedTurn list * Set<string>> =
    async {
        let! events = store.ReadBySource(scopeId, AILatencyRecord.SourceModule)

        let priced =
            events
            |> List.filter (fun evt -> evt.OccurredAt.ToUniversalTime() >= earliest)
            |> List.choose (fun evt ->
                try
                    let record =
                        JsonSerializer.Deserialize<AILatencyRecord>(evt.Payload, budgetJsonOptions)

                    if isNull (box record) then
                        None
                    else
                        let prompt = record.PromptTokens |> Option.defaultValue 0
                        let cached = record.CachedPromptTokens |> Option.defaultValue 0
                        let output = record.OutputTokens |> Option.defaultValue 0
                        let cacheWrite = record.CacheCreationTokens |> Option.defaultValue 0

                        Some {
                            UserId = if isNull (box record.UserId) then "" else record.UserId
                            OccurredAt = evt.OccurredAt.ToUniversalTime()
                            Cost =
                                table
                                |> ModelPriceTable.tryCost
                                    record.ProviderName
                                    record.ProviderModel
                                    prompt
                                    cached
                                    output
                                    cacheWrite
                            PriceKey = ModelPriceTable.key record.ProviderName record.ProviderModel
                        }
                with _ ->
                    // One malformed payload must not blind the window to
                    // every other record in it — the same choice
                    // `AILatencyHandler.decodePayload` and Phase 9s's
                    // token sum both make.
                    None)

        let unpriced =
            priced
            |> List.choose (fun turn -> if turn.Cost.IsNone then Some turn.PriceKey else None)
            |> Set.ofList

        return priced, unpriced
    }

/// The `BudgetAccount` that records a monetary refusal as an
/// `AISpendBudgetExceeded` event under its own reserved source module.
///
/// `OnNearLimit` is deliberately silent, for the reason Phase 9s gives:
/// writing a not-yet-refused request into the refusal stream would make
/// the one query that stream exists to answer wrong.
/// `policy` comes LAST so `spendEventStoreAccount store currency now`
/// partially applies to exactly the `AISpendBudgetPolicy ->
/// BudgetAccount` shape `AISpendEnforcer`'s `accountOf` takes — the
/// policy is the only argument that is not known until the check reads
/// it, and it is needed here to label the refusal's window.
let spendEventStoreAccount
    (store: IEventStore)
    (currency: string)
    (now: unit -> DateTime)
    (policy: AISpendBudgetPolicy)
    : BudgetAccount =
    BudgetAccount.onRefused (fun denial -> async {
        let record = AISpendBudgetExceeded.ofDenial (now ()) currency policy denial

        let evt: ModuleEvent = {
            Id = Guid.NewGuid()
            OccurredAt = record.OccurredAt
            ScopeId = record.ScopeId
            SourceModule = AISpendBudgetExceeded.SourceModule
            EventType = AISpendBudgetExceeded.EventType
            Payload = JsonSerializer.Serialize(record, budgetJsonOptions)
        }

        try
            let! child = Async.StartChild(store.Write evt, BudgetAuditWriteTimeoutMs)
            do! child
        with _ ->
            // The refusal itself stands and the user is being told;
            // losing the record is strictly better than a second failure
            // on a path that has already failed.
            ()
    })

/// Phase 499 — the pre-call monetary spend check.
///
/// `warn` is the once-per-`(providerId, model)` unpriced-model sink,
/// supplied as data rather than resolved here (GP 12 rule 3): the
/// composition root hands it an `ILogger`, a test hands it a collector.
/// The latch is per-enforcer and therefore per-process, which is what
/// "warn once" can honestly mean without shared state a restart would
/// have to preserve.
type AISpendEnforcer
    (
        configStore: IConfigStore,
        eventStore: IEventStore,
        priceTable: ModelPriceTable,
        cache: AISpendWindowCache,
        accountOf: AISpendBudgetPolicy -> BudgetAccount,
        warn: string -> unit,
        now: unit -> DateTime
    ) =

    /// The live window cache — the `/dev/inspect` contributor's read.
    member _.Cache = cache

    /// The rate card this enforcer prices against.
    member _.PriceTable = priceTable


    /// The scope's configured monetary policy, read fresh. Never
    /// cached: see the Phase 9s header on why ceiling changes must not
    /// wait for an invalidation signal that does not exist.
    member _.ReadPolicy(scopeId: string) : Async<AISpendBudgetPolicy> = async {
        try
            let scope: StorageScope = {
                ScopeId = scopeId
                Container = "_platform"
                Persist = true
            }

            let! raw = configStore.GetRaw(scope, AIBudgetConfigKey.value)
            return AISpendBudgetPolicy.ofRaw raw
        with _ ->
            return AISpendBudgetPolicy.unbounded
    }

    /// May `userId` spend an estimated `requested` (in the rate card's
    /// currency) in `scopeId` right now?
    ///
    /// Returns the full Phase 689 verdict, so a caller can act on
    /// `NearLimit` as well as `Refused`; the accounting has already run
    /// by the time it returns.
    member this.Check(scopeId: string, userId: string, requested: decimal) : Async<BudgetVerdict> = async {
        if String.IsNullOrWhiteSpace scopeId then
            return BudgetVerdict.Allowed
        else
            let! policy = this.ReadPolicy scopeId

            // GP 11 / GP 13. An unconfigured scope reads no events,
            // touches no cache and allocates no claim — the call is
            // byte-for-byte what it was before this phase.
            if AISpendBudgetPolicy.isUnbounded policy then
                return BudgetVerdict.Allowed
            else
                let at = now ()
                let caller = if isNull (box userId) then "" else userId

                // A ceiling that is not configured is measured over the
                // default window, which costs nothing: its claim is
                // unrestricted and `BudgetClaim.isUnrestricted` short-
                // circuits it out of the breach scan.
                let userPeriod =
                    policy.PerUser
                    |> Option.map _.Period
                    |> Option.defaultValue AISpendBudgetPolicy.defaultPeriod

                let scopePeriod =
                    policy.PerScope
                    |> Option.map _.Period
                    |> Option.defaultValue AISpendBudgetPolicy.defaultPeriod

                let userPeriodKey = BudgetPeriod.key userPeriod at
                let scopePeriodKey = BudgetPeriod.key scopePeriod at
                let userStart = periodWindowStart userPeriod at
                let scopeStart = periodWindowStart scopePeriod at

                let! entry = async {
                    match cache.TryRead(scopeId, caller, userPeriodKey, scopePeriodKey, at) with
                    | Some cached -> return cached
                    | None ->
                        try
                            let earliest = if userStart < scopeStart then userStart else scopeStart
                            let! priced, unpriced = priceWindow eventStore priceTable scopeId earliest

                            // Warn once per unpriced `(provider, model)`.
                            // `TryAdd` is the latch: the first caller to
                            // add the key is the one that warns, so two
                            // concurrent turns on a new model produce one
                            // warning rather than two.
                            for key in unpriced do
                                if cache.TryMarkUnpriced key then
                                    warn (
                                        sprintf
                                            "AI spend budget: no price declared for '%s'. Turns on this provider/model contribute nothing to the spend window and are counted only (Phase 499, GP 11). Declare a rate in the registered ModelPriceTable to include them."
                                            key
                                    )

                            // An unpriced turn contributes nothing —
                            // count-only, never a guessed rate.
                            let sumFrom (windowStart: DateTime) (keep: PricedTurn -> bool) =
                                priced
                                |> List.sumBy (fun turn ->
                                    if turn.OccurredAt >= windowStart && keep turn then
                                        turn.Cost |> Option.defaultValue 0M
                                    else
                                        0M)

                            // A record with no `UserId` (written before
                            // Phase 9s) is UNATTRIBUTED and charged to
                            // nobody in the per-USER window — wrong in
                            // the safe direction. It still counts in the
                            // per-SCOPE window, where attribution is not
                            // needed and dropping it would understate the
                            // team's actual bill.
                            let userSpend =
                                if caller = "" then
                                    0M
                                else
                                    sumFrom userStart (fun turn -> turn.UserId = caller)

                            let computed = {
                                ScopeId = scopeId
                                UserId = caller
                                UserPeriodKey = userPeriodKey
                                ScopePeriodKey = scopePeriodKey
                                UserSpend = userSpend
                                ScopeSpend = sumFrom scopeStart (fun _ -> true)
                                UnpricedTurns = priced |> List.filter (fun t -> t.Cost.IsNone) |> List.length
                                ComputedAt = at
                            }

                            cache.Write computed
                            return computed
                        with _ ->
                            // Fail open — a window that could not be read
                            // is not evidence of overspend.
                            return {
                                ScopeId = scopeId
                                UserId = caller
                                UserPeriodKey = userPeriodKey
                                ScopePeriodKey = scopePeriodKey
                                UserSpend = 0M
                                ScopeSpend = 0M
                                UnpricedTurns = 0
                                ComputedAt = at
                            }
                }

                // Per-user first, then per-scope. `BudgetPolicy.breach`
                // reports the FIRST ceiling hit and the order is the
                // caller's to choose (Phase 689): the member's own
                // ceiling has a remedy they can act on, the team's
                // monthly cap does not.
                //
                // An unattributable caller (`""`) has no per-user window
                // to enforce, so its claim is dropped rather than
                // evaluated against a zero spend — which would refuse
                // only once the ESTIMATE alone exceeded the ceiling, a
                // verdict about nobody.
                let userClaims =
                    match policy.PerUser with
                    | Some budget when caller <> "" -> [
                        AISpendBudgetPolicy.claim AISpendBudgetPolicy.PerUserDimension budget entry.UserSpend requested
                      ]
                    | _ -> []

                let scopeClaims =
                    match policy.PerScope with
                    | Some budget -> [
                        AISpendBudgetPolicy.claim
                            AISpendBudgetPolicy.PerScopeDimension
                            budget
                            entry.ScopeSpend
                            requested
                      ]
                    | None -> []

                let claims = userClaims @ scopeClaims

                if List.isEmpty claims then
                    return BudgetVerdict.Allowed
                else
                    // The denial's `PeriodKey` must name the window the
                    // BREACHED ceiling was measured in. The two ceilings
                    // can sit on different windows, so the key is chosen
                    // after the breach is known rather than passed in
                    // blind.
                    let verdictFor (periodKey: string) (subject: BudgetSubject) (cs: BudgetClaim list) =
                        BudgetPolicy.verdict subject periodKey BudgetPolicy.defaultWarnThreshold cs

                    let userVerdict =
                        verdictFor userPeriodKey (AISpendBudgetPolicy.userSubject scopeId caller) userClaims

                    let verdict =
                        match userVerdict with
                        | BudgetVerdict.Refused _ -> userVerdict
                        | _ ->
                            let scopeVerdict =
                                verdictFor scopePeriodKey (AISpendBudgetPolicy.scopeSubject scopeId) scopeClaims

                            match scopeVerdict with
                            | BudgetVerdict.Refused _ -> scopeVerdict
                            | BudgetVerdict.NearLimit _ ->
                                match userVerdict with
                                | BudgetVerdict.NearLimit _ -> userVerdict
                                | _ -> scopeVerdict
                            | BudgetVerdict.Allowed -> userVerdict

                    do! BudgetAccount.record (accountOf policy) verdict
                    return verdict
    }

/// `/dev/inspect` panel over the live spend windows (Phase 499).
///
/// Reports what the enforcer has actually evaluated recently — the
/// honest thing a stateless enforcer can say — plus the rate card's
/// shape and every `(provider, model)` it has met with no declared
/// price, which is the question an operator asks when a budget refuses
/// later than they expected.
/// Takes the CACHE (the DI singleton both it and the enforcer share)
/// and the rate card as an option, rather than the enforcer: the
/// enforcer is built per DI resolution inside `wrapFactoryForDI` and is
/// not a service anything can resolve, and an absent rate card is the
/// state an operator most wants this panel to report.
type AISpendBudgetContributor(cache: AISpendWindowCache, priceTable: ModelPriceTable option) =

    interface IDevDiagnosticsContributor with

        member _.Contribute() = async {
            let entries = cache.Snapshot()

            let payload = {|
                configKey = AIBudgetConfigKey.value
                // No rate card registered means the spend layer is not
                // composed at all: the ceilings in the config tab are
                // read by nothing. Saying so plainly is the single most
                // useful thing this panel does.
                rateCardRegistered = priceTable.IsSome
                currency = priceTable |> Option.map _.Currency |> Option.defaultValue ""
                pricedModels = priceTable |> Option.map (_.Rates >> Map.count) |> Option.defaultValue 0
                unpricedModelsSeen = cache.UnpricedKeys
                cacheTtlSeconds = cache.TtlSeconds
                liveWindows = List.length entries
                consumption =
                    entries
                    |> List.map (fun e -> {|
                        scopeId = e.ScopeId
                        userId = e.UserId
                        userPeriodKey = e.UserPeriodKey
                        scopePeriodKey = e.ScopePeriodKey
                        userSpend = e.UserSpend
                        scopeSpend = e.ScopeSpend
                        unpricedTurns = e.UnpricedTurns
                        computedAt = e.ComputedAt
                    |})
            |}

            return "AI spend budget", box payload
        }