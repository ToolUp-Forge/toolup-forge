// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open System
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Phase 6q follow-up — AI-side citation normaliser seam ───────────
//
// `AIAssistantHandler.fs` lives in `ToolUp.AI.Server`, the citation
// normaliser substrate lives in `ToolUp.RAG.Server`. RAG depends on
// AI; AI must NOT `open ToolUp.RAG.*`. The seam below lets the AI
// handler invoke a RAG-supplied normaliser without taking a build
// dependency on RAG.
//
// **Resolution model.** `RAGCompose.serviceConfig` registers a
// concrete `ICitationNormaliser` (closing over the active
// `RagCitationPolicy`) when RAG composes. Non-RAG deployments leave
// the DI slot empty; `AIAssistantHandler` resolves `null`, skips
// normalisation, and persists the model's emitted text unchanged
// (byte-for-byte pre-Phase-6q behaviour).
//
// **Why two interfaces.** `ICitationNormaliser` is the per-turn
// invocation; `ICitationCounters` is the rolling-window observation
// store for the `/dev/rag-citation` dev endpoint. Keeping them
// separate lets the dev endpoint read counters without invoking the
// normaliser, and lets a deployment swap either side without
// touching the other.
//
// **Type shape — defined seam-side.** `CitationEvent` /
// `CitationNormalisation` / `CitationCounterRow` are AI-side types
// so the AI handler doesn't traffic RAG-private records. The
// RAG-side implementation maps its native `NormaliseResult` /
// `CitationEvent` (from `CitationNormaliser.fs`) onto these shapes
// at the seam boundary.

// ─── Result shape ─────────────────────────────────────────────────────

/// Action attributed to a single recognised drift variant. Mirrors
/// the RAG-side `CitationAction` shape but defined here so the AI
/// handler reads only AI-side types.
type CitationAction =
    /// Variant normalised onto the canonical `[¹]` / `[²]` / …
    /// marker because the digit binds to a real retrieved source.
    | NormalisedToCanonical of sourceIndex: int
    /// Variant stripped silently under `LenientNormalise`.
    | StrippedPhantom
    /// Variant replaced with `[unverified]` under `Strict`.
    | UnverifiedTagged

/// One recognised drift-variant + the action taken. Emitted to
/// `IAuditLog` per-event so operators can correlate model behaviour
/// with prompt changes.
type CitationEvent = {
    /// The exact substring the regex matched — `"(1)"`, `"Source 2"`,
    /// `"²"`, `"^3"`. Cheap to render in audit payloads + dev
    /// endpoint snapshots.
    Variant: string
    /// 1-based digit parsed out of the variant.
    Digit: int
    /// What the normaliser did with this match.
    Action: CitationAction
}

/// Output of a single `ICitationNormaliser.Normalise` invocation.
/// `Events` carries the per-variant detail; the aggregate counters
/// are derived sums kept for callers that want a turn-level rollup
/// without iterating `Events`.
type CitationNormalisation = {
    /// The rewritten assistant text — drift variants normalised /
    /// stripped / tagged per the active policy. Equal to the input
    /// when the policy short-circuits.
    Text: string
    /// Per-variant detail for audit / dev-endpoint emission.
    Events: CitationEvent list
    /// Number of events where `Action = NormalisedToCanonical _`.
    Normalisations: int
    /// Number of events where `Action ∈ { StrippedPhantom;
    /// UnverifiedTagged }`. UnverifiedTagged is double-counted as
    /// both a strip + a tag (matches the substrate contract).
    Strips: int
    /// Number of events where `Action = UnverifiedTagged`.
    UnverifiedTags: int
}

// ─── Normaliser seam ──────────────────────────────────────────────────

/// AI-side seam for the RAG-supplied post-stream citation
/// normaliser. The implementation closes over the active
/// `RagCitationPolicy` (configured by `RAGServerApp.withCitationPolicy`)
/// + the per-deployment counter store, and emits one
/// `CitationEvent` per recognised drift variant.
///
/// Resolved from `HttpContext.RequestServices`; deployments that
/// don't register an implementation leave the slot empty and the AI
/// handler skips the pass.
type ICitationNormaliser =
    abstract member Normalise:
        sources: RetrievedSource list * text: string * providerName: string * model: string -> CitationNormalisation

// ─── Counter store ────────────────────────────────────────────────────

/// Snapshot row for one (provider, model) bucket in the rolling
/// window the `/dev/rag-citation` endpoint reads. Wire-shape-
/// friendly (every field is a primitive) so the dev-endpoint JSON
/// renders without DUs.
type CitationCounterRow = {
    ProviderName: string
    ProviderModel: string
    TotalTurns: int
    Normalisations: int
    Strips: int
    UnverifiedTags: int
    /// Recent rewrites surfaced for operator inspection. Capped to
    /// a small per-bucket sample so the endpoint stays cheap.
    RecentRewrites: CitationRecentRewrite list
}

and CitationRecentRewrite = {
    OccurredAt: DateTime
    EventCount: int
    SampleVariants: string list
}

/// Rolling-window store of normaliser observations. The RAG-side
/// implementation accumulates one record per `Normalise` invocation;
/// the `/dev/rag-citation` handler reads `Snapshot`. Bounded internally
/// — a fixed-size ring buffer is the canonical implementation. AI-side
/// default (when RAG isn't composed) is a no-op so callers always
/// resolve a working instance.
type ICitationCounters =
    abstract member Record: providerName: string * model: string * result: CitationNormalisation -> unit

    abstract member Snapshot: unit -> CitationCounterRow list

/// Default no-op counter store. Records are silently discarded;
/// `Snapshot` returns `[]`. Used when an AI deployment hasn't
/// composed RAG so the dev-endpoint compiles + returns an empty
/// snapshot rather than crashing on a null lookup.
type NoOpCitationCounters() =
    interface ICitationCounters with
        member _.Record(_, _, _) = ()
        member _.Snapshot() = []