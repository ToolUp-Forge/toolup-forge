// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.SchemaVersionTests

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Text
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe

// ─── Phase 69j — schema-versioned wire envelopes ─────────────────────
//
// 69j.A shipped the envelope field. This pack covers the negotiation half
// and is written against the REAL dispatcher on a TestServer rather than
// against `SchemaVersion.negotiate` alone, because every claim here is
// about what a CLIENT sees: which handler ran, which status came back, and
// what bytes the envelope carried. A unit test over the decision function
// would assert the DU case and prove nothing about the response — and the
// decision function is exercised anyway, by every one of these going
// through it.
//
// Three assertions in here are byte pins rather than substring probes
// (69j.H): the success body of a routed call, and the exact JSON of the
// refusal envelope. The Phase 784 wire corpus is not their home — it pins
// VALUE shapes, is dual-compiled into a Fable project that cannot see
// `ToolUp.Remoting.Server`, and holds no response envelope at all. A pin
// of the dispatcher's envelope belongs beside the dispatcher's own pack.

// ─── Fixtures ────────────────────────────────────────────────────────

/// The whole surface as it stands today: no suffix, no attribute. Serves
/// the composed default version and nothing else.
type private PlainApi = {
    [<AllowAnonymous>]
    Echo: string -> Async<string>
}

/// Two handlers, one logical method. The `_V<n>` suffix alone declares the
/// versions — no attribute needed for the simple case.
type private VersionedApi = {
    [<AllowAnonymous>]
    Shape_V1: string -> Async<string>
    [<AllowAnonymous>]
    Shape_V2: string -> Async<string>
}

/// Version 1 is on a published retirement timeline; version 2 is not.
type private DeprecatedApi = {
    [<AllowAnonymous>]
    [<DeprecatedSchema "2027-01-01">]
    Legacy_V1: string -> Async<string>
    [<AllowAnonymous>]
    Legacy_V2: string -> Async<string>
}

/// One handler serving two versions, declared by attribute rather than by
/// suffix — the "version 2 changed nothing for this method" shape.
type private WideApi = {
    [<AllowAnonymous>]
    [<SupportsSchema [| 1; 2 |]>]
    Wide_V1: string -> Async<string>
}

// Compose-time refusal fixtures. None of these is ever hosted; the
// classifier refuses them.

type private ClashApi = {
    Thing_V1: string -> Async<string>
    [<SupportsSchema 1>]
    Thing_V2: string -> Async<string>
}

type private BadDateApi = {
    [<DeprecatedSchema "not-a-date">]
    Thing_V1: string -> Async<string>
}

type private BadVersionApi = {
    [<SupportsSchema 0>]
    Thing: string -> Async<string>
}

type private StreamingVersionedApi = {
    [<AllowAnonymous>]
    Ticks_V1: int -> IAsyncEnumerable<int>
}

// ─── Harness ─────────────────────────────────────────────────────────

let private buildHost (h: HttpHandler) : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost.UseTestServer().Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe h)
            |> ignore)
        .Build()

/// Issue one POST at `route`, optionally carrying `X-Remoting-Schema`.
/// A fresh host per call, so "which handler ran" is a claim about THIS
/// call and not about whatever a sibling test left behind.
let private call (handler: HttpHandler) (route: string) (schemaHeader: string option) (body: string) = async {
    use host = buildHost handler
    do! host.StartAsync() |> Async.AwaitTask
    use client = host.GetTestClient()
    use req = new HttpRequestMessage(HttpMethod.Post, route)
    req.Content <- new StringContent(body, Encoding.UTF8, "application/json")

    match schemaHeader with
    | Some value -> req.Headers.TryAddWithoutValidation("X-Remoting-Schema", value) |> ignore
    | None -> ()

    let! resp = client.SendAsync req |> Async.AwaitTask
    let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
    do! host.StopAsync() |> Async.AwaitTask
    return int resp.StatusCode, text
}

let private plainHandler () =
    let impl: PlainApi = {
        Echo = fun s -> async { return "plain:" + s }
    }

    Remoting.createApi () |> Remoting.fromValue impl |> Remoting.buildHttpHandler

let private versionedHandler () =
    let impl: VersionedApi = {
        Shape_V1 = fun s -> async { return "v1:" + s }
        Shape_V2 = fun s -> async { return "v2:" + s }
    }

    Remoting.createApi () |> Remoting.fromValue impl |> Remoting.buildHttpHandler

let private wideHandler () =
    let impl: WideApi = {
        Wide_V1 = fun s -> async { return "wide:" + s }
    }

    Remoting.createApi () |> Remoting.fromValue impl |> Remoting.buildHttpHandler

/// The deprecated-version host, with a capturing diagnostics logger — the
/// only operator-facing log seam `RemotingOptions` carries.
let private deprecatedHandlerWithLog () =
    let lines = ResizeArray<string>()

    let impl: DeprecatedApi = {
        Legacy_V1 = fun s -> async { return "legacy1:" + s }
        Legacy_V2 = fun s -> async { return "legacy2:" + s }
    }

    let handler =
        Remoting.createApi ()
        |> Remoting.fromValue impl
        |> Remoting.withDiagnosticsLogger (fun line -> lock lines (fun () -> lines.Add line))
        |> Remoting.buildHttpHandler

    handler, lines

/// Run `f`, and return the message of whatever it raised. Fails the test
/// when nothing was raised — the `try/with` shape is deliberate over
/// `Expect.throwsT`, which returns unit here and so cannot hand the
/// message back for the assertions that follow.
let private refusalMessage (label: string) (f: unit -> unit) : string =
    let captured =
        try
            f ()
            None
        with ex ->
            Some ex.Message

    match captured with
    | Some message -> message
    | None -> failtestf "%s — expected a compose-time refusal, but nothing was raised" label

let private sourceFile (relative: string list) =
    let assemblyDir =
        Path.GetDirectoryName(Reflection.Assembly.GetExecutingAssembly().Location)

    // …/src/ToolUp.Platform.Tests/bin/Debug/net10.0 → repo root
    let repoRoot =
        Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

    Path.Combine(repoRoot :: relative |> List.toArray)

let tests =
    testList "Phase 69j — schema-versioned wire envelopes" [

        // ── 69j.G — the implicit version-1 envelope ───────────────────

        testAsync "no header on an ordinary method: the call succeeds unchanged" {
            let! status, text = call (plainHandler ()) "/PlainApi/Echo" None "[\"hi\"]"

            Expect.equal status 200 "an unversioned method with no header is the pre-69j path"
            Expect.equal text "\"plain:hi\"" "the success body is byte-identical to the pre-69j wire shape"
        }

        testAsync "the dispatcher's error envelope carries __schema_version 1 by default" {
            // A malformed body takes Phase 783's decode-refusal path, which
            // emits the categorised envelope. This is where 69j.A's field
            // shows up on the wire, and the claim of 69j.G is that it is
            // there by default with no composition at all.
            let! status, text = call (plainHandler ()) "/PlainApi/Echo" None "{not-an-array}"

            Expect.equal status 400 "a decode failure is Phase 783's named refusal"
            Expect.stringContains text "\"__schema_version\":1" "every dispatcher-emitted envelope carries the field"
        }

        // ── 69j.B — the header, and the refusal ───────────────────────

        testAsync "X-Remoting-Schema: 1 on an ordinary method is served" {
            let! status, text = call (plainHandler ()) "/PlainApi/Echo" (Some "1") "[\"hi\"]"

            Expect.equal status 200 "version 1 is what an unversioned method serves"
            Expect.equal text "\"plain:hi\"" "and it serves it byte-identically"
        }

        testAsync "an unsupported version is refused, naming what IS supported" {
            let! status, text = call (plainHandler ()) "/PlainApi/Echo" (Some "99") "[\"hi\"]"

            Expect.equal status 400 "an unsupported schema version is a caller-side refusal, not a 500"
            // The byte pin of the refusal envelope — the shape 69j adds to
            // the wire. Field ORDER is part of the pin, and inside `error`
            // it is ALPHABETICAL rather than the order the payload is
            // written in: F# sorts an anonymous record's fields by name at
            // the type level, so `message` precedes `methodName` precedes
            // `schemaVersion` whatever the construction site says. Renaming
            // a payload field therefore re-orders the wire — which is
            // exactly the class of change this pin exists to catch.
            Expect.equal
                text
                "{\"error\":{\"message\":\"Unsupported wire-schema version '99' for method 'Echo'. Supported: [1].\",\"methodName\":\"Echo\",\"schemaVersion\":{\"requested\":\"99\",\"supported\":[1]}},\"ignored\":false,\"handled\":true,\"category\":\"user\",\"__schema_version\":1}"
                "the refusal envelope's exact wire bytes"
        }

        testAsync "a malformed header is refused rather than coerced to a default" {
            let! status, text = call (plainHandler ()) "/PlainApi/Echo" (Some "two") "[\"hi\"]"

            Expect.equal status 400 "a caller that asked for SOMETHING and got it wrong is refused"
            Expect.stringContains text "\"requested\":\"two\"" "the refusal echoes what was sent, not a parsed value"
        }

        testAsync "the refusal never reaches the handler" {
            // The handler here returns a value no assertion below expects;
            // what is asserted is that the response is the refusal envelope,
            // which the handler cannot produce.
            let! _, text = call (plainHandler ()) "/PlainApi/Echo" (Some "99") "[\"hi\"]"
            Expect.isFalse (text.Contains "plain:hi") "the handler did not run"
        }

        testAsync "a header for ANOTHER API's version falls through, it is not refused" {
            // Several dispatch handlers under one `choose`, tried in order.
            // `PlainApi` is first and serves version 1 only; `VersionedApi`
            // is second and serves 1 and 2. A caller pinning version 2 for
            // `Shape` must reach the second handler — if the first refused
            // on a miss, whichever API was composed first would 400 every
            // other API's calls. This is the Phase 69d auth-pre-flight
            // defect in a new family, and it is why `negotiate` takes the
            // record's own addressable-name set.
            let handler = choose [ plainHandler (); versionedHandler () ]
            let! status, text = call handler "/VersionedApi/Shape" (Some "2") "[\"x\"]"

            Expect.equal status 200 "the first handler fell through instead of refusing"
            Expect.equal text "\"v2:x\"" "…and the second served the pinned version"
        }

        testAsync "a route belonging to no composed API is still a fall-through" {
            let handler = choose [ plainHandler (); versionedHandler () ]
            let! status, _ = call handler "/NoSuchApi/Nothing" (Some "99") "[]"

            Expect.notEqual status 400 "an unknown route is not this dispatcher's schema refusal"
        }

        // ── 69j.C — per-method versioned dispatch ─────────────────────

        testAsync "a multi-version method dispatches to the handler for the requested version" {
            let handler = versionedHandler ()
            let! s1, t1 = call handler "/VersionedApi/Shape" (Some "1") "[\"x\"]"
            let! s2, t2 = call handler "/VersionedApi/Shape" (Some "2") "[\"x\"]"

            Expect.equal s1 200 "version 1 is served"
            Expect.equal t1 "\"v1:x\"" "…by the _V1 handler"
            Expect.equal s2 200 "version 2 is served"
            Expect.equal t2 "\"v2:x\"" "…by the _V2 handler"
        }

        testAsync "no header on a multi-version method serves the highest supported version" {
            let! status, text = call (versionedHandler ()) "/VersionedApi/Shape" None "[\"x\"]"

            Expect.equal status 200 "an unpinned caller is served"
            Expect.equal text "\"v2:x\"" "…the highest version, per the 69j.C default"
        }

        testAsync "a version no handler serves is refused with the method's own vector" {
            let! status, text = call (versionedHandler ()) "/VersionedApi/Shape" (Some "3") "[\"x\"]"

            Expect.equal status 400 "version 3 has no handler"
            Expect.stringContains text "\"supported\":[1,2]" "the vector is the METHOD's, not the server's default"
        }

        testAsync "a versioned handler addressed directly is served as itself" {
            // A `_V<n>` field is an ordinary record field, so its own route
            // exists and dispatches with the full pre-flight chain — the
            // attributes enforced are that field's. It is deliberately NOT
            // the published surface (the docs export lists the logical name
            // only); this pins the behaviour rather than leaving it to be
            // discovered.
            let! status, text = call (versionedHandler ()) "/VersionedApi/Shape_V1" None "[\"x\"]"

            Expect.equal status 200 "the handler's own route dispatches"
            Expect.equal text "\"v1:x\"" "…to itself, without negotiation"
        }

        testAsync "one handler can declare several versions by attribute" {
            let handler = wideHandler ()
            let! s1, t1 = call handler "/WideApi/Wide" (Some "1") "[\"x\"]"
            let! s2, t2 = call handler "/WideApi/Wide" (Some "2") "[\"x\"]"

            Expect.equal s1 200 "version 1 is served"
            Expect.equal s2 200 "version 2 is served"
            Expect.equal t1 "\"wide:x\"" "…by the same handler"
            Expect.equal t2 "\"wide:x\"" "…in both directions"
        }

        // ── 69j.E — the deprecation warning ───────────────────────────

        testAsync "serving a deprecated version logs one operator warning" {
            let handler, lines = deprecatedHandlerWithLog ()
            let! status, _ = call handler "/DeprecatedApi/Legacy" (Some "1") "[\"x\"]"

            Expect.equal status 200 "a deprecated version is still SERVED — deprecation is not refusal"

            let warnings = lines |> Seq.filter (fun l -> l.Contains "DEPRECATED") |> Seq.toList

            Expect.hasLength warnings 1 "exactly one deprecation warning per call"
            Expect.stringContains warnings[0] "Legacy" "the warning names the method"
            Expect.stringContains warnings[0] "2027-01-01" "…and the published retirement date"
        }

        testAsync "serving a supported non-deprecated version logs no warning" {
            let handler, lines = deprecatedHandlerWithLog ()
            let! status, _ = call handler "/DeprecatedApi/Legacy" (Some "2") "[\"x\"]"

            Expect.equal status 200 "version 2 is served"

            Expect.isEmpty (lines |> Seq.filter (fun l -> l.Contains "DEPRECATED")) "an undeprecated version is silent"
        }

        // ── The classifier, and its compose-time refusals ─────────────

        test "an unversioned record classifies to an empty table" {
            Expect.isTrue
                (Map.isEmpty (SchemaVersion.classify typeof<PlainApi>))
                "no opt-in means no entry, so the per-request lookup is a fast miss"
        }

        test "two handlers claiming one version refuse at compose time" {
            let message =
                refusalMessage "a clash is refused" (fun () -> SchemaVersion.classify typeof<ClashApi> |> ignore)

            Expect.stringContains message "two handlers" "the refusal says what is wrong"
            Expect.stringContains message "Thing" "…and names the method"
        }

        test "an unparseable retirement date refuses at compose time" {
            let message =
                refusalMessage "a bad date is refused" (fun () -> SchemaVersion.classify typeof<BadDateApi> |> ignore)

            Expect.stringContains message "ISO-8601" "the refusal says what shape is wanted"
        }

        test "a non-positive declared version refuses at compose time" {
            let message =
                refusalMessage "version 0 is refused" (fun () -> SchemaVersion.classify typeof<BadVersionApi> |> ignore)

            Expect.stringContains message "positive integers" "the refusal says why"
        }

        test "a streaming method carrying versioning refuses at compose time" {
            let impl: StreamingVersionedApi = {
                Ticks_V1 =
                    fun _ ->
                        { new IAsyncEnumerable<int> with
                            member _.GetAsyncEnumerator _ = failwith "never enumerated"
                        }
            }

            let message =
                refusalMessage "the SSE path cannot honour a negotiated version" (fun () ->
                    Remoting.createApi ()
                    |> Remoting.fromValue impl
                    |> Remoting.buildHttpHandler
                    |> ignore)

            Expect.stringContains message "does not honour" "the refusal says the annotation would be ignored"
        }

        test "the AspNetCore adapter refuses a schema-versioned record rather than ignoring it" {
            // The parity posture this adapter already takes to every other
            // Phase 69b–69k seam. It reads no header, so a record that
            // declares versions would dispatch by the addressed name
            // whatever the caller asked for.
            let impl: VersionedApi = {
                Shape_V1 = fun s -> async { return s }
                Shape_V2 = fun s -> async { return s }
            }

            let message =
                refusalMessage "an annotation this adapter cannot honour is refused, not dropped" (fun () ->
                    Remoting.createApi ()
                    |> Remoting.fromValue impl
                    |> ToolUp.Remoting.AspNetCore.Middleware.buildFromImplementation (fun _ -> impl)
                    |> ignore)

            Expect.stringContains message "SupportsSchema" "the refusal names the annotation"
        }

        // ── 69j.D — the deploy-time discovery vector ──────────────────

        test "describe reports the whole surface, versioned methods under their logical name" {
            let described = SchemaVersion.describe 1 typeof<VersionedApi>

            Expect.equal described [ "Shape", [ 1; 2 ] ] "one entry, logical name, both versions"

            Expect.equal
                (SchemaVersion.describe 1 typeof<PlainApi>)
                [ "Echo", [ 1 ] ]
                "an unversioned method reports the composed default"
        }

        test "the docs schema publishes the per-method version vector" {
            let docs = Documentation("Versioned API", [])

            let schema =
                Docs.makeDocsSchemaWithSchemaVersions typeof<VersionedApi> docs (sprintf "/%s/%s") 1

            let json = schema.ToJsonString()

            Expect.stringContains json "\"schemaVersions\":[1,2]" "the vector is published per route"
            Expect.stringContains json "\"remoteFunction\":\"Shape\"" "under the logical name"

            Expect.isFalse
                (json.Contains "Shape_V1")
                "the _V<n> handler fields are not the published surface — the logical route is"
        }

        test "the docs schema is additive for an unversioned record" {
            let docs = Documentation("Plain API", [])

            let json =
                (Docs.makeDocsSchema typeof<PlainApi> docs (sprintf "/%s/%s")).ToJsonString()

            Expect.stringContains json "\"remoteFunction\":\"Echo\"" "the pre-69j keys are unchanged"
            Expect.stringContains json "\"route\":\"/PlainApi/Echo\"" "…including the route"
            Expect.stringContains json "\"schemaVersions\":[1]" "and the new key is purely additive"
        }

        // ── 69j.B, client half ────────────────────────────────────────

        test "the client proxy carries an opt-in withSchemaVersion knob" {
            // `Client/Remoting/Remoting.fs` is the Fable-compiled client
            // tier; this pack is the .NET Expecto runner and does not
            // reference it (no ProjectReference — see the test fsproj), so
            // the contract is pinned textually, the same way Phase 64 pins
            // `UserSession.withRequestHeaders`. What is pinned is the two
            // properties that matter: the knob exists, and it is NOT
            // applied by default anywhere in `createApi`.
            let path =
                sourceFile [ "src"; "ToolUp.Platform.Client"; "Client"; "Remoting"; "Remoting.fs" ]

            Expect.isTrue (File.Exists path) (sprintf "expected the client Remoting module at %s" path)
            let contents = File.ReadAllText path

            Expect.stringContains
                contents
                "let withSchemaVersion (version: int) (options: RemoteBuilderOptions)"
                "the opt-in knob is present"

            Expect.stringContains contents "\"X-Remoting-Schema\", string version" "…and it sends the header"

            let createApiBody =
                let start = contents.IndexOf "let createApi () = {"
                Expect.isGreaterThan start -1 "createApi is where a default would have to live"
                contents.Substring(start, contents.IndexOf("}", start) - start)

            Expect.isFalse
                (createApiBody.Contains "X-Remoting-Schema")
                "sending the header by default would change every existing deployment's request bytes (GP 11)"
        }
    ]