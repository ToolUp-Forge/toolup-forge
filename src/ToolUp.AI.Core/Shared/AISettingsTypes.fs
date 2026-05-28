// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open System

// ─── Client-facing view types ────────────────────────────────────

/// Client-safe projection of `AIProviderInstance`. Omits the API key
/// — the server never returns stored secrets to the client.
/// `HasApiKey` lets the UI render "configured" vs "needs key" state
/// without leaking the value.
type AIProviderInstanceView = {
    Label: string
    ProviderId: string
    Model: string option
    /// True when the secret store has a key under this instance's
    /// `SecretKeyName`. Checked at read time; does not imply the key
    /// is valid — `TestConnection` validates against the provider.
    HasApiKey: bool
    UpdatedAt: DateTime
}

/// Client-safe view of the current scope's AI configuration.
type AIUserConfigView = {
    ConfiguredProviders: AIProviderInstanceView list
    ActiveProviderLabel: string option
    /// User's preferred model for the platform-configured provider
    /// under `PlatformOnly`. `None` → use the platform descriptor's
    /// `DefaultModel`. Surfaced alongside `ConfiguredProviders` so a
    /// single `GetMyConfig` round-trip covers both BYOK instances and
    /// the platform-model preference.
    PlatformModelOverride: string option
}

module AIUserConfigView =
    let empty = {
        ConfiguredProviders = []
        ActiveProviderLabel = None
        PlatformModelOverride = None
    }

// ─── Save request ────────────────────────────────────────────────

/// Request body for `SaveInstance`. When `ApiKey` is `Some`, the
/// provided value is stored (or overwrites an existing one). When
/// `None`, any already-stored key for this label is preserved —
/// allowing the UI to edit metadata (model, provider) without
/// forcing the user to re-enter their key.
type AIProviderInstanceSaveRequest = {
    Label: string
    ProviderId: string
    Model: string option
    ApiKey: string option
}

// ─── API contract ────────────────────────────────────────────────

/// Fable.Remoting API for per-user / per-team AI settings. Used by
/// the settings UI (Phase D) to manage configured providers and
/// associated API keys.
///
/// Scope semantics follow the BYOK plan (decision 2): in `Team` mode
/// this API targets the team scope exclusively — no personal
/// overrides. In `Individual` / `AuthenticatedEphemeral` modes it
/// targets the user scope. In `Anonymous` mode it returns empty /
/// rejects writes.
type AISettingsApi = {
    /// Providers the caller can configure. Returns the factory's
    /// `Available` list — empty when the deployment is `PlatformOnly`
    /// (user configuration is disabled deployment-wide).
    ListAvailable: unit -> Async<AIProviderDescriptor list>
    /// Current scope's configuration, as a client-safe view. Returns
    /// an empty view when no config has been saved or when the mode
    /// does not support persistent config.
    GetMyConfig: unit -> Async<AIUserConfigView>
    /// Create a new instance or replace an existing one keyed by
    /// `Label`. The request's `ApiKey` is written to the secret
    /// store at a per-instance key name; omit it to keep the existing
    /// stored key when editing metadata only.
    ///
    /// Validation: `ProviderId` must match a descriptor from
    /// `ListAvailable`. Unknown providers are rejected to prevent
    /// orphaned configurations.
    SaveInstance: AIProviderInstanceSaveRequest -> Async<Result<unit, string>>
    /// Remove an instance and its stored API key by label. Idempotent
    /// — deleting a non-existent label succeeds silently. When the
    /// deleted label was `ActiveProviderLabel`, active is cleared so
    /// the factory falls back per policy on the next request.
    DeleteInstance: string -> Async<Result<unit, string>>
    /// Set (or clear) the active instance for chat. Passing `None`
    /// clears the active label — the factory then falls back per
    /// policy. Passing `Some l` requires `l` to exist in
    /// `ConfiguredProviders`; unknown labels are rejected.
    SetActive: string option -> Async<Result<unit, string>>
    /// Verify a configured instance's API key by sending a minimal
    /// prompt to its provider. Returns `Ok ()` on any successful
    /// response (the model's content doesn't matter — authentication
    /// + reachability is what's being validated). `Error` carries a
    /// user-facing message from either `ProviderResolutionError` or
    /// `AIProviderError`. Used by the settings UI before the user
    /// activates the instance.
    TestConnection: string -> Async<Result<unit, string>>
    /// Descriptor for the deployment's platform-configured provider,
    /// when one exists. Returns `None` if the factory has no platform
    /// provider (StrictBYOK, or PlatformOnly misconfigured).
    /// Used by the settings UI under `PlatformOnly` to surface a
    /// minimal model-picker from `SupportedModels` while provider
    /// identity and API-key source stay locked.
    GetPlatformDescriptor: unit -> Async<AIProviderDescriptor option>
    /// Set (or clear) the caller's preferred model for the platform
    /// provider. Persisted at the caller's config scope. `None`
    /// clears the override — subsequent resolution uses the platform
    /// descriptor's `DefaultModel`. In `Anonymous` mode the request
    /// is rejected (no persistent config scope).
    SetPlatformModelOverride: string option -> Async<Result<unit, string>>
}