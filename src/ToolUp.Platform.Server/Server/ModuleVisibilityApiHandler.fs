module ToolUp.Platform.ModuleVisibilityApiHandler

open Microsoft.AspNetCore.Http
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

/// `FableConverters` is required on `ModuleVisibilityChangedPayload`
/// because it carries `FlagScope` / `ModuleVisibilityChangeAction` /
/// `ModuleVisibilityRule` DUs — the bare-STJ default contract emits a
/// shape Fable cannot parse. Same pattern as `FeatureFlagHandler`.
let private eventJsonOptions = FableConverters.create ()

/// Build the `IModuleVisibilityApi` Fable.Remoting handler (Phase 637).
/// Resolves `IModuleVisibilityStore` and `AccessContext` lazily from DI
/// per request, the same pattern `FeatureFlagHandler` / `ConfigHandler`
/// use.
///
/// `registeredModuleIds` is captured at compose time from
/// `ServerConfig.ModuleNames` — the derived module surface. The editor's
/// candidate list is therefore whatever the deployment actually composed,
/// never a hand-maintained list that can drift from it.
///
/// **Scope isolation and the write gate.**
/// - Every read and write targets the caller's own
///   `AccessContext.flagScope` (Team in team mode, User for Individual /
///   AuthenticatedEphemeral, `None` for Anonymous). No method accepts a
///   scope from the wire — see `ModuleVisibilityProfileInput` for why
///   the input record deliberately omits one (GP 4).
/// - Writes additionally require `TeamRoles.canWriteTeamConfig` in team
///   mode (Owner/Admin only), mirroring `ConfigHandler` and
///   `FeatureFlagHandler`. A user-scope writer owns their own scope.
/// - Writes validate that every id named by the rule is a registered
///   module. A typo'd id in an `Allow` list is not merely inert — it
///   silently shrinks the surface by one module and looks exactly like a
///   deliberate exclusion, which is the failure mode worth rejecting at
///   the point of entry rather than diagnosing later from a sidebar.
let moduleVisibilityApi (registeredModuleIds: string list) (ctx: HttpContext) : IModuleVisibilityApi =

    let store =
        ctx.RequestServices.GetService(typeof<IModuleVisibilityStore>) :?> IModuleVisibilityStore

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
            // Fallback for tests that bypass ScopeResolutionMiddleware —
            // same pattern as ConfigHandler / FeatureFlagHandler.
            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            AccessContext.unrestricted (AnonymousSession userId)

    /// Team-mode write gate. Mirrors `FeatureFlagHandler.ensureWriteAllowed`:
    /// Owner/Admin may write the team profile; Member is read-only; other
    /// modes are ungated (user-scope callers own their scope).
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
                            $"Only team owners and admins can change module visibility. Your role: {TeamRoles.displayName r}."
                | None -> return Error "You are not a member of this team."
            | _ -> return Error "Team management is not available in this deployment."
        | _ -> return Ok()
    }

    let withScope (f: FlagScope -> Async<Result<_, string>>) = async {
        match AccessContext.flagScope accessContext with
        | None ->
            return Error "Module visibility is not available in this mode. Sign in or join a team to curate modules."
        | Some scope -> return! f scope
    }

    let withWriteScope (f: FlagScope -> Async<Result<_, string>>) = async {
        let! rbac = ensureWriteAllowed ()

        match rbac with
        | Error msg -> return Error msg
        | Ok() -> return! withScope f
    }

    /// Write a `ModuleVisibilityProfileChanged` audit event (GP 6).
    /// Best-effort, exactly as the flag handler's audit is: a missing
    /// `IEventStore` (no DI registration in tests) or a throwing write
    /// logs a Warn and returns. An admin write must not fail because the
    /// audit fabric is unavailable — the alternative is an operator
    /// unable to fix visibility during the very incident that broke the
    /// event store.
    let audit (scope: FlagScope) (action: ModuleVisibilityChangeAction) = async {
        match eventStoreOpt with
        | None -> return ()
        | Some es ->
            try
                let payload = {
                    Scope = scope
                    Action = action
                    ChangedBy = accessContext.UserId
                }

                let json = JsonSerializer.Serialize(payload, eventJsonOptions)

                let evt =
                    Events.create
                        (FlagScope.slug scope)
                        ModuleVisibilityEvents.SourceModule
                        ModuleVisibilityEvents.ProfileChanged
                        json

                do! es.Write evt
            with ex ->
                logger.Warn $"ModuleVisibility: audit write failed scope={FlagScope.slug scope}: {ex.Message}"
    }

    /// Reject ids the deployment does not register. Applies to both rule
    /// shapes: an unknown id in a `Deny` list is just as much a typo, and
    /// it is worse to diagnose because the surface looks correct.
    ///
    /// `ExcludedEntryIds` is NOT checked against the registered set — an
    /// entry there may be a composite `{moduleId}{pageRoute}` page id,
    /// whose module component the server can see but whose page routes it
    /// cannot (pages are declared client-side, on `ModuleDefinition`). A
    /// half-check that validated only the bare-id case would reject the
    /// composite form or accept anything with a slash in it; neither is
    /// worth the confusion, so page exclusions are accepted as written
    /// and are inert when they name nothing.
    let validateRule (rule: ModuleVisibilityRule) : Result<ModuleVisibilityRule, string> =
        let ids =
            match rule with
            | ModuleVisibilityRule.Allow xs
            | ModuleVisibilityRule.Deny xs -> xs

        match ids |> List.filter (fun id -> not (List.contains id registeredModuleIds)) with
        | [] -> Ok rule
        | unknown ->
            Error
                $"""Unknown module id(s): {String.concat ", " unknown}. This deployment registers: {String.concat ", " registeredModuleIds}."""

    {
        GetResolvedVisibility =
            fun () -> async { return! ModuleVisibilityResolver.resolveFor store registeredModuleIds accessContext }

        ListRegisteredModules = fun () -> async { return registeredModuleIds }

        GetProfile =
            fun () ->
                withScope (fun scope -> async {
                    let! profile = store.GetProfile scope
                    return Ok profile
                })

        SetProfile =
            fun input ->
                withWriteScope (fun scope -> async {
                    match validateRule input.Rule with
                    | Error msg -> return Error msg
                    | Ok rule ->
                        let profile = {
                            Scope = scope
                            Rule = rule
                            ExcludedEntryIds = input.ExcludedEntryIds
                            Note = input.Note
                        }

                        let! result = store.SetProfile(scope, profile)

                        match result with
                        | Ok() ->
                            logger.Info $"ModuleVisibility: profile saved scope={FlagScope.slug scope}"
                            do! audit scope (ModuleVisibilityChangeAction.Saved profile)
                            return Ok()
                        | Error e ->
                            logger.Warn $"ModuleVisibility: save failed scope={FlagScope.slug scope}: {e}"
                            return Error e
                })

        ClearProfile =
            fun () ->
                withWriteScope (fun scope -> async {
                    do! store.ClearProfile scope
                    logger.Info $"ModuleVisibility: profile cleared scope={FlagScope.slug scope}"
                    do! audit scope ModuleVisibilityChangeAction.Cleared
                    return Ok()
                })
    }