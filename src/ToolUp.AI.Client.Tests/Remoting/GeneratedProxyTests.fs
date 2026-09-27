// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.GeneratedProxyTests

// ─── Phase 853 — generated client proxies and encoders, transpiled ──────
//
// Run on the TRANSPILED client — the code a browser runs — against a
// scripted `fetch`:
//
//   * 853.D — the transpiled generated ENCODERS write text the Phase 841
//     decoders (the server's argument seam) read back to the value, and the
//     same text as the .NET host where the two format identically
//     (`ClientEncoderFixture`, compiled into both packs);
//   * 853.B — `Api.makeProxy` hands back the GENERATED proxy for a platform
//     record (its registry key is the Fable host's own spelling of the
//     record's full name), whose call sends the encoded argument array and
//     decodes the response through the generated decoder; a record with no
//     generated proxy is still served, reflectively, on its first call;
//   * the generated proxy composes with Phase 854's in-flight sharing (a
//     declared read registered FROM THE ATTRIBUTES by the generated
//     builder) and Phase 855's same-tick batching exactly as the reflective
//     proxy does.

open Fable.Core
open ToolUp.Platform
open ToolUp.Remoting
open ToolUp.Remoting.Json
open ToolUp.Remoting.Client
open ToolUp.Platform.Tests.Remoting
open ToolUp.AI.Client.Tests.NodeTest

/// A record no generator ran over: `Api.makeProxy` must still serve it.
type LazyProbeApi = {
    Get: int -> Async<int>
    Ping: unit -> Async<int>
}

// ─── fetch stub ─────────────────────────────────────────────────────
//
// Answers a request by the FIRST registered route suffix its URL (or a
// batch element's route) ends with; `200 "0"` otherwise. Records every
// request.

[<Emit("""(() => {
    const stub = { calls: [], answers: [], saved: globalThis.fetch };
    globalThis.__generatedFetch = stub;
    const lookup = (route) => {
        const hit = stub.answers.find((a) => route.endsWith(a.suffix));
        return hit ? { status: hit.status, body: hit.body } : { status: 200, body: '0' };
    };
    const respond = (r) => Promise.resolve({
        status: r.status,
        text: () => Promise.resolve(r.body),
        arrayBuffer: () => Promise.resolve(new TextEncoder().encode(r.body).buffer)
    });
    globalThis.fetch = (url, init) => {
        stub.calls.push({ url, init });
        if (url.endsWith('/_batch')) {
            const elements = JSON.parse(init.body);
            return respond({ status: 200, body: JSON.stringify(elements.map((e) => lookup(e.route))) });
        }
        return respond(lookup(url));
    };
})()""")>]
let private installFetchStub () : unit = jsNative

[<Emit("(globalThis.fetch = globalThis.__generatedFetch.saved, undefined)")>]
let private restoreFetch () : unit = jsNative

[<Emit("(globalThis.__generatedFetch.answers.push({ suffix: $0, status: $1, body: $2 }), undefined)")>]
let private answer (suffix: string) (status: int) (body: string) : unit = jsNative

[<Emit("globalThis.__generatedFetch.calls.length")>]
let private callCount () : int = jsNative

[<Emit("globalThis.__generatedFetch.calls[$0].url")>]
let private callUrl (i: int) : string = jsNative

[<Emit("globalThis.__generatedFetch.calls[$0].init.method")>]
let private callMethod (i: int) : string = jsNative

[<Emit("(globalThis.__generatedFetch.calls[$0].init.body == null ? 'none' : String(globalThis.__generatedFetch.calls[$0].init.body))")>]
let private callBody (i: int) : string = jsNative

type private Outcome<'T> =
    | Pending
    | Returned of 'T
    | Raised of exn

let private start (call: Async<'T>) : (unit -> Outcome<'T>) =
    let mutable outcome = Pending

    Async.StartImmediate(
        async {
            try
                let! value = call
                outcome <- Returned value
            with ex ->
                outcome <- Raised ex
        }
    )

    fun () -> outcome

let private same (actual: 'T) (expected: 'T) (message: string) : unit =
    Expect.isTrue (actual = expected) (sprintf "%s\n  actual:   %A\n  expected: %A" message actual expected)

let private returned (outcome: Outcome<'T>) : 'T option =
    match outcome with
    | Returned value -> Some value
    | _ -> None

/// Arrange the page for one case; the returned function puts it back.
let private arrange (batching: bool) : (unit -> unit) =
    let transport = Http.currentTransport ()
    Http.useTransport Http.Transport.Fetch
    installFetchStub ()

    if batching then
        RemoteBatching.enable RemoteBatching.DefaultRoute
    else
        RemoteBatching.disable ()

    fun () ->
        ReadPolicies.clear ()
        RemoteBatching.disable ()
        restoreFetch ()
        Http.useTransport transport

/// A deferred case whose page state is restored before its assertions run.
let private case (name: string) (batching: bool) (act: unit -> (unit -> unit)) =
    testCaseDeferred name 30 (fun () ->
        let restore = arrange batching
        let check = act ()

        fun () ->
            restore ()
            check ())

let tests =
    testList "Phase 853 — generated client proxies and encoders (transpiled)" [

        testList "853.D — the transpiled encoders write what the server reads" [
            testCase
                "every fixture case: the Fable encoder's text decodes to the value, and is the .NET text where the hosts agree"
            <| fun () ->
                Expect.isTrue (not (List.isEmpty ClientEncoderFixture.cases)) "the fixture is not vacuous"

                for c in ClientEncoderFixture.cases do
                    let text = c.Encode()

                    same
                        (c.DecodesToValue text)
                        (Ok())
                        (sprintf "%s: the Phase 841 decoder reads the Fable text\n%s" c.Name text)

                    if c.HostStable then
                        same text c.Pinned (sprintf "%s: the Fable text is the .NET text" c.Name)

            testCase "the writer's conventions, on the Fable host"
            <| fun () ->
                same (JsonEncode.toText (JsonEncode.int64 42L)) "\"+42\"" "int64 is the signed string"

                same
                    (JsonEncode.toText (JsonEncode.int64 -9007199254740993L))
                    "\"-9007199254740993\""
                    "past 2^53, exactly"

                same (JsonEncode.toText (JsonEncode.float 1.0)) "1.0" "an integral double keeps the writer's .0"
                same (JsonEncode.toText (JsonEncode.float 0.1)) "0.1" "the shortest round-trip text"
                same (JsonEncode.toText (JsonEncode.decimal 1.5M)) "1.5" "decimal as its digits"

                let instant = System.DateTime(2026, 9, 27, 10, 30, 0, 123, System.DateTimeKind.Utc)

                match JsonDecode.asDateTime (JsonEncode.dateTime instant) with
                | Ok read -> same (read.ToUniversalTime()) instant "a UTC DateTime reads back as the same instant"
                | Error e -> failwithf "the Fable DateTime text did not read: %s" (DecodeError.render e)
        ]

        testList "853.B — Api.makeProxy is generated-first" [

            testCase "a platform record's registry key is the Fable host's own spelling of its full name"
            <| fun () ->
                let key = GeneratedProxies.keyOf typeof<ColumnMappingApi.IConversionApi>

                Expect.isTrue
                    (PlatformClientProxies.coveredApiRecords |> List.contains key)
                    (sprintf "the generated module covers %s" key)

                same
                    (GeneratedProxies.keyOf typeof<ReadPolicyFixture.ReadCatalogApi>)
                    "ToolUp.Platform.Tests.Remoting.ReadPolicyFixture.ReadCatalogApi"
                    "a type nested in a module is spelled with dots, as the generator keys it"

            case
                "a generated call sends the encoded argument array and decodes through the generated decoder"
                false
                (fun () ->
                    answer "/DeleteConversion" 200 """{"Ok":null}"""
                    let api = Api.makeProxy<ColumnMappingApi.IConversionApi> ()
                    let outcome = start (api.DeleteConversion("fp-1", "sales"))

                    fun () ->
                        same (callCount ()) 1 "one request"
                        same (callUrl 0) "/api/IConversionApi/DeleteConversion" "the route makeProxy builds"
                        same (callMethod 0) "POST" "a call with an argument is a POST"
                        same (callBody 0) """[["fp-1","sales"]]""" "the argument array, the tuple as an array"
                        same (returned (outcome ())) (Some(Ok())) "decoded by the generated decoder")

            case "a parameterless generated call is a GET, its list response decoded" false (fun () ->
                answer "/ListConversions" 200 "[]"
                let api = Api.makeProxy<ColumnMappingApi.IConversionApi> ()
                let outcome = start (api.ListConversions())

                fun () ->
                    same (callMethod 0) "GET" "no arguments, no body"
                    same (callBody 0) "none" "nothing sent"
                    same (returned (outcome ())) (Some []) "an empty list")

            case "a response that does not decode is a named refusal on ProxyRequestException" false (fun () ->
                answer "/DeleteConversion" 200 """{"Ok":1}"""
                let api = Api.makeProxy<ColumnMappingApi.IConversionApi> ()
                let outcome = start (api.DeleteConversion("fp-1", "sales"))

                fun () ->
                    match outcome () with
                    | Raised(:? ProxyRequestException as error) ->
                        Expect.isTrue error.DecodeError.IsSome "the refusal is structured, not message text"
                    | other -> failwithf "expected a decode refusal, got %A" other)

            case "a server error is the ProxyRequestException it always was" false (fun () ->
                answer "/DeleteConversion" 500 "boom"
                let api = Api.makeProxy<ColumnMappingApi.IConversionApi> ()
                let outcome = start (api.DeleteConversion("fp-1", "sales"))

                fun () ->
                    match outcome () with
                    | Raised(:? ProxyRequestException as error) -> same error.StatusCode 500 "the status"
                    | other -> failwithf "expected a 500, got %A" other)

            case "a record with no generated proxy is still served — reflectively, on its first call" false (fun () ->
                answer "/Get" 200 "30"
                let api = Api.makeProxy<LazyProbeApi> ()
                let outcome = start (api.Get 3)

                fun () ->
                    same (callUrl 0) "/api/LazyProbeApi/Get" "the reflective proxy's route"
                    same (callBody 0) "[3]" "its argument array"
                    same (returned (outcome ())) (Some 30) "decoded by the reflective path")
        ]

        testList "853.B — generated proxies compose with 854 and 855" [

            case
                "854: a declared read — registered from the attributes by the generated builder — shares one in-flight request"
                false
                (fun () ->
                    answer "/GetCount" 200 "7"
                    let api = ReadCatalogClientProxies.readCatalogApiProxy (Remoting.createApi ())
                    let first = start (api.GetCount "x")
                    let second = start (api.GetCount "x")

                    fun () ->
                        same (callCount ()) 1 "two identical in-flight reads are one request"
                        same (returned (first ())) (Some 7) "the first caller"
                        same (returned (second ())) (Some 7) "the second caller shares it")

            case "855: two same-tick generated calls travel as one envelope" true (fun () ->
                answer "/DeleteConversion" 200 """{"Ok":null}"""
                let api = Api.makeProxy<ColumnMappingApi.IConversionApi> ()
                let first = start (api.DeleteConversion("a", "b"))
                let second = start (api.DeleteConversion("c", "d"))

                fun () ->
                    same (callCount ()) 1 "one envelope"
                    Expect.isTrue ((callUrl 0).EndsWith "/_batch") "sent to the batch route"
                    same (returned (first ())) (Some(Ok())) "the first element"
                    same (returned (second ())) (Some(Ok())) "the second element")
        ]
    ]