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
    /// Cell texts that mean "absent" for THIS column, as the
    /// connector knows them (GA4 answers a missing dimension value with
    /// the literal `(not set)` rather than a null). Per column and per
    /// source by design: the same text in a column that declares nothing
    /// is ordinary data. `None` — what every connector without such a
    /// vocabulary supplies — means no sentinels. The declaration travels
    /// with the stored schema (`IngestedPayload.serializeSchema`) and is
    /// applied once, where a payload cell is read
    /// (`IngestedPayload.readCell`).
    AbsentSentinels: Set<string> option
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

/// Last terminal outcome of a background OAuth refresh
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

/// Token-refresh snapshot for a single OAuth data source,
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
// ─── Bound statements ──────────────────────────────────
//
// `IDataSource.Query` takes a statement as text and nothing else, so a
// caller with a value to supply had one option: render the value into the
// text. A BOUND STATEMENT carries its values beside the text, named and
// typed, for the connector's provider to bind; a value never becomes part
// of the statement. The types are identity by value (portability rule 1):
// no provider type appears, and they cross the wire as data.
//
// **One marker form.** A statement names a parameter as `@name`
// (`BoundStatement.marker`), where a name is an ASCII letter or `_`
// followed by letters, digits and `_`. A connector rewrites the markers to
// its backend's own form; the caller writes one form for every connector.

/// One parameter value, over the scalar kinds the dataset model
/// names (`DatasetDType`). `Null` carries its declared kind, because a
/// provider must be told the type of an absent value.
[<RequireQualifiedAccess>]
type BoundValue =
    | Float of float
    | Int of int64
    | Bool of bool
    | Text of string
    | Timestamp of DateTimeOffset
    | Categorical of string
    /// An absent value of the declared kind.
    | Null of kind: DatasetDType

/// A parameter: the name the statement uses it by (without the
/// `@`), and its value.
type BoundParameter = { Name: string; Value: BoundValue }

/// A statement with its parameters. The values travel beside
/// `Text` and are bound by the connector's provider, never rendered into it.
type BoundStatement = {
    /// The statement, in the connector's dialect, naming each parameter as
    /// `@name`.
    Text: string
    /// The parameters, each named once and each used by `Text`.
    Parameters: BoundParameter list
    /// How long the connector may run the statement. `None` leaves it to the
    /// connector's own default.
    Timeout: TimeSpan option
}

/// Why a bound statement was refused before it ran. Every case
/// names the parameter it is about.
[<RequireQualifiedAccess>]
type BindingRefusal =
    /// The name is not a parameter name (`BoundStatement.isValidName`).
    | InvalidName of name: string
    /// Two parameters carry the same name.
    | DuplicateName of name: string
    /// A parameter the statement text does not use.
    | UnusedParameter of name: string
    /// A marker in the statement text that no parameter supplies.
    | MissingParameter of name: string
    /// A parameter of a kind the connector declares it cannot bind.
    | UnbindableKind of name: string * kind: DatasetDType

/// Reading a bound value.
[<RequireQualifiedAccess>]
module BoundValue =

    /// The kind a value is bound as; a `Null` is its declared kind.
    let kind (value: BoundValue) : DatasetDType =
        match value with
        | BoundValue.Float _ -> DatasetDType.Float
        | BoundValue.Int _ -> DatasetDType.Int
        | BoundValue.Bool _ -> DatasetDType.Bool
        | BoundValue.Text _ -> DatasetDType.Text
        | BoundValue.Timestamp _ -> DatasetDType.Timestamp
        | BoundValue.Categorical _ -> DatasetDType.Categorical
        | BoundValue.Null kind -> kind

    /// Every kind a bound value can carry, in declaration order.
    let kinds: DatasetDType list = [
        DatasetDType.Float
        DatasetDType.Int
        DatasetDType.Bool
        DatasetDType.Text
        DatasetDType.Timestamp
        DatasetDType.Categorical
    ]

/// Building and checking a bound statement.
[<RequireQualifiedAccess>]
module BoundStatement =

    /// A statement with no parameters and no timeout.
    let ofText (text: string) : BoundStatement = {
        Text = text
        Parameters = []
        Timeout = None
    }

    /// `statement` with `name` bound to `value`, after the parameters it has.
    let bind (name: string) (value: BoundValue) (statement: BoundStatement) : BoundStatement = {
        statement with
            Parameters = statement.Parameters @ [ { Name = name; Value = value } ]
    }

    let private isLetter (c: char) =
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c = '_'

    let private isDigit (c: char) = c >= '0' && c <= '9'

    /// Whether `name` is a parameter name: an ASCII letter or `_`, then
    /// letters, digits and `_`.
    let isValidName (name: string) : bool =
        not (String.IsNullOrEmpty name)
        && isLetter name[0]
        && name |> Seq.forall (fun c -> isLetter c || isDigit c)

    /// The marker the statement text names `name` by.
    let marker (name: string) : string = "@" + name

    /// The refusals a statement earns before any connector reads it: a name
    /// that is not a parameter name, a name given twice, and a value of a
    /// kind outside `bindable`. The first refusal in parameter order. Whether
    /// the text uses each parameter is the connector's to say, because only
    /// its dialect knows where a marker can stand.
    let check (bindable: Set<DatasetDType>) (statement: BoundStatement) : Result<unit, BindingRefusal> =
        let rec go (seen: Set<string>) (parameters: BoundParameter list) =
            match parameters with
            | [] -> Ok()
            | p :: rest ->
                if not (isValidName p.Name) then
                    Error(BindingRefusal.InvalidName p.Name)
                elif seen.Contains p.Name then
                    Error(BindingRefusal.DuplicateName p.Name)
                elif not (bindable.Contains(BoundValue.kind p.Value)) then
                    Error(BindingRefusal.UnbindableKind(p.Name, BoundValue.kind p.Value))
                else
                    go (seen.Add p.Name) rest

        go Set.empty statement.Parameters

/// Rendering a `BindingRefusal`.
[<RequireQualifiedAccess>]
module BindingRefusal =

    /// A sentence naming the parameter and what is wrong with it.
    let describe (refusal: BindingRefusal) : string =
        match refusal with
        | BindingRefusal.InvalidName name -> $"'{name}' is not a parameter name"
        | BindingRefusal.DuplicateName name -> $"parameter '{name}' is given more than once"
        | BindingRefusal.UnusedParameter name -> $"parameter '{name}' is not used by the statement"
        | BindingRefusal.MissingParameter name -> $"the statement uses '@{name}' and no parameter supplies it"
        | BindingRefusal.UnbindableKind(name, kind) ->
            $"parameter '{name}' is of kind {kind}, which the connector cannot bind"