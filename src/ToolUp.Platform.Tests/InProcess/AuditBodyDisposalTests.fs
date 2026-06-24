module ToolUp.Platform.Tests.InProcess.AuditBodyDisposalTests

open System.Net
open System.Net.Http
open System.Text
open System.Collections.Generic
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe

// ─── Regression — audited-but-unvalidated method with a request body ──
//
// An [<Audit>]-annotated method that carries a record input type BUT no
// ValidationAttributes used to throw `ObjectDisposedException` on
// `FileBufferingReadStream` AFTER the response had started: validation is
// the only pre-dispatch stage that seeds the body cache, so without it the
// audit-payload path's `readCachedBodyText ()` was the FIRST cache read —
// and by then the proxy's StreamReader had disposed `ctx.Request.Body`.
// Because the response had already started, ASP.NET's error handler could
// not run, the connection was reset, and the gateway surfaced it as a 502
// even though the handler had already succeeded (the live symptom was
// `TeamApi.CreateTeamWithOwner`: the team WAS created, but the browser saw
// a 502). The dispatcher now eagerly materialises the body for audited
// methods before dispatch, exactly as the idempotency path already did.
//
// These run over the real ToolUp.Remoting dispatcher on a TestServer so the
// genuine EnableBuffering → proxy-read → dispose lifecycle is exercised —
// the only shape that reproduces the disposed-stream throw.

type private AuditedCreateRequest = {
    [<PiiSafe>]
    Name: string
    OwnerId: string
}

// Audited + record input + NO validation attributes on the record =
// the exact unguarded shape. `Unaudited` rounds out the negative coverage
// (same body shape, no [<Audit>], must stay unaffected).
type private AuditedNoValidationApi = {
    [<AllowAnonymous>]
    [<Audit "PolicyChanged">]
    CreateThing: AuditedCreateRequest -> Async<string>
    [<AllowAnonymous>]
    Unaudited: AuditedCreateRequest -> Async<string>
}

let private impl: AuditedNoValidationApi = {
    CreateThing = fun req -> async { return req.Name }
    Unaudited = fun req -> async { return req.Name }
}

let private buildHost (h: HttpHandler) : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost.UseTestServer().Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe h)
            |> ignore)
        .Build()

// Per-call isolation: a fresh capture list + emitter + handler per
// request so the two tests never share mutable state (Expecto runs
// them in parallel). Returns the response plus the events this call
// emitted.
let private postJson (method: string) (body: string) = async {
    let captured = ResizeArray<AuditEvent>()

    let emitter =
        { new IAuditEmitter with
            member _.Emit event = async { lock captured (fun () -> captured.Add event) }
        }

    let handler =
        Remoting.createApi ()
        |> Remoting.withAudit emitter
        |> Remoting.fromValue impl
        |> Remoting.buildHttpHandler

    use host = buildHost handler
    do! host.StartAsync() |> Async.AwaitTask
    use client = host.GetTestClient()

    use req =
        new HttpRequestMessage(HttpMethod.Post, sprintf "/AuditedNoValidationApi/%s" method)

    req.Content <- new StringContent(body, Encoding.UTF8, "application/json")

    let! resp = client.SendAsync req |> Async.AwaitTask
    let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
    do! host.StopAsync() |> Async.AwaitTask
    let events = lock captured (fun () -> captured |> List.ofSeq)
    return resp.StatusCode, text, events
}

[<Tests>]
let tests =
    testList "Regression — audited-unvalidated body disposal" [

        testAsync "audited method with a body succeeds and emits its payload (no disposed-stream throw)" {
            // Single-arg method → array-wrapped single-element body, the
            // Fable.Remoting wire shape (mirrors the 88-byte CreateTeamWithOwner
            // body in the live repro).
            let! status, text, events =
                postJson "CreateThing" """[{"Name":"CPG","OwnerId":"d2d880c7-ab98-4e15-91bb-8eaf6fa40d33"}]"""

            Expect.equal status HttpStatusCode.OK "the handler succeeds — no post-response disposed-stream throw"
            Expect.stringContains text "CPG" "the handler result is returned intact"

            Expect.hasLength events 1 "exactly one audit event is emitted (the read no longer throws before Emit)"

            let evt = events[0]
            Expect.equal evt.MethodName "CreateThing" "audit event names the method"
            Expect.equal evt.Kind AuditKind.PolicyChanged "audit kind decoded from [<Audit>]"

            Expect.equal
                (evt.Payload |> Map.tryFind "Name")
                (Some "CPG")
                "the PiiSafe field carries its value in the audit payload — proof the body was read post-dispatch"

            Expect.equal
                (evt.Payload |> Map.tryFind "OwnerId")
                (Some "<redacted:String>")
                "the non-PiiSafe field is redacted"
        }

        testAsync "unaudited method with a body still succeeds (no needless materialise regression)" {
            let! status, text, events =
                postJson "Unaudited" """[{"Name":"Core","OwnerId":"d2d880c7-ab98-4e15-91bb-8eaf6fa40d33"}]"""

            Expect.equal status HttpStatusCode.OK "the unaudited path is unaffected"
            Expect.stringContains text "Core" "the handler result is returned intact"
            Expect.hasLength events 0 "unaudited methods emit no audit event"
        }
    ]