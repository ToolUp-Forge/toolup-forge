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
open ToolUp.Platform.Tests.Remoting
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
let private installXhrStubJs () : unit = jsNative

// Phase 855 - these cases script XMLHttpRequest, so they select the XHR
// transport (`fetch` is the default).
let private installXhrStub () : unit =
    Http.useTransport Http.Transport.Xhr
    installXhrStubJs ()

[<Emit("(globalThis.__xhrStub.status = $0, globalThis.__xhrStub.body = $1, undefined)")>]
let private scriptResponse (status: int) (body: string) : unit = jsNative

[<Emit("globalThis.__xhrStub.calls.length")>]
let private callCount () : int = jsNative

[<Emit("globalThis.__xhrStub.calls[$0].url")>]
let private callUrl (i: int) : string = jsNative

[<Emit("globalThis.__xhrStub.calls[$0].body")>]
let private callBody (i: int) : string = jsNative

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

/// Phase 885 — the consumer API of the acceptance case, through the
/// REFLECTIVE proxy (its arguments written by `Fable.SimpleJson`).
let private bookingApi: BrowserWriterFixture.BookingApi =
    Remoting.createApi () |> Remoting.buildProxy<BrowserWriterFixture.BookingApi>

/// Phase 899 — the union-keyed map argument, through the REFLECTIVE
/// proxy (its argument written by `Fable.SimpleJson`).
let private tallyApi: BrowserWriterFixture.TallyApi =
    Remoting.createApi () |> Remoting.buildProxy<BrowserWriterFixture.TallyApi>

/// Phase 911 — a record whose every integer field is 64 bits wide, at
/// each position the reflective read descends through. Never registered.
type WideReading = {
    Count: int64
    Size: uint64
    Maybe: int64 option
    Series: int64 list
    Pair: int64 * string
}

/// Phase 911 — int64 / uint64 responses the reflective proxy reads.
type WideApi = {
    GetCount: unit -> Async<int64>
    GetSize: unit -> Async<uint64>
    GetReading: unit -> Async<WideReading>
}

let private wideApi: WideApi = Remoting.createApi () |> Remoting.buildProxy<WideApi>

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
                         | JsonValue.Object members -> members |> Array.map fst |> List.ofArray
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
                        (JsonDecoders.isRegistered None typeof<LedgerLineViaReflection>)
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

        // ─── Phase 885 — the browser's writer, and what the client reads ──
        //
        // 885.A: the transpiled `Convert.serialize` — the writer every
        // reflective proxy sends arguments with — held to the texts in
        // `BrowserWriterFixture`, which the .NET pack holds the gate's
        // mirror (`BrowserJsonWriter`) to. 885.C: the three decisions, on
        // the client. The acceptance case: a consumer argument carrying a
        // decimal, a TimeSpan and a DateTime, captured off the wire from
        // both proxy kinds, which the .NET pack decodes on the server.
        testList "Phase 885 — the browser's writer, pinned; the forms and kinds the client reads" [
            testCase "885.A — Fable.SimpleJson writes every pinned case exactly as the fixture says"
            <| fun () ->
                Expect.isTrue (List.length BrowserWriterFixture.cases > 40) "the fixture is close to empty"

                let mismatches =
                    BrowserWriterFixture.cases
                    |> List.choose (fun c ->
                        let written =
                            try
                                Convert.serialize c.Value (createTypeInfo c.ValueType)
                            with ex ->
                                "THREW " + ex.Message

                        if written = c.Browser then
                            None
                        else
                            Some(sprintf "%s\n    pinned:  %s\n    written: %s" c.Name c.Browser written))

                Expect.isTrue (List.isEmpty mismatches) (String.Join("\n", mismatches))

            testCaseDeferred
                "885 acceptance — the reflective proxy sends the pinned body for a decimal, TimeSpan and DateTime argument"
                30
                (fun () ->
                    installXhrStub ()
                    scriptResponse 200 "null"
                    let before = callCount ()
                    let outcome = start (bookingApi.Book BrowserWriterFixture.booking)

                    fun () ->
                        same (callCount ()) (before + 1) "one request"
                        same (callBody before) BrowserWriterFixture.ReflectiveBody "the body the server decodes"

                        match outcome () with
                        | Returned() -> ()
                        | Raised ex -> failwithf "the call raised: %s" ex.Message
                        | Pending -> failwith "the call never completed")

            testCase "885 acceptance — the transpiled JsonEncode writes the pinned generated-proxy body"
            <| fun () ->
                same
                    (JsonEncode.arguments [ BrowserWriterFixture.bookingEncoder BrowserWriterFixture.booking ])
                    BrowserWriterFixture.EncodedBody
                    "the generated proxy's body in the browser"

            testCase "885.C decimal — the client reads a quoted decimal exactly, as the server now does"
            <| fun () ->
                same
                    (JsonText.tryParse "\"1234.5\"" |> Result.bind JsonDecode.asDecimal)
                    (Ok 1234.5M)
                    "the browser's quoted form"

                same
                    (JsonText.tryParse "\"79228162514264337593543950335\""
                     |> Result.bind JsonDecode.asDecimal)
                    (Ok 79228162514264337593543950335M)
                    "every digit"

                Expect.isTrue
                    (JsonText.tryParse "\"abc\""
                     |> Result.bind JsonDecode.asDecimal
                     |> Result.isError)
                    "a string that is not a number is refused"

            testCase
                "885.C DateTime — the client keeps the text's kind: `Z` is UTC, an offset is Local, none is Unspecified"
            <| fun () ->
                match
                    JsonText.tryParse "\"2026-09-27T10:30:00.0000000Z\""
                    |> Result.bind JsonDecode.asDateTime
                with
                | Ok d ->
                    same d.Kind DateTimeKind.Utc "the server writer's `Z` text is UTC"
                    same d.Hour 10 "so its fields are the UTC fields"
                    same d (DateTime(2026, 9, 27, 10, 30, 0, DateTimeKind.Utc)) "the instant"
                | Error e -> failwithf "refused: %s" (DecodeError.render e)

                match
                    JsonText.tryParse "\"2026-09-27T10:30:00.123Z\""
                    |> Result.bind JsonDecode.asDateTime
                with
                | Ok d ->
                    same d.Kind DateTimeKind.Utc "the browser's toISOString text is UTC too"
                    same d.Millisecond 123 "to the millisecond"
                | Error e -> failwithf "refused: %s" (DecodeError.render e)

                match
                    JsonText.tryParse "\"2026-09-27T10:30:00.123+02:00\""
                    |> Result.bind JsonDecode.asDateTime
                with
                | Ok d ->
                    same d.Kind DateTimeKind.Local "an explicit offset is Local, as RoundtripKind reads it"
                    same (d.ToUniversalTime().Hour) 8 "the same instant"
                | Error e -> failwithf "refused: %s" (DecodeError.render e)

                match
                    JsonText.tryParse "\"2026-01-15T08:05:09.007\""
                    |> Result.bind JsonDecode.asDateTime
                with
                | Ok d ->
                    same d.Kind DateTimeKind.Unspecified "no offset is Unspecified"
                    same d.Hour 8 "its clock fields as written"
                | Error e -> failwithf "refused: %s" (DecodeError.render e)

            testCase "885.C TimeSpan — one representation: every writer's spelling reads to the same host value"
            <| fun () ->
                let read text =
                    JsonText.tryParse text |> Result.bind JsonDecode.asTimeSpan

                for text in [ "90000"; "90000.0"; "9e4" ] do
                    same (read text) (Ok(TimeSpan.FromSeconds 90.0)) text
        ]

        testList "Phase 899 — the union-keyed map's request bodies, in the browser" [
            testCaseDeferred
                "899 — the reflective proxy sends the pinned body for a map keyed by a union with fields"
                30
                (fun () ->
                    installXhrStub ()
                    scriptResponse 200 "null"
                    let before = callCount ()
                    let outcome = start (tallyApi.Record BrowserWriterFixture.tally)

                    fun () ->
                        same (callCount ()) (before + 1) "one request"

                        same
                            (callBody before)
                            BrowserWriterFixture.TallyReflectiveBody
                            "the pairs form the server decodes"

                        match outcome () with
                        | Returned() -> ()
                        | Raised ex -> failwithf "the call raised: %s" ex.Message
                        | Pending -> failwith "the call never completed")

            testCase "899 — the transpiled `JsonEncode.mapOf` writes the pinned generated-proxy body"
            <| fun () ->
                same
                    (JsonEncode.arguments [ BrowserWriterFixture.tallyEncoder BrowserWriterFixture.tally ])
                    BrowserWriterFixture.TallyEncodedBody
                    "the generated proxy's body in the browser"
        ]

        testList "Phase 911 — int64 through the reflective proxy: exact, or refused, never 0" [
            // The measurement the phase exists for, kept as a pin on the
            // library: `Fable.SimpleJson` reads an int64 NUMBER token
            // through `int`, so anything outside int32 wraps — 2^53 + 1
            // (parsed to 2^53) is 0, 5,000,000,000 is 705,032,704. The
            // proxy no longer hands it a number at an int64 position; if
            // this case goes red the library changed, not the proxy.
            testCase "911 red — Fable.SimpleJson itself wraps an int64 number token outside int32"
            <| fun () ->
                same (viaReflection<int64> "9007199254740993") 0L "2^53 + 1 reads as 0"
                same (viaReflection<int64> "5000000000") 705032704L "5e9 wraps modulo 2^32"
                same (viaReflection<int64> "\"+9007199254740993\"") 9007199254740993L "the writer's string is exact"

            testCaseDeferred "911 — an int64 number token outside int32 reads exactly through the proxy" 30 (fun () ->
                installXhrStub ()
                JsonDecoders.resetForTests ()
                scriptResponse 200 "5000000000"
                let outcome = start (wideApi.GetCount())

                fun () ->
                    match outcome () with
                    | Returned n -> same n 5000000000L "exact, not wrapped"
                    | Raised ex -> failwithf "the call raised: %s" ex.Message
                    | Pending -> failwith "the call never completed")

            testCaseDeferred
                "911 — every int64 / uint64 position of a record reads exactly: field, option, list, tuple"
                30
                (fun () ->
                    installXhrStub ()
                    JsonDecoders.resetForTests ()

                    scriptResponse
                        200
                        """{"Count":-5000000000,"Size":9007199254740991,"Maybe":4294967296,"Series":[1,-2147483649,"+9007199254740993"],"Pair":[9007199254740991,"x"]}"""

                    let outcome = start (wideApi.GetReading())

                    fun () ->
                        match outcome () with
                        | Returned reading ->
                            same
                                reading
                                {
                                    Count = -5000000000L
                                    Size = 9007199254740991UL
                                    Maybe = Some 4294967296L
                                    Series = [ 1L; -2147483649L; 9007199254740993L ]
                                    Pair = 9007199254740991L, "x"
                                }
                                "every wide integer exact"
                        | Raised ex -> failwithf "the call raised: %s" ex.Message
                        | Pending -> failwith "the call never completed")

            testCaseDeferred "911 — an int64 number past 2^53 - 1 is a named refusal, never 0" 30 (fun () ->
                installXhrStub ()
                JsonDecoders.resetForTests ()
                scriptResponse 200 """{"Count":1,"Size":1,"Maybe":null,"Series":[9007199254740993],"Pair":[1,"x"]}"""
                let outcome = start (wideApi.GetReading())

                fun () ->
                    match outcome () with
                    | Raised(:? ProxyRequestException as ex) ->
                        match ex.DecodeError with
                        | Some error ->
                            same error.Path [ "Series"; "[0]" ] "the path to the offending element"
                            Expect.isTrue (error.Expected.Contains "Int64") error.Expected
                            Expect.isTrue (error.Found.Contains "9007199254740992") error.Found
                        | None -> failwith "DecodeError was None: the refusal was not carried as data"
                    | Raised ex -> failwithf "raised `%s`, not ProxyRequestException" ex.Message
                    | Returned reading -> failwithf "a lossy int64 decoded: %A" reading
                    | Pending -> failwith "the call never completed")

            testCaseDeferred "911 — a fractional or negative token at uint64 is refused" 30 (fun () ->
                installXhrStub ()
                JsonDecoders.resetForTests ()
                scriptResponse 200 "-1"
                let outcome = start (wideApi.GetSize())

                fun () ->
                    match outcome () with
                    | Raised(:? ProxyRequestException as ex) ->
                        match ex.DecodeError with
                        | Some error ->
                            same error.Path [] "the root"
                            Expect.isTrue (error.Expected.Contains "UInt64") error.Expected
                        | None -> failwith "DecodeError was None"
                    | other -> failwithf "expected a named refusal, got %A" other)

            testCaseDeferred "911 — a fractional token at int64 is refused" 30 (fun () ->
                installXhrStub ()
                JsonDecoders.resetForTests ()
                scriptResponse 200 "1.5"
                let outcome = start (wideApi.GetCount())

                fun () ->
                    match outcome () with
                    | Raised(:? ProxyRequestException as ex) ->
                        Expect.isTrue ex.DecodeError.IsSome "the refusal is carried as data"
                    | other -> failwithf "expected a named refusal, got %A" other)
        ]
    ]