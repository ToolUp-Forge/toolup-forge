module ToolUp.Platform.Tests.InProcess.StreamingTests

open System.Collections.Generic
open Expecto
open ToolUp.Remoting.Server

// ─── Phase 69c.tail — streaming substrate hardening ───────────────────
//
// Contract tests pinning the shipped Phase 69c server-side streaming
// substrate (`Server/Remoting/Streaming.fs`):
//   * classification recognises `'arg -> IAsyncEnumerable<'T>` methods
//     and reports the (arg, element) shape; non-streaming methods are
//     absent;
//   * the pre-flight-attribute refusal — a streaming method carrying
//     `[<RequiresRole>]` / `[<RateLimit>]` / `[<Audit>]` / `[<Idempotent>]`
//     is flagged (the SSE short-circuit can't enforce them), recognised
//     across BOTH the server-tier family and the `ToolUp.Platform.*`
//     mirrors;
//   * SSE frame formatting (`chunk` / `complete` / `error`), including
//     the SSE-spec multiline `data:` framing and JSON-escaped error
//     messages;
//   * first-argument parsing from the request body.
//
// The wire-divergent live-path migration (AIAssistantHandler SSE →
// typed stream) + the Fable Cmd.OfRemoting.callStreaming client helper
// remain consumer-trigger-gated (Phase 69c.tail A/B/C/D/E); these tests
// harden the substrate those tasks build on.

// ── Fixtures ────────────────────────────────────────────────────────

type private StreamApi = {
    [<AllowAnonymous>]
    Tail: string -> IAsyncEnumerable<string>
    [<AllowAnonymous>]
    GetOne: string -> Async<string>
}

/// A streaming method carrying pre-flight attributes the SSE short-
/// circuit can't honour — the adapter must refuse to start.
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

        test "classify recognises IAsyncEnumerable methods and reports the (arg, element) shape" {
            let cls = Streaming.classify typeof<StreamApi>

            match cls |> Map.tryFind "Tail" with
            | Some(argT, elemT) ->
                Expect.equal argT typeof<string> "arg type is string"
                Expect.equal elemT typeof<string> "element type is string"
            | None -> failtest "Tail must classify as a streaming method"

            Expect.isFalse (cls.ContainsKey "GetOne") "a non-streaming Async method is absent"
        }

        test "isAsyncEnumerable identifies the closed generic" {
            Expect.isTrue
                (Streaming.isAsyncEnumerable typeof<IAsyncEnumerable<int>>)
                "IAsyncEnumerable<int> is a stream"

            Expect.isFalse (Streaming.isAsyncEnumerable typeof<string>) "string is not"
        }

        test "a streaming method carrying unenforceable attributes is flagged (both families)" {
            let server =
                Streaming.streamingMethodsCarryingUnenforceableAttributes typeof<GuardedStreamApi>

            match server with
            | [ (name, attrs) ] ->
                Expect.equal name "Tail" "the guarded streaming method is named"
                Expect.contains attrs "RequiresRole(\"Admin\")" "the role requirement is listed"
                Expect.contains attrs "RateLimit(10, 60s)" "the rate-limit budget is listed"
            | other -> failtestf "expected exactly one flagged method, got %A" other

            let mirror =
                Streaming.streamingMethodsCarryingUnenforceableAttributes typeof<MirrorGuardedStreamApi>

            match mirror with
            | [ (name, attrs) ] ->
                Expect.equal name "Tail" "mirror-family guarded method is named"

                Expect.contains
                    attrs
                    "RequiresRole(\"Admin\")"
                    "the mirror role requirement is recognised by simple name"
            | other -> failtestf "expected one flagged mirror method, got %A" other
        }

        test "a clean streaming method (AllowAnonymous only) is not flagged" {
            Expect.isEmpty
                (Streaming.streamingMethodsCarryingUnenforceableAttributes typeof<StreamApi>)
                "[<AllowAnonymous>] is an explicit no-auth marker — not an unenforceable requirement"
        }

        test "SSE frame formatting: chunk / complete / error" {
            Expect.equal
                (utf8 (Streaming.formatChunk "{\"x\":1}"))
                "event: chunk\ndata: {\"x\":1}\n\n"
                "single-line chunk frame"

            Expect.equal (utf8 (Streaming.formatComplete ())) "event: complete\ndata: {}\n\n" "terminal complete frame"

            let err = utf8 (Streaming.formatError "boom")
            Expect.stringContains err "event: error" "error frame carries the error event"
            Expect.stringContains err "boom" "error frame carries the message"
        }

        test "multiline payloads are framed one data: line each (SSE spec)" {
            // A payload containing a literal newline must NOT prematurely
            // end the SSE event — every source line is its own data: line.
            let frame = utf8 (Streaming.formatChunk "line-a\nline-b")

            Expect.equal
                frame
                "event: chunk\ndata: line-a\ndata: line-b\n\n"
                "each source line is a data: line; the event ends with \\n\\n"
        }

        test "parseFirstArg deserialises the first body-array element" {
            let opts = System.Text.Json.JsonSerializerOptions()

            match Streaming.parseFirstArg "[\"hello\"]" typeof<string> opts with
            | Some(:? string as s) -> Expect.equal s "hello" "first arg parsed"
            | other -> failtestf "expected Some \"hello\", got %A" other

            Expect.isNone (Streaming.parseFirstArg "not-json" typeof<string> opts) "unparseable body is None"
            Expect.isNone (Streaming.parseFirstArg "[]" typeof<string> opts) "empty array is None"
        }
    ]