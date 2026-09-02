// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ClaudeOAuth

open System.Net.Http
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Phase 43.B — Claude one-click connect ────────────────────────
//
// The Claude reference `IProviderOAuthFlow`. The Authorization Code
// mechanics are `ProviderOAuthFlow.create`'s — identical across every
// vendor — so what lives here is only what is Claude-specific: the
// endpoint URLs, the requested scopes, and the descriptor id the
// minted `ProviderEntry` carries (`ClaudeAIProvider.ProviderId`, the
// SAME id a pasted-key entry for Claude carries, so both origins
// resolve through one catalogue lookup).
//
// **Endpoints are configurable, and that is not hedging.** Anthropic's
// OAuth surface is offered per-programme rather than as one public,
// permanently-stable pair of URLs, and a deployment connecting through
// a partner or gateway endpoint has different ones again. Baking a URL
// into a compiled companion would make the wrong one a code change;
// `defaults` names today's, and `configure` overrides them.
//
// **Registration.** The deployment wires the OAuth app's credentials
// into `ISecretStore` under `{flowName}-client-id` /
// `{flowName}-client-secret` at the connecting scope, then registers
// the flow like any other:
//
//     ServerApp.withOAuthCredentialFlow (ClaudeOAuth.create httpClient secretStore)
//
// and the substrate's
// `/api/oauth/claude-oauth/authorize?providerEntry={label}` route
// becomes the "Connect Anthropic" button's href.

/// `IOAuthCredentialFlow.Name` for this flow. Stable — it is the URL
/// segment AND the persisted secret-key prefix, so changing it strands
/// every connected entry's refresh token.
[<Literal>]
let FlowName = "claude-oauth"

/// Default configuration. Endpoints reflect Anthropic's documented
/// OAuth surface at the time of writing; `configure` overrides them for
/// a partner, gateway, or region-specific deployment without a rebuild.
let defaults: ProviderOAuthFlow.ProviderOAuthFlowConfig = {
    FlowName = FlowName
    DisplayName = "Anthropic"
    ProviderId = ClaudeAIProvider.ProviderId
    DefaultEntryLabel = "Anthropic"
    DefaultModel = None
    AuthorizeEndpoint = "https://console.anthropic.com/oauth/authorize"
    TokenEndpoint = "https://console.anthropic.com/v1/oauth/token"
    RevokeEndpoint = Some "https://console.anthropic.com/v1/oauth/revoke"
    Scopes = [ "user:inference" ]
    HelpUrl = Some "https://docs.anthropic.com/en/api/getting-started"
    // Anthropic's authorization-code flow accepts PKCE. Defence in
    // depth over the client secret, not a substitute for it.
    SupportsPkce = true
}

/// Override any field of `defaults`.
let configure (f: ProviderOAuthFlow.ProviderOAuthFlowConfig -> ProviderOAuthFlow.ProviderOAuthFlowConfig) = f defaults

/// Build the flow against a real `HttpClient`.
let create (httpClient: HttpClient) (secretStore: ISecretStore) : IProviderOAuthFlow =
    ProviderOAuthFlow.create (ProviderOAuthFlow.httpPost httpClient) secretStore defaults

/// Build the flow with a custom config and/or a stubbed token-endpoint
/// seam. This is the constructor a contract pack uses — the whole
/// round-trip is exercised with no socket, which is what makes the
/// conformance bar runnable on a fresh checkout with no Anthropic
/// account.
let createWith
    (post: ProviderOAuthFlow.OAuthTokenPost)
    (secretStore: ISecretStore)
    (config: ProviderOAuthFlow.ProviderOAuthFlowConfig)
    : IProviderOAuthFlow =
    ProviderOAuthFlow.create post secretStore config