// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.OriginalDocumentBridge

open ToolUp.Platform.VectorKnowledgeTypes

// ─── "View original" cross-companion bridge (Phase 102–107 tail) ──────
//
// A retrieved citation can advertise a fetchable original document via
// `RetrievedSource.OriginalRef` (Phase 103). The Sources panel that
// renders citations lives in the AI client; the surface that can
// actually fetch + open the original lives in the KnowledgeBase
// companion (it owns the `GetOriginalDocument` API, the document types,
// and the scope-gated retrieval surface). Neither companion references
// the other — so the affordance is brokered here, in `ToolUp.Platform`,
// exactly as `NarrativeCommit` brokers the reverse "Save to Knowledge
// Base" direction.
//
// The KnowledgeBase companion registers a concrete opener at compose
// time (when its `register()` runs); the AI Sources panel reads
// `current ()` per render to decide whether to show the affordance and
// invokes it on click. A deployment without a Knowledge Base never
// registers an opener, so `current ()` stays `None` and the panel
// renders exactly as it did before this release (GP 11 / GP 13).
//
// Sanctioned mutable global — same precedent as `NarrativeCommit`,
// `NavigationRequest`, `NotificationClient.state`. Per-tab singleton
// with effectively-final lifetime: a single registration at startup,
// read on every render of the Sources panel.

/// Fetch the original document behind a citation's
/// `OriginalDocumentRef` through the producer's scope-gated retrieval
/// surface and open it for the user (new-tab blob view / download),
/// honouring `ref.Location` as a deep-link target where the surface
/// supports it. Returns `Ok ()` once the original has been opened, or
/// `Error <benign message>` when it is unavailable (out-of-scope, no
/// retrievable original, or a fetch error). Never throws — absence and
/// denial are results, not exceptions (GP 9). The benign message is
/// safe to show inline: an out-of-scope id is reported as a generic
/// "not available" so the affordance can't be used as an existence
/// oracle (GP 4).
type OriginalDocumentOpener = OriginalDocumentRef -> Async<Result<unit, string>>

/// Per-tab cache of the producer-supplied opener. Its sole writer is
/// the KnowledgeBase companion's `register()`; the AI Sources panel is
/// read-only against it.
let mutable private installed: OriginalDocumentOpener option = None

/// Register the producer's "view original" opener. Called once when the
/// producing companion is composed into the app's module list.
/// Idempotent (last-writer-wins) — re-registration only happens if a
/// test harness re-runs the compose sequence.
let register (opener: OriginalDocumentOpener) : unit = installed <- Some opener

/// The currently-registered opener, if any. The Sources panel reads
/// this to decide whether to render the "view original" affordance.
/// `None` when no producing companion is composed in.
let current () : OriginalDocumentOpener option = installed