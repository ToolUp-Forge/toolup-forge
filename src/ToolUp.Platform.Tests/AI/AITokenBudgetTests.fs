module ToolUp.Platform.Tests.AI.AITokenBudgetTests

open System
open System.Text.Json
open Expecto
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Usage
open ToolUp.AI

// ─── Phase 9s — the per-user AI token budget ─────────────────────
//
// Six of these cases are the phase's own acceptance list; the other
// three exist because the design made claims that would be invisible
// if they were wrong:
//
//   * the read-through cache is a perf optimisation and NOT part of
//     the answer (Phase 9c rule 4) — proved by running the same
//     scenario with caching disabled and demanding the same verdicts;
//   * a latency record written before 9s has no `UserId` and must be
//     charged to nobody rather than to whoever asks first — proved by
//     feeding the enforcer a JSON payload that OMITS the field, which
//     is the exact shape on disk in every deployment that upgrades;
//   * the refusal WORDING is pinned, because it is what a user reads
//     when their work stops and "quota exceeded" with no numbers tells
//     them nothing about when to try again.

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

        member _.Get<'T>(_scope, _moduleKey) : Async<'T option> = failwith "not used by the budget path"

        member _.GetEffective<'T>(_scope, _moduleKey, _schema) : Async<'T> = failwith "not used by the budget path"

        member _.Set<'T>(_scope, _moduleKey, _value: 'T, _schema) = failwith "not used by the budget path"

        member _.SetRaw(_scope, _moduleKey, _values, _schema) = failwith "not used by the budget path"

        member _.Clear(_scope, _moduleKey) = failwith "not used by the budget path"

        member _.Erase(_scopeId, _subjectUserId, _policy, _dryRun) = failwith "not used by the budget path"
    }

/// An `IEventStore` that counts the reads the budget path makes, so
/// "an unconfigured deployment reads nothing" is an assertion rather
/// than an aspiration.
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

/// Write one Phase 6i.A latency record attributed to `userId`.
let private writeTurn
    (store: IEventStore)
    (scopeId: string)
    (userId: string)
    (prompt: int)
    (output: int)
    (at: DateTime)
    =
    let record: AILatencyRecord = {
        TaskId = Guid.NewGuid()
        ConversationId = Guid.NewGuid()
        UserId = userId
        TurnNumber = 1
        ProviderName = "fake"
        ProviderModel = "fake-1"
        TtftMs = None
        TurnDurationMs = 10.0
        ToolCalls = []
        StopReason = "end_turn"
        PromptTokens = Some prompt
        CachedPromptTokens = None
        OutputTokens = Some output
        CacheCreationTokens = None
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

/// Write a latency record in the shape a deployment that has NOT yet
/// upgraded has on disk: the `UserId` property is simply absent.
let private writePre9sTurn (store: IEventStore) (scopeId: string) (prompt: int) (output: int) (at: DateTime) =
    let payload =
        sprintf
            """{"TaskId":"%s","ConversationId":"%s","TurnNumber":1,"ProviderName":"fake","ProviderModel":"fake-1","TtftMs":null,"TurnDurationMs":10,"ToolCalls":[],"StopReason":"end_turn","PromptTokens":%d,"CachedPromptTokens":null,"OutputTokens":%d,"CacheCreationTokens":null}"""
            (string (Guid.NewGuid()))
            (string (Guid.NewGuid()))
            prompt
            output

    store.Write {
        Id = Guid.NewGuid()
        OccurredAt = at
        ScopeId = scopeId
        SourceModule = AILatencyRecord.SourceModule
        EventType = AILatencyRecord.EventType
        Payload = payload
    }
    |> Async.RunSynchronously

/// Every recorded refusal in `scopeId`.
let private readRefusals (store: IEventStore) (scopeId: string) : AITokenBudgetExceeded list =
    store.ReadBySource(scopeId, AITokenBudgetExceeded.SourceModule)
    |> Async.RunSynchronously
    |> List.map (fun evt -> JsonSerializer.Deserialize<AITokenBudgetExceeded>(evt.Payload, jsonOptions))

let private okResponse: AIProviderResponse = {
    Content = "ok"
    ToolCalls = []
    StopReason = "end_turn"
    Usage = None
}

/// An `IAIProvider` that records how many times it was actually
/// invoked. A refused call must never reach it — "the provider is
/// never invoked" is the property that distinguishes a budget from a
/// post-hoc report.
type private CountingProvider() =
    let mutable calls = 0
    member _.Calls = calls

    interface IAIProvider with
        member _.Capabilities = AIProviderCapabilities.unknown

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            calls <- calls + 1
            return Ok okResponse
        }

        member _.SendStructuredMessage(_messages, _tools, _systemPrompt, _schema, _retryPolicy) = async {
            calls <- calls + 1
            return Ok okResponse
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

/// An `ITeamQuotaPolicy` that refuses the token budget when told to —
/// standing in for Phase 9d's per-team day / month windows.
let private quotaPolicy (refuse: bool) =
    { new ITeamQuotaPolicy with
        member _.CheckJobConcurrency(_scopeId, _currentInFlight) = async { return Ok() }

        member _.CheckTokenBudget(scopeId, kind, _requested) = async {
            if refuse then
                return
                    Error {
                        Kind = kind
                        Limit = 10M
                        Requested = 11M
                        ScopeId = scopeId
                    }
            else
                return Ok()
        }
    }

let private userMessage text : AIProviderMessage = {
    Role = "user"
    Content = text
    ToolCalls = []
    ToolResults = []
    Parts = []
}

let private scopeId = "team-1"
let private alice = "alice"
let private bob = "bob"

/// A fixed instant inside an hour, so the window arithmetic is not a
/// function of when the suite runs (and cannot rot at an hour boundary
/// the way a `DateTime.UtcNow`-based fixture would).
let private noon = DateTime(2026, 9, 16, 12, 30, 0, DateTimeKind.Utc)

let private capOf (cap: int option) =
    match cap with
    | Some v -> Map.ofList [ AIBudgetConfigKey.maxTokensPerUserPerHour, string v ]
    | None -> Map.empty

let private enforcerOver
    (configValues: unit -> Map<string, string>)
    (store: IEventStore)
    (ttlSeconds: int)
    (at: unit -> DateTime)
    =
    AIBudgetEnforcer.AIBudgetEnforcer(
        configStoreOver configValues,
        store,
        AIBudgetEnforcer.AIBudgetWindowCache(ttlSeconds),
        AIBudgetEnforcer.eventStoreAccount store at,
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
    testList "Phase 9s — per-user AI token budget" [

        test "under cap — the call proceeds and nothing is recorded" {
            let store = newEventStore ()
            writeTurn store scopeId alice 100 100 noon

            let enforcer = enforcerOver (fun () -> capOf (Some 1000)) store 30 (fun () -> noon)
            let verdict = enforcer.Check(scopeId, alice, 10M) |> Async.RunSynchronously

            Expect.equal verdict BudgetVerdict.Allowed "200 used against a 1000 cap is allowed"
            Expect.isEmpty (readRefusals store scopeId) "an allowed call records nothing"
        }

        test "at cap — refused, with the named-cap message and exactly one recorded refusal" {
            let store = newEventStore ()
            // 1000 + 200 = 1200 already consumed against a cap of 1000.
            writeTurn store scopeId alice 1000 200 noon

            let enforcer = enforcerOver (fun () -> capOf (Some 1000)) store 30 (fun () -> noon)
            let verdict = enforcer.Check(scopeId, alice, 5M) |> Async.RunSynchronously

            let denial =
                match verdict with
                | BudgetVerdict.Refused d -> d
                | other -> failwithf "expected a refusal, got %A" other

            Expect.equal denial.Quota 1000M "the denial names the configured cap"
            Expect.equal denial.Spent 1200M "the denial names what was already used"
            Expect.equal denial.ClassLabel alice "the denial names the USER, which is what 9d could not"
            Expect.equal denial.Dimension AITokenBudgetPolicy.PerUserPerHourDimension "the denial names the window"

            // The wording is the contract with the person whose work
            // just stopped. Pinned verbatim.
            Expect.equal
                (AITokenBudgetExceeded.message denial)
                "Token budget exceeded for this user this hour — 1200 tokens used vs 1000 cap"
                "the refusal message reads as the acceptance criterion words it"

            let refusals = readRefusals store scopeId
            Expect.hasLength refusals 1 "exactly one BudgetExceeded event per refusal"
            let recorded = List.head refusals
            Expect.equal recorded.UserId alice "the recorded refusal names the user"
            Expect.equal recorded.ScopeId scopeId "…and the scope"
            Expect.equal recorded.WindowKind "hourly" "…and the window it was measured over"
            Expect.equal recorded.CapTokens 1000M "…and the cap"
            Expect.equal recorded.UsedTokens 1200M "…and the consumption"
            Expect.equal recorded.PeriodKey "2026-09-16T12" "…and the hour, so two refusals in one hour join"
        }

        test "a cap lowered mid-hour is respected by the very next request" {
            let store = newEventStore ()
            writeTurn store scopeId alice 400 100 noon

            let mutable cap = Some 1000
            let enforcer = enforcerOver (fun () -> capOf cap) store 30 (fun () -> noon)

            let first = enforcer.Check(scopeId, alice, 10M) |> Async.RunSynchronously
            Expect.equal first BudgetVerdict.Allowed "500 used against 1000 is allowed"

            // The admin lowers the ceiling. No invalidation signal is
            // sent, and none exists — the policy is never cached, only
            // the window sum is, so the new value binds immediately.
            cap <- Some 400

            match enforcer.Check(scopeId, alice, 10M) |> Async.RunSynchronously with
            | BudgetVerdict.Refused d -> Expect.equal d.Quota 400M "the refusal cites the NEW cap"
            | other -> failwithf "expected the lowered cap to refuse, got %A" other
        }

        test "None cap is unbounded — and reads nothing at all" {
            let store = newEventStore ()
            writeTurn store scopeId alice 100_000 100_000 noon

            let enforcer = enforcerOver (fun () -> capOf None) store 30 (fun () -> noon)
            let verdict = enforcer.Check(scopeId, alice, 5_000M) |> Async.RunSynchronously

            Expect.equal verdict BudgetVerdict.Allowed "an unconfigured scope is unbounded"

            // GP 11 in its strongest form: not merely "the same answer"
            // but "the same work". An unconfigured deployment must not
            // pay for a feature it has not turned on.
            Expect.equal store.Reads 0 "an unbounded policy short-circuits before touching the event store"
            Expect.isEmpty (readRefusals store scopeId) "nothing is recorded"
        }

        test "a cap of 0 reads the same as no cap at all" {
            let store = newEventStore ()
            writeTurn store scopeId alice 5_000 5_000 noon

            let enforcer = enforcerOver (fun () -> capOf (Some 0)) store 30 (fun () -> noon)

            Expect.equal
                (enforcer.Check(scopeId, alice, 10M) |> Async.RunSynchronously)
                BudgetVerdict.Allowed
                "0 is the schema default a cleared field writes; it must mean unbounded, not 'refuse everything'"

            Expect.equal store.Reads 0 "…and take the same short-circuit"
        }

        test "the window rolls — last hour's consumption does not count against this hour" {
            let store = newEventStore ()
            writeTurn store scopeId alice 900 100 (noon.AddHours -1.0)

            let enforcer = enforcerOver (fun () -> capOf (Some 1000)) store 30 (fun () -> noon)

            Expect.equal
                (enforcer.Check(scopeId, alice, 10M) |> Async.RunSynchronously)
                BudgetVerdict.Allowed
                "the previous hour is a different period key and is not summed"
        }

        test "one user's spend is not charged to another" {
            let store = newEventStore ()
            writeTurn store scopeId bob 2_000 0 noon

            let enforcer = enforcerOver (fun () -> capOf (Some 1000)) store 30 (fun () -> noon)

            Expect.equal
                (enforcer.Check(scopeId, alice, 10M) |> Async.RunSynchronously)
                BudgetVerdict.Allowed
                "the window is per-user — bob exhausting his does not stop alice"

            match enforcer.Check(scopeId, bob, 10M) |> Async.RunSynchronously with
            | BudgetVerdict.Refused d -> Expect.equal d.ClassLabel bob "…and bob is refused on his own"
            | other -> failwithf "expected bob to be refused, got %A" other
        }

        test "pre-9s records carry no UserId and are charged to nobody" {
            let store = newEventStore ()
            // The exact on-disk shape in a deployment upgrading to 9s:
            // the property is absent, and the JSON converter absorbs it
            // as null rather than throwing.
            writePre9sTurn store scopeId 5_000 5_000 noon

            let enforcer = enforcerOver (fun () -> capOf (Some 100)) store 30 (fun () -> noon)

            Expect.equal
                (enforcer.Check(scopeId, alice, 10M) |> Async.RunSynchronously)
                BudgetVerdict.Allowed
                "an unattributable turn is wrong in the SAFE direction — it never refuses a user it cannot name"
        }

        test "an unattributed caller is allowed — there is no per-user window to enforce" {
            let store = newEventStore ()
            let enforcer = enforcerOver (fun () -> capOf (Some 1)) store 30 (fun () -> noon)

            Expect.equal
                (enforcer.Check(scopeId, "", 10M) |> Async.RunSynchronously)
                BudgetVerdict.Allowed
                "an Anonymous deployment has no user identity; Phase 9d's per-scope ceilings are the control there"

            Expect.equal store.Reads 0 "and it costs nothing"
        }

        test "the cache is a perf optimisation, not part of the answer (Phase 9c rule 4)" {
            let store = newEventStore ()
            writeTurn store scopeId alice 600 100 noon

            // Same scenario, twice: once with the 30 s cache, once with
            // caching disabled entirely. If the cache were load-bearing
            // the two would disagree — which is exactly what a second
            // replica, a restarted process or a cold start would see.
            let cached = enforcerOver (fun () -> capOf (Some 1000)) store 30 (fun () -> noon)
            let uncached = enforcerOver (fun () -> capOf (Some 1000)) store 0 (fun () -> noon)

            Expect.equal
                (cached.Check(scopeId, alice, 10M) |> Async.RunSynchronously)
                (uncached.Check(scopeId, alice, 10M) |> Async.RunSynchronously)
                "warm and cold give the same verdict under the cap"

            writeTurn store scopeId alice 500 0 noon
            cached.Cache.Clear()

            let warmVerdict = cached.Check(scopeId, alice, 10M) |> Async.RunSynchronously
            let coldVerdict = uncached.Check(scopeId, alice, 10M) |> Async.RunSynchronously

            Expect.equal warmVerdict coldVerdict "warm and cold give the same verdict over the cap too"

            match coldVerdict with
            | BudgetVerdict.Refused d -> Expect.equal d.Spent 1200M "both summed the same window"
            | other -> failwithf "expected a refusal once 1200 tokens are consumed, got %A" other
        }

        test "the cached window is what /dev/inspect reports" {
            let store = newEventStore ()
            writeTurn store scopeId alice 300 200 noon

            let cache = AIBudgetEnforcer.AIBudgetWindowCache(30)

            let enforcer =
                AIBudgetEnforcer.AIBudgetEnforcer(
                    configStoreOver (fun () -> capOf (Some 1000)),
                    store,
                    cache,
                    BudgetAccount.silent,
                    fun () -> noon
                )

            enforcer.Check(scopeId, alice, 10M) |> Async.RunSynchronously |> ignore

            let panel = AIBudgetEnforcer.AITokenBudgetContributor(cache)

            let name, _payload =
                (panel :> IDevDiagnosticsContributor).Contribute() |> Async.RunSynchronously

            Expect.equal name "AI token budget" "the panel name"

            let snapshot = cache.Snapshot()
            Expect.hasLength snapshot 1 "one live window"
            Expect.equal (List.head snapshot).UsedTokens 500M "…reporting the consumption the enforcer summed"
            Expect.equal (List.head snapshot).UserId alice "…named by user"
        }

        // ─── The decorator chain ─────────────────────────────────

        test "multi-window enforcement — the per-user hour and 9d's per-team day in ONE chain" {
            let store = newEventStore ()
            writeTurn store scopeId alice 900 200 noon

            let provider = CountingProvider()
            let enforcer = enforcerOver (fun () -> capOf (Some 1000)) store 30 (fun () -> noon)

            // The composed chain, in the order `wrapFactoryForDI` builds
            // it: the per-user hour outermost, 9d's team windows inside.
            let chain (teamRefuses: bool) =
                AIProviderUsageMiddleware.BudgetEnforcingProviderFactory(
                    AIProviderUsageMiddleware.QuotaEnforcingProviderFactory(
                        factoryOver (provider :> IAIProvider),
                        quotaPolicy teamRefuses
                    ),
                    enforcer
                )
                :> IAIProviderFactory

            // Per-user window breached, team window fine: the user's
            // hour refuses, and the message is 9s's.
            match sendThrough (chain false) alice "hello" with
            | Error(PermanentClient(429, msg)) ->
                Expect.stringContains msg "Token budget exceeded for this user this hour" "the per-user window refused"
            | other -> failwithf "expected the per-user window to refuse, got %A" other

            Expect.equal provider.Calls 0 "a refused call never reaches the provider"

            // Team window breached, per-user window fine (bob has spent
            // nothing): 9d's refusal surfaces through the same chain.
            match sendThrough (chain true) bob "hello" with
            | Error(PermanentClient(429, msg)) ->
                Expect.stringContains msg "AI usage quota exceeded for scope" "9d's per-team window still refuses"
            | other -> failwithf "expected the team window to refuse, got %A" other

            Expect.equal provider.Calls 0 "neither refusal reaches the provider"

            // Neither breached: the call goes through both gates.
            match sendThrough (chain false) bob "hello" with
            | Ok response -> Expect.equal response.Content "ok" "an in-budget call is served"
            | other -> failwithf "expected the call to be served, got %A" other

            Expect.equal provider.Calls 1 "…exactly once"
        }

        test "an undeclared budget leaves the chain byte-for-byte what it was" {
            let store = newEventStore ()
            let provider = CountingProvider()
            let enforcer = enforcerOver (fun () -> capOf None) store 30 (fun () -> noon)

            let chain =
                AIProviderUsageMiddleware.BudgetEnforcingProviderFactory(
                    factoryOver (provider :> IAIProvider),
                    enforcer
                )
                :> IAIProviderFactory

            match sendThrough chain alice "hello" with
            | Ok response -> Expect.equal response.Content "ok" "the call is served"
            | other -> failwithf "expected the call to be served, got %A" other

            Expect.equal store.Reads 0 "no event-store read"
            Expect.equal provider.Calls 1 "the provider is reached exactly as before"
        }

        // ─── The admin surface ───────────────────────────────────

        test "the config schema declares the cap, defaulting to unbounded" {
            let entry = AIBudgetEnforcer.sdkAIBudgetSchema
            Expect.equal entry.ModuleKey AIBudgetConfigKey.value "the reserved module key"

            let field =
                entry.Schema.Fields
                |> List.find (fun f -> f.Key = AIBudgetConfigKey.maxTokensPerUserPerHour)

            Expect.equal field.DefaultJson "0" "the default is unbounded"
            Expect.isFalse field.Required "the field is optional"
        }

        test "an app's own budget entry keeps its fields and gains the SDK's" {
            let appEntry: ModuleConfigEntry = {
                ModuleKey = AIBudgetConfigKey.value
                DisplayName = "Our budgets"
                Schema = {
                    Fields = [
                        {
                            Key = "OurOwnField"
                            DisplayName = "Ours"
                            Description = None
                            Kind = ConfigFieldKind.Int(Some 0, None)
                            Required = false
                            DefaultJson = "0"
                        }
                    ]
                    SchemaVersion = 1
                }
            }

            let merged = AIBudgetEnforcer.mergeAIBudgetSchema [ appEntry ]
            Expect.hasLength merged 1 "the app's entry is amended, not duplicated"

            let keys = (List.head merged).Schema.Fields |> List.map _.Key
            Expect.contains keys "OurOwnField" "the app's own field survives"
            Expect.contains keys AIBudgetConfigKey.maxTokensPerUserPerHour "the SDK field is added"

            let fresh = AIBudgetEnforcer.mergeAIBudgetSchema []
            Expect.hasLength fresh 1 "an app declaring nothing gets the SDK entry"
        }
    ]