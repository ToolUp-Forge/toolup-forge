# `ServerConfig` nested by concern, and one `FeatureMode` for every on/off toggle

Version: 0.39.0 (breaking, a 0.x minor — rides the draft Phases 876 and 875 made breaking).

## What changes

1. **`ServerConfig` is a record of concern records plus what remains at the top level.** Sixty-two of the
   147 flat fields moved into five records: `SecurityPosture`, `RiskAcknowledgements`, `MultiInstance`,
   `Observability` and `Stores`. A field's home is listed in the table below; a field not in the table did
   not move. Each concern has its own `defaults` (`Stores.defaults`, `Observability.defaults`, …), and
   `ServerConfig.defaults` composes them.
2. **The thirty-seven payload-free two-case `*Mode` unions are one type, `FeatureMode = Disabled | Enabled`**
   (`FeatureMode.isEnabled`, `FeatureMode.ofBool`). `NoAuditLog | EnabledAuditLog`,
   `NoEntityStore | EnabledEntityStore`, `NoExternalCompute | CustomExternalCompute` and the rest carried one
   boolean fact each — whether the subsystem is composed — under thirty-seven names. The type is
   `RequireQualifiedAccess`: write `FeatureMode.Enabled`, never a bare `Enabled`. A mode that carries a
   payload (`EventStoreMode`, `ConversationStoreMode`), has a third case (`SecurityHardeningMode`) or chooses
   between two substrates (`GraphStoreMode`, `ServerlessHostMode`, `DeployPlaneMode`) keeps its own type.
3. **`ServerConfigOverrides` takes `FeatureMode option`** where it took `WebhookMode option`,
   `AuditLogMode option` and `ShareTokenStoreMode option`. Its fields did not move: it is a flat override
   record, and `fromEnv` applies each override to the field it names.
4. **The environment is unchanged.** Every `TOOLUP_*` key and every accepted token binds exactly what it bound
   before; `ServerConfig.fromEnv` is pinned field by field against the snapshot Phase 875 took of the flat
   record (`src/ToolUp.Platform.Tests/fixtures/server-config-fromenv-snapshot.txt`, unchanged), through the
   flat-to-nested projection in `ServerConfigSnapshotTests.fs`. A deployment edits no variable.
5. **Seven readers of boolean keys outside the binder resolve through their `ConfigKey`** —
   `ConfigKey.readBool` / `readPermissiveBool` are new — so `TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION`,
   `TOOLUP_LDAP_NESTED_GROUPS`, `TOOLUP_AUDIT_ADMIN_REQUIRED`, `TOOLUP_ENTRA_DIRECTORY_ENABLED` and
   `TOOLUP_AI_PROBE_ON_STARTUP` accept `1` / `true` / `yes` / `on` in any case, read a typo as off and
   an unset key as the registry's default, exactly as the generated reference documents; the remoting
   analyzer's `TOOLUP_REMOTING_ANALYZER_AUDIT` reads the same token set; and the two readers of
   `TOOLUP_NOTIFICATION_CHANNEL` agree that `in-process` is in-process.

## Run the codemod

```
dotnet run --project Build.fsproj -- Codemod <your-source-dir> --phase 877
```

`--phase 877` applies this migration's rules and no other. It rewrites the retired cases and types, nests
every record-update expression whose base it can re-read (`{ ServerConfig.defaults with … }`,
`{ app.Config with … }`, `{ defaults with … }`), and re-homes a moved field read off a receiver it can
prove is the config. It reports, and does not rewrite:

- a record update whose base is an application or a parenthesised expression (`{ (ServerConfig.fromEnv
  logger overrides) with LogLevel = … }`) — bind the base to a name first;
- a record update that sets only labels other records carry too (`EventStore`, `EntityStore`, `ResultStore`,
  `Datasets`, `ShareTokenStore`, `RateLimitStore`, `MetricsSink`, `SlowRequestThresholdOverrides`,
  `DatadogReadback`, `SecurityHardening`, `ProcessProfile`, `LogLevel`, `Cors`) on a base not named as the
  config — a deps or options record of your own is unchanged, a `ServerConfig` is nested by hand;
- a read of one of those labels off a receiver not named as the config (`deps.EventStore`).

Re-run `--check` until the plan is empty; the rules are idempotent.

## Diff, per file

A record update:

```diff
 let config = {
     ServerConfig.defaults with
         Port = 5000
-        EntityStore = EnabledEntityStore
-        AuditLog = EnabledAuditLog
-        RequireHttps = true
-        ReplicaCount = 3
-        AcceptEphemeralRagIndex = true
+        Stores = { ServerConfig.defaults.Stores with EntityStore = FeatureMode.Enabled }
+        AuditLog = FeatureMode.Enabled
+        SecurityPosture = { ServerConfig.defaults.SecurityPosture with RequireHttps = true }
+        MultiInstance = { ServerConfig.defaults.MultiInstance with ReplicaCount = 3 }
+        RiskAcknowledgements = { ServerConfig.defaults.RiskAcknowledgements with AcceptEphemeralRagIndex = true }
 }
```

A read, and a match:

```diff
-if config.ReplicaCount > 1 && config.EntityStore = EnabledEntityStore then
+if config.MultiInstance.ReplicaCount > 1 && config.Stores.EntityStore = FeatureMode.Enabled then
```

```diff
-match config.AuditLog with
-| NoAuditLog -> []
-| EnabledAuditLog -> routes
+match config.AuditLog with
+| FeatureMode.Disabled -> []
+| FeatureMode.Enabled -> routes
```

A type annotation, and an override:

```diff
-let describe (mode: AuditLogMode) = …
+let describe (mode: FeatureMode) = …
-{ ServerConfigOverrides.empty with Webhooks = Some EnabledWebhooks }
+{ ServerConfigOverrides.empty with Webhooks = Some FeatureMode.Enabled }
```

A reader of a boolean key outside `fromEnv`:

```diff
-match Environment.GetEnvironmentVariable ConfigKeys.Names.aiProbeOnStartup with
-| "1" | "true" | "TRUE" -> true
-| _ -> false
+let key = ConfigKeys.key ConfigKeys.Names.aiProbeOnStartup
+ConfigKeys.ConfigKey.readPermissiveBool key (Environment.GetEnvironmentVariable key.EnvVar |> Option.ofObj)
```

## The field map

Every field that moved, its new path, and its type. A field absent from this table is still at the top level
under its old name (the second table lists the top-level fields whose TYPE changed). The same table is
`Codemod.concernFields` in `ToolUp.Platform.Build`, which both the codemod and the snapshot projection read.

| Flat field | Now | Type |
|---|---|---|
| `RequireHttps` | `SecurityPosture.RequireHttps` | unchanged |
| `TrustForwardedHeaders` | `SecurityPosture.TrustForwardedHeaders` | unchanged |
| `TrustedProxyCidrs` | `SecurityPosture.TrustedProxyCidrs` | unchanged |
| `SecurityHeaders` | `SecurityPosture.SecurityHeaders` | unchanged |
| `SecurityHardening` | `SecurityPosture.SecurityHardening` | unchanged |
| `Cors` | `SecurityPosture.Cors` | unchanged |
| `SseAuthMode` | `SecurityPosture.SseAuthMode` | unchanged |
| `AuthCookieIssuance` | `SecurityPosture.AuthCookieIssuance` | `FeatureMode` (was `AuthCookieIssuanceMode`) |
| `MaxRequestBodyBytes` | `SecurityPosture.MaxRequestBodyBytes` | unchanged |
| `WebhookUrlAllowedHosts` | `SecurityPosture.WebhookUrlAllowedHosts` | unchanged |
| `AcceptForwardedHeadersFromAnyProxy` | `RiskAcknowledgements.AcceptForwardedHeadersFromAnyProxy` | unchanged |
| `AcceptHeaderAuthWhenAuthRequired` | `RiskAcknowledgements.AcceptHeaderAuthWhenAuthRequired` | unchanged |
| `AcceptPlaintextSecretsWhenAuthRequired` | `RiskAcknowledgements.AcceptPlaintextSecretsWhenAuthRequired` | unchanged |
| `AcceptInProcessSchedulerInMultiInstance` | `RiskAcknowledgements.AcceptInProcessSchedulerInMultiInstance` | unchanged |
| `AcceptInProcessIngestionInMultiInstance` | `RiskAcknowledgements.AcceptInProcessIngestionInMultiInstance` | unchanged |
| `AcceptSharedEmbeddingCacheInTeamMode` | `RiskAcknowledgements.AcceptSharedEmbeddingCacheInTeamMode` | unchanged |
| `AcceptEphemeralRagIndex` | `RiskAcknowledgements.AcceptEphemeralRagIndex` | unchanged |
| `AcceptLocalEmbedderAtScale` | `RiskAcknowledgements.AcceptLocalEmbedderAtScale` | unchanged |
| `AcceptStickyRoutedAiInMultiInstance` | `RiskAcknowledgements.AcceptStickyRoutedAiInMultiInstance` | unchanged |
| `AcceptNoRateLimitWhenAuthRequired` | `RiskAcknowledgements.AcceptNoRateLimitWhenAuthRequired` | unchanged |
| `AcceptUnsignedPublishable` | `RiskAcknowledgements.AcceptUnsignedPublishable` | unchanged |
| `AcceptQueryParamSseAuthWhenAuthRequired` | `RiskAcknowledgements.AcceptQueryParamSseAuthWhenAuthRequired` | unchanged |
| `AcceptSameSiteOnlyCsrfWhenAuthRequired` | `RiskAcknowledgements.AcceptSameSiteOnlyCsrfWhenAuthRequired` | unchanged |
| `AcceptUnboundAudienceWhenAuthRequired` | `RiskAcknowledgements.AcceptUnboundAudienceWhenAuthRequired` | unchanged |
| `AcceptInMemoryOAuthStateInMultiInstance` | `RiskAcknowledgements.AcceptInMemoryOAuthStateInMultiInstance` | unchanged |
| `AcceptInMemoryShareTokenRateLimiterInMultiInstance` | `RiskAcknowledgements.AcceptInMemoryShareTokenRateLimiterInMultiInstance` | unchanged |
| `AcceptPendingInviteStoreInMultiInstance` | `RiskAcknowledgements.AcceptPendingInviteStoreInMultiInstance` | unchanged |
| `AcceptInviteByEmailWithoutDirectory` | `RiskAcknowledgements.AcceptInviteByEmailWithoutDirectory` | unchanged |
| `AcceptEphemeralShareTokenKey` | `RiskAcknowledgements.AcceptEphemeralShareTokenKey` | unchanged |
| `AcceptAnonymousModeWithAI` | `RiskAcknowledgements.AcceptAnonymousModeWithAI` | unchanged |
| `ReplicaCount` | `MultiInstance.ReplicaCount` | unchanged |
| `ProcessProfile` | `MultiInstance.ProcessProfile` | unchanged |
| `PeerRoutePrefixes` | `MultiInstance.PeerRoutePrefixes` | unchanged |
| `PeerSubstrate` | `MultiInstance.PeerSubstrate` | `FeatureMode` (was `PeerSubstrateMode`) |
| `LogLevel` | `Observability.LogLevel` | unchanged |
| `TraceCategories` | `Observability.TraceCategories` | unchanged |
| `HealthStateTracking` | `Observability.HealthStateTracking` | unchanged |
| `MetricsEndpoint` | `Observability.MetricsEndpoint` | `FeatureMode` (was `MetricsEndpointMode`) |
| `MetricsSink` | `Observability.MetricsSink` | unchanged |
| `MetricsHistory` | `Observability.MetricsHistory` | unchanged |
| `TelemetrySink` | `Observability.TelemetrySink` | `FeatureMode` (was `TelemetrySinkMode`) |
| `SlowRequestThreshold` | `Observability.SlowRequestThreshold` | unchanged |
| `SlowRequestThresholdOverrides` | `Observability.SlowRequestThresholdOverrides` | unchanged |
| `AlertRules` | `Observability.AlertRules` | unchanged |
| `LogStore` | `Observability.LogStore` | unchanged |
| `DatadogReadback` | `Observability.DatadogReadback` | unchanged |
| `EventStore` | `Stores.EventStore` | unchanged |
| `ResultStore` | `Stores.ResultStore` | unchanged |
| `ShareTokenStore` | `Stores.ShareTokenStore` | `FeatureMode` (was `ShareTokenStoreMode`) |
| `ServiceAccounts` | `Stores.ServiceAccounts` | unchanged |
| `EntityStore` | `Stores.EntityStore` | `FeatureMode` (was `EntityStoreMode`) |
| `EntityOutbox` | `Stores.EntityOutbox` | `FeatureMode` (was `EntityOutboxMode`) |
| `GraphStore` | `Stores.GraphStore` | unchanged |
| `TimeSeriesStore` | `Stores.TimeSeriesStore` | unchanged |
| `Datasets` | `Stores.Datasets` | unchanged |
| `SessionRegistry` | `Stores.SessionRegistry` | unchanged |
| `ConversationStore` | `Stores.ConversationStore` | unchanged |
| `AssetStore` | `Stores.AssetStore` | `FeatureMode` (was `AssetStoreMode`) |
| `RateLimitStore` | `Stores.RateLimitStore` | unchanged |
| `ConsentStateStore` | `Stores.ConsentStateStore` | unchanged |
| `ExternalContactStore` | `Stores.ExternalContactStore` | `FeatureMode` (was `ExternalContactStoreMode`) |
| `FactStore` | `Stores.FactStore` | `FeatureMode` (was `FactStoreMode`) |

Why each record holds what it holds: `SecurityPosture` — what the HTTP edge trusts, requires and sends back; none selects a subsystem; `RiskAcknowledgements` — the registry's `Security preflight escape hatches` category, read off each key; `MultiInstance` — meaningful only with more than one replica or process profile; `Observability` — the registry's `Logging & observability` category, plus the sinks and histories with no key; `Stores` — selects where a kind of state lives, or whether it is kept at all.

Top-level fields whose type became `FeatureMode`:

| Field | Now | Type |
|---|---|---|
| `PlatformKnowledgeBase` | `PlatformKnowledgeBase` (top level) | `FeatureMode` (was `PlatformKnowledgeBaseMode`) |
| `Lineage` | `Lineage` (top level) | `FeatureMode` (was `LineageStoreMode`) |
| `LineageDurability` | `LineageDurability` (top level) | `FeatureMode` (was `LineageDurabilityMode`) |
| `JobProgress` | `JobProgress` (top level) | `FeatureMode` (was `JobProgressMode`) |
| `DataIngestion` | `DataIngestion` (top level) | `FeatureMode` (was `DataIngestionMode`) |
| `ColumnMapping` | `ColumnMapping` (top level) | `FeatureMode` (was `ColumnMappingMode`) |
| `OAuthRefresher` | `OAuthRefresher` (top level) | `FeatureMode` (was `OAuthRefresherMode`) |
| `OAuth1a` | `OAuth1a` (top level) | `FeatureMode` (was `OAuth1aMode`) |
| `EntityGraphProjection` | `EntityGraphProjection` (top level) | `FeatureMode` (was `EntityGraphProjectionMode`) |
| `ModelFitting` | `ModelFitting` (top level) | `FeatureMode` (was `ModelFittingMode`) |
| `ModelExecution` | `ModelExecution` (top level) | `FeatureMode` (was `ModelExecutionApiMode`) |
| `ExternalCompute` | `ExternalCompute` (top level) | `FeatureMode` (was `ExternalComputeMode`) |
| `ComputeBudget` | `ComputeBudget` (top level) | `FeatureMode` (was `ComputeBudgetMode`) |
| `UsageMetering` | `UsageMetering` (top level) | `FeatureMode` (was `UsageMeteringMode`) |
| `Webhooks` | `Webhooks` (top level) | `FeatureMode` (was `WebhookMode`) |
| `AuditLog` | `AuditLog` (top level) | `FeatureMode` (was `AuditLogMode`) |
| `ConfigDriftDetection` | `ConfigDriftDetection` (top level) | `FeatureMode` (was `ConfigDriftDetectionMode`) |
| `RateLimiter` | `RateLimiter` (top level) | `FeatureMode` (was `RateLimiterMode`) |
| `SmokeTest` | `SmokeTest` (top level) | `FeatureMode` (was `SmokeTestMode`) |
| `MediaLibrary` | `MediaLibrary` (top level) | `FeatureMode` (was `MediaLibraryMode`) |
| `ConsentAudit` | `ConsentAudit` (top level) | `FeatureMode` (was `ConsentAuditMode`) |
| `AdAnalytics` | `AdAnalytics` (top level) | `FeatureMode` (was `AdAnalyticsMode`) |
| `UserSchemaAuthoring` | `UserSchemaAuthoring` (top level) | `FeatureMode` (was `UserSchemaAuthoringMode`) |
| `TenantLifecycle` | `TenantLifecycle` (top level) | `FeatureMode` (was `TenantLifecycleMode`) |
| `DeploymentReadiness` | `DeploymentReadiness` (top level) | `FeatureMode` (was `DeploymentReadinessMode`) |
| `DeploymentVerification` | `DeploymentVerification` (top level) | `FeatureMode` (was `DeploymentVerificationMode`) |
| `Presence` | `Presence` (top level) | `FeatureMode` (was `PresenceMode`) |

## The mode map

| Retired type | Off case → `FeatureMode.Disabled` | On case → `FeatureMode.Enabled` |
|---|---|---|
| `PlatformKnowledgeBaseMode` | `NoPlatformKnowledgeBase` | `EnabledPlatformKnowledgeBase` |
| `LineageStoreMode` | `NoLineageStore` | `EnabledLineageStore` |
| `LineageDurabilityMode` | `NoDurableLineage` | `EnabledDurableLineage` |
| `JobProgressMode` | `NoJobProgress` | `EnabledJobProgress` |
| `ShareTokenStoreMode` | `NoShareTokenStore` | `EnabledShareTokenStore` |
| `DataIngestionMode` | `NoDataIngestion` | `EnabledDataIngestion` |
| `ColumnMappingMode` | `NoColumnMapping` | `EnabledColumnMapping` |
| `OAuthRefresherMode` | `NoOAuthRefresher` | `EnabledOAuthRefresher` |
| `OAuth1aMode` | `NoOAuth1a` | `EnabledOAuth1a` |
| `EntityStoreMode` | `NoEntityStore` | `EnabledEntityStore` |
| `EntityOutboxMode` | `NoEntityOutbox` | `EnabledEntityOutbox` |
| `EntityGraphProjectionMode` | `NoEntityGraphProjection` | `EnabledEntityGraphProjection` |
| `ModelFittingMode` | `NoModelFitting` | `EnabledModelFitting` |
| `ModelExecutionApiMode` | `NoModelExecutionApi` | `EnabledModelExecutionApi` |
| `ExternalComputeMode` | `NoExternalCompute` | `CustomExternalCompute` |
| `ComputeBudgetMode` | `NoComputeBudget` | `EnabledComputeBudget` |
| `TelemetrySinkMode` | `NoTelemetrySink` | `CustomTelemetrySink` |
| `UsageMeteringMode` | `NoUsageMetering` | `EnabledUsageMetering` |
| `MetricsEndpointMode` | `NoMetricsEndpoint` | `EnabledMetricsEndpoint` |
| `WebhookMode` | `NoWebhooks` | `EnabledWebhooks` |
| `AuditLogMode` | `NoAuditLog` | `EnabledAuditLog` |
| `AuthCookieIssuanceMode` | `NoAuthCookieIssuance` | `EnabledAuthCookieIssuance` |
| `ConfigDriftDetectionMode` | `NoConfigDriftDetection` | `EnabledConfigDriftDetection` |
| `RateLimiterMode` | `NoRateLimiter` | `EnabledRateLimiter` |
| `SmokeTestMode` | `NoSmokeTest` | `EnabledSmokeTest` |
| `AssetStoreMode` | `NoAssetStore` | `EnabledAssetStore` |
| `MediaLibraryMode` | `NoMediaLibrary` | `EnabledMediaLibrary` |
| `DeploymentReadinessMode` | `NoReadinessReport` | `EnabledReadinessReport` |
| `DeploymentVerificationMode` | `NoDeploymentVerification` | `EnabledDeploymentVerification` |
| `ConsentAuditMode` | `NoConsentAudit` | `EnabledConsentAudit` |
| `AdAnalyticsMode` | `NoAdAnalytics` | `EnabledAdAnalytics` |
| `PeerSubstrateMode` | `NoPeerSubstrate` | `EnabledPeerSubstrate` |
| `FactStoreMode` | `NoFactStore` | `EnabledFactStore` |
| `TenantLifecycleMode` | `NoTenantLifecycle` | `EnabledTenantLifecycle` |
| `UserSchemaAuthoringMode` | `NoUserSchemaAuthoring` | `EnabledUserSchemaAuthoring` |
| `ExternalContactStoreMode` | `NoExternalContactStore` | `EnabledExternalContactStore` |
| `PresenceMode` | `NoPresence` | `EnabledPresence` |

Also `Codemod.featureModes`. The snapshot projection renders a `FeatureMode` field as the retired case name,
which is how the pre-877 fixture stays the oracle.

## Verify

1. `dotnet build` — every remaining `NoXxx` / `EnabledXxx` and every flat read of a moved field is a
   compile error naming the site.
2. Boot the deployment with its existing environment and compare `--print-config` (or the composition
   manifest's sealed keys) before and after: identical.
3. If you hold `ServerConfig` values in tests, `%A` of a `FeatureMode` field prints `Disabled` / `Enabled`
   where it printed the retired case name.

## Rollback

Pin `ToolUp.*` to 0.38.x. The environment needs no change in either direction.
