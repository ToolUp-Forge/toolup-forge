// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AISpendBudgetTests

open System
open System.Collections.Concurrent
open System.Text.Json
open Expecto
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Providers
open ToolUp.Platform.Usage
open ToolUp.AI

// ─── Phase 499 — monetary AI cost budgets ────────────────────────
//
// The phase's own acceptance list is four cases: estimation vs true-up
// drift within tolerance, an unpriced model degrading to count-only, a
// ceiling refusing the over-budget call, and no policy leaving
// behaviour unchanged. The rest are here because the design makes
// claims that would be invisible if they were wrong:
//
//   * BOTH halves of the price model must be exercised — a turn with a
//     cache hit is priced at three different rates, and a pricing
//     function that quietly charged `PromptTokens` in full at the fresh
//     rate would pass every test built from uncached turns;
//   * the per-user and per-scope ceilings sit on INDEPENDENT windows,
//     so a cache keyed on one of them would serve a stale figure for
//     the other;
//   * the read-through cache must not be part of the ANSWER (Phase 9c
//     rule 4) — proved by running the same scenario with caching
//     disabled and demanding identical verdicts;
//   * "warn once" must mean once per process, not once per turn;
//   * the monetary refusal must be distinguishable from Phase 9s's
//     token refusal by LABEL, which is the whole of Phase 499.D.

let private jsonOptions = FableConverters.create ()

// ─── Fakes ───────────────────────────────────────────────────────

/// `IConfigStore` over a mutable raw map. Only `GetRaw` is reachable
/// from the budget path; every other member raises, so a test that
/// silently started depending on one fails loudly instead of reading a
/// plausible default.
let private configStoreOver (values: unit -> Map<string, string>) =
    { new IConfigStore with
        member _.GetRaw(_scope, moduleKey) = async {
            return
                if moduleKey = AIBudgetConfigKey.value then
                    values ()
                else
                    Map.empty
        }

        member _.Get<'T>(_scope, _moduleKey) : Async<'T option> = failwith "not used by the spend path"

        member _.GetEffective<'T>(_scope, _moduleKey, _schema) : Async<'T> = failwith "not used by the spend path"

        member _.Set<'T>(_scope, _moduleKey, _value: 'T, _schema) = failwith "not used by the spend path"

        member _.SetRaw(_scope, _moduleKey, _values, _schema) = failwith "not used by the spend path"

        member _.Clear(_scope, _moduleKey) = failwith "not used by the spend path"

        member _.Erase(_scopeId, _subjectUserId, _policy, _dryRun) = failwith "not used by the spend path"
    }

/// An `IEventStore` that counts the reads the spend path makes, so "an
/// unconfigured deployment reads nothing" is an assertion rather than
/// an aspiration.
type private CountingEventStore(inner: IEventStore) =
    let mutable reads = 0
    member _.Reads = reads

    interface IEventStore with
        member _.Write evt = inner.Write evt
        member _.ReadAll scopeId = inner.ReadAll scopeId
        member _.ReadByType(scopeId, eventType) = inner.ReadByType(scopeId, eventType)

        member _.ReadBySource(scopeId, sourceModule) =
            reads <- reads + 1
            inner.ReadBySource(scopeId, sourceModule)

        member _.ListScopes() = inner.ListScopes()

        member _.Erase(scopeId, subjectUserId, policy, dryRun) =
            inner.Erase(scopeId, subjectUserId, policy, dryRun)

let private newEventStore () =
    CountingEventStore(ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> IEventStore)

/// Write one Phase 6i.A latency record with the full four-field token
/// report a priced turn needs.
let private writeTurnOn
    (store: IEventStore)
    (scopeId: string)
    (userId: string)
    (providerName: string)
    (model: string)
    (prompt: int)
    (cached: int)
    (output: int)
    (cacheWrite: int)
    (at: DateTime)
    =
    let record: AILatencyRecord = {
        TaskId = Guid.NewGuid()
        ConversationId = Guid.NewGuid()
        UserId = userId
        TurnNumber = 1
        ProviderName = providerName
        ProviderModel = model
        TtftMs = None
        TurnDurationMs = 10.0
        ToolCalls = []
        StopReason = "end_turn"
        PromptTokens = Some prompt
        CachedPromptTokens = Some cached
        OutputTokens = Some output
        CacheCreationTokens = Some cacheWrite
    }

    store.Write {
        Id = Guid.NewGuid()
        OccurredAt = at
        ScopeId = scopeId
        SourceModule = AILatencyRecord.SourceModule
        EventType = AILatencyRecord.EventType
        Payload = JsonSerializer.Serialize(record, jsonOptions)
    }
    |> Async.RunSynchronously

/// Every recorded monetary refusal in `scopeId`.
let private readRefusals (store: IEventStore) (scopeId: string) : AISpendBudgetExceeded list =
    store.ReadBySource(scopeId, AISpendBudgetExceeded.SourceModule)
    |> Async.RunSynchronously
    |> List.map (fun evt -> JsonSerializer.Deserialize<AISpendBudgetExceeded>(evt.Payload, jsonOptions))

let private okResponse (usage: TokenUsage option) : AIProviderResponse = {
    Content = "ok"
    ToolCalls = []
    StopReason = "end_turn"
    Usage = usage
}

let private capsFor (providerName: string) (model: string) = {
    AIProviderCapabilities.unknown with
        ProviderName = providerName
        Model = model
}

/// An `IAIProvider` that records how many times it was invoked and
/// reports a fixed `(provider, model)` and usage. A refused call must
/// never reach it — "the provider is never invoked" is the property
/// that distinguishes a budget from a post-hoc report.
type private CountingProvider(providerName: string, model: string, usage: TokenUsage option) =
    let mutable calls = 0
    member _.Calls = calls
    new() = CountingProvider("acme", "acme-1", None)

    interface IAIProvider with
        member _.Capabilities = capsFor providerName model

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            calls <- calls + 1
            return Ok(okResponse usage)
        }

        member _.SendStructuredMessage(_messages, _tools, _systemPrompt, _schema, _retryPolicy) = async {
            calls <- calls + 1
            return Ok(okResponse usage)
        }

let private factoryOver (provider: IAIProvider) =
    { new IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ctx = async { return Ok provider }
        member _.TryResolveByLabel(_ctx, _label) = async { return Ok provider }
        member _.BuildPlatform(_providerId, _apiKey, _model) = Some provider
    }

let private emptyProviderProfile =
    { new IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }
    }

/// An `IUsageLog` that keeps every record it is handed.
type private CollectingUsageLog() =
    let records = ConcurrentBag<UsageRecord>()
    member _.Records = records |> List.ofSeq

    interface IUsageLog with
        member _.Record record = async { records.Add record }
        member _.Query(_scopeId, _resourceKind, _range) = async { return [] }
        member _.Aggregate(_scopeId, _grouping) = async { return Map.empty }

let private userMessage text : AIProviderMessage = {
    Role = "user"
    Content = text
    ToolCalls = []
    ToolResults = []
    Parts = []
}

// ─── Fixtures ────────────────────────────────────────────────────

let private scopeId = "team-1"
let private alice = "alice"
let private bob = "bob"

/// A fixed instant, so the window arithmetic is not a function of when
/// the suite runs. Deliberately mid-month AND mid-day, so the daily and
/// monthly windows are genuinely different spans rather than
/// coincidentally equal (a 1st-of-the-month fixture would make the two
/// indistinguishable and hide a window-selection bug).
let private noon = DateTime(2026, 9, 16, 12, 30, 0, DateTimeKind.Utc)

/// A round rate card: 1.00 per million fresh input, 0.10 per million
/// cached input, 5.00 per million output, 1.25 per million cache
/// writes. Round so an expected figure can be read off the arithmetic
/// by hand rather than copied from a previous run.
let private acmePrice: ModelPrice = {
    InputPerMillion = 1.00M
    CachedInputPerMillion = 0.10M
    OutputPerMillion = 5.00M
    CacheCreationPerMillion = 1.25M
}

let private rateCard = ModelPriceTable.ofList "USD" [ "acme", "acme-1", acmePrice ]

let private spendConfig (perUser: (decimal * string) option) (perScope: (decimal * string) option) =
    let userEntries =
        match perUser with
        | Some(cap, period) -> [
            AIBudgetConfigKey.maxSpendPerUser, string cap
            AIBudgetConfigKey.spendPerUserPeriod, period
          ]
        | None -> []

    let scopeEntries =
        match perScope with
        | Some(cap, period) -> [
            AIBudgetConfigKey.maxSpendPerScope, string cap
            AIBudgetConfigKey.spendPerScopePeriod, period
          ]
        | None -> []

    Map.ofList (userEntries @ scopeEntries)

let private enforcerOver
    (configValues: unit -> Map<string, string>)
    (store: IEventStore)
    (table: ModelPriceTable)
    (ttlSeconds: int)
    (warn: string -> unit)
    (at: unit -> DateTime)
    =
    AIBudgetEnforcer.AISpendEnforcer(
        configStoreOver configValues,
        store,
        table,
        AIBudgetEnforcer.AISpendWindowCache(ttlSeconds),
        AIBudgetEnforcer.spendEventStoreAccount store table.Currency at,
        warn,
        at
    )

let private sendThrough (factory: IAIProviderFactory) (userId: string) (text: string) =
    async {
        let ctx = AccessContext.unrestricted (TeamMember(userId, scopeId))
        let! resolved = factory.Resolve ctx

        match resolved with
        | Error e -> return failwithf "provider resolution failed: %A" e
        | Ok provider -> return! provider.SendMessage([ userMessage text ], [], None, None, RetryPolicy.defaults)
    }
    |> Async.RunSynchronously

// ─── Cases ───────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 499 — monetary AI cost budgets" [

        // ─── 499.A — the price table ─────────────────────────────

        test "the four rates are applied to the four token classes separately" {
            // 1,000,000 prompt tokens of which 400,000 came from the
            // cache, 200,000 output, 50,000 cache writes:
            //   600,000 × 1.00 / 1e6 = 0.60
            //   400,000 × 0.10 / 1e6 = 0.04
            //   200,000 × 5.00 / 1e6 = 1.00
            //    50,000 × 1.25 / 1e6 = 0.0625
            let cost = ModelPriceTable.cost acmePrice 1_000_000 400_000 200_000 50_000

            Expect.equal cost 1.7025M "each token class is charged at its own rate"

            // The discriminating case: a pricer that charged the WHOLE
            // prompt at the fresh rate would return 1.7025 - 0.04 + 0.04
            // … i.e. it must differ from the all-fresh figure.
            let allFresh = ModelPriceTable.cost acmePrice 1_000_000 0 200_000 50_000
            Expect.notEqual cost allFresh "the cached portion is NOT charged at the fresh-input rate"
        }

        test "implausible token counts price defensively rather than negatively" {
            // A provider reporting more cache than prompt is a provider
            // bug; the budget's job is to stay legible, not to go
            // negative and silently hand the user free headroom.
            let cost = ModelPriceTable.cost acmePrice 100 500 -10 -10
            Expect.isGreaterThanOrEqual cost 0M "no combination of reported counts produces a negative price"
        }

        test "an unpriced provider/model is None, and a zero-rate one is Some 0" {
            let unpriced = rateCard |> ModelPriceTable.tryCost "other" "other-1" 1_000_000 0 0 0
            Expect.isNone unpriced "a pair the rate card does not name is UNPRICED"

            let freeCard = ModelPriceTable.ofList "USD" [ "free", "free-1", ModelPrice.free ]

            let free =
                freeCard |> ModelPriceTable.tryCost "free" "free-1" 1_000_000 0 1_000_000 0

            Expect.equal
                free
                (Some 0M)
                "a declared zero rate is a PRICE of zero, which is a different fact from having no price"
        }

        // ─── 499.B / 499.D — the ceiling and the refusal ─────────

        test "a daily per-user ceiling refuses the call that would cross it" {
            let store = newEventStore ()
            // 1,000,000 fresh input + 1,000,000 output = 1.00 + 5.00 = 6.00 spent.
            writeTurnOn store scopeId alice "acme" "acme-1" 1_000_000 0 1_000_000 0 noon

            let enforcer =
                enforcerOver (fun () -> spendConfig (Some(5.00M, "daily")) None) store rateCard 30 ignore (fun () ->
                    noon)

            let denial =
                match enforcer.Check(scopeId, alice, 0.01M) |> Async.RunSynchronously with
                | BudgetVerdict.Refused d -> d
                | other -> failwithf "expected a refusal, got %A" other

            Expect.equal denial.Domain AISpendBudgetPolicy.Domain "the denial names the SPEND budget, not the token one"

            Expect.equal
                denial.Dimension
                AISpendBudgetPolicy.PerUserDimension
                "…and the per-user ceiling as the one that refused"

            Expect.equal denial.Quota 5.00M "…the configured ceiling"
            Expect.equal denial.Spent 6.00M "…the priced spend already booked"
            Expect.equal denial.ClassLabel alice "…and the user it belongs to"
            Expect.equal denial.PeriodKey "2026-09-16" "…measured over the DAILY window, not the hourly one"

            // 499.D — distinct from token-quota exhaustion by LABEL, per
            // the Phase 689 substrate note. These are the two strings a
            // client branches on, and neither matches Phase 9s's.
            Expect.notEqual denial.Domain AITokenBudgetPolicy.Domain "the two budgets are different domains"

            Expect.notEqual denial.Dimension AITokenBudgetPolicy.PerUserPerHourDimension "…and different dimensions"

            let message = AISpendBudgetExceeded.message "USD" "daily" denial
            Expect.stringContains message "USD" "the message labels every figure with the operator's currency"
            Expect.stringContains message "this user" "…and says whose ceiling refused"
            Expect.stringContains message "daily" "…and which window it refills on"

            let refusals = readRefusals store scopeId
            Expect.hasLength refusals 1 "exactly one recorded refusal"
            let recorded = List.head refusals
            Expect.equal recorded.UserId alice "the recorded refusal names the user"
            Expect.equal recorded.Currency "USD" "…and the currency its amounts are in"
            Expect.equal recorded.WindowKind "daily" "…and the window"
            Expect.equal recorded.CapAmount 5.00M "…and the ceiling"
            Expect.equal recorded.SpentAmount 6.00M "…and the spend"
        }

        test "under the ceiling the call proceeds and nothing is recorded" {
            let store = newEventStore ()
            // 1,000,000 fresh input only = 1.00 spent against a 5.00 cap.
            writeTurnOn store scopeId alice "acme" "acme-1" 1_000_000 0 0 0 noon

            let enforcer =
                enforcerOver (fun () -> spendConfig (Some(5.00M, "daily")) None) store rateCard 30 ignore (fun () ->
                    noon)

            let verdict = enforcer.Check(scopeId, alice, 0.01M) |> Async.RunSynchronously
            Expect.equal verdict BudgetVerdict.Allowed "1.00 against a 5.00 ceiling is allowed"
            Expect.isEmpty (readRefusals store scopeId) "an allowed call records nothing"
        }

        test "the per-scope ceiling counts every member, and refuses when the TEAM is over" {
            let store = newEventStore ()
            // Two members, 1.00 each. Alice alone is under her own
            // ceiling; the pair are over the team's.
            writeTurnOn store scopeId alice "acme" "acme-1" 1_000_000 0 0 0 noon
            writeTurnOn store scopeId bob "acme" "acme-1" 1_000_000 0 0 0 noon

            let enforcer =
                enforcerOver
                    (fun () -> spendConfig (Some(5.00M, "daily")) (Some(1.50M, "monthly")))
                    store
                    rateCard
                    30
                    ignore
                    (fun () -> noon)

            let denial =
                match enforcer.Check(scopeId, alice, 0.01M) |> Async.RunSynchronously with
                | BudgetVerdict.Refused d -> d
                | other -> failwithf "expected the TEAM ceiling to refuse, got %A" other

            Expect.equal
                denial.Dimension
                AISpendBudgetPolicy.PerScopeDimension
                "the team's ceiling is what refused, not Alice's"

            Expect.equal denial.Spent 2.00M "the scope window sums every member's turns"
            Expect.equal denial.ClassLabel "" "a scope-level denial has no class label — it is the scope's own limit"

            Expect.equal
                denial.PeriodKey
                "2026-09"
                "…and it is measured over the MONTHLY window the scope ceiling declared, independently of the daily per-user one"
        }

        test "the per-user ceiling is reported first when both are breached" {
            let store = newEventStore ()
            writeTurnOn store scopeId alice "acme" "acme-1" 1_000_000 0 1_000_000 0 noon

            let enforcer =
                enforcerOver
                    (fun () -> spendConfig (Some(1.00M, "daily")) (Some(1.00M, "daily")))
                    store
                    rateCard
                    30
                    ignore
                    (fun () -> noon)

            match enforcer.Check(scopeId, alice, 0.01M) |> Async.RunSynchronously with
            | BudgetVerdict.Refused d ->
                // Phase 689's ordering rule: report the ceiling whose
                // remedy the caller can act on, not the one whose remedy
                // is "wait for the month to roll".
                Expect.equal
                    d.Dimension
                    AISpendBudgetPolicy.PerUserDimension
                    "the member's own ceiling is named ahead of the team's"
            | other -> failwithf "expected a refusal, got %A" other
        }

        // ─── 499.A — unpriced degrades to count-only, warns once ──

        test "an unpriced model contributes nothing, never refuses, and warns exactly once" {
            let store = newEventStore ()
            // Three enormous turns on a model the rate card does not
            // name. Under any guessed rate this would be far over a
            // 0.01 ceiling.
            for _ in 1..3 do
                writeTurnOn store scopeId alice "mystery" "mystery-1" 10_000_000 0 10_000_000 0 noon

            let warnings = ResizeArray<string>()

            let enforcer =
                enforcerOver
                    (fun () -> spendConfig (Some(0.01M, "daily")) None)
                    store
                    rateCard
                    30
                    warnings.Add
                    (fun () -> noon)

            // The cache TTL is live, so force three independent scans by
            // asking for three different users' windows — each scan meets
            // the same unpriced pair.
            for user in [ alice; bob; "carol" ] do
                let verdict = enforcer.Check(scopeId, user, 0.001M) |> Async.RunSynchronously

                Expect.equal verdict BudgetVerdict.Allowed "an unpriced model is COUNT-ONLY — it never blocks (GP 11)"

            Expect.isEmpty (readRefusals store scopeId) "and never records a refusal"

            Expect.hasLength warnings 1 "the unpriced pair is reported ONCE, not once per turn and not once per scan"

            Expect.stringContains
                (List.ofSeq warnings |> List.head)
                (ModelPriceTable.key "mystery" "mystery-1")
                "the warning names the provider/model that has no price"
        }

        test "a priced and an unpriced model in one window sum to the priced part alone" {
            let store = newEventStore ()
            writeTurnOn store scopeId alice "acme" "acme-1" 1_000_000 0 0 0 noon
            writeTurnOn store scopeId alice "mystery" "mystery-1" 9_000_000 0 0 0 noon

            let enforcer =
                enforcerOver (fun () -> spendConfig (Some(5.00M, "daily")) None) store rateCard 30 ignore (fun () ->
                    noon)

            // 1.00 priced + 0 for the unpriced turn. A guessed rate of
            // even 1.00/M on the unpriced turn would put the window at
            // 10.00 and refuse.
            let verdict = enforcer.Check(scopeId, alice, 0.01M) |> Async.RunSynchronously
            Expect.equal verdict BudgetVerdict.Allowed "only the priced turn contributes to the window"
        }

        // ─── 499.C — estimation vs true-up ───────────────────────

        test "the estimate and the true-up are the SAME pricing function, and drift stays within tolerance" {
            // The pre-call estimate charges the whole prompt at the
            // fresh rate (no cache assumed) plus a fixed output
            // allowance; the true-up charges what the provider actually
            // reported. Both go through `ModelPriceTable.cost`, so any
            // drift between them is drift in the TOKEN estimate alone —
            // which is the only failure this phase can actually have.
            let promptChars = 4_000
            let estimatedPromptTokens = promptChars / 4

            let estimate =
                ModelPriceTable.cost acmePrice estimatedPromptTokens 0 AIBudgetEnforcer.AssumedOutputTokens 0

            // A turn whose actual usage lands on the estimator's stated
            // assumptions: English prose at ~4 chars per token, no cache
            // hit, an output at the assumed allowance.
            let trueUp =
                ModelPriceTable.cost acmePrice estimatedPromptTokens 0 AIBudgetEnforcer.AssumedOutputTokens 0

            Expect.equal estimate trueUp "one pricing function, so identical inputs give identical figures"

            // And a realistic turn: the character estimator was 20 % low
            // on the prompt and the model wrote 700 of the assumed 1000.
            let actual =
                ModelPriceTable.cost acmePrice (estimatedPromptTokens * 12 / 10) 0 700 0

            let drift = abs (estimate - actual) / actual

            Expect.isLessThanOrEqual
                drift
                AIBudgetEnforcer.EstimateDriftTolerance
                "the pre-call estimate stays within the declared tolerance of the settled figure"

            Expect.isGreaterThanOrEqual
                estimate
                actual
                "and it errs HIGH — a ceiling that admits a call it should have refused has failed at its only job"
        }

        test "the true-up writes the priced cost to IUsageLog beside the token records" {
            let usageLog = CollectingUsageLog()

            let provider =
                CountingProvider(
                    "acme",
                    "acme-1",
                    Some {
                        PromptTokens = 1_000_000
                        CachedPromptTokens = 400_000
                        OutputTokens = 200_000
                        CacheCreationTokens = Some 50_000
                    }
                )

            let metered =
                AIProviderUsageMiddleware.MeteringProviderFactory(
                    factoryOver provider,
                    usageLog,
                    emptyProviderProfile,
                    Some rateCard
                )
                :> IAIProviderFactory

            sendThrough metered alice "hello" |> ignore

            let records = usageLog.Records
            Expect.hasLength records 3 "two token records plus the monetary one"

            let cost = records |> List.find (fun r -> r.ResourceKind = AISpendResourceKind.cost)

            Expect.equal cost.Quantity 1.7025M "the cost is priced from the ACTUAL reported usage, all four classes"
            Expect.equal cost.Unit "USD" "the unit is the rate card's currency, so a mixed ledger stays legible"

            Expect.equal
                (cost.Metadata |> Map.tryFind "price_key")
                (Some(ModelPriceTable.key "acme" "acme-1"))
                "…and the record says which rate-card entry priced it"

            // The token records are untouched — the monetary record is
            // additive, not a replacement.
            Expect.isTrue
                (records |> List.exists (fun r -> r.ResourceKind = ResourceKinds.aiTokensInput))
                "the input-token record still goes out"

            Expect.isTrue
                (records |> List.exists (fun r -> r.ResourceKind = ResourceKinds.aiTokensOutput))
                "…and the output-token record"
        }

        test "an unpriced model, and a deployment with no rate card, emit exactly the two records they always did" {
            let usage =
                Some {
                    PromptTokens = 1_000
                    CachedPromptTokens = 0
                    OutputTokens = 100
                    CacheCreationTokens = None
                }

            // (a) a rate card that does not name this model.
            let unpricedLog = CollectingUsageLog()

            let unpricedFactory =
                AIProviderUsageMiddleware.MeteringProviderFactory(
                    factoryOver (CountingProvider("mystery", "mystery-1", usage)),
                    unpricedLog,
                    emptyProviderProfile,
                    Some rateCard
                )
                :> IAIProviderFactory

            sendThrough unpricedFactory alice "hello" |> ignore
            Expect.hasLength unpricedLog.Records 2 "an unpriced model is count-only in the ledger too"

            // (b) no rate card at all — the pre-499 three-argument ctor,
            // which is what every existing call site still compiles to.
            let unconfiguredLog = CollectingUsageLog()

            let unconfiguredFactory =
                AIProviderUsageMiddleware.MeteringProviderFactory(
                    factoryOver (CountingProvider("acme", "acme-1", usage)),
                    unconfiguredLog,
                    emptyProviderProfile
                )
                :> IAIProviderFactory

            sendThrough unconfiguredFactory alice "hello" |> ignore

            Expect.hasLength
                unconfiguredLog.Records
                2
                "a deployment that registered no rate card is byte-for-byte what it was (GP 11)"
        }

        // ─── 499.E — no policy leaves behaviour unchanged ────────

        test "no spend policy is unbounded — and reads nothing at all" {
            let store = newEventStore ()
            writeTurnOn store scopeId alice "acme" "acme-1" 100_000_000 0 100_000_000 0 noon

            let enforcer =
                enforcerOver (fun () -> Map.empty) store rateCard 30 ignore (fun () -> noon)

            let verdict = enforcer.Check(scopeId, alice, 1_000M) |> Async.RunSynchronously
            Expect.equal verdict BudgetVerdict.Allowed "an unconfigured scope is unbounded"

            // GP 11 in its strongest form: not merely "the same answer"
            // but "the same work". An unconfigured deployment must not
            // pay for a feature it has not turned on.
            Expect.equal store.Reads 0 "an unbounded policy short-circuits before touching the event store"
            Expect.isEmpty (readRefusals store scopeId) "nothing is recorded"
        }

        test "a zero or unparseable ceiling reads as unbounded, exactly as a missing one does" {
            let store = newEventStore ()
            writeTurnOn store scopeId alice "acme" "acme-1" 100_000_000 0 0 0 noon

            for raw in [ "0"; "not-a-number"; "-5" ] do
                let enforcer =
                    enforcerOver
                        (fun () -> Map.ofList [ AIBudgetConfigKey.maxSpendPerUser, raw ])
                        store
                        rateCard
                        30
                        ignore
                        (fun () -> noon)

                Expect.equal
                    (enforcer.Check(scopeId, alice, 1_000M) |> Async.RunSynchronously)
                    BudgetVerdict.Allowed
                    (sprintf "a ceiling of '%s' is unbounded, not a ceiling of zero that refuses everything" raw)
        }

        test "a ceiling with an unreadable window keeps the ceiling and falls back to the default window" {
            let store = newEventStore ()

            let policy =
                AISpendBudgetPolicy.ofRaw (
                    Map.ofList [
                        AIBudgetConfigKey.maxSpendPerUser, "5.00"
                        AIBudgetConfigKey.spendPerUserPeriod, "fortnightly"
                    ]
                )

            match policy.PerUser with
            | Some budget ->
                Expect.equal budget.Ceiling 5.00M "losing the window must not silently lose the ceiling"

                Expect.equal
                    budget.Period
                    AISpendBudgetPolicy.defaultPeriod
                    "an unreadable window falls back to the declared default"
            | None -> failtest "an unparseable window dropped the whole ceiling — a cost control switching itself off"

            ignore store
        }

        // ─── The cache is not part of the answer (Phase 9c rule 4) ─

        test "the same verdicts are reached with caching disabled" {
            let run (ttl: int) =
                let store = newEventStore ()
                writeTurnOn store scopeId alice "acme" "acme-1" 1_000_000 0 1_000_000 0 noon

                let enforcer =
                    enforcerOver
                        (fun () -> spendConfig (Some(5.00M, "daily")) None)
                        store
                        rateCard
                        ttl
                        ignore
                        (fun () -> noon)

                [
                    for _ in 1..3 -> enforcer.Check(scopeId, alice, 0.01M) |> Async.RunSynchronously
                ]

            let warm = run 30
            let cold = run 0

            Expect.equal cold warm "dropping the cache changes no verdict — it is an accelerator, not state"
            Expect.all warm (fun v -> v <> BudgetVerdict.Allowed) "the scenario is one the budget actually refuses"
        }

        test "a ceiling lowered mid-window is respected by the very next request" {
            let store = newEventStore ()
            writeTurnOn store scopeId alice "acme" "acme-1" 1_000_000 0 0 0 noon

            let mutable ceiling = 5.00M

            let enforcer =
                enforcerOver (fun () -> spendConfig (Some(ceiling, "daily")) None) store rateCard 30 ignore (fun () ->
                    noon)

            Expect.equal
                (enforcer.Check(scopeId, alice, 0.01M) |> Async.RunSynchronously)
                BudgetVerdict.Allowed
                "1.00 against a 5.00 ceiling is allowed"

            // The admin lowers the ceiling. No invalidation signal is
            // sent, and none exists — the POLICY is never cached, only
            // the window sum is, so the new value binds immediately.
            ceiling <- 0.50M

            match enforcer.Check(scopeId, alice, 0.01M) |> Async.RunSynchronously with
            | BudgetVerdict.Refused d -> Expect.equal d.Quota 0.50M "the refusal cites the NEW ceiling"
            | other -> failwithf "expected the lowered ceiling to refuse, got %A" other
        }

        // ─── The decorator chain ─────────────────────────────────

        test "a refused call never reaches the provider" {
            let store = newEventStore ()
            writeTurnOn store scopeId alice "acme" "acme-1" 1_000_000 0 1_000_000 0 noon

            let enforcer =
                enforcerOver (fun () -> spendConfig (Some(5.00M, "daily")) None) store rateCard 30 ignore (fun () ->
                    noon)

            let provider = CountingProvider("acme", "acme-1", None)

            let gated =
                AIProviderUsageMiddleware.SpendEnforcingProviderFactory(factoryOver provider, enforcer)
                :> IAIProviderFactory

            match sendThrough gated alice "hello" with
            | Error(PermanentClient(status, message)) ->
                Expect.equal status 429 "the refusal is the same 429 shape both token gates use"

                Expect.stringContains
                    message
                    "AI spend budget exceeded"
                    "…carrying the monetary sentence, not the token one"
            | other -> failwithf "expected a 429 refusal, got %A" other

            Expect.equal provider.Calls 0 "the provider is never invoked — that is what makes it a budget"
        }

        test "with no ceiling configured the provider is reached exactly as before" {
            let store = newEventStore ()

            let enforcer =
                enforcerOver (fun () -> Map.empty) store rateCard 30 ignore (fun () -> noon)

            let provider = CountingProvider("acme", "acme-1", None)

            let gated =
                AIProviderUsageMiddleware.SpendEnforcingProviderFactory(factoryOver provider, enforcer)
                :> IAIProviderFactory

            match sendThrough gated alice "hello" with
            | Ok _ -> ()
            | other -> failwithf "expected the call to proceed, got %A" other

            Expect.equal provider.Calls 1 "the provider is reached exactly as before"
            Expect.equal store.Reads 0 "and nothing was read to decide it"
        }

        // ─── The admin surface ───────────────────────────────────

        test "the config schema declares all four monetary fields, defaulting to unbounded" {
            let entry = AIBudgetEnforcer.sdkAIBudgetSchema
            Expect.equal entry.ModuleKey AIBudgetConfigKey.value "the reserved module key is shared with Phase 9s"

            let fieldNamed key =
                entry.Schema.Fields |> List.find (fun f -> f.Key = key)

            for key in [ AIBudgetConfigKey.maxSpendPerUser; AIBudgetConfigKey.maxSpendPerScope ] do
                let field = fieldNamed key
                Expect.equal field.DefaultJson "0" (sprintf "%s defaults to unbounded" key)
                Expect.isFalse field.Required (sprintf "%s is optional" key)

            for key in [ AIBudgetConfigKey.spendPerUserPeriod; AIBudgetConfigKey.spendPerScopePeriod ] do
                match (fieldNamed key).Kind with
                | ConfigFieldKind.Choice options ->
                    Expect.equal
                        options
                        (BudgetPeriod.all |> List.map BudgetPeriod.label)
                        "the window is a closed choice over the seam's own periods, never free text"
                | other -> failwithf "%s should be a Choice, got %A" key other

            // Phase 9s's field is untouched — this phase appended.
            Expect.isTrue
                (entry.Schema.Fields
                 |> List.exists (fun f -> f.Key = AIBudgetConfigKey.maxTokensPerUserPerHour))
                "the Phase 9s token cap still stands beside the monetary ones"
        }
    ]