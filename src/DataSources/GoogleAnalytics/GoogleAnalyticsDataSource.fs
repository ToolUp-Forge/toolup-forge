// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.GoogleAnalyticsDataSource

open System
open System.Text
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Google Analytics 4 connector ────────────────────────────────────
//
// `IDataSource` implementation over the GA4 Data API, with property
// discovery through the Analytics Admin API. The credential half lives
// beside it in `GoogleOAuthFlow.fs`; between them a deployment gets the
// whole story — consent, refresh, report, disconnect — from two
// registrations.
//
// **`sql` is a JSON-encoded report request, because GA4 has no SQL
// surface.** The `IDataSource` contract says the parameter is
// connector-specific syntax and that connectors document their dialect;
// this one's dialect is the Data API's own `RunReportRequest` JSON. A
// bare property id or resource name is also accepted as a documented
// shorthand that expands to a default report — see `Query`.
//
// **The network sits behind `GoogleAnalyticsTransport`.** Three calls —
// mint an access token, list properties, run a report — are the entire
// surface this connector needs from Google. Isolating them means the
// contract pack exercises the real connector (credential resolution,
// request interpretation, property normalisation, descriptor
// registration, error mapping) with only the network substituted, rather
// than exercising a second implementation written to pass.
//
// GP 1 — the Google client libraries are referenced by this companion
// package alone; nothing in `ToolUp.Platform.*` gains a Google
// dependency.
// Phase 9c rule 4 — stateless across calls: `ISecretStore`, the
// transport, and the optional refresher arrive via `create`; per-call
// state rides `DataSourceCallContext`, and credentials are re-read from
// the secret store on every call so rotation flows through without
// reconstruction.

// ─── Property discovery ──────────────────────────────────────────────

/// One GA4 property visible to the connected account, as returned by
/// the Admin API's account-summary listing.
type GoogleAnalyticsProperty = {
    /// The property's resource name — `"properties/123456789"`. This is
    /// the literal value a report request's `property` field takes, and
    /// it is what `ListTables` returns, so a caller can hand a listed
    /// name straight back as a query target.
    ResourceName: string
    /// The property's display name in the Google Analytics UI.
    DisplayName: string
    /// Display name of the account the property belongs to. One
    /// credential commonly sees several accounts; without this an
    /// operator picking from a list of same-named properties cannot
    /// tell them apart.
    AccountDisplayName: string
}

// ─── Transport seam ──────────────────────────────────────────────────

/// The three network calls the GA4 connector makes. A record of
/// functions rather than an interface: it is a private seam between two
/// files of one companion, not an SDK extension point, and the record
/// keeps a test double to three lambdas.
///
/// Every member returns `Result<_, IngestionError>` — the transport owns
/// mapping Google's exception taxonomy onto the SDK's error cases, so
/// the connector above it never sees a vendor type.
type GoogleAnalyticsTransport = {
    /// `clientId * clientSecret * refreshToken` → a short-lived access
    /// token. Mirrors what `IOAuthCredentialFlow.RefreshAccessToken`
    /// does; the connector uses this one so a deployment can compose the
    /// connector without also composing the flow (a service-account or
    /// externally-managed credential still reaches the same code path).
    MintAccessToken: string * string * string -> Async<Result<string, IngestionError>>
    /// `accessToken` → every property the credential can see.
    ListProperties: string -> Async<Result<GoogleAnalyticsProperty list, IngestionError>>
    /// `accessToken * runReportRequestJson` → the report, already
    /// rendered as the bytes `IDataSource.Query` returns. Shaping lives
    /// on this side of the seam because it is where the protobuf
    /// response is; see `GoogleAnalyticsLiveTransport`.
    RunReport: string * string -> Async<Result<byte[], IngestionError>>
}

// ─── Connector configuration ─────────────────────────────────────────

/// Connector-level settings. Defaults match `GoogleOAuthFlowConfig`'s,
/// and the two must agree: the connector reads the refresh token the
/// flow persisted, under a key derived from the flow name.
type GoogleAnalyticsSourceConfig = {
    /// `IDataSource.Kind` discriminator — the value a
    /// `DataSourceConfig.Kind` must carry to route here. Default
    /// `"GoogleAnalytics"`.
    Kind: string
    /// The paired `IOAuthCredentialFlow.Name`. Determines the
    /// `ISecretStore` keys the connector reads (client id / secret /
    /// refresh token) and the `Provider` of the refresh descriptor it
    /// registers. Must equal the composed flow's name.
    FlowName: string
    /// Google's token endpoint, recorded on the refresh descriptor so
    /// the Phase 10h substrate knows where to refresh.
    TokenEndpoint: string
    /// Lookback window for the bare-property-name query shorthand.
    DefaultLookbackDays: int
    /// Dimension API names for the shorthand's default report.
    DefaultDimensions: string list
    /// Metric API names for the shorthand's default report.
    DefaultMetrics: string list
}

module GoogleAnalyticsSourceConfig =
    [<Literal>]
    let DefaultKind = "GoogleAnalytics"

    /// `DataSourceConfig.ConnectionScope` key naming the property this
    /// source reports on. Read when a report request omits `property`,
    /// which is the common case for a module that stores its query
    /// alongside a source rather than repeating the property in it.
    [<Literal>]
    let PropertyIdKey = "property_id"

    /// Standard settings, paired with
    /// `GoogleOAuthFlowConfig.analyticsReadonly`.
    let standard: GoogleAnalyticsSourceConfig = {
        Kind = DefaultKind
        FlowName = GoogleOAuthFlow.GoogleOAuthFlowConfig.DefaultFlowName
        TokenEndpoint = GoogleOAuthFlow.GoogleOAuthFlowConfig.DefaultTokenBaseUrl
        DefaultLookbackDays = 28
        DefaultDimensions = [ "date" ]
        DefaultMetrics = [ "activeUsers" ]
    }

// ─── Report-request interpretation ───────────────────────────────────

/// Pure helpers turning an `IDataSource.Query` `sql` argument into the
/// report-request JSON the Data API accepts. Vendor-free by design:
/// everything here is `System.Text.Json` over the documented request
/// shape, so the interpretation rules are testable without a Google
/// client, a credential, or a network.
module GoogleAnalyticsQuery =

    /// GA4 addresses a property as `properties/{id}`. Operators
    /// routinely paste the bare numeric id they see in the Analytics UI,
    /// so accept both and normalise. An already-prefixed name is
    /// returned unchanged.
    let normaliseProperty (property: string) : string =
        let trimmed = property.Trim()

        if trimmed.StartsWith("properties/", StringComparison.Ordinal) then
            trimmed
        else
            "properties/" + trimmed

    /// Whether the `sql` argument looks like a report-request object
    /// rather than a bare property name. Structural, not a parse
    /// attempt: a malformed object must reach the parser and fail as a
    /// malformed request, not be silently reinterpreted as a property
    /// name and produce a confusing "unknown property" error instead.
    let looksLikeJson (sql: string) : bool =
        sql.TrimStart().StartsWith("{", StringComparison.Ordinal)

    /// Build the default report for the bare-property-name shorthand.
    /// Deliberately a report that returns something: a request with no
    /// metric is rejected by the Data API, so "default" has to mean a
    /// real query, not an empty one.
    let defaultRequestJson
        (property: string)
        (lookbackDays: int)
        (dimensions: string list)
        (metrics: string list)
        : string =
        let names (key: string) (values: string list) =
            let items =
                values
                |> List.map (fun n -> sprintf """{"name":%s}""" (JsonSerializer.Serialize n))
                |> String.concat ","

            sprintf """"%s":[%s]""" key items

        // `NdaysAgo` / `today` are the Data API's own relative-date
        // tokens — using them rather than computing dates here keeps the
        // window anchored to Google's notion of the property's day
        // boundary rather than this process's clock.
        let dateRange =
            sprintf """"dateRanges":[{"startDate":"%ddaysAgo","endDate":"today"}]""" lookbackDays

        String.concat "," [
            sprintf """"property":%s""" (JsonSerializer.Serialize(normaliseProperty property))
            dateRange
            names "dimensions" dimensions
            names "metrics" metrics
        ]
        |> sprintf "{%s}"

    /// Read the `property` field of a report-request object, if present
    /// and non-empty.
    let tryReadProperty (requestJson: string) : string option =
        try
            use doc = JsonDocument.Parse requestJson

            match doc.RootElement.TryGetProperty "property" with
            | true, v when v.ValueKind = JsonValueKind.String ->
                match v.GetString() with
                | null -> None
                | s when String.IsNullOrWhiteSpace s -> None
                | s -> Some s
            | _ -> None
        with :? JsonException ->
            None

    /// Re-emit a report-request object with `property` set to
    /// `property`, replacing whatever was there. Every other field is
    /// copied through untouched, so a request naming filters, ordering,
    /// pagination or a metric aggregation keeps them — this only
    /// normalises the one field the connector is authoritative about.
    let withProperty (property: string) (requestJson: string) : Result<string, string> =
        try
            use doc = JsonDocument.Parse requestJson

            if doc.RootElement.ValueKind <> JsonValueKind.Object then
                Error "report request must be a JSON object"
            else
                use stream = new IO.MemoryStream()
                use writer = new Utf8JsonWriter(stream)
                writer.WriteStartObject()
                writer.WriteString("property", property)

                for prop in doc.RootElement.EnumerateObject() do
                    if prop.NameEquals "property" then
                        ()
                    else
                        prop.WriteTo writer

                writer.WriteEndObject()
                writer.Flush()
                Ok(Encoding.UTF8.GetString(stream.ToArray()))
        with :? JsonException as ex ->
            Error(sprintf "report request was not valid JSON: %s" ex.Message)

    /// Resolve the `sql` argument to the report-request JSON the
    /// transport will send.
    ///
    /// `configuredProperty` is the source's `ConnectionScope`
    /// `property_id`, used when the request omits `property`. A request
    /// that names neither is an error the caller can act on — the
    /// alternative, guessing the account's first property, would produce
    /// a plausible report about the wrong site.
    let resolve
        (config: GoogleAnalyticsSourceConfig)
        (configuredProperty: string option)
        (sql: string)
        : Result<string * string, IngestionError> =
        if String.IsNullOrWhiteSpace sql then
            match configuredProperty with
            | Some p ->
                let property = normaliseProperty p

                Ok(
                    property,
                    defaultRequestJson
                        property
                        config.DefaultLookbackDays
                        config.DefaultDimensions
                        config.DefaultMetrics
                )
            | None ->
                Error(
                    SchemaMismatch
                        "empty query and no property_id configured — supply a property resource name or a RunReportRequest JSON object"
                )
        elif not (looksLikeJson sql) then
            let property = normaliseProperty sql

            Ok(
                property,
                defaultRequestJson property config.DefaultLookbackDays config.DefaultDimensions config.DefaultMetrics
            )
        else
            let property =
                match tryReadProperty sql with
                | Some p -> Some(normaliseProperty p)
                | None -> configuredProperty |> Option.map normaliseProperty

            match property with
            | None ->
                Error(
                    SchemaMismatch
                        "report request omits \"property\" and the data source has no property_id in its ConnectionScope"
                )
            | Some p ->
                match withProperty p sql with
                | Ok json -> Ok(p, json)
                | Error msg -> Error(SchemaMismatch msg)

// ─── Credential resolution ───────────────────────────────────────────

/// Read the refresh token for a data source. Three sources, in the
/// order that costs least:
///   1. `ctx.Credential` — the ingestor pre-resolved it.
///   2. The OAuth substrate's own slot, `{flowName}-refresh-{id}`. This
///      is where a completed consent flow leaves it, so it is the normal
///      case.
///   3. `ctx.Config.CredentialKey` — the escape hatch for a deployment
///      that provisions the token out of band rather than through the
///      consent flow.
let private readRefreshToken
    (secretStore: ISecretStore)
    (flowName: string)
    (ctx: DataSourceCallContext)
    : Async<Result<string, IngestionError>> =
    async {
        match ctx.Credential with
        | Some c when not (String.IsNullOrWhiteSpace c) -> return Ok c
        | _ ->
            let substrateKey = GoogleOAuthFlow.refreshTokenKey flowName ctx.Config.Id
            let! fromSubstrate = secretStore.GetSecret(ctx.ScopeId, substrateKey)

            match fromSubstrate with
            | Some t when not (String.IsNullOrWhiteSpace t) -> return Ok t
            | _ ->
                let! fromConfigKey = secretStore.GetSecret(ctx.ScopeId, ctx.Config.CredentialKey)

                match fromConfigKey with
                | Some t when not (String.IsNullOrWhiteSpace t) -> return Ok t
                | _ -> return Error(CredentialMissing substrateKey)
    }

/// Read the OAuth client id / secret pair the token mint needs.
let private readClientCredentials
    (secretStore: ISecretStore)
    (flowName: string)
    (ctx: DataSourceCallContext)
    : Async<Result<string * string, IngestionError>> =
    async {
        let idKey = GoogleOAuthFlow.clientIdKey flowName ctx.Config.Id
        let secretKey = GoogleOAuthFlow.clientSecretKey flowName ctx.Config.Id
        let! clientId = secretStore.GetSecret(ctx.ScopeId, idKey)
        let! clientSecret = secretStore.GetSecret(ctx.ScopeId, secretKey)

        match clientId, clientSecret with
        | Some id, Some secret -> return Ok(id, secret)
        | None, _ -> return Error(CredentialMissing idKey)
        | _, None -> return Error(CredentialMissing secretKey)
    }

/// Mint an access token: client credentials + refresh token → transport.
/// Every credentialed connector method starts here, so the "no
/// credential" and "credential rejected" cases are distinguished once
/// rather than per call site.
let private authorise
    (secretStore: ISecretStore)
    (transport: GoogleAnalyticsTransport)
    (flowName: string)
    (ctx: DataSourceCallContext)
    : Async<Result<string, IngestionError>> =
    async {
        match! readClientCredentials secretStore flowName ctx with
        | Error e -> return Error e
        | Ok(clientId, clientSecret) ->
            match! readRefreshToken secretStore flowName ctx with
            | Error e -> return Error e
            | Ok refreshToken -> return! transport.MintAccessToken(clientId, clientSecret, refreshToken)
    }

// ─── Construction ────────────────────────────────────────────────────

/// Build the GA4 `IDataSource`.
///
/// `secretStore` supplies the client credentials + refresh token per
/// call (never captured — rotation flows through). `transport` is the
/// network seam; `GoogleAnalyticsLiveTransport.create` is the real one.
///
/// `refresher` is the Phase 10h token-refresh substrate. When present, a
/// successful `Connect` registers this data source's refresh descriptor,
/// which is what moves the deployment from "mint a token on every call"
/// to "a scheduled job keeps a valid token warm". `None` for a
/// deployment that composed no refresher: the connector then mints per
/// call exactly as it did before Phase 10h, and pays nothing for the
/// substrate it did not compose (GP 13).
let create
    (secretStore: ISecretStore)
    (transport: GoogleAnalyticsTransport)
    (refresher: IOAuthTokenRefresher option)
    (config: GoogleAnalyticsSourceConfig)
    : IDataSource =
    let flowName = config.FlowName

    /// Phase 10h adoption, register half. Called after a successful
    /// token mint, so the descriptor is only ever registered for a
    /// credential that has just been observed to work — registering on
    /// an unverified credential would schedule a job whose first act is
    /// to fail.
    ///
    /// Idempotent by the substrate's contract, so a `Connect` on every
    /// ingestion attempt re-registers harmlessly and picks up a changed
    /// token endpoint without a separate migration.
    let registerRefreshDescriptor (ctx: DataSourceCallContext) (clientId: string) : Async<unit> =
        match refresher with
        | None -> async { return () }
        | Some r -> async {
            let descriptor =
                OAuthRefreshDescriptor.withDefaults
                    flowName
                    ctx.Config.Id
                    ctx.ScopeId
                    config.TokenEndpoint
                    clientId
                    (GoogleOAuthFlow.clientSecretKey flowName ctx.Config.Id)
                    (GoogleOAuthFlow.refreshTokenKey flowName ctx.Config.Id)

            do! r.RegisterDescriptor descriptor
          }

    { new IDataSource with
        member _.Kind = config.Kind

        member _.Connect ctx = async {
            match! readClientCredentials secretStore flowName ctx with
            | Error e -> return Error e
            | Ok(clientId, clientSecret) ->
                match! readRefreshToken secretStore flowName ctx with
                | Error e -> return Error e
                | Ok refreshToken ->
                    // The probe IS the token mint. GA4 has no cheaper
                    // reachability call, and a mint answers the question
                    // an operator is actually asking when they click
                    // "Test connection" — is this credential still
                    // good — which a network ping would not.
                    match! transport.MintAccessToken(clientId, clientSecret, refreshToken) with
                    | Error e -> return Error e
                    | Ok _ ->
                        do! registerRefreshDescriptor ctx clientId
                        return Ok()
        }

        member _.ListTables ctx = async {
            match! authorise secretStore transport flowName ctx with
            | Error e -> return Error e
            | Ok accessToken ->
                match! transport.ListProperties accessToken with
                | Error e -> return Error e
                | Ok properties -> return Ok(properties |> List.map _.ResourceName)
        }

        member _.GetSchema(_ctx, table) =
            // Answered from the static catalogue — no credential, no
            // network. See `GoogleAnalyticsSchema` for why, and for what
            // the catalogue deliberately does not cover.
            async { return Ok(GoogleAnalyticsSchema.tableSchema table) }

        member _.Query(ctx, sql) = async {
            let configuredProperty =
                ctx.Config.ConnectionScope
                |> Map.tryFind GoogleAnalyticsSourceConfig.PropertyIdKey

            match GoogleAnalyticsQuery.resolve config configuredProperty sql with
            | Error e -> return Error e
            | Ok(_property, requestJson) ->
                match! authorise secretStore transport flowName ctx with
                | Error e -> return Error e
                | Ok accessToken -> return! transport.RunReport(accessToken, requestJson)
        }
    }