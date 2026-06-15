module ToolUp.Platform.Tests.InProcess.IdempotencyReplayAuditTests

open System.Net
open System.Net.Http
open System.Text
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe

// ─── Phase 69f.E + 69h.tail Task C — idempotency-replay audit (real HTTP) ──
//
// The structural pins in AuditTests / IdempotencyTests assert the replay
// early-return precedes the audit-emitting Success branch. This is the
// integration-shape companion: a real ToolUp.Remoting dispatcher mounted
// on an in-memory TestServer, with a composed IIdempotencyStore + a
// counting IAuditEmitter, dispatched over HTTP twice with the same
// X-Idempotency-Key. It pins the observable end-to-end behaviour:
//
//   * the handler runs exactly once (the replay skips it);
//   * the method's own [<Audit>] event (PolicyChanged) emits once, on the
//     first call only — a replay does NOT double-audit (69h.tail Task C);
//   * the replay emits exactly one IdempotencyReplay event citing the
//     original via the shared idempotency key (69f.E).
//
// This is the first remoting-dispatcher-over-TestServer test in this
// runner; it retires the "needs a TestServer scaffold" deferral the
// 69f.E / 69h.tail.C / 69o tails shared. The host shape mirrors
// GiraffeStockHelperTests.buildHost / PlatformPeerTests.buildSellerHost.

// Fixtures are NOT private — the wire converters + the dispatcher proxy
// reflect over the record/method shape (same constraint the
// GiraffeStockHelperTests + PlatformPeerTests fixtures document).
type ReplayInput = { WidgetId: string }

type ReplayAuditApi = {
    [<AllowAnonymous>]
    [<Idempotent>]
    [<Audit "PolicyChanged">]
    DoThing: ReplayInput -> Async<unit>
}

/// Records every AuditEvent the dispatcher emits so the test can assert
/// kind counts + payloads. Thread-safe — the dispatcher may emit from a
/// request thread.
type private CountingEmitter() =
    let events = System.Collections.Generic.List<AuditEvent>()
    member _.Events = lock events (fun () -> events |> List.ofSeq)

    interface IAuditEmitter with
        member _.Emit event = async { lock events (fun () -> events.Add event) }

let private dummyResolver (_: HttpContext) : Async<IAuthContext> = async {
    return
        { new IAuthContext with
            member _.HasRole _ = true
            member _.HasClaim(_, _) = true
            member _.HasTenant() = true
            member _.IsAnonymous() = false
            member _.SubjectId = "test-subject"
        }
}

let private buildHandler (emitter: IAuditEmitter) (store: IIdempotencyStore) (invocations: int ref) : HttpHandler =
    let impl: ReplayAuditApi = {
        DoThing = fun _ -> async { System.Threading.Interlocked.Increment invocations |> ignore }
    }

    Remoting.createApi ()
    |> Remoting.withAuthContext dummyResolver
    |> Remoting.withAudit emitter
    |> Remoting.withIdempotencyStore store
    |> Remoting.fromValue impl
    |> Remoting.buildHttpHandler

let private buildHost (handler: HttpHandler) : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost.UseTestServer().Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe handler)
            |> ignore)
        .Build()

/// POST a single-arg remoting call; return (status, was-replay, body).
let private post (client: HttpClient) (key: string) (bodyJson: string) = async {
    use req = new HttpRequestMessage(HttpMethod.Post, "/ReplayAuditApi/DoThing")
    req.Headers.Add("x-remoting-proxy", "true")
    req.Headers.Add("X-Idempotency-Key", key)
    req.Content <- new StringContent(bodyJson, Encoding.UTF8, "application/json")

    let! resp = client.SendAsync req |> Async.AwaitTask
    let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

    let wasReplay =
        match resp.Headers.TryGetValues "x-idempotency-replay" with
        | true, values -> values |> Seq.contains "true"
        | _ -> false

    return resp.StatusCode, wasReplay, body
}

[<Tests>]
let tests =
    testList "Phase 69f.E + 69h.tail.C — idempotency replay audit (integration)" [

        testAsync "replay skips the handler + the method audit, and emits one IdempotencyReplay citing the key" {
            let emitter = CountingEmitter()
            let store = InMemoryIdempotencyStore()
            let invocations = ref 0
            use host = buildHost (buildHandler emitter store invocations)
            do! host.StartAsync() |> Async.AwaitTask
            use client = host.GetTestClient()

            let key = "idem-key-1"
            let body = """[{"WidgetId":"w-1"}]"""

            let! (status1, replay1, _) = post client key body
            let! (status2, replay2, _) = post client key body

            do! host.StopAsync() |> Async.AwaitTask

            Expect.equal status1 HttpStatusCode.OK "first call succeeds"
            Expect.equal status2 HttpStatusCode.OK "replayed call succeeds"
            Expect.isFalse replay1 "first call is a fresh invocation, not a replay"
            Expect.isTrue replay2 "second call with the same key is served from the cache (x-idempotency-replay)"

            Expect.equal
                invocations.Value
                1
                "the handler ran exactly once — the replay short-circuited before invocation"

            let kinds = emitter.Events |> List.map (fun e -> e.Kind)

            Expect.equal
                (kinds |> List.filter ((=) AuditKind.PolicyChanged) |> List.length)
                1
                "the method's own [<Audit>] event emitted exactly once (first call only) — a replay must not double-audit"

            Expect.equal
                (kinds |> List.filter ((=) AuditKind.IdempotencyReplay) |> List.length)
                1
                "the replay emitted exactly one IdempotencyReplay event"

            let replayEvt =
                emitter.Events |> List.find (fun e -> e.Kind = AuditKind.IdempotencyReplay)

            Expect.equal replayEvt.MethodName "DoThing" "the replay event names the replayed method"

            Expect.equal
                (replayEvt.Payload |> Map.tryFind "idempotencyKey")
                (Some key)
                "the IdempotencyReplay event cites the original via the shared idempotency key"
        }
    ]