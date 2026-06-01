// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.PlatformAIKeysHandler

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.TeamManagement
open ToolUp.AI

// ─── Phase 70 — Platform Admin AI keys handler ──────────────────
//
// Backs the `PlatformAIKeysApi` Fable.Remoting contract. Every
// method gates on `AccessContext.canModifyPlatformConfig`; non-admin
// callers receive the same wording the existing
// `PlatformAdminApiHandler` uses so client-side error banners render
// uniformly.
//
// **The current key value is never returned.** Read paths emit
// `HasKey: bool` projections; write paths consume a value from the
// caller but never echo it back. Test-key calls issue billed
// provider requests using the stored value resolved internally.

let private testMessage: AIProviderMessage = {
    Role = "user"
    Content = "ping"
    ToolCalls = []
    ToolResults = []
    Parts = []
}

let private testSystemPrompt = Some "Reply with a single word."

let private toTeamView (t: TeamInfo) : PlatformAIKeysTeamView = {
    TeamId = t.TeamId
    DisplayName = t.Name
}

/// Resolve `AccessContext` from DI per request — same pattern as
/// `AISettingsHandler.aiSettingsApi` / `PlatformAdminApiHandler.platformAdminApi`.
let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        AccessContext.unrestricted (AnonymousSession userId)

/// Build the `PlatformAIKeysApi` Fable.Remoting handler. Resolves
/// `IPlatformAIKeyStore` and `ITeamStore` from DI; the factory's
/// `PlatformDescriptors` + `BuildPlatform` are consumed from the
/// passed-in factory (which `composeAI` already has in hand).
let platformAIKeysApi (factory: IAIProviderFactory) (ctx: HttpContext) : PlatformAIKeysApi =

    let accessContext = resolveAccessContext ctx

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

    let store =
        ctx.RequestServices.GetService(typeof<IPlatformAIKeyStore>) :?> IPlatformAIKeyStore

    let teamStoreOpt =
        match ctx.RequestServices.GetService(typeof<ITeamStore>) with
        | :? ITeamStore as ts -> Some ts
        | _ -> None

    /// Gate every method behind the Platform Admin role check.
    /// Identical wording to `PlatformAdminApiHandler.withGate` so the
    /// client-side error banners render uniformly.
    let withAdmin (work: unit -> Async<Result<_, string>>) = async {
        if not (AccessContext.canModifyPlatformConfig accessContext) then
            return Error "platform admin role required"
        else
            return! work ()
    }

    /// Validate a provider id against the wired descriptors. Returns
    /// `Ok ()` when the id matches; `Error` otherwise.
    let validateProviderId (providerId: string) =
        if factory.PlatformDescriptors |> List.exists (fun d -> d.Id = providerId) then
            Ok()
        else
            Error $"Provider '{providerId}' is not configured for this deployment."

    /// Shared key-test path. Resolves the stored key for (scope, providerId),
    /// constructs the provider via its wired `Build` closure with the
    /// descriptor's `DefaultModel`, and issues a minimal ping.
    let runTest
        (scopeLabel: string)
        (providerId: string)
        (loadKey: unit -> Async<string option>)
        : Async<Result<unit, string>> =
        async {
            match validateProviderId providerId with
            | Error e -> return Error e
            | Ok() ->
                let! keyOpt = loadKey ()

                match keyOpt with
                | None -> return Error $"{scopeLabel} key not configured for provider '{providerId}'."
                | Some apiKey ->
                    let descriptor =
                        factory.PlatformDescriptors |> List.find (fun d -> d.Id = providerId)

                    match factory.BuildPlatform(providerId, apiKey, descriptor.DefaultModel) with
                    | None -> return Error $"Provider '{providerId}' is not configured for this deployment."
                    | Some provider ->
                        let! response =
                            provider.SendMessage([ testMessage ], [], testSystemPrompt, None, RetryPolicy.noRetry)

                        match response with
                        | Ok _ ->
                            logger.Info
                                $"Platform AI keys: test-key ok scope={scopeLabel} provider={providerId} user={accessContext.UserId}"

                            return Ok()
                        | Error err ->
                            let msg = AIProviderError.toMessage err

                            logger.Warn
                                $"Platform AI keys: test-key provider error scope={scopeLabel} provider={providerId}: {msg}"

                            return Error msg
        }

    {
        ListPlatformDescriptors =
            fun () ->
                // Catalogue read — gated for symmetry with the rest of
                // the API (non-admins shouldn't be able to enumerate
                // descriptors via this path; the AISettingsApi exposes
                // the same data to authenticated users via
                // GetPlatformDescriptors).
                async {
                    if not (AccessContext.canModifyPlatformConfig accessContext) then
                        return []
                    else
                        return factory.PlatformDescriptors
                }

        ListPlatformKeyStatuses =
            fun () -> async {
                if not (AccessContext.canModifyPlatformConfig accessContext) then
                    return []
                else
                    let! rows =
                        factory.PlatformDescriptors
                        |> List.map (fun d -> async {
                            let! has = store.HasPlatformKey d.Id
                            return { ProviderId = d.Id; HasKey = has }
                        })
                        |> Async.Parallel

                    return rows |> Array.toList
            }

        SetPlatformKey =
            fun req ->
                withAdmin (fun () -> async {
                    match validateProviderId req.ProviderId with
                    | Error e -> return Error e
                    | Ok() ->
                        let trimmed = req.ApiKey.Trim()

                        if trimmed = "" then
                            return Error "API key cannot be empty."
                        else
                            let! result = store.SetPlatformKey(req.ProviderId, trimmed)

                            match result with
                            | Ok() ->
                                logger.Info
                                    $"Platform AI keys: platform key set provider={req.ProviderId} user={accessContext.UserId}"

                                return Ok()
                            | Error e ->
                                logger.Warn $"Platform AI keys: platform key set failed provider={req.ProviderId}: {e}"

                                return Error e
                })

        DeletePlatformKey =
            fun providerId ->
                withAdmin (fun () -> async {
                    let! result = store.DeletePlatformKey providerId

                    match result with
                    | Ok() ->
                        logger.Info
                            $"Platform AI keys: platform key deleted provider={providerId} user={accessContext.UserId}"

                        return Ok()
                    | Error e ->
                        logger.Warn $"Platform AI keys: platform key delete failed provider={providerId}: {e}"
                        return Error e
                })

        TestPlatformKey =
            fun providerId ->
                withAdmin (fun () -> runTest "platform" providerId (fun () -> store.GetPlatformKey providerId))

        ListTeams =
            fun () -> async {
                if not (AccessContext.canModifyPlatformConfig accessContext) then
                    return []
                else
                    match teamStoreOpt with
                    | None -> return []
                    | Some ts ->
                        let! teams = ts.ListTeams()
                        return teams |> List.map toTeamView
            }

        ListTeamKeyStatuses =
            fun teamId -> async {
                if not (AccessContext.canModifyPlatformConfig accessContext) then
                    return []
                else
                    let! rows =
                        factory.PlatformDescriptors
                        |> List.map (fun d -> async {
                            let! has = store.HasTeamKey(teamId, d.Id)

                            return {
                                TeamId = teamId
                                ProviderId = d.Id
                                HasKey = has
                            }
                        })
                        |> Async.Parallel

                    return rows |> Array.toList
            }

        SetTeamKey =
            fun req ->
                withAdmin (fun () -> async {
                    match validateProviderId req.ProviderId with
                    | Error e -> return Error e
                    | Ok() ->
                        let trimmed = req.ApiKey.Trim()

                        if trimmed = "" then
                            return Error "API key cannot be empty."
                        else
                            // Validate that the team exists when an
                            // ITeamStore is wired; in deployments without
                            // teams this validation is a no-op (the
                            // admin UI's ListTeams returns [], so the
                            // form can't reach this point anyway).
                            let! teamOk =
                                match teamStoreOpt with
                                | None -> async { return Ok() }
                                | Some ts -> async {
                                    let! info = ts.GetTeam req.TeamId

                                    return
                                        match info with
                                        | Some _ -> Ok()
                                        | None -> Error $"Team '{req.TeamId}' does not exist."
                                  }

                            match teamOk with
                            | Error e -> return Error e
                            | Ok() ->
                                let! result = store.SetTeamKey(req.TeamId, req.ProviderId, trimmed)

                                match result with
                                | Ok() ->
                                    logger.Info
                                        $"Platform AI keys: team key set team={req.TeamId} provider={req.ProviderId} user={accessContext.UserId}"

                                    return Ok()
                                | Error e ->
                                    logger.Warn
                                        $"Platform AI keys: team key set failed team={req.TeamId} provider={req.ProviderId}: {e}"

                                    return Error e
                })

        DeleteTeamKey =
            fun (teamId, providerId) ->
                withAdmin (fun () -> async {
                    let! result = store.DeleteTeamKey(teamId, providerId)

                    match result with
                    | Ok() ->
                        logger.Info
                            $"Platform AI keys: team key deleted team={teamId} provider={providerId} user={accessContext.UserId}"

                        return Ok()
                    | Error e ->
                        logger.Warn
                            $"Platform AI keys: team key delete failed team={teamId} provider={providerId}: {e}"

                        return Error e
                })

        TestTeamKey =
            fun (teamId, providerId) ->
                withAdmin (fun () ->
                    runTest $"team={teamId}" providerId (fun () -> store.GetTeamKey(teamId, providerId)))
    }