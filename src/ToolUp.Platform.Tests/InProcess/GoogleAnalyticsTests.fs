// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.GoogleAnalyticsTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.DataSources
open ToolUp.DataSources.GoogleAnalyticsDataSource
open ToolUp.Platform.Tests.Contracts

// ─── Google Analytics 4 connector tests ──────────────────────────────
//
// Three groups:
//
//   * The `IDataSource` contract pack bound to the real connector with
//     a faked `GoogleAnalyticsTransport` — so credential resolution,
//     request interpretation, property normalisation and descriptor
//     registration are all under test, and only the network is
//     substituted.
//   * The `IOAuthCredentialFlow` contract pack bound to the real Google
//     flow over a stubbed HTTP handler, plus the Google-specific
//     assertions the shared pack deliberately does not make
//     (`access_type=offline` / `prompt=consent`, PKCE parameters, the
//     no-refresh-token diagnostic, descriptor unregistration on revoke).
//   * Pure tests over the query-interpretation and schema-catalogue
//     helpers, which need neither.
//
// A real-API smoke arm sits at the end, gated on credentials being
// present in the environment.

// ─── Fakes ───────────────────────────────────────────────────────────

/// Secret store that answers the connector's key conventions by
/// construction rather than from a seeded list, because the contract
/// pack invents a fresh data-source id per test and a fixed seed list
/// could not cover them.
///
/// The refresh token it returns encodes the source id
/// (`refresh-{sourceId}`), which is what lets the fake transport below
/// route a minted token back to the right seeded content — the same
/// correlation a real deployment gets from the credential genuinely
/// being per-source.
type private DerivedSecretStore() =
    let overrides = ConcurrentDictionary<string, string>()

    member _.Peek(key: string) : string option =
        match overrides.TryGetValue key with
        | true, v -> Some v
        | _ -> None

    interface ISecretStore with
        member _.GetSecret(_scope, key) = async {
            match overrides.TryGetValue key with
            | true, v -> return Some v
            | _ ->
                if key.StartsWith("google-analytics-client-id-", StringComparison.Ordinal) then
                    return Some "client-abc.apps.googleusercontent.com"
                elif key.StartsWith("google-analytics-client-secret-", StringComparison.Ordinal) then
                    return Some "GOCSPX-secret"
                elif key.StartsWith("google-analytics-refresh-", StringComparison.Ordinal) then
                    let sourceId = key.Substring("google-analytics-refresh-".Length)
                    return Some("refresh-" + sourceId)
                else
                    return None
        }

        member _.SetSecret(_scope, key, value) = async {
            overrides[key] <- value
            return Ok()
        }

        member _.DeleteSecret(_scope, key) = async {
            overrides.TryRemove key |> ignore
            return Ok()
        }

        member _.ListKeys(_scope) = async { return overrides.Keys |> List.ofSeq }

/// Secret store that holds nothing at all — used to assert the
/// missing-credential paths report the key the operator has to fill.
type private EmptySecretStore() =
    interface ISecretStore with
        member _.GetSecret(_scope, _key) = async { return None }
        member _.SetSecret(_scope, _key, _value) = async { return Ok() }
        member _.DeleteSecret(_scope, _key) = async { return Ok() }
        member _.ListKeys(_scope) = async { return [] }

/// Records what the connector asked the Phase 10h substrate to do.
/// Only the two lifecycle methods are exercised; the read methods
/// answer from the same map so a test can assert the descriptor's
/// contents rather than merely that a call happened.
type private RecordingRefresher() =
    let registered = ConcurrentDictionary<string, OAuthRefreshDescriptor>()
    let unregistered = ResizeArray<string * string>()

    member _.Registered = registered.Values |> List.ofSeq
    member _.Unregistered = unregistered |> List.ofSeq

    interface IOAuthTokenRefresher with
        member _.RefreshNow(provider, configId) = async {
            return PermanentError(sprintf "not implemented in the test double (%s:%s)" provider configId)
        }

        member _.RegisterDescriptor descriptor = async {
            registered[OAuthRefreshDescriptor.key descriptor] <- descriptor
        }

        member _.UnregisterDescriptor(provider, configId) = async {
            lock unregistered (fun () -> unregistered.Add(provider, configId))
            registered.TryRemove(sprintf "%s:%s" provider configId) |> ignore
        }

        member _.GetDescriptor(provider, configId) = async {
            match registered.TryGetValue(sprintf "%s:%s" provider configId) with
            | true, d -> return Some d
            | _ -> return None
        }

        member _.ListDescriptors() = async { return registered.Values |> List.ofSeq }

/// Fake `GoogleAnalyticsTransport` over an in-memory
/// `(sourceId, table) → bytes` map — the shape the `IDataSource`
/// contract pack's seeder produces.
///
/// The access token it mints is `token:{refreshToken}`, and
/// `DerivedSecretStore` above makes the refresh token
/// `refresh-{sourceId}`, so the transport recovers the source id from
/// the token exactly as a real credential would scope a real call.
type private FakeGa4Transport() =
    let content = ConcurrentDictionary<string * string, byte[]>()
    let runRequests = ResizeArray<string>()

    let sourceIdOf (accessToken: string) =
        // "token:refresh-src-1" → "src-1"
        let withoutPrefix = accessToken.Replace("token:refresh-", "")
        withoutPrefix

    member _.Seed (sourceId: DataSourceId) (table: string) (bytes: byte[]) : unit = content[(sourceId, table)] <- bytes

    /// Every report-request JSON the connector sent, in order. Lets a
    /// test assert what the connector BUILT, not just what came back.
    member _.RunRequests = runRequests |> List.ofSeq

    member this.Transport: GoogleAnalyticsTransport = {
        MintAccessToken = fun (_clientId, _clientSecret, refreshToken) -> async { return Ok("token:" + refreshToken) }

        ListProperties =
            fun accessToken -> async {
                let sourceId = sourceIdOf accessToken

                let properties =
                    content.Keys
                    |> Seq.filter (fun (s, _) -> s = sourceId)
                    |> Seq.map (fun (_, table) -> {
                        ResourceName = table
                        DisplayName = "Property " + table
                        AccountDisplayName = "Account for " + sourceId
                    })
                    |> List.ofSeq

                return Ok properties
            }

        RunReport =
            fun (accessToken, requestJson) -> async {
                lock runRequests (fun () -> runRequests.Add requestJson)
                let sourceId = sourceIdOf accessToken

                let property =
                    GoogleAnalyticsQuery.tryReadProperty requestJson
                    |> Option.defaultValue ""
                    |> fun p -> p.Replace("properties/", "")

                match content.TryGetValue((sourceId, property)) with
                | true, bytes -> return Ok bytes
                | _ ->
                    return
                        Error(SourceUnreachable(sprintf "no such property %s under data source %s" property sourceId))
            }
    }

// ─── IDataSource contract binding ────────────────────────────────────

let private mkConnector (refresher: IOAuthTokenRefresher option) =
    let transport = FakeGa4Transport()
    let secrets = DerivedSecretStore() :> ISecretStore

    let source =
        create secrets transport.Transport refresher GoogleAnalyticsSourceConfig.standard

    source, transport

let private contractTests =
    let factory () =
        let source, transport = mkConnector None

        let seeder: IDataSourceContract.Seeder =
            fun sourceId table bytes -> transport.Seed sourceId table bytes

        source, seeder

    IDataSourceContract.tests "GoogleAnalyticsDataSource" factory

// ─── Connector-specific tests ────────────────────────────────────────

let private mkCtx (sourceId: DataSourceId) (connectionScope: (string * string) list) : DataSourceCallContext = {
    ScopeId = "team-scope-1"
    Config = {
        Id = sourceId
        Name = "GA4 " + sourceId
        Kind = GoogleAnalyticsSourceConfig.DefaultKind
        ConnectionScope = Map.ofList connectionScope
        CredentialKey = "ga4-credential"
        Tables = None
        Tags = Map.empty
    }
    Credential = None
}

let private connectorTests =
    testList "GoogleAnalyticsDataSource — connector" [

        test "Kind is the routing discriminator the config declares" {
            let source, _ = mkConnector None
            Expect.equal source.Kind "GoogleAnalytics" "Kind routes DataSourceConfig.Kind = \"GoogleAnalytics\""
        }

        testCaseAsync "Connect registers a Phase 10h refresh descriptor pointing at the substrate's own token key"
        <| async {
            let refresher = RecordingRefresher()
            let transport = FakeGa4Transport()
            let secrets = DerivedSecretStore() :> ISecretStore

            let source =
                create
                    secrets
                    transport.Transport
                    (Some(refresher :> IOAuthTokenRefresher))
                    GoogleAnalyticsSourceConfig.standard

            match! source.Connect(mkCtx "ga-1" []) with
            | Error err -> failtestf "Connect failed: %A" err
            | Ok() ->
                match refresher.Registered with
                | [ d ] ->
                    Expect.equal d.Provider "google-analytics" "Provider matches the paired flow name"
                    Expect.equal d.ConfigId "ga-1" "ConfigId is the data-source id"
                    Expect.equal d.ScopeId "team-scope-1" "ScopeId is pinned from the call context"

                    Expect.equal
                        d.TokenEndpoint
                        "https://oauth2.googleapis.com/token"
                        "Points at Google's token endpoint"

                    // The load-bearing assertion. A descriptor whose
                    // RefreshTokenKey does not match the key the OAuth
                    // callback wrote refreshes nothing, and does so
                    // silently — the substrate would read `None` and
                    // dead-letter with a message about a missing token
                    // rather than about a mismatched key.
                    Expect.equal
                        d.RefreshTokenKey
                        "google-analytics-refresh-ga-1"
                        "RefreshTokenKey matches the OAuth callback's persistence key"

                    Expect.equal
                        d.ClientSecretKey
                        "google-analytics-client-secret-ga-1"
                        "ClientSecretKey matches the flow's client-secret key"

                    Expect.equal
                        d.ClientId
                        "client-abc.apps.googleusercontent.com"
                        "ClientId is carried in plaintext (public by the OAuth spec)"
                | other -> failtestf "expected exactly one registered descriptor, got %A" other
        }

        testCaseAsync "Connect registers nothing when no refresher is composed"
        <| async {
            let source, _ = mkConnector None

            match! source.Connect(mkCtx "ga-2" []) with
            | Ok() -> ()
            | Error err -> failtestf "Connect must still succeed without a refresher: %A" err
        }

        testCaseAsync "Connect re-registration is idempotent on the descriptor key"
        <| async {
            let refresher = RecordingRefresher()
            let transport = FakeGa4Transport()
            let secrets = DerivedSecretStore() :> ISecretStore

            let source =
                create
                    secrets
                    transport.Transport
                    (Some(refresher :> IOAuthTokenRefresher))
                    GoogleAnalyticsSourceConfig.standard

            let ctx = mkCtx "ga-3" []
            let! _ = source.Connect ctx
            let! _ = source.Connect ctx

            Expect.equal (List.length refresher.Registered) 1 "two Connects register one descriptor, not two"
        }

        testCaseAsync "Connect reports the missing client-id key when no credentials are stored"
        <| async {
            let transport = FakeGa4Transport()

            let source =
                create (EmptySecretStore()) transport.Transport None GoogleAnalyticsSourceConfig.standard

            match! source.Connect(mkCtx "ga-4" []) with
            | Ok() -> failtest "Connect must not succeed with no stored credentials"
            | Error(CredentialMissing key) ->
                Expect.equal key "google-analytics-client-id-ga-4" "names the key the operator has to fill"
            | Error other -> failtestf "expected CredentialMissing, got %A" other
        }

        testCaseAsync "ListTables returns GA4 property resource names verbatim"
        <| async {
            let source, transport = mkConnector None
            transport.Seed "ga-5" "properties/111" (Encoding.UTF8.GetBytes "{}")
            transport.Seed "ga-5" "properties/222" (Encoding.UTF8.GetBytes "{}")

            match! source.ListTables(mkCtx "ga-5" []) with
            | Ok tables ->
                Expect.containsAll
                    tables
                    [ "properties/111"; "properties/222" ]
                    "resource names pass through unchanged so a listed name can be queried directly"
            | Error err -> failtestf "ListTables failed: %A" err
        }

        testCaseAsync "GetSchema answers from the static catalogue without a credential"
        <| async {
            let transport = FakeGa4Transport()
            // Deliberately the EMPTY secret store: `GetSchema` must not
            // need a credential, and this is how that stays true.
            let source =
                create (EmptySecretStore()) transport.Transport None GoogleAnalyticsSourceConfig.standard

            match! source.GetSchema(mkCtx "ga-6" [], "properties/111") with
            | Ok schema ->
                Expect.equal schema.TableName "properties/111" "TableName echoes the request"
                Expect.isNonEmpty schema.Columns "catalogue is populated"

                let names = schema.Columns |> List.map _.Name |> Set.ofList
                Expect.isTrue (names.Contains "date") "carries the date dimension"
                Expect.isTrue (names.Contains "activeUsers") "carries the activeUsers metric"
            | Error err -> failtestf "GetSchema failed: %A" err
        }

        testCaseAsync "Query expands a bare property id into a runnable default report"
        <| async {
            let source, transport = mkConnector None
            transport.Seed "ga-7" "777" (Encoding.UTF8.GetBytes """{"rows":[]}""")

            // A bare numeric id — what an operator pastes out of the
            // Analytics UI.
            match! source.Query(mkCtx "ga-7" [], "777") with
            | Error err -> failtestf "Query failed: %A" err
            | Ok bytes ->
                Expect.equal (Encoding.UTF8.GetString bytes) """{"rows":[]}""" "transport bytes pass through verbatim"

                match transport.RunRequests with
                | [ request ] ->
                    use doc = JsonDocument.Parse request
                    let root = doc.RootElement

                    Expect.equal
                        (root.GetProperty("property").GetString())
                        "properties/777"
                        "bare id normalised to a resource name"

                    // A RunReportRequest with no metric is rejected by
                    // the Data API, so "default" has to mean a report
                    // that actually returns something.
                    Expect.isTrue (root.GetProperty("metrics").GetArrayLength() > 0) "default report names a metric"

                    Expect.isTrue
                        (root.GetProperty("dateRanges").GetArrayLength() > 0)
                        "default report names a date range"
                | other -> failtestf "expected one report request, got %A" other
        }

        testCaseAsync "Query preserves a caller's report request and only normalises `property`"
        <| async {
            let source, transport = mkConnector None
            transport.Seed "ga-8" "888" (Encoding.UTF8.GetBytes """{"rows":[]}""")

            let request =
                """{"property":"888","dimensions":[{"name":"pagePath"}],"metrics":[{"name":"screenPageViews"}],"limit":"25"}"""

            match! source.Query(mkCtx "ga-8" [], request) with
            | Error err -> failtestf "Query failed: %A" err
            | Ok _ ->
                match transport.RunRequests with
                | [ sent ] ->
                    use doc = JsonDocument.Parse sent
                    let root = doc.RootElement
                    Expect.equal (root.GetProperty("property").GetString()) "properties/888" "property normalised"
                    Expect.equal (root.GetProperty("limit").GetString()) "25" "unrelated fields survive untouched"

                    Expect.equal
                        (root.GetProperty("dimensions").[0].GetProperty("name").GetString())
                        "pagePath"
                        "the caller's dimensions survive"
                | other -> failtestf "expected one report request, got %A" other
        }

        testCaseAsync "Query falls back to the source's configured property when the request omits one"
        <| async {
            let source, transport = mkConnector None
            transport.Seed "ga-9" "999" (Encoding.UTF8.GetBytes """{"rows":[]}""")

            let request = """{"metrics":[{"name":"sessions"}]}"""

            match! source.Query(mkCtx "ga-9" [ "property_id", "999" ], request) with
            | Error err -> failtestf "Query failed: %A" err
            | Ok _ ->
                match transport.RunRequests with
                | [ sent ] ->
                    use doc = JsonDocument.Parse sent

                    Expect.equal
                        (doc.RootElement.GetProperty("property").GetString())
                        "properties/999"
                        "property_id from ConnectionScope fills the gap"
                | other -> failtestf "expected one report request, got %A" other
        }

        testCaseAsync "Query refuses a request that names no property and has none configured"
        <| async {
            let source, _ = mkConnector None

            match! source.Query(mkCtx "ga-10" [], """{"metrics":[{"name":"sessions"}]}""") with
            | Ok _ ->
                failtest
                    "Query must not guess a property — a plausible report about the wrong site is worse than an error"
            | Error(SchemaMismatch msg) -> Expect.stringContains msg "property" "the error names what is missing"
            | Error other -> failtestf "expected SchemaMismatch, got %A" other
        }
    ]

// ─── Pure query-interpretation tests ─────────────────────────────────

let private queryTests =
    testList "GoogleAnalyticsQuery" [

        test "normaliseProperty prefixes a bare id and leaves a resource name alone" {
            Expect.equal (GoogleAnalyticsQuery.normaliseProperty "123456") "properties/123456" "bare id prefixed"

            Expect.equal
                (GoogleAnalyticsQuery.normaliseProperty "properties/123456")
                "properties/123456"
                "resource name unchanged"

            Expect.equal
                (GoogleAnalyticsQuery.normaliseProperty "  123456  ")
                "properties/123456"
                "surrounding whitespace trimmed — operators paste"
        }

        test "looksLikeJson is structural, so a malformed object still reaches the parser" {
            Expect.isTrue (GoogleAnalyticsQuery.looksLikeJson """{"property":"x"}""") "well-formed object"
            Expect.isTrue (GoogleAnalyticsQuery.looksLikeJson "  {oops") "malformed object is still an object attempt"
            Expect.isFalse (GoogleAnalyticsQuery.looksLikeJson "properties/123") "resource name is not JSON"
        }

        test "withProperty replaces the property field and copies every other field through" {
            let source = """{"property":"old","limit":"10","metrics":[{"name":"sessions"}]}"""

            match GoogleAnalyticsQuery.withProperty "properties/new" source with
            | Error e -> failtestf "withProperty failed: %s" e
            | Ok json ->
                use doc = JsonDocument.Parse json
                let root = doc.RootElement
                Expect.equal (root.GetProperty("property").GetString()) "properties/new" "property replaced"
                Expect.equal (root.GetProperty("limit").GetString()) "10" "limit preserved"
                Expect.equal (root.GetProperty("metrics").GetArrayLength()) 1 "metrics preserved"

                // Exactly one `property` — a duplicate key would parse
                // as last-wins somewhere and first-wins elsewhere.
                let propertyCount =
                    root.EnumerateObject()
                    |> Seq.filter (fun p -> p.NameEquals "property")
                    |> Seq.length

                Expect.equal propertyCount 1 "no duplicate property key emitted"
        }

        test "withProperty rejects a non-object request" {
            match GoogleAnalyticsQuery.withProperty "properties/1" "[1,2,3]" with
            | Ok _ -> failtest "a JSON array is not a report request"
            | Error msg -> Expect.stringContains msg "object" "error says what shape was expected"
        }

        test "defaultRequestJson emits a request carrying property, date range, dimensions and metrics" {
            let json =
                GoogleAnalyticsQuery.defaultRequestJson "properties/1" 28 [ "date" ] [ "activeUsers" ]

            use doc = JsonDocument.Parse json
            let root = doc.RootElement
            Expect.equal (root.GetProperty("property").GetString()) "properties/1" "property"

            Expect.equal
                (root.GetProperty("dateRanges").[0].GetProperty("startDate").GetString())
                "28daysAgo"
                "relative start date keeps the window on Google's day boundary, not this process's clock"

            Expect.equal (root.GetProperty("dimensions").[0].GetProperty("name").GetString()) "date" "dimension name"

            Expect.equal (root.GetProperty("metrics").[0].GetProperty("name").GetString()) "activeUsers" "metric name"
        }
    ]

// ─── Schema-catalogue tests ──────────────────────────────────────────

let private schemaTests =
    testList "GoogleAnalyticsSchema" [

        test "dimensions and metrics are disjoint and correctly tagged" {
            Expect.isNonEmpty GoogleAnalyticsSchema.dimensions "dimensions populated"
            Expect.isNonEmpty GoogleAnalyticsSchema.metrics "metrics populated"

            Expect.isTrue
                (GoogleAnalyticsSchema.dimensions
                 |> List.forall (fun f -> f.Kind = GoogleAnalyticsSchema.Dimension))
                "every entry in `dimensions` is tagged Dimension"

            Expect.isTrue
                (GoogleAnalyticsSchema.metrics
                 |> List.forall (fun f -> f.Kind = GoogleAnalyticsSchema.Metric))
                "every entry in `metrics` is tagged Metric"

            let dimNames = GoogleAnalyticsSchema.dimensions |> List.map _.ApiName |> Set.ofList
            let metNames = GoogleAnalyticsSchema.metrics |> List.map _.ApiName |> Set.ofList

            // A name appearing on both sides would let a picker put it
            // in the wrong list of the request, which the Data API
            // rejects with an unhelpful message.
            Expect.isEmpty
                (Set.intersect dimNames metNames |> Set.toList)
                "no API name is both a dimension and a metric"
        }

        test "API names are unique across the catalogue" {
            let names = GoogleAnalyticsSchema.allFields |> List.map _.ApiName

            Expect.equal
                (List.length names)
                (names |> Set.ofList |> Set.count)
                "a duplicated API name would render two identical picker entries"
        }

        test "every field carries a UI label, a category and a wire type" {
            for field in GoogleAnalyticsSchema.allFields do
                Expect.isNonEmpty field.ApiName "ApiName"
                Expect.isNonEmpty field.UiName $"UiName for {field.ApiName}"
                Expect.isNonEmpty field.Category $"Category for {field.ApiName}"

                let expected =
                    match field.Kind with
                    | GoogleAnalyticsSchema.Dimension -> "string"
                    | GoogleAnalyticsSchema.Metric -> "number"

                Expect.equal field.DataType expected $"wire type for {field.ApiName}"
        }

        test "categories are the distinct labels in first-appearance order" {
            Expect.isNonEmpty GoogleAnalyticsSchema.categories "categories populated"

            Expect.equal
                (List.length GoogleAnalyticsSchema.categories)
                (GoogleAnalyticsSchema.categories |> Set.ofList |> Set.count)
                "no repeats"

            Expect.equal (List.head GoogleAnalyticsSchema.categories) "Time" "first dimension's category leads"
        }

        test "tryFind resolves a known name and declines an unknown one" {
            match GoogleAnalyticsSchema.tryFind "sessionSource" with
            | Some f -> Expect.equal f.Kind GoogleAnalyticsSchema.Dimension "sessionSource is a dimension"
            | None -> failtest "sessionSource should be in the catalogue"

            Expect.isNone
                (GoogleAnalyticsSchema.tryFind "customEvent:my_custom_thing")
                "a property-specific custom dimension is not in a property-independent catalogue"
        }

        test "every column is nullable, because GA4 omits rows rather than reporting nulls" {
            Expect.isTrue
                (GoogleAnalyticsSchema.columns |> List.forall _.Nullable)
                "any requested field can be absent from a response"
        }
    ]

// ─── IOAuthCredentialFlow binding ────────────────────────────────────

/// Stub for Google's token + revocation endpoints. The token response
/// carries a refresh token (the code-exchange shape); `RefreshAccessToken`
/// tolerates its presence and does not treat the unchanged value as a
/// rotation.
type private StubGoogle(?tokenBody: string, ?revokeStatus: HttpStatusCode, ?revokeBody: string) =
    inherit HttpMessageHandler()

    let tokenBody =
        defaultArg
            tokenBody
            """{"access_token":"ya29.access","refresh_token":"1//refresh","expires_in":3599,"token_type":"Bearer","id_token":"eyJhbGc"}"""

    let requests = ResizeArray<string * string>()

    member _.Requests = requests |> List.ofSeq

    override _.SendAsync(request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
        let path = request.RequestUri.AbsolutePath

        let body =
            match request.Content with
            | null -> ""
            | content -> content.ReadAsStringAsync().GetAwaiter().GetResult()

        lock requests (fun () -> requests.Add(path, body))

        let resp =
            if path.EndsWith "/revoke" then
                let r = new HttpResponseMessage(defaultArg revokeStatus HttpStatusCode.OK)
                r.Content <- new StringContent(defaultArg revokeBody "")
                r
            elif path.EndsWith "/token" then
                let r = new HttpResponseMessage(HttpStatusCode.OK)
                r.Content <- new StringContent(tokenBody)
                r
            else
                new HttpResponseMessage(HttpStatusCode.NotFound)

        Task.FromResult resp

let private mkFlow (secrets: ISecretStore) (refresher: IOAuthTokenRefresher option) (stub: StubGoogle) =
    let httpClient = new HttpClient(stub)
    GoogleOAuthFlow.create httpClient secrets refresher GoogleOAuthFlow.GoogleOAuthFlowConfig.analyticsReadonly

let private flowContractTests =
    let factory () =
        mkFlow (DerivedSecretStore()) None (StubGoogle())

    IOAuthCredentialFlowContract.tests "GoogleOAuthFlow" factory

let private flowCtx: OAuthFlowContext =
    OAuthFlowContext.forDataSource "team-scope-1" "ds-1" None

let private flowTests =
    testList "GoogleOAuthFlow — Google specifics" [

        test "declares PKCE support" {
            let flow = mkFlow (DerivedSecretStore()) None (StubGoogle())
            Expect.isTrue flow.SupportsPkce "Google accepts PKCE on the web-server flow"
        }

        test "flow name and scope match the connector's expectations" {
            let flow = mkFlow (DerivedSecretStore()) None (StubGoogle())
            Expect.equal flow.Name "google-analytics" "flow name is the connector's FlowName default"

            Expect.equal
                flow.Descriptor.Scopes
                [ "https://www.googleapis.com/auth/analytics.readonly" ]
                "read-only Analytics scope"
        }

        testCaseAsync "authorize URL carries access_type=offline and prompt=consent"
        <| async {
            let flow = mkFlow (DerivedSecretStore()) None (StubGoogle())
            let redirect = "https://example.com/api/oauth/google-analytics/callback"

            match! flow.BuildAuthorizeUrl(flowCtx, "state-1", redirect, None) with
            | Error err -> failtestf "BuildAuthorizeUrl failed: %s" (OAuthError.toMessage err)
            | Ok url ->
                // Both are mandatory and each fails silently in its own
                // way when omitted — see the flow's module note.
                Expect.stringContains url "access_type=offline" "without it Google issues no refresh token at all"

                Expect.stringContains
                    url
                    "prompt=consent"
                    "without it Google issues no refresh token on a RE-consent, which is the trap"

                Expect.stringContains url "response_type=code" "authorization-code flow"
                Expect.stringContains url "analytics.readonly" "requested scope"
        }

        testCaseAsync "authorize URL omits PKCE parameters when the substrate supplied no challenge"
        <| async {
            let flow = mkFlow (DerivedSecretStore()) None (StubGoogle())
            let redirect = "https://example.com/api/oauth/google-analytics/callback"

            match! flow.BuildAuthorizeUrl(flowCtx, "state-1", redirect, None) with
            | Error err -> failtestf "BuildAuthorizeUrl failed: %s" (OAuthError.toMessage err)
            | Ok url -> Expect.isFalse (url.Contains "code_challenge") "no challenge in, no challenge out"
        }

        testCaseAsync "ExchangeCode sends the code verifier when the substrate supplied one"
        <| async {
            let stub = StubGoogle()
            let flow = mkFlow (DerivedSecretStore()) None stub
            let redirect = "https://example.com/api/oauth/google-analytics/callback"

            let! _ = flow.ExchangeCode(flowCtx, "auth-code", redirect, Some "the-verifier")

            let tokenBodies =
                stub.Requests |> List.filter (fun (p, _) -> p.EndsWith "/token") |> List.map snd

            match tokenBodies with
            | [ body ] ->
                Expect.stringContains body "code_verifier=the-verifier" "verifier reaches the token endpoint"
                Expect.stringContains body "grant_type=authorization_code" "code exchange"
            | other -> failtestf "expected one token request, got %A" other
        }

        testCaseAsync "ExchangeCode names the offline-access cause when Google returns no refresh token"
        <| async {
            // The failure mode the flow's module note describes: a
            // re-consent without `prompt=consent` returns an access
            // token and nothing else.
            let stub =
                StubGoogle(tokenBody = """{"access_token":"ya29.access","expires_in":3599}""")

            let flow = mkFlow (DerivedSecretStore()) None stub

            match! flow.ExchangeCode(flowCtx, "auth-code", "https://example.com/cb", Some "v") with
            | Ok _ -> failtest "a credential with no refresh token cannot satisfy the substrate"
            | Error(OAuthFlowFailed msg) ->
                Expect.stringContains msg "access_type=offline" "the diagnostic names the first cause"
                Expect.stringContains msg "prompt=consent" "and the second"
            | Error other -> failtestf "expected OAuthFlowFailed, got %A" other
        }

        testCaseAsync "a provider error keeps Google's own error code in the message"
        <| async {
            let stub =
                StubGoogle(
                    tokenBody = """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}"""
                )

            let flow = mkFlow (DerivedSecretStore()) None stub

            match! flow.RefreshAccessToken(flowCtx, "stale-refresh") with
            | Ok _ -> failtest "expected the provider rejection to surface"
            | Error(ProviderRejected msg) ->
                // Operators recognise `invalid_grant` faster than any
                // translation of it, so it is passed through verbatim.
                Expect.stringContains msg "invalid_grant" "provider error code preserved"
            | Error other -> failtestf "expected ProviderRejected, got %A" other
        }

        testCaseAsync "missing client credentials name the key to fill"
        <| async {
            let flow = mkFlow (EmptySecretStore()) None (StubGoogle())

            match! flow.BuildAuthorizeUrl(flowCtx, "state-1", "https://example.com/cb", None) with
            | Ok _ -> failtest "expected ClientCredentialMissing"
            | Error(ClientCredentialMissing key) ->
                Expect.equal key "google-analytics-client-id-ds-1" "names the client-id key"
            | Error other -> failtestf "expected ClientCredentialMissing, got %A" other
        }

        testCaseAsync "Revoke unregisters the Phase 10h descriptor before the substrate deletes the token"
        <| async {
            let refresher = RecordingRefresher()

            let flow =
                mkFlow (DerivedSecretStore()) (Some(refresher :> IOAuthTokenRefresher)) (StubGoogle())

            match! flow.Revoke(flowCtx, "1//refresh") with
            | Error err -> failtestf "Revoke failed: %s" (OAuthError.toMessage err)
            | Ok() ->
                Expect.equal
                    refresher.Unregistered
                    [ "google-analytics", "ds-1" ]
                    "the descriptor is cancelled while its token is still readable"
        }

        testCaseAsync "Revoke unregisters even when Google's revocation endpoint fails"
        <| async {
            let refresher = RecordingRefresher()

            let flow =
                mkFlow
                    (DerivedSecretStore())
                    (Some(refresher :> IOAuthTokenRefresher))
                    (StubGoogle(revokeStatus = HttpStatusCode.InternalServerError))

            let! _ = flow.Revoke(flowCtx, "1//refresh")

            // A Google outage must not leave a live refresh job behind
            // for a connector the operator has disconnected.
            Expect.equal
                refresher.Unregistered
                [ "google-analytics", "ds-1" ]
                "unregistration is unconditional and precedes the network call"
        }

        testCaseAsync "Revoke treats an already-invalid token as success"
        <| async {
            let flow =
                mkFlow
                    (DerivedSecretStore())
                    None
                    (StubGoogle(revokeStatus = HttpStatusCode.BadRequest, revokeBody = """{"error":"invalid_token"}"""))

            match! flow.Revoke(flowCtx, "already-gone") with
            | Ok() -> ()
            | Error err ->
                failtestf
                    "an already-revoked token is the desired end state, not a failure: %s"
                    (OAuthError.toMessage err)
        }

        testCaseAsync "Revoke works without a refresher composed"
        <| async {
            let flow = mkFlow (DerivedSecretStore()) None (StubGoogle())

            match! flow.Revoke(flowCtx, "1//refresh") with
            | Ok() -> ()
            | Error err -> failtestf "Revoke must not require a refresher: %s" (OAuthError.toMessage err)
        }
    ]

// ─── Env-gated real-API smoke ────────────────────────────────────────
//
// Runs the connector against a real GA4 property when all four of
// `TOOLUP_GA4_CLIENT_ID`, `TOOLUP_GA4_CLIENT_SECRET`,
// `TOOLUP_GA4_REFRESH_TOKEN` and `TOOLUP_GA4_PROPERTY_ID` are set. With
// any of them unset the arm reports Pending, so a fresh checkout is
// green without credentials while a CI job that was SUPPOSED to have
// them shows "skipped" rather than a silent pass.

/// Secret store serving one source's credentials from the environment.
type private EnvSecretStore(clientId: string, clientSecret: string, refreshToken: string) =
    interface ISecretStore with
        member _.GetSecret(_scope, key) = async {
            if key.StartsWith("google-analytics-client-id-", StringComparison.Ordinal) then
                return Some clientId
            elif key.StartsWith("google-analytics-client-secret-", StringComparison.Ordinal) then
                return Some clientSecret
            elif key.StartsWith("google-analytics-refresh-", StringComparison.Ordinal) then
                return Some refreshToken
            else
                return None
        }

        member _.SetSecret(_scope, _key, _value) = async { return Ok() }
        member _.DeleteSecret(_scope, _key) = async { return Ok() }
        member _.ListKeys(_scope) = async { return [] }

let private env (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some v

let private liveTests =
    match
        env "TOOLUP_GA4_CLIENT_ID",
        env "TOOLUP_GA4_CLIENT_SECRET",
        env "TOOLUP_GA4_REFRESH_TOKEN",
        env "TOOLUP_GA4_PROPERTY_ID"
    with
    | Some clientId, Some clientSecret, Some refreshToken, Some propertyId ->
        let secrets = EnvSecretStore(clientId, clientSecret, refreshToken) :> ISecretStore

        let source =
            create
                secrets
                (GoogleAnalyticsLiveTransport.create "ToolUp.Platform test suite")
                None
                GoogleAnalyticsSourceConfig.standard

        let ctx = mkCtx "live" [ "property_id", propertyId ]

        testList "GoogleAnalytics — live API" [

            testCaseAsync "Connect mints an access token from the stored refresh token"
            <| async {
                match! source.Connect ctx with
                | Ok() -> ()
                | Error err -> failtestf "Connect failed: %A" err
            }

            testCaseAsync "ListTables enumerates the account's GA4 properties"
            <| async {
                match! source.ListTables ctx with
                | Ok tables ->
                    Expect.isNonEmpty tables "the credential should see at least one property"

                    Expect.isTrue
                        (tables
                         |> List.forall (fun t -> t.StartsWith("properties/", StringComparison.Ordinal)))
                        "every entry is a property resource name"
                | Error err -> failtestf "ListTables failed: %A" err
            }

            testCaseAsync "Query runs the default report and returns the documented envelope"
            <| async {
                match! source.Query(ctx, propertyId) with
                | Error err -> failtestf "Query failed: %A" err
                | Ok bytes ->
                    use doc = JsonDocument.Parse(Encoding.UTF8.GetString bytes)
                    let root = doc.RootElement

                    Expect.equal
                        (root.GetProperty("property").GetString())
                        (GoogleAnalyticsQuery.normaliseProperty propertyId)
                        "envelope echoes the property queried"

                    Expect.isTrue (root.TryGetProperty("rows") |> fst) "envelope carries a rows array"

                    Expect.isTrue
                        (root.GetProperty("metricHeaders").GetArrayLength() > 0)
                        "the default report requested a metric and got a header for it"
            }
        ]
    | _ ->
        testList "GoogleAnalytics — live API" [
            ptestCase "skipped — TOOLUP_GA4_CLIENT_ID / _CLIENT_SECRET / _REFRESH_TOKEN / _PROPERTY_ID not all set"
            <| fun _ -> ()
        ]

let tests =
    testList "GoogleAnalytics" [
        contractTests
        connectorTests
        queryTests
        schemaTests
        flowContractTests
        flowTests
        liveTests
    ]