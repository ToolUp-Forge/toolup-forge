module LocalEmbeddingProviderHealth

open System
open ToolUp.Platform.HealthChecks

// ─── Phase 9k Local embedding provider health probe ──────────────────
//
// `LocalEmbeddingProvider` is fully in-process (TF-IDF over an evolving
// vocabulary, no external network or credentials). The probe is
// vacuously Healthy — if the process is up, the provider is reachable.
// We register a probe anyway so `/dev/inspect` shows the provider in
// the Health Checks panel and operators can confirm the local
// embedding path is wired into the deployment.

type LocalEmbeddingProviderHealthCheck() =
    interface IHealthCheck with
        member _.Name = "embedding_provider:local"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 1.0
        member _.Check() = async { return Healthy }

let create () : IHealthCheck =
    LocalEmbeddingProviderHealthCheck() :> IHealthCheck