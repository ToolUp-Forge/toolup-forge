// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Data ingestion substrate ──────────────────────────────────────
//
// Shared-layer types for the SDK's external-data-source ingestion
// substrate. Lives in the Fable-compatible `Shared/` layer so the
// admin-UI client can render `DataSourceConfig` / `IngestionRun`
// records the server persists, and so module authors can describe
// data-source configurations from any project that depends on
// `ToolUp.Platform`.
//
// **Design constraint — the AI provider's secret-store thunk pattern
// is the model for credential management.** Connector implementations
// take an `ISecretStore` constructor argument and resolve credentials
// per-call via `GetSecret(scopeId, credentialKey)`. This supports
// rotation without provider reconstruction; no connection string is
// ever embedded in the persisted `DataSourceConfig`.

/// Stable per-scope identifier for a configured data source. Plain
/// `string` (identity by value, portability rule 1) so the wire shape
/// stays portable across in-process and any future distributed config
/// store. Modules choose their own naming convention — `"sales-bq"`,
/// `"hr-redshift"`, etc.
type DataSourceId = string

/// Column metadata returned by `IDataSource.GetSchema`. Mirrors the
/// shape of `DataTypeColumn` (the processed-data catalog) so admin UIs
/// can render data-source schemas with the same component used for
/// processed-data tables.
type ColumnInfo = {
    Name: string
    DataType: string
    Nullable: bool
}

/// Schema for one source-side table — what `IDataSource.GetSchema`
/// returns when the connector knows the column shape ahead of an
/// actual query. Connectors that cannot introspect (REST APIs
/// against undocumented endpoints) return `Columns = []`.
type TableSchema = {
    TableName: string
    Columns: ColumnInfo list
}

/// Persisted data-source configuration. One blob per source under
/// `_platform/data-sources/{scopeId}/{sourceId}.json`. The
/// `CredentialKey` names a key in `ISecretStore` — the actual
/// credential value is never stored in this record (rotation-safe;
/// the secret store is the source of truth).
type DataSourceConfig = {
    /// Caller-chosen stable identifier — must be unique within a scope.
    Id: DataSourceId
    /// Human-readable name shown in the admin UI.
    Name: string
    /// Connector type discriminator — `"BigQuery"`, `"Redshift"`,
    /// `"Synapse"`, `"InMemory"`, etc. The `IDataIngestor` resolves
    /// the matching `IDataSource` implementation by this value.
    Kind: string
    /// Connection-level configuration as a free-form key-value map.
    /// Connectors interpret it (`"project_id"` for BigQuery, `"host"`
    /// + `"port"` for Redshift). Never includes credentials.
    ConnectionScope: Map<string, string>
    /// Key into `ISecretStore` under the caller's scope. The
    /// connector reads `secretStore.GetSecret(scopeId, this)` per
    /// call (mirrors the AI provider thunk pattern).
    CredentialKey: string
    /// Optional fixed table list. `None` means "discoverable via
    /// `ListTables`"; `Some` constrains ingestion to the listed
    /// tables (admin UI hint, not an enforcement).
    Tables: string list option
    /// Free-form metadata the ingestor ignores. Admin UIs and audit
    /// trails read it.
    Tags: Map<string, string>
}

/// Lifecycle status of a single ingestion attempt. `Pending` is
/// the brief window between scheduler dispatch and the connector
/// `Connect` call returning. `Running` covers the actual `Query`
/// execution. `Succeeded` / `Failed` are terminal.
type IngestionStatus =
    | Pending
    | Running
    | Succeeded
    | Failed

/// Why an ingestion attempt failed. Distinguishes operator-fixable
/// errors (credential missing) from infrastructure errors (storage
/// unreachable). Admin UIs render the cases differently.
type IngestionError =
    /// `ISecretStore.GetSecret` returned `None` for the configured
    /// `CredentialKey`. Operator action: rotate / re-supply the
    /// credential.
    | CredentialMissing of credentialKey: string
    /// Connector reached the source but the source returned an
    /// error or refused the connection. Inner message captures the
    /// connector's diagnostic.
    | SourceUnreachable of message: string
    /// Connector queried the source successfully but the response
    /// shape did not match expectations. E.g. a BigQuery query
    /// returning columns the table no longer has.
    | SchemaMismatch of message: string
    /// `IDataObjectStore.Save` failed (disk full, cloud throttling,
    /// versioning policy conflict). Wraps the inner message.
    | StorageFailure of message: string
    /// Catch-all for unexpected exceptions. Connectors that throw
    /// instead of returning `Result` get caught by the ingestor and
    /// wrapped here.
    | UnexpectedFailure of message: string

/// Status of a data source's credentials, surfaced to admin-UI clients
/// through `IDataIngestionApi.GetCredentialStatus`. Generic across
/// credential shapes — applies to OAuth flows, service-account JSON
/// blobs, and bearer-token connectors uniformly. Admin UIs render each
/// case differently (red badge for `NotConfigured`, "Connect" button
/// for `NeedsAuthorization`, green badge with timestamp for `Connected`,
/// "Reconnect required" banner for `NeedsReauthorization`).
type CredentialStatus =
    /// No credential has been supplied yet — connector cannot be used.
    /// Admin UI prompts the user to enter or upload credentials.
    | NotConfigured
    /// For OAuth-style flows: client identity has been entered but
    /// the user has not yet completed the upstream consent flow. The
    /// admin UI shows a "Connect" button bouncing through
    /// `IDataIngestionApi.BeginOAuth`.
    | NeedsAuthorization
    /// Credentials are present and last verified at `connectedAt`. For
    /// OAuth: a refresh token is stored. For service-account: the JSON
    /// is in `ISecretStore`. Admin UI shows a connected status and a
    /// "Disconnect" button.
    | Connected of connectedAt: DateTime
    /// Credentials existed but the upstream provider revoked them
    /// (refresh-token rotation, user-initiated revocation, expiry
    /// after long inactivity). Admin UI surfaces `reason` and prompts
    /// re-authorisation.
    | NeedsReauthorization of reason: string

/// Phase 10h — last terminal outcome of a background OAuth refresh
/// attempt, surfaced to the admin-UI Token-status column. One case per
/// `OAuthRefreshResult` terminal tag — the `Refreshed` case carries the
/// freshly-minted access-token expiry so the UI can render "valid
/// until …"; the failure cases carry the verbatim reason from the
/// matching audit payload.
type OAuthRefreshOutcome =
    /// Most recent terminal outcome was a successful refresh; the
    /// substrate persisted a new access token expiring at `newExpiry`.
    | RefreshedOutcome of newExpiry: DateTime
    /// Most recent terminal outcome was a recoverable failure pending
    /// the next scheduled retry. `reason` is the verbatim
    /// `OAuthTokenRefreshFailedPayload.Reason`.
    | TransientErrorOutcome of reason: string
    /// Upstream rejected the refresh token (`invalid_grant` or
    /// equivalent). `reason` is the verbatim
    /// `OAuthRefreshTokenInvalidatedPayload.Reason`.
    | RequiresReauthOutcome of reason: string
    /// Background refresh exhausted its `JobRetryPolicy.MaxAttempts`
    /// and dead-lettered. `finalReason` is the verbatim
    /// `OAuthRefreshDeadLetteredPayload.FinalReason`.
    | DeadLetteredOutcome of finalReason: string

/// Phase 10h — token-refresh snapshot for a single OAuth data source,
/// surfaced to the admin-UI Token-status column.
/// `IDataIngestionApi.GetTokenStatus` returns `None` when no
/// `IOAuthTokenRefresher` descriptor is registered for the source (the
/// connector is not OAuth-shaped, or its `Connect` path has not yet
/// registered the descriptor); the column renders a neutral "—" in
/// that case. Generic across providers — identity by value (the
/// descriptor's `Provider` string), no live handles.
type TokenStatus = {
    /// Stable provider name (the descriptor's `Provider`, matching
    /// `IOAuthCredentialFlow.Name`). Surfaced alongside the outcome so
    /// the operator can disambiguate when one data source maps to a
    /// less-obvious provider name.
    Provider: string
    /// Most recent terminal outcome. `None` when the descriptor was
    /// registered but no terminal `_platform.oauth.refresh` audit row
    /// exists yet (first registration, before the first scheduled
    /// dispatch fires).
    LastOutcome: OAuthRefreshOutcome option
    /// Wall-clock instant of `LastOutcome`'s audit row. `None` when
    /// `LastOutcome` is `None`.
    LastOutcomeAt: DateTime option
    /// Wall-clock instant of the next scheduled background refresh —
    /// derived from the most recent `OAuthTokenRefreshed.NewExpiry`
    /// minus the descriptor's `ScheduleAheadOfExpiry`. `None` when no
    /// successful refresh has been observed yet (the scheduler still
    /// dispatches every minute and short-circuits until expiry is
    /// known, but there is no specific instant to render).
    NextScheduledRefresh: DateTime option
}

/// Per-attempt history row. Persisted alongside the source config
/// under `_platform/data-sources/{scopeId}/runs/{sourceId}/{runId}.json`.
/// The job-run history pattern: one blob per attempt, ISO-
/// ordered timestamp prefix so newest-first listing is a cheap
/// `List + sortDescending + truncate count`.
type IngestionRun = {
    RunId: Guid
    DataSourceId: DataSourceId
    ScopeId: string
    Table: string
    StartedAt: DateTime
    CompletedAt: DateTime option
    Status: IngestionStatus
    /// Number of rows / records the connector returned. `0` for
    /// failed attempts; `None` only when the connector cannot count.
    RowsIngested: int64 option
    /// `IDataObjectStore` object id where the result bytes landed.
    /// `None` when the ingestion failed before reaching storage.
    ResultObjectId: string option
    /// Set for terminal-Failed runs. Includes the discriminator + a
    /// human-readable message so admin-UI renderers can branch.
    Error: IngestionError option
    /// Optional caller-supplied `JobId` if the ingestion was
    /// triggered through `IJobScheduler.TriggerOnce` or a scheduled
    /// `Trigger.CronTrigger`. Lets admin UIs cross-link from a
    /// job's history to the matching ingestion run.
    JobId: Guid option
}