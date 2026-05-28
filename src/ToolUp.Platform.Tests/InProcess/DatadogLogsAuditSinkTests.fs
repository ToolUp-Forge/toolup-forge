module ToolUp.Platform.Tests.InProcess.DatadogLogsAuditSinkTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AuditSinks.DatadogLogs
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts

/// `HttpMessageHandler` that always returns 202 Accepted (Datadog's
/// success status for the Logs intake endpoint) and records every
/// outgoing request body. Mirrors the SplunkHec test harness.
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
            return new HttpResponseMessage(HttpStatusCode.Accepted)
        }

type private FakeSecretStore(apiKey: string) =
    interface ISecretStore with
        member _.GetSecret(_scope, _key) = async { return Some apiKey }
        member _.SetSecret(_scope, _key, _value) = async { return Ok() }
        member _.DeleteSecret(_scope, _key) = async { return Ok() }
        member _.ListKeys(_scope) = async { return [] }

let private handlers = ConcurrentDictionary<obj, RecordingHandler>()

let tests =
    let factory () =
        let handler = new RecordingHandler()
        let httpClient = new HttpClient(handler)

        let settings: DatadogLogsSettings = {
            EndpointUrl = "https://localhost.invalid/api/v2/logs"
            Service = "toolup"
            Env = "test"
            DdSource = "toolup_audit"
            Host = None
        }

        let secretStore = FakeSecretStore("test-key") :> ISecretStore
        let sink = create "test-datadog" settings secretStore "datadog_api_key" httpClient

        handlers[box sink] <- handler
        sink

    let verifyDelivered (sink: IAuditSink) (expected: AuditEnvelope list list) =
        let handler = handlers[box sink]

        Expect.hasLength handler.RecordedBodies (List.length expected) "one POST per delivered batch"

    IAuditSinkContract.tests "DatadogLogsAuditSink" factory verifyDelivered