// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 650 — the gated entry point to the chart export bundle.
///
/// [`ChartExportBundle.ofDocument`](../ToolUp.Reporting.Core/ChartExportBundle.fs)
/// is pure and knows nothing about egress policy, which is right for a
/// Core function and wrong for the surface a deployment calls. A bundle
/// leaves the deployment: it carries the document's prose, its quoted
/// numbers, and rendered pictures of its series, to a tier whose whole
/// job is to publish them. That is the same egress event a rendered
/// report is, so it goes through the same door.
///
/// Concretely: `createWithDisclosureGate` runs
/// `ReportApiHandler.applyExportDisclosure` — the one `FactExport` door
/// the render path runs, called rather than reproduced — and pairs the
/// artifacts with the DISCLOSED document. Two consequences worth stating
/// because they are the point:
///
///   * a fact this principal may not egress is redacted in the bundle's
///     document exactly as it would be in a rendered report, and the
///     withheld-values note travels with it, so the export tier cannot
///     re-publish what the door refused;
///   * the bundle's keys index its own (disclosed) document, so the
///     pairing a consumer relies on holds after the door ran, not before
///     it.
///
/// The ungated `create` is the honest counterpart, not a loophole: a
/// deployment that composes no fact tier has no gate to consult and pays
/// nothing for the door (GP 13) — the same posture `ReportApiHandler`
/// takes with `create` / `createWithDisclosureGate`.
module ToolUp.Reporting.NarrativeExportBundle

open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Reporting

/// Pair a document with its rendered chart artifacts, with no disclosure
/// gate — for a deployment that composes no fact tier. Byte-for-byte
/// `ChartExportBundle.ofDocument`; named here so the gated and ungated
/// postures sit side by side and a reader chooses deliberately.
let create (renderer: ChartArtifactRenderer) (document: NarrativeDocument) : ChartExportBundle =
    ChartExportBundle.ofDocument renderer document

/// Pair a document with its rendered chart artifacts, with the fact
/// disclosure export door engaged.
///
/// Every fact ref the document cites is checked through the supplied gate
/// at the `FactExport` surface BEFORE the pairing: denied values are
/// redacted to the policy-naming marker and a withheld-values section is
/// appended, and the artifacts are rendered from — and keyed against —
/// that disclosed document. `principal` is the resolved caller the gate
/// audits denies against; resolve it upstream alongside `scopeId`, as the
/// report handler does.
///
/// A document citing no facts never consults the gate, so the async is
/// the only cost a fact-free document pays.
let createWithDisclosureGate
    (gate: IFactDisclosureGate)
    (principal: string)
    (renderer: ChartArtifactRenderer)
    (scopeId: string)
    (document: NarrativeDocument)
    : Async<ChartExportBundle> =
    async {
        let! disclosed = ReportApiHandler.applyExportDisclosure gate principal scopeId document
        return ChartExportBundle.ofDocument renderer disclosed
    }