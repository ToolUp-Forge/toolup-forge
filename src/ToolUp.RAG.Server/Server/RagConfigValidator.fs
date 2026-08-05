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
///
/// **Phase 9m.B** — honours `ServerConfig.AcceptLocalEmbedderAtScale`
/// (`TOOLUP_ACCEPT_LOCAL_EMBEDDER_AT_SCALE=1`), the escape hatch shared
/// with `LocalEmbeddingProviderInProductionModeValidator`. The two
/// validators cover disjoint deployment shapes but name the SAME
/// remedy (swap to a stateless embedder), so an operator who has
/// accepted the trade-off silences the family with one flag rather
/// than learning two.
type TeamModeLocalEmbedderValidator(serverConfig: ServerConfig, embedder: IEmbeddingProvider, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "team-mode-local-embedder"
        member _.Timeout = timeout

        member _.Validate() = async {
            let isTeamMode = DeploymentConfig.hasTeamScope serverConfig

            if
                isTeamMode
                && embedder.ProviderId = "local"
                && not serverConfig.AcceptLocalEmbedderAtScale
            then
                return
                    Warning(
                        "LocalEmbeddingProvider is active in Team / MultiTeam mode. "
                        + "The IDF dictionary is shared across all teams in this process, which lets "
                        + "term-frequency variance leak embedding-quality information across team boundaries "
                        + "(chunks themselves remain scope-isolated and never leak). This is acceptable for "
                        + "single-user dev / staging but should not run in multi-tenant production. "
                        + "Wire in a stateless embedder (OpenAI / Cohere / Anthropic embeddings) via "
                        + "RAGServerApp.create, or set ServerConfig.AcceptLocalEmbedderAtScale = true "
                        + "(TOOLUP_ACCEPT_LOCAL_EMBEDDER_AT_SCALE=1) to accept the trade-off explicitly. "
                        + "Phase 14z addresses this structurally via per-team IDF."
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
///
/// **Phase 9m.B (2026-05-06 audit, Gap 3) — the escape hatch.** The
/// refusal had no opt-out, so a deployment that *deliberately* runs an
/// ephemeral index on a persistent surface — a build-time-seeded
/// corpus re-ingested on boot, a sandbox, an integration-test harness —
/// could not start at all. `ServerConfig.AcceptEphemeralRagIndex`
/// (`TOOLUP_ACCEPT_EPHEMERAL_RAG_INDEX=1`) degrades the refusal to a
/// `Warning`, which is the GP 13 shape used across the `Accept*`
/// family: default refuses, the operator opts past it by name, and the
/// choice stays visible in the `/dev/inspect` Validators panel instead
/// of going silent. `RagDurabilityContributor` surfaces the same fact
/// as a "RAG durability: ephemeral" line in the same panel set.
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
                    if serverConfig.AcceptEphemeralRagIndex then
                        // Phase 9m.B — the operator has opted past the
                        // refusal by name. Keep it visible (never silent)
                        // so `/dev/inspect` still shows the deployment is
                        // running an index that does not survive a restart.
                        return
                            Warning(
                                msg
                                + " ServerConfig.AcceptEphemeralRagIndex = true (TOOLUP_ACCEPT_EPHEMERAL_RAG_INDEX=1) — RAG durability is EPHEMERAL by explicit operator opt-in: the corpus must be re-ingested after every restart."
                            )
                    else
                        return
                            Error(
                                msg
                                + " If the corpus is deliberately re-ingested on every boot (build-time-seeded index, sandbox, or test harness), set ServerConfig.AcceptEphemeralRagIndex = true (TOOLUP_ACCEPT_EPHEMERAL_RAG_INDEX=1) to start with a Warning instead."
                            )
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
/// deployments (the common single-node shape) are fine.
///
/// Phase 633 — `crossReplicaCache` is the composed-cache observation the
/// validator could not previously make: built from `ServerConfig` alone it
/// could only see that a deployment MIGHT diverge, never that the operator
/// had already addressed it, so it warned at a deployment doing the right
/// thing. `RAGServerApp.withEmbeddingCache` composing a cross-replica cache
/// (the shipped backing is `ToolUp.EmbeddingCaches.Redis`) removes the
/// divergence outright, so the warning is **lifted** — the premise it rests
/// on no longer holds. It is not silenced: an operator who composed nothing,
/// or composed the process-local `InMemoryEmbeddingCache` explicitly, still
/// sees it, and the remaining escape hatch is still
/// `ServerConfig.AcceptSharedEmbeddingCacheInTeamMode = true`.
type TeamModeSharedEmbeddingCacheValidator(serverConfig: ServerConfig, crossReplicaCache: bool, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "team-mode-shared-embedding-cache"
        member _.Timeout = timeout

        member _.Validate() = async {
            let isTeamMode = DeploymentConfig.hasTeamScope serverConfig
            let multiInstance = serverConfig.ReplicaCount > 1
            let accepted = serverConfig.AcceptSharedEmbeddingCacheInTeamMode

            if isTeamMode && multiInstance && not accepted && not crossReplicaCache then
                return
                    Warning(
                        "Process-local InMemoryEmbeddingCache is active in Team / MultiTeam mode with ReplicaCount > 1. "
                        + "The cache key (EmbeddingCacheKey in ToolUp.Platform.IEmbeddingCache) carries no tenant "
                        + "component, so each replica maintains an independent cache for the same text — retrieval "
                        + "and per-call latency become non-deterministic across replicas. Compose a cross-replica "
                        + "cache (RAGServerApp.withEmbeddingCache — e.g. the ToolUp.EmbeddingCaches.Redis companion), "
                        + "which removes the divergence and lifts this warning; or accept best-effort per-replica "
                        + "hit-rate via ServerConfig.AcceptSharedEmbeddingCacheInTeamMode = true "
                        + "(TOOLUP_ACCEPT_SHARED_EMBEDDING_CACHE_IN_TEAM_MODE=1). Verify in the HealthMonitorUI admin "
                        + "tab or /dev/inspect Validators panel."
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

/// 2026-06-12 audit (RAG Gap 8) — flags the now-inert
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
/// per-instance ingestion via the explicit escape hatch or stay
/// single-instance. Mirrors `JobSchedulerInstanceValidator` (Phase 6l.F).
///
/// **Phase 509 — the lift.** `durableQueue` is `true` when the
/// deployment composed an `IIngestionQueueStore`
/// (`RAGServerApp.withDurableIngestionQueue`). The refusal exists
/// entirely because the default queue is process-local with no
/// redelivery; a durable queue removes that premise — the queue outlives
/// the process, the claim is atomic, and an unacknowledged lease is
/// redelivered — so the validator passes. The validator therefore reads
/// "refuse multi-replica UNLESS a durable queue is composed", not
/// "refuse multi-replica".
type RagIngestionInstanceValidator(serverConfig: ServerConfig, durableQueue: bool, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "rag-ingestion-instance"
        member _.Timeout = timeout

        member _.Validate() = async {
            let multiInstance = serverConfig.ReplicaCount > 1
            let escapeHatch = serverConfig.AcceptInProcessIngestionInMultiInstance

            if multiInstance && not durableQueue && not escapeHatch then
                return
                    Error(
                        sprintf
                            "RAG is composed with the in-process ingestion queue and ReplicaCount = %d. The queue is a process-local channel with no leasing or redelivery: only the replica that handled an upload can drain its ingestion job, and a crash/redeploy between dequeue and completion loses that document silently (the corpus becomes quietly incomplete). Compose a durable ingestion queue (RAGServerApp.withDurableIngestionQueue — e.g. the ToolUp.IngestionQueues.Redis companion), keep RAG single-instance, or set ServerConfig.AcceptInProcessIngestionInMultiInstance = true (TOOLUP_ACCEPT_INPROCESS_INGESTION_MULTI_INSTANCE=1) if you accept best-effort per-instance ingestion. Verify in the HealthMonitorUI admin tab or /dev/inspect Validators panel."
                            serverConfig.ReplicaCount
                    )
            else
                return Ok
        }

/// Phase 14w — surfaces the steady-state-memory contract for tombstone
/// vacuuming. `IVectorStore.DeleteChunk` is a soft-delete (stamps
/// `_deletedAt`); those tombstones are only reclaimed when
/// `IVectorStore.Vacuum` runs. `RAGServerApp.withVacuumSchedule` drives
/// `Vacuum` on a cron via the `IJobScheduler`, so a long-running replica's
/// memory stabilises — but only when a scheduler is actually composed.
///
/// Two misconfigurations warrant a `Warning` (not `Error` — the
/// deployment still runs, it just leaks tombstoned chunks over time):
///   1. `withVacuumSchedule` set but `JobScheduler = NoJobScheduler` — the
///      scheduled sweep can never fire (dead configuration).
///   2. A persistent deployment with NO vacuum schedule at all — tombstones
///      accumulate indefinitely until an operator vacuums by hand.
/// Ephemeral / no-schedule-and-no-scheduler dev deployments are `Ok`.
type VacuumScheduleValidator(serverConfig: ServerConfig, vacuumScheduleEnabled: bool, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "rag-tombstone-vacuum-schedule"
        member _.Timeout = timeout

        member _.Validate() = async {
            let schedulerOff = serverConfig.JobScheduler = NoJobScheduler
            let persistent = DeploymentConfig.hasPersistentAuthenticatedStorage serverConfig

            if vacuumScheduleEnabled && schedulerOff then
                return
                    Warning(
                        "RAGServerApp.withVacuumSchedule is configured but JobScheduler = NoJobScheduler — the tombstone auto-vacuum can never run. Soft-deleted chunks (_deletedAt tombstones from IVectorStore.DeleteChunk) accumulate until the process restarts, so a long-running replica grows toward OOM. Set ServerConfig.JobScheduler = InProcessJobScheduler (or a distributed scheduler companion) so the scheduled sweep can fire."
                    )
            elif not vacuumScheduleEnabled && persistent then
                return
                    Warning(
                        "RAG is composed without a tombstone auto-vacuum schedule. Soft-deleted chunks (_deletedAt tombstones from IVectorStore.DeleteChunk) are reclaimed only when an operator calls IVectorStore.Vacuum manually — otherwise they accumulate indefinitely and a long-running replica's memory grows without bound. Enable RAGServerApp.withVacuumSchedule together with ServerConfig.JobScheduler = InProcessJobScheduler for steady-state memory. Verify in the HealthMonitorUI admin tab or /dev/inspect Validators panel."
                    )
            else
                return Ok
        }

/// Phase 14y — warns when a RAG deployment that requires authentication
/// runs with no rate limiter configured (`RateLimit = RateLimitConfig.none`,
/// the default). Retrieval embeds *every* query through the configured
/// embedding provider, so the cost of a request is per-query, not per-
/// connection: an unbounded request loop against the AI-assistant /
/// knowledge-search path burns embedding-token spend and CPU with no
/// ceiling. That makes an unlimited authenticated RAG deployment a cost-DoS
/// surface even behind a TLS-terminating proxy (which caps connections, not
/// per-query provider spend) — a broader concern than the general
/// `RateLimitModeValidator`, which only fires when the deployment is also
/// internet-facing.
///
/// `Warning` (not `Error`): single-tenant deployments behind their own
/// rate-limiting proxy legitimately run `RateLimit = None`. Honours the same
/// escape hatch as `RateLimitModeValidator`
/// (`ServerConfig.AcceptNoRateLimitWhenAuthRequired` /
/// `TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE=1`) so an operator who has made
/// the informed decision silences both at once. Anonymous-only deployments
/// are `Ok` — there's no per-user budget to protect and public tools accept
/// the exposure by shape.
type RAGRateLimitConfiguredValidator(serverConfig: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "rag-rate-limit-configured"
        member _.Timeout = timeout

        member _.Validate() = async {
            let requiresAuth = DeploymentConfig.requiresAnyAuth serverConfig
            let rateLimitOff = not (RateLimitConfig.isEnabled serverConfig.RateLimit)
            let escapeHatch = serverConfig.AcceptNoRateLimitWhenAuthRequired

            if requiresAuth && rateLimitOff && not escapeHatch then
                return
                    Warning(
                        sprintf
                            "RAG is composed in an authenticated deployment (Surfaces = %s) with ServerConfig.RateLimit = RateLimitConfig.none. Retrieval embeds every query through the configured embedding provider, so an unbounded request loop against the AI-assistant / knowledge-search path burns embedding-token spend and CPU with no ceiling — a cost-DoS surface even behind a TLS-terminating proxy, because retrieval cost is per-query, not per-connection. Enable the SDK's per-subject-kind fixed-window limiter by setting ServerConfig.RateLimit = RateLimitConfig.uniform { PermitLimit = 100; WindowSeconds = 60; QueueLimit = 20 } (or RateLimitConfig.withOverrides for per-shape limits), or set ServerConfig.AcceptNoRateLimitWhenAuthRequired = true (TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE=1) if a rate-limiting proxy enforces per-client limits upstream. Verify in the HealthMonitorUI Preflight tab or /dev/inspect Validators panel."
                            (DeploymentConfig.surfacesLabel serverConfig)
                    )
            else
                return Ok
        }

// ─── Phase 9m.B — the 2026-05-06 RAG gap audit, Gaps 4 / 6 / 7 ───────
//
// Three validators closing the "silent default / unvalidated config
// knob / single-instance assumption" classes the audit named, plus the
// two `/dev/inspect` contributors that make the same facts readable
// without a restart. Gap 3 (RAG durability) is closed by the escape
// hatch added to `RagPersistenceValidator` above rather than by a fifth
// validator — a second check firing on the identical condition would
// warn twice about one problem, which is the nagging this family is
// explicitly gated to avoid.

/// **Gap 4** — the dev-only `LocalEmbeddingProvider` serving a
/// production-shaped deployment. The local embedder derives vectors
/// from a TF-IDF dictionary that evolves with the corpus *in this
/// process*: embeddings are therefore not reproducible across restarts
/// (a re-ingest of the same document yields a different vector) and not
/// comparable across replicas (each holds its own dictionary), so
/// retrieval quality drifts as the corpus grows and diverges as the
/// deployment scales out. It is the documented exception to the
/// stateless-between-calls rule (portability rule 4) and its file
/// header says so.
///
/// `Warning`, not `Error` — a single-replica `Individual` deployment
/// running the local embedder is a legitimate, fully-working shape, and
/// refusing it would break every offline / no-API-key deployment. The
/// operator gets the signal; the choice stays theirs.
///
/// **Gating — this fires only where `TeamModeLocalEmbedderValidator`
/// does not.** That validator already covers `Team` / `MultiTeam` with
/// a sharper message (the shared IDF dictionary leaks term-frequency
/// variance ACROSS tenants, a confidentiality concern this one is not
/// about). Both name the same remedy, so firing both on a team
/// deployment would report one problem twice. The two therefore
/// partition the deployment space: this one takes
/// `Individual` / `AuthenticatedEphemeral` with no team surface
/// present. `Anonymous`-only and `ClaimBearer`-only deployments are
/// outside both — no per-user corpus, nothing to drift.
type LocalEmbeddingProviderInProductionModeValidator
    (serverConfig: ServerConfig, embedder: IEmbeddingProvider, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "rag-local-embedder-in-production-mode"
        member _.Timeout = timeout

        member _.Validate() = async {
            // The team half belongs to TeamModeLocalEmbedderValidator —
            // see the gating note above.
            let nonTeamProductionShape =
                DeploymentConfig.hasAuthenticatedUserScope serverConfig
                && not (DeploymentConfig.hasTeamScope serverConfig)

            if
                nonTeamProductionShape
                && embedder.ProviderId = "local"
                && not serverConfig.AcceptLocalEmbedderAtScale
            then
                return
                    Warning(
                        sprintf
                            "LocalEmbeddingProvider is active in a %s deployment. The local TF-IDF embedder is dev-only and process-stateful: its IDF dictionary evolves with the corpus in THIS process, so the same document re-ingested later embeds differently, and a second replica embeds it differently again — retrieval quality drifts over time and diverges across instances (LocalEmbeddingProvider is the documented exception to portability rule 4, stateless-between-calls). Wire a stateless embedder (OpenAI / Cohere / Anthropic embeddings) as the third argument to RAGServerApp.create, or set ServerConfig.AcceptLocalEmbedderAtScale = true (TOOLUP_ACCEPT_LOCAL_EMBEDDER_AT_SCALE=1) to accept the trade-off on a single-replica deployment. The embedding_provider:local health probe reports Degraded for the same reason — see /dev/inspect."
                            (DeploymentConfig.surfacesLabel serverConfig)
                    )
            else
                return Ok
        }

/// **Gap 6** — RAG composed over a deployment whose modules register
/// data types but contribute NO `VectorisationHandler`. Nothing is ever
/// indexed: uploads succeed, the ingestion queue stays empty, retrieval
/// returns nothing, and every symptom points at the embedder or the
/// vector store rather than at the missing handler. There is no error
/// anywhere in that path — this is the pure silent-default class.
///
/// Complementary to, not overlapping with, the core composition rule
/// `unsatisfied-needs-data`, which checks the OTHER direction: a
/// `VectorisationHandler` whose `DataTypeId` matches no registered data
/// type. That rule cannot fire here, because the handler list is empty.
///
/// `Warning`, not `Error`. Two shapes legitimately index nothing:
/// a retrieval-only deployment reading a corpus some other process
/// wrote, and — checked explicitly below — a deployment that replaced
/// the whole pipeline via `RAGServerApp.withRetrievalPipeline` (the
/// static-corpus shape, where chunk embeddings are precomputed at build
/// time and live ingestion is deliberately suppressed).
type RAGHandlersRegisteredValidator
    (dataTypeIds: string list, handlerDataTypeIds: string list, retrievalPipelineOverridden: bool, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "rag-vectorisation-handlers-registered"
        member _.Timeout = timeout

        member _.Validate() = async {
            // A composed pipeline override owns its own corpus — no
            // handlers is the intended shape there, not a defect
            // (`composeRAG` suppresses the ingestion services outright
            // on exactly this condition).
            if retrievalPipelineOverridden then
                return Ok
            elif not handlerDataTypeIds.IsEmpty then
                return Ok
            elif dataTypeIds.IsEmpty then
                // No data types either — a document-only / KB-only
                // deployment. Nothing to vectorise, nothing to warn about.
                return Ok
            else
                let unhandled = dataTypeIds |> List.distinct |> List.sort

                return
                    Warning(
                        sprintf
                            "RAG is composed but NO module contributes a VectorisationHandler, while %d data type(s) are registered: %s. Nothing will ever be indexed — uploads succeed, the ingestion queue stays empty, and retrieval returns no matches with no error anywhere in the path, so the symptom looks like a broken embedder or vector store. Add a VectorisationHandler whose DataTypeId matches each type you want retrievable (ServerModule.withVectorisationHandlers), or compose a precomputed pipeline via RAGServerApp.withRetrievalPipeline if this deployment is retrieval-only by design. The /dev/inspect 'Vectorisation handlers' panel lists what is actually registered."
                            (List.length unhandled)
                            (String.concat ", " unhandled)
                    )
        }

/// **Gap 7** — the resolved RAG tuning knobs, as data, for
/// `RAGConfigBoundsValidator`. A record rather than seven constructor
/// arguments so a new knob joins by adding a field, and so the compose
/// site reads as an assignment of named values rather than a positional
/// sequence of ints and floats that is trivially transposable.
type RagConfigBounds = {
    TopK: int
    MinScore: float option
    /// Only checked when `MmrEnabled` — an inert λ on a deployment that
    /// never enabled MMR is not a misconfiguration worth a startup line.
    MmrLambda: float
    MmrEnabled: bool
    SnippetCharLimit: int
    IngestionConcurrency: int
    IngestionQueueCapacity: int
}

/// **Gap 7** — explicit bounds validation for the RAG tuning knobs,
/// replacing silence with a named contract.
///
/// The `with*` setters clamp on the way in (`withTopK 0` → `1`,
/// `withMinScore (Some 1.5)` → `Some 0.99`), and
/// `RetrievalDefaultsValidator` reports every clamp that fired — but the
/// clamps are all one-sided lower bounds. Nothing rejects `withTopK 200`
/// (a 200-match context block crowds out the conversation and dilutes
/// per-source attention), `withSnippetCharLimit 20` (clamped only to
/// 16 — below the ~32 chars a preview needs to be readable),
/// `withIngestionConcurrency 500` (500 concurrent embedding calls are
/// rate-limited by every hosted provider), or
/// `withIngestionQueueCapacity 10` (a queue that saturates and drops
/// documents on the first bulk upload). Each of those starts cleanly
/// today and misbehaves later, far from the line that caused it.
///
/// Severity split follows the phase's rule: `Error` — and therefore a
/// refused boot via `ConfigPreflightFailedException` — for a value
/// outside the supported range, because there is no correct behaviour
/// to fall back to. `Warning` for "legal but probably not what you
/// meant" (`TopK > 50`). Every message names the setter that produced
/// the value and the range it must land in, so the fix needs no doc
/// lookup.
///
/// Gated by construction: every default (`TopK = 5`, `MinScore = None`,
/// `SnippetCharLimit = 240`, `IngestionConcurrency = 8`,
/// `IngestionQueueCapacity = 5000`) sits inside its range, so a
/// deployment that tunes nothing is silent.
type RAGConfigBoundsValidator(bounds: RagConfigBounds, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "rag-config-bounds"
        member _.Timeout = timeout

        member _.Validate() = async {
            let errors = ResizeArray<string>()
            let warnings = ResizeArray<string>()

            if bounds.TopK < 1 || bounds.TopK > 100 then
                errors.Add(
                    sprintf
                        "TopK = %d is outside [1, 100] (RAGServerApp.withTopK / withRetrievalDefaults). TopK is the number of matches spliced into every system prompt: below 1 retrieval returns nothing at all, and above 100 the retrieval block crowds out the conversation and dilutes per-source attention long before it helps."
                        bounds.TopK
                )
            elif bounds.TopK > 50 then
                warnings.Add(
                    sprintf
                        "TopK = %d is legal but unusually high (RAGServerApp.withTopK). Typical deployments sit in 3-10; beyond ~50 the retrieval block dominates the prompt budget and per-source attention degrades. Confirm this was intended."
                        bounds.TopK
                )

            match bounds.MinScore with
            | Some score when score < 0.0 || score > 1.0 ->
                errors.Add(
                    sprintf
                        "MinScore = %g is outside [0.0, 1.0] (RAGServerApp.withMinScore). It gates cosine similarity, which is bounded by that range: a negative threshold is a no-op gate and a threshold above 1.0 filters out EVERY match, so the assistant goes silent with no diagnostic."
                        score
                )
            | _ -> ()

            if bounds.MmrEnabled && (bounds.MmrLambda < 0.0 || bounds.MmrLambda > 1.0) then
                errors.Add(
                    sprintf
                        "MmrLambda = %g is outside [0.0, 1.0] (RAGServerApp.withMmrLambda). Lambda interpolates relevance against diversity in the MMR re-rank; outside the unit interval the objective is no longer a convex combination and the ordering it produces is meaningless."
                        bounds.MmrLambda
                )

            if bounds.SnippetCharLimit < 32 || bounds.SnippetCharLimit > 8192 then
                errors.Add(
                    sprintf
                        "SnippetCharLimit = %d is outside [32, 8192] (RAGServerApp.withSnippetCharLimit). Below 32 characters a Sources-panel preview is truncated past the point of being identifiable; above 8192 the preview stops being a preview and ships whole documents to every client rendering the panel."
                        bounds.SnippetCharLimit
                )

            if bounds.IngestionConcurrency < 1 || bounds.IngestionConcurrency > 64 then
                errors.Add(
                    sprintf
                        "IngestionConcurrency = %d is outside [1, 64] (RAGServerApp.withIngestionConcurrency). Each slot is one in-flight batched embedding call, so this is the effective upstream request concurrency: 0 halts ingestion entirely, and above 64 every hosted embedding provider rate-limits you into the retry path instead of going faster."
                        bounds.IngestionConcurrency
                )

            if bounds.IngestionQueueCapacity < 100 || bounds.IngestionQueueCapacity > 1_000_000 then
                errors.Add(
                    sprintf
                        "IngestionQueueCapacity = %d is outside [100, 1000000] (RAGServerApp.withIngestionQueueCapacity). The queue is the buffer between upload and indexing: below 100 a single bulk upload saturates it and documents are dropped (saved but permanently unsearchable), and above 1000000 the pending backlog is a memory-exhaustion surface rather than a buffer."
                        bounds.IngestionQueueCapacity
                )

            if errors.Count > 0 then
                return Error(sprintf "RAG config knob(s) outside supported bounds — %s" (String.concat " | " errors))
            elif warnings.Count > 0 then
                return Warning(String.concat " | " warnings)
            else
                return Ok
        }

// ─── /dev/inspect contributors (Phase 9m.B) ─────────────────────────

/// Surfaces the deployment's RAG durability posture as a
/// `/dev/inspect` panel — "ephemeral" when the corpus does not survive
/// a restart, "durable" when it does, plus which backing supplies the
/// durability and whether the ephemeral state is an explicit opt-in
/// (`AcceptEphemeralRagIndex`) or the by-design shape of an ephemeral
/// deployment.
///
/// The preflight validator says this once, at boot, in a log line that
/// has usually scrolled away by the time anyone asks "does this thing
/// keep its index?". The panel answers it at any time without a restart.
type RagDurabilityContributor(blobStorageSupplied: bool, vectorStoreSupplied: bool, acceptedEphemeral: bool) =
    interface IDevDiagnosticsContributor with
        member _.Contribute() = async {
            let durable = blobStorageSupplied || vectorStoreSupplied

            let payload = {|
                durability = (if durable then "durable" else "ephemeral")
                survivesRestart = durable
                blobStorage = blobStorageSupplied
                vectorStoreOverride = vectorStoreSupplied
                acceptedEphemeralIndex = acceptedEphemeral
                note =
                    if durable then
                        "Ingested chunks are written through to durable storage and reload after a restart."
                    elif acceptedEphemeral then
                        "RAG durability: ephemeral — accepted explicitly via ServerConfig.AcceptEphemeralRagIndex (TOOLUP_ACCEPT_EPHEMERAL_RAG_INDEX=1). The corpus must be re-ingested after every process restart."
                    else
                        "RAG durability: ephemeral — the in-memory vector store writes through a null blob store that discards bytes. The corpus starts empty after every process restart."
            |}

            return "RAG durability", box payload
        }

/// Lists every registered `VectorisationHandler` by `DataTypeId`
/// alongside the registered data types, so an operator can see which
/// types are actually retrievable — whether or not
/// `RAGHandlersRegisteredValidator` fired.
///
/// The panel is the point: the validator only speaks when the handler
/// list is EMPTY, but the far more common production question is
/// "which of my types are indexed?", which a partial handler set
/// answers wrongly by silence. Registering the panel unconditionally
/// makes the partial case readable without adding a warning that would
/// fire on the many deployments where partial coverage is deliberate.
type VectorisationHandlerContributor(dataTypeIds: string list, handlerDataTypeIds: string list) =
    interface IDevDiagnosticsContributor with
        member _.Contribute() = async {
            let handlers = handlerDataTypeIds |> List.distinct |> List.sort
            let handlerSet = Set.ofList handlers
            let types = dataTypeIds |> List.distinct |> List.sort
            let typeSet = Set.ofList types

            let payload = {|
                handlerCount = List.length handlers
                handlers = handlers
                registeredDataTypes = types
                // The two asymmetries worth seeing at a glance: a type
                // nothing will index, and a handler that will never fire.
                unhandledDataTypes = types |> List.filter (handlerSet.Contains >> not)
                handlersWithNoDataType = handlers |> List.filter (typeSet.Contains >> not)
            |}

            return "Vectorisation handlers", box payload
        }