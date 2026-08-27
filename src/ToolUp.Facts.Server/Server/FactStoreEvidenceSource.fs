// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open ToolUp.Platform

// ─── FactStoreEvidenceSource (Phase 520 / 524 wiring) ─────────────────
//
// The concrete adapter that fills the Platform-side `IFactEvidenceSource`
// seam (Phase 524) from an `IFactStore` — the forge pattern: the interface
// lives in `ToolUp.Platform.Server`, the implementation in the companion.
// Composing it lets `ProvenanceGraph.createWithFacts` walk real fact
// nodes (evidence + input data objects + supersession) without
// `ToolUp.Platform.Server` ever depending on the `ToolUp.Facts` companion.

/// Projects an `IFactStore` onto the `IFactEvidenceSource` the provenance
/// graph reads. Scope-bounded (GP 4) — every read forwards the caller's
/// `scopeId` to the store.
type FactStoreEvidenceSource(store: IFactStore) =

    // A stored fact → the flattened evidence the provenance graph needs.
    // Disclosure / subject rendering reuse the shared renderers so the
    // provenance surface matches the audit trail exactly.
    let toEvidence (f: Fact) : FactEvidence = {
        FactId = f.FactId
        Subject = SubjectRef.toString f.Subject
        Metric = f.Metric.Value
        Disclosure = Disclosure.toString f.Disclosure
        ResultRef = f.Evidence.ResultRef
        InputHashes = f.Evidence.InputHashes
        Supersedes = f.Supersedes
    }

    interface IFactEvidenceSource with
        member _.GetFact(scopeId: string, factId: string) : Async<FactEvidence option> = async {
            let! fact = store.Get(scopeId, factId)
            return fact |> Option.map toEvidence
        }

        member _.FactsForResult(scopeId: string, resultId: string) : Async<FactEvidence list> = async {
            // Include superseded facts: "what was computed from this result"
            // is a provenance question, so a since-corrected fact still
            // belongs in the chain.
            let! facts =
                store.Query(
                    scopeId,
                    {
                        FactQuery.all with
                            IncludeSuperseded = true
                    }
                )

            return
                facts
                |> List.filter (fun f -> f.Evidence.ResultRef = Some resultId)
                |> List.map toEvidence
        }

/// Construction for `FactStoreEvidenceSource`.
module FactStoreEvidenceSource =

    /// Adapt an `IFactStore` to the provenance graph's `IFactEvidenceSource`
    /// seam.
    let create (store: IFactStore) : IFactEvidenceSource =
        FactStoreEvidenceSource(store) :> IFactEvidenceSource