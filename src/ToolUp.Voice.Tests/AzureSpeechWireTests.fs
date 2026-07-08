module ToolUp.Voice.Tests.AzureSpeechWireTests

open Expecto
open ToolUp.Voice

// Azure Speech provider request-shaping + response-parsing — the pure
// `Wire` surface, asserted without any HTTP call.

[<Tests>]
let azureSpeechWireTests =
    testList "AzureSpeech Wire" [
        test "accepts webm/ogg/wav, rejects others" {
            Expect.isTrue (AzureSpeechTranscriptionProvider.Wire.isAccepted "audio/webm;codecs=opus") "webm accepted"
            Expect.isTrue (AzureSpeechTranscriptionProvider.Wire.isAccepted "audio/wav") "wav accepted"
            Expect.isFalse (AzureSpeechTranscriptionProvider.Wire.isAccepted "audio/mpeg") "mp3 rejected (short-audio)"
        }

        test "azureContentTypeHeader maps captured formats to Azure headers" {
            Expect.stringContains
                (AzureSpeechTranscriptionProvider.Wire.azureContentTypeHeader "audio/webm;codecs=opus")
                "codecs=opus"
                "webm declares opus"

            Expect.stringContains
                (AzureSpeechTranscriptionProvider.Wire.azureContentTypeHeader "audio/wav")
                "samplerate=16000"
                "wav declares PCM sample rate"
        }

        test "recognitionUri embeds region, url-encoded language, and detailed format" {
            let uri = AzureSpeechTranscriptionProvider.Wire.recognitionUri "westeurope" "en-US"
            Expect.stringContains uri "westeurope.stt.speech.microsoft.com" "region host"
            Expect.stringContains uri "language=en-US" "language query"
            Expect.stringContains uri "format=detailed" "detailed for confidence + timing"
        }

        test "effectiveLanguage defaults when no hint, honours a hint" {
            Expect.equal (AzureSpeechTranscriptionProvider.Wire.effectiveLanguage None) "en-US" "default language"

            Expect.equal
                (AzureSpeechTranscriptionProvider.Wire.effectiveLanguage (Some "fr-FR"))
                "fr-FR"
                "hint honoured"

            Expect.equal
                (AzureSpeechTranscriptionProvider.Wire.effectiveLanguage (Some "  "))
                "en-US"
                "blank hint falls back to default"
        }

        test "parseResponse maps a Success N-best hypothesis with confidence + ticks" {
            let json =
                """{ "RecognitionStatus":"Success", "DisplayText":"Hello world",
                     "NBest":[ {"Confidence":0.94,"Display":"Hello world","Lexical":"hello world",
                                "Offset":5000000,"Duration":12000000} ] }"""

            match AzureSpeechTranscriptionProvider.Wire.parseResponse json with
            | Ok t ->
                Expect.equal t.Segments.Length 1 "one segment"
                Expect.equal t.Segments.[0].Text "Hello world" "display text"
                Expect.equal t.Segments.[0].Confidence (Some 0.94) "confidence mapped"
                // 5,000,000 ticks = 0.5 s (100-ns ticks).
                Expect.equal t.Segments.[0].Start (System.TimeSpan.FromSeconds 0.5) "offset ticks → TimeSpan"
                Expect.equal t.Segments.[0].End (System.TimeSpan.FromSeconds 1.7) "offset + duration"
            | Error e -> failtestf "expected Ok, got %A" e
        }

        test "parseResponse maps NoMatch / silence to an empty transcript" {
            for status in [ "NoMatch"; "InitialSilenceTimeout"; "BabbleTimeout" ] do
                let json = sprintf """{ "RecognitionStatus":"%s" }""" status

                match AzureSpeechTranscriptionProvider.Wire.parseResponse json with
                | Ok t -> Expect.equal t.Segments [] (sprintf "%s → empty transcript" status)
                | Error e -> failtestf "%s expected Ok empty, got %A" status e
        }

        test "parseResponse without a status is malformed" {
            match AzureSpeechTranscriptionProvider.Wire.parseResponse """{ "DisplayText":"x" }""" with
            | Error(TranscriptionError.MalformedResponse _) -> ()
            | other -> failtestf "expected MalformedResponse, got %A" other
        }

        test "classifyStatus splits transient from permanent" {
            match AzureSpeechTranscriptionProvider.Wire.classifyStatus 429 "throttled" with
            | TranscriptionError.Transient _ -> ()
            | other -> failtestf "429 should be transient, got %A" other

            match AzureSpeechTranscriptionProvider.Wire.classifyStatus 403 "forbidden" with
            | TranscriptionError.PermanentClient(403, _) -> ()
            | other -> failtestf "403 should be permanent, got %A" other
        }

        test "provider declares azure-speech identity, batch-only" {
            let provider =
                AzureSpeechTranscriptionProvider.createWithKeyAndRegion "key" "westeurope"

            Expect.equal provider.ProviderId "azure-speech" "provider id"
            Expect.isFalse provider.SupportsStreaming "batch only"

            match provider.OpenSession((fun _ -> ()), None) |> Async.RunSynchronously with
            | Error(TranscriptionError.StreamingUnsupported _) -> ()
            | other -> failtestf "expected StreamingUnsupported, got %A" other
        }
    ]