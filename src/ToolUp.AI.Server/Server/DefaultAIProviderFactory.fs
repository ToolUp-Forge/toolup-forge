module ToolUp.AI.DefaultAIProviderFactory

open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.AI

// ─── Platform-provider bundle ────────────────────────────────────

/// Wraps the deployment's pre-built platform provider with an
/// optional rebuilder. The rebuilder lets the factory honour a
/// user's platform-model override under `PlatformOnly` (and
/// `PermissiveWithPlatformFallback`) — when `Some rebuild`, Resolve
/// calls `rebuild overrideModel` to produce a provider bound to the
/// user-chosen model; when `None`, the pre-built `Provider` is used
/// unchanged and any stored override is silently ignored.
///
/// Deployments build the rebuilder by closing over the same
/// API-key source as `Provider` (usually `ISecretStore`) — see
/// `ClaudeAIProvider.createWithModel` for the canonical shape.
type AIPlatformProvider = {
    Descriptor: AIProviderDescriptor
    Provider: IAIProvider
    Rebuild: (string -> IAIProvider) option
}

// ─── Simple adapters (retained from Phase A) ─────────────────────

/// Wrap a single IAIProvider as a factory. Every Resolve returns the
/// same provider, regardless of context. Phase A migration adapter —
/// use `create` instead when the deployment wants user-configurable
/// providers.
let singleProvider (descriptor: AIProviderDescriptor) (provider: IAIProvider) : IAIProviderFactory =
    { new IAIProviderFactory with
        member _.Available = [ descriptor ]

        member _.PlatformDescriptor = Some descriptor

        member _.Resolve _accessContext = async { return Ok provider }

        // The single-provider adapter ignores label semantics — any
        // test-connection request resolves to the one provider this
        // factory wraps.
        member _.TryResolveByLabel(_accessContext, _label) = async { return Ok provider }
    }

/// Factory that always fails with `NoProviderConfigured`. Useful for
/// strict-BYOK deployments whose provider-profile layer isn't wired
/// yet, and as a no-op for tests that don't exercise the AI path.
let empty: IAIProviderFactory =
    { new IAIProviderFactory with
        member _.Available = []

        member _.PlatformDescriptor = None

        member _.Resolve _accessContext = async { return Error NoProviderConfigured }

        member _.TryResolveByLabel(_accessContext, _label) = async { return Error NoProviderConfigured }
    }

// ─── Full factory with provider-profile store + fallback policy ──

/// Build a factory that resolves user/team-configured AI providers
/// against the canonical platform `IProviderProfile` store (Phase
/// 43.A — this replaces the removed `IUserAIConfigStore` shim;
/// resolution behaviour and the persisted blob are unchanged), honours
/// the deployment's `AIFallbackPolicy`, and looks up API keys from
/// `ISecretStore` scoped to the same container as the profile.
///
/// Resolution chain (per-request):
/// 1. Compute scope from `AccessContext.Mode`. Anonymous + Team-
///    without-a-team yield no scope — fall back per policy.
/// 2. Resolve the entry routed for surface `AIProviderSurface.aiAssistant`
///    (the shim's `ActiveProviderLabel` is now the `{ Surface =
///    "ai.assistant"; Context = None }` routing rule). Missing profile
///    / no matching rule / stale label → fall back per policy.
/// 3. Look up the entry's `ProviderId` in `builders`. Unknown →
///    `UnknownProvider`.
/// 4. Fetch the API key from the secret store at the same scope.
///    Missing → `MissingApiKey`.
/// 5. Invoke the builder with (apiKey, resolved model) and return Ok.
///
/// `platformProvider` is the deployment-provided fallback bundle used
/// under `PlatformOnly` (always) and `PermissiveWithPlatformFallback`
/// (when no entry is routed). `StrictBYOK` never returns the platform
/// provider; missing config surfaces `NoProviderConfigured` to the UI.
/// When the bundle's `Rebuild` is `Some` and the user has a platform-
/// model override (`SurfaceModelOverrides["ai.platform"]`), Resolve
/// returns `rebuild model` instead of the pre-built `Provider`.
///
/// The `Available` view reflects policy:
/// - `PlatformOnly`: empty (no user-configurable providers) — the
///   settings UI hides the configuration surface entirely and
///   instead exposes a minimal model-picker from `PlatformDescriptor`.
/// - `PermissiveWithPlatformFallback` / `StrictBYOK`: all builders'
///   descriptors (what the user can actually pick).
let create
    (builders: AIProviderBuilder list)
    (providerProfile: IProviderProfile)
    (secretStore: ISecretStore)
    (fallbackPolicy: AIFallbackPolicy)
    (platformProvider: AIPlatformProvider option)
    : IAIProviderFactory =

    let builderById = builders |> List.map (fun b -> b.Descriptor.Id, b) |> Map.ofList

    /// Resolve the platform provider for a given access context,
    /// applying the user's platform-model override when one is set and
    /// the bundle supports rebuild. When the bundle doesn't support
    /// rebuild, or no override exists, the pre-built `Provider` is
    /// returned unchanged.
    let platformWithOverride (ctx: AccessContext) (bundle: AIPlatformProvider) = async {
        match bundle.Rebuild with
        | None -> return bundle.Provider
        | Some rebuild ->
            match AccessContext.configScope ctx with
            | None ->
                // No persistent scope (Anonymous) — no override possible.
                return bundle.Provider
            | Some scope ->
                let! profile = providerProfile.Get scope

                let overrideModel =
                    profile
                    |> Option.bind (ProviderProfile.surfaceModelOverride AIProviderSurface.platformModelKey)

                match overrideModel with
                | Some model when
                    bundle.Descriptor.SupportedModels |> List.contains model
                    || model = bundle.Descriptor.DefaultModel
                    ->
                    return rebuild model
                | Some _
                | None -> return bundle.Provider
    }

    let fallback (ctx: AccessContext) : Async<Result<IAIProvider, ProviderResolutionError>> = async {
        match fallbackPolicy, platformProvider with
        | StrictBYOK, _ -> return Error NoProviderConfigured
        | PlatformOnly, Some bundle
        | PermissiveWithPlatformFallback, Some bundle ->
            let! provider = platformWithOverride ctx bundle
            return Ok provider
        | PlatformOnly, None
        | PermissiveWithPlatformFallback, None ->
            // Deployment misconfigured: policy allows fallback but
            // no platform provider supplied.
            return Error NoProviderConfigured
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

        member _.PlatformDescriptor = platformProvider |> Option.map _.Descriptor

        member _.Resolve ctx = async {
            match fallbackPolicy with
            | PlatformOnly ->
                // Profile ignored for provider identity by design,
                // but a user-chosen model override is honoured if the
                // deployment supplied a rebuilder.
                return! fallback ctx
            | PermissiveWithPlatformFallback
            | StrictBYOK ->
                match AccessContext.configScope ctx with
                | None ->
                    // No persistent config applies (Anonymous /
                    // Team-without-team). Fall back per policy.
                    return! fallback ctx
                | Some scope ->
                    // ResolveEntry encapsulates Get + routing-rule
                    // lookup with the same None-on-stale semantics the
                    // shim's `AIUserConfig.activeInstance` had: no
                    // profile, no `ai.assistant` rule, or a rule whose
                    // label points at a deleted entry all yield None.
                    let! entry = providerProfile.ResolveEntry(scope, AIProviderSurface.aiAssistant, None)

                    match entry with
                    | None -> return! fallback ctx
                    | Some e -> return! buildFromEntry e scope
        }

        member _.TryResolveByLabel(ctx, label) = async {
            // Bypasses the routing-rule gate — used by the
            // test-connection flow to verify a specific entry's key
            // without routing to it first. Still honours the
            // mode → scope rules (Team mode reads team scope, etc.)
            // and the builder-lookup / secret-resolution chain.
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
    }