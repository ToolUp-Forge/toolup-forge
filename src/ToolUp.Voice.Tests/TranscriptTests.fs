module ToolUp.Voice.Tests.TranscriptTests

open System
open Expecto
open ToolUp.Voice

// Core `Transcript` model + `TranscriptionError` behaviour. Pure data —
// no HTTP, no provider — so this pack is a fast, credential-free gate on
// the abstraction's projections and taxonomy.

[<Tests>]
let transcriptTests =
    testList "Transcript model" [
        test "plainText joins segment texts with single spaces" {
            let t = {
                Segments = [
                    {
                        Text = "hello"
                        Start = TimeSpan.Zero
                        End = TimeSpan.FromSeconds 1.0
                        Confidence = None
                    }
                    {
                        Text = "world"
                        Start = TimeSpan.FromSeconds 1.0
                        End = TimeSpan.FromSeconds 2.0
                        Confidence = None
                    }
                ]
                Language = Some "en-US"
            }

            Expect.equal (Transcript.plainText t) "hello world" "two segments join with one space"
        }

        test "plainText trims segments and drops empties, no double whitespace" {
            let t = {
                Segments = [
                    TranscriptSegment.ofText "  leading "
                    TranscriptSegment.ofText ""
                    TranscriptSegment.ofText "   "
                    TranscriptSegment.ofText " trailing  "
                ]
                Language = None
            }

            Expect.equal
                (Transcript.plainText t)
                "leading trailing"
                "empty/whitespace segments dropped, rest single-spaced"
        }

        test "plainText of the empty transcript is the empty string" {
            Expect.equal (Transcript.plainText Transcript.empty) "" "no segments → empty string"
        }

        test "ofText builds a single flat segment with no timing/confidence" {
            let t = Transcript.ofText "spoken words"
            Expect.equal t.Segments.Length 1 "one segment"
            Expect.equal t.Segments.[0].Text "spoken words" "carries the text"
            Expect.equal t.Segments.[0].Start TimeSpan.Zero "no start offset"
            Expect.equal t.Segments.[0].Confidence None "no confidence"
            Expect.equal (Transcript.plainText t) "spoken words" "projects back to the text"
        }

        test "plainText tolerates a null segment text without throwing" {
            let t = {
                Segments = [
                    {
                        Text = null
                        Start = TimeSpan.Zero
                        End = TimeSpan.Zero
                        Confidence = None
                    }
                    TranscriptSegment.ofText "ok"
                ]
                Language = None
            }

            Expect.equal (Transcript.plainText t) "ok" "null text coerces to empty and is dropped"
        }
    ]

[<Tests>]
let errorTests =
    testList "TranscriptionError" [
        test "only Transient is retryable" {
            Expect.isTrue (TranscriptionError.isRetryable (TranscriptionError.Transient "blip")) "transient retryable"

            Expect.isFalse
                (TranscriptionError.isRetryable (TranscriptionError.PermanentClient(401, "bad key")))
                "auth failure not retryable"

            Expect.isFalse
                (TranscriptionError.isRetryable (TranscriptionError.NotConfigured "no key"))
                "misconfig not retryable"

            Expect.isFalse
                (TranscriptionError.isRetryable (TranscriptionError.MalformedResponse "junk"))
                "malformed not retryable"

            Expect.isFalse
                (TranscriptionError.isRetryable (TranscriptionError.UnsupportedAudio("audio/x", "nope")))
                "unsupported audio not retryable"

            Expect.isFalse
                (TranscriptionError.isRetryable (TranscriptionError.StreamingUnsupported "batch only"))
                "streaming-unsupported not retryable"
        }

        test "describe names the status for a permanent client error" {
            let msg =
                TranscriptionError.describe (TranscriptionError.PermanentClient(400, "bad request"))

            Expect.stringContains msg "400" "mentions the status code"
        }

        test "describe names the content type for unsupported audio" {
            let msg =
                TranscriptionError.describe (TranscriptionError.UnsupportedAudio("audio/aiff", "nope"))

            Expect.stringContains msg "audio/aiff" "mentions the content type"
        }
    ]

[<Tests>]
let requestTests =
    testList "TranscriptionRequest" [
        test "create sets no language hint" {
            let r = TranscriptionRequest.create "audio/webm" [| 1uy; 2uy |]
            Expect.equal r.ContentType "audio/webm" "content type set"
            Expect.equal r.Audio.Length 2 "audio carried"
            Expect.equal r.LanguageHint None "no hint by default"
        }

        test "withLanguage attaches the hint" {
            let r =
                TranscriptionRequest.create "audio/wav" [||]
                |> TranscriptionRequest.withLanguage "en-GB"

            Expect.equal r.LanguageHint (Some "en-GB") "hint attached"
        }
    ]