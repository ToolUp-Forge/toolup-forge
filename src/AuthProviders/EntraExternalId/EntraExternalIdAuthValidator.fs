// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.EntraExternalIdAuthValidator

open System
open System.Net.Http
open System.Text.Json
open ToolUp.Platform.ConfigValidation
open ToolUp.AuthProviders.EntraExternalIdConfig

// ─── Entra External ID config preflight ──────────────────────────────
//
// Probes the constructed issuer's
// `{issuer}/.well-known/openid-configuration` discovery endpoint once
// at startup. Mirrors the shape of `OidcAuthValidator` exactly (Phase
// 9m pattern), specialised for the External ID issuer-URL construction
// rule (tenant id + optional custom domain -> v2.0 path).
//
// Companion auto-registration: a deployment that sets
// `TOOLUP_ENTRA_EXTERNAL_ID_TENANT` calls `tryFromEnv` at composition
// time. When the env var is unset, `tryFromEnv` returns `None` and no
// validator is registered (lightweight default; deployments without
// External ID pay nothing).

let private httpClient = lazy (new HttpClient())

let private discoveryPath = "/.well-known/openid-configuration"

let private discoveryUrl (issuer: string) : string = issuer.TrimEnd('/') + discoveryPath

let private hasField (json: string) (field: string) : bool =
    use doc = JsonDocument.Parse json
    doc.RootElement.TryGetProperty field |> fst

type private Impl(tenant: string, customDomain: string option, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout
    let issuer = EntraExternalIdConfig.issuerUrl tenant customDomain
    let url = discoveryUrl issuer

    interface IConfigValidator with
        member _.Name = sprintf "entra-external-id-auth (%s)" issuer
        member _.Timeout = timeout

        member _.Validate() = async {
            try
                let! response = httpClient.Value.GetAsync url |> Async.AwaitTask

                if not response.IsSuccessStatusCode then
                    return Error(sprintf "discovery endpoint at %s returned HTTP %d" url (int response.StatusCode))
                else
                    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                    if hasField body "authorization_endpoint" && hasField body "token_endpoint" then
                        return Ok
                    else
                        return
                            Error(sprintf "discovery doc at %s is missing authorization_endpoint or token_endpoint" url)
            with ex ->
                return Error(sprintf "discovery endpoint at %s unreachable: %s" url ex.Message)
        }

/// Construct a validator for an explicit Entra External ID tenant +
/// optional custom domain. Apps with per-deployment custom config (a
/// YAML file, KMS secrets) build the inputs themselves and call this
/// directly.
let create (tenant: string) (customDomain: string option) : IConfigValidator =
    Impl(tenant, customDomain) :> IConfigValidator

/// Validator that always returns `Error` with a fixed message. Used
/// when env-var presence is partially correct (TENANT set without
/// AUDIENCE, or AUDIENCE set without TENANT) — the deployment looks
/// like it intends to wire Entra External ID but the provider's own
/// `fromEnv` would silently return `None` (provider not wired) while
/// the discovery probe would succeed (auth-preflight ok). The mismatch
/// becomes a cryptic startup-success-then-runtime-failure unless the
/// validator surfaces the gap explicitly at boot.
type private MisconfiguredImpl(message: string) =
    interface IConfigValidator with
        member _.Name = "entra-external-id-auth (misconfigured)"
        member _.Timeout = IConfigValidator.defaultTimeout
        member _.Validate() = async { return Error message }

/// Read tenant + optional custom domain from environment variables and
/// return a validator if the tenant + audience are both set; an
/// `Error`-returning validator when exactly one of the required pair
/// is set; `None` when neither is set. Mirrors the `OidcAuthValidator`
/// shape but tightens the contract so the validator's outcome matches
/// `EntraExternalIdConfig.fromEnv`'s wiring decision: both vars set →
/// probe the discovery endpoint; one var set → refuse with an
/// actionable message; neither set → silently skip (no Entra intent).
let tryFromEnv () : IConfigValidator option =
    let readEnv name =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" -> None
        | value -> Some value

    let tenant = readEnv ToolUp.Platform.ConfigKeys.Names.entraExternalIdTenant
    let audience = readEnv ToolUp.Platform.ConfigKeys.Names.entraExternalIdAudience

    match tenant, audience with
    | None, None ->
        // No Entra intent — silently skip.
        None
    | Some _, None ->
        Some(
            MisconfiguredImpl(
                "TOOLUP_ENTRA_EXTERNAL_ID_TENANT is set but TOOLUP_ENTRA_EXTERNAL_ID_AUDIENCE is unset. \
                 Set the app-registration client id (audience) to complete the Entra External ID setup, \
                 or unset TENANT to skip Entra entirely. EntraExternalIdConfig.fromEnv requires both \
                 to wire the auth provider, so the current state would silently boot without Entra auth."
            )
            :> IConfigValidator
        )
    | None, Some _ ->
        Some(
            MisconfiguredImpl(
                "TOOLUP_ENTRA_EXTERNAL_ID_AUDIENCE is set but TOOLUP_ENTRA_EXTERNAL_ID_TENANT is unset. \
                 Set the External ID tenant identifier to complete the setup, or unset AUDIENCE to skip \
                 Entra entirely."
            )
            :> IConfigValidator
        )
    | Some tenant, Some _ ->
        let customDomain =
            readEnv ToolUp.Platform.ConfigKeys.Names.entraExternalIdCustomDomain

        Some(create tenant customDomain)