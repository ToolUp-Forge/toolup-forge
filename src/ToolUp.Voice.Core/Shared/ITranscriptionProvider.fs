// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Voice

open System

// ─── Streaming session surface ────────────────────────────────────

/// A streaming recognition result. `Partial` hypotheses are unstable —
/// feed them to the UI as provisional text but do not commit them; a
/// later event may revise or finalise the span. `Final` carries a stable
/// `TranscriptSegment` safe to commit.
[<RequireQualifiedAccess>]
type StreamingHypothesis =
    | Partial of text: string
    | Final of segment: TranscriptSegment

/// An open streaming-transcription session. The session is a *local*
/// resource (identity-by-value at the interface — no live handle crosses
/// it): audio chunks are pushed in, `Partial` / `Final` hypotheses arrive
/// via the callback supplied to `OpenSession`, and `Complete` drains the
/// final transcript. Disposing the session tears down the underlying
/// transport without waiting for a final result (use `Complete` for a
/// graceful end-of-audio).
///
/// The `onHypothesis` callback passed to `OpenSession` is synchronous —
/// a documented exemption to portability rule 2 mirroring the
/// `IAIProvider.SendMessage` `onStream` callback: the session methods are
/// `Async`, per-hypothesis delivery stays synchronous by design.
type ITranscriptionSession =
    inherit IDisposable
    /// Push a chunk of captured audio into the open session. Chunks are
    /// the same codec/content type declared when the session opened.
    abstract PushAudio: chunk: byte[] -> Async<Result<unit, TranscriptionError>>
    /// Signal end-of-audio and await the assembled final transcript.
    abstract Complete: unit -> Async<Result<Transcript, TranscriptionError>>

// ─── Provider interface ───────────────────────────────────────────

/// Abstraction over any speech-to-text provider (Azure AI Speech, OpenAI
/// Whisper, a browser Web-Speech shim, …). Implementations are companion
/// packages under `src/VoiceProviders/`; the interface carries no
/// provider-specific types or dependencies.
///
/// Satisfies the six portability rules (GP 12): identity-by-value
/// (`TranscriptionRequest` / `Transcript` are records over primitives, no
/// live handles), async at every boundary, failures as data
/// (`TranscriptionError`), stateless between `Transcribe` calls (each
/// call derives its result from its parameters + the credential read
/// per-call), no cross-shard ordering promises (each request is
/// independent), and no timing primitives to declare a precision for.
/// See `TECHNICAL_GUIDE.md` for the full audit.
type ITranscriptionProvider =
    /// Stable provider-family identifier — `"azure-speech"`, `"whisper"`,
    /// `"web-speech"`. Must not change for the lifetime of the instance.
    abstract ProviderId: string

    /// Whether this provider implements the streaming session surface. A
    /// provider that returns `false` here returns
    /// `Error (StreamingUnsupported _)` from `OpenSession`; callers should
    /// gate on this flag and fall back to `Transcribe`.
    abstract SupportsStreaming: bool

    /// Batch transcription: audio in, transcript out, failures as data.
    /// The whole audio is transcribed in a single call — suited to a
    /// `MediaRecorder` clip captured client-side and uploaded, or any
    /// short-audio file. Returns `Ok transcript` on success (an empty
    /// transcript for silent audio), `Error TranscriptionError` otherwise.
    abstract Transcribe: request: TranscriptionRequest -> Async<Result<Transcript, TranscriptionError>>

    /// Open a streaming session for low-latency, incremental recognition.
    /// `onHypothesis` receives `Partial` / `Final` hypotheses as they
    /// arrive; `languageHint` is an optional BCP-47 tag. Returns
    /// `Error (StreamingUnsupported _)` when `SupportsStreaming` is
    /// `false`. The caller owns the returned session's lifetime (dispose
    /// it, or call `Complete` for a graceful end-of-audio).
    abstract OpenSession:
        onHypothesis: (StreamingHypothesis -> unit) * languageHint: string option ->
            Async<Result<ITranscriptionSession, TranscriptionError>>

module ITranscriptionProvider =
    /// Default `OpenSession` implementation for providers that do not
    /// support streaming — returns `Error (StreamingUnsupported _)` so a
    /// batch-only provider implements the member in one line:
    ///
    ///   member _.OpenSession(_, _) =
    ///       ITranscriptionProvider.streamingUnsupported "whisper"
    let streamingUnsupported (providerId: string) : Async<Result<ITranscriptionSession, TranscriptionError>> = async {
        return
            Error(
                TranscriptionError.StreamingUnsupported(
                    sprintf "provider '%s' supports batch transcription only; use Transcribe" providerId
                )
            )
    }