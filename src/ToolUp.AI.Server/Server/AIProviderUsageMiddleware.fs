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
    (inner: IAIProvider, usageLog: IUsageLog, scopeId: string, userId: string, origin: ProviderOrigin) =

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

    let emit (kind: string) (qty: decimal) (extra: (string * string) list) = async {
        try
            let record = {
                RecordId = Guid.NewGuid()
                ScopeId = scopeId
                ResourceKind = kind
                Quantity = qty
                Unit = "tokens"
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
                | None -> ()
            | Error _ -> ()

            return result
        }

/// Wraps an `IAIProviderFactory` so resolved providers fire usage
/// records on every `SendMessage`. `Available`, `PlatformDescriptor`,
/// and `TryResolveByLabel` (settings-UI catalogue + diagnostic
/// test-connection flow) intentionally do NOT meter — those are
/// admin / catalogue calls, not user-attributable AI usage.
type MeteringProviderFactory(inner: IAIProviderFactory, usageLog: IUsageLog, providerProfile: IProviderProfile) =

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
                let metered = MeteringProvider(provider, usageLog, scopeId, ctx.UserId, origin)
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

        let baseFactory =
            match config.UsageMetering with
            | NoUsageMetering -> failoverAware
            | EnabledUsageMetering ->
                let usageLog = sp.GetService(typeof<IUsageLog>) :?> IUsageLog

                MeteringProviderFactory(failoverAware, usageLog, providerProfile) :> IAIProviderFactory

        match sp.GetService(typeof<ITeamQuotaPolicy>) with
        | :? ITeamQuotaPolicy as quota -> QuotaEnforcingProviderFactory(baseFactory, quota) :> IAIProviderFactory
        | _ -> baseFactory)