# ToolUp.Platform Technical Guide — 07. Module Communication, Indexing & Portability

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> [← Prev: 6. Background Jobs, Ingestion & Diagnostics](06-jobs-ingestion-and-diagnostics.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 8. UI Components & Front-End →](08-ui-components.md)

---

## Module-to-module query bus

`IModuleQueryBus` (`Shared/IModuleQueryBus.fs`) lets one module ask another for data without a compile-time dependency between them. The canonical case: a dashboard module pulls the latest analysis from an analytics module; neither imports the other, both share only primitive request / response records declared in `ToolUp-SharedTypes`. The bus runs on both server and client — the same handler shape, the same permission model, and the same three-valued return — so a module can answer queries in-browser and fall back to the server for anything it can't satisfy locally.

### Three-valued return shape

```fsharp
abstract Ask:
    context: AccessContext * request: ModuleQueryRequest ->
        Async<Result<ModuleQueryResponse, ModuleQueryError> option>
```

The outer `option` and inner `Result` encode three distinct outcomes:

- `None` — the target module is not registered in this deployment. Callers treat this as **graceful degradation**: skip the enrichment, fall back to a default, run without the optional context. A module that becomes optional (e.g., an analytics module that only some tenants enable) can be removed from a deployment without breaking callers — they see `None` and carry on.
- `Some (Ok response)` — handler succeeded. `response.Payload` is a JSON string the caller deserialises into the typed response.
- `Some (Error err)` — target is present but the specific call failed. Three cases: `PermissionDenied` (Phase 4 RBAC blocked the caller), `NoHandler` (typo in `QueryKey` — the module is deployed but doesn't answer this key), `HandlerFailed` (the handler raised an exception; the bus caught and wrapped it).

The contrast matters: `None` is "feature not installed", `Some (Error (NoHandler _))` is "feature installed but the caller named a key it doesn't answer". The latter is a bug, the former is a deployment choice.

### Typed handler / caller helpers

`ModuleQueryHandler` is a single record (`QueryKey: string`, `Handle: ModuleQueryContext -> Async<string>`) shared across server and client. Modules construct handlers with `ModuleQueryHandler.typed` (server, in `Server/ModuleQueryBus.fs`) or `ClientModuleQueryHandler.typed` (client, in `Client/ModuleQueryClient.fs`) — both take typed request / response functions and wrap them in JSON (de)serialisation:

```fsharp
// Server — analytics module registers a handler:
let latestAnalysisHandler =
    ModuleQueryHandler.typed<LatestAnalysisReq, LatestAnalysisResp>
        "latest"
        (fun ctx req -> async {
            let! result = store.LoadLatest(ctx.AccessContext.TeamId, req.DatasetId)
            return { Summary = result.Summary; ComputedAt = result.ComputedAt }
        })

let serverModule =
    ServerModule.create "SkuAnalysis"
    |> ServerModule.withQueryHandlers [ latestAnalysisHandler ]
```

Callers use the mirror-image `ModuleQueryBus.ask` / `ModuleQueryClient.ask` helper:

```fsharp
let! result =
    ModuleQueryBus.ask<LatestAnalysisReq, LatestAnalysisResp>
        bus accessContext "SkuAnalysis" "latest" { DatasetId = id }

match result with
| None                              -> // SkuAnalysis not deployed — skip enrichment
| Some (Ok resp)                    -> // use resp.Summary / resp.ComputedAt
| Some (Error (PermissionDenied _)) -> // caller lacks Read on SkuAnalysis
| Some (Error (NoHandler _))        -> // key typo — caller bug
| Some (Error (HandlerFailed msg))  -> // handler threw; message already logged server-side
```

The raw `IModuleQueryBus.Ask` stays string-based (`Payload: string`) so the wire format is identical across in-process, ToolUp.Remoting, and any future distributed bus — callers opt into the typed projection at the call site, not at the interface.

### Serialisation rule (server + client)

Request and response payloads cross the manual JSON boundary — they are not ToolUp.Remoting for the in-process and HTTP paths. The server serialises with `Fable.Remoting.Json.FableJsonConverter` on Newtonsoft; the client serialises with `Fable.SimpleJson`. Both converters agree on the wire shape for records, unions, and `option` types, so the same `'TRequest` / `'TResponse` round-trips losslessly regardless of which side emitted the JSON. Do **not** swap in `DiscriminatedUnionConverter` or `CamelCasePropertyNamesContractResolver` — same rule as SSE (see `AI integration` → `ToolUp.AI/TECHNICAL_GUIDE.md`).

Typed request / response records must live in `ToolUp-SharedTypes` or be primitives (GP 10). Declaring them inside the answering module's project would force the caller to import that project, defeating the bus's whole purpose.

### In-browser short-circuit + HTTP fallback

The client-side `ClientModuleQueryBus` prefers local dispatch and falls back to the server for anything it can't answer:

1. **Module not locally registered** → HTTP call to `/api/IModuleQueryBusApi/Ask` (ToolUp.Remoting). The server's `None` (module not deployed) flows through unchanged.
2. **Module locally registered but no handler for the key** → fall through to HTTP. Server-only keys still work — a module can answer some keys in-browser (cached data, client-derived projections) and leave server-only keys (large joins, auth-sensitive queries) to the HTTP path.
3. **Local handler matched** → invoke in-process; permission checks are **not** performed client-side (the browser cannot enforce RBAC — any cross-module call that matters for security falls through to the server where the real check lives). Exceptions are caught, logged to `console.error`, and returned as `Some (Error (HandlerFailed _))` — the raw exception does not re-throw (portability Rule 3).

`ClientModuleQueryBus` is constructed once at shell startup (`SDK.Client.buildQueryBus`) and handed to every module via `ClientModuleContext.QueryBus`. Modules register their client handlers via `ClientModule.withQueryHandlers` — the shell aggregates them into a `(moduleId → queryKey → handler)` registry at `Client.run` time.

The `AccessContext` used for the in-browser path is best-effort — `UserId` from `UserSession`, `TeamId = None`, `ModulePermissions = Map.empty`. Handlers that need team identity should dispatch through the HTTP fallback, which resolves `AccessContext` from DI per request (populated by `ScopeResolutionMiddleware`).

### Permission check (server-side only)

`InMemoryModuleQueryBus` calls `AccessContext.hasPermission targetModule ModulePermission.Read ctx` before dispatching to the handler. Empty permission map = unrestricted (opt-in RBAC, same convention as `makePermissionGuardedApi`). A denied call returns `Some (Error (PermissionDenied moduleName))` as a typed result — it is **not** a 403 HTTP response. Clients branch on the typed error rather than parsing status codes, and the wire behaviour is identical whether the caller is in-process, another server module, or an HTTP ToolUp.Remoting client.

### Auto-injected HTTP endpoint

`ServerApp.run` registers `IModuleQueryBusApi` as a ToolUp.Remoting endpoint at `/api/IModuleQueryBusApi/Ask`. The server-side wrapper resolves `IModuleQueryBus` and `AccessContext` from DI per request and forwards the call — the typed `AccessContext` stays server-side, never crossing the wire, because clients cannot fabricate it. This matches the pattern used by `IConfigApi`, the five sibling platform APIs (`PlatformInfoApi` / `TeamApi` / `PermissionApi` / `AccessibilityApi` / `DataCatalogApi`), and the feature-flag API.

### Portability rule audit (GP 12 / Phase 9c)

The six rules, checked against the shipped `InMemoryModuleQueryBus` and its client `ClientModuleQueryBus` counterpart:

- **Rule 1 (identity by value).** `ModuleQueryRequest` fields are all primitive strings. Handler registry keys are `string` × `string`. No `IActorRef`, `IGrainReference`, or other runtime handle crosses the interface. ✓
- **Rule 2 (async at every boundary).** `IModuleQueryBus.Ask` is `Async<_>`. `ModuleQueryHandler.Handle` is `Async<string>`. No synchronous escape hatch. ✓
- **Rule 3 (retry as data).** v1 ships no retry — a future `RetryPolicy` overload is Phase 9c (out of scope for 6b). No callback-style `OnFailure` parameter or supervision strategy on the interface. The bus catches handler exceptions and returns `HandlerFailed`; the raw exception does not propagate. ✓
- **Rule 4 (stateless handlers).** `ModuleQueryContext` carries `AccessContext`, `CallerModule`, and `Request`. Handlers receive all per-invocation state through this record — no ambient globals, no assumption of in-memory state between calls. An Orleans grain or Akka actor restart between invocations is invisible to the handler. ✓
- **Rule 5 (no cross-shard ordering).** Point queries only. Concurrent `Ask`s to the same target are not promised to complete in submission order — callers that need ordering layer it above the bus. ✓
- **Rule 6 (precision at the lower bound).** No timing contract. ✓

A distributed bus (Akka cluster `Ask`, Orleans grain method, Redis request/reply) drops in under `IModuleQueryBus` without changing any caller — same typed helpers, same three-valued return, same permission model.

### Where this will go next

- **Phase 9c** (portability audit) adds `RetryPolicy` as an optional second parameter to `Ask`, with exponential-backoff semantics. Callers that want fire-and-retry opt in at the call site; the default stays no-retry.
- **Streaming responses** (potential future phase) — the current shape is request/reply only. A streaming variant would add an `AskStream` returning `AsyncSeq<_>`, reusing the same handler registry.
- **Distributed implementation** lands under `src/ModuleQueryBuses/<Name>/` when a multi-node deployment needs it. The companion pattern mirrors `src/NotificationChannels/Redis/`: a `.fsproj` + `.Server.props` injecting the implementation, bound to the shared interface contract test pack.

## Secondary indexes over `IBlobStorage` (Phase 9f)

The default blob-backed implementations of `IEventStore` and `IJobStore` filter by attribute (event type, idempotency key, due-job bucket) without scanning every blob in the scope. The mechanism is a shared helper in `Server/SecondaryIndex.fs` that maintains a per-key set of empty (or tiny-payload) `.ref` blobs alongside the canonical record. Distributed companions implement the same `IEventStore` / `IJobStore` interfaces — the index pattern is an implementation detail, not part of the contract.

### When to add an index

If a request-path query filters a collection of blobs by some attribute and the current implementation scales `O(items-in-scope)`, that's a candidate. The three Phase 9f indexes:

| Store | Index name | Filters by | Layout |
|---|---|---|---|
| `PersistentEventStore` | `_by-type` | `eventType` | `events/{scope}/_by-type/{eventType}/{eventId:N}.ref` |
| `PersistentEventStore` | `_by-source` | `sourceModule` | `events/{scope}/_by-source/{sourceModule}/{eventId:N}.ref` |
| `BlobJobStore` | `_idempotency` | sha256 of idempotency key | `jobs/{scope}/_idempotency/{sha256}/{jobId:N}.ref` |
| `BlobJobStore` | `_next-run` | `yyyyMMddHHmm` minute bucket | `jobs/{scope}/_next-run/{bucket}/{jobId:N}.ref` |

### Layout rules

- **Scope-prefixed.** The `indexPrefix` always sits under the canonical scope path (`events/{scope}/...`, `jobs/{scope}/...`). A `Lookup` against scope A cannot list scope B's refs — GP4 (team isolation) is structural, not defensive.
- **Empty or tiny payload.** Use empty blobs when the value-id alone is sufficient (the next-run index). Use a small denormalised JSON payload when the lookup needs auxiliary data without a canonical resolve (the canonical blob name for events, `(JobId, CreatedAt)` for idempotency TTL filtering).
- **Hash user-controlled keys.** Idempotency keys are user-supplied. Hash them (sha256-hex) before using as a path segment — bounds path length and avoids path-character issues.

### `BlobIndex<'TKey, 'TValue>` — the shared helper

```fsharp
type BlobIndex<'TKey, 'TValue> = {
    Add:    'TKey -> 'TValue -> byte[] option -> Async<unit>
    Remove: 'TKey -> 'TValue -> Async<unit>
    Lookup: 'TKey -> Async<('TValue * byte[] option) list>
    Rebuild: (unit -> Async<seq<'TKey * 'TValue * byte[] option>>) -> Async<int>
}
```

`BlobIndex.create` takes the storage handle, container, prefix, and three pure functions (`keyToSegment`, `valueToSegment`, `valueParser`). No instance state — distributed companions can drop in equivalent helpers binding to the same caller-side shape.

### Drift contract

Canonical state is authoritative. The two failure modes:

1. **Canonical write succeeds, index `Add` fails.** Reader `Lookup` misses the entry until `Rebuild` runs. The store treats index writes as best-effort (try/with that swallows) and never propagates the failure.
2. **Canonical record is deleted while an index ref still points at it.** The caller's resolver downloads the canonical, gets `Error`, and silently drops the entry from the result (a "soft miss"). Stale refs accumulate but reads stay correct.

Both surface in `IndexConsistencyCheck` (Phase 9f Step 5 — `/dev/inspect` exposes drift counts per indexed store / per index for the caller's scope). Drift > 0 is a recoverable bug class, not an alerting condition. The recovery is `Rebuild`, exposed Owner-only via `IMaintenanceApi`.

### Concurrency

`Add` / `Remove` race only on a single leaf `.ref` blob. `IBlobStorage.Upload` overwrites idempotently and `Delete` is idempotent. Two writers writing the same `(key, valueId)` pair produce identical content. No CAS needed at the index layer.

The `_next-run` index is the one place where ordering across writes matters: `BlobJobStore.Save` reads the prior persisted definition to find the prior bucket entry, removes it, and writes the new bucket entry. The in-process scheduler serialises all `Save` / `Update` calls per `JobId` via a per-job semaphore, so this read-then-write cannot interleave. Distributed companions either preserve that serialisation through their grain / actor model or use ETag-based CAS at the canonical-blob layer.

### Six-rule portability (GP12)

The helper satisfies every rule:

- **Rule 1 — Identity by value.** `'TKey` and `'TValue` are user value types — never live handles.
- **Rule 2 — Async at every boundary.** All four operations return `Async<_>`.
- **Rule 3 — Retry as data.** No retry / supervision parameters; delegated to caller's policy.
- **Rule 4 — Stateless handlers.** No instance state beyond the `IBlobStorage` reference + the closure-captured config.
- **Rule 5 — No cross-shard ordering.** `Lookup` result order is unspecified.
- **Rule 6 — Precision.** N/A (no scheduling primitives).

### Worked example: filter audit events by `userId`

A new dashboard wants to list "everything user X did" — an `events/{scope}/_by-actor/{userId}/{eventId:N}.ref` index plugs straight in:

1. Add a `_by-actor` index inside `PersistentEventStore` constructed via `BlobIndex.create` with `keyToSegment = id` and `valueToSegment = (fun (g: Guid) -> g.ToString "N")`. Match the existing by-type / by-source helpers' shape.
2. In `Write`, after canonical upload, `Add` to all three indexes in parallel. The single Phase 9f try/with already swallows index-write failures.
3. Add a `ReadByActor` member that delegates to `index.Lookup actorId` and resolves canonicals via the stored payload (the canonical blob name).
4. Extend `Rebuild` to populate the new index from canonical state alongside the existing two.
5. Extend `IndexConsistencyCheck` to return a third `IndexConsistencyEntry` for `_by-actor`.
6. Update `PruneScope` to delete by-actor refs alongside canonical (the existing prune code already deletes per-event index refs in one batch).

The pattern keeps the change additive — neither `IEventStore` nor any caller of `Write` / `ReadAll` changes.

## Production hardening — silent-default refusals (Phase 6l)

Phase 6l added six validators / probes / audit emitters that turn previously-silent production misconfigurations into loud refusals or visible degradations. The pattern is deliberately uniform: every check uses Phase 9m's `IConfigValidator` (refusals + warnings) or Phase 9k's `IHealthCheck` (durability probes) or Phase 9 audit emission (silent-but-now-visible runtime drops), and every refusal carries a documented escape hatch.

Catalogue (each row links to the validator/probe + its `ServerConfig` flag + the env var that flips it):

| Surface | What it refuses / surfaces | Escape hatch (`ServerConfig.X`) | Env var |
|---------|----------------------------|----------------------------------|---------|
| `HeaderAuthProviderModeValidator` | `Mode` requires auth + `IAuthProvider = HeaderAuthProvider` (header is spoofable; per-tenant data isolation evaporates) | `AcceptHeaderAuthInAuthenticatedMode` | `TOOLUP_ACCEPT_HEADER_AUTH_IN_AUTH_MODE=1` |
| `OidcAudienceBindingValidator` | `Mode` requires auth + `TOOLUP_AUTH_MODE=oidc` + `TOOLUP_OIDC_AUDIENCE` unset (the `aud` check is skipped, so a token issued for another relying party on the same IdP authenticates here — confused-deputy / token reuse) | `AcceptUnboundAudienceInAuthenticatedMode` (or set `TOOLUP_OIDC_AUDIENCE`) | `TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE=1` |
| `AuditLogModeValidator` | `Mode` requires auth + `AuditLog = NoAuditLog` (audit emission silently discards; SOC 2/ISO 27001/HIPAA/GDPR Article 30 blind spot) | (warning, not error — no flag needed; deployment can ignore) | n/a |
| `AuditLogHealthCheck` | Audit chain durability — write a marker through `IEventStore` under `_platform.audit_health`, read back. `NoOpAuditLog` reports `Healthy` (configured off, not broken) | n/a (probe) | n/a |
| `NotificationSilentlySkipped` audit case | `TransactionalDispatcher` prefs-driven drop — admin disabled email opt-in but a publish happened. PII-free: recipient userIds are SHA256-truncated to 8 hex chars | n/a (always emits — no policy decision is silent) | n/a |
| SSE per-scope cap (`MaxSseConnectionsPerScope`) | More than N concurrent SSE connections per scope → HTTP 429 + `Retry-After: 30` (memory DoS surface — one client could open 10k SSE conns) | `MaxSseConnectionsPerScope = None` (unbounded) or raise above default 10 | `TOOLUP_MAX_SSE_CONNECTIONS_PER_SCOPE=N\|none\|0` |
| `EncryptedSecretStoreModeValidator` | `Mode` requires auth + `TOOLUP_SECRETS_MASTER_KEY` unset (`EncryptedSecretStore` falls through to plaintext writes; permanent unencrypted credentials in blob storage) | `AcceptPlaintextSecretsInAuthenticatedMode` | `TOOLUP_ACCEPT_PLAINTEXT_SECRETS_IN_AUTH_MODE=1` |
| `JobSchedulerInstanceValidator` | `JobScheduler = InProcessJobScheduler` + `ReplicaCount > 1` (silent N-times duplication of cron jobs, webhooks, audit emissions) | `AcceptInProcessSchedulerInMultiInstance` (or configure Phase 9c.A distributed scheduler) | `TOOLUP_ACCEPT_INPROCESS_SCHEDULER_MULTI_INSTANCE=1` (+ `TOOLUP_REPLICA_COUNT=N`) |
| `OAuthStateStoreInstanceValidator` | in-memory `IOAuthStateStore` (the SDK default) + `ReplicaCount > 1` (OAuth CSRF/PKCE `state` is process-local; a /callback that lands on a different replica than issued the `state` fails with a state-mismatch — connector authorisation breaks intermittently) | `AcceptInMemoryOAuthStateInMultiInstance` (or configure a distributed `IOAuthStateStore` / sticky sessions) | `TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE=1` (+ `TOOLUP_REPLICA_COUNT=N`) |
| `RateLimitModeValidator` | `Mode != Anonymous` + `RequireHttps = true` + `RateLimit = None` (warning) — internet-facing authenticated deployment without rate-limiting is a trivial-cost DoS surface | `AcceptNoRateLimitInAuthenticatedMode` (or configure `TOOLUP_RATE_LIMIT_*`) | `TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE=1` |
| `SseAuthModeValidator` | `Mode != Anonymous` + `SseAuthMode = QueryParamFallback` (refusal) — browser `EventSource` puts userId in URL, leaking via CDN / web-server / Referer / browser history | `AcceptQueryParamSseAuthInAuthenticatedMode` (or set `TOOLUP_SSE_AUTH=cookie` + wire client `IAuthBridge`) | `TOOLUP_ACCEPT_QUERYPARAM_SSE_AUTH_IN_AUTH_MODE=1` |
| `SecurityHeadersValidator` | `Mode != Anonymous` + `RequireHttps = true` + `SecurityHeaders = Map.empty` (warning) — no HSTS / CSP / X-Frame-Options / nosniff on internet-facing surface | `ServerConfig.SecurityHeaders = SecurityHeaders.productionDefaults` (or merge with overrides) | n/a (no escape hatch — operator either sets headers or accepts warning) |
| `ForwardedHeadersTrustValidator` | `Mode != Anonymous` + `TrustForwardedHeaders = true` + `RequireHttps = false` (warning) — `X-Forwarded-Proto: https` from any peer fools `Request.IsHttps`, which fools cookie-secure / OIDC-RedirectUri / TLS-branching code | Set `TOOLUP_REQUIRE_HTTPS=1` (recommended) or accept warning for plain-HTTP-behind-upstream-TLS staging | n/a (warning, no escape hatch) |
| `LocalSecretFilePermissionsValidator` | (Unix only) Working-dir `secrets.json` / `secrets-*.json` with `GroupRead` or `OtherRead` set (warning) — multi-user-host secret leak. New writes via `FileSecretStore` are tightened to mode 600; this catches files predating the hardening or placed by deployment tooling. Windows path skipped (deferred follow-up). | `chmod 600 secrets*.json` or migrate to a cloud secret-manager `ISecretStore` | n/a (warning) |
| `IdentitySanitiser` (defence-in-depth, no validator) | Identity values containing `..` / `/` / `\` / NUL / control chars / Windows reserved names rejected at every auth boundary (`HeaderAuthProvider`, `OidcAuthProvider`); `LocalFileStorage` adds belt-and-braces `Path.GetFullPath` + `StartsWith(baseDir)` check on every read/write/list. | n/a (no escape hatch — no legitimate use for `..` in a user id) | n/a |

**`SkipPreflight` is not a master off-switch.** `ServerConfig.SkipPreflight = true` is the emergency-boot lever for a noisy *companion* probe — it skips the non-security-class validators only. The auth / secret / cross-instance-auth-state guards (`ConfigValidatorAggregator.securityClassValidatorNames`: `header-auth-mode`, `oidc-config-completeness`, `oidc-audience-binding`, `sse-auth-mode`, `encrypted-secret-store-mode`, `oauth-state-store-instance`, `per-scope-key-resolver-distributed`) always run and still abort startup on `Error`. The names of every skipped validator are logged at `Warn`, so a `SkipPreflight` boot is visible in the deployment log, not just the `/dev/inspect` panel. A single boolean must never silently disable identity-spoofing / unauthenticated-access protection.

**Operator scoreboard.** A healthy authenticated-mode deployment shows zero Warning/Error in `/dev/inspect`'s Validators panel and zero `Unhealthy` in the Health Checks panel. The `/dev/sse-trace` panel's `RefusalCounts` and `DroppedBroadcasts` should both stay at zero in the steady state — non-zero values point at either a misconfigured cap (operator's call) or a real client misbehaving.

**Adding a new silent-default check.** Mirror the Phase 6l pattern:
1. New `IConfigValidator` returning `Error` (refuse startup) or `Warning` (surface in `/dev/inspect`).
2. Optional companion `IHealthCheck` for runtime durability probes.
3. Optional new `AuditEvent` case if the check fires at runtime (not just startup).
4. Document the escape hatch in `ServerConfig` doc-comments.
5. Wire env var in the reference `Server.fs`.
6. Tests under `src/ToolUp.Platform.Tests/InProcess/`.

The `[<Tests>]` testList should be `testSequenced` if it mutates process-global env vars (`TOOLUP_*`) — Expecto's default parallel execution races those.

## Phase 9c portability — cross-interface audit summary

Phase 9c gates "build a distributed-task-framework companion" (Akka.NET reference + a second framework) on the precondition that the four GP-12-relevant infrastructure interfaces — `IJobScheduler`, `IJobStore`, `IModuleQueryBus`, `INotificationChannel` — already satisfy the six portability rules. This section is the consolidated verdict.

**Verdict:** all four interfaces pass all six rules with no code-level violations. The audit produced four documentation tightenings (each landed in its own commit referenced below) and one explicit per-interface exemption (the `INotificationChannel.Subscribe` callback shape, documented and justified).

### Per-interface verdict

| Interface | Source | Rule 1 | Rule 2 | Rule 3 | Rule 4 | Rule 5 | Rule 6 | Per-section audit |
|---|---|---|---|---|---|---|---|---|
| `IJobScheduler` | `src/ToolUp.Platform/Server/IJobScheduler.fs` | ✓ `JobId = Guid` (`Shared/JobTypes.fs`) | ✓ Every method `Async<_>` | ✓ `JobRetryPolicy` is a record, no callbacks | ✓ `IJobHandler.Execute(JobContext)` | ✓ `ShardKey` documented as affinity hint | ✓ `Schedule` rejects `Second` precision with `PrecisionUnsupported(Second, [Minute])` | "Background jobs (Phase 9b) → Phase 9c portability rule audit" |
| `IJobStore` | `src/ToolUp.Platform/Server/IJobStore.fs` | ✓ `JobId` + `ScopeId` lookups only | ✓ All `Async<_>` | n/a (no retry dispatch) | n/a (no handler shape) | ✓ Cross-scope reads structurally impossible | n/a | (same) |
| `IModuleQueryBus` | `src/ToolUp.Platform/Shared/IModuleQueryBus.fs` | ✓ Primitive-string fields on `ModuleQueryRequest` | ✓ `Ask` returns `Async<_>` | ✓ v1 ships no retry; future `RetryPolicy` overload is record-based | ✓ `ModuleQueryContext` is the handler's only state source | ✓ Point queries; no ordering promised | ✓ No timing contract | "Module-to-module query bus → Portability rule audit (GP 12 / Phase 9c)" |
| `INotificationChannel` | `src/ToolUp.Platform/Shared/INotificationChannel.fs` | ✓ `NotificationSubscriptionId = Guid`; `scopeId = string` | ⚠ Documented exemption — per-item handler callback is `(NotificationEnvelope -> unit)` (sync); the method is still `Async<_>` | ✓ Fire-and-forget; no callback supervision | ✓ Handlers receive every envelope as a value | ✓ Per-scope FIFO; cross-scope unordered | ⚠ Tightened — lower bound now declared as `JobPrecision.Minute` (cross-references Phase 9b) | "Real-time notification pipeline → Phase 9c portability rule audit — worked example" |

The two ⚠ marks are not violations:
- `INotificationChannel.Subscribe`'s sync callback is the documented Rule-2 exemption from the six portability rules — per-item hot-path dispatchers stay synchronous; the method itself is `Async<_>`. Carried verbatim from the Redis-companion audit.
- `INotificationChannel`'s precision lower bound was previously implicit ("near-real-time but not sub-second"); the Phase 9c audit tightened the docstring to explicitly cite `JobPrecision.Minute` as the contract floor (commit landed alongside this section).

### Tightenings landed in the audit

- **`IJobStore.fs:DueJobs`** — strengthened the indexing requirement from "becomes a Phase 9c follow-up only when measured pressure surfaces" to "Implementations targeting non-trivial scale MUST index on `(Status, NextRunAt)`." The contract now declares the scaling shape; implementations choose the storage that satisfies it.
- **`INotificationChannel.fs` Precision section** — added an explicit `JobPrecision.Minute` lower-bound reference so cron consumers know the channel is not a sub-minute transport.
- **No tightening needed on `IJobScheduler.fs`** — the existing docstring already cites the validation chain ("First failure short-circuits with the appropriate `ScheduleError` case").
- **No tightening needed on `IModuleQueryBus.fs`** — the existing docstring already cites Phase 9c as the home for the future `RetryPolicy` overload.

### Contract test coverage

| Interface | Contract pack | Bound to |
|---|---|---|
| `IJobStore` | `src/ToolUp.Platform.Tests/Contracts/IJobStoreContract.fs` (10 tests, Phase 9b) | `BlobJobStore` over `LocalFileStorage` |
| `INotificationChannel` | `src/ToolUp.Platform.Tests/Contracts/INotificationChannelContract.fs` (Phase 6a/6e) | `InMemoryNotificationChannel`, `RedisNotificationChannel` (env-gated) |
| `IJobScheduler` | `src/ToolUp.Platform.Tests/Contracts/IJobSchedulerContract.fs` (Phase 9c) | `InProcessJobScheduler` |
| `IModuleQueryBus` | `src/ToolUp.Platform.Tests/Contracts/IModuleQueryBusContract.fs` (Phase 9c) | `InMemoryModuleQueryBus` |
| `IHealthCheck` | `src/ToolUp.Platform.Tests/Contracts/IHealthCheckContract.fs` (7 tests, Phase 9k) | Healthy / Degraded / Unhealthy fakes; companion bindings (`RedisNotificationChannelHealthTests`, `AIProviderHealthTests`) |
| `IConfigValidator` | `src/ToolUp.Platform.Tests/Contracts/IConfigValidatorContract.fs` (7 tests, Phase 9m) | Ok / Warning / Error fakes; aggregator behaviour in `ConfigValidatorAggregatorTests` (11 tests) |

When a distributed companion lands (Akka, Orleans, Hangfire, …), it binds the same packs without modification — that is the load-bearing portability test.

### What this audit does NOT verify

- No multi-node cluster behaviour. The audit is per-interface contract-shape only. Cross-instance cache invalidation (Phase 5d's `MembershipChanged` flow under an Akka cluster), distributed leasing for `JobId` mutexes, and split-brain semantics all live in the actual companion implementation and need a real Akka cluster (or Orleans silo) to verify.
- No second-framework attempt. Validating a second companion (Orleans or Hangfire) against these contracts is the implementation work, not this audit. The audit's role is to confirm the interfaces are ready to host such an implementation.

### Where the actual companion lands

`src/JobScheduler/Akka/` is reserved for the Akka.NET reference companion. Currently contains a `README.md` placeholder describing the planned file structure (mirroring `src/NotificationChannels/Redis/`), the four interface implementations it will house, and the multi-node testing prerequisite. No `.fsproj` yet — empty F# projects in both solutions would be misleading clutter.

## Share-token substrate + anonymous routes (Phase 21b)

Phase 21b added a generic SDK-level substrate for distributing signed, scoped, expiring, use-limited tokens for delegated access. Forms publishable surveys are the first consumer (`src/ToolUp.Forms/TECHNICAL_GUIDE.md` "Phase 21b — publishable surveys" carries the survey-specific architecture); the same substrate will host future shareable dashboards, magic-login links, and public read-only views without renegotiating the wire format.

### `IShareTokenStore` — the primitive

Five-method interface (`src/ToolUp.Platform/Server/IShareTokenStore.fs`):

| Method | Returns | Purpose |
|---|---|---|
| `Issue` | `Async<Result<ShareToken, ShareTokenError>>` | Server fills `TokenId` / `IssuedAt` / `UsedCount=0` / `Revoked=false`; defaults applied for `ExpiresAt` (issue + 30 days) / `UseLimit` (`Some 1`) when caller passes `None` |
| `Validate` | `Async<Result<ShareTokenClaim, ShareTokenError>>` | Re-reads persisted claim; checks signature → existence → expiry → revocation → use-limit. Does NOT bump `UsedCount` |
| `MarkUsed` | `Async<Result<unit, ShareTokenError>>` | Atomic increment with `UseLimitExceeded` on overflow. Caller invokes after the consuming operation succeeds |
| `Revoke` | `Async<Result<unit, ShareTokenError>>` | Idempotent. `actorUserId` for audit |
| `ListByResource` | `Async<ShareTokenClaim list>` | Enumerate every claim for `(resourceKind, resourceId)` in scope. Includes revoked tokens; callers filter |

Opt-in via `ServerConfig.ShareTokenStore = EnabledShareTokenStore` (default `NoShareTokenStore` — apps that don't issue tokens pay nothing). Default impl `BlobShareTokenStore` is blob-backed; future distributed companions (e.g. Redis-cached for hot validation) bind the same five-method interface.

### Wire format

```
{tokenId}.{base64url(payloadJson)}.{base64url(hmac)}
```

- `tokenId` — 16 random bytes base64url-encoded (22 chars). Storage key for the persisted `ShareTokenClaim`.
- `payloadJson` — UTF-8-encoded JSON of `{ TokenId; ScopeId; ResourceKind; ResourceId }`. The `TokenId` field is duplicated so the embedded payload cross-checks the prefix; `ScopeId` / `ResourceKind` / `ResourceId` are duplicated so the consuming handler can detect a tampered scope or resource splice without trusting the embedded values (the persisted record is the source of truth — these fields exist to widen the signature target).
- `hmac` — HMAC-SHA256 over `payloadJson` bytes using the platform signing key.

The platform signing key (32 bytes) lives in `ISecretStore` under the reserved `_platform` scope as `share_token_signing_key`. Auto-generated and persisted on first use if absent — operators get share-tokens working immediately without pre-provisioning the key. Rotation: regenerate the secret via the secret store's normal Set/Delete flow, restart the process; all previously-issued tokens fail verification (no separate "key id" field — single key per deployment in v1).

### Storage layout

```
_platform/
  share-tokens/
    {scopeId}/
      {tokenId}.json                                       ← persisted ShareTokenClaim
      _by-resource/
        {sha256(resourceKind|resourceId)}/
          {tokenId}.ref                                    ← empty-payload index entry
```

Mirrors the `_platform/jobs/`, `_platform/audit/` layouts. Scope-prefixed for structural per-scope isolation. Resource-index segment hashes `(kind|id)` so user-supplied `resourceKind` / `resourceId` strings can't escape the path layout.

### Validation chain

`Validate` runs in this order (`ShareTokenStore.fs`):

1. `parts.Length = 3` — else `Malformed`
2. base64url-decode parts[1] (payload) and parts[2] (signature) — else `Malformed`
3. HMAC-SHA256 over decoded payload bytes equals decoded signature (constant-time compare) — else `InvalidSignature`
4. JSON-deserialise payload to `SignedPayload` — else `Malformed`
5. Persisted claim lookup by `payload.TokenId` — `NotFound` if absent
6. Cross-check claim's `(ScopeId, ResourceKind, ResourceId)` against the signed payload — `InvalidSignature` on mismatch (treated as forgery, not as a different error class — same observable)
7. `claim.Revoked = false` — else `RevokedToken`
8. `claim.ExpiresAt > DateTimeOffset.UtcNow` — else `Expired`
9. `claim.UseLimit` not exceeded by `claim.UsedCount` — else `UseLimitExceeded`

Steps 1–4 fast-fail before the storage hit (cheap rejection of obviously bad tokens); steps 5–9 require the persisted lookup. Revocation is server-side, so even a wire-valid token fails immediately after `Revoke` — no token-cache invalidation problem.

### Audit emission

Three new `AuditEvent` cases under `_platform.share_tokens`:

- `ShareTokenIssued` — payload carries `UserId` (issuer), `TokenId`, `ResourceKind`, `ResourceId`, `AttributedHandle option`, `ExpiresAt`. `AttributedHandle` may carry PII when the issuer chose to use an email as the handle — by design (the issuer's own scope already holds this data; audit echoes it for forensics).
- `ShareTokenUsed` — payload carries `TokenId`, `ResourceKind`, `ResourceId`, `AttributedHandle option`. **No `UserId`** — consumers are anonymous; the token IS the authentication.
- `ShareTokenRevoked` — payload carries `UserId` (actor), `TokenId`, `ResourceKind`, `ResourceId`.

`BlobShareTokenStore` takes an `IAuditLog option` constructor parameter — `Some` when `ServerConfig.AuditLog = EnabledAuditLog`, `None` otherwise. Audit emission is fire-and-forget per the existing `IAuditLog.Record` contract.

### Anonymous-route registry

`ServerConfig.AnonymousRoutePrefixes: string list` (default `[]`) declares path prefixes that bypass `AuthEnforcementMiddleware`. Companions register their public-write surfaces via `ServerApp.withAnonymousRoute (prefix: string)` at compose time. Default-deny is preserved — paths NOT registered continue through normal auth enforcement.

`AuthEnforcementMiddleware.isAnonymousRoute` is a case-insensitive `StartsWith` match. Requests that match the prefix get through to the route handler, which is responsible for authenticating via its own mechanism (typically validating a share token via `IShareTokenStore`).

### Per-token rate-limit partition

`RateLimiting.partitionKey` (`Server/RateLimiting.fs`) checks the anonymous-route registry first. When the request matches an anonymous prefix AND carries a `?token=` query parameter, the partition key is `token:{sha256(token)[..16]}` — so one respondent can't hammer the public endpoint and starve the rest of their cohort. Falls back to `ip:{remote-ip}` when the token query param is absent. Other paths use the existing team / user / IP partitioning.

The token-digest truncation (16 hex chars = 64 bits) is intentional — bounded keyspace keeps the partition dictionary small, and a deliberate collision would just merge two attackers' rate-limit buckets, no security implication.

### Six-rule Phase 9c portability audit

| Rule | `IShareTokenStore` |
|---|---|
| **1. Identity by value** | ✓ `tokenId: string`, `scopeId: string`, all DTO fields are strings / `DateTimeOffset` / `int`. No `IActorRef` / `IGrainReference` |
| **2. Async at every boundary** | ✓ All five methods return `Async<Result<_,_>>` |
| **3. Retry / supervision as data** | ✓ Failures returned as `ShareTokenError`; no callbacks; no `OnFailure` parameter |
| **4. Stateless between invocations** | ✓ Default impl re-reads the persisted claim on every `Validate`. `BlobShareTokenStore` caches the signing key (one in-process atom; resolved lazily) but the cache is correct under concurrent reads and would re-resolve in a fresh process |
| **5. No cross-shard ordering** | ✓ Each token is independent; no inter-token ordering claim |
| **6. Precision at lower bound** | ✓ N/A — token validity is timestamp-bounded (`DateTimeOffset`), not tick-driven |

Bound by `IShareTokenStoreContract` (11 tests in `src/ToolUp.Platform.Tests/Contracts/IShareTokenStoreContract.fs`) — the load-bearing portability test for any future distributed companion.


---

> [← Prev: 6. Background Jobs, Ingestion & Diagnostics](06-jobs-ingestion-and-diagnostics.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 8. UI Components & Front-End →](08-ui-components.md)
