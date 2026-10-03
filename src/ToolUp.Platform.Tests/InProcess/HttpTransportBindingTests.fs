module ToolUp.Platform.Tests.InProcess.HttpTransportBindingTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Threading.Tasks
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.Transport
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.IHttpTransportContract
open ToolUp.DataSources.Common

// ─── Phase 128 — IHttpTransport contract bindings ────────────────────
//
// The two production implementations of the seam, each wired to the
// pack's stub server with no network:
//
//   * `HttpClientTransport` (ToolUp.Platform.Server) — over an
//     `HttpClient` whose handler answers from the stub, so the BCL request
//     and response mapping is what runs;
//   * `OutboundRateBudget.decorate` (ToolUp.DataSources.Common) — over a
//     stub transport, under a budget generous enough never to refuse, so
//     what is proven is that the decorator passes the seam's promises
//     through untouched.

/// An `HttpMessageHandler` that answers from the stub server; a `None`
/// reply raises as a refused socket would.
type private StubHandler(server: StubServer) =
    inherit HttpMessageHandler()

    override _.SendAsync(request, _) = task {
        let! body =
            if isNull request.Content then
                Task.FromResult(None)
            else
                task {
                    let! text = request.Content.ReadAsStringAsync()
                    return Some text
                }

        let wire: WireRequest = {
            Method = request.Method.Method
            Url = string request.RequestUri
            Headers =
                request.Headers
                |> Seq.collect (fun kv -> kv.Value |> Seq.map (fun v -> kv.Key, v))
                |> List.ofSeq
            Body = body
        }

        match server wire with
        | None -> return raise (HttpRequestException "connection refused (stub)")
        | Some reply ->
            let response = new HttpResponseMessage(enum<HttpStatusCode> reply.StatusCode)
            response.Content <- new StringContent(reply.Body)

            for name, value in reply.Headers do
                if not (response.Headers.TryAddWithoutValidation(name, value)) then
                    response.Content.Headers.TryAddWithoutValidation(name, value) |> ignore

            return response
    }

let private httpClientTransport (server: StubServer) : IHttpTransport =
    let client = new HttpClient(new StubHandler(server), BaseAddress = StubBase)
    HttpClientTransport(client, TimeSpan.FromSeconds 10.0) :> IHttpTransport

/// The decorator's inner transport: resolves the URL against the base the
/// way a host adapter does, and raises on a refused connection.
let private stubTransport (server: StubServer) : IHttpTransport =
    { new IHttpTransport with
        member _.Send request = async {
            let wire: WireRequest = {
                Method = request.Method
                Url = string (Uri(StubBase, request.Url))
                Headers = request.Headers
                Body = request.Body
            }

            match server wire with
            | Some reply -> return reply
            | None -> return raise (IOException "connection refused (stub)")
        }
    }

let private noopLogger =
    { new ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(_: string) = ()
        member _.Error(_: string, _: exn option) = ()
    }

let private budgetedTransport (server: StubServer) : IHttpTransport =
    let window: RateLimitDescriptor = {
        Provider = "stub"
        ShortWindow = 10_000, TimeSpan.FromHours 1.0
        LongWindow = None
        FairnessMode = PerScope
    }

    let limiter =
        InProcessRateLimiter(
            [ window ],
            NoOpMetricsSink() :> IMetricsSink,
            AuditLog.NoOpAuditLog() :> IAuditLog,
            TimeSpan.FromSeconds 5.0,
            noopLogger
        )
        :> IRateLimiter

    let budget = {
        OutboundRateBudget.ofWindow window with
            MaxConcurrency = Some 2
    }

    OutboundRateBudget.decorate limiter TimeProvider.System budget "team-a" None (stubTransport server)

let tests =
    Expecto.Tests.testList "IHttpTransport bindings" [
        IHttpTransportContract.tests "HttpClientTransport" httpClientTransport
        IHttpTransportContract.tests "OutboundRateBudget.decorate" budgetedTransport
    ]