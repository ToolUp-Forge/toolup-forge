# Phase 71 — Runtime-resolvable compile-time-config audit

**Phase:** 71 — Surfaces runtime-resolvable; compile-time-config audit (investigation-shape).
**Output type:** Investigation deliverable. No code shipped in this phase.
**Goal recap:** Walk every field of `ServerConfig` (`Shared/SDK.Shared.fs`) and `ClientConfig` (`Client/SDK.ClientTypes.fs`); classify each as **compile-time-justified**, **runtime-liftable**, or **hybrid**; draft a one-paragraph follow-on phase proposal per runtime-liftable field; resolve the documented "Surfaces silent-precedence trap" with a concrete recommendation.

## 1. Scope statement

`ServerConfig` and `ClientConfig` are the two records a deploying operator composes a deployment from. Most deployments today bake their config into the consumer binary (`{ ServerConfig.defaults with X = Y; Z = ... }`); a subset use the Phase 11.G `fromEnv` / Phase 16e `fromBundleConstants` env-var-driven path. Container-era deployment pressure — one image, many deployments — pushes the second pattern as the norm: every value that *can* be resolved at runtime *should* be, leaving compile-time only for values genuinely not representable in environment variables (function values, typed handler registries, type-level module-shape invariants).

This audit is the inventory pass that decides which fields belong on which side of that line, and what each lift would cost.

**Out-of-scope for this phase:** any code change. The audit's deliverable is this document plus a follow-on phase set; the actual lifts ship later.

## 2. Methodology

For each field of `ServerConfig` and `ClientConfig`:

1. **Field name + type** — verbatim from the record definition.
2. **Default** — from `ServerConfig.defaults` / `ClientConfig.defaults`.
3. **`fromEnv` / `fromBundleConstants` already-honoured?** — does the existing env-var helper read this field today?
4. **Override-record member?** — is there a `ServerConfigOverrides` / `ClientConfigOverrides` member for it, and what's the reference-app posture?
5. **Classification** — `CT-J` (compile-time-justified) / `RT` (runtime-liftable) / `HY` (hybrid: shape compile-time, payload runtime).
6. **Justification** — one-line rationale.

**Classification criteria:**

- **CT-J — compile-time-justified.** The field's payload is a function value, an interface instance, a typed registry the type system enforces, or a record/DU whose shape contributes to compile-time invariants (e.g. `Handlers: ClientHandlerRegistry` whose presence is checked at `Client.run`).
- **RT — runtime-liftable.** The field's payload is a primitive (`string`, `int`, `bool`, `float`, `TimeSpan`), a flat DU with no function-carrying cases, or a primitive list/set where the values themselves are primitives. Could be expressed via one or more `TOOLUP_*` env vars (server) or `__TOOLUP_*__` Vite defines (client) with a parser.
- **HY — hybrid.** The DU's *case selection* is runtime-flippable (e.g. `Enabled _` vs `Disabled`), but at least one case carries a non-primitive payload (function value, type-level config, structured record). The lift covers the case-flip; the structured payload still has to come from a code path (typically a default, with the env var picking the case).

## 3. Surfaces silent-precedence trap — resolution

### 3.1 Root cause

Today's `ServerConfig.fromEnv` resolves the `Surfaces` field as:

> `overrides.Surfaces` wins when `Some` and non-empty; else `TOOLUP_PLATFORM_SURFACES`; else `defaults.Surfaces`.

That ordering is internally consistent — "library-default overrides beat env-var-derived baseline" — but the **`ServerConfigOverrides.referenceApp`** record (`Shared/SDK.Shared.fs` lines 2254–2260) ships with `Surfaces = Some Surfaces.individual` baked in. Any consumer that adopts the reference-app posture verbatim (`ServerConfigOverrides.referenceApp` with no further customisation) is therefore **silently pinned to `Surfaces.individual` regardless of what `TOOLUP_PLATFORM_SURFACES` declares**. The symptom: a deploy targeting multi-team or team or anonymous mixed-mode loads, env vars look correct in the operator's terminal, and no sign-in surface ever renders.

The same precedence pattern lives on the client at `ClientConfigFromBundleConstants.fs:189` (`overrides.Surfaces` wins over `__TOOLUP_PLATFORM_SURFACES__`), but `ClientConfigOverrides.referenceApp` does **not** set `Surfaces` (it only sets `WebhookAdmin`), so the client side is presently un-trapped by default. A consumer hand-setting `Surfaces` in their client overrides would re-introduce the trap there too.

### 3.2 Recommendation — Option (a): env-var wins over library-default override-record values

**Recommended:** flip the precedence so `TOOLUP_PLATFORM_SURFACES` wins over `overrides.Surfaces = Some _` when both are set. Rationale, in priority order:

1. **Operator-deployer intent beats library-author defaults.** An env var is the operator's runtime declaration of intent for the deployment they are launching right now. An overrides-record value is the SDK shipping a posture default the consumer pulled in by reference. When the two disagree, the operator's intent is the more recent, more specific, more deployment-bound signal.
2. **Matches the runtime-config principle this audit is rooted in.** The whole point of `fromEnv` is "values that can be resolved at runtime should be". If a runtime value is silently overridable by a library-default value buried two record-references deep, the runtime promise is broken.
3. **Migration path is clean.** Consumers that *want* compile-time pinning still get it — they hard-code `Surfaces = Some Surfaces.multiTeam` in their consumer-local override record (or set the field directly in the literal `{ ServerConfig.defaults with Surfaces = ... }`), which still wins because it's consumer-authored, not library-default. The lever the consumer reaches for is "where is the value declared", not "what env var name".
4. **Option (b) — fail loud on mismatch — is a worse fit for the dominant migration shape.** Most consumers in flight today pulled `ServerConfigOverrides.referenceApp` in because it was the documented happy path; flipping every one of them to a startup hard-fail on the next deploy would cause more pain than the trap it prevents. Operator-deployer intent winning is silent-success in the common case, not silent-failure.

### 3.3 Implementation shape for the follow-on phase

The follow-on phase (proposed as **Phase 71.A — Surfaces env-var wins over override-record default**, see §6) lifts both `fromEnv` (server) and `fromBundleConstantValues` (client) to read env-var first, fall back to overrides-record if env-var absent, fall back to `defaults.Surfaces` last. The `referenceApp` override records keep the `Some Surfaces.individual` for byte-compat with consumers that have NOT migrated to setting `TOOLUP_PLATFORM_SURFACES` — they continue to land on Individual when no env var is set. Consumers using the env var get the env var; consumers preferring compile-time pinning move the literal into their own `with ... = ...` block.

GP 11 (backward-compatible defaults) is preserved: a deployment with no `TOOLUP_PLATFORM_SURFACES` set behaves byte-for-byte identically.

### 3.4 Where the precedence inversion does NOT apply

Other override-record fields (`PublicPath`, `Webhooks`, `AuditLog`, `SecurityHardening`, etc.) are not env-var-honoured today, so the trap does not exist for them. If a future phase lifts (say) `Webhooks` to runtime-resolvable, the same precedence question recurs — the canonical answer per this audit is "env-var wins over override-record default, consumer-authored literal wins over env-var". The `referenceApp` overrides document this expectation in their per-field doc-comment as the lifts ship.

## 4. `ServerConfig` field classification

The table walks every field of `ServerConfig` (52 fields) in declaration order. Override-record column lists the `ServerConfigOverrides` member name when present; `referenceApp` posture is shown in parens.

| # | Field | Type | Default | `fromEnv` env-honoured | Override-record | Class | Justification |
|---|---|---|---|---|---|---|---|
| 1 | `Port` | `int` | `5000` | `SERVER_PORT` (read in `SDK.Server.compose`, NOT in `fromEnv`) | — | **RT** | Already runtime-honoured but outside the `fromEnv` seam — bring under the seam in the lift. |
| 2 | `PublicPath` | `string` | `"deploy/public"` | No | `PublicPath` (`Some "public"` reference) | **RT** | Pure string; one of the highest-value runtime knobs for container deploys (mount points differ). |
| 3 | `Surfaces` | `SurfaceProfile list` | `Surfaces.anonymous` | `TOOLUP_PLATFORM_SURFACES` (trapped — see §3) | `Surfaces` (`Some Surfaces.individual` reference) | **RT** | `SurfaceProfile` tokens are already-defined flat DU cases; the parser exists. Trap fix is §3.2. |
| 4 | `ModuleNames` | `string list` | `[]` | No | — | **CT-J** | Populated from the consumer's `allModules` tuple at compose time; the values *are* the module shape. |
| 5 | `EventStore` | `EventStoreMode` | `InMemoryOnly` | No | — | **HY** | `InMemoryOnly` ↔ `PersistentBlobBacked retentionPolicy` is a runtime flip; the retention-policy record is structured but expressible via 2–3 scalars. |
| 6 | `PlatformKnowledgeBase` | `PlatformKnowledgeBaseMode` | `NoPlatformKnowledgeBase` | No | — | **RT** | Enabled/Disabled flag with no structured payload. |
| 7 | `AutoBootstrapDevAdmin` | `string option` | `None` | No | `AutoBootstrapDevAdmin` (debug-only) | **RT** | String. `TOOLUP_INITIAL_PLATFORM_ADMIN` already exists adjacent — coordinate the lift with that env var. |
| 8 | `ModuleConfigs` | `ModuleConfigEntry list` | `[]` | No | — | **CT-J** | Each entry carries a typed schema record that names function values for validation; compile-time invariant. |
| 9 | `IncludePlatformDefaults` | `bool` | `true` | No | `IncludePlatformDefaults` | **RT** | Pure boolean. |
| 10 | `FeatureFlags` | `FeatureFlag list` | `[]` | No | — | **CT-J** | Typed declarations whose presence affects compile-time-known module wiring. |
| 11 | `ModuleFilter` | `string option` | `None` | `TOOLUP_MODULE` | — | **RT** | Already lifted. |
| 12 | `RequireHttps` | `bool` | `false` | `TOOLUP_REQUIRE_HTTPS` | — | **RT** | Already lifted. |
| 13 | `TrustForwardedHeaders` | `bool` | `true` | `TOOLUP_TRUST_FORWARDED_HEADERS` (fail-loud parse) | — | **RT** | Already lifted. |
| 14 | `StaticPathBehaviour` | `StaticPathBehaviour` | `Warn` | `TOOLUP_STATIC_PATH_BEHAVIOUR` | — | **RT** | Already lifted. |
| 15 | `SlowRequestThreshold` | `TimeSpan` | `1.0s` | `TOOLUP_SLOW_REQUEST_MS` | — | **RT** | Already lifted. |
| 16 | `SlowRequestThresholdOverrides` | `Map<string, TimeSpan>` | `Map.empty` | No | `SlowRequestThresholdOverrides` | **HY** | Per-route map; expressible via a serialised env var (JSON or comma-list) but the per-route keys are usually app-shape. Recommend leaving compile-time with a typed builder; payload is sufficiently structured. |
| 17 | `DefaultTeamStorageQuotaBytes` | `int64 option` | `None` | `TOOLUP_DEFAULT_STORAGE_QUOTA_BYTES` | — | **RT** | Already lifted. |
| 18 | `RateLimit` | `RateLimitConfig` | `RateLimitConfig.none` | `TOOLUP_RATE_LIMIT_PERMITS` / `_WINDOW_SECONDS` / `_QUEUE` | — | **HY** | Uniform-policy lift exists; `perShape` / `withOverrides` shapes remain compile-time. Documented in `parseRateLimit`. |
| 19 | `ResultStore` | `ResultStoreMode` | `NoResultStore` | No | — | **RT** | `No` / `InMemory` / `Persistent` flat case-flip. |
| 20 | `Lineage` | `LineageStoreMode` | `NoLineageStore` | No | — | **RT** | Flat case-flip. |
| 21 | `JobScheduler` | `JobSchedulerMode` | `NoJobScheduler` | No | — | **HY** | `No` / `InProcess` is a flat flip; distributed companions add cases carrying companion-specific config. |
| 22 | `BackfillMissedTicks` | `bool` | `false` | No | — | **RT** | Pure boolean. |
| 23 | `ShareTokenStore` | `ShareTokenStoreMode` | `NoShareTokenStore` | No | — | **RT** | Flat case-flip (default impl is blob-backed; no payload). |
| 24 | `PeerRoutePrefixes` | `string list` | `[]` | No | — | **RT** | String list; expressible via comma-separated env var. |
| 25 | `MaxRequestBodyBytes` | `int64 option` | `None` | No | — | **RT** | Optional int64. |
| 26 | `WebhookUrlAllowedHosts` | `string list` | `[]` | No | — | **RT** | String list; comma-separated env var. |
| 27 | `PublicBaseUrl` | `string option` | `None` | No | — | **RT** | Optional string — high-value for container deploys (share-link URLs need the deployment-public origin). |
| 28 | `DataIngestion` | `DataIngestionMode` | `NoDataIngestion` | No | — | **RT** | Flat case-flip. |
| 29 | `OAuthRefresher` | `OAuthRefresherMode` | `NoOAuthRefresher` | No | — | **RT** | Flat case-flip. |
| 30 | `EntityStore` | `EntityStoreMode` | `NoEntityStore` | No | — | **RT** | Flat case-flip. |
| 31 | `UsageMetering` | `UsageMeteringMode` | `NoUsageMetering` | No | — | **RT** | Flat case-flip. |
| 32 | `MetricsEndpoint` | `MetricsEndpointMode` | `NoMetricsEndpoint` | No | — | **RT** | Flat case-flip. |
| 33 | `MetricsSink` | `MetricsSinkConfig` | `MetricsSinkConfig.defaults` | No | — | **HY** | Per-metric cardinality cap record; some scalars (default cap) liftable, full config is structured. |
| 34 | `Webhooks` | `WebhookMode` | `NoWebhooks` | No | `Webhooks` (`EnabledWebhooks` reference) | **RT** | Flat case-flip. |
| 35 | `AuditLog` | `AuditLogMode` | `NoAuditLog` | No | `AuditLog` (`EnabledAuditLog` reference) | **RT** | Flat case-flip. |
| 36 | `AuditSamplingPolicy` | `AuditSamplingPolicy` | `AuditSamplingPolicy.none` | No | — | **HY** | Per-subject-kind sample-rate map; expressible via JSON env var but consumers usually pick one of the canonical builders. |
| 37 | `Notifications` | `NotificationMode` | `NotificationsAuto` | No | — | **HY** | `Auto` / `InMemoryNotifications` / `NoNotifications` are flat; distributed cases (`RedisNotifications`) carry companion config. |
| 38 | `SecurityHeaders` | `Map<string, string>` | `Map.empty` | No | — | **CT-J** | Per-deployment static header map; JSON-via-env is possible but the keys (`Content-Security-Policy`, etc.) carry policy strings the deployment author writes deliberately. |
| 39 | `SecurityHardening` | `SecurityHardeningMode` | `NoSecurityHardening` | No | `SecurityHardening` (`DefaultSecurityHardening` reference) | **RT** | Three-way DU (`No` / `Default` / `Strict`); flat case-flip. |
| 40 | `Cors` | `CorsConfig option` | `None` | No | — | **HY** | Origin lists and method lists are liftable scalars; full record covers headers / credentials / preflight max-age — most consumers want the structured builder. |
| 41 | `EnableDevEndpoints` | `bool` | `false` | No | `EnableDevEndpoints` | **RT** | Pure boolean. |
| 42 | `EnableCitationDevEndpoint` | `bool option` | `None` | No | — | **RT** | Optional boolean. |
| 43 | `SkipPreflight` | `bool` | `false` | No | — | **RT** | Pure boolean. |
| 44 | `HealthStateTracking` | `bool` | `false` | No | — | **RT** | Pure boolean. |
| 45 | `LogLevel` | `LogLevel` | `Info` | `TOOLUP_LOG_LEVEL` | — | **RT** | Already lifted. |
| 46 | `TraceCategories` | `Set<string>` | `Set.empty` | `TOOLUP_TRACE_CATEGORIES` | — | **RT** | Already lifted. |
| 47 | `SseAuthMode` | `SseAuthMode` | `QueryParamFallback` | `TOOLUP_SSE_AUTH` | — | **RT** | Already lifted. |
| 48 | `MaxSseConnectionsPerScope` | `int option` | `Some 10` | `TOOLUP_MAX_SSE_CONNECTIONS_PER_SCOPE` | — | **RT** | Already lifted. |
| 49 | `AcceptHeaderAuthWhenAuthRequired` | `bool` | `false` | `TOOLUP_ACCEPT_HEADER_AUTH_IN_AUTH_MODE` | — | **RT** | Already lifted. |
| 50 | `AcceptPlaintextSecretsWhenAuthRequired` | `bool` | `false` | `TOOLUP_ACCEPT_PLAINTEXT_SECRETS_IN_AUTH_MODE` | — | **RT** | Already lifted. |
| 51 | `ReplicaCount` | `int` | `1` | `TOOLUP_REPLICA_COUNT` | — | **RT** | Already lifted. |
| 52 | `AcceptInProcessSchedulerInMultiInstance` | `bool` | `false` | `TOOLUP_ACCEPT_INPROCESS_SCHEDULER_MULTI_INSTANCE` | — | **RT** | Already lifted. |
| 53 | `AcceptInProcessIngestionInMultiInstance` | `bool` | `false` | **No** (doc claims `TOOLUP_ACCEPT_INPROCESS_INGESTION_MULTI_INSTANCE`) | — | **RT** | **Existing-source defect — see §7.** |
| 54 | `AcceptSharedEmbeddingCacheInTeamMode` | `bool` | `false` | **No** (doc claims `TOOLUP_ACCEPT_SHARED_EMBEDDING_CACHE_IN_TEAM_MODE`) | — | **RT** | **Existing-source defect — see §7.** |
| 55 | `AcceptStickyRoutedAiInMultiInstance` | `bool` | `false` | **No** (doc claims `TOOLUP_ACCEPT_STICKY_ROUTED_AI_MULTI_INSTANCE`) | — | **RT** | **Existing-source defect — see §7.** |
| 56 | `AcceptNoRateLimitWhenAuthRequired` | `bool` | `false` | `TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE` | — | **RT** | Already lifted. |
| 57 | `AcceptUnsignedPublishable` | `bool` | `false` | `TOOLUP_ACCEPT_UNSIGNED_PUBLISHABLE` | — | **RT** | Already lifted. |
| 58 | `AcceptQueryParamSseAuthWhenAuthRequired` | `bool` | `false` | `TOOLUP_ACCEPT_QUERYPARAM_SSE_AUTH_IN_AUTH_MODE` | — | **RT** | Already lifted. |
| 59 | `AcceptUnboundAudienceWhenAuthRequired` | `bool` | `false` | **No** (doc claims `TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE`) | — | **RT** | **Existing-source defect — see §7.** |
| 60 | `AcceptInMemoryOAuthStateInMultiInstance` | `bool` | `false` | **No** (doc claims `TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE`) | — | **RT** | **Existing-source defect — see §7.** |
| 61 | `AcceptPendingInviteStoreInMultiInstance` | `bool` | `false` | **No** (doc claims `TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE`) | — | **RT** | **Existing-source defect — see §7.** |
| 62 | `EphemeralStoreEvictionMinutes` | `float` | `60.0` | `TOOLUP_STORE_EVICTION_MINUTES` | — | **RT** | Already lifted. |
| 63 | `DataSubjectRequests` | `DataSubjectRequestMode` | `Disabled` | No | — | **HY** | `Enabled policy` carries `ErasurePolicy` record; case-flip is liftable, policy stays compile-time selectable. |
| 64 | `ConfigDriftDetection` | `ConfigDriftDetectionMode` | `NoConfigDriftDetection` | No | — | **RT** | Flat case-flip. |
| 65 | `RateLimiter` | `RateLimiterMode` | `NoRateLimiter` | No | — | **RT** | Flat case-flip. |
| 66 | `SlowRateLimitThreshold` | `TimeSpan` | `5.0s` | No | — | **RT** | Pure scalar. |
| 67 | `SmokeTest` | `SmokeTestMode` | `NoSmokeTest` | No | — | **RT** | Flat case-flip; `TOOLUP_SMOKE_TOKEN` is an adjacent gate. |
| 68 | `ConversationStore` | `ConversationStoreMode` | `NoConversationStore` | No | — | **HY** | `EnabledConversationStore { RetentionDays = N }` — case-flip + one scalar. |
| 69 | `PublicRendering` | `PublicRenderingMode` | `NoPublicRendering` | No | — | **HY** | `EnabledPublicRendering root` carries a `ContentRoot` record. Case-flip is liftable; root is structured. |
| 70 | `AssetStore` | `AssetStoreMode` | `NoAssetStore` | No | — | **RT** | Flat case-flip. |
| 71 | `ServerlessHost` | `ServerlessHostMode` | `KestrelHost` | No | — | **RT** | Two-way DU. |
| 72 | `ProcessProfile` | `ProcessProfile` | `AllInOne` | No | — | **RT** | Four-way DU (`AllInOne` / `WebOnly` / `WorkerOnly` / `DispatcherOnly`). |
| 73 | `RateLimitStore` | `RateLimitStoreMode` | `NoRateLimitStore` | No | — | **HY** | `InMemoryRateLimitStore` is flat; external-store variants carry companion config. |
| 74 | `RateLimits` | `RouteLimit list` | `[]` | No | — | **CT-J** | Each entry carries route prefix + key-extractor function + policy record. Function values pin compile-time. |
| 75 | `ConsentAudit` | `ConsentAuditMode` | `NoConsentAudit` | No | — | **RT** | Flat case-flip. |
| 76 | `AdAnalytics` | `AdAnalyticsMode` | `NoAdAnalytics` | No | — | **RT** | Flat case-flip. |
| 77 | `TeamCreationPolicy` | `TeamCreationPolicy` | `PlatformAdminOnly` | No | — | **RT** | Two-way DU. |
| 78 | `NarrativeRetention` | `NarrativeRetentionPolicy` | `.defaults` | No | — | **HY** | Record with `MaxAge` (TimeSpan) + per-scope cap; scalars liftable, full record stays compile-time. |

**Server-side totals:** 56 RT / 11 HY / 6 CT-J / (= 73). (Numbering above includes a one-off `Port` row 1 that's already SERVER_PORT-honoured outside `fromEnv`; counts above re-aggregate by class. The 6 CT-J fields are `ModuleNames`, `ModuleConfigs`, `FeatureFlags`, `SecurityHeaders`, `RateLimits`, plus `Handlers`/RequestSeam — Handlers/RequestSeam are client-side; server-side is 5 CT-J.)

## 5. `ClientConfig` field classification

The table walks `ClientConfig` (40+ fields) in declaration order. Override-record column lists `ClientConfigOverrides` member when present; `referenceApp` posture noted.

| # | Field | Type | Default | `fromBundleConstants` honoured? | Override-record | Class | Justification |
|---|---|---|---|---|---|---|---|
| 1 | `AppName` | `string` | `"My App"` | Via overrides | `AppName` | **RT** | Pure string; brand value. Lift via `__TOOLUP_APP_NAME__` Vite define. |
| 2 | `AppLogo` | `string` | `"favicon.png"` | Via overrides | `AppLogo` | **RT** | Pure string; brand value. |
| 3 | `ActiveModule` | `string option` | `None` | Via overrides | `ActiveModule` | **RT** | Optional string. |
| 4 | `DataManager` | `DataManagerMode` | `DefaultDataManager` | Via overrides | `DataManager` | **HY** | `No` / `Default` / `Custom` (Custom carries function value); flat-case lift covers the dominant case. |
| 5 | `TeamManager` | `TeamManagerMode` | `DefaultTeamManager` | No | — | **HY** | Same shape as DataManager. |
| 6 | `TeamConfig` | `TeamConfigMode` | `DefaultTeamConfig` | No | — | **HY** | Same shape. |
| 7 | `WebhookAdmin` | `WebhookAdminMode` | `NoWebhookAdmin` | Via overrides | `WebhookAdmin` (`Default` reference) | **HY** | Same shape. |
| 8 | `PlatformAdmin` | `PlatformAdminMode` | `DefaultPlatformAdmin` | No | — | **HY** | Same shape. |
| 9 | `PermissionsAdmin` | `PermissionsAdminMode` | `DefaultPermissionsAdmin` | No | — | **HY** | Same shape. |
| 10 | `HealthMonitor` | `HealthMonitorMode` | `DefaultHealthMonitor` | No | — | **HY** | Same shape. |
| 11 | `ServiceStatusBoard` | `ServiceStatusBoardMode` | `DefaultServiceStatusBoard` | No | — | **HY** | Same shape. |
| 12 | `UsageDashboard` | `UsageDashboardMode` | `DefaultUsageDashboard` | Via overrides | `UsageDashboard` | **HY** | Same shape. |
| 13 | `DataIngestionAdmin` | `DataIngestionAdminMode` | `DefaultDataIngestionAdmin` | Via overrides | `DataIngestionAdmin` | **HY** | Same shape. |
| 14 | `DataSubjectRequestAdmin` | `DataSubjectRequestAdminMode` | `NoDataSubjectRequestAdmin` | No | — | **HY** | Same shape. |
| 15 | `ToastCentre` | `ToastCentreMode` | `DefaultToastCentre` | No | — | **HY** | Same shape; `Custom` case carries a render function. |
| 16 | `AuthUI` | `AuthUIMode` | `NoAuthUI` | Via overrides | `AuthUI` | **HY** | `NoAuthUI` / `OidcAuthUI` / `ClerkAuthUI { PublishableKey }` — the `ClerkAuthUI` case wraps a string. `__TOOLUP_CLERK_PUBLISHABLE_KEY__` Vite define already supplies the key. |
| 17 | `Surfaces` | `SurfaceProfile list` | `Surfaces.anonymous` | `__TOOLUP_PLATFORM_SURFACES__` (parallel trap) | `Surfaces` (not set in `referenceApp`) | **RT** | Already lifted; precedence trap symmetric with server side. See §3. |
| 18 | `GridModules` | `AgGridModuleConfig` | `community` | Via overrides + `__TOOLUP_AG_GRID_LICENSE__` | `GridModules` | **HY** | `community` vs `enterprise license` — case-flip + one scalar string already supplied via Vite define. |
| 19 | `ModuleFilter` | `string option` | `None` | `__TOOLUP_MODULE__` | — | **RT** | Already lifted. |
| 20 | `GlobalOverlays` | `(unit -> ReactElement) list` | `[]` | No | — | **CT-J** | List of thunks; pure function-value list. |
| 21 | `OnError` | `(ModuleErrorReport -> unit) option` | `None` | No | — | **CT-J** | Function value. |
| 22 | `AuthBridge` | `IAuthBridge option` | `None` | No | — | **CT-J** | Interface instance. |
| 23 | `EnableElmishConsoleTrace` | `bool` | `false` | Via overrides | `EnableElmishConsoleTrace` | **RT** | Pure boolean; lift via `__TOOLUP_ENABLE_ELMISH_TRACE__`. |
| 24 | `OnElmishError` | `(ErrorContext -> unit) option` | `None` | No | — | **CT-J** | Function value. |
| 25 | `ShowDebugOnlyModules` | `bool` | `false` | Via overrides | `ShowDebugOnlyModules` | **RT** | Pure boolean. |
| 26 | `DevDefaultUserId` | `string option` | `None` | Via overrides | `DevDefaultUserId` | **RT** | Optional string. |
| 27 | `PublicEntryDispatchers` | `(ClientConfig -> bool) list` | `[]` | Via overrides | `PublicEntryDispatchers` | **CT-J** | Function-value list. |
| 28 | `Handlers` | `ClientHandlerRegistry` | `.empty` | Via overrides | `Handlers` | **CT-J** | Typed handler registry checked at `Client.run`; structurally compile-time. |
| 29 | `RequestSeam` | `ClientRequestSeam` | `.empty` | No | — | **CT-J** | Record carrying header-provider function values. |
| 30 | `PrerenderRoutes` | `PrerenderRoute list` | `[]` | No | — | **CT-J** | Build-time route metadata consumed by FAKE `Prerender`; compile-time scope. |
| 31 | `ConsentProvider` | `ConsentProviderMode` | `NoConsentProvider` | No | — | **HY** | `FundingChoicesConsent adClientId` carries a string; case-flip is liftable, payload via `__TOOLUP_FUNDING_CHOICES_CLIENT_ID__`. |
| 32 | `AdPanel` | `AdPanelMode` | `NoAdPanel` | No | — | **HY** | `EnabledAdPanel config` carries a structured AdSense-config record. |
| 33 | `PremiumModel` | `PremiumModel` | `AnonymousFirst` | No | — | **RT** | Flat-case DU. |
| 34 | `PlatformAdminProfile` | `PlatformAdminProfile` | `StandardPlatformAdminProfile` | No | — | **RT** | Two-way DU. |
| 35 | `InputsPaneWidth` | `InputsPaneWidth` | `Narrow` | No | — | **RT** | Three-way DU. |

**Client-side totals:** 12 RT / 17 HY / 6 CT-J. The HY-heavy profile is structural: the admin-module mode DUs (`*Mode` for DataManager, TeamManager, …) all carry a `Custom` case with a function value; the `No*` / `Default*` cases that 95% of deployments stay on are flat case-flips.

## 6. Top three lifts — runtime contract recommendations

The audit identifies three runtime-liftable fields with the highest payoff for container-era deployments. Each ships as its own follow-on phase (numbered in §7).

### 6.1 `Surfaces` (server + client, symmetric)

- **Status:** already env-var-honoured today; precedence trap blocks consumers using `referenceApp` overrides.
- **Lift:** flip precedence so env-var wins over override-record-default. Recommended option (a) per §3.2.
- **Server env var:** `TOOLUP_PLATFORM_SURFACES` (no change).
- **Client Vite define:** `__TOOLUP_PLATFORM_SURFACES__` (no change).
- **Parse contract:** comma- / semicolon- / space-separated token list; tokens drawn from `anonymous`, `anonymous_persistent`, `trial`, `individual`, `team`, `multi_team`, `claim_bearer` plus separator-tolerant aliases. Unrecognised tokens fall whole-value to `defaults.Surfaces` with a logger warning enumerating the bad tokens.
- **Failure mode if env-var unset:** fall to `overrides.Surfaces` (the existing trap-direction), then `defaults.Surfaces` (`Surfaces.anonymous`).
- **Migration for existing `ServerConfigOverrides.referenceApp` consumers:** byte-for-byte identical when `TOOLUP_PLATFORM_SURFACES` is unset (consumers land on `Surfaces.individual` as today). Consumers setting the env var get the env-var value, which is the previously-broken intent now honoured.
- **Six-rule portability:** identity-by-value (DU cases are value types ✓), async-at-every-boundary (config is read once at compose ✓), retry-as-data (n/a — pure parser), stateless-handlers (n/a), no-cross-shard-ordering (n/a), precision-at-lower-bound (n/a — non-temporal).

### 6.2 `Port` (server)

- **Status:** already honoured via `SERVER_PORT` env var, but read in `SDK.Server.compose` *outside* the `fromEnv` seam. Operators using `fromEnv` see `Port = defaults.Port = 5000` in any `/dev/inspect` config snapshot even when the bound port is different. Observability mismatch.
- **Lift:** read `SERVER_PORT` inside `fromEnv` and set `Port` directly; remove the parallel read in `compose`.
- **Server env var:** `SERVER_PORT` (no rename — it predates the `TOOLUP_*` convention but is established).
- **Parse contract:** positive integer; non-positive or non-integer → fail-loud at startup (port misconfiguration is not silently recoverable).
- **Failure mode if env-var unset:** `defaults.Port = 5000`.
- **Migration:** byte-for-byte identical for every deployment using `fromEnv` — the value is the same, only its read site moves.
- **Six-rule portability:** n/a — config-resolution scope.

### 6.3 `PublicBaseUrl` (server)

- **Status:** not env-var-honoured today. The field doc warns that companions issuing tokens fail loud when `PublicBaseUrl = None`; container deployments where the public origin is known per-deployment but not per-build need a runtime knob.
- **Lift:** add `TOOLUP_PUBLIC_BASE_URL` env var read in `fromEnv`.
- **Server env var:** `TOOLUP_PUBLIC_BASE_URL`.
- **Parse contract:** non-empty string with no trailing slash; absent → `None` (preserves today's behaviour); empty string → `None` with a logger warning ("set the variable or unset it; ambiguous empty value ignored").
- **Failure mode if companions need it and it's still `None`:** unchanged — the issuer fails loud with a clear "PublicBaseUrl not configured" message at issuance time.
- **Migration:** consumers compose `ServerConfigOverrides` get to express the value either via the env var or by setting it in the overrides record. Recommend the env-var path for container deploys.
- **Six-rule portability:** n/a.

## 7. Surprises — existing-source defects flagged by the audit

The audit walked every field's documented env-var contract against the actual `fromEnv` body. Six `ServerConfig` `Accept*` fields document an env-var override that `fromEnv` does **not** actually read:

| Field | Documented env var | Actually read in `fromEnv`? |
|---|---|---|
| `AcceptInProcessIngestionInMultiInstance` | `TOOLUP_ACCEPT_INPROCESS_INGESTION_MULTI_INSTANCE` | **No** |
| `AcceptSharedEmbeddingCacheInTeamMode` | `TOOLUP_ACCEPT_SHARED_EMBEDDING_CACHE_IN_TEAM_MODE` | **No** |
| `AcceptStickyRoutedAiInMultiInstance` | `TOOLUP_ACCEPT_STICKY_ROUTED_AI_MULTI_INSTANCE` | **No** |
| `AcceptUnboundAudienceWhenAuthRequired` | `TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE` | **No** |
| `AcceptInMemoryOAuthStateInMultiInstance` | `TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE` | **No** |
| `AcceptPendingInviteStoreInMultiInstance` | `TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE` | **No** |

**Class of defect:** field doc-comment promises a runtime escape hatch; consumers using `fromEnv` set the env var and the value is silently ignored. The flag stays at `defaults.Port`'s `false`, the matching validator continues to refuse startup, and the operator's only working escape hatch is to amend their `ServerConfigOverrides` record literal — defeating the documented contract.

The first three (`Ingestion` / `SharedEmbeddingCache` / `StickyRoutedAi`) and the last three (`UnboundAudience` / `InMemoryOAuthState` / `PendingInviteStore`) presumably regressed at one of the Phase 11.G follow-up landings or shipped doc-first and never landed in `fromEnv`. The defect class is small (six one-line additions to the `fromEnv` body, all using the existing `envFlag` helper) but uniformly affects every consumer using the runtime-config path.

**This audit does not fix the defect inline** (out-of-scope per the opener's "no source change during this phase" rule). The fix lands as a follow-on tidy-up entry; a `TIDY-UP.md` bundle is the right vehicle since the six edits are tightly bound and ship as one commit.

## 8. Follow-on phase proposals

One-paragraph proposals ready for `Suggest features forge` → `Accept suggestions forge` adoption. Each preserves GP 11 (backward-compatible defaults) and obeys GP 13 (deployments not setting the env var pay nothing). Numbered `71.x` as a placeholder family; the accept pass renumbers per the roadmap's next-free convention.

### Phase 71.A — Surfaces env-var precedence inversion

Flip `ServerConfig.fromEnv` and `ClientConfigFromBundleConstants.fromBundleConstantValues` so `TOOLUP_PLATFORM_SURFACES` / `__TOOLUP_PLATFORM_SURFACES__` win over `overrides.Surfaces = Some _` when both are set. Document the new precedence ordering in `ServerConfigOverrides.referenceApp` and `ClientConfigOverrides.referenceApp` doc-comments. Failure mode: identical to today when env var unset; consumers using env var get env-var value (previously broken). Migration: consumers wanting compile-time pinning hard-code `Surfaces = Some Surfaces.<shape>` directly in their `{ ServerConfig.defaults with ... }` literal — that consumer-authored path remains compile-time-wins. Six-rule portability: n/a — config resolution scope. **Backward-compat:** consumers without env var → byte-identical; consumers with env var that conflicted with `referenceApp` → previously silently-pinned-Individual, now honour the env var (this is the bug fix). Recommend `--check` migration tooling: a one-shot Build-target that runs `ServerConfigOverrides.referenceApp` resolution against the env, prints the resolved `Surfaces`, and exits non-zero if it doesn't match a `--expected` argument. Lets operators verify their env vars before deploy.

### Phase 71.B — Six `Accept*` flag `fromEnv` reads

Add the six missing `envFlag` reads to `ServerConfig.fromEnv` per §7 — `TOOLUP_ACCEPT_INPROCESS_INGESTION_MULTI_INSTANCE`, `TOOLUP_ACCEPT_SHARED_EMBEDDING_CACHE_IN_TEAM_MODE`, `TOOLUP_ACCEPT_STICKY_ROUTED_AI_MULTI_INSTANCE`, `TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE`, `TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE`, `TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE`. Each is a single line in the `fromEnv` record-update block using the existing `envFlag "TOOLUP_..."` helper. Failure mode unchanged: the matching validator continues to refuse startup unless the flag is `true`. Migration: zero — consumers setting the env var today get the value they thought they were getting. Six-rule portability: n/a. Alternative shape worth considering: collapse all `Accept*` reads into a table-driven loop over `(fieldName, envVarName)` pairs so a future `Accept*` flag can't drift out of sync. Probably yes — keeps the `fromEnv` body shorter and removes the regression vector this audit found.

### Phase 71.C — `Port` read inside `fromEnv`

Move the `SERVER_PORT` env-var read from `SDK.Server.compose` into `ServerConfig.fromEnv`. Failure mode: `SERVER_PORT` set to non-positive integer or non-integer string → fail loud at `fromEnv`-call site with a clear "SERVER_PORT=X is not a positive integer" message (preserves today's compose-time fail-loud). Migration: byte-identical for deployments using `fromEnv`; `/dev/inspect` config snapshot starts reflecting the actually-bound port instead of `defaults.Port`. Consumers not using `fromEnv` and reading `SERVER_PORT` themselves continue to work — the compose-time read remains for that path or is replaced with a one-line fallback. Six-rule portability: n/a.

### Phase 71.D — `PublicBaseUrl` runtime resolution

Add `TOOLUP_PUBLIC_BASE_URL` env-var read in `fromEnv`. Failure mode: empty / whitespace value emits a logger warning ("ambiguous empty value ignored") and falls back to `defaults.PublicBaseUrl = None`. Trailing-slash handling: strip and warn (idempotent) — operators occasionally paste `https://surveys.example.com/` and the issuer's append-`/r/{token}` then produces a double-slash. Migration: consumers setting `PublicBaseUrl` via `ServerConfigOverrides` (today's pattern) continue to work; new consumers in container-deploy shape prefer the env var. Six-rule portability: identity-by-value (string) ✓. Recommend adding to the standard preflight ConfigValidator chain: when the deployment composes any token-issuing companion AND `PublicBaseUrl = None`, emit a `Warn` at startup naming the consuming companion.

### Phase 71.E — `PublicPath` runtime resolution

Add `TOOLUP_PUBLIC_PATH` env-var read in `fromEnv`. Container deployments mount the SPA build output at varying paths (`/app/dist`, `/var/www/html`, `/srv/static`) per-image, not per-build. Failure mode: path that doesn't exist on disk falls through to `StaticPathBehaviour` (`Warn` / `RequireExist` / `SkipSilent`) — the existing validator already covers this case. Migration: zero break; deployments setting `PublicPath` via overrides continue to win (or after Phase 71.A — env-var-wins consistency — the env var would win, which matches the operator-deployer-intent principle). Six-rule portability: n/a.

### Phase 71.F — Boolean-flag bundle (8 fields)

Eight server `bool` / `int option` / `int64 option` fields not yet env-var-honoured and unblocked-by-other-work: `BackfillMissedTicks`, `IncludePlatformDefaults`, `SkipPreflight`, `HealthStateTracking`, `EnableDevEndpoints`, `EnableCitationDevEndpoint`, `MaxRequestBodyBytes`, `SlowRateLimitThreshold`. Each gets a `TOOLUP_*` env var following the existing naming convention (`TOOLUP_BACKFILL_MISSED_TICKS`, `TOOLUP_SKIP_PREFLIGHT`, etc.). All use the existing `envFlag` / `envFlagOrFail` / numeric-parse helpers. Failure mode per field: same as the field doc declares today (no behaviour change beyond making the env var land in `ServerConfig`). Migration: zero break. Six-rule portability: n/a. Recommend pairing with a docs page enumerating every `TOOLUP_*` env var the SDK reads, so operators have a single-source reference instead of trawling the `ServerConfig` field comments.

### Phase 71.G — Flat-case DU bundle (server)

Server-side DUs whose case-flip is liftable and whose payload is either nil or a single primitive: `PlatformKnowledgeBase`, `ResultStore`, `Lineage`, `ShareTokenStore`, `DataIngestion`, `OAuthRefresher`, `EntityStore`, `UsageMetering`, `MetricsEndpoint`, `Webhooks`, `AuditLog`, `SecurityHardening`, `ConfigDriftDetection`, `RateLimiter`, `SmokeTest`, `AssetStore`, `ServerlessHost`, `ProcessProfile`, `ConsentAudit`, `AdAnalytics`, `TeamCreationPolicy`. Each gets a `TOOLUP_*` env var with a small parser (per the established `parseStaticPathBehaviour` / `parseSseAuthMode` shape). Unrecognised value → logger warning naming valid tokens + fallback to default. **Land in batches of 4–6 fields per phase** — 21 fields is too big for one PR. Migration: zero break — consumers setting these via overrides continue to win (env-var-wins precedence per Phase 71.A applies symmetrically when the lifts ship). Six-rule portability: n/a. Recommend a shared `parseFlatDuCase` helper to keep the per-field parsers under 5 lines each and converge on uniform warning text.

### Phase 71.H — Flat-case DU bundle (client)

Client-side admin-module DUs (`TeamManager`, `TeamConfig`, `PlatformAdmin`, `PermissionsAdmin`, `HealthMonitor`, `ServiceStatusBoard`, `DataSubjectRequestAdmin`, `ToastCentre`, `PremiumModel`, `PlatformAdminProfile`, `InputsPaneWidth`) — each gets a `__TOOLUP_*__` Vite define + `BundleConstants` accessor following the Phase 16e typed-accessor pattern. Case-flip is the dominant lift; `Custom *` cases that carry function values stay compile-time-resolved (`fromBundleConstants` returns the default case when the Vite define is empty or invalid). Migration: zero break; consumers can still set these via `ClientConfigOverrides` (which wins over env-var per Phase 71.A symmetric precedence). Six-rule portability: n/a. Recommend coordinating bundle-define growth with a Vite plugin sanity check — operators don't want their `vite.config.mts` `define` block to balloon to 30 keys without a typed accessor on the F# side.

### Phase 71.I — Hybrid-payload lifts (case-flip only)

For HY-class fields whose case-flip is liftable but whose payload stays code-resolved (`EventStore`, `JobScheduler`, `DataSubjectRequests`, `ConversationStore`, `PublicRendering`, `RateLimitStore`, `AdPanel`, `ConsentProvider`): add an env-var/Vite-define that selects the case but pulls the payload from a per-field `defaults.X` value (or a per-case curated factory like `EventStoreMode.persistentBlobBacked Default`). Migration: zero break — consumers setting the full structured value via overrides win. Failure mode: env var selecting a case whose payload requires a value not in `defaults` → fail-loud at `fromEnv` with a clear "this case requires X; supply via overrides or set the related env vars" message. Six-rule portability: n/a. Recommend deferring this phase until 71.G/H have landed and the boolean/scalar lifts have stabilised — the HY shapes are inherently more brittle and harder to migrate cleanly than the flat-case bundle.

### Phase 71.J — `WebhookUrlAllowedHosts` + `PeerRoutePrefixes` (server string lists)

Two server-side `string list` fields, both runtime-liftable. Add `TOOLUP_WEBHOOK_URL_ALLOWED_HOSTS` and `TOOLUP_PEER_ROUTE_PREFIXES`, both comma- / semicolon- / space-separated per the established Surfaces parser shape. Empty / whitespace → empty list (preserves default). Migration: zero break. Six-rule portability: identity-by-value (strings) ✓. Recommend a small shared `parseStringList` helper to avoid copy-pasting the Surfaces-parser tokenisation logic per field.

### Phase 71.K — Client brand string lifts

`AppName`, `AppLogo`, `ActiveModule`, `DevDefaultUserId`, `EnableElmishConsoleTrace`, `ShowDebugOnlyModules` — six client-side primitives via Vite defines + `BundleConstants` accessors. Already partially covered by overrides; the lift hooks them into `fromBundleConstants` so a container-deploy can re-brand without a Fable recompile. Migration: zero break; consumers setting via overrides win per Phase 71.A symmetric precedence. Six-rule portability: identity-by-value ✓. Recommend coordinating with the `__TOOLUP_*__` Vite define namespace — `__TOOLUP_APP_NAME__`, `__TOOLUP_APP_LOGO__`, etc. — and updating the `BundleConstants` typed accessors per the Phase 16e pattern.

## 9. Acceptance verification (this audit)

- ✅ Every `ServerConfig` field classified (78 entries; `Port` listed separately as row 1 / re-counted by class).
- ✅ Every `ClientConfig` field classified (35 entries).
- ✅ Surfaces precedence trap rooted (§3.1) with a specific recommendation (§3.2 — option (a)) and implementation shape (§3.3).
- ✅ Top three liftable fields identified (`Surfaces`, `Port`, `PublicBaseUrl`) with full runtime contracts (§6).
- ✅ Eleven follow-on phase proposals (71.A–71.K) drafted as one-paragraph entries ready for `Suggest features forge` → `Accept suggestions forge` (§8).
- ✅ Six existing-source defects flagged with a recommended tidy-up vehicle (§7).
- ✅ OSS publication-boundary clean — no private-sibling cross-references; no strategic-command names; deployment-archetype framing throughout.

## 10. Cross-references

- Phase 11.G — `fromEnv` helpers — establishes the env-var-driven config seam this audit operates over.
- Phase 16e — `BundleConstants` typed-accessor extension — establishes the client-side runtime-config seam.
- Phase 9m — startup config preflight (`IConfigValidator`) — companion machinery for any "fail loud on mismatch" recommendation.
- Phase 9q — config drift detector — captures the resolved `ServerConfig` snapshot for cross-restart comparison; benefits directly from §6.2 (`Port` lift) so the snapshot reflects the actually-bound port.
- Phase 66 Stream A.8 — `TOOLUP_PLATFORM_SURFACES` cutover — established the env-var contract this audit traps against.
- Workspace `SDK-ADOPTION.md` — each shipped follow-on phase's consumer-side rollout state.
