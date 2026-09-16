module ToolUp.AI.DefaultAIProviderFactory

open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.AI

// ─── Platform-provider bundle (Phase 70 shape) ───────────────────

/// One wired platform provider. Phase 70 promotes the previous
/// `{ Descriptor; Provider; Rebuild }` shape — where `Provider` was
/// pre-built at startup with the API key baked in — to a
/// `{ Descriptor; Build; BootstrapKeyFromEnv }` shape where the key
/// is resolved at request time via `IPlatformAIKeyStore`.
///
/// `Build` is curried `apiKey -> model -> IAIProvider` so the same
/// `createWithApiKeyAndModel` helper each vendor companion exports
/// can populate it directly. The factory invokes `Build` once per
/// resolution with the API key resolved from
/// `IPlatformAIKeyStore` (team-scope, then platform-scope) or
/// `BootstrapKeyFromEnv` as a final fallback.
///
/// `BootstrapKeyFromEnv` is the migration shim — when the deployment
/// boots from an env-var-supplied key (the v0.4 shape) and the
/// Platform Admin keys store has no key recorded yet, the factory
/// uses this value. Setting a key via the Platform Admin module
/// thereafter takes precedence (the store is read first).
type AIPlatformProvider = {
    Descriptor: AIProviderDescriptor
    Build: string -> string -> IAIProvider
    BootstrapKeyFromEnv: string option
}

// ─── Simple adapters ─────────────────────────────────────────────

/// Wrap a single pre-built `IAIProvider` as a factory. Every Resolve
/// returns the same provider, regardless of context. Retained as a
/// thin shim for tests + the deployment shape that wants to bypass
/// the key store entirely. Use `create` instead for deployments that
/// want user-configurable providers or Platform-Admin-managed keys.
let singleProvider (descriptor: AIProviderDescriptor) (provider: IAIProvider) : IAIProviderFactory =
    { new IAIProviderFactory with
        member _.Available = [ descriptor ]

        member _.PlatformDescriptors = [ descriptor ]

        member _.PlatformDescriptor = Some descriptor

        member _.Resolve _accessContext = async { return Ok provider }

        member _.TryResolveByLabel(_accessContext, _label) = async { return Ok provider }

        member _.BuildPlatform(providerId, _apiKey, _model) =
            // The single-provider adapter wraps an already-built
            // IAIProvider; it doesn't carry a key-aware Build closure.
            // Return the wrapped provider when the id matches, None
            // otherwise — the (apiKey, model) inputs are ignored.
            if providerId = descriptor.Id then Some provider else None
    }

/// Factory that always fails with `NoProviderConfigured`. Useful for
/// strict-BYOK deployments whose provider-profile layer isn't wired
/// yet, and as a no-op for tests that don't exercise the AI path.
let empty: IAIProviderFactory =
    { new IAIProviderFactory with
        member _.Available = []

        member _.PlatformDescriptors = []

        member _.PlatformDescriptor = None

        member _.Resolve _accessContext = async { return Error NoProviderConfigured }

        member _.TryResolveByLabel(_accessContext, _label) = async { return Error NoProviderConfigured }

        member _.BuildPlatform(_providerId, _apiKey, _model) = None
    }

// ─── Full factory with provider-profile store + key store ────────

/// Resolve the user's preferred platform provider from the
/// `IProviderProfile` `ai.platform.provider` surface override. Falls
/// back to the first descriptor when the user hasn't picked, when
/// they picked a provider no longer wired, or when no persistent
/// scope is available (Anonymous).
let private resolvePlatformProvider
    (descriptors: AIPlatformProvider list)
    (providerProfile: IProviderProfile)
    (ctx: AccessContext)
    =
    async {
        let firstOpt = descriptors |> List.tryHead

        match AccessContext.configScope ctx with
        | None -> return firstOpt
        | Some scope ->
            let! profile = providerProfile.Get scope

            let overrideId =
                profile
                |> Option.bind (ProviderProfile.surfaceProviderOverride AIProviderSurface.platformProviderKey)

            match overrideId with
            | Some id ->
                match descriptors |> List.tryFind (fun b -> b.Descriptor.Id = id) with
                | Some matched -> return Some matched
                | None -> return firstOpt
            | None -> return firstOpt
    }

/// Resolve the model override the user has set against the
/// `ai.platform` surface, validated against the active provider's
/// supported models. Returns the active provider's `DefaultModel`
/// when no override is set or the stored value is no longer valid
/// for the active provider (e.g. the user previously chose Anthropic
/// + Opus, then switched provider to OpenAI — the Opus model is
/// silently ignored).
let private resolvePlatformModel (active: AIPlatformProvider) (providerProfile: IProviderProfile) (ctx: AccessContext) = async {
    match AccessContext.configScope ctx with
    | None -> return active.Descriptor.DefaultModel
    | Some scope ->
        let! profile = providerProfile.Get scope

        let overrideModel =
            profile
            |> Option.bind (ProviderProfile.surfaceModelOverride AIProviderSurface.platformModelKey)

        match overrideModel with
        | Some m when
            active.Descriptor.SupportedModels |> List.contains m
            || m = active.Descriptor.DefaultModel
            ->
            return m
        | _ -> return active.Descriptor.DefaultModel
}

/// Resolve the API key for the active platform provider against the
/// Phase 70 four-step chain: team-scope key store → platform-scope
/// key store → `BootstrapKeyFromEnv` → `None` (caller surfaces
/// `MissingApiKey`).
let private resolvePlatformApiKey
    (active: AIPlatformProvider)
    (keyStore: IPlatformAIKeyStore option)
    (ctx: AccessContext)
    =
    async {
        let providerId = active.Descriptor.Id

        match keyStore with
        | None -> return active.BootstrapKeyFromEnv
        | Some store ->
            let! teamKey =
                match ctx.TeamId with
                | Some tid -> store.GetTeamKey(tid, providerId)
                | None -> async { return None }

            match teamKey with
            | Some k -> return Some k
            | None ->
                let! platformKey = store.GetPlatformKey providerId

                match platformKey with
                | Some k -> return Some k
                | None -> return active.BootstrapKeyFromEnv
    }

/// Build a factory that resolves user/team-configured AI providers
/// against the canonical platform `IProviderProfile` store (Phase
/// 43.A) AND the Phase 70 platform-managed key store. Honours the
/// deployment's `AIFallbackPolicy` and looks up BYOK keys from
/// `ISecretStore` scoped to the request scope.
///
/// **BYOK resolution chain** (unchanged from Phase 43.A):
/// 1. Compute scope from `AccessContext.Subject`. Anonymous + Team-
///    without-a-team yield no scope — fall back per policy.
/// 2. Resolve the entry routed for surface
///    `AIProviderSurface.aiAssistant`. Missing profile / no matching
///    rule / stale label → fall back per policy.
/// 3. Look up the entry's `ProviderId` in `builders`; fetch the key
///    from `secretStore` at the same scope; build the provider.
///
/// **Platform-fallback resolution chain** (Phase 70 extension):
/// 1. Pick the user's preferred platform provider from
///    `PlatformDescriptors` via the `ai.platform.provider` surface
///    override; fall back to the first descriptor.
/// 2. Resolve the active provider's API key via
///    `IPlatformAIKeyStore` — team-scope first, then platform-scope.
/// 3. Fall back to `BootstrapKeyFromEnv` from the wired bundle.
/// 4. If still no key → `MissingApiKey`.
/// 5. Resolve the model via the `ai.platform` surface override
///    (validated against the active provider's `SupportedModels`).
/// 6. Build the provider via `bundle.Build apiKey model`.
///
/// `platformProviders` is the deployment's wired-platform-provider
/// list. Under `PlatformOnly` users always get one of these; under
/// `PermissiveWithPlatformFallback` they're the fallback when no
/// BYOK entry is routed. `StrictBYOK` never returns a platform
/// provider; missing config surfaces `NoProviderConfigured` to the
/// UI.
///
/// The `Available` view reflects policy:
/// - `PlatformOnly`: empty (no user-configurable BYOK providers) —
///   the settings UI surfaces a platform-provider+model picker
///   driven by `PlatformDescriptors`.
/// - `PermissiveWithPlatformFallback` / `StrictBYOK`: all builders'
///   descriptors (what the user can configure via BYOK).
let create
    (builders: AIProviderBuilder list)
    (providerProfile: IProviderProfile)
    (secretStore: ISecretStore)
    (fallbackPolicy: AIFallbackPolicy)
    (platformProviders: AIPlatformProvider list)
    (platformKeyStore: IPlatformAIKeyStore option)
    : IAIProviderFactory =

    let builderById = builders |> List.map (fun b -> b.Descriptor.Id, b) |> Map.ofList

    let platformDescriptors = platformProviders |> List.map _.Descriptor

    let platformBuildById =
        platformProviders |> List.map (fun b -> b.Descriptor.Id, b.Build) |> Map.ofList

    let fallback (ctx: AccessContext) : Async<Result<IAIProvider, ProviderResolutionError>> = async {
        match fallbackPolicy, platformProviders with
        | StrictBYOK, _ -> return Error NoProviderConfigured
        | (PlatformOnly | PermissiveWithPlatformFallback), [] ->
            // Deployment misconfigured: policy allows fallback but no
            // platform providers were wired.
            return Error NoProviderConfigured
        | (PlatformOnly | PermissiveWithPlatformFallback), _ ->
            let! activeOpt = resolvePlatformProvider platformProviders providerProfile ctx

            match activeOpt with
            | None -> return Error NoProviderConfigured
            | Some active ->
                let! apiKeyOpt = resolvePlatformApiKey active platformKeyStore ctx

                match apiKeyOpt with
                | None ->
                    // 0.4.3 — second field carries an actionable hint
                    // rather than empty string. Operators upgrading
                    // from the 0.3.x env-var-only shape see where to
                    // remediate (Platform Admin AI Keys, or the
                    // composition-time BootstrapKeyFromEnv shim) rather
                    // than `MissingApiKey(anthropic, "")` with no signal.
                    //
                    // BootstrapKeyFromEnv is snapshotted at process start
                    // — the SDK does not re-read the env var per request,
                    // so an env-var unset post-start does not propagate
                    // and an env-var set post-start requires a restart.
                    // Surfaces both states accurately so the operator
                    // does not chase the wrong remediation.
                    let hint =
                        match active.BootstrapKeyFromEnv with
                        | Some _ ->
                            "No key is recorded in IPlatformAIKeyStore for this scope (team or platform). The composition-time BootstrapKeyFromEnv shim was populated at process start but the factory does not re-read env vars per request — set a key via Platform Admin > AI Keys (preferred), or restart the process with the env var populated."
                        | None ->
                            "No key is recorded in IPlatformAIKeyStore for this scope (team or platform), and no BootstrapKeyFromEnv was wired at composition time. Set a key via Platform Admin > AI Keys, or wire BootstrapKeyFromEnv in the composition root (re-reading env vars at runtime is not supported)."

                    return Error(MissingApiKey(active.Descriptor.Id, hint))
                | Some apiKey ->
                    let! model = resolvePlatformModel active providerProfile ctx
                    return Ok(active.Build apiKey model)
    }

    let buildFromEntry (entry: ProviderEntry) (scope: StorageScope) = async {
        match builderById.TryFind entry.ProviderId with
        | None -> return Error(UnknownProvider entry.ProviderId)
        | Some builder ->
            let! apiKey = secretStore.GetSecret(scope.Container, entry.SecretKeyName)

            match apiKey with
            | None -> return Error(MissingApiKey(entry.ProviderId, entry.SecretKeyName))
            | Some key ->
                let model = entry.Model |> Option.defaultValue builder.Descriptor.DefaultModel

                return Ok(builder.Build key model)
    }

    { new IAIProviderFactory with
        member _.Available =
            match fallbackPolicy with
            | PlatformOnly -> []
            | PermissiveWithPlatformFallback
            | StrictBYOK -> builders |> List.map _.Descriptor

        member _.PlatformDescriptors = platformDescriptors

        member _.PlatformDescriptor = platformDescriptors |> List.tryHead

        member _.Resolve ctx = async {
            match fallbackPolicy with
            | PlatformOnly ->
                // Profile ignored for provider identity in PlatformOnly
                // mode (no BYOK), but the user's platform-provider and
                // platform-model overrides are honoured via `fallback`.
                return! fallback ctx
            | PermissiveWithPlatformFallback
            | StrictBYOK ->
                match AccessContext.configScope ctx with
                | None ->
                    // No persistent config applies (Anonymous /
                    // Team-without-team). Fall back per policy.
                    return! fallback ctx
                | Some scope ->
                    let! entry = providerProfile.ResolveEntry(scope, AIProviderSurface.aiAssistant, None)

                    match entry with
                    | None -> return! fallback ctx
                    | Some e -> return! buildFromEntry e scope
        }

        member _.TryResolveByLabel(ctx, label) = async {
            // Bypasses the routing-rule gate — used by the
            // test-connection flow to verify a specific entry's key
            // without routing to it first.
            match AccessContext.configScope ctx with
            | None -> return Error NoProviderConfigured
            | Some scope ->
                let! profile = providerProfile.Get scope

                let entry =
                    profile
                    |> Option.bind (fun p -> p.Entries |> List.tryFind (fun e -> e.Label = label))

                match entry with
                | None -> return Error(UnknownProvider label)
                | Some e -> return! buildFromEntry e scope
        }

        member _.BuildPlatform(providerId, apiKey, model) =
            platformBuildById.TryFind providerId
            |> Option.map (fun build -> build apiKey model)
    }

// ─── Phase 498 — fallback / failover routing ─────────────────────
//
// Phase 43.B shipped the chain as DATA (`ProviderProfile.Fallback`,
// an ordered list of entry labels); this is the runtime that honours
// it. The whole mechanism is one composite `IAIProvider` plus one
// factory decorator, and where each sits is the load-bearing part:
//
// **The composite is the provider, not a branch in the agent loop.**
// `AIAgentEngine` acquires its provider once and calls `SendMessage`
// from inside a gate stack it has built for that turn — the Phase 503
// tool-approval prompt, the Phase 525 disclosure egress doors, the
// Phase 730 grant gate, the Phase 523 answer gate. Those gates produce
// the message list; `SendMessage` consumes it. Re-routing INSIDE
// `SendMessage` therefore re-sends the exact bytes the gate stack
// already approved, to a different endpoint. It cannot reach around a
// gate because it never returns to a point above one — a stronger
// guarantee than an engine-level retry loop could offer, where "which
// gates have already run for this payload" becomes a question someone
// has to keep answering correctly.
//
// **The composite is INNERMOST in the factory stack.**
// `AIProviderUsageMiddleware.wrapFactoryForDI` stacks metering and
// then quota OVER it, so the metering decorator observes the composite
// as its `inner` and reads `Capabilities` per call — usage is
// attributed to the entry that actually served the turn (Phase 498.D)
// with no per-entry bookkeeping. Stacking it outermost would have made
// each chain entry resolve through `TryResolveByLabel`, which both
// decorators deliberately forward UNMETERED (it is the settings-UI
// test-connection path), so a failed-over turn would have been free.
//
// **Cost when no chain is declared: one profile read, then nothing.**
// The decorator returns the inner provider UNWRAPPED when
// `Fallback.Ordered` is empty, so a deployment that has never declared
// a chain runs the identical object graph it ran before this phase
// (GP 11). The read is the price of knowing that, and the request
// already performs two (the factory's own `ResolveEntry` and the
// metering wrapper's origin probe).

/// The composite `IAIProvider` a deployment gets when its
/// `ProviderProfile.Fallback` declares a non-empty chain. Delegates
/// every call to the entry currently serving; on an outage-class
/// failure it advances one position and re-issues the SAME call.
///
/// **The position is sticky and only ever moves forward**, which is
/// what makes "a single turn tries at most the whole chain once"
/// (Phase 498.C) true across an agent loop rather than merely within
/// one `SendMessage`: once the primary has been shown to be down, the
/// remaining tool-use iterations of that conversation go straight to
/// the entry that took over instead of re-probing a dead endpoint on
/// every turn. The instance's lifetime is one `Resolve`, i.e. one
/// request, so nothing is shared between conversations.
type private AIFailoverProvider
    (
        primary: IAIProvider,
        chain: string list,
        scopeId: string,
        resolveLabel: string -> Async<Result<IAIProvider, ProviderResolutionError>>,
        onFailover: AIProviderFailoverRecord -> Async<unit>
    ) =

    let chainLength = List.length chain
    let mutable active = primary

    /// Count of chain entries already consumed. 0 = serving the routed
    /// primary; `chainLength` = the chain is exhausted.
    let mutable consumed = 0

    /// Advance past chain entries until one resolves. Returns true when
    /// a new provider is now active, false when the chain ran out.
    ///
    /// An entry that fails to resolve — a label the user has since
    /// deleted, an entry whose key has been rotated away — is SKIPPED
    /// rather than treated as the end of the chain, and the skip is
    /// recorded with `Resolved = false`. One stale label in the middle
    /// of a chain should not cost a deployment the entries behind it,
    /// and a silent skip would leave the operator with a chain that
    /// quietly does less than it says.
    let rec advance (err: AIProviderError) (attemptMs: float) = async {
        if consumed >= chainLength then
            return false
        else
            let label = chain[consumed]
            consumed <- consumed + 1
            let fromCaps = active.Capabilities

            let! resolved = resolveLabel label

            match resolved with
            | Ok next ->
                active <- next
                let toCaps = next.Capabilities

                do!
                    onFailover {
                        OccurredAt = System.DateTime.UtcNow
                        ScopeId = scopeId
                        FromProvider = fromCaps.ProviderName
                        FromModel = fromCaps.Model
                        ToLabel = label
                        ToProvider = toCaps.ProviderName
                        ToModel = toCaps.Model
                        Reason = AIProviderError.toMessage err
                        AttemptDurationMs = attemptMs
                        ChainPosition = consumed
                        ChainLength = chainLength
                        Resolved = true
                    }

                return true
            | Error resolutionError ->
                do!
                    onFailover {
                        OccurredAt = System.DateTime.UtcNow
                        ScopeId = scopeId
                        FromProvider = fromCaps.ProviderName
                        FromModel = fromCaps.Model
                        ToLabel = label
                        ToProvider = ""
                        ToModel = ""
                        Reason =
                            sprintf
                                "%s — fallback chain entry '%s' could not be resolved: %s"
                                (AIProviderError.toMessage err)
                                label
                                (ProviderResolutionError.toMessage resolutionError)
                        AttemptDurationMs = attemptMs
                        ChainPosition = consumed
                        ChainLength = chainLength
                        Resolved = false
                    }

                return! advance err attemptMs
    }

    /// One send, with the chain walked on outage-class failures.
    /// Generic over which of the two `IAIProvider` send methods is
    /// being issued so both honour the chain identically — a
    /// structured-output call is as entitled to survive an outage as a
    /// conversational one, and two copies of this loop would drift.
    let sendWithFailover (send: IAIProvider -> Async<Result<AIProviderResponse, AIProviderError>>) = async {
        let rec attempt () = async {
            let started = System.Diagnostics.Stopwatch.StartNew()
            let! result = send active

            match result with
            | Ok response -> return Ok response
            | Error err ->
                if AIProviderFailover.isOutageClass err then
                    let! advanced = advance err started.Elapsed.TotalMilliseconds

                    if advanced then
                        return! attempt ()
                    else
                        // Chain exhausted. The LAST error is returned
                        // rather than a new "everything is down" case:
                        // the callers' existing handling — the agent
                        // loop's `classifyForAgentLoop`, the handler's
                        // `AITaskFailed` rendering — already says the
                        // right thing about it, and inventing a case
                        // here would retype the `SendMessage` contract
                        // for every consumer to serve one diagnostic.
                        // The failover records name every entry tried.
                        return Error err
                else
                    return Error err
        }

        return! attempt ()
    }

    interface IAIProvider with
        /// The entry CURRENTLY serving. Read per call by the metering
        /// decorator and by the agent loop's latency record, which is
        /// how both name the provider that actually served the turn.
        member _.Capabilities = active.Capabilities

        member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) =
            sendWithFailover (fun p -> p.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy))

        member _.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            sendWithFailover (fun p -> p.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy))

/// Wrap a factory so a `Resolve`-d provider honours the profile's
/// `FallbackChain` (Phase 43.B) — the shipped data, not a second
/// configuration value.
///
/// Returns the inner provider UNWRAPPED, and therefore leaves a
/// deployment byte-for-byte unchanged (GP 11), whenever:
/// - resolution failed (nothing to fall back FROM);
/// - the request has no persistent config scope (anonymous — no
///   profile, so no chain);
/// - the profile declares no chain, or declares one that is empty
///   once the routed primary's own label and duplicates are removed.
///
/// `TryResolveByLabel` is forwarded untouched: a caller naming a
/// specific entry — the settings-UI test-connection probe, or a user
/// who picked a provider for this one conversation — has asked about
/// THAT entry, and silently answering from a different one would make
/// a connection test unable to fail.
let withFailoverChain
    (providerProfile: IProviderProfile)
    (onFailover: AIProviderFailoverRecord -> Async<unit>)
    (inner: IAIProviderFactory)
    : IAIProviderFactory =
    { new IAIProviderFactory with
        member _.Available = inner.Available

        member _.PlatformDescriptors = inner.PlatformDescriptors

        member _.PlatformDescriptor = inner.PlatformDescriptor

        member _.Resolve ctx = async {
            let! resolved = inner.Resolve ctx

            match resolved with
            | Error e -> return Error e
            | Ok primary ->
                match AccessContext.configScope ctx with
                | None -> return Ok primary
                | Some scope ->
                    let! profileOpt = providerProfile.Get scope

                    match profileOpt with
                    | None -> return Ok primary
                    | Some profile ->
                        // The label the primary was routed from, when
                        // there is one. A chain naming it would re-issue
                        // the call to the endpoint that just failed —
                        // which is a retry, not a failover, and the
                        // provider's own `RetryPolicy` has already spent
                        // its budget on exactly that.
                        let routedLabel =
                            ProviderProfile.resolveEntry AIProviderSurface.aiAssistant None profile
                            |> Option.map _.Label

                        let ordered =
                            profile.Fallback.Ordered
                            |> List.filter (fun l -> not (System.String.IsNullOrWhiteSpace l))
                            |> List.filter (fun l -> Some l <> routedLabel)
                            |> List.distinct

                        if List.isEmpty ordered then
                            return Ok primary
                        else
                            let composite =
                                AIFailoverProvider(
                                    primary,
                                    ordered,
                                    scope.ScopeId,
                                    (fun label -> inner.TryResolveByLabel(ctx, label)),
                                    onFailover
                                )

                            return Ok(composite :> IAIProvider)
        }

        member _.TryResolveByLabel(ctx, label) = inner.TryResolveByLabel(ctx, label)

        member _.BuildPlatform(providerId, apiKey, model) =
            inner.BuildPlatform(providerId, apiKey, model)
    }