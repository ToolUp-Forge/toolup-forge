// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.GoogleAnalyticsLiveTransport

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open Google
open Google.Analytics.Data.V1Beta
open Google.Apis.Auth.OAuth2
open Google.Apis.Auth.OAuth2.Flows
open Google.Apis.Auth.OAuth2.Responses
open Google.Apis.GoogleAnalyticsAdmin.v1beta
open Google.Apis.Services
open Google.Protobuf
open Grpc.Core
open ToolUp.Platform
open ToolUp.DataSources.GoogleAnalyticsDataSource

// ─── The real GA4 network layer ──────────────────────────────────────
//
// Implements `GoogleAnalyticsTransport` over Google's client libraries:
// `Google.Apis.Auth` for the token mint, the Analytics Admin API for
// property discovery, and the Data API for reports.
//
// **This file is the only place a vendor type appears.** Everything
// above it — credential resolution, request interpretation, error
// surfaces — is ordinary F# over SDK types, which is what lets the
// contract pack exercise the connector without a Google client (GP 1,
// and the reason the seam exists at all).
//
// **Report shaping lives here, not in the caller.** The Data API answers
// with a protobuf `RunReportResponse` whose JSON projection carries the
// API's own envelope (separate header and value arrays, positionally
// correlated). Emitting that verbatim would make every consuming module
// re-implement the same zip. The connector instead emits a flat,
// documented envelope — headers named once, then one object per row
// keyed by API name — so a module parses rows directly. The raw
// projection is recoverable by anyone who wants it: the request is
// unchanged, so the Data API returns the same thing to a direct caller.

// The bytes `IDataSource.Query` returns:
//
//   { "property": "properties/123456789",
//     "dimensionHeaders": ["date"],
//     "metricHeaders": ["activeUsers"],
//     "rowCount": 2,
//     "rows": [ { "date": "20260101", "activeUsers": "17" },
//               { "date": "20260102", "activeUsers": "23" } ] }
//
// **Every cell is a string, including metrics.** That is the Data API's
// own wire shape and preserving it is deliberate: GA4 reports integers,
// floats, currency and durations through one field, and a connector that
// guessed which to parse each as would be wrong for somebody. The
// consuming module knows which metrics it asked for and parses
// accordingly.
//
// `rowCount` is the total matching rows Google reports, which can exceed
// `rows.Length` when the request paginates — a consumer comparing the
// two learns whether it saw everything.

// ─── Error mapping ───────────────────────────────────────────────────

/// Map a gRPC failure from the Data API onto the SDK's ingestion-error
/// cases. The distinction that matters operationally is
/// operator-fixable (credential, malformed request) versus
/// wait-and-retry (quota, upstream unavailable), because the ingestor
/// emits different lifecycle events for each.
let private fromRpc (ex: RpcException) : IngestionError =
    match ex.StatusCode with
    | StatusCode.Unauthenticated ->
        CredentialMissing(sprintf "Google Analytics rejected the access token: %s" ex.Status.Detail)
    | StatusCode.PermissionDenied ->
        // Distinct from Unauthenticated: the token is valid, the account
        // it belongs to just cannot see this property. Re-consenting
        // will not help; being granted access to the property will.
        SourceUnreachable(
            sprintf
                "Google Analytics denied access — the connected account lacks permission on this property: %s"
                ex.Status.Detail
        )
    | StatusCode.InvalidArgument ->
        // Almost always a report request naming a dimension/metric that
        // does not exist or cannot be combined. Google's detail names
        // the offending field, so it is passed through verbatim.
        SchemaMismatch(sprintf "Google Analytics rejected the report request: %s" ex.Status.Detail)
    | StatusCode.ResourceExhausted ->
        // GA4 enforces per-property and per-project token buckets that
        // refill hourly / daily. Retryable, but not immediately — the
        // caller's backoff has to be long enough to matter.
        SourceUnreachable(
            sprintf "Google Analytics quota exhausted — retry after the quota window refills: %s" ex.Status.Detail
        )
    | StatusCode.Unavailable
    | StatusCode.DeadlineExceeded -> SourceUnreachable(sprintf "Google Analytics unavailable: %s" ex.Status.Detail)
    | other -> UnexpectedFailure(sprintf "Google Analytics call failed (%A): %s" other ex.Status.Detail)

/// Map a REST failure from the Admin API onto the same cases.
let private fromApi (ex: GoogleApiException) : IngestionError =
    match ex.HttpStatusCode with
    | HttpStatusCode.Unauthorized ->
        CredentialMissing(sprintf "Google Analytics Admin rejected the token: %s" ex.Message)
    | HttpStatusCode.Forbidden ->
        SourceUnreachable(
            sprintf
                "Google Analytics Admin denied access — check the Admin API is enabled on the project and the account has access: %s"
                ex.Message
        )
    | HttpStatusCode.TooManyRequests ->
        SourceUnreachable(sprintf "Google Analytics Admin quota exhausted — retry later: %s" ex.Message)
    | other -> SourceUnreachable(sprintf "Google Analytics Admin call failed (HTTP %d): %s" (int other) ex.Message)

// ─── Report projection ───────────────────────────────────────────────

/// Project a `RunReportResponse` into the flat envelope documented
/// above.
let private renderReport (property: string) (response: RunReportResponse) : byte[] =
    use stream = new IO.MemoryStream()
    use writer = new Utf8JsonWriter(stream)

    let dimensionHeaders = response.DimensionHeaders |> Seq.map _.Name |> Seq.toArray
    let metricHeaders = response.MetricHeaders |> Seq.map _.Name |> Seq.toArray

    writer.WriteStartObject()
    writer.WriteString("property", property)

    writer.WriteStartArray "dimensionHeaders"

    for h in dimensionHeaders do
        writer.WriteStringValue h

    writer.WriteEndArray()

    writer.WriteStartArray "metricHeaders"

    for h in metricHeaders do
        writer.WriteStringValue h

    writer.WriteEndArray()

    writer.WriteNumber("rowCount", response.RowCount)

    writer.WriteStartArray "rows"

    for row in response.Rows do
        writer.WriteStartObject()

        // Positional correlation is the Data API's contract: the nth
        // value belongs to the nth header. `Seq.truncate` guards a
        // response with fewer values than headers rather than throwing
        // — a partial row is more useful than no report.
        row.DimensionValues
        |> Seq.truncate dimensionHeaders.Length
        |> Seq.iteri (fun i v -> writer.WriteString(dimensionHeaders[i], v.Value))

        row.MetricValues
        |> Seq.truncate metricHeaders.Length
        |> Seq.iteri (fun i v -> writer.WriteString(metricHeaders[i], v.Value))

        writer.WriteEndObject()

    writer.WriteEndArray()
    writer.WriteEndObject()
    writer.Flush()
    stream.ToArray()

// ─── Construction ────────────────────────────────────────────────────

/// Build the live transport.
///
/// `applicationName` is sent to Google as the calling application's name
/// — it appears in the project's API dashboards, so give it the
/// deployment's name rather than a placeholder.
///
/// Nothing here is cached across calls: the access token is minted per
/// call by the caller and the two service clients are constructed per
/// call around it. That is the price of portability rule 4 (stateless
/// between invocations) and of reading credentials per call, and it is
/// the right trade while the Phase 10h refresher is what keeps a warm
/// token available.
let create (applicationName: string) : GoogleAnalyticsTransport = {
    MintAccessToken =
        fun (clientId, clientSecret, refreshToken) -> async {
            try
                let initializer = GoogleAuthorizationCodeFlow.Initializer()

                initializer.ClientSecrets <- ClientSecrets(ClientId = clientId, ClientSecret = clientSecret)

                use flow = new GoogleAuthorizationCodeFlow(initializer)
                let token = TokenResponse(RefreshToken = refreshToken)
                // The user id is a local bookkeeping key for the
                // flow's (absent) data store, not an identity claim
                // — the refresh token alone determines who this is.
                let credential = UserCredential(flow, "toolup", token)

                let! refreshed = credential.RefreshTokenAsync CancellationToken.None |> Async.AwaitTask

                if not refreshed then
                    return
                        Error(
                            CredentialMissing
                                "Google declined to refresh the access token — the stored refresh token is no longer valid; reconnect the data source"
                        )
                else
                    return Ok credential.Token.AccessToken
            with
            | :? TokenResponseException as ex ->
                // `invalid_grant` here means the user revoked the
                // grant, changed their password, or the token aged
                // out of an unverified app's seven-day window. All
                // three need fresh consent, not a retry.
                return
                    Error(
                        CredentialMissing(
                            sprintf "Google rejected the refresh token (%s) — reconnect the data source" ex.Message
                        )
                    )
            | :? HttpRequestException as ex -> return Error(SourceUnreachable ex.Message)
        }

    ListProperties =
        fun accessToken -> async {
            try
                let initializer = BaseClientService.Initializer()
                initializer.HttpClientInitializer <- GoogleCredential.FromAccessToken accessToken
                initializer.ApplicationName <- applicationName
                use service = new GoogleAnalyticsAdminService(initializer)

                let mutable pageToken: string = null
                let mutable finished = false
                let acc = ResizeArray<GoogleAnalyticsProperty>()

                while not finished do
                    let request = service.AccountSummaries.List()
                    request.PageSize <- 200

                    if not (isNull pageToken) then
                        request.PageToken <- pageToken

                    let! response = request.ExecuteAsync() |> Async.AwaitTask

                    if not (isNull response.AccountSummaries) then
                        for summary in response.AccountSummaries do
                            if not (isNull summary.PropertySummaries) then
                                for property in summary.PropertySummaries do
                                    acc.Add {
                                        ResourceName = property.Property
                                        DisplayName = property.DisplayName
                                        AccountDisplayName = summary.DisplayName
                                    }

                    pageToken <- response.NextPageToken
                    finished <- String.IsNullOrEmpty pageToken

                return Ok(List.ofSeq acc)
            with
            | :? GoogleApiException as ex -> return Error(fromApi ex)
            | :? HttpRequestException as ex -> return Error(SourceUnreachable ex.Message)
        }

    RunReport =
        fun (accessToken, requestJson) -> async {
            // Parse first, and separately, so a malformed request is
            // reported as a malformed request rather than as
            // whatever the API says about it after a round-trip.
            let parsed =
                try
                    Ok(RunReportRequest.Parser.ParseJson requestJson)
                with :? InvalidProtocolBufferException as ex ->
                    Error(SchemaMismatch(sprintf "report request is not a valid RunReportRequest: %s" ex.Message))

            match parsed with
            | Error e -> return Error e
            | Ok request ->
                try
                    let builder = BetaAnalyticsDataClientBuilder()
                    builder.Credential <- GoogleCredential.FromAccessToken accessToken
                    let client = builder.Build()

                    let! response = client.RunReportAsync request |> Async.AwaitTask
                    return Ok(renderReport request.Property response)
                with
                | :? RpcException as ex -> return Error(fromRpc ex)
                | :? HttpRequestException as ex -> return Error(SourceUnreachable ex.Message)
        }
}