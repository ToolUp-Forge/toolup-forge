module ToolUp.Platform.Tests.InProcess.RemotingDecodeRefusalTests

open System
open System.Net
open System.Net.Http
open System.Text
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Remoting
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe

// ─── Phase 783 — decode failures are named refusals, not exceptions ──
//
// Before this phase both decode paths threw, and the dispatcher's error
// adapter folded every throw into `Errors.unhandled` — the generic
// "Error occured while running the function X" envelope on a 500. A
// caller that sent the wrong SHAPE was therefore indistinguishable, on
// the wire and in the client, from a caller that tripped a handler bug.
//
// These run over the real dispatcher on a TestServer rather than over
// `Proxy.makeApiProxy` in isolation, because the claim is about what a
// CLIENT sees: the status code, the envelope category, and the fact that
// the handler never ran. A unit test over the proxy would assert the
// `InvocationResult` case and prove nothing about the response.
//
// The sentinel the pack is built around is `Errors.unhandled`'s exact
// text. It is asserted ABSENT on every malformed shape — that assertion
// is the phase's acceptance criterion stated directly, and it goes red
// the moment any decode failure falls back to the generic envelope.

/// `Errors.unhandled`'s wire text (`Errors.fs`). Matched as a prefix so
/// the assertion does not depend on which method name is interpolated.
let private unhandledSentinel = "Error occured while running the function"

type private DecodeProbeRequest = { Count: int; Label: string }

type private DecodeProbeApi = {
    [<AllowAnonymous>]
    Echo: DecodeProbeRequest -> Async<string>
    [<AllowAnonymous>]
    Pair: int -> string -> Async<string>
}

let private buildHost (h: HttpHandler) : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost.UseTestServer().Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe h)
            |> ignore)
        .Build()

/// Per-call isolation: a fresh invocation counter and implementation per
/// request, so "the handler never ran" is a claim about THIS call and not
/// about whatever a sibling test did.
let private post (method: string) (body: string) = async {
    let invocations = ref 0

    let impl: DecodeProbeApi = {
        Echo =
            fun req -> async {
                System.Threading.Interlocked.Increment invocations |> ignore
                return sprintf "%d:%s" req.Count req.Label
            }
        Pair =
            fun n s -> async {
                System.Threading.Interlocked.Increment invocations |> ignore
                return sprintf "%d/%s" n s
            }
    }

    let handler =
        Remoting.createApi () |> Remoting.fromValue impl |> Remoting.buildHttpHandler

    use host = buildHost handler
    do! host.StartAsync() |> Async.AwaitTask
    use client = host.GetTestClient()

    use req =
        new HttpRequestMessage(HttpMethod.Post, sprintf "/DecodeProbeApi/%s" method)

    req.Content <- new StringContent(body, Encoding.UTF8, "application/json")

    let! resp = client.SendAsync req |> Async.AwaitTask
    let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
    do! host.StopAsync() |> Async.AwaitTask
    return int resp.StatusCode, text, invocations.Value
}

/// Every assertion a malformed request must satisfy, in one place: it is
/// refused (400, never 500), it is categorised `validation` so a client
/// that already branches on Phase 69e's category needs no new code, it
/// carries the structured detail, and it is NOT the generic unhandled
/// envelope.
let private expectRefusal (label: string) (status: int) (text: string) (invocations: int) =
    Expect.equal status 400 (sprintf "%s — refused with 400, not the pre-783 uncategorised 500" label)

    Expect.isFalse
        (text.Contains unhandledSentinel)
        (sprintf "%s — `Errors.unhandled` is not reachable from a decode failure" label)

    Expect.stringContains text "\"category\":\"validation\"" (sprintf "%s — carries the Phase 69e category" label)
    Expect.stringContains text "\"decodeError\"" (sprintf "%s — carries the structured refusal" label)
    Expect.stringContains text "\"expected\"" (sprintf "%s — names what was expected" label)
    Expect.stringContains text "\"found\"" (sprintf "%s — names what was found" label)
    Expect.stringContains text "\"path\"" (sprintf "%s — names where" label)
    Expect.equal invocations 0 (sprintf "%s — the handler never ran" label)

let tests =
    testList "Phase 783 — remoting decode refusals" [

        // ── the STJ request wire ──────────────────────────────────────
        //
        // Note what is NOT here: a MsgPack **argument** case. The server
        // dispatcher has no MsgPack request wire — `InvocationPropsInt.
        // Arguments` is `Choice<byte[], JsonElement> list` and every
        // decode runs through System.Text.Json. `MsgPack.Read.Reader` has
        // exactly one call site in the tree, `Remoting.withBinarySerialization`
        // on the CLIENT, decoding the server's binary RESPONSE. Its
        // refusals are covered by the reader pack further down and by the
        // client-side pack at the end.

        testAsync "a well-formed request is unaffected — byte-identical response" {
            let! status, text, invocations = post "Echo" """[{"Count":3,"Label":"ok"}]"""

            Expect.equal status 200 "the happy path still succeeds"
            Expect.equal text "\"3:ok\"" "the response body is byte-identical to the pre-phase wire shape"
            Expect.equal invocations 1 "the handler ran exactly once"
        }

        testAsync "a mistyped field is a named refusal, not a 500" {
            // A JSON object where the record declares an `int`. This is
            // the canonical "you sent the wrong shape" — and the one that
            // used to arrive as a handler-bug-shaped 500.
            let! status, text, invocations = post "Echo" """[{"Count":{"nested":1},"Label":"ok"}]"""

            expectRefusal "mistyped field" status text invocations
            Expect.stringContains text "Echo" "the refusal names the method"
        }

        testAsync "a body that is not a JSON array is a named refusal" {
            let! status, text, invocations = post "Echo" """{"Count":3,"Label":"ok"}"""

            expectRefusal "non-array body" status text invocations
            Expect.stringContains text "argument(s)" "the refusal says an argument array was expected"
        }

        testAsync "a malformed JSON body is a named refusal" {
            let! status, text, invocations = post "Echo" """[{"Count":3,"""

            expectRefusal "malformed JSON" status text invocations
            Expect.stringContains text "malformed JSON" "the refusal says the body did not parse"
        }

        testAsync "too few arguments is a named refusal" {
            // `Pair: int -> string -> Async<string>` given one argument.
            let! status, text, invocations = post "Pair" """[7]"""

            expectRefusal "arity short" status text invocations
        }

        testAsync "too many arguments is a named refusal" {
            let! status, text, invocations = post "Pair" """[7,"a","b"]"""

            expectRefusal "arity long" status text invocations
        }

        test "the unhandled sentinel is the real emitter's text" {
            // Verifies the PROBE, not the verdict. Every refusal assertion
            // above is an `isFalse (contains sentinel)`, which passes
            // vacuously if the sentinel ever stops matching what
            // `Errors.unhandled` actually writes. Pinning it against the
            // emitter means the pack goes red on that drift instead of
            // going quietly green over a check that can no longer fail.
            Expect.stringStarts
                (string (Errors.unhandled "SomeMethod").error)
                unhandledSentinel
                "the sentinel is `Errors.unhandled`'s own wire text"
        }

        // ── the closed vocabulary ─────────────────────────────────────

        test "DecodeError renders as the substrate's decoder messages do" {
            let withPath = {
                Path = [ "args[0]"; "count" ]
                Expected = "int"
                Found = "string"
            }

            Expect.equal
                (DecodeError.render withPath)
                "expected int at args[0].count, got string"
                "the rendered sentence is the one the phase specified"

            Expect.equal
                (DecodeError.render (DecodeError.create "object" "array"))
                "expected object, got array"
                "an empty path drops the `at …` clause rather than rendering it empty"
        }

        test "ParsingArgumentsError is the rendered form, not a second vocabulary" {
            let error = DecodeError.at [ "Echo"; "args[0]" ] "int" "string"

            Expect.equal
                (ParsingArgumentsError.ofDecodeError error).ParsingArgumentsError
                (DecodeError.render error)
                "the legacy stringly-typed shape is produced from the structured one"
        }

        test "`under` annotates outermost-first as a refusal unwinds" {
            let annotated =
                DecodeError.create "int" "string"
                |> DecodeError.under "count"
                |> DecodeError.under "args[0]"

            Expect.equal annotated.Path [ "args[0]"; "count" ] "each frame pushes onto the front"
        }

        // ── the MsgPack reader (the client's response wire) ───────────

        test "the MsgPack reader refuses with a named DecodeError" {
            // 0xC1 is the one byte MessagePack never assigns, so it
            // matches no format arm and falls through to the reader's
            // final refusal.
            let reader = MsgPack.Read.Reader [| 0xC1uy |]

            match reader.TryRead typeof<int> with
            | Ok value -> failtestf "expected a refusal, got %A" value
            | Error error ->
                Expect.equal error.Expected "Int32" "the refusal names the type that was wanted"
                Expect.stringContains error.Found "format byte" "the refusal names what the bytes held"
                Expect.isNonEmpty error.Path "the refusal carries the byte offset it was at"
        }

        test "the throwing reader entry still throws for existing callers (GP 11)" {
            let reader = MsgPack.Read.Reader [| 0xC1uy |]

            Expect.throws
                (fun () -> reader.Read typeof<int> |> ignore)
                "`Read` keeps its pre-783 contract — external callers compile and behave unchanged"
        }

        test "a well-formed payload reads identically through both entries" {
            // fixposnum 7 — the same bytes through `Read` and `TryRead`.
            Expect.equal (MsgPack.Read.Reader([| 7uy |]).Read typeof<int>) (box 7) "Read decodes"

            Expect.equal
                (MsgPack.Read.Reader([| 7uy |]).TryRead typeof<int>)
                (Ok(box 7))
                "TryRead decodes to the identical value — the refusing entry is not a different decoder"
        }

        // ── the client surface ────────────────────────────────────────

        testAsync "the client recovers the server's refusal from the wire, without parsing prose" {
            // End-to-end: the body asserted here is the one the server
            // actually wrote above, not a hand-written fixture — so the
            // emitter and the reader cannot drift apart silently.
            let! _, text, _ = post "Echo" """[{"Count":{"nested":1},"Label":"ok"}]"""

            match ToolUp.Remoting.Client.Proxy.tryReadDecodeError text with
            | None -> failtestf "the client could not recover a refusal from the server's own envelope: %s" text
            | Some error ->
                Expect.isNonEmpty error.Expected "the recovered refusal names what was expected"
                Expect.isNonEmpty error.Found "the recovered refusal names what was found"
                Expect.isNonEmpty error.Path "the recovered refusal carries the path"
        }

        test "a non-decode error body yields None, not a fabricated refusal" {
            // A Phase 69e attribute-validation envelope: `validation`
            // category, but a DECODED value failing a rule. The two must
            // stay distinguishable — `Some` here would make every
            // validation failure look like a decode failure.
            let violationBody =
                """{"error":{"methodName":"Echo","violations":[]},"ignored":false,"handled":true,"category":"validation","__schema_version":1}"""

            Expect.isNone
                (ToolUp.Remoting.Client.Proxy.tryReadDecodeError violationBody)
                "a 69e violation envelope is not a decode refusal"

            Expect.isNone
                (ToolUp.Remoting.Client.Proxy.tryReadDecodeError "not json at all")
                "an unparseable body yields None rather than throwing"

            Expect.isNone (ToolUp.Remoting.Client.Proxy.tryReadDecodeError "") "an empty body yields None"
        }

        test "ProxyRequestException carries the refusal, and the pre-783 shape still constructs" {
            let response: ToolUp.Remoting.Client.HttpResponse = { StatusCode = 400; ResponseBody = "" }

            let error = DecodeError.at [ "Echo"; "args[0]" ] "int" "string"

            let withRefusal =
                ToolUp.Remoting.Client.ProxyRequestException(response, "msg", "", Some error)

            Expect.equal withRefusal.DecodeError (Some error) "the refusal rides the exception"

            let withoutRefusal =
                ToolUp.Remoting.Client.ProxyRequestException(response, "msg", "")

            Expect.isNone
                withoutRefusal.DecodeError
                "the three-argument constructor still exists and defaults the refusal to None (GP 11)"
        }
    ]