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

// ─── Phase 9m.B — production-shape awareness (2026-05-06 audit, Gap 4)
//
// "Reachable" was never the interesting question about this provider.
// The interesting question is whether it should be serving THIS
// deployment at all: the local embedder is dev-only and process-
// stateful (its IDF dictionary evolves with the corpus in this
// process), so on any production-shaped surface — Individual,
// AuthenticatedEphemeral, Team, MultiTeam — embeddings are neither
// reproducible across restarts nor comparable across replicas, and
// retrieval quality drifts.
//
// `RagConfigValidator` says so at preflight; this probe says the same
// thing on the surface an operator reaches for FIRST when retrieval
// looks wrong. A probe that reported Healthy there would actively
// mislead — it would confirm the component an operator is right to
// suspect.
//
// `Degraded`, not `Unhealthy`, and the distinction is load-bearing:
// `Degraded` does NOT trip `/ready` to 503 (see `HealthResult` in
// `IHealthCheck.fs`), so a deployment that has deliberately accepted
// the local embedder keeps serving traffic. The probe informs; it does
// not take the deployment down.

/// `productionShaped` is the deployment-shape verdict, passed in rather
/// than read — a companion never reads `ServerConfig` or the
/// environment directly (see the companion-authoring rules). Compute it
/// at the composition root as:
///
/// ```fsharp
/// DeploymentConfig.isProductionShapedForStatefulEmbedder config
/// && not config.AcceptLocalEmbedderAtScale
/// ```
///
/// so an operator who has opted past the preflight warning
/// (`TOOLUP_ACCEPT_LOCAL_EMBEDDER_AT_SCALE=1`) silences the probe with
/// the same flag. A probe that stayed `Degraded` after an explicit,
/// named opt-in is just a permanent yellow light nobody reads.
type LocalEmbeddingProviderHealthCheck(productionShaped: bool) =
    /// The pre-9m.B parameterless constructor, preserved rather than
    /// replaced. Dropping it would be a BREAKING public-surface change
    /// under the SemVer-on-0.x policy (GP 11) for a companion whose
    /// behaviour has not changed for any existing caller — the added
    /// overload is the whole feature. Keeping it also leaves the
    /// `api-baselines` entry for this assembly purely additive.
    new() = LocalEmbeddingProviderHealthCheck(false)

    interface IHealthCheck with
        member _.Name = "embedding_provider:local"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 1.0

        member _.Check() = async {
            if productionShaped then
                return
                    Degraded(
                        "LocalEmbeddingProvider is serving a production-shaped deployment. The local TF-IDF embedder is dev-only and process-stateful: its IDF dictionary evolves with the corpus in this process, so the same document re-ingested later embeds differently and a second replica embeds it differently again — retrieval quality drifts over time and diverges across instances. The provider is reachable and answering; this is a suitability signal, not an outage (Degraded does not trip /ready). Wire a stateless embedder (OpenAI / Cohere / Anthropic embeddings) into RAGServerApp.create, or set ServerConfig.AcceptLocalEmbedderAtScale = true (TOOLUP_ACCEPT_LOCAL_EMBEDDER_AT_SCALE=1) to accept the trade-off."
                    )
            else
                return Healthy
        }

/// Back-compatible constructor — reports `Healthy` unconditionally,
/// exactly as before Phase 9m.B (GP 11: an existing consumer that
/// upgrades is byte-for-byte unchanged until it opts in). New consumers
/// should prefer `createForDeployment`, which lets the probe answer the
/// question operators actually ask of it.
let create () : IHealthCheck =
    LocalEmbeddingProviderHealthCheck(false) :> IHealthCheck

/// Phase 9m.B — the deployment-aware probe. Reports `Degraded` (never
/// `Unhealthy`) when the local embedder is serving a production-shaped
/// deployment. See the `LocalEmbeddingProviderHealthCheck` doc comment
/// for how to compute `productionShaped`.
let createForDeployment (productionShaped: bool) : IHealthCheck =
    LocalEmbeddingProviderHealthCheck(productionShaped) :> IHealthCheck