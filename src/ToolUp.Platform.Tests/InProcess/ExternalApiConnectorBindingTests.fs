module ToolUp.Platform.Tests.InProcess.ExternalApiConnectorBindingTests

open System
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.Transport
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.IExternalApiConnectorContract
open ToolUp.DataSources.Common

// ─── Phase 128 — IExternalApiConnector contract bindings ─────────────
//
// Two connectors of deliberately different shape run the same pack:
//
// | | MockConnector | LinkConnector |
// |---|---|---|
// | wire | `id|mark;…` text | JSON envelope (`data` + `links.next`) |
// | paging | opaque token (`after=<id>`) | absolute next-link URL |
// | auth | `Authorization: Bearer` | `X-Api-Key` |
// | watermark | `tNNNN` string | zero-padded sequence number |
// | rows | one record type | a DU over two endpoints |
// | endpoints | incremental + full refresh | incremental `events`, full-refresh `accounts` |
// | back-pressure | nothing declared | a rate budget AND a retry policy |
//
// LinkConnector's transport goes through `ExternalApi.withBudget`, so its
// declared budget is what the pack's calls run under.

// ── the second provider: a JSON API paging by next-link ──────────

type LinkRow =
    | Event of seq: int64 * kind: string
    | Account of id: string * name: string

let private linkBase = "https://api.link.test"

/// An in-memory JSON API. `GET /v2/<endpoint>?page=<n>&limit=<k>[&since=<seq>]`
/// answers `{ "data": [...], "links": { "next": <absolute URL or null> } }`;
/// a request without `X-Api-Key: link-key` is a 401.
type private LinkProvider(events: (int64 * string) list, accounts: (string * string) list, limit: int) =
    let query (url: Uri) =
        url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun pair ->
            match pair.Split('=', 2) with
            | [| k; v |] -> Some(k, Uri.UnescapeDataString v)
            | _ -> None)
        |> Map.ofArray

    interface IHttpTransport with
        member _.Send request = async {
            if not (request.Headers |> List.contains ("X-Api-Key", "link-key")) then
                return {
                    StatusCode = 401
                    Headers = []
                    Body = """{"error":"unauthorised"}"""
                }
            else
                let url = Uri(Uri linkBase, request.Url)
                let q = query url
                let page = q |> Map.tryFind "page" |> Option.map int |> Option.defaultValue 1
                let endpoint = url.AbsolutePath.Substring("/v2/".Length)
                let since = q |> Map.tryFind "since"

                let items =
                    match endpoint with
                    | "events" ->
                        events
                        |> List.filter (fun (seq, _) ->
                            match since with
                            | Some s -> seq > int64 s
                            | None -> true)
                        |> List.map (fun (seq, kind) -> $"""{{"seq":{seq},"kind":"{kind}"}}""")
                    | _ -> accounts |> List.map (fun (id, name) -> $"""{{"id":"{id}","name":"{name}"}}""")

                let pageItems =
                    items
                    |> List.skip (min ((page - 1) * limit) items.Length)
                    |> List.truncate limit

                let next =
                    if page * limit < items.Length then
                        let sinceQuery =
                            match since with
                            | Some s -> "&since=" + s
                            | None -> ""

                        $"\"{linkBase}/v2/{endpoint}?page={page + 1}&limit={limit}{sinceQuery}\""
                    else
                        "null"

                return {
                    StatusCode = 200
                    Headers = [ "Content-Type", "application/json" ]
                    Body = $"""{{"data":[{String.concat "," pageItems}],"links":{{"next":{next}}}}}"""
                }
        }

/// The second connector: JSON, next-link paging, an API-key header, a DU
/// row type, a numeric watermark, and a full back-pressure declaration.
type LinkConnector() =
    static member Window: RateLimitDescriptor = {
        Provider = "linkapi"
        ShortWindow = 1_000, TimeSpan.FromMinutes 1.0
        LongWindow = Some(100_000, TimeSpan.FromDays 1.0)
        FairnessMode = PerScope
    }

    interface IExternalApiConnector<LinkRow> with
        member _.Provider = {
            Id = "linkapi"
            DisplayName = "Link API"
        }

        member _.Endpoints = [
            {
                Name = "events"
                Description = "Events, read incrementally by sequence number."
                Sync = SyncMode.Incremental
            }
            {
                Name = "accounts"
                Description = "Accounts, re-read whole."
                Sync = SyncMode.FullRefresh
            }
        ]

        member _.BackPressure = {
            RateBudget =
                Some {
                    OutboundRateBudget.ofWindow LinkConnector.Window with
                        MaxConcurrency = Some 2
                }
            Retry =
                Some {
                    MaxAttempts = 4
                    InitialBackoff = TimeSpan.FromMilliseconds 250.0
                    MaxBackoff = TimeSpan.FromSeconds 10.0
                    Timeout = Some(TimeSpan.FromSeconds 30.0)
                }
        }

        member _.PageRequest(call, cursor) =
            let headers = [ "X-Api-Key", call.Credential ]

            match cursor with
            | PageCursor.First ->
                let since =
                    match call.Watermark with
                    | Some w -> "&since=" + string (Int64.Parse w)
                    | None -> ""

                Ok(HttpCall.get $"/v2/{call.Endpoint}?page=1&limit=3{since}" headers)
            | PageCursor.NextUrl url -> Ok(HttpCall.get url headers)
            | other -> Error $"the Link API pages by next-link only, not %A{other}"

        member _.DecodePage(call, response) =
            try
                use doc = JsonDocument.Parse response.Body
                let root = doc.RootElement

                let rows = [
                    for item in root.GetProperty("data").EnumerateArray() do
                        match call.Endpoint with
                        | "events" -> Event(item.GetProperty("seq").GetInt64(), item.GetProperty("kind").GetString())
                        | _ -> Account(item.GetProperty("id").GetString(), item.GetProperty("name").GetString())
                ]

                let next =
                    match root.GetProperty("links").GetProperty("next") with
                    | link when link.ValueKind = JsonValueKind.String -> Some(PageCursor.NextUrl(link.GetString()))
                    | _ -> None

                Ok { Rows = rows; Next = next }
            with ex ->
                Error $"not a Link API page: {ex.Message}"

        member _.Watermark row =
            match row with
            | Event(seq, _) -> Some(seq.ToString("D12"))
            | Account _ -> None

let private noopLogger =
    { new ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(_: string) = ()
        member _.Error(_: string, _: exn option) = ()
    }

let private linkFactory (endpoint: string) () =
    let connector = LinkConnector() :> IExternalApiConnector<LinkRow>

    let provider =
        LinkProvider(
            [ for i in 1L .. 7L -> i, (if i % 2L = 0L then "opened" else "closed") ],
            [ for i in 1..4 -> $"acc-{i}", $"Account {i}" ],
            3
        )

    let limiter =
        InProcessRateLimiter(
            [ LinkConnector.Window ],
            NoOpMetricsSink() :> IMetricsSink,
            AuditLog.NoOpAuditLog() :> IAuditLog,
            TimeSpan.FromSeconds 5.0,
            noopLogger
        )
        :> IRateLimiter

    let call: ExternalApiCall = {
        Endpoint = endpoint
        ScopeId = "team-a"
        ConnectionScope = Map.empty
        Credential = "link-key"
        Watermark = None
    }

    connector, ExternalApi.withBudget limiter TimeProvider.System connector "team-a" None provider, call

let private mockFactory () =
    let provider = MockProvider(MockStyle.Token, 2)
    provider.Add [ for i in 1..5 -> { Id = i; UpdatedAt = $"t%04d{i}" } ]

    let call: ExternalApiCall = {
        Endpoint = "items"
        ScopeId = "team-a"
        ConnectionScope = Map.empty
        Credential = "secret"
        Watermark = None
    }

    MockConnector() :> IExternalApiConnector<MockRow>, provider :> IHttpTransport, call

let tests =
    Expecto.Tests.testList "IExternalApiConnector bindings" [
        IExternalApiConnectorContract.tests "MockConnector (token paging, text wire)" mockFactory 5
        IExternalApiConnectorContract.tests
            "LinkConnector events (next-link paging, JSON, incremental)"
            (linkFactory "events")
            7
        IExternalApiConnectorContract.tests
            "LinkConnector accounts (next-link paging, JSON, full refresh)"
            (linkFactory "accounts")
            4
    ]