// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── DataIngestionApi (Fable.Remoting wire surface) ──────────────
//
// Client-callable API for managing data sources, triggering refreshes,
// and inspecting ingestion history through the SDK's data-ingestion
// substrate. Lives in the shared layer so a Fable client
// (admin UI module, future) can call it via ToolUp.Remoting.
//
// **Scope discipline.** Every method validates the caller's resolved
// `AccessContext`'s `configScope` server-side. Callers cannot pass an
// arbitrary scope; the handler reads the caller's scope from
// `ScopeResolutionMiddleware` and ignores any wire-side scope hint.
//
// **Permission gating.** Read methods (`ListDataSources`, `GetDataSource`,
// `ListRecentRuns`) are available to any team member. Write methods
// (`SaveDataSource`, `DeleteDataSource`, `TriggerRefresh`) require
// Owner / Admin in `Team` / `MultiTeam` mode (via
// `TeamRoles.canWriteTeamConfig`); other modes are ungated.

type IDataIngestionApi = {
    /// List every data-source config in the caller's resolved scope.
    /// Empty when `DataIngestion` is disabled for the deployment, or
    /// when no sources have been configured.
    ListDataSources: unit -> Async<DataSourceConfig list>

    /// Read one data-source config by id. `None` for unknown ids —
    /// does not throw.
    GetDataSource: DataSourceId -> Async<DataSourceConfig option>

    /// Persist a data-source config. Idempotent — re-saving the same
    /// `Id` overwrites. Owner / Admin only in Team mode.
    SaveDataSource: DataSourceConfig -> Async<Result<unit, string>>

    /// Delete a data-source config (and stop scheduled refreshes
    /// against it). Does not delete the persisted `IngestionRun`
    /// history — that remains for audit. Owner / Admin only.
    DeleteDataSource: DataSourceId -> Async<Result<unit, string>>

    /// Trigger an immediate refresh of one table from a data source.
    /// First tuple element is the source id; second is the table name.
    /// Schedules a `Manual`-trigger `JobRegistration` with the
    /// scheduler and returns the assigned `JobId`. The actual
    /// ingestion runs asynchronously on the scheduler's worker;
    /// admin UIs poll `ListRecentRuns` for completion.
    /// Owner / Admin only in Team mode.
    TriggerRefresh: DataSourceId * string -> Async<Result<System.Guid, string>>

    /// Read the most recent N ingestion-run rows for a data source.
    /// First tuple element is the source id; second is the count
    /// (capped server-side at 50). Newest first.
    ListRecentRuns: DataSourceId * int -> Async<IngestionRun list>

    /// Start an OAuth Authorization Code flow for the
    /// given data source. Returns the absolute URL the client should
    /// `window.location.href` to. Server validates Owner/Admin (in
    /// `Team` / `MultiTeam` mode), the data source's existence, and
    /// the OAuth flow's registration before returning the URL —
    /// callers see a synchronous error rather than discovering them
    /// after the upstream redirect. Tuple shape: (`dataSourceId`,
    /// `flowName`). The `flowName` matches `IOAuthCredentialFlow.Name`
    /// (e.g. `"google-analytics"`) and is contributed by the
    /// connector companion's credential-UI plugin.
    BeginOAuth: DataSourceId * string -> Async<Result<string, string>>

    /// Read the credential status for a data source.
    /// Returns `NotConfigured` when no `DataSourceConfig` is saved,
    /// `NeedsAuthorization` when the config exists but no successful
    /// OAuth callback has fired, `Connected of connectedAt` when a
    /// refresh token is in place, or `NeedsReauthorization` when the
    /// last refresh attempt was rejected upstream. Generic across
    /// credential shapes (OAuth, service-account, bearer-token); the
    /// admin UI dispatches on the case.
    GetCredentialStatus: DataSourceId -> Async<CredentialStatus>

    /// Disconnect a data source. Best-effort revokes the
    /// refresh token upstream (when the flow supports revocation),
    /// deletes the refresh token from `ISecretStore`, removes the
    /// credential metadata blob, and emits an `OAuthDisconnected`
    /// audit event. The `DataSourceConfig` is preserved — operators
    /// re-authorise rather than re-create. Owner / Admin only in
    /// `Team` / `MultiTeam` mode.
    Disconnect: DataSourceId -> Async<Result<unit, string>>

    /// Phase 10h — read the OAuth-refresh token status for a data
    /// source. Returns `None` when no `IOAuthTokenRefresher` descriptor
    /// is registered for the source under the caller's scope (the
    /// connector is not OAuth-shaped, or its `Connect` path has not
    /// yet registered a descriptor). Returns `Some TokenStatus` with
    /// the most recent terminal outcome + next scheduled refresh
    /// derived from the `_platform.oauth.refresh` audit family
    /// otherwise. Read-only — no permission gate beyond scope
    /// resolution.
    GetTokenStatus: DataSourceId -> Async<TokenStatus option>
}