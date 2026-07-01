# Phase 14y — Retrieval rate-limit + query-size cap + endpoint exposure docs

**Ships in:** `ToolUp.RAG.Server` (`RetrievalPipeline`, `RAGCompose`/`RAGServerApp`,
`RagConfigValidator`) + a new root `DEPLOYMENT.md`. **Additive / opt-in with a safe default — a
deployment that never touches the new helpers changes behaviour only for pathologically long
queries (see below).**

## What changes

Three cost-and-DX gaps closed in one phase.

### 1. Query-size cap (`RAGServerApp.withMaxQueryChars`)

`IRetrievalPipeline.Retrieve` now refuses a query longer than a configured character cap **before
any embedding call**. A query this long is almost always a programming bug (an entire document
pasted into the query slot); embedding it wastes provider spend and can trip the provider's own
token limit with an opaque error. The refusal is hard: the pipeline raises
`ToolUp.RAG.RetrievalPipeline.KnowledgeQueryTooLargeException` (carrying `QueryChars` /
`MaxQueryChars`) and emits a `KnowledgeQueryRejected` audit event — **query hash + length only, never
plaintext** (same privacy contract as `RetrievalTrace`).

- **Default `Some 16384`** (~4k tokens) — generous for any genuine natural-language question. The
  only behaviour change for an existing deployment: a `Retrieve` call whose query exceeds 16384
  chars now raises instead of embedding. Tune via `RAGServerApp.withMaxQueryChars n` (floored at 1).

### 2. `RAGRateLimitConfiguredValidator`

A new startup `IConfigValidator` (registered by `composeRAG`) that emits a `Warning` when a RAG
deployment **requires authentication** (mode ≠ Anonymous) **and** `ServerConfig.RateLimit =
RateLimitConfig.none`. Retrieval embeds every query, so cost is per-query, not per-connection — an
unbounded request loop burns embedding-token spend even behind a TLS-terminating proxy. Broader than
`RateLimitModeValidator` (which additionally requires the deployment to be internet-facing). Honours
the same escape hatch — `ServerConfig.AcceptNoRateLimitWhenAuthRequired`
(`TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE=1`) — so an operator behind a rate-limiting proxy
silences both at once. `Warning`, never `Error`.

### 3. Endpoint-exposure docs (`DEPLOYMENT.md`)

New root `DEPLOYMENT.md` with a **"Health and metrics endpoints — authentication posture"** table
covering `/health`, `/ready`, `/metrics`, `/health/rag`, `/dev/inspect` — what each discloses,
whether it's always-on or gated, and the recommended proxy-layer rule. Closes the implicit-safety
gap (Gap 12): only `/health` + `/ready` are public-safe; `/metrics` and `/health/rag` belong on the
monitoring network; `/dev/inspect` (gated by `EnableDevEndpoints`, default off) must never be
exposed in production.

## New surface (all additive)

- `RAGServerApp.withMaxQueryChars (maxChars: int)` + record field `MaxQueryChars: int option`
  (default `Some 16384`).
- `ToolUp.RAG.RetrievalPipeline.KnowledgeQueryTooLargeException` (props `QueryChars`,
  `MaxQueryChars`) + literal `KnowledgeQueryRejectedEventType = "KnowledgeQueryRejected"`.
- `RetrievalPipeline` constructor gains two optional params `?maxQueryChars: int`,
  `?eventStore: IEventStore` (positional callers unaffected — appended at the end).
- `RagConfigValidator.RAGRateLimitConfiguredValidator`.

## Consumer action

**None required to stay on the new SDK version** beyond awareness of the 16384-char default. To
adopt:

1. Set your own `RAGServerApp.withMaxQueryChars n` if 16384 is wrong for your surface.
2. Configure `ServerConfig.RateLimit` (or set `AcceptNoRateLimitWhenAuthRequired = true`) to silence
   the new validator warning on authenticated deployments.
3. Apply the `DEPLOYMENT.md` endpoint table's proxy rules to your deployment topology.

## Verification

- `dotnet build src/ToolUp.RAG.Server/ToolUp.RAG.Server.fsproj` (0 errors).
- Public-API baseline (`api-baselines/ToolUp.RAG.Server.approved.txt`) regenerated for the two
  changed constructors (`RAGServerApp`, `RetrievalPipeline`).

## Rollback

Remove the `withMaxQueryChars` call (or pass a very large cap), drop the validator registration line
in `composeRAG`, and delete `DEPLOYMENT.md`. The default cap can be disabled entirely only by
constructing `RAGServerApp` with `MaxQueryChars = None` (not reachable through the public builder).
