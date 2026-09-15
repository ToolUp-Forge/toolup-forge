// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ProviderProfileApiHandler

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.Platform.TeamManagement

// ─── IProviderProfileApi handler (Phase 44) ──────────────────────
//
// Delegates every method of the Phase 44 remoting contract to the
// registered `IProviderProfile` — the canonical store Phase 42.B
// landed and Phase 43.A cut the AI assistant over to. It adds no
// persistence of its own beyond the `ISecretStore` write a pasted key
// needs, and it interprets nothing the store interprets: `ProviderId`
// is not validated against any catalogue (the consuming factory does
// that at resolve time) and `Tags` are free-form.
//
// **Scope isolation (GP 4).** Every read and write targets the
// caller's own `AccessContext.configScope`. No method accepts a scope
// from the wire, which is why `IProviderProfileApi` carries no scope
// parameter — the same structural choice `IModuleVisibilityApi` and
// `AISettingsApi` make.
//
// **The write gate** mirrors `AISettingsHandler` exactly: in team mode
// `TeamRoles.canWriteTeamConfig` (Owner/Admin); user-scope callers own
// their own scope; Anonymous has no scope and is refused by the
// scope-`None` path.
//
// **Secret placement.** A brand-new entry's key is stored under
// `provider-key-{label}`. An EDIT reuses the existing entry's
// `SecretKeyName` verbatim rather than re-deriving it, so an entry
// created through the AI settings API (which derives `ai-key-{label}`)
// keeps working when it is later edited here — the two surfaces manage
// one store, and a re-derivation would orphan the live credential and
// silently break a working provider.

/// Secret-store key for a brand-new entry. Per-entry, not per-provider,
/// for the same reason the AI path's is: one user may hold several keys
/// for one provider.
let private newSecretKeyName (label: string) = $"provider-key-{label}"

/// Project one stored entry for the client. `HasCredential` is resolved
/// against the secret store for a pasted-key entry and against the
/// binding for an OAuth-connected one — an OAuth entry's refresh token
/// lives under a key the substrate derived, which this handler does not
/// own and must not probe by guessing.
let private toView (hasStoredKey: bool) (entry: ProviderEntry) : ProviderEntryView =
    let binding = ProviderEntry.oauthBinding entry

    {
        Label = entry.Label
        ProviderId = entry.ProviderId
        Model = entry.Model
        Tags = entry.Tags
        Origin = entry.Origin
        HasCredential =
            match entry.Origin with
            | CredentialOrigin.OAuthConnected -> binding.IsSome
            | CredentialOrigin.PastedKey -> hasStoredKey
        ConnectedAt = binding |> Option.map _.ConnectedAt
        Health = entry.Health
        UpdatedAt = entry.UpdatedAt
    }

/// Map a consumer's verification outcome onto the store's advisory
/// health record. The client reports what it OBSERVED; the timestamp
/// and the error counter are minted here, so neither can be fabricated
/// from the wire.
let private healthOf (prior: ProviderHealth) (outcome: ProviderVerificationOutcome) : ProviderHealth =
    match outcome with
    | ProviderVerificationOutcome.Verified _ -> {
        LastVerifiedAt = Some DateTime.UtcNow
        // A successful verification resets the rolling failure count,
        // matching the `ProviderHealth` doc comment's contract.
        RecentErrorCount = 0
        RateLimitHeadroom = prior.RateLimitHeadroom
        Status = ProviderHealthStatus.Healthy
      }
    | ProviderVerificationOutcome.Failed _ -> {
        prior with
            RecentErrorCount = prior.RecentErrorCount + 1
            Status = ProviderHealthStatus.Unhealthy
      }

/// Build the `IProviderProfileApi` handler. The store is passed in at
/// compose time (the composition root already holds it); `ISecretStore`,
/// `AccessContext` and `ILogger` resolve from DI per request, the same
/// pattern `AISettingsHandler` / `ModuleVisibilityApiHandler` use.
let providerProfileApi (providerProfile: IProviderProfile) (ctx: HttpContext) : IProviderProfileApi =

    let secretStore =
        ctx.RequestServices.GetService(typeof<ISecretStore>) :?> ISecretStore

    /// Observational only. Never logs a key, a label's credential, or
    /// any provider output.
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
            // the same shape AISettingsHandler and ConfigHandler use.
            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            AccessContext.unrestricted (AnonymousSession userId)

    let scopeOpt = AccessContext.configScope accessContext

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
                            $"Only team owners and admins can change the team's provider profile. Your role: {TeamRoles.displayName r}."
                | None -> return Error "You are not a member of this team."
            | _ -> return Error "Team management is not available in this deployment."
        | _ -> return Ok()
    }

    let withScope (f: StorageScope -> Async<Result<_, string>>) = async {
        match scopeOpt with
        | None ->
            return
                Error
                    "Provider configuration is not available in this mode. Sign in or join a team to configure providers."
        | Some scope -> return! f scope
    }

    let withWriteScope (f: StorageScope -> Async<Result<_, string>>) = async {
        let! rbac = ensureWriteAllowed ()

        match rbac with
        | Error msg -> return Error msg
        | Ok() -> return! withScope f
    }

    /// Read the profile for a scope, treating "never saved" as the empty
    /// profile. Every mutating method is a read-modify-write over this,
    /// which is last-writer-wins per scope — the store's own `Set`
    /// contract, not a promise added here.
    let current (scope: StorageScope) : Async<ProviderProfile> = async {
        let! existing = providerProfile.Get scope
        return existing |> Option.defaultValue (ProviderProfile.empty ())
    }

    /// Refuse a label no entry carries. Shared by `SetRoute` and
    /// `SetFallback`: a routing rule or a fallback position naming
    /// nothing is silently inert at resolve time and looks exactly like
    /// "nothing configured", which is the failure worth refusing at the
    /// point of entry rather than diagnosing later from a dead surface.
    let requireKnownLabels (profile: ProviderProfile) (labels: string list) : Result<unit, string> =
        let known = profile.Entries |> List.map _.Label

        match labels |> List.filter (fun l -> not (List.contains l known)) with
        | [] -> Ok()
        | unknown ->
            let named = String.concat ", " unknown

            let available =
                if List.isEmpty known then
                    "this profile has no entries yet"
                else
                    String.concat ", " known

            Error $"No provider entry is labelled: {named}. Configured entries: {available}."

    {
        GetProfile =
            fun () ->
                withScope (fun scope -> async {
                    let! profile = current scope

                    // The secret-store reads are independent; serialising
                    // them would add latency for no gain. Same shape as
                    // AISettingsHandler.GetMyConfig.
                    let! entries =
                        profile.Entries
                        |> List.map (fun entry -> async {
                            let! key = secretStore.GetSecret(scope.Container, entry.SecretKeyName)
                            return toView key.IsSome entry
                        })
                        |> Async.Parallel

                    return
                        Ok {
                            Entries = List.ofArray entries
                            Routing = profile.Routing
                            Fallback = profile.Fallback
                        }
                })

        SaveEntry =
            fun input ->
                withWriteScope (fun scope -> async {
                    let label = input.Label.Trim()

                    if String.IsNullOrWhiteSpace label then
                        return Error "A provider entry needs a label."
                    elif String.IsNullOrWhiteSpace input.ProviderId then
                        return Error "A provider entry needs a provider id."
                    else
                        let! profile = current scope
                        let prior = profile.Entries |> List.tryFind (fun e -> e.Label = label)

                        // Reuse the existing entry's key name; mint one
                        // only for a brand-new entry. See the header note
                        // on secret placement — re-deriving would orphan
                        // an entry created through the AI settings API.
                        let keyName =
                            prior
                            |> Option.map _.SecretKeyName
                            |> Option.defaultValue (newSecretKeyName label)

                        let entry: ProviderEntry = {
                            Label = label
                            ProviderId = input.ProviderId
                            Model = input.Model
                            SecretKeyName = keyName
                            Tags = input.Tags
                            // Origin / Health / OAuthBinding are PRESERVED,
                            // never minted here. This surface is the
                            // pasted-key path; a metadata edit on an
                            // OAuth-connected entry must not sever its
                            // refresh binding or discard a probe's health.
                            Origin = prior |> Option.map _.Origin |> Option.defaultValue CredentialOrigin.PastedKey
                            Health = prior |> Option.map _.Health |> Option.defaultValue ProviderHealth.unknown
                            OAuthBinding = prior |> Option.bind _.OAuthBinding
                            UpdatedAt = DateTime.UtcNow
                        }

                        let updated = {
                            profile with
                                Entries = (profile.Entries |> List.filter (fun e -> e.Label <> label)) @ [ entry ]
                                UpdatedAt = DateTime.UtcNow
                        }

                        // Write the key first: a profile entry that names a
                        // key the store does not hold renders as
                        // "configured but not working", whereas a stored key
                        // with no entry is inert.
                        let! keyResult =
                            match input.ApiKey with
                            | Some key when not (String.IsNullOrWhiteSpace key) ->
                                secretStore.SetSecret(scope.Container, keyName, key)
                            | Some _ -> async { return Error "The API key is blank." }
                            | None -> async { return Ok() }

                        match keyResult with
                        | Error e -> return Error $"Failed to store the API key: {e}"
                        | Ok() ->
                            let! saved = providerProfile.Set(scope, updated)

                            match saved with
                            | Ok() ->
                                logger.Info
                                    $"Provider profile: saved entry label='{label}' provider={input.ProviderId} scope={scope.Container} keyRotated={input.ApiKey.IsSome}"

                                return Ok()
                            | Error e ->
                                logger.Warn
                                    $"Provider profile: save failed for label='{label}' scope={scope.Container}: {e}"

                                return Error e
                })

        RemoveEntry =
            fun label ->
                withWriteScope (fun scope -> async {
                    let! existing = providerProfile.Get scope

                    match existing with
                    | None ->
                        // Nothing saved — removal is idempotent.
                        return Ok()
                    | Some profile ->
                        match profile.Entries |> List.tryFind (fun e -> e.Label = label) with
                        | None -> return Ok()
                        | Some entry ->
                            // Clear the credential first. A failed delete
                            // does not block the edit: an orphaned secret
                            // is inert (nothing references it), whereas a
                            // retained entry is live.
                            let! _ = secretStore.DeleteSecret(scope.Container, entry.SecretKeyName)

                            let updated = {
                                profile with
                                    Entries = profile.Entries |> List.filter (fun e -> e.Label <> label)
                                    // Drop every rule and fallback position
                                    // that named the removed entry — both
                                    // would otherwise resolve to None and
                                    // read as "no provider configured".
                                    Routing = profile.Routing |> List.filter (fun r -> r.EntryLabel <> label)
                                    Fallback = {
                                        Ordered = profile.Fallback.Ordered |> List.filter (fun l -> l <> label)
                                    }
                                    UpdatedAt = DateTime.UtcNow
                            }

                            let! saved = providerProfile.Set(scope, updated)

                            match saved with
                            | Ok() ->
                                logger.Info $"Provider profile: removed entry label='{label}' scope={scope.Container}"
                                return Ok()
                            | Error e -> return Error e
                })

        SetRoute =
            fun rule ->
                withWriteScope (fun scope -> async {
                    let! profile = current scope

                    match requireKnownLabels profile [ rule.EntryLabel ] with
                    | Error e -> return Error e
                    | Ok() ->
                        let updated = {
                            ProviderProfile.withRoute rule.Surface rule.Context rule.EntryLabel profile with
                                UpdatedAt = DateTime.UtcNow
                        }

                        return! providerProfile.Set(scope, updated)
                })

        ClearRoute =
            fun (surface, context) ->
                withWriteScope (fun scope -> async {
                    let! profile = current scope

                    let updated = {
                        profile with
                            Routing =
                                profile.Routing
                                |> List.filter (fun r -> not (r.Surface = surface && r.Context = context))
                            UpdatedAt = DateTime.UtcNow
                    }

                    return! providerProfile.Set(scope, updated)
                })

        SetFallback =
            fun ordered ->
                withWriteScope (fun scope -> async {
                    let! profile = current scope

                    match requireKnownLabels profile ordered with
                    | Error e -> return Error e
                    | Ok() ->
                        let updated = {
                            profile with
                                Fallback = { Ordered = ordered }
                                UpdatedAt = DateTime.UtcNow
                        }

                        return! providerProfile.Set(scope, updated)
                })

        GetHealth =
            fun () ->
                withScope (fun scope -> async {
                    let! profile = current scope
                    return Ok(profile.Entries |> List.map (fun e -> e.Label, e.Health))
                })

        RecordVerification =
            fun (label, outcome) ->
                withWriteScope (fun scope -> async {
                    let! profile = current scope

                    match profile.Entries |> List.tryFind (fun e -> e.Label = label) with
                    | None ->
                        // Matches `IProviderProfile.SetEntryHealth`'s own
                        // contract: a label no entry carries is a no-op Ok,
                        // not an error. A verify delegate racing a removal
                        // must not surface as a failure the user has to act
                        // on.
                        return Ok()
                    | Some entry ->
                        let health = healthOf entry.Health outcome

                        match outcome with
                        | ProviderVerificationOutcome.Failed reason ->
                            logger.Info
                                $"Provider profile: verification reported failure for label='{label}' scope={scope.Container}: {reason}"
                        | ProviderVerificationOutcome.Verified _ -> ()

                        return! providerProfile.SetEntryHealth(scope, label, health)
                })
    }