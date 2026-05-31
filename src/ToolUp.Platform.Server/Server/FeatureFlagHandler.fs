module ToolUp.Platform.FeatureFlagHandler

open Microsoft.AspNetCore.Http
open Newtonsoft.Json
open Fable.Remoting.Json
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

/// `FableJsonConverter` is required on `FlagChangedPayload` because it
/// carries a `FlagScope` / `FlagChangeAction` / `FlagValue` DU — the
/// default Newtonsoft contract emits a shape Fable cannot parse.
/// Same pattern as `ConfigStore`, `FeatureFlagStore`, and
/// `NotificationHandler`. Event payloads are server-only (`IEventStore`
/// is never surfaced to the client), but keeping the converter keeps
/// the persisted representation consistent with every other SDK store
/// and future-proofs direct Fable consumers.
let private eventJsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(FableJsonConverter())
    s

/// Build the `IFeatureFlagApi` Fable.Remoting handler. Resolves
/// `FlagEvaluator`, `IFeatureFlagStore`, and `AccessContext` lazily
/// from DI per request (same pattern as `ConfigHandler`,
/// `AISettingsHandler`, etc.).
///
/// `declared` is captured at compose time — the union of platform
/// flags (`ServerConfig.FeatureFlags`) and module-declared flags. The
/// handler never invents keys; an empty `declared` list produces an
/// empty resolved map and rejects every admin write as unknown-key.
///
/// Resolution routes through `FlagEvaluator.Resolve`, which coerces
/// stored overrides to the declared shape (schema drift — e.g. a
/// manual blob edit that persisted a Variant under a Bool-declared
/// key — falls back to the declared default and logs a Warn in the
/// evaluator). The returned map is safe for the client to consume
/// without further validation.
///
/// Admin-path scope isolation and write gating:
/// - Admin reads / writes target the caller's `AccessContext.flagScope`
///   (Team for Team mode, User for Individual / AuthenticatedEphemeral,
///   `None` for Anonymous). Platform-scope writes are not exposed
///   through this API — see `AccessContext.flagScope` commentary.
/// - Writes additionally require `TeamRoles.canWriteTeamConfig` in
///   Team mode (Owner/Admin only), mirroring `ConfigHandler`. User-
///   scope writers own their scope.
/// - Writes validate the payload against the declared flag's shape:
///   `Bool` key accepts only `FlagValue.Bool`; `Variant` key accepts
///   only `FlagValue.Variant` whose value is in the declared option
///   list. Unknown keys are rejected to surface admin-UI typos.
let featureFlagApi (declared: FeatureFlag list) (ctx: HttpContext) : IFeatureFlagApi =

    let evaluator =
        ctx.RequestServices.GetService(typeof<FlagEvaluator.FlagEvaluator>) :?> FlagEvaluator.FlagEvaluator

    let store =
        ctx.RequestServices.GetService(typeof<IFeatureFlagStore>) :?> IFeatureFlagStore

    let eventStoreOpt: IEventStore option =
        match ctx.RequestServices.GetService(typeof<IEventStore>) with
        | :? IEventStore as es -> Some es
        | _ -> None

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

    let accessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac
        | _ ->
            // Fallback for tests that bypass ScopeResolutionMiddleware
            // — same pattern as ConfigHandler / AISettingsHandler.
            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            let teamId =
                match ctx.Items.TryGetValue "ToolUp.StorageScope" with
                | true, (:? StorageScope as s) when s.Container.StartsWith "team-" -> Some s.ScopeId
                | _ -> None

            AccessContext.unrestricted (AnonymousSession userId)

    let findDeclared (key: string) =
        declared |> List.tryFind (fun f -> f.Key = key)

    /// Team-mode write gate. Mirrors `ConfigHandler.ensureWriteAllowed`:
    /// Owner/Admin may write team flags; Member is read-only; other
    /// modes are ungated (user-scope users own their scope).
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
                            $"Only team owners and admins can modify team feature flags. Your role: {TeamRoles.displayName r}."
                | None -> return Error "You are not a member of this team."
            | _ -> return Error "Team management is not available in this deployment."
        | _ -> return Ok()
    }

    let withScope (f: FlagScope -> Async<Result<_, string>>) = async {
        match AccessContext.flagScope accessContext with
        | None -> return Error "Feature flags are not available in this mode. Sign in or join a team to manage flags."
        | Some scope -> return! f scope
    }

    let withWriteScope (f: FlagScope -> Async<Result<_, string>>) = async {
        let! rbac = ensureWriteAllowed ()

        match rbac with
        | Error msg -> return Error msg
        | Ok() -> return! withScope f
    }

    /// Write a `FlagChanged` audit event to `IEventStore`. Event
    /// failures must not fail the admin write — audit is best-effort;
    /// if the store is absent (no DI registration in tests) or the
    /// write throws, log a Warn and return silently. Integrates with
    /// Phase 9's audit-query UI when it lands; until then downstream
    /// consumers read directly from `IEventStore.ReadByType (scopeSlug,
    /// "FlagChanged")`.
    let audit (scope: FlagScope) (action: FlagChangeAction) (key: string) = async {
        match eventStoreOpt with
        | None -> return ()
        | Some es ->
            try
                let payload = {
                    Key = key
                    Scope = scope
                    Action = action
                    ChangedBy = accessContext.UserId
                }

                let json = JsonConvert.SerializeObject(payload, eventJsonSettings)

                let evt =
                    Events.create
                        (FlagScope.slug scope)
                        FeatureFlagEvents.SourceModule
                        FeatureFlagEvents.FlagChanged
                        json

                do! es.Write evt
            with ex ->
                logger.Warn $"FeatureFlags: audit write failed key='{key}' scope={FlagScope.slug scope}: {ex.Message}"
    }

    /// Validate that an incoming value matches the declared shape.
    /// `Bool` declarations accept only `FlagValue.Bool`; `Variant`
    /// declarations accept only `FlagValue.Variant` whose `value` is
    /// in the declared option list. The stored option list is ignored
    /// — the declared option list is authoritative.
    let validateShape (declaredFlag: FeatureFlag) (incoming: FlagValue) : Result<FlagValue, string> =
        match declaredFlag.DefaultValue, incoming with
        | FlagValue.Bool _, FlagValue.Bool _ -> Ok incoming
        | FlagValue.Variant(options, _), FlagValue.Variant(_, v) ->
            if options |> List.contains v then
                // Normalise: always persist using the declared option list,
                // not whatever the caller sent. Prevents drift between
                // declared and stored option sets.
                Ok(FlagValue.Variant(options, v))
            else
                Error $"Value '{v}' is not in the declared options for flag '{declaredFlag.Key}'."
        | FlagValue.Bool _, FlagValue.Variant _ ->
            Error $"Flag '{declaredFlag.Key}' is declared as Bool — expected a boolean value."
        | FlagValue.Variant _, FlagValue.Bool _ ->
            Error $"Flag '{declaredFlag.Key}' is declared as Variant — expected a variant value."

    {
        GetResolvedFlags =
            fun () -> async {
                match declared with
                | [] -> return Map.empty
                | flags ->
                    let! pairs =
                        flags
                        |> List.map (fun flag -> async {
                            let! v = evaluator.Resolve flag accessContext
                            return flag.Key, v
                        })
                        |> Async.Parallel

                    return pairs |> Map.ofArray
            }

        ListDeclared = fun () -> async { return declared }

        GetOverrides =
            fun () ->
                withScope (fun scope -> async {
                    let! overrides = store.ListFlags scope
                    return Ok overrides
                })

        SetOverride =
            fun (key, value) ->
                withWriteScope (fun scope -> async {
                    match findDeclared key with
                    | None -> return Error $"Unknown feature flag: '{key}'."
                    | Some flag ->
                        match validateShape flag value with
                        | Error msg -> return Error msg
                        | Ok normalised ->
                            let! result = store.SetFlag(scope, key, normalised)

                            match result with
                            | Ok() ->
                                logger.Info $"FeatureFlags: set key='{key}' scope={FlagScope.slug scope}"
                                do! audit scope (FlagChangeAction.Set normalised) key
                                return Ok()
                            | Error e ->
                                logger.Warn $"FeatureFlags: set failed key='{key}' scope={FlagScope.slug scope}: {e}"
                                return Error e
                })

        ClearOverride =
            fun key ->
                withWriteScope (fun scope -> async {
                    match findDeclared key with
                    | None -> return Error $"Unknown feature flag: '{key}'."
                    | Some _ ->
                        do! store.ClearFlag(scope, key)
                        logger.Info $"FeatureFlags: cleared key='{key}' scope={FlagScope.slug scope}"
                        do! audit scope FlagChangeAction.Cleared key
                        return Ok()
                })
    }