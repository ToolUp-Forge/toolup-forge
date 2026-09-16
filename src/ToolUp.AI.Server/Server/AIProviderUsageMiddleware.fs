module ToolUp.AI.AIProviderUsageMiddleware

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Metrics
open ToolUp.Platform.Providers
open ToolUp.Platform.Usage
open ToolUp.AI

// ─── Phase 9d — AI provider usage middleware ─────────────────────
//
// `MeteringProviderFactory` wraps an `IAIProviderFactory`. The wrapper
// forwards `Available`, `PlatformDescriptor`, and `TryResolveByLabel`
// without metering — those are diagnostic / catalogue surfaces, not
// real user-attributable AI calls. Only `Resolve`'s returned provider
// is wrapped: each `IAIProvider.SendMessage` from a `Resolve`-d
// instance fires two `UsageRecord`s (one for input tokens, one for
// output tokens) keyed against the caller's resolved scope.
//
// Wire-in lives in `AICompose.aiServiceConfig` — when
// `ServerConfig.UsageMetering = EnabledUsageMetering`, the SDK
// rebinds the `IAIProviderFactory` DI registration to the metered
// wrapper. NoUsageMetering deployments keep the raw factory and pay
// nothing.
//
// ProviderOrigin discrimination (Phase 43.A — reads the canonical
// platform `IProviderProfile` directly; the `IUserAIConfigStore` shim
// is removed):
//   * If the scope's profile routes an entry for surface
//     `ai.assistant` (the shim's `ActiveProviderLabel`), the user is
//     BYOK (`TenantBYOK`).
//   * Otherwise the caller is using the deployment-default
//     (`PlatformManaged`).
// The check runs once per `Resolve` (one HTTP request) and the
// resulting `ProviderOrigin` is captured into the inner provider's
// closure so every `SendMessage` carries the same origin tag.

let private originFor (providerProfile: IProviderProfile) (ctx: AccessContext) : Async<ProviderOrigin> = async {
    match AccessContext.configScope ctx with
    | None -> return PlatformManaged
    | Some scope ->
        let! entry = providerProfile.ResolveEntry(scope, AIProviderSurface.aiAssistant, None)

        match entry with
        | Some _ -> return TenantBYOK
        | None -> return PlatformManaged
}

let private scopeIdFor (ctx: AccessContext) : string =
    match AccessContext.configScope ctx with
    | Some scope -> scope.ScopeId
    | None -> ctx.UserId

/// Wraps an `IAIProvider` returned by `Resolve` so each `SendMessage`
/// emits `ai.tokens.input` and `ai.tokens.output` `UsageRecord`s.
/// `Capabilities` and `SendMessage` semantics pass through unchanged
/// — emission is best-effort: a `Record` failure must never fail the
/// AI call.
type private MeteringProvider
    (
        inner: IAIProvider,
        usageLog: IUsageLog,
        scopeId: string,
        userId: string,
        origin: ProviderOrigin,
        priceTable: ModelPriceTable option
    ) =

    // Phase 498 — read `inner.Capabilities` PER EMISSION, not once at
    // construction. `emit` runs after `inner.SendMessage` has returned,
    // and when `inner` is the Phase 498 failover composite the entry it
    // reports is the one that actually served the turn. Snapshotting
    // here — which is what this did until Phase 498 — would have billed
    // the secondary's tokens to the primary on every failed-over turn,
    // silently and in the one record the operator bills from. On a
    // deployment with no `FallbackChain` the two reads return the same
    // values they always did.
    let buildMetadata (extra: (string * string) list) =
        let caps = inner.Capabilities

        let baseEntries = [ "provider", caps.ProviderName; "model", caps.Model; "userId", userId ]

        Map.ofList (baseEntries @ extra)

    let emitUnit (kind: string) (unit': string) (qty: decimal) (extra: (string * string) list) = async {
        try
            let record = {
                RecordId = Guid.NewGuid()
                ScopeId = scopeId
                ResourceKind = kind
                Quantity = qty
                Unit = unit'
                Origin = Some origin
                Metadata = buildMetadata extra
                Timestamp = DateTime.UtcNow
            }

            do! usageLog.Record record
        with _ ->
            // Best-effort emission — IUsageLog implementations should
            // self-protect, but a thrown exception here must never
            // fail the AI call.
            ()
    }

    let emit (kind: string) (qty: decimal) (extra: (string * string) list) = emitUnit kind "tokens" qty extra

    // Phase 499.C — the post-call true-up. The turn's ACTUAL reported
    // `TokenUsage` is priced through the registered rate card and
    // written as a third `UsageRecord` beside the two token records,
    // `Unit` carrying the operator's currency tag. Additive in both
    // senses that matter: `UsageRecord` is unchanged (the kind string
    // is open by design), and a deployment with no rate card — or a
    // turn on a model the rate card does not price — emits exactly the
    // two records it always did (GP 11).
    //
    // `inner.Capabilities` is read here, after the call returned, for
    // the Phase 498 reason `buildMetadata` reads it here: on a failover
    // chain the entry that SERVED the turn is what must be priced, and
    // a construction-time snapshot would bill the primary's rates for
    // the secondary's tokens.
    let emitCost (usage: TokenUsage) = async {
        match priceTable with
        | None -> ()
        | Some table ->
            let caps = inner.Capabilities

            let priced =
                table
                |> ModelPriceTable.tryCost
                    caps.ProviderName
                    caps.Model
                    usage.PromptTokens
                    usage.CachedPromptTokens
                    usage.OutputTokens
                    (usage.CacheCreationTokens |> Option.defaultValue 0)

            match priced with
            | None ->
                // Unpriced model — count-only. The token records above
                // still went out; inventing a cost here would put a
                // number nobody chose into the ledger an operator bills
                // from.
                ()
            | Some amount ->
                do!
                    emitUnit AISpendResourceKind.cost table.Currency amount [
                        "price_key", ModelPriceTable.key caps.ProviderName caps.Model
                    ]
    }

    interface IAIProvider with
        member _.Capabilities = inner.Capabilities

        member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) = async {
            let! result = inner.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy)

            match result with
            | Ok response ->
                match response.Usage with
                | Some usage ->
                    let cachedExtra = [ "cached_input_tokens", string usage.CachedPromptTokens ]
                    do! emit ResourceKinds.aiTokensInput (decimal usage.PromptTokens) cachedExtra
                    do! emit ResourceKinds.aiTokensOutput (decimal usage.OutputTokens) []
                    do! emitCost usage
                | None ->
                    // Provider couldn't extract usage (transient parse
                    // failure or streaming early-exit). Skip emission
                    // — half a record is worse than no record.
                    ()
            | Error _ -> ()

            return result
        }

        // Phase 67b — structured-output path is metered identically to
        // SendMessage: delegate to inner.SendStructuredMessage, record
        // usage on success. The schema is metered as input tokens at
        // the provider's report rather than reconstructed here.
        member _.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) = async {
            let! result = inner.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy)

            match result with
            | Ok response ->
                match response.Usage with
                | Some usage ->
                    let cachedExtra = [ "cached_input_tokens", string usage.CachedPromptTokens ]
                    do! emit ResourceKinds.aiTokensInput (decimal usage.PromptTokens) cachedExtra
                    do! emit ResourceKinds.aiTokensOutput (decimal usage.OutputTokens) []
                    do! emitCost usage
                | None -> ()
            | Error _ -> ()

            return result
        }

/// Wraps an `IAIProviderFactory` so resolved providers fire usage
/// records on every `SendMessage`. `Available`, `PlatformDescriptor`,
/// and `TryResolveByLabel` (settings-UI catalogue + diagnostic
/// test-connection flow) intentionally do NOT meter — those are
/// admin / catalogue calls, not user-attributable AI usage.
type MeteringProviderFactory
    (
        inner: IAIProviderFactory,
        usageLog: IUsageLog,
        providerProfile: IProviderProfile,
        priceTable: ModelPriceTable option
    ) =

    /// Phase 499 — the pre-499 three-argument shape, preserved as an
    /// EXPLICIT secondary constructor rather than folded into an
    /// optional parameter.
    ///
    /// An `?priceTable` would collapse both into one widened ctor and
    /// the three-argument token would disappear from the public-API
    /// baseline, which the approval gate scores as a REMOVAL — a
    /// genuine break, not a false positive. This keeps the diff purely
    /// additive and leaves every existing call site compiling
    /// byte-for-byte (GP 11).
    new(inner: IAIProviderFactory, usageLog: IUsageLog, providerProfile: IProviderProfile) =
        MeteringProviderFactory(inner, usageLog, providerProfile, None)

    interface IAIProviderFactory with
        member _.Available = inner.Available
        member _.PlatformDescriptors = inner.PlatformDescriptors
        member _.PlatformDescriptor = inner.PlatformDescriptor

        member _.Resolve(ctx) = async {
            let! resolved = inner.Resolve(ctx)

            match resolved with
            | Error e -> return Error e
            | Ok provider ->
                let! origin = originFor providerProfile ctx
                let scopeId = scopeIdFor ctx

                let metered =
                    MeteringProvider(provider, usageLog, scopeId, ctx.UserId, origin, priceTable)

                return Ok(metered :> IAIProvider)
        }

        member _.TryResolveByLabel(ctx, label) = inner.TryResolveByLabel(ctx, label)

        member _.BuildPlatform(providerId, apiKey, model) =
            inner.BuildPlatform(providerId, apiKey, model)

// ─── Phase 9 — compute-quota enforcement decorator ───────────────
//
// The `ITeamQuotaPolicy` contract specifies a decorator around
// `IAIProvider.SendMessage` that consults the policy BEFORE the call
// and returns a typed error rather than spending provider budget.
// `MeteringProvider` above only records usage AFTER the fact — this is
// the enforcement half the audit found missing on the AI request path.
// Composed OUTSIDE the metering decorator (see `AICompose`) so a denied
// call is never sent and never metered (it didn't happen).

/// Pre-call point-in-time gate. Asks the policy whether "one more AI
/// call unit" of input-token budget remains for the scope; if the
/// scope is already at/over its configured per-day/per-month budget,
/// the policy breaches here and the provider is never invoked.
type private QuotaEnforcingProvider(inner: IAIProvider, quota: ITeamQuotaPolicy, scopeId: string) =
    interface IAIProvider with
        member _.Capabilities = inner.Capabilities

        member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) = async {
            let! gate = quota.CheckTokenBudget(scopeId, ResourceKinds.aiTokensInput, 1m)

            match gate with
            | Ok() -> return! inner.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy)
            | Error qb ->
                // `PermanentClient(429)` — a quota breach is NOT
                // retry-worthy inside the provider loop (the budget
                // won't free up mid-loop). The agent loop surfaces
                // `AIProviderError.toMessage` as `AITaskFailed`, so the
                // user sees the quota message instead of a silent stall.
                return
                    Error(
                        PermanentClient(
                            429,
                            sprintf
                                "AI usage quota exceeded for scope '%s': %s budget limit %M reached. Usage resets per the configured per-day / per-month window."
                                qb.ScopeId
                                qb.Kind
                                qb.Limit
                        )
                    )
        }

        // Phase 67b — structured-output path is quota-gated identically
        // to SendMessage. Same point-in-time gate semantics.
        member _.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) = async {
            let! gate = quota.CheckTokenBudget(scopeId, ResourceKinds.aiTokensInput, 1m)

            match gate with
            | Ok() -> return! inner.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy)
            | Error qb ->
                return
                    Error(
                        PermanentClient(
                            429,
                            sprintf
                                "AI usage quota exceeded for scope '%s': %s budget limit %M reached. Usage resets per the configured per-day / per-month window."
                                qb.ScopeId
                                qb.Kind
                                qb.Limit
                        )
                    )
        }

/// Wraps an `IAIProviderFactory` so every `Resolve`-d provider's
/// `SendMessage` is quota-gated. `TryResolveByLabel` (diagnostic
/// test-connection) is forwarded ungated — an admin catalogue call,
/// not user-attributable spend.
type QuotaEnforcingProviderFactory(inner: IAIProviderFactory, quota: ITeamQuotaPolicy) =

    interface IAIProviderFactory with
        member _.Available = inner.Available
        member _.PlatformDescriptors = inner.PlatformDescriptors
        member _.PlatformDescriptor = inner.PlatformDescriptor

        member _.Resolve(ctx) = async {
            let! resolved = inner.Resolve(ctx)

            match resolved with
            | Error e -> return Error e
            | Ok provider ->
                let scopeId = scopeIdFor ctx
                return Ok(QuotaEnforcingProvider(provider, quota, scopeId) :> IAIProvider)
        }

        member _.TryResolveByLabel(ctx, label) = inner.TryResolveByLabel(ctx, label)

        member _.BuildPlatform(providerId, apiKey, model) =
            inner.BuildPlatform(providerId, apiKey, model)

// ─── Phase 9s — per-user token-budget enforcement decorator ──────
//
// The per-USER window Phase 9d does not carry, composed INSIDE this
// chain rather than beside it. `QuotaEnforcingProvider` above answers
// "has this TEAM spent its day / month"; this answers "has this MEMBER
// spent their hour". Two windows, two ceilings, one decorator chain —
// which is what keeps the refusal ordering, the error shape and the
// "never send a call we are about to refuse" property shared between
// them instead of reinvented.
//
// **Stacked OUTSIDE the quota gate**, so the per-user hour is checked
// first. The Phase 689 header states the rule this follows: report the
// cheapest, most-immediate ceiling first, because a refusal naming
// three problems invites fixing the wrong one. The hourly window is
// also the one whose remedy an ordinary user can act on ("wait" /
// "ask an admin"); the team's monthly allowance is not.
//
// **The refusal is `PermanentClient(429)`, the same shape the quota
// gate uses**, because that is what the agent loop classifies as
// catastrophic-no-retry and surfaces to the user via
// `AIProviderError.toMessage` as an `AITaskFailed`. The phase's
// required wording is the message carried inside it.

/// Pre-call per-user gate. Consults `AIBudgetEnforcer`; on a Phase 689
/// `Refused` verdict the provider is never invoked and the refusal has
/// already been recorded as an `AITokenBudgetExceeded` event.
type private BudgetEnforcingProvider
    (inner: IAIProvider, enforcer: AIBudgetEnforcer.AIBudgetEnforcer, scopeId: string, userId: string) =

    // Phase 9d's advisory estimator, reused rather than re-derived: the
    // pre-call figure is an estimate in both windows, and two budgets
    // disagreeing about what a request "asks for" would be a defect
    // nobody could explain.
    let estimate (messages: AIProviderMessage list) : decimal =
        messages
        |> List.map (fun m -> (m.Role, m.Content))
        |> TeamQuotaPolicy.RequestTokenEstimator.estimateMessages

    let refusal (denial: BudgetDenial) =
        Error(PermanentClient(429, AITokenBudgetExceeded.message denial))

    interface IAIProvider with
        member _.Capabilities = inner.Capabilities

        member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) = async {
            let! verdict = enforcer.Check(scopeId, userId, estimate messages)

            match verdict with
            | BudgetVerdict.Refused denial -> return refusal denial
            | BudgetVerdict.Allowed
            | BudgetVerdict.NearLimit _ ->
                // `NearLimit` proceeds — it is a leading indicator, not
                // a refusal. The account has already recorded it.
                return! inner.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy)
        }

        // Structured output is gated identically: it spends the same
        // provider budget out of the same window.
        member _.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) = async {
            let! verdict = enforcer.Check(scopeId, userId, estimate messages)

            match verdict with
            | BudgetVerdict.Refused denial -> return refusal denial
            | BudgetVerdict.Allowed
            | BudgetVerdict.NearLimit _ ->
                return! inner.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy)
        }

/// Wraps an `IAIProviderFactory` so every `Resolve`-d provider is
/// gated by the caller's per-user hourly budget. `TryResolveByLabel`
/// is forwarded ungated, exactly as the metering and quota wrappers
/// forward it — an admin test-connection call is not user-attributable
/// spend.
type BudgetEnforcingProviderFactory(inner: IAIProviderFactory, enforcer: AIBudgetEnforcer.AIBudgetEnforcer) =

    interface IAIProviderFactory with
        member _.Available = inner.Available
        member _.PlatformDescriptors = inner.PlatformDescriptors
        member _.PlatformDescriptor = inner.PlatformDescriptor

        member _.Resolve(ctx) = async {
            let! resolved = inner.Resolve(ctx)

            match resolved with
            | Error e -> return Error e
            | Ok provider ->
                let scopeId = scopeIdFor ctx
                return Ok(BudgetEnforcingProvider(provider, enforcer, scopeId, ctx.UserId) :> IAIProvider)
        }

        member _.TryResolveByLabel(ctx, label) = inner.TryResolveByLabel(ctx, label)

        member _.BuildPlatform(providerId, apiKey, model) =
            inner.BuildPlatform(providerId, apiKey, model)

// ─── Phase 499 — monetary spend-budget enforcement decorator ─────
//
// The third window in the one chain. `QuotaEnforcingProvider` asks
// "has this TEAM spent its day / month of tokens"; Phase 9s's
// `BudgetEnforcingProvider` asks "has this MEMBER spent their hour of
// tokens"; this asks "has either of them spent their MONEY". Three
// windows, three ceilings, one decorator chain — which is what keeps
// the refusal ordering, the error shape and the "never send a call we
// are about to refuse" property shared between them instead of
// reinvented three times.
//
// **Stacked OUTSIDE both token gates**, so money is checked first. The
// Phase 689 ordering rule is to report the cheapest, most-immediate
// ceiling first, and a monetary ceiling is the one an operator
// actually set deliberately: a token window is a proxy for cost, this
// IS cost. It is also the ceiling whose breach a user can do least
// about, so naming it first keeps the refusal honest rather than
// telling them to wait an hour for a window that is not what stopped
// them.
//
// **The refusal is `PermanentClient(429)`, the same shape both token
// gates use**, because that is what the agent loop classifies as
// catastrophic-no-retry and surfaces via `AIProviderError.toMessage`
// as an `AITaskFailed`. Phase 499.D's requirement that the monetary
// refusal be DISTINCT from token-quota exhaustion is met exactly as
// the Phase 689 substrate note prescribes — by the `Domain` /
// `Dimension` labels on the denial and by the message, not by a second
// error type. A new `AIProviderError` case would be a DU-case addition
// to a wire contract with exhaustive matches across the estate, bought
// for a cosmetic difference.

/// Pre-call monetary gate. Prices the request through the operator's
/// rate card, consults `AISpendEnforcer`; on a Phase 689 `Refused`
/// verdict the provider is never invoked and the refusal has already
/// been recorded as an `AISpendBudgetExceeded` event.
///
/// **Reads `inner.Capabilities` PER CALL, never at construction**
/// (Phase 498): when `inner` is the failover composite, the entry that
/// will serve this turn can differ from the one that served the last,
/// and pricing the wrong model is a silent billing error rather than a
/// visible failure.
type private SpendEnforcingProvider
    (inner: IAIProvider, enforcer: AIBudgetEnforcer.AISpendEnforcer, scopeId: string, userId: string) =

    /// The pre-call cost estimate, in the rate card's currency.
    ///
    /// Conservative by construction, in the two places it can be:
    /// input is Phase 9d's advisory character estimator (reused rather
    /// than re-derived — two budgets disagreeing about what a request
    /// "asks for" would be a defect nobody could explain), and it
    /// assumes NO cache hit, so the whole prompt is charged at the
    /// dearer fresh-input rate. Output cannot be known before the model
    /// writes it, so it is charged at
    /// `AIBudgetEnforcer.AssumedOutputTokens`.
    ///
    /// An unpriced `(provider, model)` estimates `0M` — count-only,
    /// never a guessed rate and never a block (GP 11).
    let estimate (messages: AIProviderMessage list) : decimal =
        let caps = inner.Capabilities

        let promptTokens =
            messages
            |> List.map (fun m -> (m.Role, m.Content))
            |> TeamQuotaPolicy.RequestTokenEstimator.estimateMessages

        enforcer.PriceTable
        |> ModelPriceTable.tryCost
            caps.ProviderName
            caps.Model
            (int (ceil promptTokens))
            0
            AIBudgetEnforcer.AssumedOutputTokens
            0
        |> Option.defaultValue 0M

    let refusal (denial: BudgetDenial) (policy: AISpendBudgetPolicy) =
        let windowLabel =
            let budget =
                if denial.Dimension = AISpendBudgetPolicy.PerScopeDimension then
                    policy.PerScope
                else
                    policy.PerUser

            budget
            |> Option.map _.Period
            |> Option.defaultValue AISpendBudgetPolicy.defaultPeriod
            |> BudgetPeriod.label

        Error(PermanentClient(429, AISpendBudgetExceeded.message enforcer.PriceTable.Currency windowLabel denial))

    /// Run the gate, then the inner call. `NearLimit` proceeds — it is
    /// a leading indicator, not a refusal, and the account has already
    /// recorded it.
    let gated (messages: AIProviderMessage list) (proceed: unit -> Async<Result<AIProviderResponse, AIProviderError>>) = async {
        let! verdict = enforcer.Check(scopeId, userId, estimate messages)

        match verdict with
        | BudgetVerdict.Refused denial ->
            let! policy = enforcer.ReadPolicy scopeId
            return refusal denial policy
        | BudgetVerdict.Allowed
        | BudgetVerdict.NearLimit _ -> return! proceed ()
    }

    interface IAIProvider with
        member _.Capabilities = inner.Capabilities

        member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) =
            gated messages (fun () -> inner.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy))

        // Structured output is gated identically: it spends the same
        // provider budget out of the same window.
        member _.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            gated messages (fun () -> inner.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy))

/// Wraps an `IAIProviderFactory` so every `Resolve`-d provider is gated
/// by the caller's monetary budget. `TryResolveByLabel` is forwarded
/// ungated, exactly as the metering and both token wrappers forward it
/// — an admin test-connection call is not user-attributable spend.
type SpendEnforcingProviderFactory(inner: IAIProviderFactory, enforcer: AIBudgetEnforcer.AISpendEnforcer) =

    interface IAIProviderFactory with
        member _.Available = inner.Available
        member _.PlatformDescriptors = inner.PlatformDescriptors
        member _.PlatformDescriptor = inner.PlatformDescriptor

        member _.Resolve(ctx) = async {
            let! resolved = inner.Resolve(ctx)

            match resolved with
            | Error e -> return Error e
            | Ok provider ->
                let scopeId = scopeIdFor ctx
                return Ok(SpendEnforcingProvider(provider, enforcer, scopeId, ctx.UserId) :> IAIProvider)
        }

        member _.TryResolveByLabel(ctx, label) = inner.TryResolveByLabel(ctx, label)

        member _.BuildPlatform(providerId, apiKey, model) =
            inner.BuildPlatform(providerId, apiKey, model)

// ─── DI composition helper ───────────────────────────────────────
//
// Both `AICompose.composeAI` and `RAGCompose.composeRAG` need the
// same Metering+Quota wrap on their `IAIProviderFactory` registration.
// Previously AICompose wrapped inline while RAGCompose registered the
// raw factory — RAG-using deployments silently bypassed both
// subsystems even when `ServerConfig.UsageMetering =
// EnabledUsageMetering` and an `ITeamQuotaPolicy` was wired. The
// keep-in-sync comment block in RAGCompose flagged this drift; the
// helper here removes the surface.

// ─── Phase 498 — failover observability sink ─────────────────────
//
// A failover is a change of who is serving the user's turn; it must
// leave a trace, and the two traces an operator reaches for are
// different questions. The audit event answers "what happened to THIS
// conversation" (readable per scope, joinable with the turn's
// `AILatencyRecord` on `ScopeId`); the counter answers "is a provider
// degrading" across the fleet. Both are best-effort: the failover has
// already been performed by the time either runs, and neither may fail
// the AI call.
//
// It lives here rather than inside the composite because this is where
// DI is — `wrapFactoryForDI` already receives the `IServiceProvider`
// it resolves `IUsageLog` and `ITeamQuotaPolicy` from. The composite
// stays a pure routing decision with an injected sink, which is also
// what makes it testable without a service provider.

/// Serialiser for the failover audit payload. Mirrors
/// `AIAgentEngine`'s latency serialiser — `FableConverters`
/// round-trips F# records / `option` losslessly, so a reader gets back
/// the shape that was written.
let private failoverJsonOptions = FableConverters.create ()

/// Cap on the audit write. A wedged event store must not hold up a
/// conversation that has just survived a provider outage — the whole
/// point of the failover is that the user's turn continues.
[<Literal>]
let private FailoverAuditWriteTimeoutMs = 5_000

/// Build the Phase 498 failover sink from whatever DI offers. Both
/// services are optional: a deployment with neither gets a sink that
/// does nothing, at the cost of two failed `GetService` calls per
/// composition (GP 13 — this runs once per `IAIProviderFactory`
/// resolution, not per turn).
let private failoverSink (sp: System.IServiceProvider) : AIProviderFailoverRecord -> Async<unit> =
    let eventStoreOpt =
        match sp.GetService(typeof<IEventStore>) with
        | :? IEventStore as s -> Some s
        | _ -> None

    let metricsSinkOpt =
        match sp.GetService(typeof<IMetricsSink>) with
        | :? IMetricsSink as s -> Some s
        | _ -> None

    fun (record: AIProviderFailoverRecord) -> async {
        match eventStoreOpt with
        | None -> ()
        | Some store ->
            let evt: ModuleEvent = {
                Id = Guid.NewGuid()
                OccurredAt = record.OccurredAt
                ScopeId = record.ScopeId
                SourceModule = AIProviderFailoverRecord.SourceModule
                EventType = AIProviderFailoverRecord.EventType
                Payload = JsonSerializer.Serialize(record, failoverJsonOptions)
            }

            try
                let! child = Async.StartChild(store.Write evt, FailoverAuditWriteTimeoutMs)
                do! child
            with _ ->
                // Timed out or threw. The failover itself already
                // happened and the turn is proceeding; dropping the
                // record is strictly better than failing the call the
                // record is about.
                ()

        match metricsSinkOpt with
        | None -> ()
        | Some sink ->
            try
                // `to` is empty on an unresolvable chain entry; the
                // literal "unresolved" keeps the tag a closed, readable
                // vocabulary rather than an empty string nobody can
                // grep for.
                let target = if record.Resolved then record.ToProvider else "unresolved"

                sink.Record(
                    AILatencyMetrics.ProviderFailover,
                    1.0,
                    Map.ofList [ "provider", record.FromProvider; "to", target ]
                )
            with _ ->
                ()
    }

/// Build a DI factory delegate that resolves `IAIProviderFactory` with
/// the standard Metering + Quota wrap applied over `rawFactory`:
///   * `MeteringProviderFactory` is stacked when
///     `config.UsageMetering = EnabledUsageMetering` (Phase 9d).
///   * `QuotaEnforcingProviderFactory` is stacked OUTERMOST whenever an
///     `ITeamQuotaPolicy` resolves from DI (Phase 9 compute-quota).
///   * Phase 498 — `DefaultAIProviderFactory.withFailoverChain` is
///     stacked INNERMOST, so metering sees the failover composite as
///     its `inner` and attributes a failed-over turn's tokens to the
///     entry that served it. See the placement note in
///     `DefaultAIProviderFactory.fs`; stacking it outermost would have
///     resolved every chain entry through the deliberately-unmetered
///     `TryResolveByLabel` path.
///
/// Both composers register their factory via this helper so a
/// deployment composing RAG retains metering + quota enforcement on
/// AI calls.
let wrapFactoryForDI
    (config: ServerConfig)
    (rawFactory: IAIProviderFactory)
    (providerProfile: IProviderProfile)
    : System.Func<System.IServiceProvider, IAIProviderFactory> =
    System.Func<System.IServiceProvider, IAIProviderFactory>(fun sp ->
        // Phase 498. A deployment whose profiles declare no
        // `FallbackChain` gets its resolved provider back unwrapped
        // from this layer, so the object graph below is what it always
        // was (GP 11).
        let failoverAware =
            DefaultAIProviderFactory.withFailoverChain providerProfile (failoverSink sp) rawFactory

        // Phase 499. The operator's rate card, supplied through the
        // documented `ComposeExtensions.ServiceConfig` seam. `None` on
        // every deployment that registers none, which is what makes
        // both halves of this phase cost nothing when uncomposed (GP
        // 11 / GP 13): no cost record on the metering path below, and
        // no spend decorator at the bottom of this function.
        let priceTable =
            match sp.GetService(typeof<ModelPriceTable>) with
            | :? ModelPriceTable as table -> Some table
            | _ -> None

        let baseFactory =
            match config.UsageMetering with
            | NoUsageMetering -> failoverAware
            | EnabledUsageMetering ->
                let usageLog = sp.GetService(typeof<IUsageLog>) :?> IUsageLog

                MeteringProviderFactory(failoverAware, usageLog, providerProfile, priceTable) :> IAIProviderFactory

        let quotaGated =
            match sp.GetService(typeof<ITeamQuotaPolicy>) with
            | :? ITeamQuotaPolicy as quota -> QuotaEnforcingProviderFactory(baseFactory, quota) :> IAIProviderFactory
            | _ -> baseFactory

        // Phase 9s. The presence of the `AIBudgetWindowCache` singleton
        // IS the composition gate, the same shape `ITeamQuotaPolicy`'s
        // presence is one line above: `AICompose` registers it only on a
        // team-scoped deployment, because a per-USER window inside a
        // per-SCOPE budget means nothing when the scope is the user. A
        // deployment without it resolves the same object graph it always
        // did (GP 11/13) — no decorator, no config read, no allocation.
        //
        // The config and event stores are required too, and their absence
        // is treated the same way rather than as an error: a budget that
        // cannot read its policy would refuse everything or allow
        // everything, and the honest form of "I cannot enforce this" is
        // not to compose the enforcement.
        let tokenBudgetGated =
            match
                sp.GetService(typeof<AIBudgetEnforcer.AIBudgetWindowCache>),
                sp.GetService(typeof<IConfigStore>),
                sp.GetService(typeof<IEventStore>)
            with
            | (:? AIBudgetEnforcer.AIBudgetWindowCache as cache),
              (:? IConfigStore as configStore),
              (:? IEventStore as eventStore) ->
                let enforcer =
                    AIBudgetEnforcer.AIBudgetEnforcer(
                        configStore,
                        eventStore,
                        cache,
                        AIBudgetEnforcer.eventStoreAccount eventStore (fun () -> DateTime.UtcNow),
                        fun () -> DateTime.UtcNow
                    )

                BudgetEnforcingProviderFactory(quotaGated, enforcer) :> IAIProviderFactory
            | _ -> quotaGated

        // Phase 499. The presence of the `ModelPriceTable` singleton IS
        // the composition gate, exactly as `AIBudgetWindowCache`'s
        // presence is 9s's: an operator registers a rate card through
        // `ComposeExtensions.ServiceConfig`, and a deployment that
        // registers none resolves the object graph it always did — no
        // decorator, no config read, no allocation (GP 11 / GP 13).
        //
        // The config and event stores are required alongside it and
        // their absence is treated the same way rather than as an
        // error, for the reason 9s gives: the honest form of "I cannot
        // enforce this" is not to compose the enforcement.
        //
        // NOT gated on team scope, unlike 9s. A per-USER TOKEN window
        // inside a per-SCOPE budget is meaningless when the scope IS
        // the user, which is why 9s gates; a monetary ceiling is not —
        // "this individual deployment may spend 5.00 a day" is a
        // budget an operator plainly wants, and the per-user and
        // per-scope ceilings simply measure the same figure against
        // two independently-configurable ceilings there.
        //
        // The spend gate stacks OUTERMOST, so money is evaluated
        // before either token window. See the decorator's own header.
        match
            sp.GetService(typeof<ModelPriceTable>),
            sp.GetService(typeof<IConfigStore>),
            sp.GetService(typeof<IEventStore>)
        with
        | (:? ModelPriceTable as table), (:? IConfigStore as configStore), (:? IEventStore as eventStore) ->
            let spendCache =
                match sp.GetService(typeof<AIBudgetEnforcer.AISpendWindowCache>) with
                | :? AIBudgetEnforcer.AISpendWindowCache as c -> c
                | _ -> AIBudgetEnforcer.AISpendWindowCache()

            let warn =
                match sp.GetService(typeof<ILogger>) with
                | :? ILogger as logger -> fun (message: string) -> logger.Warn message
                | _ -> ignore

            let spendEnforcer =
                AIBudgetEnforcer.AISpendEnforcer(
                    configStore,
                    eventStore,
                    table,
                    spendCache,
                    AIBudgetEnforcer.spendEventStoreAccount eventStore table.Currency (fun () -> DateTime.UtcNow),
                    warn,
                    fun () -> DateTime.UtcNow
                )

            SpendEnforcingProviderFactory(tokenBudgetGated, spendEnforcer) :> IAIProviderFactory
        | _ -> tokenBudgetGated)