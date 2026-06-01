module ToolUp.AI.AISettingsHandler

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.Platform.TeamManagement
open ToolUp.AI

// ─── Test-connection payload ─────────────────────────────────────

/// Minimal provider message used to validate a configured instance
/// (authentication + reachability). Content doesn't matter to the
/// caller — any successful response is sufficient to confirm the
/// key works.
let private testMessage: AIProviderMessage = {
    Role = "user"
    Content = "ping"
    ToolCalls = []
    ToolResults = []
    Parts = []
}

/// Tight system prompt that discourages long responses — minimises
/// token usage when testing. Providers still charge for the round-
/// trip; `TestConnection` is not free.
let private testSystemPrompt = Some "Reply with a single word."

/// Secret-store key name for an instance's API key. Per-instance (not
/// per-provider) so two instances of the same provider can hold
/// separate keys — required for the multi-instance design (decision
/// point 3 of the BYOK plan).
let private secretKeyName (label: string) = $"ai-key-{label}"

// ─── View projection ─────────────────────────────────────────────

let private toView (entry: ProviderEntry) (hasKey: bool) : AIProviderInstanceView = {
    Label = entry.Label
    ProviderId = entry.ProviderId
    Model = entry.Model
    HasApiKey = hasKey
    UpdatedAt = entry.UpdatedAt
}

// ─── Routing helpers (Phase 43.A) ────────────────────────────────
//
// The AI assistant's "active provider" is the platform store's
// routing rule `{ Surface = "ai.assistant"; Context = None }`. These
// mirror the lossless mapping the removed `IUserAIConfigStore` shim
// applied, so a settings-API action persists the same blob it did
// before the cutover (the byte-identical-state regression bar).

let private activeLabel (p: ProviderProfile) : string option =
    p.Routing
    |> List.tryFind (fun r -> r.Surface = AIProviderSurface.aiAssistant && r.Context = None)
    |> Option.map _.EntryLabel

/// Drop every routing rule whose target is `label`. In a profile only
/// ever mutated through this settings API the sole rule is the
/// `ai.assistant` default, so this matches the shim's
/// "clear ActiveProviderLabel when it pointed at the deleted entry"
/// semantics exactly while staying correct for richer profiles.
let private removeRoutesTo (label: string) (p: ProviderProfile) : ProviderProfile = {
    p with
        Routing = p.Routing |> List.filter (fun r -> r.EntryLabel <> label)
}

/// Clear the `ai.assistant` default route (SetActive None).
let private clearActiveRoute (p: ProviderProfile) : ProviderProfile = {
    p with
        Routing =
            p.Routing
            |> List.filter (fun r -> not (r.Surface = AIProviderSurface.aiAssistant && r.Context = None))
}

// ─── Handler factory ─────────────────────────────────────────────

/// Build the AISettingsApi Fable.Remoting handler. Resolves
/// `ISecretStore` from DI per request (same pattern as
/// `AIAssistantHandler.aiAssistantApi`); `IAIProviderFactory` and the
/// canonical platform `IProviderProfile` store are passed in at
/// compose time since composeAI already has them in hand. Phase
/// 43.A — reads/writes the platform store directly; the
/// `IUserAIConfigStore` shim is removed. The client wire contract
/// (`AISettingsApi` / `AIUserConfigView` / `AIProviderInstanceView`)
/// is unchanged — only the server-side persistence target moved.
let aiSettingsApi (factory: IAIProviderFactory) (providerProfile: IProviderProfile) (ctx: HttpContext) : AISettingsApi =

    let secretStore =
        ctx.RequestServices.GetService(typeof<ISecretStore>) :?> ISecretStore

    // Observational logger; silent no-op when DI has none (tests
    // bypassing `compose`). Must not log API keys or provider output.
    let logger: ILogger =
        match ctx.RequestServices.GetService(typeof<ILogger>) with
        | :? ILogger as l -> l
        | _ ->
            { new ILogger with
                member _.Debug _ = ()
                member _.Info _ = ()
                member _.Warn _ = ()
                member _.Error(_, _) = ()
            }

    // Same access-context resolution pattern as AIAssistantHandler —
    // DI first, fall back to HttpContext.Items for tests that bypass
    // composeWithAI's middleware.
    let accessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac
        | _ ->
            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            let teamId =
                match ctx.Items.TryGetValue "ToolUp.StorageScope" with
                | true, (:? StorageScope as s) when s.Container.StartsWith "team-" -> Some s.ScopeId
                | _ -> None

            AccessContext.unrestricted (AnonymousSession userId)

    let scopeOpt = AccessContext.configScope accessContext

    /// In Team mode only Owner and Admin roles may write team-scoped
    /// AI configuration (decision point 2 of the BYOK plan: team AIs
    /// are team-managed, no personal overrides; this is the enforcement
    /// of that for write operations). Members can still READ the team's
    /// config (GetMyConfig is not gated).
    ///
    /// Other modes are ungated — Individual / AuthenticatedEphemeral
    /// users own their user-scope config; Anonymous is blocked by the
    /// scope-None path in `withScope`.
    let ensureWriteAllowed () : Async<Result<unit, string>> = async {
        match accessContext.Subject with
        | TeamMember(userId, teamId) ->
            match ctx.RequestServices.GetService(typeof<ITeamStore>) with
            | :? ITeamStore as ts ->
                let! role = ts.GetMemberRole(teamId, userId)

                match role with
                | Some r when TeamRoles.canWriteTeamConfig r -> return Ok()
                | Some r ->
                    return
                        Error
                            $"Only team owners and admins can modify team AI settings. Your role: {TeamRoles.displayName r}."
                | None -> return Error "You are not a member of this team."
            | _ ->
                // ITeamStore not registered — deployment misconfiguration.
                return Error "Team management is not available in this deployment."
        | _ -> return Ok()
    }

    /// Common preamble for reads: resolve scope or short-circuit with
    /// an error message suitable for the client.
    let withScope (f: StorageScope -> Async<Result<_, string>>) = async {
        match scopeOpt with
        | None ->
            return
                Error "AI configuration is not available in this mode. Sign in or join a team to configure providers."
        | Some scope -> return! f scope
    }

    /// Common preamble for writes: ensure scope exists AND the caller
    /// has write permission (Owner/Admin in Team mode, unrestricted
    /// in user modes).
    let withWriteScope (f: StorageScope -> Async<Result<_, string>>) = async {
        let! rbac = ensureWriteAllowed ()

        match rbac with
        | Error msg -> return Error msg
        | Ok() -> return! withScope f
    }

    {
        ListAvailable = fun () -> async { return factory.Available }

        GetMyConfig =
            fun () -> async {
                match scopeOpt with
                | None -> return AIUserConfigView.empty
                | Some scope ->
                    let! profile = providerProfile.Get scope
                    let p = profile |> Option.defaultValue (ProviderProfile.empty ())

                    // Check HasApiKey for each entry in parallel. The
                    // secret-store reads are independent; serialising
                    // them would add latency for no gain.
                    let! instances =
                        p.Entries
                        |> List.map (fun entry -> async {
                            let! key = secretStore.GetSecret(scope.Container, entry.SecretKeyName)
                            return toView entry (key.IsSome)
                        })
                        |> Async.Parallel

                    return {
                        ConfiguredProviders = instances |> Array.toList
                        ActiveProviderLabel = activeLabel p
                        PlatformModelOverride =
                            ProviderProfile.surfaceModelOverride AIProviderSurface.platformModelKey p
                        PlatformProviderOverride =
                            ProviderProfile.surfaceProviderOverride AIProviderSurface.platformProviderKey p
                    }
            }

        SaveInstance =
            fun req ->
                withWriteScope (fun scope -> async {
                    // Validate ProviderId against the factory's
                    // Available catalogue. Rejecting unknown providers
                    // prevents orphan configs that would resolve to
                    // UnknownProvider at chat time.
                    match factory.Available |> List.tryFind (fun d -> d.Id = req.ProviderId) with
                    | None -> return Error $"Unknown provider: '{req.ProviderId}'"
                    | Some _ ->
                        let keyName = secretKeyName req.Label

                        // Upsert: remove any existing entry with the
                        // same label, then append the new one. Order
                        // doesn't matter for resolution; removing
                        // first keeps the list deduplicated.
                        let! existing = providerProfile.Get scope
                        let current = existing |> Option.defaultValue (ProviderProfile.empty ())

                        let priorEntry = current.Entries |> List.tryFind (fun e -> e.Label = req.Label)

                        // The 42.B-only fields (Tags / Origin / Health)
                        // are not part of the settings-API wire contract.
                        // A brand-new entry gets the same defaults the
                        // removed shim wrote (Tags=[], Origin=PastedKey,
                        // Health=unknown) — byte-identical persisted
                        // state for a settings-only action sequence. An
                        // *edit* preserves the existing entry's values so
                        // a Phase 43.B OAuth origin or a Phase 43.C
                        // probe-written health survives a later metadata
                        // edit (the shim could not — it round-tripped
                        // through a lossy `AIUserConfig`).
                        let newEntry: ProviderEntry = {
                            Label = req.Label
                            ProviderId = req.ProviderId
                            Model = req.Model
                            SecretKeyName = keyName
                            Tags = priorEntry |> Option.map _.Tags |> Option.defaultValue []
                            Origin =
                                priorEntry
                                |> Option.map _.Origin
                                |> Option.defaultValue CredentialOrigin.PastedKey
                            Health = priorEntry |> Option.map _.Health |> Option.defaultValue ProviderHealth.unknown
                            UpdatedAt = DateTime.UtcNow
                        }

                        let updated = {
                            current with
                                Entries =
                                    (current.Entries |> List.filter (fun e -> e.Label <> req.Label)) @ [ newEntry ]
                                UpdatedAt = DateTime.UtcNow
                        }

                        // Persist the API key when provided. A `None`
                        // ApiKey means the caller is editing metadata
                        // (model, provider) without rotating the key.
                        let keyRotated = req.ApiKey.IsSome

                        let! saveResult =
                            match req.ApiKey with
                            | Some key -> async {
                                let! keyResult = secretStore.SetSecret(scope.Container, keyName, key)

                                match keyResult with
                                | Error e -> return Error $"Failed to store API key: {e}"
                                | Ok() -> return! providerProfile.Set(scope, updated)
                              }
                            | None -> providerProfile.Set(scope, updated)

                        match saveResult with
                        | Ok() ->
                            logger.Info
                                $"AI settings: saved instance label='{req.Label}' provider={req.ProviderId} model={req.Model} scope={scope.Container} keyRotated={keyRotated}"

                            return Ok()
                        | Error e ->
                            logger.Warn
                                $"AI settings: save failed for label='{req.Label}' scope={scope.Container}: {e}"

                            return Error e
                })

        DeleteInstance =
            fun label ->
                withWriteScope (fun scope -> async {
                    let! existing = providerProfile.Get scope

                    match existing with
                    | None ->
                        // No config saved — delete is idempotent.
                        logger.Info $"AI settings: delete noop (no config) for label='{label}' scope={scope.Container}"

                        return Ok()
                    | Some current ->
                        // Clear the associated key first. If the key
                        // delete fails we still update the config so
                        // the user isn't blocked — orphaned secrets
                        // are harmless (nothing references them).
                        let! _ = secretStore.DeleteSecret(scope.Container, secretKeyName label)

                        // Remove the entry and any routing rule that
                        // targeted it. `removeRoutesTo` reproduces the
                        // shim's "clear active when it pointed here"
                        // behaviour (the only rule a settings-only
                        // profile carries is the `ai.assistant`
                        // default).
                        let pruned = current |> removeRoutesTo label

                        let updated = {
                            pruned with
                                Entries = pruned.Entries |> List.filter (fun e -> e.Label <> label)
                                UpdatedAt = DateTime.UtcNow
                        }

                        let! result = providerProfile.Set(scope, updated)

                        match result with
                        | Ok() ->
                            logger.Info $"AI settings: deleted instance label='{label}' scope={scope.Container}"
                            return Ok()
                        | Error e ->
                            logger.Warn $"AI settings: delete failed for label='{label}' scope={scope.Container}: {e}"
                            return Error e
                })

        SetActive =
            fun labelOpt ->
                withWriteScope (fun scope -> async {
                    let! existing = providerProfile.Get scope
                    let current = existing |> Option.defaultValue (ProviderProfile.empty ())

                    // Validate that the label exists (or is None to
                    // clear active).
                    let exists label =
                        current.Entries |> List.exists (fun e -> e.Label = label)

                    match labelOpt with
                    | Some l when not (exists l) ->
                        logger.Warn
                            $"AI settings: setActive rejected — no instance with label='{l}' in scope={scope.Container}"

                        return Error $"No instance with label '{l}' is configured"
                    | _ ->
                        // `Some l` upserts the `ai.assistant` default
                        // route; `None` clears it — the lossless
                        // mapping the shim applied for
                        // `ActiveProviderLabel`.
                        let routed =
                            match labelOpt with
                            | Some l -> current |> ProviderProfile.withRoute AIProviderSurface.aiAssistant None l
                            | None -> current |> clearActiveRoute

                        let updated = {
                            routed with
                                UpdatedAt = DateTime.UtcNow
                        }

                        let! result = providerProfile.Set(scope, updated)

                        match result with
                        | Ok() ->
                            let rendered = labelOpt |> Option.defaultValue "<none>"
                            logger.Info $"AI settings: active provider set to '{rendered}' scope={scope.Container}"
                            return Ok()
                        | Error e ->
                            logger.Warn $"AI settings: setActive failed scope={scope.Container}: {e}"
                            return Error e
                })

        TestConnection =
            fun label -> async {
                // Gate behind the same RBAC as config writes: a
                // successful test issues a billed provider request
                // against the team's shared key, so it belongs on the
                // write side of the fence in Team mode. Other modes are
                // unaffected (`ensureWriteAllowed` is a no-op outside
                // Team). Ungated members who want to know whether team
                // AI is working should ask an Owner / Admin.
                let! rbac = ensureWriteAllowed ()

                match rbac with
                | Error msg ->
                    logger.Warn
                        $"AI settings: test-connection blocked by RBAC label='{label}' user={accessContext.UserId}: {msg}"

                    return Error msg
                | Ok() ->

                    // Use the factory's TryResolveByLabel — same resolution
                    // chain as a real chat turn but gated on the specified
                    // label instead of ActiveProviderLabel. Returns Ok on
                    // any successful provider response; the test message
                    // content is arbitrary.
                    let! providerResult = factory.TryResolveByLabel(accessContext, label)

                    match providerResult with
                    | Error err ->
                        logger.Warn
                            $"AI settings: test-connection resolution failed label='{label}' user={accessContext.UserId}: {ProviderResolutionError.toMessage err}"

                        return Error(ProviderResolutionError.toMessage err)
                    | Ok provider ->
                        let! response =
                            provider.SendMessage([ testMessage ], [], testSystemPrompt, None, RetryPolicy.noRetry)

                        match response with
                        | Ok _ ->
                            logger.Info
                                $"AI settings: test-connection ok label='{label}' user={accessContext.UserId} provider={provider.Capabilities.ProviderName}/{provider.Capabilities.Model}"

                            return Ok()
                        | Error err ->
                            logger.Warn
                                $"AI settings: test-connection provider error label='{label}' user={accessContext.UserId}: {AIProviderError.toMessage err}"

                            return Error(AIProviderError.toMessage err)
            }

        GetPlatformDescriptor = fun () -> async { return factory.PlatformDescriptor }

        SetPlatformModelOverride =
            fun modelOpt ->
                // Intentionally uses `withScope` (not `withWriteScope`): a
                // user's model preference is user-owned even in Team mode
                // — it only takes effect when the factory falls back to
                // the platform provider, which is a personal-rendering
                // choice, not a team-configuration change. Only the model
                // choice is user-editable.
                withScope (fun scope -> async {
                    // Phase 70: validate the model against ANY wired
                    // platform descriptor's models (not just the singular
                    // one). The UI's provider+model picker pairs
                    // `SetPlatformProviderOverride` with this call when
                    // the user switches provider, so the model belongs
                    // to whichever provider the user just chose. `None`
                    // is always allowed (clears the override).
                    let validates =
                        match modelOpt, factory.PlatformDescriptors with
                        | None, _ -> Ok()
                        | Some _, [] -> Error "This deployment has no platform-configured AI provider."
                        | Some m, descriptors ->
                            let isKnown =
                                descriptors
                                |> List.exists (fun d -> d.SupportedModels |> List.contains m || m = d.DefaultModel)

                            if isKnown then
                                Ok()
                            else
                                Error $"Model '{m}' is not supported by any configured platform provider."

                    match validates with
                    | Error e -> return Error e
                    | Ok() ->
                        let! existing = providerProfile.Get scope
                        let current = existing |> Option.defaultValue (ProviderProfile.empty ())

                        let updated = {
                            (current
                             |> ProviderProfile.withSurfaceModelOverride AIProviderSurface.platformModelKey modelOpt) with
                                UpdatedAt = DateTime.UtcNow
                        }

                        let! result = providerProfile.Set(scope, updated)

                        match result with
                        | Ok() ->
                            let rendered = modelOpt |> Option.defaultValue "<cleared>"

                            logger.Info
                                $"AI settings: platform model override set to '{rendered}' scope={scope.Container}"

                            return Ok()
                        | Error e ->
                            logger.Warn
                                $"AI settings: platform model override save failed scope={scope.Container}: {e}"

                            return Error e
                })

        SetPlatformProviderOverride =
            fun providerIdOpt ->
                // Phase 70 — same user-rendering scope semantics as
                // `SetPlatformModelOverride`. Validates the supplied id
                // against the deployment's wired `PlatformDescriptors`;
                // `None` clears the override (resolution falls back to
                // the first descriptor).
                withScope (fun scope -> async {
                    let validates =
                        match providerIdOpt, factory.PlatformDescriptors with
                        | None, _ -> Ok()
                        | Some _, [] -> Error "This deployment has no platform-configured AI provider."
                        | Some id, descriptors ->
                            if descriptors |> List.exists (fun d -> d.Id = id) then
                                Ok()
                            else
                                Error $"Provider '{id}' is not configured for this deployment."

                    match validates with
                    | Error e -> return Error e
                    | Ok() ->
                        let! existing = providerProfile.Get scope
                        let current = existing |> Option.defaultValue (ProviderProfile.empty ())

                        let updated = {
                            (current
                             |> ProviderProfile.withSurfaceProviderOverride
                                 AIProviderSurface.platformProviderKey
                                 providerIdOpt) with
                                UpdatedAt = DateTime.UtcNow
                        }

                        let! result = providerProfile.Set(scope, updated)

                        match result with
                        | Ok() ->
                            let rendered = providerIdOpt |> Option.defaultValue "<cleared>"

                            logger.Info
                                $"AI settings: platform provider override set to '{rendered}' scope={scope.Container}"

                            return Ok()
                        | Error e ->
                            logger.Warn
                                $"AI settings: platform provider override save failed scope={scope.Container}: {e}"

                            return Error e
                })
    }