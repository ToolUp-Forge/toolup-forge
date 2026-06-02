# BigQuery connector (Phase 10 — implementation deferred)

This directory is reserved for the BigQuery connector — the first concrete `IDataSource` implementation that the SDK's data-ingestion substrate will host. The substrate itself (interfaces, in-memory connector, orchestrator, scheduled-ingestion handler, admin API) shipped under `Phase 10 (1/4)..(4/4)`. The actual BigQuery integration is the implementation phase that follows.

## Why this is a placeholder, not a stub `.fsproj`

Same rationale as `src/JobScheduler/Akka/README.md`. An empty F# project listed in both solutions would be misleading clutter:

- It compiles to nothing useful (no working integration).
- Its `paket.references` would advertise `Google.Cloud.BigQuery.V2` dependencies that aren't actually exercised against a live BigQuery dataset, polluting the dependency graph.
- New SDK contributors would see "BigQuery support exists" in the solution view and assume cloud ingestion works.

A README placeholder records the design intent. When the implementation lands, this README becomes the design rationale embedded in the companion's actual source.

## Planned file layout

Mirrors `src/AIProviders/Claude/` (the structural template for an external-cloud connector):

```
src/DataSources/BigQuery/
├── BigQueryDataSource.fsproj           Meta-project (no DLL output)
├── paket.references                    FSharp.Core, Google.Cloud.BigQuery.V2,
│                                       Google.Apis.Auth (for service-account creds)
├── BigQueryDataSource.Server.props     Injects BigQueryDataSource.fs into
│                                       _ToolUpPlatformServerSources
├── BigQueryDataSource.fs               IDataSource implementation
└── README.md                           This file (replaced with usage notes when impl lands)
```

The consuming `ToolupApp-Server.fsproj` will add a `<ProjectReference>` to this companion and import the `.Server.props` — same pattern used today by `src/AIProviders/Claude/ClaudeAIProvider.Server.props` and `src/EmbeddingProviders/OpenAI/OpenAIEmbeddingProvider.Server.props`.

## Credential storage convention

BigQuery service-account credentials are JSON blobs (the contents of a downloaded `service-account.json` file from GCP IAM). The connector reads them through the SDK's `ISecretStore`:

- **Scope:** the team's storage scope (`team-{teamId}` in Team / MultiTeam mode, `user-{userId}` in Individual mode).
- **Key:** `bigquery_service_account_json` (or whatever `DataSourceConfig.CredentialKey` names — admin chooses).
- **Value:** the full JSON blob, stored encrypted at rest via `EncryptedSecretStore` when `TOOLUP_SECRETS_MASTER_KEY` is configured.

The orchestrator pre-resolves the credential and hands it to the connector via `DataSourceCallContext.Credential`. The connector parses the JSON, builds a `GoogleCredential`, and constructs a `BigQueryClient` per call (clients are cheap; reuse is a follow-up optimisation if profiling shows pressure).

## What gates the actual implementation

Before this directory can contain working code, the implementation session must have:

1. **A real Google Cloud project + service account.** Service-account JSON file with `roles/bigquery.dataViewer` + `roles/bigquery.jobUser` at minimum. A test dataset with at least one populated table to verify the round-trip.
2. **`Google.Cloud.BigQuery.V2` package compatibility verified.** The SDK targets `net10.0`; some older Google Cloud .NET packages may lag behind. A trivial `Connect → Query` smoke test against the real dataset is the load-bearing prerequisite.
3. **`IDataSourceContract` pack passes.** The connector must bind to and pass `src/ToolUp.Platform.Tests/Contracts/IDataSourceContract.fs` — the same pack the in-memory connector passes today. Connector-specific behaviour (BigQuery dialect SQL parsing, `INFORMATION_SCHEMA.COLUMNS` for `GetSchema`) is tested separately in env-gated integration tests, mirroring `AzureBlobStorageTests.fs` / `AwsS3StorageTests.fs` / `GoogleCloudStorageTests.fs` which only run when their respective bucket / connection-string env vars are set.
4. **Cost-control documentation.** BigQuery bills per byte scanned. The connector docs should warn about full-table scans on large datasets and recommend `LIMIT` clauses for testing.

## What lives where today

| Concern | File | Status |
|---|---|---|
| `IDataSource` interface contract | `src/ToolUp.Platform/Server/IDataSource.fs` | Shipped (Phase 10 (1/4)) |
| `DataSourceConfig` shape (incl. `Kind`, `CredentialKey`) | `src/ToolUp.Platform/Shared/DataIngestionTypes.fs` | Shipped |
| `IDataSourceContract` test pack | `src/ToolUp.Platform.Tests/Contracts/IDataSourceContract.fs` | Shipped (7 tests) |
| In-process orchestrator | `src/ToolUp.Platform/Server/DataIngestor.fs` | Shipped (binds connectors by `Kind`, resolves credentials, writes through `IDataObjectStore` with `Versioned`) |
| Scheduled-ingestion job handler | `src/ToolUp.Platform/Server/DataIngestionJobHandler.fs` | Shipped (registered against `IJobScheduler` at compose time) |
| ToolUp.Remoting admin API | `src/ToolUp.Platform/Server/DataIngestionApiHandler.fs` | Shipped (Owner/Admin write gate, Manual-trigger schedule) |
| In-memory test connector | `src/ToolUp.Platform/Server/InMemoryDataSource.fs` | Shipped (Kind = "InMemory"; serves the test pack + dev harness) |
| BigQuery connector | This directory | **Deferred** — needs GCP credentials |
| Redshift / Athena / Synapse connectors | Reserved at `src/DataSources/<Name>/` (not yet created) | **Deferred** — same dependency on real cloud accounts |
| Admin UI module | A future `src/ToolUp.Platform/Client/DataIngestionUI.fs` | **Deferred** — pairs with the shipped `IDataIngestionApi` ToolUp.Remoting surface |

## Don't ship working BigQuery code without testing it

The interface contract is strict (`IDataSource` returns `Result<_, IngestionError>` for every method). Connector-specific bugs surface only against real datasets — credential rotation under load, `INFORMATION_SCHEMA` introspection across legacy / Standard SQL, BigQuery's quotas, query-cost surprises. Silently shipping a BigQuery connector that has never run against a real dataset would be the worst kind of code: looks correct, fails on first real ingestion. This README is the contract that says "interface is ready; integration is the next session's work."
