// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Voice

open System

// ─── Transcript model ─────────────────────────────────────────────
//
// The value types the transcription abstraction exchanges. Pure data —
// FSharp.Core + BCL only, no System.Text.Json, no HttpClient — so this
// file is the SDK's "minimum viable voice consumer" floor and packs
// under `fable/` unchanged for the client companion, which reuses the
// `Transcript` / `TranscriptSegment` shapes to render captured text.

/// A single recognised span of a transcript. `Start` / `End` are offsets
/// from the beginning of the audio; `Confidence`, when the provider
/// reports it, is a `[0.0, 1.0]` score. Providers that report neither
/// timestamps nor confidence still project their whole recognised text
/// as one segment spanning `[Zero, Zero]` with `Confidence = None`.
type TranscriptSegment = {
    Text: string
    Start: TimeSpan
    End: TimeSpan
    Confidence: float option
}

/// The result of a transcription. `Segments` is ordered by `Start`;
/// `Language` is the BCP-47 tag the provider detected or was told to use,
/// when reported.
type Transcript = {
    Segments: TranscriptSegment list
    Language: string option
}

module TranscriptSegment =
    /// A whole-utterance segment with no timing / confidence — the shape a
    /// provider that returns only flat text projects into.
    let ofText (text: string) : TranscriptSegment = {
        Text = text
        Start = TimeSpan.Zero
        End = TimeSpan.Zero
        Confidence = None
    }

module Transcript =
    /// The empty transcript — no segments, no detected language. The
    /// identity a provider returns for silent / empty audio (rather than
    /// an error — empty input is a valid, if uninteresting, result).
    let empty: Transcript = { Segments = []; Language = None }

    /// Build a single-segment transcript from flat recognised text.
    let ofText (text: string) : Transcript = {
        Segments = [ TranscriptSegment.ofText text ]
        Language = None
    }

    /// Plain-text projection — the canonical "just give me the words"
    /// view for dropping into a text input. Segment texts are trimmed,
    /// empty segments dropped, and the rest joined with single spaces so
    /// the result carries no leading/trailing/double whitespace
    /// regardless of how the provider chunked its segments.
    let plainText (t: Transcript) : string =
        t.Segments
        |> List.map (fun s -> if isNull s.Text then "" else s.Text.Trim())
        |> List.filter (fun s -> s <> "")
        |> String.concat " "

// ─── Failures as data ─────────────────────────────────────────────

/// A transcription failure surfaced as data (portability rule 3 — no
/// callback / exception semantics leak across the interface). The
/// `Transient` / `PermanentClient` split mirrors the AI-provider and
/// embedding-provider taxonomies so a caller's retry logic reads the
/// same regardless of which substrate produced the error.
[<RequireQualifiedAccess>]
type TranscriptionError =
    /// No API key / credential is configured for the provider — a
    /// deployment misconfiguration, not a retry-worthy condition.
    | NotConfigured of detail: string
    /// The provider rejected the request permanently (auth 4xx, bad
    /// request, unsupported language). The identical request fails
    /// identically, so it is never retried.
    | PermanentClient of statusCode: int * detail: string
    /// A transient failure (429 / 5xx / timeout / network). The same
    /// request may succeed on retry.
    | Transient of detail: string
    /// The provider returned a response that could not be parsed into a
    /// `Transcript`.
    | MalformedResponse of detail: string
    /// The supplied audio content type / format is not accepted by this
    /// provider.
    | UnsupportedAudio of contentType: string * detail: string
    /// The provider does not support the streaming session surface — a
    /// caller should fall back to `Transcribe`.
    | StreamingUnsupported of detail: string

module TranscriptionError =
    /// Whether the error is worth retrying with the same request. Only
    /// `Transient` is; every other case is a permanent condition (bad
    /// key, bad audio, unparseable response) that a retry cannot heal.
    let isRetryable (error: TranscriptionError) : bool =
        match error with
        | TranscriptionError.Transient _ -> true
        | _ -> false

    /// A short human-readable description for surfacing in a UI / log.
    let describe (error: TranscriptionError) : string =
        match error with
        | TranscriptionError.NotConfigured detail -> sprintf "transcription provider not configured: %s" detail
        | TranscriptionError.PermanentClient(status, detail) ->
            sprintf "provider rejected the request (HTTP %d): %s" status detail
        | TranscriptionError.Transient detail -> sprintf "transient transcription failure: %s" detail
        | TranscriptionError.MalformedResponse detail -> sprintf "provider returned an unparseable response: %s" detail
        | TranscriptionError.UnsupportedAudio(contentType, detail) ->
            sprintf "audio content type '%s' is not supported: %s" contentType detail
        | TranscriptionError.StreamingUnsupported detail ->
            sprintf "streaming is not supported by this provider: %s" detail

// ─── Request ──────────────────────────────────────────────────────

/// A batch transcription request — the audio bytes, their MIME content
/// type, and an optional language hint. Identity-by-value: no live
/// handles, so the request crosses any process boundary a distributed
/// implementation might impose (portability rule 1).
type TranscriptionRequest = {
    /// The raw audio bytes to transcribe.
    Audio: byte[]
    /// MIME content type of the audio (`"audio/wav"`, `"audio/webm"`,
    /// `"audio/mpeg"`, …). Providers validate what they accept and return
    /// `UnsupportedAudio` otherwise.
    ContentType: string
    /// Optional BCP-47 language hint (`"en-US"`). `None` lets the provider
    /// auto-detect where supported.
    LanguageHint: string option
}

module TranscriptionRequest =
    /// Build a request from a content type + audio bytes, no language hint.
    let create (contentType: string) (audio: byte[]) : TranscriptionRequest = {
        Audio = audio
        ContentType = contentType
        LanguageHint = None
    }

    /// Attach a BCP-47 language hint.
    let withLanguage (language: string) (request: TranscriptionRequest) : TranscriptionRequest = {
        request with
            LanguageHint = Some language
    }