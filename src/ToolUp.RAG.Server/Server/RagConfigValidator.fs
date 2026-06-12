module ToolUp.RAG.RagConfigValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.IEmbeddingProvider

// ─── RAG-specific config validators (Phase 4b commit 3) ─────────────
//
// Companion validators for RAG-specific config concerns. Auto-registered
// by `composeWithRAG` against the resolved `ServerConfig` + active
// `IEmbeddingProvider`. Mirrors the first-party validator pattern in
// `ToolUp.Platform.ConfigValidator` (BlobStorageValidator,
// SecretStoreValidator).

/// Surfaces the embedding-quality variance leak that occurs when the
/// dev-only `LocalEmbeddingProvider` is in use under `Team` /
/// `MultiTeam` mode. Chunks themselves are scope-isolated by the vector
/// store (`RetrievalPipeline.authorisedScopes` filters by `Team teamId`
/// per request), but the `LocalEmbeddingProvider`'s IDF dictionary is
/// shared across all teams in the same process. Term-frequency variance
/// reveals to one team which terms have been indexed by other teams,
/// even though the chunks themselves never leak. CLAUDE.md flags this
/// in `LocalEmbeddingProvider`'s file header — production deployments
/// must swap to a stateless embedder (OpenAI / Cohere / Anthropic
/// embeddings) where each call is independent of corpus state.
///
/// Returns `Warning` (not `Error`) so dev / single-tenant staging
/// deployments using the local embedder continue to start cleanly;
/// operators get a clear preflight signal in the startup log + the
/// `/dev/inspect` validators panel pointing at the actionable
/// remediation. Phase 14z structurally closes the leak via per-team
/// IDF dictionaries, at which point this validator can be removed.
type TeamModeLocalEmbedderValidator(serverConfig: ServerConfig, embedder: IEmbeddingProvider, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "team-mode-local-embedder"
        member _.Timeout = timeout

        member _.Validate() = async {
            let isTeamMode = DeploymentConfig.hasTeamScope serverConfig

            if isTeamMode && embedder.ProviderId = "local" then
                return
                    Warning(
                        "LocalEmbeddingProvider is active in Team / MultiTeam mode. "
                        + "The IDF dictionary is shared across all teams in this process, which lets "
                        + "term-frequency variance leak embedding-quality information across team boundaries "
                        + "(chunks themselves remain scope-isolated and never leak). This is acceptable for "
                        + "single-user dev / staging but should not run in multi-tenant production. "
                        + "Wire in a stateless embedder (OpenAI / Cohere / Anthropic embeddings) via "
                        + "RAGServerApp.create. Phase 14z addresses this structurally via per-team IDF."
                    )
            else
                return Ok
        }

/// Refuses (or, for ephemeral modes, warns about) a RAG deployment
/// that has neither `IBlobStorage` nor an `IVectorStore` override —
/// the in-memory store over the null blob store accepts every
/// `Upsert`, "persists" to a sink that discards the bytes, and starts
/// EMPTY on the next process restart. Previously this was a single
/// startup `Warn` line, so an operator saw a working RAG that silently
/// lost its entire corpus on every container restart. Persistent modes
/// (Individual / Team / MultiTeam) treat this as a fatal
/// misconfiguration; ephemeral modes (Anonymous / AuthenticatedEphemeral)
/// are non-persistent by design, so a Warning is sufficient there.
type RagPersistenceValidator
    (serverConfig: ServerConfig, blobStorageSupplied: bool, vectorStoreSupplied: bool, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "rag-persistence"
        member _.Timeout = timeout

        member _.Validate() = async {
            let durable = blobStorageSupplied || vectorStoreSupplied

            if durable then
                return Ok
            else
                let msg =
                    "RAG is composed with neither an IBlobStorage nor an IVectorStore override. The in-memory vector store writes through a null blob store that DISCARDS bytes — every ingested chunk is lost on the next process restart with no further signal. Pass `Some storage` to composeWithRAG (or a durable IVectorStore companion, e.g. src/VectorStores/Hnsw) for a deployment whose corpus survives a restart."

                if DeploymentConfig.hasPersistentAuthenticatedStorage serverConfig then
                    return Error msg
                else
                    // Ephemeral / public-only by design — non-durable
                    // RAG is consistent with the surface shape; still
                    // surface it so it isn't silent.
                    return Warning(msg + " (Surfaces are ephemeral, so this may be intentional.)")
        }

/// Surfaces the embedding-cache replica-divergence concern when the
/// default `InMemoryEmbeddingCache` is paired with `Team` / `MultiTeam`
/// mode. `EmbeddingCacheKey` (in `ToolUp.Platform.IEmbeddingCache`) has
/// the shape `{ Version; TextHash }` — no tenant component — so two
/// teams indexing identical document text share cache entries. That's
/// fine for correctness (embeddings are deterministic), but in a
/// multi-instance deployment each replica's cache is independent, so
/// the same query against the same text may hit cache on replica A and
/// miss on replica B (causing different latency, different metering
/// attribution, and different short-window telemetry).
///
/// Returns `Warning` (not `Error`) — single-instance multi-team
/// deployments (the common single-node shape) are fine. Operators on
/// multi-instance can either accept best-effort cache hit-rate
/// (`ServerConfig.AcceptSharedEmbeddingCacheInTeamMode = true`) or
/// wire a tenant-aware `IEmbeddingCache` override at the composition
/// root once that extension point exists.
type TeamModeSharedEmbeddingCacheValidator(serverConfig: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "team-mode-shared-embedding-cache"
        member _.Timeout = timeout

        member _.Validate() = async {
            let isTeamMode = DeploymentConfig.hasTeamScope serverConfig
            let multiInstance = serverConfig.ReplicaCount > 1
            let accepted = serverConfig.AcceptSharedEmbeddingCacheInTeamMode

            if isTeamMode && multiInstance && not accepted then
                return
                    Warning(
                        "Default InMemoryEmbeddingCache is active in Team / MultiTeam mode with ReplicaCount > 1. "
                        + "The cache key (EmbeddingCacheKey in ToolUp.Platform.IEmbeddingCache) carries no tenant "
                        + "component, so each replica maintains an independent cache for the same text — retrieval "
                        + "and per-call latency become non-deterministic across replicas. Accept this and silence the "
                        + "warning via ServerConfig.AcceptSharedEmbeddingCacheInTeamMode = true "
                        + "(TOOLUP_ACCEPT_SHARED_EMBEDDING_CACHE_IN_TEAM_MODE=1), or wire a tenant-aware IEmbeddingCache "
                        + "override at composeWithRAG once that extension point exists."
                    )
            else
                return Ok
        }

/// Surfaces the `original → clamped` history of
/// `RAGServerApp.withTopK` / `withMinScore` / `withSnippetCharLimit` /
/// `withMmrLambda` / `withRetrievalDefaults` calls that landed an
/// out-of-range value. The targeted setters clamp to a sane window so
/// retrieval doesn't silently disable itself (TopK = 0 would return
/// nothing; MinScore = 1.0 would filter every match), but the silent
/// clamp also hides operator intent — an operator who typoed
/// `withMinScore (Some 1.5)` and meant `0.15` never learned why
/// retrieval looked broken.
///
/// Validator surfaces every clamp as a single `Warning` at startup so
/// the operator can see what the SDK changed and re-issue the call
/// with the intended value. Empty clamp log = `Ok` (the common case).
type RetrievalDefaultsValidator(clampLog: string list, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "rag-retrieval-defaults-clamp"
        member _.Timeout = timeout

        member _.Validate() = async {
            if clampLog.IsEmpty then
                return Ok
            else
                // clampLog is built head-prepend, so most recent first.
                // Reverse for chronological order in the message.
                let chronological = clampLog |> List.rev
                let joined = String.concat "; " chronological

                return
                    Warning(
                        sprintf
                            "RAG retrieval-defaults setter clamped %d operator-supplied value(s) to in-range bounds: %s. The clamp prevents silently-disabled retrieval (TopK=0 returns nothing; MinScore=1.0 filters everything) but hides operator intent — if any of these were typos, re-issue the call with the intended value."
                            (List.length clampLog)
                            joined
                    )
        }

/// Surfaces operator-supplied `ChunkingConfig` values that fail
/// `ChunkingConfig.validate` — typically a typo that produces
/// pathological chunks (e.g. `MinTokens > MaxTokens` drops every
/// chunk by the size floor; `OverlapTokens >= MaxTokens` blocks
/// forward progress). The validator is intended to be wired by
/// `VectorisationHandler` authors who expose a configurable
/// `ChunkingConfig`; consumers using `ChunkingConfig.defaults` /
/// `tabular` are safe by construction and don't need this.
///
/// `Error` (not `Warning`) — an invalid chunking config either
/// produces empty output or never terminates progress; there is no
/// graceful fallback.
type ChunkingConfigValidator(configName: string, config: ToolUp.RAG.Chunking.ChunkingConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = sprintf "chunking-config-%s" configName
        member _.Timeout = timeout

        member _.Validate() = async {
            match ToolUp.RAG.Chunking.ChunkingConfig.validate config with
            | Result.Ok _ -> return ValidationResult.Ok
            | Result.Error msgs ->
                return
                    ValidationResult.Error(
                        sprintf "ChunkingConfig '%s' is invalid: %s" configName (String.concat " | " msgs)
                    )
        }

/// Investigate gaps 2026-06-12 (RAG Gap 8) — flags the now-inert
/// `EnableCitationDevEndpoint = Some true` paired with
/// `EnableDevEndpoints = false`. Phase 14s shipped that combination as
/// a force-on arm ("register /dev/rag-citation even when the master is
/// off"); the 2026-06-12 audit reversed it because the force-on broke
/// the "master off ⇒ no dev surface" audit invariant for the most
/// privacy-sensitive dev endpoint (conversation-derived rewrite
/// samples, no per-endpoint auth). `composeWithRAG` now resolves
/// `Some v` as `v && EnableDevEndpoints` — the override can suppress
/// but never force-on — so a config still carrying `Some true` under a
/// disabled master is dead configuration. `Warning`, not `Error`: the
/// resolved behaviour is the safe one; the operator just needs to know
/// their override no longer does what Phase 14s documented.
type CitationDevEndpointValidator(serverConfig: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "rag-citation-dev-endpoint"
        member _.Timeout = timeout

        member _.Validate() = async {
            match serverConfig.EnableCitationDevEndpoint with
            | Some true when not serverConfig.EnableDevEndpoints ->
                return
                    Warning(
                        "EnableCitationDevEndpoint = Some true has no effect while EnableDevEndpoints = false — "
                        + "the per-endpoint override can suppress /dev/rag-citation but no longer force-registers it "
                        + "(the former force-on arm exposed conversation-derived rewrite samples on an unauthenticated "
                        + "endpoint while every other dev surface was off). Set EnableDevEndpoints = true to get the "
                        + "endpoint, or drop the override to silence this warning."
                    )
            | _ -> return Ok
        }

/// Phase 9j follow-up — refuses an in-process RAG ingestion queue
/// paired with `ReplicaCount > 1`. `composeWithRAG` builds a
/// process-local `IngestionQueue` + `IngestionBackgroundService` per
/// instance with no leasing/redelivery: only the replica that handled
/// an upload can drain its job, and a crash between dequeue and
/// completion loses that job. In a multi-instance deployment that is
/// silent corpus incompleteness. Single-instance deployments are
/// unaffected; multi-instance deployments either accept best-effort
/// per-instance ingestion via the explicit escape hatch (a distributed
/// ingestion path is a roadmap item) or stay single-instance. Mirrors
/// `JobSchedulerInstanceValidator` (Phase 6l.F).
type RagIngestionInstanceValidator(serverConfig: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "rag-ingestion-instance"
        member _.Timeout = timeout

        member _.Validate() = async {
            let multiInstance = serverConfig.ReplicaCount > 1
            let escapeHatch = serverConfig.AcceptInProcessIngestionInMultiInstance

            if multiInstance && not escapeHatch then
                return
                    Error(
                        sprintf
                            "RAG is composed with the in-process ingestion queue and ReplicaCount = %d. The queue is a process-local channel with no leasing or redelivery: only the replica that handled an upload can drain its ingestion job, and a crash/redeploy between dequeue and completion loses that document silently (the corpus becomes quietly incomplete). Keep RAG single-instance, or set ServerConfig.AcceptInProcessIngestionInMultiInstance = true (TOOLUP_ACCEPT_INPROCESS_INGESTION_MULTI_INSTANCE=1) if you accept best-effort per-instance ingestion. A distributed ingestion path is a roadmap item. Verify in the HealthMonitorUI admin tab or /dev/inspect Validators panel."
                            serverConfig.ReplicaCount
                    )
            else
                return Ok
        }