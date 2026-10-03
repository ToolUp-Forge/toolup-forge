module ToolUp.Platform.Tests.Contracts.IExternalApiConnectorContract

open System
open System.Collections.Generic
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.EntityStore
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Metrics
open ToolUp.Platform.Secrets
open ToolUp.Platform.Transport
open ToolUp.DataSources.Common

// ─── Phase 128 — outbound API connector kit contract ─────────────
//
// The kit's behaviour pinned against an in-memory third-party API: a mock
// provider serving a resource in pages (opaque-token, offset and next-URL
// continuation styles), with scripted failures; a mock connector that is
// the thin provider mapping the authoring guide describes; and the shared
// machinery under it — paged fetch, retry as data, the outbound rate
// budget, and incremental sync cursors over a real `IEntityStore`.
//
// `tests` holds the kit-level cases; `connectorTests` is the parametrised
// half an `IExternalApiConnector` implementation binds to, given a
// transport serving a known resource.

// ── the mock provider ────────────────────────────────────────────

type MockRow = { Id: int; UpdatedAt: string }

[<RequireQualifiedAccess>]
type MockStyle =
    | Token
    | Offset
    | NextLink

let private mark (n: int) = $"t%04d{n}"

/// An in-memory paged API. Rows are served in `Id` order, `pageSize` at a
/// time; `since=<mark>` filters to rows updated after the mark; a request
/// without `Authorization: Bearer secret` is a 401. Scripted responses are
/// served, in order, before the real page.
type MockProvider(style: MockStyle, pageSize: int) =
    let gate = obj ()
    let rows = ResizeArray<MockRow>()
    let scripted = Queue<unit -> HttpResponse>()
    let requests = ResizeArray<HttpRequest>()

    let query (url: string) =
        match url.IndexOf '?' with
        | -1 -> Map.empty
        | i ->
            url.Substring(i + 1).Split('&', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun pair ->
                match pair.Split('=', 2) with
                | [| k; v |] -> k, Uri.UnescapeDataString v
                | parts -> parts[0], "")
            |> Map.ofArray

    let serve (request: HttpRequest) : HttpResponse =
        let authorised = request.Headers |> List.contains ("Authorization", "Bearer secret")

        if not authorised then
            {
                StatusCode = 401
                Headers = []
                Body = "unauthorised"
            }
        else
            let q = query request.Url

            let visible =
                rows
                |> Seq.filter (fun r ->
                    match Map.tryFind "since" q with
                    | Some since -> String.CompareOrdinal(r.UpdatedAt, since) > 0
                    | None -> true)
                |> Seq.sortBy _.Id
                |> List.ofSeq

            let start =
                match Map.tryFind "after" q, Map.tryFind "offset" q with
                | Some after, _ ->
                    let afterId = int after

                    visible
                    |> List.tryFindIndex (fun r -> r.Id > afterId)
                    |> Option.defaultValue visible.Length
                | None, Some offset -> int offset
                | None, None -> 0

            let page = visible |> List.skip (min start visible.Length) |> List.truncate pageSize
            let nextStart = start + pageSize

            let next =
                if nextStart >= visible.Length || page.IsEmpty then
                    []
                else
                    match style with
                    | MockStyle.Token -> [ "x-next-token", string (List.last page).Id ]
                    | MockStyle.Offset -> [ "x-next-offset", string nextStart ]
                    | MockStyle.NextLink ->
                        let since =
                            match Map.tryFind "since" q with
                            | Some s -> "&since=" + s
                            | None -> ""

                        [ "x-next-url", $"https://mock.test/items?offset={nextStart}{since}" ]

            {
                StatusCode = 200
                Headers = next
                Body = page |> List.map (fun r -> $"{r.Id}|{r.UpdatedAt}") |> String.concat ";"
            }

    member _.Add(newRows: MockRow seq) =
        lock gate (fun () -> rows.AddRange newRows)

    member _.Script(response: unit -> HttpResponse) =
        lock gate (fun () -> scripted.Enqueue response)

    member _.Requests = lock gate (fun () -> List.ofSeq requests)

    interface IHttpTransport with
        member _.Send request = async {
            let respond =
                lock gate (fun () ->
                    requests.Add request

                    if scripted.Count > 0 then
                        scripted.Dequeue()
                    else
                        fun () -> serve request)

            return respond ()
        }

let private rowsUpTo (n: int) = [ for i in 1..n -> { Id = i; UpdatedAt = mark i } ]

// ── the mock connector: the thin provider mapping ────────────────

/// What a provider companion writes: two pure functions and two lists.
type MockConnector() =
    interface IExternalApiConnector<MockRow> with
        member _.Provider = {
            Id = "mockapi"
            DisplayName = "Mock API"
        }

        member _.Endpoints = [
            {
                Name = "items"
                Description = "Items, read incrementally by update mark."
                Sync = SyncMode.Incremental
            }
            {
                Name = "snapshot"
                Description = "Items, re-read whole."
                Sync = SyncMode.FullRefresh
            }
        ]

        member _.PageRequest(call, cursor) =
            let since =
                call.Watermark
                |> Option.map (fun w -> "since=" + Uri.EscapeDataString w)
                |> Option.toList

            let withQuery (path: string) (pairs: string list) =
                match pairs with
                | [] -> path
                | _ -> path + "?" + String.concat "&" pairs

            let headers = [ "Authorization", "Bearer " + call.Credential ]

            match cursor with
            | PageCursor.First -> Ok(HttpRequest.get (withQuery "/items" since) headers)
            | PageCursor.Token token -> Ok(HttpRequest.get (withQuery "/items" (("after=" + token) :: since)) headers)
            | PageCursor.Offset offset -> Ok(HttpRequest.get (withQuery "/items" ($"offset={offset}" :: since)) headers)
            | PageCursor.NextUrl url -> Ok(HttpRequest.get url headers)

        member _.DecodePage(_, response) =
            let rows =
                if response.Body = "" then
                    Ok []
                else
                    response.Body.Split ';'
                    |> Array.fold
                        (fun acc item ->
                            match acc, item.Split '|' with
                            | Ok rows, [| id; updated |] ->
                                match Int32.TryParse id with
                                | true, id -> Ok({ Id = id; UpdatedAt = updated } :: rows)
                                | _ -> Error $"bad id '{id}'"
                            | Ok _, _ -> Error $"bad item '{item}'"
                            | error, _ -> error)
                        (Ok [])
                    |> Result.map List.rev

            let header name =
                response.Headers |> List.tryFind (fst >> (=) name) |> Option.map snd

            let next =
                match header "x-next-token", header "x-next-offset", header "x-next-url" with
                | Some token, _, _ -> Some(PageCursor.Token token)
                | _, Some offset, _ -> Some(PageCursor.Offset(int offset))
                | _, _, Some url -> Some(PageCursor.NextUrl url)
                | _ -> None

            rows |> Result.map (fun rows -> { Rows = rows; Next = next })

        member _.Watermark row = Some row.UpdatedAt

// ── fixtures ─────────────────────────────────────────────────────

let private fastRetry attempts : RetryPolicy = {
    MaxAttempts = attempts
    InitialBackoff = TimeSpan.Zero
    MaxBackoff = TimeSpan.Zero
    Timeout = None
}

let private options: PagedFetchOptions = { Retry = fastRetry 3; MaxPages = None }

let private call: ExternalApiCall = {
    Endpoint = "items"
    ScopeId = "team-a"
    ConnectionScope = Map.empty
    Credential = "secret"
    Watermark = None
}

let private connector = MockConnector() :> IExternalApiConnector<MockRow>

let private endpointFor (call: ExternalApiCall) = ExternalApi.endpoint connector call

let private noopLogger =
    { new ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(_: string) = ()
        member _.Error(_: string, _: exn option) = ()
    }

let private limiterOf (descriptors: RateLimitDescriptor list) : IRateLimiter =
    InProcessRateLimiter(
        descriptors,
        NoOpMetricsSink() :> IMetricsSink,
        AuditLog.NoOpAuditLog() :> IAuditLog,
        TimeSpan.FromSeconds 5.0,
        noopLogger
    )
    :> IRateLimiter

let private secretStore (scope: string) (key: string) (value: string) : ISecretStore =
    { new ISecretStore with
        member _.GetSecret(s, k) = async { return if s = scope && k = key then Some value else None }
        member _.SetSecret(_, _, _) = async { return Error "read-only" }
        member _.DeleteSecret(_, _) = async { return Error "read-only" }
        member _.ListKeys _ = async { return [ key ] }
    }

/// A fresh blob-backed entity store with the cursor entity registered —
/// the "durable" store a restarted process re-opens by directory.
let private openCursorStore (directory: string) : IEntityStore =
    Directory.CreateDirectory directory |> ignore
    let blob = LocalFileStorage.LocalFileStorage(directory) :> IBlobStorage
    let dos = DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityRegistry()
    registry.Register<SyncCursorEntity>(SyncCursor.registration)
    BlobEntityStore(dos, blob, registry, None) :> IEntityStore

let private tempDir () =
    Path.Combine(Path.GetTempPath(), "toolup-sync-cursor-" + Guid.NewGuid().ToString("N"))

let private source: DataSourceConfig = {
    Id = "src-1"
    Name = "Mock source"
    Kind = "mockapi"
    ConnectionScope = Map.ofList [ "account", "acme" ]
    CredentialKey = "mockapi-token"
    Tables = None
    Tags = Map.empty
}

/// Incremental-sync deps over `provider`, ingesting into `sink`. Built
/// fresh per "process" — nothing but the entity store's directory survives.
let private depsFor
    (provider: IHttpTransport)
    (store: IEntityStore)
    (sink: ResizeArray<MockRow>)
    (maxPages: int option)
    : IncrementalSyncDeps<MockRow> =
    {
        Connector = connector
        Transport = provider
        Cursors = store
        SecretStore = Some(secretStore "team-a" "mockapi-token" "secret")
        Options = { options with MaxPages = maxPages }
        Ingest =
            fun _ rows -> async {
                lock sink (fun () -> sink.AddRange rows)
                return Ok()
            }
    }

let private request: IncrementalSyncRequest = {
    Source = source
    Endpoint = "items"
    ScheduledBy = "scheduler-test"
}

let private ok429 (retryAfter: string option) () : HttpResponse = {
    StatusCode = 429
    Headers = retryAfter |> Option.map (fun v -> "Retry-After", v) |> Option.toList
    Body = "slow down"
}

let private status (code: int) () : HttpResponse = {
    StatusCode = code
    Headers = []
    Body = $"status {code}"
}

// ── the parametrised half: any connector over a known resource ───

/// Bind an `IExternalApiConnector` to the shared machinery's contract.
/// `factory ()` returns the connector, a transport serving its `endpoint`
/// with exactly `expectedRows` rows across more than one page, and the
/// resolved call to read it with.
let connectorTests
    (name: string)
    (factory: unit -> IExternalApiConnector<'Row> * IHttpTransport * ExternalApiCall)
    (expectedRows: int)
    =
    testList $"{name} — IExternalApiConnector contract" [
        testCaseAsync "declares a provider id and at least one endpoint"
        <| async {
            let connector, _, _ = factory ()
            Expect.isFalse (String.IsNullOrWhiteSpace connector.Provider.Id) "provider id is set"
            Expect.isNonEmpty connector.Endpoints "endpoint catalogue is non-empty"
        }

        testCaseAsync "reads the whole resource across pages, in order, with no failure"
        <| async {
            let connector, transport, call = factory ()
            let! outcome = ExternalApi.fetchAll options transport connector call PageCursor.First
            Expect.isNone outcome.Failure "no page failed"
            Expect.isNone outcome.ResumeFrom "the resource is exhausted"
            Expect.isGreaterThan outcome.Pages 1 "more than one page"
            Expect.hasLength outcome.State expectedRows "every row read exactly once"
        }

        testCaseAsync "a page cap stops with a resume point that completes the read"
        <| async {
            let connector, transport, call = factory ()
            let capped = { options with MaxPages = Some 1 }
            let! first = ExternalApi.fetchAll capped transport connector call PageCursor.First
            Expect.equal first.Pages 1 "one page taken"
            Expect.isSome first.ResumeFrom "a resume point is reported"
            let! rest = ExternalApi.fetchAll options transport connector call first.ResumeFrom.Value
            Expect.hasLength (first.State @ rest.State) expectedRows "the two runs read every row once"
        }

        testCaseAsync "PageRequest and DecodePage are pure — the same inputs give the same request"
        <| async {
            let connector, _, call = factory ()
            let a = connector.PageRequest(call, PageCursor.First)
            let b = connector.PageRequest(call, PageCursor.First)
            Expect.equal a b "request building is deterministic (GP 12 rule 4)"
        }
    ]

// ── the kit-level cases ──────────────────────────────────────────

let private pagedFetchTests =
    testList "PagedFetch" [
        for style in [ MockStyle.Token; MockStyle.Offset; MockStyle.NextLink ] do
            testCaseAsync $"multi-page read ({style} continuation) yields every row in order"
            <| async {
                let provider = MockProvider(style, 2)
                provider.Add(rowsUpTo 5)
                let! outcome = PagedFetch.all options provider (endpointFor call) PageCursor.First
                Expect.equal (outcome.State |> List.map _.Id) [ 1..5 ] "rows in provider order"
                Expect.equal outcome.Pages 3 "three pages of two"
                Expect.isNone outcome.Failure "no failure"
                Expect.isNone outcome.ResumeFrom "complete"
            }

        testCaseAsync "an empty resource is one empty page, complete"
        <| async {
            let provider = MockProvider(MockStyle.Token, 2)
            let! outcome = PagedFetch.all options provider (endpointFor call) PageCursor.First
            Expect.isEmpty outcome.State "no rows"
            Expect.equal outcome.Pages 1 "the empty page was taken"
            Expect.isNone outcome.Failure "an empty page is not a failure"
            Expect.isNone outcome.ResumeFrom "complete"
        }

        testCaseAsync "a mid-read error keeps the earlier pages and reports the failing page"
        <| async {
            let provider = MockProvider(MockStyle.Offset, 2)
            provider.Add(rowsUpTo 5)
            // Page 1 serves normally; the second request is a 404.
            let! first = PagedFetch.page options provider (endpointFor call) PageCursor.First
            Expect.isOk first "page 1 reads"
            provider.Script(status 404)
            let! outcome = PagedFetch.all options provider (endpointFor call) (PageCursor.Offset 2)

            match outcome.Failure with
            | Some {
                       PageIndex = 0
                       Cursor = PageCursor.Offset 2
                       Failure = PageFailure.Transport(TransportError.Permanent(404, _))
                   } -> ()
            | other -> failtestf "expected a located 404 on the offset-2 page, got %A" other

            Expect.equal outcome.ResumeFrom (Some(PageCursor.Offset 2)) "resume at the failing page"
        }

        testCaseAsync "a failure on page 2 does not discard page 1's rows; resuming completes the read"
        <| async {
            let provider = MockProvider(MockStyle.Token, 2)
            provider.Add(rowsUpTo 5)

            // Fail only the SECOND request (the page after "after=2").
            let endpoint = endpointFor call
            let mutable calls = 0

            let flaky =
                { new IHttpTransport with
                    member _.Send request = async {
                        calls <- calls + 1

                        if calls = 2 then
                            return status 400 ()
                        else
                            return! (provider :> IHttpTransport).Send request
                    }
                }

            let! outcome = PagedFetch.all options flaky endpoint PageCursor.First
            Expect.equal (outcome.State |> List.map _.Id) [ 1; 2 ] "page 1's rows are kept"
            Expect.equal outcome.Pages 1 "one page taken"
            Expect.equal outcome.ResumeFrom (Some(PageCursor.Token "2")) "resume from the failing page"

            match outcome.Failure with
            | Some {
                       PageIndex = 1
                       Failure = PageFailure.Transport(TransportError.Permanent(400, _))
                   } -> ()
            | other -> failtestf "expected a located 400 on page index 1, got %A" other

            let! rest = PagedFetch.all options flaky endpoint outcome.ResumeFrom.Value
            Expect.equal (outcome.State @ rest.State |> List.map _.Id) [ 1..5 ] "no row lost or repeated"
        }

        testCaseAsync "an undecodable page is a Decode failure, not an exception"
        <| async {
            let provider = MockProvider(MockStyle.Token, 2)

            provider.Script(fun () -> {
                StatusCode = 200
                Headers = []
                Body = "not|a|row"
            })

            let! outcome = PagedFetch.all options provider (endpointFor call) PageCursor.First

            match outcome.Failure with
            | Some { Failure = PageFailure.Decode _ } -> ()
            | other -> failtestf "expected a Decode failure, got %A" other
        }

        testCaseAsync "a provider that returns the cursor it was asked for is a Stalled failure"
        <| async {
            let provider = MockProvider(MockStyle.Token, 2)

            provider.Script(fun () -> {
                StatusCode = 200
                Headers = [ "x-next-token", "7" ]
                Body = ""
            })

            let! outcome = PagedFetch.all options provider (endpointFor call) (PageCursor.Token "7")

            match outcome.Failure with
            | Some {
                       Failure = PageFailure.Stalled(PageCursor.Token "7")
                   } -> ()
            | other -> failtestf "expected Stalled, got %A" other
        }

        testCaseAsync "a request the connector cannot build is a Request failure and sends nothing"
        <| async {
            let provider = MockProvider(MockStyle.Token, 2)

            let endpoint: PagedEndpoint<MockRow> = {
                endpointFor call with
                    Request = fun _ -> Error "account is not configured"
            }

            let! outcome = PagedFetch.all options provider endpoint PageCursor.First

            match outcome.Failure with
            | Some {
                       Failure = PageFailure.Request "account is not configured"
                   } -> ()
            | other -> failtestf "expected a Request failure, got %A" other

            Expect.isEmpty provider.Requests "nothing was sent"
        }

        testCase "PageCursor encode/tryDecode round-trips every style"
        <| fun () ->
            for cursor in
                [
                    PageCursor.First
                    PageCursor.Token "abc:def"
                    PageCursor.Offset 40
                    PageCursor.NextUrl "https://x.test/a?b=c"
                ] do
                Expect.equal (PageCursor.tryDecode (PageCursor.encode cursor)) (Some cursor) $"%A{cursor} round-trips"

            Expect.isNone (PageCursor.tryDecode "bogus") "an unknown form decodes to None"
            Expect.isNone (PageCursor.tryDecode "offset:-1") "a negative offset decodes to None"
    ]

let private retryTests =
    testList "retry as data" [
        testCaseAsync "a 503 then success retries and succeeds"
        <| async {
            let provider = MockProvider(MockStyle.Token, 10)
            provider.Add(rowsUpTo 3)
            provider.Script(status 503)
            let! outcome = PagedFetch.all options provider (endpointFor call) PageCursor.First
            Expect.isNone outcome.Failure "recovered"
            Expect.hasLength outcome.State 3 "all rows"
            Expect.hasLength provider.Requests 2 "one retry"
        }

        testCaseAsync "a 401 is permanent — one attempt, no retry"
        <| async {
            let provider = MockProvider(MockStyle.Token, 10)
            let unauthorised = { call with Credential = "wrong" }
            let! outcome = PagedFetch.all options provider (endpointFor unauthorised) PageCursor.First

            match outcome.Failure with
            | Some {
                       Failure = PageFailure.Transport(TransportError.Permanent(401, _))
                   } -> ()
            | other -> failtestf "expected Permanent 401, got %A" other

            Expect.hasLength provider.Requests 1 "not retried"
        }

        testCaseAsync "persistent 5xx exhausts the policy: Exhausted(MaxAttempts, last)"
        <| async {
            let provider = MockProvider(MockStyle.Token, 10)

            for _ in 1..3 do
                provider.Script(status 502)

            let! outcome = PagedFetch.all options provider (endpointFor call) PageCursor.First

            match outcome.Failure with
            | Some {
                       Failure = PageFailure.Transport(TransportError.Exhausted(3,
                                                                                TransportError.Transient(502, _, None)))
                   } -> ()
            | other -> failtestf "expected Exhausted(3, 502), got %A" other
        }

        testCaseAsync "a transport exception classifies as Network and is retried"
        <| async {
            let provider = MockProvider(MockStyle.Token, 10)
            provider.Add(rowsUpTo 1)
            provider.Script(fun () -> raise (IOException "connection reset"))
            let! outcome = PagedFetch.all options provider (endpointFor call) PageCursor.First
            Expect.isNone outcome.Failure "recovered after the network failure"
            Expect.hasLength provider.Requests 2 "retried once"
        }

        testCaseAsync "429 Retry-After within MaxBackoff is honoured and retried"
        <| async {
            let provider = MockProvider(MockStyle.Token, 10)
            provider.Add(rowsUpTo 1)
            provider.Script(ok429 (Some "0"))
            let! outcome = PagedFetch.all options provider (endpointFor call) PageCursor.First
            Expect.isNone outcome.Failure "recovered"
            Expect.hasLength provider.Requests 2 "retried once"
        }

        testCaseAsync "429 Retry-After beyond MaxBackoff ends the loop without waiting"
        <| async {
            let provider = MockProvider(MockStyle.Token, 10)
            provider.Script(ok429 (Some "120"))

            let policy = {
                fastRetry 5 with
                    MaxBackoff = TimeSpan.FromSeconds 30.0
            }

            let started = DateTime.UtcNow
            let! sent = HttpCall.send policy provider (HttpRequest.get "/items" [ "Authorization", "Bearer secret" ])

            match sent with
            | Error(TransportError.Exhausted(1, TransportError.Transient(429, _, Some wait))) ->
                Expect.equal wait (TimeSpan.FromSeconds 120.0) "the hint is carried"
            | other -> failtestf "expected Exhausted(1, 429 after 120 s), got %A" other

            Expect.isLessThan (DateTime.UtcNow - started) (TimeSpan.FromSeconds 10.0) "did not wait the hint out"
            Expect.hasLength provider.Requests 1 "no second attempt"
        }

        testCase "the neutral classifier and the AI classifier agree on every status (the rule exists once)"
        <| fun () ->
            for code in 400..599 do
                let neutral = TransportClassifier.classifyStatus code "b"
                let ai = ErrorClassifier.classifyStatus code "b"

                match neutral, ai with
                | TransportError.Transient(c, _, None), TransientServer(c', _) when c = code && c' = code -> ()
                | TransportError.Permanent(c, _), PermanentClient(c', _) when c = code && c' = code -> ()
                | _ -> failtestf "status %d: neutral %A vs AI %A" code neutral ai

        testCaseAsync "the AI RetryRunner keeps its Phase 251 semantics over the shared loop"
        <| async {
            let mutable attempts = 0

            let! result =
                RetryRunner.run (fastRetry 3) (fun () -> async {
                    attempts <- attempts + 1
                    let failed: Result<unit, AIProviderError> = Error(TransientServer(503, "x"))
                    return failed
                })

            match result with
            | Error(RetriesExhausted(3, TransientServer(503, "x"))) -> ()
            | other -> failtestf "expected RetriesExhausted(3, …), got %A" other

            Expect.equal attempts 3 "three attempts"

            let! single =
                RetryRunner.run (fastRetry 1) (fun () -> async {
                    let failed: Result<unit, AIProviderError> = Error(TransientNetwork "down")
                    return failed
                })

            Expect.equal single (Error(TransientNetwork "down")) "MaxAttempts = 1 surfaces the raw error"
        }

        testCase "Retry-After is read in delta-seconds form only"
        <| fun () ->
            Expect.equal
                (TransportClassifier.tryRetryAfter [ "retry-after", " 7 " ])
                (Some(TimeSpan.FromSeconds 7.0))
                "case-insensitive, trimmed"

            Expect.isNone
                (TransportClassifier.tryRetryAfter [ "Retry-After", "Wed, 21 Oct 2015 07:28:00 GMT" ])
                "the HTTP-date form reads as absent"
    ]

let private budgetTests =
    let window count longCount : RateLimitDescriptor = {
        Provider = "mockapi"
        ShortWindow = count, TimeSpan.FromHours 1.0
        LongWindow = Some(longCount, TimeSpan.FromHours 24.0)
        FairnessMode = PerScope
    }

    testList "OutboundRateBudget" [
        testCaseAsync "a burst past the long-window quota is refused locally as data"
        <| async {
            let provider = MockProvider(MockStyle.Token, 10)
            let descriptor = window 100 5
            let budget = OutboundRateBudget.ofWindow descriptor

            let transport =
                OutboundRateBudget.decorate (limiterOf [ descriptor ]) TimeProvider.System budget "team-a" None provider

            let! results =
                [
                    for _ in 1..8 ->
                        HttpCall.send
                            (fastRetry 1)
                            transport
                            (HttpRequest.get "/items" [ "Authorization", "Bearer secret" ])
                ]
                |> Async.Parallel

            let admitted = results |> Array.filter Result.isOk |> Array.length

            let refused =
                results
                |> Array.filter (function
                    | Error(TransportError.Refused _) -> true
                    | _ -> false)
                |> Array.length

            Expect.equal admitted 5 "exactly the quota is admitted"
            Expect.equal refused 3 "the rest are refused as TransportError.Refused"
            Expect.hasLength provider.Requests 5 "refused calls never reach the provider"
        }

        testCaseAsync "a refused call is not retried by the retry policy"
        <| async {
            let provider = MockProvider(MockStyle.Token, 10)
            let descriptor = window 100 1
            let budget = OutboundRateBudget.ofWindow descriptor

            let transport =
                OutboundRateBudget.decorate (limiterOf [ descriptor ]) TimeProvider.System budget "team-a" None provider

            let req = HttpRequest.get "/items" [ "Authorization", "Bearer secret" ]
            let! _ = HttpCall.send (fastRetry 5) transport req
            let! second = HttpCall.send (fastRetry 5) transport req

            match second with
            | Error(TransportError.Refused _) -> ()
            | other -> failtestf "expected Refused, got %A" other
        }

        testCaseAsync "the concurrency cap bounds in-flight requests under burst"
        <| async {
            let mutable inFlight = 0
            let mutable peak = 0
            let gate = obj ()

            let slow =
                { new IHttpTransport with
                    member _.Send _ = async {
                        lock gate (fun () ->
                            inFlight <- inFlight + 1
                            peak <- max peak inFlight)

                        do! Async.Sleep 30
                        lock gate (fun () -> inFlight <- inFlight - 1)

                        return {
                            StatusCode = 200
                            Headers = []
                            Body = ""
                        }
                    }
                }

            let descriptor = window 1000 1000

            let budget = {
                OutboundRateBudget.ofWindow descriptor with
                    MaxConcurrency = Some 2
            }

            let transport =
                OutboundRateBudget.decorate (limiterOf [ descriptor ]) TimeProvider.System budget "team-a" None slow

            let! _ = [ for _ in 1..10 -> transport.Send(HttpRequest.get "/x" []) ] |> Async.Parallel
            Expect.isLessThanOrEqual peak 2 "never more than two in flight"
            Expect.equal peak 2 "the cap is reached, not undershot"
        }

        testCaseAsync "a provider 429 Retry-After holds the next call through the budget"
        <| async {
            let provider = MockProvider(MockStyle.Token, 10)
            provider.Script(ok429 (Some "1"))
            let descriptor = window 1000 1000
            let budget = OutboundRateBudget.ofWindow descriptor

            let transport =
                OutboundRateBudget.decorate (limiterOf [ descriptor ]) TimeProvider.System budget "team-a" None provider

            let req = HttpRequest.get "/items" [ "Authorization", "Bearer secret" ]
            let! first = transport.Send req
            Expect.equal first.StatusCode 429 "the provider's 429 passes through"
            let started = DateTime.UtcNow
            let! second = transport.Send req
            Expect.equal second.StatusCode 200 "the next call is admitted after the pause"

            Expect.isGreaterThanOrEqual
                (DateTime.UtcNow - started)
                (TimeSpan.FromMilliseconds 800.0)
                "the next call waited out the provider's pause"
        }

        testCaseAsync "a provider pause longer than the budget waits refuses calls instead of blocking"
        <| async {
            let provider = MockProvider(MockStyle.Token, 10)
            provider.Script(ok429 (Some "3600"))
            let descriptor = window 1000 1000
            let budget = OutboundRateBudget.ofWindow descriptor

            let transport =
                OutboundRateBudget.decorate (limiterOf [ descriptor ]) TimeProvider.System budget "team-a" None provider

            let req = HttpRequest.get "/items" [ "Authorization", "Bearer secret" ]
            let! _ = transport.Send req
            let! second = HttpCall.send (fastRetry 3) transport req

            match second with
            | Error(TransportError.Refused reason) -> Expect.stringContains reason "mockapi" "names the provider"
            | other -> failtestf "expected Refused, got %A" other

            Expect.hasLength provider.Requests 1 "the refused call never reached the provider"
        }

        testCase "validate rejects a budget no transport can honour"
        <| fun () ->
            let budget = OutboundRateBudget.ofWindow (window 10 10)
            Expect.isOk (OutboundRateBudget.validate budget) "the default is valid"
            Expect.isError (OutboundRateBudget.validate { budget with MaxConcurrency = Some 0 }) "zero slots"

            Expect.isError
                (OutboundRateBudget.validate {
                    budget with
                        Window = { budget.Window with Provider = " " }
                })
                "a blank provider"
    ]

let private cursorTests =
    testList "SyncCursor + IncrementalSync" [
        testCaseAsync "a cursor round-trips through the entity store by value"
        <| async {
            let store = openCursorStore (tempDir ())

            let cursor = {
                SyncCursor.initial "mockapi" "src-1" "items" with
                    Resume = Some(PageCursor.Token "42")
                    Watermark = Some(mark 9)
                    PendingWatermark = Some(mark 11)
            }

            let! saved = SyncCursor.save store "team-a" (EntityPrincipal.ofPrincipal "t") cursor
            let saved = Expect.wantOk saved "saved"
            Expect.equal saved.Version 1 "first write is version 1"
            let! loaded = SyncCursor.load store "team-a" "mockapi" "src-1" "items"
            Expect.equal (Expect.wantOk loaded "loaded") saved "the loaded value equals the saved one"
            let! other = SyncCursor.load store "team-b" "mockapi" "src-1" "items"

            Expect.equal
                (Expect.wantOk other "other scope")
                (SyncCursor.initial "mockapi" "src-1" "items")
                "scope-isolated"
        }

        testCaseAsync "a stale write is a Conflict, not a silent overwrite"
        <| async {
            let store = openCursorStore (tempDir ())
            let actor = EntityPrincipal.ofPrincipal "t"
            let fresh = SyncCursor.initial "mockapi" "src-1" "items"
            let! first = SyncCursor.save store "team-a" actor fresh
            Expect.isOk first "first writer wins"
            let! second = SyncCursor.save store "team-a" actor fresh

            match second with
            | Error(SyncCursorError.Conflict(0, 1)) -> ()
            | other -> failtestf "expected Conflict(0, 1), got %A" other
        }

        testCase "entity ids are escaped and injective"
        <| fun () ->
            let a = SyncCursor.entityId "a.b" "c" "d"
            let b = SyncCursor.entityId "a" "b.c" "d"
            Expect.notEqual a b "a separator in a part cannot forge another key"

            Expect.isTrue
                (a
                 |> Seq.forall (fun ch -> Char.IsAsciiLetterOrDigit ch || ch = '_' || ch = '-' || ch = '.'))
                "only index-safe characters"

        testCaseAsync "an incremental sync resumes after a restart with no duplicate ingestion"
        <| async {
            let directory = tempDir ()
            let provider = MockProvider(MockStyle.Token, 2)
            provider.Add(rowsUpTo 5)
            let sink = ResizeArray<MockRow>()

            // Process 1: one page, then the process "dies".
            let! first =
                IncrementalSync.run (depsFor provider (openCursorStore directory) sink (Some 1)) "team-a" request

            let first = Expect.wantOk first "first run"
            Expect.isFalse first.Completed "the pass is unfinished"
            Expect.equal first.Cursor.Resume (Some(PageCursor.Token "2")) "the resume point is persisted"

            // Process 2: a fresh store handle over the same directory, fresh deps.
            let! second = IncrementalSync.run (depsFor provider (openCursorStore directory) sink None) "team-a" request
            let second = Expect.wantOk second "second run"
            Expect.isTrue second.Completed "the pass completes"
            Expect.equal (sink |> Seq.map _.Id |> List.ofSeq) [ 1..5 ] "every row exactly once, in order"
            Expect.equal second.Cursor.Watermark (Some(mark 5)) "the watermark is promoted on completion"
            Expect.isNone second.Cursor.Resume "the resume point clears between passes"

            // New data arrives; the next pass reads only what changed.
            provider.Add [ { Id = 6; UpdatedAt = mark 6 }; { Id = 7; UpdatedAt = mark 7 } ]
            let! third = IncrementalSync.run (depsFor provider (openCursorStore directory) sink None) "team-a" request
            let third = Expect.wantOk third "third run"
            Expect.equal third.Rows 2 "only the new rows"
            Expect.equal (sink |> Seq.map _.Id |> List.ofSeq) [ 1..7 ] "still no duplicates"
            Expect.equal third.Cursor.CompletedPasses 2 "two completed passes"
        }

        testCaseAsync "a sink failure stops the pass and leaves the page to be offered again"
        <| async {
            let directory = tempDir ()
            let provider = MockProvider(MockStyle.Offset, 2)
            provider.Add(rowsUpTo 4)
            let sink = ResizeArray<MockRow>()
            let mutable failNext = false

            let failing = {
                depsFor provider (openCursorStore directory) sink None with
                    Ingest =
                        fun _ rows -> async {
                            if failNext then
                                return Error "sink unavailable"
                            else
                                sink.AddRange rows
                                failNext <- true
                                return Ok()
                        }
            }

            let! first = IncrementalSync.run failing "team-a" request
            let first = Expect.wantOk first "the run itself reports"

            match first.Failure with
            | Some {
                       Failure = PageFailure.Consume "sink unavailable"
                   } -> ()
            | other -> failtestf "expected a Consume failure, got %A" other

            Expect.equal
                (IncrementalSync.toJobResult (Ok first))
                (TransientFailure "page 1: The page was not consumed: sink unavailable")
                "transient for the scheduler"

            let! second = IncrementalSync.run (depsFor provider (openCursorStore directory) sink None) "team-a" request
            Expect.isTrue (Expect.wantOk second "second").Completed "the retry completes"
            Expect.equal (sink |> Seq.map _.Id |> List.ofSeq) [ 1..4 ] "the refused page was ingested exactly once"
        }

        testCaseAsync "a full-refresh endpoint ignores the watermark"
        <| async {
            let directory = tempDir ()
            let provider = MockProvider(MockStyle.Token, 10)
            provider.Add(rowsUpTo 3)
            let sink = ResizeArray<MockRow>()
            let snapshot = { request with Endpoint = "snapshot" }
            let! _ = IncrementalSync.run (depsFor provider (openCursorStore directory) sink None) "team-a" snapshot
            let! _ = IncrementalSync.run (depsFor provider (openCursorStore directory) sink None) "team-a" snapshot
            Expect.equal sink.Count 6 "each pass re-reads the whole resource"
        }

        testCaseAsync "an unknown endpoint or a missing credential fails before any call"
        <| async {
            let provider = MockProvider(MockStyle.Token, 10)
            let deps = depsFor provider (openCursorStore (tempDir ())) (ResizeArray()) None
            let! unknown = IncrementalSync.run deps "team-a" { request with Endpoint = "nope" }

            match unknown with
            | Error(IncrementalSyncError.Prepare(SchemaMismatch _)) -> ()
            | other -> failtestf "expected SchemaMismatch, got %A" other

            let! noCredential = IncrementalSync.run deps "team-b" request

            match noCredential with
            | Error(IncrementalSyncError.Prepare(CredentialMissing "mockapi-token")) -> ()
            | other -> failtestf "expected CredentialMissing, got %A" other

            Expect.equal
                (IncrementalSync.toJobResult noCredential)
                (PermanentFailure "credential 'mockapi-token' is missing")
                "permanent for the scheduler"

            Expect.isEmpty provider.Requests "nothing was sent"
        }

        testCaseAsync "the job handler is stateless: payload in, cursor re-read, verdict out"
        <| async {
            let directory = tempDir ()
            let provider = MockProvider(MockStyle.NextLink, 2)
            provider.Add(rowsUpTo 3)
            let sink = ResizeArray<MockRow>()
            let payload = IncrementalSync.payload request
            Expect.equal (IncrementalSync.tryParsePayload payload) (Ok request) "the payload round-trips"
            Expect.isError (IncrementalSync.tryParsePayload "{") "a malformed payload is an Error"

            let execute maxPages = async {
                let handler =
                    IncrementalSync.handler (depsFor provider (openCursorStore directory) sink maxPages)

                let ctx: JobContext = {
                    JobId = Guid.NewGuid()
                    ScopeId = "team-a"
                    Scope = ResolvedScope.anonymous
                    AccessContext = AccessContext.unrestricted (AuthenticatedUser "scheduler-test")
                    Attempt = 1
                    Trigger = Trigger.Manual
                    TriggerSource = ScheduledManually "scheduler-test"
                    ScheduledAt = DateTime.UtcNow
                    RunningAt = DateTime.UtcNow
                    Payload = payload
                    DeadLetterDestination = None
                }

                return! handler.Execute ctx
            }

            let! first = execute (Some 1)
            Expect.equal first Success "a capped pass is a Success; the next run continues"
            let! second = execute None
            Expect.equal second Success "the pass completes"
            Expect.equal (sink |> Seq.map _.Id |> List.ofSeq) [ 1..3 ] "each row once across two invocations"
        }
    ]

/// A stub handler recording what the BCL transport put on the wire.
type private RecordingHandler(respond: Net.Http.HttpRequestMessage -> Net.Http.HttpResponseMessage) =
    inherit Net.Http.HttpMessageHandler()
    member val Seen = ResizeArray<Net.Http.HttpRequestMessage>()

    override this.SendAsync(request, _) =
        this.Seen.Add request
        Threading.Tasks.Task.FromResult(respond request)

let private connectorTransportTests =
    testList "ConnectorTransport.ofHttpClient" [
        testCaseAsync "maps the portable records onto HttpClient and returns non-2xx as data"
        <| async {
            let handler =
                new RecordingHandler(fun request ->
                    let response =
                        new Net.Http.HttpResponseMessage(
                            if request.RequestUri.AbsolutePath = "/v1/missing" then
                                Net.HttpStatusCode.NotFound
                            else
                                Net.HttpStatusCode.OK
                        )

                    response.Headers.Add("x-next-token", "abc")
                    response.Content <- new Net.Http.StringContent("1|t0001")
                    response)

            use client =
                new Net.Http.HttpClient(handler, BaseAddress = Uri "https://api.example.test/")

            let transport =
                ConnectorTransport.ofHttpClient client (Some(TimeSpan.FromSeconds 5.0))

            let! ok = transport.Send(HttpRequest.get "/v1/items?after=2" [ "Authorization", "Bearer k" ])
            Expect.equal ok.StatusCode 200 "status mapped"
            Expect.equal ok.Body "1|t0001" "body read"
            Expect.contains ok.Headers ("x-next-token", "abc") "response headers mapped"
            let seen = handler.Seen[0]
            Expect.equal (string seen.RequestUri) "https://api.example.test/v1/items?after=2" "relative URL resolved"
            Expect.equal (seen.Headers.Authorization.ToString()) "Bearer k" "request headers carried"

            let! absolute = transport.Send(HttpRequest.get "https://other.example.test/v1/items?offset=4" [])
            Expect.equal absolute.StatusCode 200 "absolute next-page URL"

            Expect.equal
                (string handler.Seen[1].RequestUri)
                "https://other.example.test/v1/items?offset=4"
                "used as given"

            let! missing = transport.Send(HttpRequest.get "/v1/missing" [])
            Expect.equal missing.StatusCode 404 "a 404 is data, not an exception"
        }
    ]

let private stripImportsTests =
    testList "strip-imports (GP 1 / GP 13)" [
        testCase "the kit carries no vendor SDK — BCL, FSharp.Core and ToolUp.* only"
        <| fun () ->
            let references =
                typeof<PageCursor>.Assembly.GetReferencedAssemblies() |> Array.map _.Name

            let foreign =
                references
                |> Array.filter (fun name ->
                    not (
                        name.StartsWith "System"
                        || name.StartsWith "Microsoft.Extensions"
                        || name.StartsWith "ToolUp."
                        || name = "FSharp.Core"
                        || name = "netstandard"
                        || name = "mscorlib"
                    ))

            Expect.isEmpty foreign $"no vendor reference, got %A{foreign}"

        testCase "nothing in ToolUp.Platform.* or ToolUp.AI.Wire references the kit"
        <| fun () ->
            for platformType in [ typeof<RetryPolicy>; typeof<IRateLimiter>; typeof<IJobHandler> ] do
                let references = platformType.Assembly.GetReferencedAssemblies() |> Array.map _.Name

                Expect.isFalse
                    (references |> Array.contains "ToolUp.DataSources.Common")
                    $"{platformType.Assembly.GetName().Name} must not reference the kit"

        testCase "the neutral transport layer adds no System.Net.Http to the Fable-safe tier"
        <| fun () ->
            let references =
                typeof<TransportError>.Assembly.GetReferencedAssemblies() |> Array.map _.Name

            Expect.isFalse (references |> Array.contains "System.Net.Http") "ToolUp.AI.Wire stays HTTP-client-free"
    ]

/// The kit-level pack, plus the parametrised connector half bound to the
/// mock connector in all three continuation styles.
let tests =
    testList "Phase 128 — outbound API connector kit" [
        pagedFetchTests
        retryTests
        budgetTests
        cursorTests
        connectorTransportTests
        stripImportsTests
        for style in [ MockStyle.Token; MockStyle.Offset; MockStyle.NextLink ] do
            connectorTests
                $"MockConnector ({style})"
                (fun () ->
                    let provider = MockProvider(style, 2)
                    provider.Add(rowsUpTo 5)
                    connector, provider :> IHttpTransport, call)
                5
    ]