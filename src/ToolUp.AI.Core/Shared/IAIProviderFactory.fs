// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open ToolUp.Platform
open ToolUp.Platform.AI

// ─── Provider catalogue ──────────────────────────────────────────

/// Describes an AI provider available to a deployment. Enumerated at
/// compose time from the sub-companions whose `.Server.props` files were
/// imported by `ToolupApp-Server.fsproj`. Drives the settings UI
/// (Phase D) provider list and validates user-stored configuration
/// against a known set.
type AIProviderDescriptor = {
    /// Stable identifier used in user configs and as a component of
    /// secret-store key names. Must be URL-safe ASCII. Examples:
    /// "anthropic-claude", "openai-gpt4", "google-gemini-pro".
    Id: string
    /// Human-readable name for UI labels.
    DisplayName: string
    /// Known-good model identifiers for this provider. The settings UI
    /// surfaces these in a dropdown; users may also enter a custom
    /// model string for versions released after the build.
    SupportedModels: string list
    /// Default model used when the user has not selected one.
    DefaultModel: string
    /// Capabilities declared by this provider across its supported
    /// models. Per-model refinements live in the provider implementation
    /// where needed (e.g. Vision on Sonnet but not Haiku).
    Capabilities: AIProviderCapabilities
}

// ─── Fallback policy ─────────────────────────────────────────────

/// How the factory resolves a request when the user/team has no
/// configured provider. Chosen by the deployment at compose time.
type AIFallbackPolicy =
    /// Current (pre-BYOK) behaviour: ignore user/team configuration
    /// and always use the deployment-configured platform provider.
    /// Deployments that pay for AI usage on behalf of all users.
    | PlatformOnly
    /// Fall back to the deployment's platform provider when a user or
    /// team has not configured their own. Free-tier + upgrade-to-BYOK
    /// deployments.
    | PermissiveWithPlatformFallback
    /// User/team MUST have configured a provider. Missing configuration
    /// yields `ProviderResolutionError.NoProviderConfigured`. Strict
    /// BYOK deployments where the platform never pays for any user's
    /// AI usage.
    | StrictBYOK

// ─── Resolution errors ───────────────────────────────────────────

/// Classification of factory resolution failures. Distinct from
/// `AIProviderError` (runtime transport/API problems); these describe
/// configuration or identity issues detected before constructing a
/// provider instance.
type ProviderResolutionError =
    /// No user/team configuration and the deployment's fallback policy
    /// does not fall back to a platform provider.
    | NoProviderConfigured
    /// Stored configuration references a provider id absent from this
    /// deployment's `Available` list. Indicates stale configuration or
    /// tampering — the UI populates dropdowns from `Available`, so
    /// valid UI flows never produce this.
    | UnknownProvider of providerId: string
    /// Known provider but the user's/team's secret scope has no API key
    /// stored for it under the expected name.
    | MissingApiKey of providerId: string * secretKeyName: string

module ProviderResolutionError =
    /// Human-readable rendering suitable for `AITaskFailed` messages and
    /// the SSE error stream. Same style as `AIProviderError.toMessage`.
    let toMessage (err: ProviderResolutionError) =
        match err with
        | NoProviderConfigured ->
            "No AI provider configured for your account. Open AI settings to select and configure a provider."
        | UnknownProvider id ->
            $"AI provider '{id}' is not available in this deployment. Open AI settings to select a different provider."
        | MissingApiKey(id, keyName) ->
            $"API key missing for provider '{id}' (expected secret name '{keyName}'). Open AI settings to enter your key."

// ─── Provider builder ────────────────────────────────────────────

/// Binds a provider descriptor to its constructor. The app curates a
/// list of these at compose time — one entry per sub-companion
/// imported via `.Server.props`. The factory uses them to construct
/// an `IAIProvider` from a resolved API key + model after reading the
/// user's or team's stored config.
///
/// Keeping Build as a plain closure (rather than a typed interface)
/// lets providers stay free of a `ToolUp.AI` project reference — the
/// app constructs the closure by calling the provider's own
/// `createWithApiKeyAndModel` helper.
type AIProviderBuilder = {
    /// Catalogue entry surfaced through `IAIProviderFactory.Available`.
    Descriptor: AIProviderDescriptor
    /// Construct a concrete provider for a specific API key + model.
    /// Curried: `apiKey -> model -> IAIProvider`. The factory invokes
    /// this after reading the user's/team's stored instance and
    /// resolving its secret.
    Build: string -> string -> IAIProvider
}

// ─── Factory interface ───────────────────────────────────────────

/// Resolves the `IAIProvider` instance to use for a given request.
/// Replaces the single-provider `IAIProvider` argument previously taken
/// by `composeWithAI`. Implementations honour:
/// - Deployment-level fallback policy (`AIFallbackPolicy`)
/// - Per-user / per-team configuration from the canonical platform
///   `IProviderProfile` store (Phase 43.A — was `IUserAIConfigStore`
///   before the cutover) — in team mode, team config only with no
///   personal overrides
/// - The enumerated provider catalogue for UI population and config
///   validation
///
/// Callers (the agent loop) SHOULD cache the result for the duration of
/// a multi-turn loop so that a user's provider choice doesn't shift
/// mid-tool-use. The factory need not be cheap to call.
type IAIProviderFactory =
    /// Providers this deployment can construct. Enumerated from the
    /// sub-companions imported at build time. Used by the settings UI
    /// to populate dropdowns and by the factory itself to validate
    /// stored configurations against the known set.
    abstract Available: AIProviderDescriptor list

    /// Descriptors for every platform-configured provider the
    /// deployment has wired up. Phase 70: this replaces the singular
    /// `PlatformDescriptor` as the source of truth — under
    /// `PlatformOnly` a deployment may wire Anthropic + OpenAI +
    /// Gemini together and surface all three to the user. An empty
    /// list means the deployment has no platform provider at all
    /// (strict BYOK, or misconfigured `PlatformOnly`).
    abstract PlatformDescriptors: AIProviderDescriptor list

    /// Backward-compatible accessor returning the first entry from
    /// `PlatformDescriptors` (or `None` when empty). Existing callers
    /// that pre-date Phase 70's multi-provider surface keep working;
    /// new callers should prefer `PlatformDescriptors` and respect
    /// the user's `PlatformProviderOverride` (Phase 70 Stream B) when
    /// picking from the list.
    abstract PlatformDescriptor: AIProviderDescriptor option

    /// Resolve the concrete provider for a request, given the resolved
    /// per-request access context (user id, team id, mode, permissions).
    /// Returns `Ok provider` on success, `Error ProviderResolutionError`
    /// when configuration is missing or inconsistent. Runtime transport
    /// errors surface later through `IAIProvider.SendMessage` as
    /// `AIProviderError`.
    abstract Resolve: AccessContext -> Async<Result<IAIProvider, ProviderResolutionError>>

    /// Resolve a specific configured instance by its user-chosen
    /// label, without consulting the `ActiveProviderLabel`. Used by
    /// the settings UI's test-connection flow so users can verify a
    /// freshly-entered key without having to make it active first.
    ///
    /// Implementations that don't distinguish instances (the
    /// single-provider adapter) return their single provider for any
    /// label; the empty adapter returns `NoProviderConfigured`. The
    /// full factory looks the label up in the user/team's config and
    /// builds a provider bound to its stored secret.
    abstract TryResolveByLabel: AccessContext * label: string -> Async<Result<IAIProvider, ProviderResolutionError>>

    /// Phase 70 — build an `IAIProvider` for the named platform
    /// provider using the supplied API key + model. Returns `None`
    /// when `providerId` is not one of the wired platform providers.
    /// Used by the Platform Admin keys handler's `TestPlatformKey` /
    /// `TestTeamKey` paths: the handler reads the stored key from
    /// `IPlatformAIKeyStore`, then calls this to construct the
    /// provider for the ping. Bypasses the resolution chain — no
    /// fallback, no override, no BYOK consideration — by design;
    /// the caller is verifying a specific (providerId, apiKey) pair.
    abstract BuildPlatform: providerId: string * apiKey: string * model: string -> IAIProvider option