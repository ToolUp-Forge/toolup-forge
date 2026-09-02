// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module OpenAIOAuth

open System.Net.Http
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Phase 43.B — OpenAI one-click connect ────────────────────────
//
// The OpenAI reference `IProviderOAuthFlow`, the sibling of
// `ClaudeOAuth`. Same shape, same reasoning: the Authorization Code
// mechanics belong to `ProviderOAuthFlow.create`, and only the
// endpoints, scopes and descriptor id are OpenAI's.
//
// See `ClaudeOAuth.fs` for the registration recipe and the rationale
// for configurable endpoints — it applies unchanged here.

/// `IOAuthCredentialFlow.Name` for this flow. Stable — URL segment and
/// persisted secret-key prefix.
[<Literal>]
let FlowName = "openai-oauth"

/// Default configuration. `configure` overrides any field for an
/// enterprise, gateway, or region-specific deployment.
let defaults: ProviderOAuthFlow.ProviderOAuthFlowConfig = {
    FlowName = FlowName
    DisplayName = "OpenAI"
    ProviderId = OpenAIProvider.ProviderId
    DefaultEntryLabel = "OpenAI"
    DefaultModel = None
    AuthorizeEndpoint = "https://auth.openai.com/oauth/authorize"
    TokenEndpoint = "https://auth.openai.com/oauth/token"
    RevokeEndpoint = None
    Scopes = [ "api.read"; "api.write"; "offline_access" ]
    HelpUrl = Some "https://platform.openai.com/docs/api-reference/authentication"
    SupportsPkce = true
}

/// Override any field of `defaults`.
let configure (f: ProviderOAuthFlow.ProviderOAuthFlowConfig -> ProviderOAuthFlow.ProviderOAuthFlowConfig) = f defaults

/// Build the flow against a real `HttpClient`.
let create (httpClient: HttpClient) (secretStore: ISecretStore) : IProviderOAuthFlow =
    ProviderOAuthFlow.create (ProviderOAuthFlow.httpPost httpClient) secretStore defaults

/// Build the flow with a custom config and/or a stubbed token-endpoint
/// seam — the contract-pack constructor.
let createWith
    (post: ProviderOAuthFlow.OAuthTokenPost)
    (secretStore: ISecretStore)
    (config: ProviderOAuthFlow.ProviderOAuthFlowConfig)
    : IProviderOAuthFlow =
    ProviderOAuthFlow.create post secretStore config