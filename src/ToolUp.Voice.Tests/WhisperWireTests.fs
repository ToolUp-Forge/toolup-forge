module ToolUp.Voice.Tests.WhisperWireTests

open Expecto
open ToolUp.Voice

// Whisper provider request-shaping + response-parsing — the pure `Wire`
// surface, asserted without any HTTP call. The provider instance is also
// checked for its declared capabilities.

[<Tests>]
let whisperWireTests =
    testList "Whisper Wire" [
        test "bareContentType strips the codec parameter" {
            Expect.equal
                (WhisperTranscriptionProvider.Wire.bareContentType "audio/webm;codecs=opus")
                "audio/webm"
                "drops ;codecs=…"
        }

        test "accepts MediaRecorder webm and common upload formats" {
            Expect.isTrue (WhisperTranscriptionProvider.Wire.isAccepted "audio/webm;codecs=opus") "webm+opus accepted"
            Expect.isTrue (WhisperTranscriptionProvider.Wire.isAccepted "audio/mpeg") "mpeg accepted"
            Expect.isFalse (WhisperTranscriptionProvider.Wire.isAccepted "audio/aiff") "aiff rejected"
            Expect.isFalse (WhisperTranscriptionProvider.Wire.isAccepted "video/mp4") "video rejected"
        }

        test "filenameFor maps the content type to a recognised extension" {
            Expect.equal (WhisperTranscriptionProvider.Wire.filenameFor "audio/webm;codecs=opus") "audio.webm" "webm"
            Expect.equal (WhisperTranscriptionProvider.Wire.filenameFor "audio/mpeg") "audio.mp3" "mpeg→mp3"
            Expect.equal (WhisperTranscriptionProvider.Wire.filenameFor "audio/wav") "audio.wav" "wav"
        }

        test "formFields always requests verbose_json and includes the model" {
            let fields = WhisperTranscriptionProvider.Wire.formFields "whisper-1" None
            Expect.contains fields ("model", "whisper-1") "carries the model"
            Expect.contains fields ("response_format", "verbose_json") "requests per-segment timings"
            Expect.isFalse (fields |> List.exists (fun (k, _) -> k = "language")) "no language field when no hint"
        }

        test "formFields includes the language only when a hint is supplied" {
            let fields = WhisperTranscriptionProvider.Wire.formFields "whisper-1" (Some "en-GB")
            Expect.contains fields ("language", "en-GB") "language field present"
        }

        test "formFields drops a blank language hint" {
            let fields = WhisperTranscriptionProvider.Wire.formFields "whisper-1" (Some "   ")
            Expect.isFalse (fields |> List.exists (fun (k, _) -> k = "language")) "blank hint omitted"
        }

        test "parseResponse maps verbose_json segments with timings" {
            let json =
                """{ "language":"english", "text":"hello world",
                     "segments":[ {"start":0.0,"end":1.5,"text":" hello"},
                                  {"start":1.5,"end":2.4,"text":" world"} ] }"""

            match WhisperTranscriptionProvider.Wire.parseResponse json with
            | Ok t ->
                Expect.equal t.Segments.Length 2 "two segments"
                Expect.equal t.Language (Some "english") "language carried"
                Expect.equal t.Segments.[1].Start (System.TimeSpan.FromSeconds 1.5) "second segment start"
                Expect.equal t.Segments.[0].Confidence None "confidence deliberately None"
                Expect.equal (Transcript.plainText t) "hello world" "projects to plain text"
            | Error e -> failtestf "expected Ok, got %A" e
        }

        test "parseResponse falls back to flat text with no segments array" {
            let json = """{ "text":"just text" }"""

            match WhisperTranscriptionProvider.Wire.parseResponse json with
            | Ok t -> Expect.equal (Transcript.plainText t) "just text" "flat text one segment"
            | Error e -> failtestf "expected Ok, got %A" e
        }

        test "parseResponse surfaces malformed JSON as MalformedResponse" {
            match WhisperTranscriptionProvider.Wire.parseResponse "{ not json" with
            | Error(TranscriptionError.MalformedResponse _) -> ()
            | other -> failtestf "expected MalformedResponse, got %A" other
        }

        test "classifyStatus splits transient from permanent" {
            match WhisperTranscriptionProvider.Wire.classifyStatus 429 "rate" with
            | TranscriptionError.Transient _ -> ()
            | other -> failtestf "429 should be transient, got %A" other

            match WhisperTranscriptionProvider.Wire.classifyStatus 503 "down" with
            | TranscriptionError.Transient _ -> ()
            | other -> failtestf "503 should be transient, got %A" other

            match WhisperTranscriptionProvider.Wire.classifyStatus 401 "bad key" with
            | TranscriptionError.PermanentClient(401, _) -> ()
            | other -> failtestf "401 should be permanent, got %A" other
        }

        test "provider declares whisper identity, batch-only" {
            let provider = WhisperTranscriptionProvider.createWithApiKey "sk-test"
            Expect.equal provider.ProviderId "whisper" "provider id"
            Expect.isFalse provider.SupportsStreaming "batch only"
        }

        test "OpenSession on a batch-only provider returns StreamingUnsupported" {
            let provider = WhisperTranscriptionProvider.createWithApiKey "sk-test"

            match provider.OpenSession((fun _ -> ()), None) |> Async.RunSynchronously with
            | Error(TranscriptionError.StreamingUnsupported _) -> ()
            | other -> failtestf "expected StreamingUnsupported, got %A" other
        }
    ]