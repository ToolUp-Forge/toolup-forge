// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module KnowledgeBase.ServerApiNarrativeIngestor

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Narrative
open SharedTypes
open KnowledgeBase.ServerApiDeps

// ─── The programmatic narrative-commit door (Phase 707) ──────────────
//
// `INarrativeIngestor` (declared in `ToolUp.Platform.Server`) implemented
// over the ordinary ingestion path. This is deliberately the thinnest
// possible adapter: it builds the same `KnowledgeApiDeps` the request
// path builds — from the same DI container, with the scope and principal
// handed in rather than read off an `HttpContext` — and calls the same
// `ingestNarrative`.
//
// **Everything that makes the interactive commit safe therefore applies
// here unchanged**, because it is the same function: the Phase 525.D
// disclosure egress check refuses a document citing facts the acting
// principal may not publish, the provenance dedup collapses repeated
// commits onto one document, the Phase 521.D chunk metadata stamps the
// cited fact ids, and the overwrite path sheds the orphan chunk tail.
// The temptation on a background path is to reach past the API for the
// storage and the vector store directly, and that is exactly how two
// paths that must agree stop agreeing.
//
// `Overwrite = true` is not a parameter. The seam's contract is
// replace-under-the-same-provenance (see `INarrativeIngestor.Ingest`),
// and a programmatic producer that accumulated documents under one key
// would grow the corpus without bound.

/// Implementation of the request-free commit door over the composed
/// knowledge-base substrate. Holds only the root `IServiceProvider`;
/// every dependency is resolved per call, so nothing is captured across
/// invocations (GP 12 rule 4).
type KnowledgeNarrativeIngestor(services: IServiceProvider) =

    interface INarrativeIngestor with

        member _.Ingest
            (scope: StorageScope, principal: string, document: NarrativeDocument)
            : Async<NarrativeIngestOutcome> =
            async {
                // Probed rather than assumed: on a deployment composing the
                // knowledge-base companion without storage, the deps record
                // would hold a null and the first index read would throw
                // from inside the ingestion path. A named failure at the
                // door beats a `NullReferenceException` attributed to
                // whatever the caller was doing.
                match services.GetService(typeof<IBlobStorage>) with
                | :? IBlobStorage ->
                    // The document must carry provenance — the ingestion
                    // path refuses one without it, and the refusal is much
                    // clearer raised here where the producer can be named.
                    match document.Provenance with
                    | None ->
                        return
                            NarrativeIngestRefused
                                "The document carries no NarrativeProvenance. A programmatic commit must stamp one: its ModuleId + SettingsKey are the dedup key that makes the commit a replacement rather than an accumulation."
                    | Some _ ->
                        try
                            let deps = KnowledgeApiDeps.resolveFrom services (Some scope) principal

                            let! outcome =
                                KnowledgeBase.ServerApiNarrative.ingestNarrative deps {
                                    Document = document
                                    Overwrite = true
                                }

                            match outcome with
                            | Ok doc -> return NarrativeIngested doc.Id
                            | Error MissingProvenance ->
                                return
                                    NarrativeIngestRefused
                                        "The ingestion path reported missing provenance for a document that carries it — the knowledge API and this door disagree about the document shape."
                            | Error(DuplicateExists existing) ->
                                // Unreachable while `Overwrite = true` (the
                                // duplicate branch is what overwrite skips).
                                // Reported rather than assumed away: if it
                                // ever fires, the overwrite contract above
                                // has been broken and the message says so.
                                return
                                    NarrativeIngestRefused(
                                        sprintf
                                            "A document already exists under this provenance (%s) and the overwrite did not take effect."
                                            existing.FileName
                                    )
                            // The one `IngestFailed` the path produces is
                            // the Phase 525.D disclosure refusal, which
                            // names the offending fact refs and their
                            // policies and never their values. It is an
                            // ordinary outcome, not a fault.
                            | Error(IngestFailed reason) -> return NarrativeIngestRefused reason
                        with ex ->
                            return NarrativeIngestFailed(sprintf "Narrative ingestion faulted: %s" ex.Message)
                | _ ->
                    return
                        NarrativeIngestFailed
                            "No IBlobStorage is composed in this deployment, so the knowledge base has nowhere to persist a narrative."
            }

/// Build the ingestor over the composed container. Registered by the
/// knowledge-base companion's compose; consumers reach it through DI as
/// an `INarrativeIngestor` and never construct one.
let create (services: IServiceProvider) : INarrativeIngestor =
    KnowledgeNarrativeIngestor(services) :> INarrativeIngestor