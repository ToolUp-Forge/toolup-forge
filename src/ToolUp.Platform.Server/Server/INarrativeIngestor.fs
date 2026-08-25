// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open ToolUp.Platform.Narrative

// ─── INarrativeIngestor (Phase 707) ──────────────────────────────────
//
// The **programmatic** entry point onto the narrative-ingestion path.
//
// Until now a `NarrativeDocument` reached the knowledge base exactly one
// way: a user pressed "Save to KB" and the client called the knowledge
// API's `IngestNarrative` over the wire. That is the right door for a
// document a person just looked at, and the only door there was — so a
// server-side producer with a document to commit had no door at all.
//
// The producer this seam exists for is the coverage narrative (Phase
// 707): a small, registry-bounded set of documents describing what the
// fact base HOLDS, regenerated when its coverage materially moves and
// committed with no user in the loop. It has no request, no session and
// no client; what it has is a resolved storage scope, an acting
// principal, and a document.
//
// **Why an interface here rather than a project reference.** The
// producer lives in the fact companion and the ingestion path lives in
// the knowledge-base companion, and neither references the other — the
// same shape `IFactDisclosureGate` already takes in the opposite
// direction (the fact companion registers it; the knowledge base
// consumes it to refuse a commit). Both companions reference this
// assembly, so the seam belongs here and the coupling stays a DI lookup.
//
// **What an implementation MUST preserve.** This is a door onto the
// *existing* path, not a second path beside it. An implementation
// routes through whatever the interactive commit routes through, so the
// disclosure egress check (Phase 525.D), the provenance dedup, the
// chunk metadata stamping (Phase 521.D) and the orphan-tail cleanup all
// apply unchanged. A refusal the interactive path would have produced
// is reported here as `NarrativeIngestRefused`, never swallowed.

/// What one programmatic narrative commit did. Deliberately three cases
/// rather than `Result<string, string>`: a refusal is an ORDINARY
/// outcome on this path (a coverage narrative for a metric the acting
/// principal may not see is refused by design), while an ingestion
/// FAILURE is a fault. Collapsing them would make a working
/// default-deny indistinguishable from a broken deployment in a log
/// line.
type NarrativeIngestOutcome =
    /// Committed. Carries the knowledge document's id — the same id the
    /// interactive path returns, so a caller can correlate a later
    /// retrieval back to the commit that produced it.
    | NarrativeIngested of documentId: string
    /// The ingestion path refused. Carries the refusal diagnostic
    /// verbatim (e.g. the Phase 525.D disclosure refusal, which names
    /// the offending fact refs and their policies but never their
    /// values).
    | NarrativeIngestRefused of reason: string
    /// The commit could not be attempted or did not complete — no
    /// knowledge base composed, storage unavailable, an unexpected
    /// fault. Distinct from a refusal: nothing decided against this
    /// document, something prevented the decision.
    | NarrativeIngestFailed of reason: string

/// Server-side, request-free narrative commit. Registered by the
/// knowledge-base companion's compose when a deployment composes a
/// knowledge base at all; absent otherwise, which is what makes every
/// producer of programmatic narratives dormant by construction on a
/// deployment with nowhere to put them (GP 13).
///
/// GP 12 audit: `scope` and `principal` are plain values, the single
/// method is `Async<T>`, no callbacks, no state is held between calls,
/// and no ordering is promised across scopes.
type INarrativeIngestor =
    /// Commit `document` into `scope`'s knowledge base as `principal`,
    /// **replacing** any document already stored under the same
    /// `NarrativeProvenance` (`ModuleId` + `SettingsKey`). Replacement
    /// rather than duplication is the contract, not a parameter: a
    /// programmatic producer that wanted a second copy would key it
    /// differently, and one that keyed identically and accumulated
    /// would grow the corpus without bound.
    ///
    /// `scope` is the caller's ALREADY-RESOLVED storage scope. An
    /// implementation never mints a container from a scope id (see the
    /// scope-container contract on `IBlobStorage`) — it is handed one.
    ///
    /// A document with no `Provenance` is refused, exactly as the
    /// interactive path refuses it.
    abstract Ingest:
        scope: StorageScope * principal: string * document: NarrativeDocument -> Async<NarrativeIngestOutcome>