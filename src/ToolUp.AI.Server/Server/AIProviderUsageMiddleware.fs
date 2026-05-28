module ToolUp.AI.AIProviderUsageMiddleware

open System
open ToolUp.Platform
open ToolUp.Platform.AI
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

    let providerName = inner.Capabilities.ProviderName
    let modelName = inner.Capabilities.Model

    let buildMetadata (extra: (string * string) list) =
        let baseEntries = [ "provider", providerName; "model", modelName; "userId", userId ]

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

/// Wraps an `IAIProviderFactory` so resolved providers fire usage
/// records on every `SendMessage`. `Available`, `PlatformDescriptor`,
/// and `TryResolveByLabel` (settings-UI catalogue + diagnostic
/// test-connection flow) intentionally do NOT meter — those are
/// admin / catalogue calls, not user-attributable AI usage.
type MeteringProviderFactory(inner: IAIProviderFactory, usageLog: IUsageLog, providerProfile: IProviderProfile) =

    interface IAIProviderFactory with
        member _.Available = inner.Available
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

/// Wraps an `IAIProviderFactory` so every `Resolve`-d provider's
/// `SendMessage` is quota-gated. `TryResolveByLabel` (diagnostic
/// test-connection) is forwarded ungated — an admin catalogue call,
/// not user-attributable spend.
type QuotaEnforcingProviderFactory(inner: IAIProviderFactory, quota: ITeamQuotaPolicy) =

    interface IAIProviderFactory with
        member _.Available = inner.Available
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