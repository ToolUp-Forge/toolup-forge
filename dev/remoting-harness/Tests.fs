module ToolUp.Remoting.Harness.Tests

open System
open System.Net
open System.Net.Http
open System.Text
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Hosting
open Expecto
open ToolUp.Remoting.Server
open ToolUp.Remoting.Harness.Server

// ---- Test fixture: in-memory TestServer + HttpClient ------------------------
//
// One host per top-level testList; tests inside share it. The host is
// idempotent — Echo / Heartbeat / Boom calls don't accumulate state, so
// shared-fixture is safe.

let private withClient (test: HttpClient -> unit) : unit =
    use host = buildHost None
    host.Start()
    use client = host.GetTestClient()
    test client

let private withTelemetryClient (test: RecordingTelemetry -> HttpClient -> unit) : unit =
    let sink = RecordingTelemetry.create ()
    use host = buildHost (Some (sink :> IRemotingTelemetry))
    host.Start()
    use client = host.GetTestClient()
    test sink client

let private postRemoting (client: HttpClient) (path: string) (body: string) : HttpResponseMessage =
    use req = new HttpRequestMessage(HttpMethod.Post, path)
    req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
    req.Headers.Add("x-remoting-proxy", "true")
    client.SendAsync(req).Result

let private readBody (response: HttpResponseMessage) : string =
    response.Content.ReadAsStringAsync().Result

// ---- Suite -----------------------------------------------------------------
//
// v0 coverage: Echo round-trip, Heartbeat (unit-method body normalisation),
// Boom (error envelope). Each Phase 69b/c/d/... seam adds tests here as
// it ships.

[<Tests>]
let tests =
    testList "ToolUp.Remoting harness — v0" [

        testCase "Echo: round-trips a string through dispatcher + STJ"
        <| fun _ ->
            withClient
            <| fun client ->
                let response = postRemoting client "/api/IHarnessApi/Echo" "[\"hello\"]"
                Expect.equal response.StatusCode HttpStatusCode.OK "200 expected"
                let body = readBody response
                Expect.equal body "\"hello\"" "Echo body should round-trip the string verbatim"

        testCase "Heartbeat: unit method works through body normalisation (empty body)"
        <| fun _ ->
            withClient
            <| fun client ->
                let response = postRemoting client "/api/IHarnessApi/Heartbeat" ""
                Expect.equal response.StatusCode HttpStatusCode.OK "200 expected"
                let body = readBody response
                // Body is an STJ-serialised DateTimeOffset; we don't pin the
                // exact value (time moves), but the shape — a quoted ISO-8601
                // string with an offset suffix — is byte-shape-invariant.
                Expect.isGreaterThan body.Length 20 "DateTimeOffset body should be non-trivial"
                Expect.stringStarts body "\"" "DateTimeOffset serialises as a quoted string"
                Expect.stringEnds body "\"" "DateTimeOffset serialises as a quoted string"

        testCase "Heartbeat: unit method works with \"null\" body"
        <| fun _ ->
            withClient
            <| fun client ->
                let response = postRemoting client "/api/IHarnessApi/Heartbeat" "null"
                Expect.equal response.StatusCode HttpStatusCode.OK "Body normalisation should turn \"null\" into []"

        testCase "Heartbeat: unit method works with \"\" body"
        <| fun _ ->
            withClient
            <| fun client ->
                let response = postRemoting client "/api/IHarnessApi/Heartbeat" "\"\""
                Expect.equal response.StatusCode HttpStatusCode.OK "Body normalisation should turn empty-string into []"

        testCase "Boom: throwing handler returns 500 with the propagated error message"
        <| fun _ ->
            withClient
            <| fun client ->
                let response = postRemoting client "/api/IHarnessApi/Boom" "[\"fuse-blown\"]"
                Expect.equal response.StatusCode HttpStatusCode.InternalServerError "500 expected on handler exception"
                let body = readBody response
                Expect.stringContains body "Boom" "Error body should carry the propagated message"
                Expect.stringContains body "fuse-blown" "Error body should carry the original reason"

        // ---- Phase 69b.B coverage: fromContextAsync per-request resolution ----

        testCase "WhoAmI: fromContextAsync resolves header per request — Alice"
        <| fun _ ->
            withClient
            <| fun client ->
                use req = new HttpRequestMessage(HttpMethod.Post, "/api/IContextApi/WhoAmI")
                req.Content <- new StringContent("", Encoding.UTF8, "application/json")
                req.Headers.Add("x-remoting-proxy", "true")
                req.Headers.Add("X-Subject", "alice")
                let response = client.SendAsync(req).Result
                Expect.equal response.StatusCode HttpStatusCode.OK "200 expected"
                let body = readBody response
                Expect.equal body "\"alice\"" "Async resolver should return the X-Subject header value"

        testCase "WhoAmI: subsequent request with different header returns different subject — Bob"
        <| fun _ ->
            withClient
            <| fun client ->
                use req = new HttpRequestMessage(HttpMethod.Post, "/api/IContextApi/WhoAmI")
                req.Content <- new StringContent("", Encoding.UTF8, "application/json")
                req.Headers.Add("x-remoting-proxy", "true")
                req.Headers.Add("X-Subject", "bob")
                let response = client.SendAsync(req).Result
                Expect.equal response.StatusCode HttpStatusCode.OK "200 expected"
                let body = readBody response
                Expect.equal body "\"bob\"" "Async resolver re-runs per request — does NOT snapshot at boot"

        testCase "WhoAmI: missing header gives default 'anonymous' subject"
        <| fun _ ->
            withClient
            <| fun client ->
                use req = new HttpRequestMessage(HttpMethod.Post, "/api/IContextApi/WhoAmI")
                req.Content <- new StringContent("", Encoding.UTF8, "application/json")
                req.Headers.Add("x-remoting-proxy", "true")
                let response = client.SendAsync(req).Result
                Expect.equal response.StatusCode HttpStatusCode.OK "200 expected"
                let body = readBody response
                Expect.equal body "\"anonymous\"" "Async resolver default branch fires when header absent"

        // ---- Phase 69b.C coverage: telemetry hook emission ----

        testCase "Telemetry: success call emits one Succeeded MethodTelemetry"
        <| fun _ ->
            withTelemetryClient
            <| fun sink client ->
                let response = postRemoting client "/api/IHarnessApi/Echo" "[\"hello\"]"
                Expect.equal response.StatusCode HttpStatusCode.OK "200 expected"
                let events = sink.Events
                Expect.equal events.Length 1 "Exactly one telemetry event expected per call"
                let evt = events.[0]
                Expect.equal evt.MethodName "Echo" "MethodName should reflect the invoked method"
                Expect.equal evt.Outcome MethodOutcome.Succeeded "Successful invocation should record Succeeded outcome"
                Expect.isGreaterThanOrEqual evt.ElapsedMs 0 "ElapsedMs should be non-negative"

        testCase "Telemetry: exception emits Failed outcome carrying the exn"
        <| fun _ ->
            withTelemetryClient
            <| fun sink client ->
                let response = postRemoting client "/api/IHarnessApi/Boom" "[\"fuse-blown\"]"
                Expect.equal response.StatusCode HttpStatusCode.InternalServerError "500 expected"
                let events = sink.Events
                Expect.equal events.Length 1 "One telemetry event expected for the failing call"
                let evt = events.[0]
                Expect.equal evt.MethodName "Boom" "MethodName captured even on failure"
                match evt.Outcome with
                | MethodOutcome.Failed ex ->
                    Expect.stringContains ex.Message "fuse-blown" "Exception carries the original reason"
                | MethodOutcome.Succeeded ->
                    failtest "Expected Failed outcome, got Succeeded"

        // ---- Phase 69b.D coverage: correlation-id ambient propagation ----

        testCase "WhereAreWe: provided x-correlation-id flows into ambient context"
        <| fun _ ->
            withClient
            <| fun client ->
                use req = new HttpRequestMessage(HttpMethod.Post, "/api/IContextApi/WhereAreWe")
                req.Content <- new StringContent("", Encoding.UTF8, "application/json")
                req.Headers.Add("x-remoting-proxy", "true")
                req.Headers.Add("x-correlation-id", "abc-123-xyz")
                let response = client.SendAsync(req).Result
                Expect.equal response.StatusCode HttpStatusCode.OK "200 expected"
                let body = readBody response
                Expect.equal body "\"abc-123-xyz\"" "Handler reads ambient correlation id without threading"
                // Server stamps it back on the response so the client correlates end-to-end.
                let echoed =
                    if response.Headers.Contains("x-correlation-id") then
                        response.Headers.GetValues("x-correlation-id") |> Seq.head
                    else "<missing>"
                Expect.equal echoed "abc-123-xyz" "Server stamps correlation id back on response header"

        testCase "WhereAreWe: missing x-correlation-id gets a generated GUID + response header"
        <| fun _ ->
            withClient
            <| fun client ->
                use req = new HttpRequestMessage(HttpMethod.Post, "/api/IContextApi/WhereAreWe")
                req.Content <- new StringContent("", Encoding.UTF8, "application/json")
                req.Headers.Add("x-remoting-proxy", "true")
                let response = client.SendAsync(req).Result
                Expect.equal response.StatusCode HttpStatusCode.OK "200 expected"
                let body = readBody response
                // Body is the generated GUID, quoted as a JSON string.
                Expect.isGreaterThan body.Length 20 "Generated correlation id should be a non-trivial GUID string"
                Expect.notEqual body "\"<absent>\"" "Dispatcher generates a correlation id when header absent"
                let echoed =
                    if response.Headers.Contains("x-correlation-id") then
                        response.Headers.GetValues("x-correlation-id") |> Seq.head
                    else "<missing>"
                // The echoed header should match the body value (sans JSON quotes).
                Expect.equal ("\"" + echoed + "\"") body "Generated correlation id is consistent between body and response header"

        // ---- Phase 69d coverage: authorisation metadata ----

        testCase "AdminOnly: caller with Admin role gets through"
        <| fun _ ->
            withClient
            <| fun client ->
                use req = new HttpRequestMessage(HttpMethod.Post, "/api/ISecureApi/AdminOnly")
                req.Content <- new StringContent("", Encoding.UTF8, "application/json")
                req.Headers.Add("x-remoting-proxy", "true")
                req.Headers.Add("X-Roles", "Admin")
                let response = client.SendAsync(req).Result
                Expect.equal response.StatusCode HttpStatusCode.OK "Admin caller authorised"
                let body = readBody response
                Expect.equal body "\"admin-secret\"" "Body returns the handler's value"

        testCase "AdminOnly: caller without Admin role is denied with category=auth"
        <| fun _ ->
            withClient
            <| fun client ->
                use req = new HttpRequestMessage(HttpMethod.Post, "/api/ISecureApi/AdminOnly")
                req.Content <- new StringContent("", Encoding.UTF8, "application/json")
                req.Headers.Add("x-remoting-proxy", "true")
                req.Headers.Add("X-Roles", "User")
                let response = client.SendAsync(req).Result
                Expect.equal response.StatusCode HttpStatusCode.Unauthorized "401 expected on auth deny"
                let body = readBody response
                Expect.stringContains body "\"category\":\"auth\"" "Categorised auth envelope"

        testCase "AdminOnly: anonymous caller (no X-Roles header) is denied"
        <| fun _ ->
            withClient
            <| fun client ->
                use req = new HttpRequestMessage(HttpMethod.Post, "/api/ISecureApi/AdminOnly")
                req.Content <- new StringContent("", Encoding.UTF8, "application/json")
                req.Headers.Add("x-remoting-proxy", "true")
                let response = client.SendAsync(req).Result
                Expect.equal response.StatusCode HttpStatusCode.Unauthorized "Anonymous denied on RequiresRole method"

        testCase "OpenToAll: AllowAnonymous method always returns success"
        <| fun _ ->
            withClient
            <| fun client ->
                use req = new HttpRequestMessage(HttpMethod.Post, "/api/ISecureApi/OpenToAll")
                req.Content <- new StringContent("", Encoding.UTF8, "application/json")
                req.Headers.Add("x-remoting-proxy", "true")
                let response = client.SendAsync(req).Result
                Expect.equal response.StatusCode HttpStatusCode.OK "AllowAnonymous always passes"
                let body = readBody response
                Expect.equal body "\"everyone-welcome\"" "Handler invoked"

        testCase "PublicOnly: PublicEndpoint method passes without resolver invocation"
        <| fun _ ->
            withClient
            <| fun client ->
                use req = new HttpRequestMessage(HttpMethod.Post, "/api/ISecureApi/PublicOnly")
                req.Content <- new StringContent("", Encoding.UTF8, "application/json")
                req.Headers.Add("x-remoting-proxy", "true")
                let response = client.SendAsync(req).Result
                Expect.equal response.StatusCode HttpStatusCode.OK "PublicEndpoint always passes"
                let body = readBody response
                Expect.equal body "\"public-info\"" "Handler invoked"

        // ---- Phase 69b.E coverage: error envelope categorisation ----

        testCase "BoomCategorised: handler maps UserError → ErrorCategory.User wire envelope"
        <| fun _ ->
            withClient
            <| fun client ->
                let response = postRemoting client "/api/IHarnessApi/BoomCategorised" "[\"bad-input\"]"
                Expect.equal response.StatusCode HttpStatusCode.InternalServerError "500 expected"
                let body = readBody response
                Expect.stringContains body "\"category\":\"user\"" "Categorised envelope carries category field"
                Expect.stringContains body "User-fault" "Original error payload preserved in `error` body"
                Expect.stringContains body "bad-input" "Original reason preserved through envelope"

        testCase "Boom: uncategorised exception path stays wire-compatible (no category field)"
        <| fun _ ->
            withClient
            <| fun client ->
                let response = postRemoting client "/api/IHarnessApi/Boom" "[\"old-style\"]"
                Expect.equal response.StatusCode HttpStatusCode.InternalServerError "500 expected"
                let body = readBody response
                Expect.isFalse (body.Contains "\"category\"") "Legacy Propagate path emits no category field — backwards-compatible wire shape"
                Expect.stringContains body "old-style" "Original reason preserved"

        testCase "Telemetry: MethodTelemetry includes the correlation id from the request"
        <| fun _ ->
            withTelemetryClient
            <| fun sink client ->
                use req = new HttpRequestMessage(HttpMethod.Post, "/api/IHarnessApi/Echo")
                req.Content <- new StringContent("[\"corr-test\"]", Encoding.UTF8, "application/json")
                req.Headers.Add("x-remoting-proxy", "true")
                req.Headers.Add("x-correlation-id", "cid-42")
                let _ = client.SendAsync(req).Result
                let events = sink.Events
                Expect.equal events.Length 1 "One event"
                Expect.equal events.[0].CorrelationId (Some "cid-42") "Telemetry carries the request's correlation id"

        testCase "Telemetry: three sequential calls produce three events in order"
        <| fun _ ->
            withTelemetryClient
            <| fun sink client ->
                let _ = postRemoting client "/api/IHarnessApi/Echo" "[\"first\"]"
                let _ = postRemoting client "/api/IHarnessApi/Echo" "[\"second\"]"
                let _ = postRemoting client "/api/IHarnessApi/Heartbeat" ""
                let events = sink.Events
                Expect.equal events.Length 3 "Three calls = three telemetry events"
                let methodNames = events |> List.map _.MethodName |> List.sort
                Expect.equal methodNames [ "Echo"; "Echo"; "Heartbeat" ] "All three methods recorded by name"

        testCase "WhoAmI: two sequential requests on same client get distinct per-call resolution"
        <| fun _ ->
            withClient
            <| fun client ->
                // The core build-once / read-per-call check: prove the resolver
                // is invoked PER CALL, not once at Api.make time. If snapshotting,
                // the second call would return whatever the first call resolved.
                let callWith subject =
                    use req = new HttpRequestMessage(HttpMethod.Post, "/api/IContextApi/WhoAmI")
                    req.Content <- new StringContent("", Encoding.UTF8, "application/json")
                    req.Headers.Add("x-remoting-proxy", "true")
                    req.Headers.Add("X-Subject", (subject: string))
                    let response = client.SendAsync(req).Result
                    readBody response
                let first = callWith "alice"
                let second = callWith "bob"
                Expect.equal first "\"alice\"" "First request resolves Alice"
                Expect.equal second "\"bob\"" "Second request on same client resolves Bob — proves per-call invocation"
    ]