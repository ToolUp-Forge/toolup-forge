module OpenAIEmbeddingProviderHealth

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets

// ─── Phase 9k OpenAI embedding provider health probe ─────────────────
//
// Verifies that the `openai-api-key` secret resolves to a non-empty
// value in the `_platform` scope. Note the key name differs from the
// AI provider's (`OPENAI_API_KEY` for chat-completions vs
// `openai-api-key` for embeddings) — match each provider's actual
// lookup key so the probe reflects what the runtime read will see.
//
// Same trade-off as the AI provider probes: no upstream call to
// api.openai.com on every probe (token cost amortisation). Bad keys
// surface at first embed-call time via the existing error path.

type OpenAIEmbeddingProviderHealthCheck(secretStore: ISecretStore) =
    interface IHealthCheck with
        member _.Name = "embedding_provider:openai"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                let! secret = secretStore.GetSecret("_platform", "openai-api-key")

                match secret with
                | Some value when not (String.IsNullOrWhiteSpace value) -> return Healthy
                | Some _ -> return Unhealthy "openai-api-key is set but empty"
                | None -> return Unhealthy "openai-api-key not configured in secret store"
            with ex ->
                return Unhealthy ex.Message
        }

let create (secretStore: ISecretStore) : IHealthCheck =
    OpenAIEmbeddingProviderHealthCheck(secretStore) :> IHealthCheck

// ─── Phase 14u — live embedding probe ────────────────────────────────
//
// The presence probe above cannot tell a valid key from a revoked-but-
// non-empty one — both resolve to a non-empty secret. This variant
// issues one real `embeddings.create` (a single ~1-token input) so a
// 401 / 403 / model-access mismatch is caught at `/ready` instead of at
// first ingestion. Cost / risk trade-off vs the cheap probe:
//
//   * A definitive auth failure (401 / 403) or other permanent 4xx →
//     `Unhealthy` naming the status ("embeddings.create returned 401").
//   * A transient blip (429 / 5xx / timeout / network) → `Degraded`, NOT
//     `Unhealthy`: OpenAI being briefly slow must not flip `/ready` to
//     503 and have the orchestrator pull the instance (the same reason
//     the presence validator deliberately avoids a live call). `/ready`
//     runs Readiness probes and `Degraded` does not trip it.
//
// Opt-in — wire `createLive` instead of `create` when you want revoked-
// key detection at the readiness boundary and accept the per-probe token
// cost.

type OpenAIEmbeddingProviderLiveHealthCheck(secretStore: ISecretStore, ?model: string) =
    let model = defaultArg model OpenAIEmbeddingProvider.defaultModel

    interface IHealthCheck with
        member _.Name = "embedding_provider:openai (live)"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            let! result = OpenAIEmbeddingProvider.probeEmbeddingsApi secretStore model

            match result with
            | OpenAIEmbeddingProvider.ProbeOk -> return Healthy
            | OpenAIEmbeddingProvider.ProbeMissingKey ->
                return Unhealthy "openai-api-key not configured in secret store"
            | OpenAIEmbeddingProvider.ProbePermanent(status, _) ->
                return Unhealthy(sprintf "embeddings.create returned %d" status)
            | OpenAIEmbeddingProvider.ProbeTransient msg ->
                return Degraded(sprintf "embeddings.create probe could not verify the provider (transient): %s" msg)
        }

/// Build the OpenAI-embedding readiness probe that issues a real
/// `embeddings.create` (catches revoked keys / model-access mismatches;
/// `Degraded` — not `Unhealthy` — on a transient blip).
let createLive (secretStore: ISecretStore) : IHealthCheck =
    OpenAIEmbeddingProviderLiveHealthCheck(secretStore) :> IHealthCheck