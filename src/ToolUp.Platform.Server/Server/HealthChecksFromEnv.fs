// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.HealthCheckDefaults

open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets

/// Probe-builder bundle for the reference-app posture (Claude AI
/// provider, OpenAI AI provider, local TF-IDF embedding provider).
/// Consumer fills in from companion modules so
/// `ToolUp.Platform.Server` doesn't reference them directly.
///
/// Typical wiring:
///   { ClaudeProbe = ClaudeAIProviderHealth.create
///     OpenAIProbe = OpenAIProviderHealth.create
///     LocalEmbeddingProbe = fun () -> LocalEmbeddingProviderHealth.create () }
///
/// Consumers that don't ship AI / RAG don't use this helper — they
/// construct their probe list directly.
type ReferenceProbeBuilders = {
    ClaudeProbe: ISecretStore -> IHealthCheck
    OpenAIProbe: ISecretStore -> IHealthCheck
    LocalEmbeddingProbe: unit -> IHealthCheck
}

/// Build the reference-app probe list — Claude provider probe +
/// OpenAI provider probe + LocalEmbedding probe + an optional
/// notification-channel probe surfaced by
/// `NotificationChannel.fromEnv`. Each consumer threads the
/// resulting list into the composition pipeline via the SDK's
/// per-probe `withHealthCheck` calls or in one shot via a future
/// `withHealthChecks`.
///
/// Order matches the hand-written reference composition root: SSE /
/// notification first (when active), then AI providers, then
/// embedding.
let fromEnv
    (builders: ReferenceProbeBuilders)
    (secretStore: ISecretStore)
    (notificationProbe: IHealthCheck option)
    : IHealthCheck list =
    [
        yield! notificationProbe |> Option.toList
        builders.ClaudeProbe secretStore
        builders.OpenAIProbe secretStore
        builders.LocalEmbeddingProbe()
    ]