module ToolUp.Platform.Tests.InProcess.SplunkHecAuditSinkTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AuditSinks.SplunkHec
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts

/// `HttpMessageHandler` that always returns 200 OK and records every
/// outgoing request body for verification. Used by the contract pack
/// binding so we can exercise the SplunkHec sink without a real
/// Splunk endpoint.
type private RecordingHandler() =
    inherit HttpMessageHandler()

    let bodies = ConcurrentQueue<string>()

    member _.RecordedBodies: string list = bodies |> List.ofSeq

    override _.SendAsync
        (request: HttpRequestMessage, _cancellationToken: CancellationToken)
        : Task<HttpResponseMessage> =
        task {
            let! body =
                if isNull request.Content then
                    Task.FromResult ""
                else
                    request.Content.ReadAsStringAsync()

            bodies.Enqueue body
            return new HttpResponseMessage(HttpStatusCode.OK)
        }

/// In-memory `ISecretStore` that returns a fixed token. We don't need
/// the full FileSecretStore here — the SplunkHec sink just calls
/// `GetSecret(_platform, secretKey)`.
type private FakeSecretStore(token: string) =
    interface ISecretStore with
        member _.GetSecret(_scope, _key) = async { return Some token }
        member _.SetSecret(_scope, _key, _value) = async { return Ok() }
        member _.DeleteSecret(_scope, _key) = async { return Ok() }
        member _.ListKeys(_scope) = async { return [] }

/// Per-sink recording handler so the verifier can locate the right
/// request log per sink instance.
let private handlers = ConcurrentDictionary<obj, RecordingHandler>()

let tests =
    let factory () =
        let handler = new RecordingHandler()
        let httpClient = new HttpClient(handler)

        let settings: SplunkHecSettings = {
            EndpointUrl = "https://localhost.invalid/services/collector/event"
            Sourcetype = "toolup_audit"
            Index = None
            Host = None
        }

        let secretStore = FakeSecretStore("test-token") :> ISecretStore
        let sink = create "test-splunk" settings secretStore "splunk_token" httpClient

        handlers[box sink] <- handler
        sink

    let verifyDelivered (sink: IAuditSink) (expected: AuditEnvelope list list) =
        let handler = handlers[box sink]

        Expect.hasLength handler.RecordedBodies (List.length expected) "one POST per delivered batch"

    IAuditSinkContract.tests "SplunkHecAuditSink" factory verifyDelivered