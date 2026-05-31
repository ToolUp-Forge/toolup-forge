module ToolUp.Remoting.Harness.Tests

open System
open System.Net
open System.Net.Http
open System.Text
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Hosting
open Expecto
open ToolUp.Remoting.Harness.Server

// ---- Test fixture: in-memory TestServer + HttpClient ------------------------
//
// One host per top-level testList; tests inside share it. The host is
// idempotent — Echo / Heartbeat / Boom calls don't accumulate state, so
// shared-fixture is safe.

let private withClient (test: HttpClient -> unit) : unit =
    use host = buildHost ()
    host.Start()
    use client = host.GetTestClient()
    test client

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
    ]