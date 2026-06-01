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
                    // 0.4.3 — second field now carries an actionable
                    // hint rather than empty string. Operators upgrading
                    // from the 0.3.x env-var-only shape see where to
                    // remediate (Platform Admin AI Keys, or the
                    // composition-time BootstrapKeyFromEnv shim) rather
                    // than `MissingApiKey(anthropic, "")` with no signal.
                    let hint =
                        match active.BootstrapKeyFromEnv with
                        | Some _ ->
                            "Platform Admin > AI Keys (the wired BootstrapKeyFromEnv was non-empty at startup but is currently unset for this scope)"
                        | None -> "Platform Admin > AI Keys, or wire BootstrapKeyFromEnv at composition time"

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