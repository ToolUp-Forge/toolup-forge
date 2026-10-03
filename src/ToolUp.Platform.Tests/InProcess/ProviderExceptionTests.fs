// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ProviderExceptionTests

open System
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Reflection
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ProviderExceptions
open ToolUp.Platform.Secrets
open ToolUp.Platform.IEmbeddingProvider

// ─── Phase 972 — provider exception matches see through Async.AwaitTask ───
//
// `Async.AwaitTask` raises a faulted task's `AggregateException`, so a
// `with` branch that type-tests the provider's exception never fires on that
// path. Each pin below drives a REAL code path — a companion's own
// `HttpClient` call over a handler whose task faults with the provider's
// exception, or a real SDK client over a local HTTP endpoint — and asserts
// the outcome the code was written to produce. Before Phase 972 every pin
// here was red: the failure either fell to a generic branch (the wrong
// outcome) or escaped the function as an `AggregateException`.

/// A transport whose every send faults its task with `fault ()` — what a
/// refused connection, a DNS failure or a reset socket does to `HttpClient`.
type private FaultingHandler(fault: unit -> exn) =
    inherit HttpMessageHandler()
    let mutable sends = 0
    member _.Sends = sends

    override _.SendAsync(_request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
        Interlocked.Increment &sends |> ignore
        Task.FromException<HttpResponseMessage>(fault ())

/// Faults the first `failures` sends, then answers `ok ()`.
type private FlakyHandler(failures: int, ok: unit -> HttpResponseMessage) =
    inherit HttpMessageHandler()
    let mutable sends = 0
    member _.Sends = sends

    override _.SendAsync(_request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
        let n = Interlocked.Increment &sends

        if n <= failures then
            Task.FromException<HttpResponseMessage>(HttpRequestException("connection refused"))
        else
            Task.FromResult(ok ())

/// Every key holds a value, so a flow gets past its credential read and
/// reaches the network.
type private AnySecretStore() =
    interface ISecretStore with
        member _.GetSecret(_scope, key) = async { return Some("value-of-" + key) }
        member _.SetSecret(_scope, _key, _value) = async { return Ok() }
        member _.DeleteSecret(_scope, _key) = async { return Ok() }
        member _.ListKeys(_scope) = async { return [] }

let private refused () : exn =
    HttpRequestException("connection refused")

let private clientOver (handler: HttpMessageHandler) = new HttpClient(handler)

/// Run `work`; an exception that escapes it is the defect this phase
/// fixes, reported as such rather than as an unexplained test error.
let private orEscaped (work: Async<'T>) : Async<Result<'T, string>> = async {
    match! Async.Catch work with
    | Choice1Of2 value -> return Ok value
    | Choice2Of2 ex -> return Error(sprintf "the failure ESCAPED as %s: %s" (ex.GetType().Name) ex.Message)
}

// ─── The shared pattern ────────────────────────────────────────────────

let private patternTests =
    testList "ProviderException — the shared unwrap" [

        test "a bare provider exception matches exactly as `:?` would" {
            match HttpRequestException "down" :> exn with
            | ProviderException(h: HttpRequestException) -> Expect.equal h.Message "down" "the same exception"
            | _ -> failtest "an unwrapped exception must match"
        }

        test "it sees through AggregateException (what Async.AwaitTask raises)" {
            let wrapped =
                async {
                    try
                        let! _ = Task.FromException<int>(HttpRequestException "down") |> Async.AwaitTask
                        return None
                    with ex ->
                        return Some ex
                }
                |> Async.RunSynchronously
                |> Option.get

            Expect.isTrue (wrapped :? AggregateException) "premise: AwaitTask raises the AggregateException"

            match wrapped with
            | ProviderException(h: HttpRequestException) -> Expect.equal h.Message "down" "the inner exception"
            | _ -> failtest "the wrapped provider exception must match"
        }

        test "it sees through nested aggregates and TargetInvocationException" {
            let inner = SocketException(10061) :> exn

            let wrapped =
                TargetInvocationException(AggregateException(AggregateException(inner))) :> exn

            match wrapped with
            | ProviderException(s: SocketException) -> Expect.isTrue (obj.ReferenceEquals(s, inner)) "the same object"
            | _ -> failtest "the doubly wrapped exception must match"

            Expect.isTrue (obj.ReferenceEquals(unwrap wrapped, inner)) "unwrap answers the provider's exception"
        }

        test "it answers the first cause of the annotated type, and only that type" {
            let wrapped =
                AggregateException(TimeoutException "first", HttpRequestException "second") :> exn

            match wrapped with
            | ProviderException(h: HttpRequestException) -> Expect.equal h.Message "second" "found past a sibling"
            | _ -> failtest "a later cause of the type must match"

            match wrapped with
            | ProviderException(_: SocketException) -> failtest "no cause is a SocketException"
            | _ -> ()

            Expect.equal
                (causes wrapped |> List.map (fun e -> e.GetType().Name))
                [ "TimeoutException"; "HttpRequestException" ]
                "causes lists both, in order"
        }

        test "an empty aggregate is its own cause" {
            let empty = AggregateException() :> exn
            Expect.equal (causes empty) [ empty ] "never empty"
        }
    ]

// ─── Red pins: transport failures that ESCAPED instead of classifying ───

let private githubTests =
    testList "GitHub (auth + data source)" [

        testCaseAsync "GitHubApi.getUser: a refused connection is NetworkError"
        <| async {
            use client = clientOver (new FaultingHandler(refused))

            match!
                orEscaped (ToolUp.AuthProviders.GitHubApi.getUser client "ua" "https://api.github.invalid" "tok")
            with
            | Ok(Error(ToolUp.AuthProviders.GitHubApi.NetworkError _)) -> ()
            | Ok other -> failtestf "expected NetworkError, got %A" other
            | Error escaped -> failtest escaped
        }

        testCaseAsync "GitHubApi.getPrimaryEmail: a refused connection is NetworkError"
        <| async {
            use client = clientOver (new FaultingHandler(refused))

            match!
                orEscaped (
                    ToolUp.AuthProviders.GitHubApi.getPrimaryEmail client "ua" "https://api.github.invalid" "tok"
                )
            with
            | Ok(Error(ToolUp.AuthProviders.GitHubApi.NetworkError _)) -> ()
            | Ok other -> failtestf "expected NetworkError, got %A" other
            | Error escaped -> failtest escaped
        }

        testCaseAsync "GitHubApi.checkOrgMembership: a refused connection is NetworkError"
        <| async {
            use client = clientOver (new FaultingHandler(refused))

            match!
                orEscaped (
                    ToolUp.AuthProviders.GitHubApi.checkOrgMembership
                        client
                        "ua"
                        "https://api.github.invalid"
                        "tok"
                        "acme"
                )
            with
            | Ok(Error(ToolUp.AuthProviders.GitHubApi.NetworkError _)) -> ()
            | Ok other -> failtestf "expected NetworkError, got %A" other
            | Error escaped -> failtest escaped
        }

        testCaseAsync "GitHubOAuth.exchangeCode: a refused connection is the 'could not reach' error"
        <| async {
            use client = clientOver (new FaultingHandler(refused))

            let config =
                ToolUp.AuthProviders.GitHubOAuth.GitHubOAuthAppConfig.create "client-id" [ "read:user" ]

            match!
                orEscaped (
                    ToolUp.AuthProviders.GitHubOAuth.exchangeCode
                        client
                        (AnySecretStore())
                        config
                        "code"
                        "https://app/cb"
                )
            with
            | Ok(Error message) ->
                Expect.stringContains message "could not reach the GitHub token endpoint" "the transport branch"
                Expect.stringContains message "connection refused" "the provider's own message, not the wrapper's"
            | Ok(Ok _) -> failtest "a refused connection reported success"
            | Error escaped -> failtest escaped
        }

        testCaseAsync "GitHubAppFlow: ExchangeCode and Revoke report NetworkError on a refused connection"
        <| async {
            use client = clientOver (new FaultingHandler(refused))

            let flow =
                ToolUp.DataSources.GitHubAppFlow.create
                    client
                    (AnySecretStore())
                    (ToolUp.DataSources.GitHubAppFlow.GitHubAppFlowConfig.create [ "repo" ])

            let ctx = OAuthFlowContext.forDataSource "team-scope-1" "ds-1" None

            match! orEscaped (flow.ExchangeCode(ctx, "code", "https://app/cb", None)) with
            | Ok(Error(NetworkError _)) -> ()
            | Ok other -> failtestf "ExchangeCode: expected NetworkError, got %A" other
            | Error escaped -> failtest ("ExchangeCode: " + escaped)

            match! orEscaped (flow.Revoke(ctx, "refresh")) with
            | Ok(Error(NetworkError _)) -> ()
            | Ok other -> failtestf "Revoke: expected NetworkError, got %A" other
            | Error escaped -> failtest ("Revoke: " + escaped)
        }
    ]

let private oauthTests =
    testList "OAuth token endpoints" [

        testCaseAsync "GoogleOAuthFlow: ExchangeCode and Revoke report NetworkError on a refused connection"
        <| async {
            use client = clientOver (new FaultingHandler(refused))

            let flow =
                ToolUp.DataSources.GoogleOAuthFlow.create
                    client
                    (AnySecretStore())
                    None
                    ToolUp.DataSources.GoogleOAuthFlow.GoogleOAuthFlowConfig.analyticsReadonly

            let ctx = OAuthFlowContext.forDataSource "team-scope-1" "ds-1" None

            match! orEscaped (flow.ExchangeCode(ctx, "code", "https://app/cb", None)) with
            | Ok(Error(NetworkError _)) -> ()
            | Ok other -> failtestf "ExchangeCode: expected NetworkError, got %A" other
            | Error escaped -> failtest ("ExchangeCode: " + escaped)

            match! orEscaped (flow.Revoke(ctx, "refresh")) with
            | Ok(Error(NetworkError _)) -> ()
            | Ok other -> failtestf "Revoke: expected NetworkError, got %A" other
            | Error escaped -> failtest ("Revoke: " + escaped)
        }

        testCaseAsync "ProviderOAuthFlow.httpPost (every provider's token POST) reports NetworkError"
        <| async {
            use client = clientOver (new FaultingHandler(refused))

            match!
                orEscaped (
                    ProviderOAuthFlow.httpPost client "https://oauth.provider.invalid/token" [ "grant_type", "x" ]
                )
            with
            | Ok(Error(NetworkError message)) ->
                Expect.stringContains message "connection refused" "the provider's own message"
            | Ok other -> failtestf "expected NetworkError, got %A" other
            | Error escaped -> failtest escaped
        }
    ]

let private embeddingTests =
    testList "OpenAI embedding provider" [

        testCaseAsync "a refused connection is a TRANSIENT attempt — retried, not thrown on the first try"
        <| async {
            let ok () =
                let vector = String.Join(",", Array.create 1536 "0.5")

                new HttpResponseMessage(
                    HttpStatusCode.OK,
                    Content =
                        new StringContent(
                            sprintf "{\"data\":[{\"index\":0,\"embedding\":[%s]}]}" vector,
                            Encoding.UTF8,
                            "application/json"
                        )
                )

            let handler = new FlakyHandler(1, ok)
            use client = new HttpClient(handler, BaseAddress = Uri "https://api.openai.invalid")

            let options =
                OpenAIEmbeddingProvider.OpenAIEmbeddingOptions.defaults
                |> OpenAIEmbeddingProvider.withEmbedderRetryPolicy {
                    EmbedderRetryPolicy.noRetry with
                        MaxAttempts = 2
                        InitialBackoff = TimeSpan.FromMilliseconds 1.0
                        MaxBackoff = TimeSpan.FromMilliseconds 1.0
                }

            let provider =
                OpenAIEmbeddingProvider.createWithClient client (AnySecretStore()) options

            match! orEscaped (provider.GenerateEmbedding "text") with
            | Ok vector -> Expect.equal vector.Length 1536 "the retry succeeded"
            | Error escaped -> failtest escaped

            Expect.equal handler.Sends 2 "the transport failure was retried under the policy"
        }
    ]

let private healthTests =
    testList "Meta WhatsApp readiness probe" [

        testCaseAsync "an unreachable Graph API is Degraded (retryable), not Unhealthy"
        <| async {
            let settings = {
                ToolUp.Platform.NotificationChannels.WhatsApp.MetaCloud.MetaWhatsAppSettings.create "106540352242922" with
                    EndpointOverride = Some "https://graph.test.invalid"
            }

            let probe =
                ToolUp.Platform.NotificationChannels.WhatsApp.MetaCloudHealth.createWith
                    (AnySecretStore())
                    settings
                    (new FaultingHandler(refused))

            match! probe.Check() with
            | HealthChecks.Degraded message -> Expect.stringContains message "unreachable" "the network branch"
            | other -> failtestf "expected Degraded, got %A" other
        }
    ]

// ─── Azure Table Storage rate-limit store, over a local Table endpoint ───

/// A minimal Azure Table endpoint on loopback: the table already exists,
/// no counter entity exists yet (the first request of every window), and
/// an insert answers `insertStatus`. The real SDK client talks to it, so
/// the `RequestFailedException` the store classifies is the SDK's own,
/// raised inside the SDK's task.
type private LocalTableEndpoint(insertStatus: int) =
    let port =
        let probe = new TcpListener(IPAddress.Loopback, 0)
        probe.Start()
        let p = (probe.LocalEndpoint :?> IPEndPoint).Port
        probe.Stop()
        p

    let listener = new HttpListener()
    do listener.Prefixes.Add(sprintf "http://localhost:%d/" port)
    do listener.Start()
    let mutable inserts = 0

    let answer (ctx: HttpListenerContext) (status: int) (errorCode: string option) =
        ctx.Response.StatusCode <- status

        match errorCode with
        | Some code ->
            ctx.Response.Headers.Add("x-ms-error-code", code)
            ctx.Response.ContentType <- "application/json;odata=minimalmetadata"

            let body =
                Encoding.UTF8.GetBytes(
                    sprintf """{"odata.error":{"code":"%s","message":{"lang":"en-US","value":"%s"}}}""" code code
                )

            ctx.Response.OutputStream.Write(body, 0, body.Length)
        | None -> ctx.Response.Headers.Add("ETag", "W/\"datetime'2026-10-03T00%3A00%3A00Z'\"")

        ctx.Response.Close()

    let serve = async {
        while listener.IsListening do
            let! ctx = listener.GetContextAsync() |> Async.AwaitTask
            let path = ctx.Request.Url.AbsolutePath

            match ctx.Request.HttpMethod with
            | "POST" when path.EndsWith "/Tables" -> answer ctx 409 (Some "TableAlreadyExists")
            | "GET" -> answer ctx 404 (Some "ResourceNotFound")
            | "POST" ->
                Interlocked.Increment &inserts |> ignore

                if insertStatus = 409 then
                    answer ctx 409 (Some "EntityAlreadyExists")
                else
                    answer ctx insertStatus None
            | _ -> answer ctx 400 (Some "Unexpected")
    }

    do
        Async.Start(
            async {
                try
                    do! serve
                with _ ->
                    ()
            }
        )

    member _.ConnectionString =
        sprintf
            "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://localhost:%d/devstoreaccount1;"
            port

    member _.Inserts = inserts

    interface IDisposable with
        member _.Dispose() = listener.Close()

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private azureTableTests =
    let storeOver (endpoint: LocalTableEndpoint) =
        ToolUp.RateLimit.AzureTableStorage.AzureTableRateLimitStore.create
            {
                ToolUp.RateLimit.AzureTableStorage.Options.defaults with
                    ConnectionString = endpoint.ConnectionString
                    MaxRetries = 2
            }
            silentLogger

    testList "Azure Table rate-limit store" [

        testCaseAsync "the first request of a window (no counter entity: a 404) is admitted"
        <| async {
            use endpoint = new LocalTableEndpoint(204)
            let store = storeOver endpoint

            match! orEscaped (store.IncrementAndCheck(IpAddressKey "203.0.113.7", PerMinute, 10)) with
            | Ok(Ok(AllowWithRemaining remaining)) -> Expect.equal remaining 9 "one of ten used"
            | Ok other -> failtestf "expected AllowWithRemaining 9, got %A" other
            | Error escaped -> failtest escaped
        }

        testCaseAsync "a lost insert race (409) is retried, then reported as StoreUnavailable — never thrown"
        <| async {
            use endpoint = new LocalTableEndpoint(409)
            let store = storeOver endpoint

            match! orEscaped (store.IncrementAndCheck(IpAddressKey "203.0.113.7", PerMinute, 10)) with
            | Ok(Error(StoreUnavailable _)) -> ()
            | Ok other -> failtestf "expected StoreUnavailable, got %A" other
            | Error escaped -> failtest escaped

            Expect.isGreaterThan endpoint.Inserts 1 "the ETag-mismatch arm drove the retry loop"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 972 — provider exceptions seen through Async.AwaitTask" [
        patternTests
        githubTests
        oauthTests
        embeddingTests
        healthTests
        azureTableTests
    ]