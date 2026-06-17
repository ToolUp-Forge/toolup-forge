module ToolUp.Platform.ConfigHandler

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

/// Build the `IConfigApi` Fable.Remoting handler. Resolves
/// `IConfigStore`, `AccessContext`, and `ITeamStore` lazily from DI per
/// request (same pattern as `AISettingsHandler` and the built-in
/// `PlatformApi`). Registered modules come from `ServerConfig.ModuleConfigs`
/// and are captured at compose time — the list doesn't change within a
/// process lifetime.
///
/// Scope isolation and write gating:
/// - Read and write paths both resolve `AccessContext.configScope`;
///   `Anonymous` (no persistent config) short-circuits to an error.
/// - Writes additionally require `TeamRoles.canWriteTeamConfig` in
///   Team mode (Owner/Admin only). Individual and AuthenticatedEphemeral
///   users own their user-scope config.
/// - Reads are ungated within the caller's resolved scope — a team
///   member can see team config, a user can see their own. Cross-scope
///   reads are impossible; the handler never resolves another scope.
let configApi (entries: ModuleConfigEntry list) (ctx: HttpContext) : IConfigApi =

    let store = ctx.RequestServices.GetService(typeof<IConfigStore>) :?> IConfigStore

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
            // — same pattern as AISettingsHandler.
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

    let findEntry (moduleKey: string) =
        entries |> List.tryFind (fun e -> e.ModuleKey = moduleKey)

    /// Team-mode write gate. Mirrors AISettingsHandler.ensureWriteAllowed:
    /// Owner/Admin may write team config; Member is read-only; other
    /// modes are ungated (user-scope users own their config).
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
                            $"Only team owners and admins can modify team configuration. Your role: {TeamRoles.displayName r}."
                | None -> return Error "You are not a member of this team."
            | _ -> return Error "Team management is not available in this deployment."
        | _ -> return Ok()
    }

    let withScope (f: StorageScope -> Async<Result<_, string>>) = async {
        match scopeOpt with
        | None ->
            return Error "Configuration is not available in this mode. Sign in or join a team to configure modules."
        | Some scope -> return! f scope
    }

    let withWriteScope (f: StorageScope -> Async<Result<_, string>>) = async {
        let! rbac = ensureWriteAllowed ()

        match rbac with
        | Error msg -> return Error msg
        | Ok() -> return! withScope f
    }

    {
        ListModules = fun () -> async { return entries }

        GetModuleConfig =
            fun moduleKey ->
                withScope (fun scope -> async {
                    match findEntry moduleKey with
                    | None -> return Error $"Unknown module config key: '{moduleKey}'."
                    | Some entry ->
                        let! values = store.GetRaw(scope, moduleKey)

                        return
                            Ok {
                                ModuleKey = moduleKey
                                Schema = entry.Schema
                                Values = values
                            }
                })

        GetAllModuleConfigs =
            fun () -> async {
                // Boot-path batch: one round-trip for every module's
                // persisted map instead of the client's ListModules +
                // per-module GetModuleConfig fan-out. No config scope
                // (Anonymous) yields an empty map — the same empty-config
                // result the per-module path produced, never an error.
                // The per-key reads run in parallel; on a remote
                // IConfigStore (one blob per scope/module) that keeps the
                // server-side cost a single fan-out, not a serial walk.
                match scopeOpt with
                | None -> return Map.empty
                | Some scope ->
                    let! pairs =
                        entries
                        |> List.map (fun entry -> async {
                            let! values = store.GetRaw(scope, entry.ModuleKey)
                            return entry.ModuleKey, values
                        })
                        |> Async.Parallel

                    return Map.ofArray pairs
            }

        SaveModuleConfig =
            fun (moduleKey, values) ->
                withWriteScope (fun scope -> async {
                    match findEntry moduleKey with
                    | None -> return Error $"Unknown module config key: '{moduleKey}'."
                    | Some entry ->
                        let! result = store.SetRaw(scope, moduleKey, values, entry.Schema)

                        match result with
                        | Ok() ->
                            logger.Info
                                $"Config: saved moduleKey='{moduleKey}' scope={scope.Container} fields={values.Count}"

                            return Ok()
                        | Error e ->
                            logger.Warn $"Config: save failed moduleKey='{moduleKey}' scope={scope.Container}: {e}"

                            return Error e
                })

        ClearModuleConfig =
            fun moduleKey ->
                withWriteScope (fun scope -> async {
                    match findEntry moduleKey with
                    | None -> return Error $"Unknown module config key: '{moduleKey}'."
                    | Some _ ->
                        do! store.Clear(scope, moduleKey)

                        logger.Info $"Config: cleared moduleKey='{moduleKey}' scope={scope.Container}"

                        return Ok()
                })
    }