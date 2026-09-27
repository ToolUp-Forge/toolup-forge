// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.JsonClientDecodeTests

// ─── Phase 843 — the client's JSON response decode joins the algebra ─────
//
// The browser half of the phase, run on the TRANSPILED client:
//
//   * 843.C — the pinned payload in `JsonClientDecodeFixture` (compiled
//     into the .NET pack too, which holds the writer to it and
//     System.Text.Json to the same model) reads here to the same model
//     and decodes to the same value, and every pinned agreement case
//     reads as the .NET pack asserts it does.
//   * 843.D — through the real proxy (`Remoting.buildProxy`, a scripted
//     XMLHttpRequest): a registered return type decodes through the
//     algebra to the declared value; a malformed body is a named refusal
//     on `ProxyRequestException.DecodeError`, not an exception out of a
//     parser; an unregistered type takes the reflection path exactly as
//     before; and the losses Phase 785.F named are shown closed on the
//     client, beside a measurement of what the reflection path does with
//     the same text.
//
// The XMLHttpRequest stub is installed on first use and serves one
// scripted status + body per request; Node has no XMLHttpRequest of its
// own, and no other pack in this harness installs one.

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.SimpleJson
open ToolUp.Remoting
open ToolUp.Remoting.Json
open ToolUp.Remoting.Client
open ToolUp.Platform.Tests.Remoting.JsonClientDecodeFixture
open ToolUp.AI.Client.Tests.NodeTest

// ─── XMLHttpRequest stub ────────────────────────────────────────────

[<Emit("""(() => {
    if (globalThis.__xhrStub) return;
    const stub = { calls: [], status: 200, body: '' };
    globalThis.__xhrStub = stub;
    globalThis.XMLHttpRequest = class {
        constructor() { this.readyState = 0; this.status = 0; this.responseText = ''; this.headers = {}; }
        open(method, url) { this.method = method; this.url = url; }
        setRequestHeader(key, value) { this.headers[key] = value; }
        abort() { }
        send(body) {
            stub.calls.push({ method: this.method, url: this.url, body });
            this.status = stub.status;
            this.responseText = stub.body;
            this.readyState = 4;
            queueMicrotask(() => { if (this.onreadystatechange) this.onreadystatechange(); });
        }
    };
})()""")>]
let private installXhrStub () : unit = jsNative

[<Emit("(globalThis.__xhrStub.status = $0, globalThis.__xhrStub.body = $1, undefined)")>]
let private scriptResponse (status: int) (body: string) : unit = jsNative

[<Emit("globalThis.__xhrStub.calls.length")>]
let private callCount () : int = jsNative

[<Emit("globalThis.__xhrStub.calls[$0].url")>]
let private callUrl (i: int) : string = jsNative

// ─── The API under test ─────────────────────────────────────────────

/// `LedgerLine`'s twin, never registered — the reflection path's subject.
type LedgerLineViaReflection = {
    Id: int64
    Amount: decimal
    Tiny: decimal
    Elapsed: TimeSpan
    Label: string
    Note: string option
    Tags: string list
}

type LedgerApi = {
    GetLine: unit -> Async<LedgerLine>
    GetLineViaReflection: unit -> Async<LedgerLineViaReflection>
}

let private api: LedgerApi = Remoting.createApi () |> Remoting.buildProxy<LedgerApi>

/// What the reflection path makes of `text` at `'T` — the pre-843
/// response decode, called directly.
let inline private viaReflection<'T> (text: string) : 'T =
    Convert.fromJsonAs (SimpleJson.parseNative text) (createTypeInfo typeof<'T>) :?> 'T

/// Run a proxy call and capture how it ended.
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

/// F# structural equality, with both sides rendered on failure — the
/// node assert's deep equality would compare a decimal's or an int64's
/// runtime representation rather than its value.
let private same (actual: 'T) (expected: 'T) (message: string) : unit =
    Expect.isTrue (actual = expected) (sprintf "%s\n  actual:   %A\n  expected: %A" message actual expected)

let private int64Text (n: int64) = n.ToString()
let private decimalText (d: decimal) = d.ToString()

let tests =
    testList "Phase 843 — the client's JSON response decode" [

        testList "843.C — the pinned payload, read in the browser" [
            testCase "JsonText reads the pinned text to the model the .NET pack pins"
            <| fun () -> same (JsonText.tryParse SamplePayload) (Ok sampleModel) "the pinned model"

            testCase "the decoder reads it to the sample, every digit intact"
            <| fun () ->
                match JsonText.tryParse SamplePayload |> Result.bind decoder with
                | Ok line ->
                    same (int64Text line.Id) "9007199254740993" "Id"
                    same (decimalText line.Amount) "79228162514264337593543950335" "Amount"
                    same (decimalText line.Tiny) "0.0000000000000000000000000001" "Tiny"
                    // The tick is the one field compared as a VALUE of this
                    // host's TimeSpan, not as the .NET tick count: see the
                    // TimeSpan case in the 785.F list below.
                    same line.Elapsed sample.Elapsed "Elapsed, at this host's resolution"
                    same line sample "the whole record"
                | Error e -> failwithf "refused: %s" (DecodeError.render e)

            testCase "every pinned agreement case reads here as the .NET pack asserts it"
            <| fun () ->
                Expect.isTrue (List.length agreementCases > 20) "the agreement list is close to empty"

                for name, text, expected in agreementCases do
                    let actual =
                        match JsonText.tryParse text with
                        | Ok value -> Some value
                        | Error _ -> None

                    same actual expected name
        ]

        testList "843.A — the recorded answer, pinned where it can go red" [
            testCase "JSON.parse's object model loses the two facts JsonText keeps"
            <| fun () ->
                // The reason a `JSON.parse` reviver cannot carry the model:
                // by the time a reviver sees an object, the engine has
                // moved its array-index keys first and collapsed its
                // duplicates. If an engine ever stops doing either, this
                // case goes red and 843.A's answer is worth revisiting.
                let keys: string[] =
                    emitJsExpr () "Object.keys(JSON.parse('{\"b\":1,\"10\":2,\"2\":3}'))"

                same keys [| "2"; "10"; "b" |] "JSON.parse reorders array-index keys"
                let dup: int = emitJsExpr () "JSON.parse('{\"Ok\":1,\"Ok\":2}').Ok"
                same dup 2 "JSON.parse keeps the LAST duplicate"

                same
                    (JsonText.tryParse """{"b":1,"10":2,"2":3}"""
                     |> Result.map (function
                         | JsonValue.Object members -> List.map fst members
                         | _ -> []))
                    (Ok [ "b"; "10"; "2" ])
                    "JsonText keeps wire order"

                same
                    (JsonText.tryParse """{"Ok":1,"Ok":2}"""
                     |> Result.bind (JsonDecode.result JsonDecode.asInt32 JsonDecode.asString))
                    (Error(
                        DecodeError.create
                            "Result (a case name, or a one-member object {\"Case\": payload})"
                            "object of 2 member(s)"
                    ))
                    "a duplicate stays visible, so the union decoder refuses it as the server does"
        ]

        testList "843.D — through the proxy" [
            testCaseDeferred "a registered return type decodes through the algebra to the declared value" 30 (fun () ->
                installXhrStub ()
                JsonDecoders.register<LedgerLine> decoder
                scriptResponse 200 SamplePayload
                let before = callCount ()
                let outcome = start (api.GetLine())

                fun () ->
                    JsonDecoders.resetForTests ()
                    same (callCount ()) (before + 1) "one request"
                    same (callUrl before) "/LedgerApi/GetLine" "the ordinary route"

                    match outcome () with
                    | Returned line ->
                        same line sample "the declared value"
                        same (int64Text line.Id) "9007199254740993" "2^53 + 1 survives"
                        same line.Elapsed sample.Elapsed "Elapsed, at this host's resolution"
                    | Raised ex -> failwithf "the call raised: %s" ex.Message
                    | Pending -> failwith "the call never completed")

            testCaseDeferred
                "a malformed body is a named refusal on ProxyRequestException, not a parser exception"
                30
                (fun () ->
                    installXhrStub ()
                    JsonDecoders.register<LedgerLine> decoder
                    scriptResponse 200 """{"Id":"+1","Amount":"""
                    let truncated = start (api.GetLine())

                    fun () ->
                        JsonDecoders.resetForTests ()

                        match truncated () with
                        | Raised(:? ProxyRequestException as ex) ->
                            same ex.StatusCode 200 "the status is the response's"

                            match ex.DecodeError with
                            | Some error ->
                                same error.Expected "a JSON document" "a syntax refusal"

                                Expect.isTrue
                                    (error.Found.Contains "offset 20")
                                    (sprintf "names where: %s" error.Found)
                            | None -> failwith "DecodeError was None: the refusal was not carried as data"
                        | Raised ex -> failwithf "raised `%s`, not ProxyRequestException" ex.Message
                        | Returned line -> failwithf "a truncated body decoded: %A" line
                        | Pending -> failwith "the call never completed")

            testCaseDeferred "a well-formed body of the wrong shape is refused at its path" 30 (fun () ->
                installXhrStub ()
                JsonDecoders.register<LedgerLine> decoder
                scriptResponse 200 (SamplePayload.Replace("\"Tags\":[\"a\",\"b\"]", "\"Tags\":[\"a\",2]"))
                let wrong = start (api.GetLine())

                fun () ->
                    JsonDecoders.resetForTests ()

                    match wrong () with
                    | Raised(:? ProxyRequestException as ex) ->
                        match ex.DecodeError with
                        | Some error ->
                            same error.Path [ "Tags"; "[1]" ] "the path to the offending element"
                            same error.Found "number 2" "what was there"
                        | None -> failwith "DecodeError was None"
                    | other -> failwithf "expected a named refusal, got %A" other)

            testCaseDeferred "an unregistered return type takes the reflection path, exactly as before" 30 (fun () ->
                installXhrStub ()
                JsonDecoders.resetForTests ()
                scriptResponse 200 SamplePayload
                let outcome = start (api.GetLineViaReflection())

                fun () ->
                    Expect.isFalse
                        (JsonDecoders.isRegistered typeof<LedgerLineViaReflection>)
                        "the twin type must be unregistered for this case to mean anything"

                    match outcome () with
                    | Returned line ->
                        same
                            line
                            (viaReflection<LedgerLineViaReflection> SamplePayload)
                            "what the pre-843 decode makes of it"
                    | Raised ex -> failwithf "the call raised: %s" ex.Message
                    | Pending -> failwith "the call never completed")
        ]

        testList "843.D — the 785.F losses, on the client" [
            // The measurement that says the phase did its job: each value a
            // `float` carrier loses, decoded through the algebra (exact)
            // and through the reflection path (measured and recorded).
            // The writer emits `int64` as a signed STRING, which the
            // reflection path already reads exactly; a number token is what
            // any other writer sends, and is the int64 case a float loses.
            testCase "int64 past 2^53 as a number token: exact through the algebra"
            <| fun () ->
                let text = "9007199254740993"
                same (JsonText.tryParse text |> Result.bind JsonDecode.asInt64) (Ok 9007199254740993L) "algebra"
                let reflected = viaReflection<int64> text
                printfn "785.F on the client — int64 number token via reflection: %s" (int64Text reflected)

                Expect.isTrue
                    (reflected <> 9007199254740993L)
                    "the reflection path read 2^53 + 1 exactly — this case no longer measures a loss"

            testCase "decimal at full precision: exact through the algebra"
            <| fun () ->
                let text = "79228162514264337593543950335"

                same
                    (JsonText.tryParse text |> Result.bind JsonDecode.asDecimal)
                    (Ok 79228162514264337593543950335M)
                    "algebra"

                let reflected = viaReflection<decimal> text
                printfn "785.F on the client — Decimal.MaxValue via reflection: %s" (decimalText reflected)

                Expect.isTrue
                    (reflected <> 79228162514264337593543950335M)
                    "the reflection path read Decimal.MaxValue exactly — this case no longer measures a loss"

            // The third loss is NOT closable here, and the reason is the
            // host's type rather than the decode: Fable's TimeSpan is a
            // count of milliseconds and `TimeSpan.FromTicks` divides the
            // ticks down to WHOLE milliseconds, so the declared value
            // itself holds no sub-millisecond tick on this host. The
            // algebra reads the writer's text to exactly the value this
            // host can represent — the same one the reflection path
            // reaches. The resolution is pinned so that a Fable TimeSpan
            // that one day carries ticks turns this case red, and the
            // client's tick can then be claimed and measured.
            testCase "the TimeSpan tick: bounded by this host's TimeSpan, which the algebra reaches exactly"
            <| fun () ->
                let text = "51211215.8396"
                let declared = TimeSpan.FromTicks 512112158396L

                same
                    declared.Ticks
                    512112150000L
                    "Fable's TimeSpan.FromTicks no longer truncates to whole milliseconds; re-measure the client's tick"

                same (JsonText.tryParse text |> Result.bind JsonDecode.asTimeSpan) (Ok declared) "algebra"
                let reflected = viaReflection<TimeSpan> text
                same reflected declared "reflection reaches the same host value"

                printfn
                    "785.F on the client — TimeSpan: host resolution is whole milliseconds; algebra and reflection both %d ticks (.NET: 512112158396)"
                    reflected.Ticks
        ]
    ]