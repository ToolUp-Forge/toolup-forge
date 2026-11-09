module ToolUp.Platform.Tests.InProcess.StreamingTests

open System.Collections.Generic
open Expecto
open ToolUp.Remoting
open ToolUp.Remoting.Server

// ─── Streaming substrate hardening ───────────────────
//
// Contract tests pinning the server-side streaming
// substrate (`Server/Remoting/Streaming.fs`):
//   * classification recognises `'arg -> IAsyncEnumerable<'T>` methods
//     and reports the (arg, element) shape; non-streaming methods are
//     not streams;
//   * a streaming method's markers are read onto its contract like any
//     other method's (the pipeline enforces them), across BOTH the
//     server-tier family and the `ToolUp.Platform.*` mirrors;
//   * SSE frame formatting (`chunk` / `complete` / `error`), including
//     the SSE-spec multiline `data:` framing and JSON-escaped error
//     messages;
//   * a typed source encodes each element at its declared type.

// ── Fixtures ────────────────────────────────────────────────────────

type private StreamApi = {
    [<AllowAnonymous>]
    Tail: string -> IAsyncEnumerable<string>
    [<AllowAnonymous>]
    GetOne: string -> Async<string>
}

/// A streaming method carrying pre-flight attributes, which the pipeline
/// enforces for it as for any method.
type private GuardedStreamApi = {
    [<RequiresRole "Admin">]
    [<RateLimit(10, RateLimitWindow.perMinute)>]
    Tail: string -> IAsyncEnumerable<string>
}

/// Same, via the tier-shared mirrors a Fable API record carries.
type private MirrorGuardedStreamApi = {
    [<ToolUp.Platform.RequiresRole "Admin">]
    Tail: string -> IAsyncEnumerable<string>
}

let private utf8 (b: byte[]) = System.Text.Encoding.UTF8.GetString b

[<Tests>]
let tests =
    testList "Phase 69c.tail — streaming substrate" [

        test "shapeOf recognises IAsyncEnumerable methods and reports the (arg, element) shape" {
            let field (name: string) =
                typeof<StreamApi>
                    .GetProperty(
                        name,
                        System.Reflection.BindingFlags.Instance
                        ||| System.Reflection.BindingFlags.Public
                        ||| System.Reflection.BindingFlags.NonPublic
                    )

            match Streaming.shapeOf (field "Tail") with
            | Some(argT, elemT) ->
                Expect.equal argT typeof<string> "arg type is string"
                Expect.equal elemT typeof<string> "element type is string"
            | None -> failtest "Tail must classify as a streaming method"

            Expect.isNone (Streaming.shapeOf (field "GetOne")) "a non-streaming Async method is not a stream"
        }

        test "isAsyncEnumerable identifies the closed generic" {
            Expect.isTrue
                (Streaming.isAsyncEnumerable typeof<IAsyncEnumerable<int>>)
                "IAsyncEnumerable<int> is a stream"

            Expect.isFalse (Streaming.isAsyncEnumerable typeof<string>) "string is not"
        }

        test "a streaming method's markers are read onto its contract (both families)" {
            match (Contract.read typeof<GuardedStreamApi>).Methods with
            | [ tail ] ->
                Expect.equal
                    tail.Classification
                    (Some(Classified.Requires [ RoleRequired "Admin" ]))
                    "the role requirement is the method's classification"

                Expect.equal tail.Budgets [ { Count = 10; WindowSeconds = 60 } ] "the rate-limit budget is read"
            | other -> failtestf "expected one method, got %A" other

            match (Contract.read typeof<MirrorGuardedStreamApi>).Methods with
            | [ tail ] ->
                Expect.equal
                    tail.Classification
                    (Some(Classified.Requires [ RoleRequired "Admin" ]))
                    "the mirror role requirement is recognised"
            | other -> failtestf "expected one mirror method, got %A" other
        }

        test "SSE frame formatting: chunk / complete / error" {
            Expect.equal
                (Sse.encode (StreamFrame.Chunk(None, "{\"x\":1}")))
                "event: chunk\ndata: {\"x\":1}\n\n"
                "single-line chunk frame"

            Expect.equal (utf8 (Streaming.formatComplete ())) "event: complete\ndata: {}\n\n" "terminal complete frame"

            let err = utf8 (Streaming.formatError "boom")
            Expect.stringContains err "event: error" "error frame carries the error event"
            Expect.stringContains err "boom" "error frame carries the message"
        }

        test "formatChunkWithId stamps the id line before the data" {
            Expect.equal
                (utf8 (Streaming.formatChunkWithId "abc-0" "{\"x\":1}"))
                "event: chunk\nid: abc-0\ndata: {\"x\":1}\n\n"
                "chunk frame carries `id: <correlation-id>-<chunk-index>`"

            // Multiline payloads keep their per-line data: framing under an id.
            Expect.equal
                (utf8 (Streaming.formatChunkWithId "abc-1" "a\nb"))
                "event: chunk\nid: abc-1\ndata: a\ndata: b\n\n"
                "id line composes with the spec's multiline data framing"
        }

        test "Sse.parse decodes exactly what the server frames" {
            let wire =
                utf8 (Streaming.formatChunkWithId "corr-0" "{\"x\":1}")
                + utf8 (Streaming.formatChunkWithId "corr-1" "line-a\nline-b")
                + ": keepalive\n\n"
                + Sse.encode (StreamFrame.Chunk(None, "no-id"))
                + utf8 (Streaming.formatComplete ())

            Expect.equal
                (Sse.parse wire)
                [
                    StreamFrame.Chunk(Some "corr-0", "{\"x\":1}")
                    StreamFrame.Chunk(Some "corr-1", "line-a\nline-b")
                    StreamFrame.Chunk(None, "no-id")
                    StreamFrame.Complete
                ]
                "chunk / multiline chunk / id-less chunk / complete decode in order; the comment is dropped"

            Expect.equal
                (Sse.parse (utf8 (Streaming.formatError "boom")))
                [ StreamFrame.Error "{\"message\":\"boom\"}" ]
                "error frame decodes with its JSON payload intact"
        }

        test "Sse.parse is terminator-tolerant and holds a trailing partial event" {
            Expect.equal
                (Sse.parse "event: chunk\r\nid: c-0\r\ndata: 1\r\n\r\n")
                [ StreamFrame.Chunk(Some "c-0", "1") ]
                "CRLF frames decode"

            Expect.equal
                (Sse.parse "event: chunk\rdata: 2\r\r")
                [ StreamFrame.Chunk(None, "2") ]
                "bare-CR frames decode"

            // No event: field means the spec's default `message` type (the
            // legacy per-user AI channel is framed this way).
            Expect.equal
                (Sse.parse "data: {\"a\":1}\n\n")
                [ StreamFrame.Other("message", None, "{\"a\":1}") ]
                "an event with no event: field is a `message`"

            // A partial trailing event (no closing blank line) is NOT
            // emitted: a reader holds it for the next chunk, exactly as a
            // browser does.
            Expect.equal (Sse.parse "event: chunk\ndata: partial") [] "a trailing partial event is held, not emitted"

            // An event with no data: line is dropped per the spec.
            Expect.equal (Sse.parse "event: chunk\nid: x\n\n") [] "an event with no data is dropped"
        }

        test "multiline payloads are framed one data: line each (SSE spec)" {
            // A payload containing a literal newline must NOT prematurely
            // end the SSE event: every source line is its own data: line.
            Expect.equal
                (Sse.encode (StreamFrame.Chunk(None, "line-a\nline-b")))
                "event: chunk\ndata: line-a\ndata: line-b\n\n"
                "each source line is a data: line; the event ends with a blank line"
        }

        test "a typed source encodes each element at its declared type, a None included" {
            let options = ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

            let values: IAsyncEnumerable<string option> =
                { new IAsyncEnumerable<string option> with
                    member _.GetAsyncEnumerator _ = failwith "never enumerated"
                }

            let source = Streaming.sourceOf options values
            Expect.equal source.Element typeof<string option> "the element type"
            Expect.equal (source.Encode(box (Some "a"))) "\"a\"" "Some encodes as its value"
            Expect.equal (source.Encode null) "null" "None encodes as null rather than faulting"
        }
    ]