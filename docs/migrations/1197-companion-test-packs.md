# A companion family's tests live beside the companion

**Ships in:** the repository's test layout only. Eleven new `*.Tests` projects (`IsPackable=false`), one gate-manifest row
each, and no packable surface change. The server-tier parts and a few companions grant their internals to the new test
assemblies, as they already did to `ToolUp.DataSources.Tests`. No consumer action is needed.

**Affected:** anyone adding or editing a companion's tests, and the gate's selection (a change confined to one moved
family now reaches its own pack's sources, not the Platform pack's).

## The census

Every file of the Platform pack whose opens (or fully qualified uses) reach a companion assembly, grouped by family, on
the tree this phase started from. A family is a non-core group of `version-groups.json`. A file that reaches two families
is named with both and goes to the family whose assembly it constructs. **Test sites** is a source scan: the number of
`test` / `testCase` / `testProperty` constructors written in the file. A file whose cases come from a contract pack's
generated list shows fewer sites than cases. The exact case counts are per pack, in the next section, and they sum to
the cases the Platform pack lost.

The destination says one of three things:

- **moved**: the file left the Platform pack for the family's pack.
- **stays: an integration over …**: the file's subject is the core, and the companion is one backend among others. For
  example, `ReportSubscriptionScopeTests` runs reports on either shipped scheduler, and `PublicRenderingTests` signs in
  through OIDC. Moving such a file would drag the named SDK tiers into a companion pack whose closure must not hold them.
  It stays until its companion use is factored behind a fake. That is a separate change (see "Not done here").
- **deferred**: the file is a companion's own test and can move. This phase did not reach its family.

| File (under `src/ToolUp.Platform.Tests/`) | Family (the companion assemblies it opens) | Test sites | Destination |
|---|---|---|---|
| `Contracts/IProviderOAuthFlowContract.fs` | `ai-providers` | 8 | stays: an SDK contract pack the Platform pack defines, binding the companion |
| `InProcess/AIProviderHealthTests.fs` | `ai-providers` | 0 | deferred: family not moved in this phase |
| `InProcess/AuditCallSiteFireAndForgetTests.fs` | `ai-providers` | 3 | deferred: family not moved in this phase |
| `InProcess/GroundedNarrativeTests.fs` | `ai-providers` | 85 | stays: an integration over ArtefactSigning, ToolUp.AI.Client, ToolUp.AI.Core, ToolUp.AI.Server, ToolUp.Facts.Core, ToolUp.Facts.Presentation, ToolUp.Facts.Presentation.Server, ToolUp.Facts.Server, ToolUp.Facts.Shared, ToolUp.RAG.Server, ToolUp.Reporting.Core, ToolUp.Reporting.Server |
| `InProcess/ProviderOAuthFlowTests.fs` | `ai-providers` | 8 | deferred: family not moved in this phase |
| `InProcess/AICrossModuleAuditTests.fs` | `audit-sinks` | 33 | stays: an integration over ToolUp.AI.Client, ToolUp.AI.Core, ToolUp.AI.Server |
| `InProcess/CefAuditSinkTests.fs` | `audit-sinks` | 31 | moved to `src/AuditSinks/ToolUp.AuditSinks.Tests/` |
| `InProcess/CertificateIssuanceTransparencyTests.fs` | `audit-sinks` | 8 | stays: an integration over ArtefactSigning, ToolUp.Facts.Core, ToolUp.Facts.Server, ToolUp.Facts.Shared |
| `InProcess/ChainedAuditLedgerTests.fs` | `audit-sinks` | 41 | moved to `src/AuditSinks/ToolUp.AuditSinks.Tests/` |
| `InProcess/DatadogLogsAuditSinkTests.fs` | `audit-sinks` | 0 | moved to `src/AuditSinks/ToolUp.AuditSinks.Tests/` |
| `InProcess/DeploymentVerificationReportTests.fs` | `audit-sinks` | 77 | stays: an integration over ToolUp.AI.Server |
| `InProcess/GroundedAnswerChainJoinTests.fs` | `audit-sinks` | 11 | stays: an integration over ToolUp.AI.Client, ToolUp.AI.Core, ToolUp.AI.Server, ToolUp.Facts.Core, ToolUp.Facts.Server, ToolUp.Facts.Shared |
| `InProcess/GroundingSigningConvergenceTests.fs` | `audit-sinks` | 9 | stays: an integration over ArtefactSigning, ToolUp.Facts.Core, ToolUp.Facts.Server, ToolUp.Facts.Shared |
| `InProcess/S3ArchiveAuditSinkTests.fs` | `audit-sinks` | 0 | moved to `src/AuditSinks/ToolUp.AuditSinks.Tests/` |
| `InProcess/SplunkHecAuditSinkTests.fs` | `audit-sinks` | 0 | moved to `src/AuditSinks/ToolUp.AuditSinks.Tests/` |
| `InProcess/AuthProviderTests.fs` | `auth-providers` | 85 | deferred: family not moved in this phase |
| `InProcess/DirectoryRoleApiGrantTests.fs` | `auth-providers` | 18 | stays: an integration over ToolUp.PublicRendering |
| `InProcess/FederatedIdentitySanitisationTests.fs` | `auth-providers` | 9 | stays: an integration over InterPlatform |
| `InProcess/GatedSsrTests.fs` | `auth-providers` | 68 | stays: an integration over ToolUp.PublicRendering |
| `InProcess/GitHubAuthProviderTests.fs` | `auth-providers` | 13 | deferred: family not moved in this phase |
| `InProcess/GoogleDirectoryTests.fs` | `auth-providers` | 23 | deferred: family not moved in this phase |
| `InProcess/GoogleIdentityCompanionTests.fs` | `auth-providers` | 31 | deferred: family not moved in this phase |
| `InProcess/HttpBodyBindingTests.fs` | `auth-providers` | 15 | deferred: family not moved in this phase |
| `InProcess/LdapAuthProviderTests.fs` | `auth-providers` | 21 | deferred: family not moved in this phase |
| `InProcess/OidcAuthValidatorTimeoutTests.fs` | `auth-providers` | 7 | deferred: family not moved in this phase |
| `InProcess/OidcBearerStrategyTests.fs` | `auth-providers` | 37 | deferred: family not moved in this phase |
| `InProcess/OidcClassifyTokenTests.fs` | `auth-providers` | 11 | deferred: family not moved in this phase |
| `InProcess/OidcCoherenceValidatorTests.fs` | `auth-providers` | 37 | deferred: family not moved in this phase |
| `InProcess/OidcDiagnoseTests.fs` | `auth-providers` | 14 | deferred: family not moved in this phase |
| `InProcess/OidcJwksTtlTests.fs` | `auth-providers` | 11 | deferred: family not moved in this phase |
| `InProcess/OidcPresetsTests.fs` | `auth-providers` | 51 | deferred: family not moved in this phase |
| `InProcess/OidcRefreshPlanTests.fs` | `auth-providers` | 31 | deferred: family not moved in this phase |
| `InProcess/OidcSecondaryFlowTests.fs` | `auth-providers` | 22 | deferred: family not moved in this phase |
| `InProcess/OidcSignInContractTests.fs` | `auth-providers` | 31 | deferred: family not moved in this phase |
| `InProcess/OidcSignInSpaPrecedenceTests.fs` | `auth-providers` | 2 | deferred: family not moved in this phase |
| `InProcess/OidcStateMachineTests.fs` | `auth-providers` | 24 | deferred: family not moved in this phase |
| `InProcess/OidcTracerTests.fs` | `auth-providers` | 10 | deferred: family not moved in this phase |
| `InProcess/PasskeyAuthProviderTests.fs` | `auth-providers` | 13 | deferred: family not moved in this phase |
| `InProcess/ProviderExceptionTests.fs` | `auth-providers`, `data-sources`, `embedding-providers`, `notification-channels`, `rate-limit` | 16 | deferred: family not moved in this phase |
| `InProcess/PublicRenderingTests.fs` | `auth-providers` | 121 | stays: an integration over ToolUp.AI.Client, ToolUp.AI.Core, ToolUp.AI.Server, ToolUp.PublicRendering, ToolUp.RAG.Server |
| `InProcess/ScimProvisioningTests.fs` | `auth-providers` | 33 | deferred: family not moved in this phase |
| `InProcess/StreamAuthBothSignInsTests.fs` | `auth-providers` | 12 | deferred: family not moved in this phase |
| `InProcess/ThreatLensRegressionSuite.fs` | `auth-providers` | 37 | deferred: family not moved in this phase |
| `InProcess/DeployPlaneTests.fs` | `container-schedulers` | 15 | deferred: family not moved in this phase |
| `InProcess/ContentScannerTests.fs` | `content-scanners` | 13 | stays: an integration over ToolUp.KnowledgeBase.Core, ToolUp.KnowledgeBase.Server, ToolUp.RAG.Core, ToolUp.RAG.Server |
| `Contracts/IExternalApiConnectorContract.fs` | `data-sources` | 45 | stays: an integration over ToolUp.AI.Wire |
| `InProcess/ExternalApiConnectorBindingTests.fs` | `data-sources` | 0 | deferred: family not moved in this phase |
| `InProcess/GitHubAppFlowTests.fs` | `data-sources` | 6 | deferred: family not moved in this phase |
| `InProcess/GoogleAnalyticsTests.fs` | `data-sources` | 40 | deferred: family not moved in this phase |
| `InProcess/HttpTransportBindingTests.fs` | `data-sources` | 0 | deferred: family not moved in this phase |
| `InProcess/ParquetDatasetCodecTests.fs` | `data-sources` | 8 | deferred: family not moved in this phase |
| `InProcess/RedisEmbeddingCacheTests.fs` | `embedding-caches` | 47 | stays: an integration over ToolUp.AI.Client, ToolUp.AI.Core, ToolUp.AI.Server, ToolUp.RAG.Server |
| `InProcess/EmbeddingProviderEnvTests.fs` | `embedding-providers` | 16 | moved to `src/EmbeddingProviders/ToolUp.EmbeddingProviders.Tests/` |
| `InProcess/EmbeddingProviderKeyRotationTests.fs` | `embedding-providers` | 4 | moved to `src/EmbeddingProviders/ToolUp.EmbeddingProviders.Tests/` |
| `InProcess/HnswFidelityTests.fs` | `embedding-providers`, `vector-stores` | 6 | deferred: family not moved in this phase |
| `InProcess/IngestionBackpressureTests.fs` | `embedding-providers` | 23 | stays: an integration over ToolUp.AI.Client, ToolUp.AI.Core, ToolUp.AI.Server, ToolUp.RAG.Core, ToolUp.RAG.Server |
| `InProcess/KnowledgeScopeResetAuditTests.fs` | `embedding-providers` | 4 | stays: an integration over ToolUp.KnowledgeBase.Core, ToolUp.KnowledgeBase.Server, ToolUp.RAG.Core, ToolUp.RAG.Server |
| `InProcess/LocalEmbeddingHashingTests.fs` | `embedding-providers` | 9 | moved to `src/EmbeddingProviders/ToolUp.EmbeddingProviders.Tests/` |
| `InProcess/LocalEmbeddingScopeTests.fs` | `embedding-providers` | 35 | stays: an integration over ToolUp.RAG.Server |
| `InProcess/Phase971EmbeddingTests.fs` | `embedding-providers` | 2 | moved to `src/EmbeddingProviders/ToolUp.EmbeddingProviders.Tests/` |
| `InProcess/RagConfigValidatorExtensionTests.fs` | `embedding-providers` | 35 | stays: an integration over ToolUp.AI.Client, ToolUp.AI.Core, ToolUp.AI.Server, ToolUp.RAG.Server |
| `InProcess/PostgresEntityStoreTests.fs` | `entity-stores` | 8 | moved to `src/EntityStores/ToolUp.EntityStores.Tests/` |
| `InProcess/ExperimentSubstrateTests.fs` | `experiments` | 7 | deferred: family not moved in this phase |
| `InProcess/PostgresFactStoreTests.fs` | `fact-stores` | 35 | stays: an integration over ToolUp.Facts.Core, ToolUp.Facts.Server, ToolUp.Facts.Shared |
| `InProcess/FlagSourceTests.fs` | `feature-flag-providers` | 10 | moved to `src/FeatureFlagProviders/ToolUp.FeatureFlagProviders.Tests/` |
| `Contracts/IEdgeCacheContract.fs` | `hosts` | 0 | stays: an SDK contract pack the Platform pack defines, binding the companion |
| `InProcess/DeliveredEgressTests.fs` | `hosts`, `media` | 54 | deferred: family not moved in this phase |
| `InProcess/EdgeCacheTests.fs` | `hosts`, `media` | 78 | stays: an integration over ToolUp.PublicRendering |
| `InProcess/DurableIngestionQueueTests.fs` | `ingestion-queues` | 17 | stays: an integration over ToolUp.RAG.Core, ToolUp.RAG.Server |
| `InProcess/QuartzJobSchedulerTests.fs` | `job-schedulers` | 12 | moved to `src/JobSchedulers/ToolUp.JobSchedulers.Tests/` |
| `InProcess/ReportSubscriptionScopeTests.fs` | `job-schedulers` | 26 | stays: an integration over ArtefactSigning, ToolUp.AI.Client, ToolUp.AI.Core, ToolUp.AI.Server, ToolUp.Facts.Core, ToolUp.Facts.Server, ToolUp.Facts.Shared, ToolUp.RAG.Server, ToolUp.Reporting.Core, ToolUp.Reporting.Server |
| `Contracts/IMediaLibraryContract.fs` | `media` | 29 | moved to `src/Media/ToolUp.Media.Tests/Contracts/` |
| `InProcess/MediaLibraryTests.fs` | `media` | 111 | moved to `src/Media/ToolUp.Media.Tests/` |
| `InProcess/OtelActivitySinkTests.fs` | `metrics` | 0 | deferred: family not moved in this phase |
| `Contracts/IModelEvaluationPlanMetricsContract.fs` | `model-providers` | 8 | stays: an SDK contract pack the Platform pack defines, binding the companion |
| `Contracts/IModelFitProviderContract.fs` | `model-providers` | 16 | stays: an SDK contract pack the Platform pack defines, binding the companion |
| `Contracts/IModelScorerContract.fs` | `model-providers` | 14 | stays: an SDK contract pack the Platform pack defines, binding the companion |
| `InProcess/ComputeBudgetTests.fs` | `model-providers` | 34 | stays: an integration over InterPlatform |
| `InProcess/ModelEvaluationTests.fs` | `model-providers` | 33 | deferred: family not moved in this phase |
| `InProcess/ModelExecutionApiTests.fs` | `model-providers` | 11 | deferred: family not moved in this phase |
| `InProcess/ModelFitBatchTests.fs` | `model-providers` | 6 | deferred: family not moved in this phase |
| `InProcess/MetaWhatsAppCloudNotificationSinkTests.fs` | `notification-channels`, `webhooks` | 15 | moved to `src/NotificationChannels/ToolUp.NotificationChannels.Tests/` |
| `InProcess/RedisDistributedLockTests.fs` | `notification-channels` | 1 | moved to `src/NotificationChannels/ToolUp.NotificationChannels.Tests/` |
| `InProcess/RedisNotificationChannelHealthTests.fs` | `notification-channels` | 1 | moved to `src/NotificationChannels/ToolUp.NotificationChannels.Tests/` |
| `InProcess/RedisNotificationChannelTests.fs` | `notification-channels` | 1 | moved to `src/NotificationChannels/ToolUp.NotificationChannels.Tests/` |
| `InProcess/SendGridNotificationSinkTests.fs` | `notification-channels` | 0 | moved to `src/NotificationChannels/ToolUp.NotificationChannels.Tests/` |
| `InProcess/SmtpNotificationSinkTests.fs` | `notification-channels` | 0 | moved to `src/NotificationChannels/ToolUp.NotificationChannels.Tests/` |
| `InProcess/TwilioNotificationSinkTests.fs` | `notification-channels` | 0 | moved to `src/NotificationChannels/ToolUp.NotificationChannels.Tests/` |
| `InProcess/TwilioWhatsAppNotificationSinkTests.fs` | `notification-channels` | 25 | moved to `src/NotificationChannels/ToolUp.NotificationChannels.Tests/` |
| `InProcess/WebPushNotificationSinkTests.fs` | `notification-channels` | 0 | moved to `src/NotificationChannels/ToolUp.NotificationChannels.Tests/` |
| `InProcess/OcrProviderTests.fs` | `ocr-providers` | 36 | stays: an integration over ToolUp.KnowledgeBase.Core, ToolUp.KnowledgeBase.Server, ToolUp.RAG.Core, ToolUp.RAG.Server |
| `Contracts/IRateLimitStoreContract.fs` | `rate-limit` | 9 | stays: an SDK contract pack the Platform pack defines, binding the companion |
| `InProcess/InMemoryRateLimitStoreTests.fs` | `rate-limit` | 12 | deferred: family not moved in this phase |
| `InProcess/AwsSecretsManagerSecretStoreTests.fs` | `secrets` | 1 | moved to `src/Secrets/ToolUp.Secrets.Tests/` |
| `InProcess/AzureKeyVaultSecretStoreTests.fs` | `secrets` | 1 | moved to `src/Secrets/ToolUp.Secrets.Tests/` |
| `InProcess/GcpSecretManagerSecretStoreTests.fs` | `secrets` | 4 | moved to `src/Secrets/ToolUp.Secrets.Tests/` |
| `InProcess/HashiCorpVaultSecretStoreTests.fs` | `secrets` | 1 | moved to `src/Secrets/ToolUp.Secrets.Tests/` |
| `InProcess/ServerAppSecretStoreTests.fs` | `secrets` | 13 | moved to `src/Secrets/ToolUp.Secrets.Tests/` |
| `InProcess/RevokeOnIssuerRemovedTests.fs` | `share-token-store-decorators` | 5 | deferred: family not moved in this phase |
| `InProcess/CrossIndexErasureConformanceTests.fs` | `sparse-indices` | 17 | stays: an integration over ToolUp.RAG.Core, ToolUp.RAG.Server |
| `InProcess/PostgresFullTextIndexTests.fs` | `sparse-indices`, `vector-stores` | 15 | stays: an integration over ToolUp.AI.Client, ToolUp.AI.Core, ToolUp.AI.Server, ToolUp.RAG.Core, ToolUp.RAG.Server |
| `InProcess/SparseIndexCompositionTests.fs` | `sparse-indices`, `vector-stores` | 36 | stays: an integration over ToolUp.AI.Client, ToolUp.AI.Core, ToolUp.AI.Server, ToolUp.RAG.Core, ToolUp.RAG.Server |
| `RAG/SparseAnalyzerContract.fs` | `sparse-indices` | 22 | stays: an integration over ToolUp.RAG.Core, ToolUp.RAG.Server |
| `InProcess/AwsS3StorageTests.fs` | `storage` | 1 | moved to `src/Storage/ToolUp.Storage.Tests/` |
| `InProcess/AzureBlobStorageTests.fs` | `storage` | 1 | moved to `src/Storage/ToolUp.Storage.Tests/` |
| `InProcess/BlobStorageAuthAuditTests.fs` | `storage` | 22 | moved to `src/Storage/ToolUp.Storage.Tests/` |
| `InProcess/ConditionalBlobStorageTests.fs` | `storage` | 1 | moved to `src/Storage/ToolUp.Storage.Tests/` |
| `InProcess/GoogleCloudStorageTests.fs` | `storage` | 2 | moved to `src/Storage/ToolUp.Storage.Tests/` |
| `InProcess/TelemetrySinkTests.fs` | `telemetry-sinks` | 13 | deferred: family not moved in this phase |
| `InProcess/TimescaleTimeSeriesStoreTests.fs` | `time-series-stores` | 1 | moved to `src/TimeSeriesStores/ToolUp.TimeSeriesStores.Tests/` |
| `Contracts/IDataSubjectRequestContract.fs` | `vector-stores` | 32 | stays: an SDK contract pack the Platform pack defines, binding the companion |
| `InProcess/HnswVectorStoreTests.fs` | `vector-stores` | 12 | stays: an integration over ToolUp.RAG.Server |
| `InProcess/PgvectorVectorStoreTests.fs` | `vector-stores` | 81 | deferred: family not moved in this phase |
| `InProcess/WebhookSubstrateTests.fs` | `webhooks` | 8 | moved to `src/Webhooks/ToolUp.Webhooks.Tests/` |
| `InProcess/WorkflowEngineTests.fs` | `workflow` | 6 | deferred: family not moved in this phase |

## The packs

| Pack | Project | Hosts | Cases | Live arms (Pending without the variable) |
|---|---|---|---|---|
| `Webhooks` | `src/Webhooks/ToolUp.Webhooks.Tests/` | none: the family implements no SDK contract pack | 8 | — |
| `JobSchedulers` | `src/JobSchedulers/ToolUp.JobSchedulers.Tests/` | `IJobSchedulerContract`, `IJobStoreContract` over Quartz | 68 | — |
| `NotificationChannels` | `src/NotificationChannels/ToolUp.NotificationChannels.Tests/` | `INotificationChannelContract` over the Redis channel; `INotificationSinkContract` over SMTP, SendGrid, Twilio SMS, Twilio WhatsApp, Meta WhatsApp Cloud and WebPush; `IDistributedLockContract` over the Redis lock | 67 | `TOOLUP_REDIS_CONNECTION` |
| `AuditSinks` | `src/AuditSinks/ToolUp.AuditSinks.Tests/` | `IAuditSinkContract` over CEF, the chained ledger, Datadog Logs, the S3 archive and Splunk HEC | 107 | — |
| `Storage` | `src/Storage/ToolUp.Storage.Tests/` | `IBlobStorageContract`, `IConditionalBlobStorageContract` over AWS S3, Azure Blob and Google Cloud Storage | 103 | `TOOLUP_AWS_S3_BUCKET`, `TOOLUP_AZURE_STORAGE_CONNECTION_STRING`, `TOOLUP_GCS_BUCKET` |
| `Secrets` | `src/Secrets/ToolUp.Secrets.Tests/` | `ISecretStoreContract` over AWS Secrets Manager, Azure Key Vault, GCP Secret Manager and HashiCorp Vault | 22 | `TOOLUP_AWS_SECRETS_REGION`, `TOOLUP_AZURE_KEY_VAULT_URL`, `TOOLUP_GCP_PROJECT_ID`, `VAULT_ADDR` / `VAULT_TOKEN` |
| `EmbeddingProviders` | `src/EmbeddingProviders/ToolUp.EmbeddingProviders.Tests/` | none: the cases are each provider's own | 31 | — |
| `EntityStores` | `src/EntityStores/ToolUp.EntityStores.Tests/` | `IEntityStoreContract` over Postgres | 1 | `TOOLUP_TEST_POSTGRES` |
| `TimeSeriesStores` | `src/TimeSeriesStores/ToolUp.TimeSeriesStores.Tests/` | the Timescale store's live arm | 1 | `TOOLUP_TIMESCALE_CONN` |
| `FeatureFlagProviders` | `src/FeatureFlagProviders/ToolUp.FeatureFlagProviders.Tests/` | the OpenFeature provider's own cases | 10 | — |
| `Media` | `src/Media/ToolUp.Media.Tests/` | `IMediaLibraryContract`, which moved with the family: it is written over the media library's own types and nothing in the Platform pack bound it | 161 | — |

The case counts exclude each pack's two registration-guard cases and include its Pending cases. Measured on this tree,
the Platform pack listed 15,816 cases before and 15,237 after, and the eleven packs list 579. Nothing was lost or
duplicated. One label path changed: the embedding-provider environment cases were registered inside the Platform pack's
sequenced `env-mutating config validators` group, and they now run at the top of their own sequenced pack. The guard of
every new pack has a floor of 0. A pack that source-links a Platform file carrying a `[<Tests>]` binding exempts that
binding by name, because its cases are the Platform pack's. `NotificationChannels` links `TransactionalDispatcherTests`
for its capturing audit log.

**An env-gated arm is Pending, never Failed.** Each arm without its variable registers one `skiptest`, which Expecto
lists as `P …skipped — <VAR> not set`. Each row declares its variables, so the scrubbed environment `VerifyAll` launches
the pack under keeps them. The variables leave the Platform row wherever no remaining Platform source reads them. A pack
whose every case is Pending still exits 0. `Gate.verdictOfExit` reads that as `PASS — <pack>`, because an Expecto pack
reports only an exit code. The Pending cases show in the pack's own summary (`N ignored`), not in the `VerifyAll` block.

**How a pack hosts its contract.** Each contract pack is source-linked from `src/ToolUp.Platform.Tests/Contracts/`
(`<Compile Include="..\..\ToolUp.Platform.Tests\Contracts\X.fs" Link="Contracts\X.fs" />`), so there is one copy of
each law and nothing to drift. A pack's project references its family, the substrate (`ToolUp.Platform.Core` and the
`ToolUp.Platform.Server` umbrella), and whatever the linked contract pack's own closure needs. The companions themselves
reference the umbrella, so naming a part would not narrow the closure. Two contract packs reach past the server tier.
`IJobSchedulerContract`'s carried-scope recompute law constructs `ToolUp.Facts`, and `IConditionalBlobStorageContract`'s
re-ingest law constructs the knowledge base. So `JobSchedulers` references `ToolUp.Facts.Server`, and `Storage`
references the knowledge-base, RAG and Facts server tiers. Phase 1100 moves the contract packs into
`ToolUp.Platform.Testing`, and that is where those laws' dependencies can be cut.

**The drift check.** Where a copy cannot be avoided (a template that ships to consumers cannot link into this
repository), the Build pack's `vendored contract copies` list compares the copy with its source up to the laws /
self-test boundary. It covers every contract-named file under a `Contracts/` directory outside
`ToolUp.Platform.Tests`, in `src/`, `templates/`, `tests/` and `tools/`. The finding names the pack and the law that
drifted. A copy with no registered boundary is red, and the remedy it names is a source link. Today the only copy is the
packaged-module template's `ModuleContract.fs`, which the template gate also compares.

## Families with no pack

- **No Platform cases of their own** (no pack, and none is needed until a case exists): `calendar`, `cloud`,
  `encryption`, `observability`, `rerankers`, `voice-providers`. `algorithm-providers`, `artefact-signing`,
  `report-renderers`, `svg-rasterizers` and `stripe` already have packs of their own.
- **Deferred families** (their own files are marked deferred above): `ai-providers` and `data-sources` (each into its
  existing pack, `ToolUp.AIProviders.Tests` / `ToolUp.DataSources.Tests`), `auth-providers` (the largest: OIDC, LDAP,
  passkeys, SCIM, GitHub and Google), `container-schedulers`, `experiments`, `hosts`, `metrics`, `model-providers`,
  `rate-limit`, `share-token-store-decorators`, `vector-stores`, `workflow`. `ProviderExceptionTests` reaches five
  families and constructs all five, so it goes to none of them whole. It splits by family when those families move.

## Not done here

- The deferred families above. The mechanism is the same for each: move the file, source-link its closure, add a row,
  and grant internals where the cases read them.
- The **stays** files. Each is an integration of a core tier with one companion backend. Its companion use has to be
  factored behind a fake before the Platform pack can stop referencing that companion. Until then the Platform pack keeps
  the references those files need.

## Verification

- `dotnet build ToolUp.Forge.sln` and each new pack's dll run whole, green.
- The Platform pack's registration guard is green after each family's move, and the case sums above hold.
- `dotnet run --project Build.fsproj -- Gate --check` is clean (`EXPECTED_PACKS` and the CI table are the
  `Gate --render` of the new rows), and the Build pack is green, including `GateReconcile` and `Census --check`.
- The drift check was shown red once, by perturbing the template's copy inside `lawWireTypeNameUnique`. It named
  `templates/platformsdk-module-packaged/tests/MyModule.Tests` and the law. The copy was then restored.

**Rollback:** revert the phase's commits. Each family's move is one commit, except the five single-file families, which
share one.
