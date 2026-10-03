// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.DataSources.Common

open System
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.Transport

// ─── Phase 128 — the outbound API connector authoring pattern ────
//
// What a third-party REST API connector supplies, and nothing more: who
// the provider is, which resources it exposes, how to ask for one page of
// a resource, and how to read the answer. Everything a connector would
// otherwise re-derive — credential resolution, the HTTP call, retry and
// backoff, quota, paging, cursors — is shared machinery the connector
// composes rather than writes:
//
//   * credential  — `ExternalApi.prepare` resolves it through
//                   `ConnectorSupport.Credentials.resolve`, the same thunk
//                   every `IDataSource` uses: the ingestor's pre-resolved
//                   credential first, `ISecretStore` re-read per call
//                   otherwise. OAuth connections (Phase 10e) land their
//                   access token in that store and Phase 10h's refresher
//                   keeps it current, so a connector never sees a refresh
//                   flow;
//   * the call    — `IHttpTransport` (`HttpClientTransport` over a BCL
//                   `HttpClient` on the server host),
//                   wrapped by `OutboundRateBudget.decorate` for quota;
//   * retry       — `RetryPolicy`, as data (`PagedFetchOptions.Retry`);
//   * paging      — `PagedFetch`, over the connector's two functions;
//   * cursors     — `SyncCursor` / `IncrementalSync`, over `IEntityStore`.
//
// A connector is therefore a thin provider mapping: two pure functions and
// two lists. It carries no vendor SDK (GP 1) — the shared layer is BCL.
//
// Six-rule portability audit (GP 12):
//   1. Identity by value  — provider, endpoint and call are records of
//                           strings; no live handles cross the interface.
//   2. Async at boundary  — the only effects (credential read, HTTP) sit
//                           in `ExternalApi`, all `Async<_>`.
//   3. Retry as data      — no `OnFailure` hook on the interface: the
//                           connector returns `Result`, the policy decides.
//   4. Stateless          — `PageRequest` / `DecodePage` are pure functions
//                           of their arguments; an implementation holds no
//                           per-call state, so any instance can serve any
//                           call, before or after a restart.
//   5. No cross-shard order — per-resource paging order only.
//   6. Precision          — watermarks are opaque strings compared
//                           ordinally; a connector emits a sortable form
//                           (ISO-8601 UTC, zero-padded ids).

/// The provider, by value. `Id` is the stable label — it is also the
/// `RateLimitDescriptor.Provider` the connector's budget draws from and
/// the connector half of every sync-cursor key.
type ExternalApiProvider = { Id: string; DisplayName: string }

/// Whether a resource is re-read whole or read incrementally from a
/// high-water mark.
[<RequireQualifiedAccess>]
type SyncMode =
    /// Read the whole resource each pass.
    | FullRefresh
    /// Read only what changed since the last completed pass's watermark.
    | Incremental

/// One readable resource in the connector's catalogue.
type ExternalApiEndpoint = {
    /// Stable resource name ("contacts", "events"). Part of the cursor key.
    Name: string
    /// Operator-facing description.
    Description: string
    Sync: SyncMode
}

/// Everything one page call needs, resolved and by value.
type ExternalApiCall = {
    /// The endpoint being read (an `ExternalApiEndpoint.Name`).
    Endpoint: string
    /// The tenant scope the call runs for.
    ScopeId: string
    /// The data source's free-form connection settings — read them with
    /// `ConnectionScope.require` / `optional`.
    ConnectionScope: Map<string, string>
    /// The resolved credential (API key or OAuth access token).
    Credential: string
    /// The high-water mark the pass reads from — `None` on a full refresh
    /// or a first incremental pass.
    Watermark: string option
}

/// The connector authoring seam. Implement it once per provider; every
/// member is pure.
type IExternalApiConnector<'Row> =
    /// Who the provider is.
    abstract Provider: ExternalApiProvider
    /// The resources this connector reads.
    abstract Endpoints: ExternalApiEndpoint list
    /// The HTTP request for one page of `call.Endpoint` at `cursor` —
    /// headers included (the credential goes here). `Error` for a
    /// configuration problem; nothing is sent.
    abstract PageRequest: call: ExternalApiCall * cursor: PageCursor -> Result<HttpRequest, string>
    /// Decode a 2xx response into typed rows and the continuation.
    abstract DecodePage: call: ExternalApiCall * response: HttpResponse -> Result<Page<'Row>, string>
    /// The row's high-water mark, for incremental endpoints (`None` when
    /// the row carries none).
    abstract Watermark: row: 'Row -> string option

/// The shared machinery a connector runs on.
[<RequireQualifiedAccess>]
module ExternalApi =

    /// The catalogue entry for `endpoint`, if the connector declares it.
    let tryEndpoint (connector: IExternalApiConnector<'Row>) (endpoint: string) : ExternalApiEndpoint option =
        connector.Endpoints |> List.tryFind (fun e -> e.Name = endpoint)

    /// Resolve the call for one endpoint of the data source `ctx` names:
    /// the endpoint must be in the catalogue (`SchemaMismatch` otherwise —
    /// an operator-fixable configuration error), and the credential comes
    /// through `Credentials.resolve` (`CredentialMissing` when absent).
    let prepare
        (connector: IExternalApiConnector<'Row>)
        (secretStore: ISecretStore option)
        (ctx: DataSourceCallContext)
        (endpoint: string)
        (watermark: string option)
        : Async<Result<ExternalApiCall, IngestionError>> =
        async {
            match tryEndpoint connector endpoint with
            | None ->
                let known = connector.Endpoints |> List.map _.Name |> String.concat ", "

                return
                    Error(
                        SchemaMismatch
                            $"{connector.Provider.DisplayName} has no endpoint '{endpoint}' (declared: {known})"
                    )
            | Some declared ->
                let! credential = Credentials.resolve secretStore ctx

                return
                    credential
                    |> Result.map (fun credential -> {
                        Endpoint = declared.Name
                        ScopeId = ctx.ScopeId
                        ConnectionScope = ctx.Config.ConnectionScope
                        Credential = credential
                        Watermark =
                            match declared.Sync with
                            | SyncMode.Incremental -> watermark
                            | SyncMode.FullRefresh -> None
                    })
        }

    /// The connector's two functions for one call, as a `PagedEndpoint`.
    let endpoint (connector: IExternalApiConnector<'Row>) (call: ExternalApiCall) : PagedEndpoint<'Row> = {
        Request = fun cursor -> connector.PageRequest(call, cursor)
        Decode = fun response -> connector.DecodePage(call, response)
    }

    /// Read the whole resource (or up to the page cap) into typed rows,
    /// with any failure structured beside the rows already read.
    let fetchAll
        (options: PagedFetchOptions)
        (transport: IHttpTransport)
        (connector: IExternalApiConnector<'Row>)
        (call: ExternalApiCall)
        (start: PageCursor)
        : Async<PagedOutcome<'Row list>> =
        PagedFetch.all options transport (endpoint connector call) start

    /// Project a page failure onto the ingestion taxonomy an admin UI
    /// renders: a 401/403 or a decode / request failure is the operator's
    /// to fix (`SchemaMismatch` for shape, `SourceUnreachable` for access
    /// and infrastructure).
    let toIngestionError (connector: IExternalApiConnector<'Row>) (error: PageError) : IngestionError =
        let context =
            $"{connector.Provider.DisplayName} page {error.PageIndex} ({PageCursor.encode error.Cursor})"

        let message = $"{context}: {PageFailure.toMessage error.Failure}"

        match error.Failure with
        | PageFailure.Request _
        | PageFailure.Decode _
        | PageFailure.Stalled _ -> SchemaMismatch message
        | PageFailure.Transport _ -> SourceUnreachable message
        | PageFailure.Consume _ -> IngestionError.StorageFailure message