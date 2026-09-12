module ToolUp.Platform.Tests.InProcess.EmbeddingProviderKeyRotationTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Reflection
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform.Secrets
open ToolUp.Platform.IEmbeddingProvider

// ─── Phase 459 — the three properties C/D pin, tested on the wire ────
//
// Phase 459's A and B were found already true by the 2026-09-05 Refine:
// `OpenAIEmbeddingProvider` reads `openai-api-key` from the injected
// `ISecretStore` inside `postEmbedding`, i.e. on EVERY call. This file
// exists so that stays true — a future refactor that hoists the read to
// construction time (the shape the phase was originally filed against)
// would leave every deployment serving a revoked key until restart, and
// nothing else in the suite would notice.
//
// The three properties, one test each:
//
//  1. Rotation with NO reconstruction. One provider instance, two calls,
//     a different secret between them — the Authorization header on the
//     second request must carry the new key.
//  2. Breaker scope is PER INSTANCE. Two providers in one process trip
//     independently; an OPEN breaker on one does not fast-fail the other.
//     That is the honest scope task C documents, and asserting it is what
//     stops the documentation and the behaviour drifting apart.
//  3. One HttpClient. The provider constructs no transport of its own,
//     and the `create*` factories hand every instance the same shared
//     per-process client (task D — a client per instance is the classic
//     socket-exhaustion antipattern, and the provider is deliberately
//     built per batch).
//
// Everything runs over a stub `HttpMessageHandler` — the shape
// `AuthProviderTests` / `GitHubAuthProviderTests` already use — through
// the `createWithClient` seam. No test in this file reaches the network.

/// Records every request's `Authorization` header and answers with a
/// caller-supplied response. Requests are recorded BEFORE the response is
/// produced, so a test can assert what reached the wire even when the
/// call then fails.
type private RecordingHandler(respond: unit -> HttpResponseMessage) =
    inherit HttpMessageHandler()

    let authorizations = ConcurrentQueue<string>()

    /// The `Authorization` header of each request, in order. `""` for a
    /// request that carried none.
    member _.Authorizations = authorizations |> Seq.toList

    member _.RequestCount = authorizations.Count

    override _.SendAsync(request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
        let auth =
            match request.Headers.Authorization with
            | null -> ""
            | header -> sprintf "%s %s" header.Scheme header.Parameter

        authorizations.Enqueue auth
        Task.FromResult(respond ())

/// An `ISecretStore` whose one secret can be rotated between calls, the
/// deployment act this phase is about. Counts reads so a test can tell
/// "read once at construction" from "read per call".
type private RotatingSecretStore(initial: string) =
    let mutable current = initial
    let mutable reads = 0

    member _.Rotate(value: string) = current <- value
    member _.Reads = reads

    interface ISecretStore with
        member _.GetSecret(_scopeId, _key) = async {
            reads <- reads + 1
            return Some current
        }

        member _.SetSecret(_scopeId, _key, _value) = async { return Error "read-only test store" }
        member _.DeleteSecret(_scopeId, _key) = async { return Ok() }
        member _.ListKeys _scopeId = async { return [] }

/// A model this build does not know the native dimension of, so the
/// provider's `validateDimensions` accepts the tiny vector these tests
/// hand back instead of demanding 1536 floats per response.
let private testModel = "test-embedding-model"

let private testDimensions = 3

let private okResponse () =
    let body =
        sprintf "{\"data\":[{\"index\":0,\"embedding\":[%s]}]}" (String.Join(",", Array.create testDimensions "0.5"))

    new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent(body, Encoding.UTF8, "application/json"))

let private serverErrorResponse () =
    new HttpResponseMessage(
        HttpStatusCode.InternalServerError,
        Content = new StringContent("upstream is down", Encoding.UTF8, "text/plain")
    )

let private clientOver (handler: HttpMessageHandler) =
    new HttpClient(handler, BaseAddress = Uri("https://api.openai.invalid"))

let private baseOptions =
    OpenAIEmbeddingProvider.OpenAIEmbeddingOptions.defaults
    |> OpenAIEmbeddingProvider.withEmbedderModel testModel testDimensions

/// Every `HttpClient` an instance holds in a field of its own. Found by
/// FIELD TYPE rather than by name so the assertion survives a rename of
/// the constructor parameter — a test that goes quiet when the code is
/// tidied is worse than no test.
let private httpClientsHeldBy (provider: obj) : HttpClient list =
    provider.GetType().GetFields(BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public)
    |> Array.filter (fun f -> typeof<HttpClient>.IsAssignableFrom f.FieldType)
    |> Array.map (fun f -> f.GetValue provider :?> HttpClient)
    |> Array.toList

[<Tests>]
let tests =
    testList "Phase 459 — embedding provider key rotation, breaker scope, client reuse" [

        testCase "a secret rotated between two calls reaches the wire without reconstructing the provider"
        <| fun () ->
            let handler = new RecordingHandler(okResponse)
            use client = clientOver handler
            let store = RotatingSecretStore "key-before-rotation"

            let provider = OpenAIEmbeddingProvider.createWithClient client store baseOptions

            provider.GenerateEmbedding "first" |> Async.RunSynchronously |> ignore
            store.Rotate "key-after-rotation"
            // The SAME instance — no reconstruction, no restart.
            provider.GenerateEmbedding "second" |> Async.RunSynchronously |> ignore

            Expect.equal
                handler.Authorizations
                [ "Bearer key-before-rotation"; "Bearer key-after-rotation" ]
                "the second call must carry the rotated key — the provider reads the secret store per call, so a revoked key stops working on the next call rather than at the next restart"

            Expect.equal store.Reads 2 "one secret read per embed call, not one per provider"

        testCase "the circuit breaker is per instance — an OPEN breaker on one provider does not fast-fail another"
        <| fun () ->
            let handler = new RecordingHandler(serverErrorResponse)
            use client = clientOver handler
            let store = RotatingSecretStore "key"

            // Threshold 1 + no retry: one failed call trips the breaker,
            // and nothing sleeps.
            let options =
                baseOptions
                |> OpenAIEmbeddingProvider.withEmbedderRetryPolicy EmbedderRetryPolicy.noRetry
                |> OpenAIEmbeddingProvider.withEmbedderCircuitBreaker {
                    FailureThreshold = 1
                    Cooldown = TimeSpan.FromMinutes 5.0
                }

            let providerA = OpenAIEmbeddingProvider.createWithClient client store options
            let providerB = OpenAIEmbeddingProvider.createWithClient client store options

            let embed (p: IEmbeddingProvider) =
                try
                    p.GenerateEmbedding "text" |> Async.RunSynchronously |> ignore
                    "no exception"
                with
                | EmbeddingProviderCircuitOpenException _ -> "circuit-open"
                | EmbeddingProviderRetriesExhaustedException _ -> "retries-exhausted"
                | ex -> ex.GetType().Name

            Expect.equal (embed providerA) "retries-exhausted" "A's first call reaches the wire and fails"
            Expect.equal handler.RequestCount 1 "one request so far"

            Expect.equal
                (embed providerA)
                "circuit-open"
                "A's breaker is now OPEN, so its next call is fast-failed without a request"

            Expect.equal handler.RequestCount 1 "a fast-failed call must not reach the wire"

            Expect.equal
                (embed providerB)
                "retries-exhausted"
                "B holds its OWN breaker state — A tripping does not fast-fail B. This is the scope the wiring seam documents: per instance, not per process and not across silos"

            Expect.equal handler.RequestCount 2 "B's call did reach the wire"

        testCase "a provider holds exactly one HttpClient, and never one of its own"
        <| fun () ->
            let handler = new RecordingHandler(okResponse)
            use client = clientOver handler
            let store = RotatingSecretStore "key"

            let providerA = OpenAIEmbeddingProvider.createWithClient client store baseOptions
            let providerB = OpenAIEmbeddingProvider.createWithClient client store baseOptions

            for provider in [ providerA; providerB ] do
                match httpClientsHeldBy provider with
                | [ held ] ->
                    Expect.isTrue
                        (obj.ReferenceEquals(held, client))
                        "the provider must send through the client it was given, never one it constructed"
                | held ->
                    failtestf
                        "expected exactly one HttpClient field on the provider, found %d — a second transport means a second connection pool"
                        held.Length

            // Both instances really do use it, and neither disposes it.
            providerA.GenerateEmbedding "a" |> Async.RunSynchronously |> ignore
            providerB.GenerateEmbedding "b" |> Async.RunSynchronously |> ignore
            Expect.equal handler.RequestCount 2 "both providers sent through the one client"

        testCase "the create* factories share one HttpClient across every provider in the process"
        <| fun () ->
            // The production path (no explicit client). Construction makes
            // no request, so this touches no network — it asserts only
            // which transport the factories hand out.
            let store = RotatingSecretStore "key"

            let held (p: IEmbeddingProvider) =
                match httpClientsHeldBy p with
                | [ c ] -> c
                | other -> failtestf "expected exactly one HttpClient field, found %d" other.Length

            let first = OpenAIEmbeddingProvider.create store |> held
            let second = OpenAIEmbeddingProvider.create store |> held

            let third = OpenAIEmbeddingProvider.createWithOptions store baseOptions |> held

            Expect.isTrue
                (obj.ReferenceEquals(first, second) && obj.ReferenceEquals(second, third))
                "every provider built through create / createWithOptions must share the one per-process HttpClient — a client per instance owns its own connection pool, and its sockets linger in TIME_WAIT, so a provider built per ingest batch would exhaust the process's ephemeral ports"
    ]