// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ServerRemotingTailTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Platform
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 856 — the server remoting tail ────────────────────────────
//
// 856.B — ONE deserialise. A validated method's first argument used to be
// decoded twice: once by the validation pre-flight
// (`Validation.parseFirstArgFromBody`, a plain `JsonSerializer.Deserialize`
// by `Type`) and again by the proxy at dispatch. The pre-flight now runs
// the proxy's own parse + first-argument decode (`Proxy.ApiProxy.ParseFirst`)
// and hands the result to dispatch. Pinned BEHAVIOURALLY, on the real
// dispatcher over a TestServer: a converter for the input type counts how
// many times the request decodes it.
//
// 856.C — audit off the response path. The default remoting audit emitter
// enqueues on `RemotingAuditQueue` when compose registered one, and a
// hosted service writes; full is back-pressure (inline write), a failed
// write is classified, and shutdown drains.

// ─── 856.B ───────────────────────────────────────────────────────────

type ValidatedInput = {
    [<MinLength 3>]
    Name: string
    Count: int
}

type ValidatedApi = {
    [<AllowAnonymous>]
    Create: ValidatedInput -> Async<string>
}

/// Counts every decode of `ValidatedInput` and delegates to the full
/// platform converter set, so the value decoded is the one it always was.
type private CountingConverter(inner: JsonSerializerOptions, decodes: int ref) =
    inherit JsonConverter<ValidatedInput>()

    override _.Read(reader: byref<Utf8JsonReader>, _typeToConvert: Type, _options: JsonSerializerOptions) =
        decodes.Value <- decodes.Value + 1
        JsonSerializer.Deserialize<ValidatedInput>(&reader, inner)

    override _.Write(writer: Utf8JsonWriter, value: ValidatedInput, _options: JsonSerializerOptions) =
        JsonSerializer.Serialize(writer, value, inner)

let private buildHost (handler: HttpHandler) : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost.UseTestServer().Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe handler)
            |> ignore)
        .Build()

let private post (host: IHost) (path: string) (body: string) = async {
    do! host.StartAsync() |> Async.AwaitTask
    use client = host.GetTestClient()
    use req = new HttpRequestMessage(HttpMethod.Post, path)
    req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
    let! resp = client.SendAsync req |> Async.AwaitTask
    let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
    do! host.StopAsync() |> Async.AwaitTask
    return resp.StatusCode, text
}

/// A validated API whose input-type decodes are counted, and whose handler
/// counts its own invocations.
let private countedValidatedApi () =
    let decodes = ref 0
    let invoked = ref 0
    let options = FableConverters.create ()
    options.Converters.Insert(0, CountingConverter(FableConverters.create (), decodes))

    let impl: ValidatedApi = {
        Create =
            fun input -> async {
                invoked.Value <- invoked.Value + 1
                return sprintf "%s:%d" input.Name input.Count
            }
    }

    let handler =
        Remoting.createApi ()
        |> Remoting.fromValue impl
        |> Remoting.withSerializerOptions options
        |> Remoting.buildHttpHandler

    handler, decodes, invoked

// ─── 856.C ───────────────────────────────────────────────────────────

/// An `IAuditLog` whose writes wait on a gate, so a test can tell an
/// awaited write from an enqueued one.
type private GatedAuditLog(gate: Task) =
    let written =
        System.Collections.Concurrent.ConcurrentQueue<string * ToolUp.Platform.AuditEvent>()

    member _.Written = List.ofSeq written

    interface IAuditLog with
        member _.Record(scopeId, audit) = async {
            do! gate |> Async.AwaitTask
            written.Enqueue(scopeId, audit)
        }

        member _.GetAuditTrail(_, _, _) = async { return [] }

type private ThrowingAuditLog() =
    interface IAuditLog with
        member _.Record(_, _) = async { return raise (InvalidOperationException "sink unavailable") }
        member _.GetAuditTrail(_, _, _) = async { return [] }

type private CountingMetrics() =
    let increments =
        System.Collections.Concurrent.ConcurrentQueue<string * Map<string, string>>()

    member _.Increments = List.ofSeq increments

    interface Metrics.IMetricsSink with
        member _.Record(_, _, _) = ()
        member _.Increment(name, tags) = increments.Enqueue(name, tags)
        member _.SetGauge(_, _, _) = ()

type private QuietLogger() =
    let warnings = System.Collections.Concurrent.ConcurrentQueue<string>()
    member _.Warnings = List.ofSeq warnings

    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn message = warnings.Enqueue message
        member _.Error(_, _) = ()

let private remotingEvent: ToolUp.Remoting.Server.AuditEvent = {
    Kind = AuditKind.PolicyChanged
    MethodName = "Create"
    SubjectId = "user:1"
    CorrelationId = Some "corr-856"
    Timestamp = DateTimeOffset.UtcNow
    Payload = Map.empty
}

let private platformEvent (method: string) : ToolUp.Platform.AuditEvent =
    ToolUp.Platform.AuditEvent.RemotingMethodAudited {
        Kind = "PolicyChanged"
        MethodName = method
        SubjectId = "user:1"
        CorrelationId = None
        Payload = Map.empty
    }

/// Run the default emitter as a request would: its `IServiceProvider`
/// stashed in the same `AsyncLocal` `Api.make`'s wrapper fills.
let private emitWith (services: IServiceProvider) : Task =
    ApiSeams.requestServices.Value <- services
    ApiSeams.requestScopeId.Value <- "scope-856"
    ApiSeams.defaultAuditEmitter.Emit remotingEvent |> Async.StartAsTask :> Task

let private registered (config: ServerConfig) : bool =
    let services = ServiceCollection()
    ComposeRuntimeServices.registerRemotingAuditQueue services config (QuietLogger())
    services |> Seq.exists (fun d -> d.ServiceType = typeof<RemotingAuditQueue>)

[<Tests>]
let tests =
    testList "Phase 856 — the server remoting tail" [

        testList "856.B — a validated method's body is deserialised once" [

            testAsync "a valid call decodes its first argument ONCE and reaches the handler" {
                let handler, decodes, invoked = countedValidatedApi ()
                use host = buildHost handler
                let! status, text = post host "/ValidatedApi/Create" """[{"Name":"CPG","Count":3}]"""

                Expect.equal status HttpStatusCode.OK "the valid call succeeds"
                Expect.stringContains text "CPG:3" "the handler got the decoded value"
                Expect.equal invoked.Value 1 "the handler ran once"

                Expect.equal
                    decodes.Value
                    1
                    "validation and dispatch share ONE decode of the first argument (it was two before Phase 856.B)"
            }

            testAsync "an invalid call is refused by validation after ONE decode, and the handler never runs" {
                let handler, decodes, invoked = countedValidatedApi ()
                use host = buildHost handler
                let! status, text = post host "/ValidatedApi/Create" """[{"Name":"ab","Count":3}]"""

                Expect.equal status HttpStatusCode.BadRequest "MinLength 3 refuses a two-character name"
                Expect.stringContains text "MinLength" "the refusal names the violated rule"
                Expect.equal invoked.Value 0 "a refused call never reaches the handler"
                Expect.equal decodes.Value 1 "the validation stage decoded once, and nothing decoded again"
            }

            testAsync "a first argument the dispatch decode refuses is still the proxy's refusal, not a validation pass" {
                let handler, _, invoked = countedValidatedApi ()
                use host = buildHost handler
                let! status, _ = post host "/ValidatedApi/Create" """[{"Name":"CPG","Count":"three"}]"""

                Expect.notEqual status HttpStatusCode.OK "an undecodable argument is refused"
                Expect.equal invoked.Value 0 "and the handler never runs"
            }
        ]

        testList "856.C — the default audit write is off the response path" [

            test "with the queue composed, the emitter returns WITHOUT awaiting a slow write" {
                let gate = TaskCompletionSource()
                let log = GatedAuditLog(gate.Task)
                let queue = RemotingAuditQueue(RemotingAuditQueue.DefaultCapacity)

                use services =
                    ServiceCollection()
                        .AddSingleton<IAuditLog>(log)
                        .AddSingleton<RemotingAuditQueue>(queue)
                        .BuildServiceProvider()

                let emitted = emitWith services

                Expect.isTrue
                    (emitted.Wait(TimeSpan.FromSeconds 5.0))
                    "Emit completed while the audit write was still blocked"

                Expect.equal queue.Count 1 "the record is queued, not lost"
                Expect.isEmpty log.Written "and not yet written"
                gate.SetResult()
            }

            test "without the queue the write is inline, exactly as before" {
                let gate = TaskCompletionSource()
                let log = GatedAuditLog(gate.Task)

                use services =
                    ServiceCollection().AddSingleton<IAuditLog>(log).BuildServiceProvider()

                let emitted = emitWith services

                Expect.isFalse
                    (emitted.Wait(TimeSpan.FromMilliseconds 200.0))
                    "Emit awaits the write when no queue is composed"

                gate.SetResult()
                Expect.isTrue (emitted.Wait(TimeSpan.FromSeconds 5.0)) "and completes once the write does"
                Expect.equal (log.Written |> List.map fst) [ "scope-856" ] "written inline, under the request's scope"
            }

            test "a FULL queue is back-pressure — the record is written inline, never dropped" {
                let gate = TaskCompletionSource()
                gate.SetResult()
                let log = GatedAuditLog(gate.Task)
                let queue = RemotingAuditQueue(1)

                Expect.isTrue
                    (queue.TryEnqueue {
                        AuditLog = log
                        ScopeId = "earlier"
                        Event = platformEvent "Earlier"
                    })
                    "the one slot is taken"

                use services =
                    ServiceCollection()
                        .AddSingleton<IAuditLog>(log)
                        .AddSingleton<RemotingAuditQueue>(queue)
                        .BuildServiceProvider()

                Expect.isTrue ((emitWith services).Wait(TimeSpan.FromSeconds 5.0)) "Emit completed"
                Expect.equal (log.Written |> List.map fst) [ "scope-856" ] "the overflow record was written inline"
                Expect.equal queue.Count 1 "the queued one is still waiting for the drain"
            }

            test "a write the log lets escape is classified: the audit write-failure counter and the Warn" {
                let metrics = CountingMetrics()
                let logger = QuietLogger()

                let work = {
                    AuditLog = ThrowingAuditLog()
                    ScopeId = "scope-856"
                    Event = platformEvent "Create"
                }

                (RemotingAuditDrainService.writeOne logger (fun () -> metrics) work).Wait()

                Expect.equal
                    metrics.Increments
                    [
                        AuditLog.AuditMetrics.WriteFailuresTotal, Map [ "event_type", "RemotingMethodAudited" ]
                    ]
                    "counted on the existing audit failure counter, tagged with the event type"

                Expect.exists
                    logger.Warnings
                    (fun w -> w.Contains "[AuditLog] write failed" && w.Contains "sink unavailable")
                    "and warned in the audit log's own words"
            }

            test "shutdown drains every accepted record into the in-memory audit log" {
                let store = InMemoryEventStore.InMemoryEventStore()
                let auditLog = AuditLog.EventStoreAuditLog(store, QuietLogger()) :> IAuditLog
                let queue = RemotingAuditQueue(RemotingAuditQueue.DefaultCapacity)

                let service =
                    new RemotingAuditDrainService.RemotingAuditDrainService(
                        queue,
                        QuietLogger(),
                        fun () -> CountingMetrics() :> Metrics.IMetricsSink
                    )

                service.StartAsync(CancellationToken.None).Wait()

                for i in 1..50 do
                    Expect.isTrue
                        (queue.TryEnqueue {
                            AuditLog = auditLog
                            ScopeId = "scope-856"
                            Event = platformEvent (sprintf "M%d" i)
                        })
                        "accepted"

                service.StopAsync(CancellationToken.None).Wait()

                let trail =
                    auditLog.GetAuditTrail("scope-856", None, Some "RemotingMethodAudited")
                    |> Async.RunSynchronously

                Expect.equal trail.Length 50 "every accepted record was written before the drain stopped"
                Expect.equal queue.Count 0 "nothing left behind"

                Expect.isFalse
                    (queue.TryEnqueue {
                        AuditLog = auditLog
                        ScopeId = "late"
                        Event = platformEvent "Late"
                    })
                    "a record after shutdown is refused by the queue, so the emitter writes it inline"
            }

            test "compose registers the queue only where a drain can honour it" {
                let enabled = {
                    ServerConfig.defaults with
                        AuditLog = EnabledAuditLog
                }

                Expect.isTrue (registered enabled) "an enabled audit log under the default policy gets the queue"

                Expect.isFalse
                    (registered {
                        enabled with
                            AuditFailurePolicy = RefuseAction
                    })
                    "RefuseAction keeps the write inline — its caller must see the failure"

                Expect.isFalse
                    (registered {
                        enabled with
                            ServerlessHost = ServerlessHost
                    })
                    "a serverless host keeps no drain alive"

                Expect.isFalse
                    (registered {
                        enabled with
                            ProcessProfile = WorkerOnly
                    })
                    "a silo serving no HTTP has no audited calls to queue"

                Expect.isFalse (registered { enabled with AuditLog = NoAuditLog }) "no audit log, no queue (GP 13)"
            }
        ]
    ]