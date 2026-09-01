// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text

// ─── Deploy-record retirement (Phase 678) ────────────────────────────
//
// A deployment that ends is, today, a deployment that stops answering.
// Its sealed deploy record (Phase 656) stays perfectly verifiable, its
// composition binding (Phase 657) stays perfectly checkable, and nothing
// anywhere says the deployment is OVER — so a record recovered from a
// backup, or a container restarted from an archived image, boots and
// verifies exactly as it did on its first day. For an engagement-scoped
// deployment that is precisely wrong: the buyer's question is not "was
// this deployed correctly" but "is this gone, and can you prove it".
//
// A **retirement** is that proof's substrate half: a reference binding
// one sealed deploy record to the terminal op that closed its audit
// ledger, naming who decommissioned it and when. It is a claim about a
// deployment's END, in the same shape and with the same discipline as
// Phase 656's claim about its beginning.
//
// **A companion record, not a widening — the third time this file family
// has made the same call, and for the same reason.** A `Retirement` field
// on `DeployRecord` would retype its constructor and break every consumer
// that builds one literally, for the benefit of consumers that fill it;
// worse here than in Phase 656, because it would also change
// `DeployRecord.canonicalForm` and therefore invalidate every seal ever
// minted. As a separate record that EMBEDS the sealed record, an existing
// deployment is untouched, every existing seal keeps verifying, and
// retirement is a thing a deployment starts doing rather than a thing it
// is retyped into (GP 11 / GP 13).
//
// **What this file knows about ledgers: nothing.** The terminal op lives
// in the chained-ledger companion, above this tier, and reaches here as
// two opaque lowercase-hex digests. That direction is the only one GP 1
// permits — a Core type referencing a companion's type would nail every
// deployment to that companion — and it costs nothing, because a digest
// is exactly what a binding needs.
//
// **What a retirement does NOT prove**, stated here rather than left to
// be inferred. It proves that someone holding the signing key asserted,
// at a recorded time, that this record's ledger was closed at a named
// head. It does not prove the deployment's data was destroyed, that no
// copy of the image survives, or that the deployment stopped serving —
// those are operational facts no signature over a digest can reach. What
// it does give a relying party is a decommission claim that is
// attributable, tamper-evident, and refuseable at the next boot.

/// Phase 678 — the retirement reference for one sealed deploy record.
///
/// Every field is a primitive or a string, so the record crosses any
/// boundary and round-trips through any serialiser (GP 12 rule 1). The
/// two timestamps in this family are strings for the same reason
/// `LedgerRecord.OccurredAt` is: the value is framed into a digest and
/// compared, never arithmetic'd, and a string cannot disagree with the
/// bytes some other tier framed.
type DeployRetirement = {
    /// Schema version of the retirement shape. Bumped only when the
    /// canonical form changes, which by construction invalidates every
    /// existing seal over it.
    SchemaVersion: int
    /// Lowercase-hex digest of the canonical bytes of the `DeployRecord`
    /// being retired (`DeployRecords.canonicalBytes` hashed by
    /// `DeployRecords.digestBytes`) — the same identity Phase 657's
    /// composition binding uses, so a retirement minted for one deploy
    /// cannot be presented alongside another.
    DeployRecordDigest: string
    /// Digest of the terminal op that closed the deployment's audit
    /// ledger. Opaque here: this tier neither computes nor interprets it.
    TerminalOpDigest: string
    /// The ledger head the terminal op closed at.
    LedgerHeadDigest: string
    /// The number of records in the closed chain. Carried beside the
    /// digest for the reason Phase 658 signs the pair: a head digest alone
    /// cannot distinguish a chain from a truncation of it.
    LedgerRecordCount: int64
    /// The decommissioning actor, as the deployment names it. Free-form:
    /// the substrate does not enumerate actors, and recording an identity
    /// this SDK has never heard of is the expected case.
    RetiredBy: string
    /// When the deployment was decommissioned, as an invariant round-trip
    /// string.
    RetiredAt: string
    /// Why, in the operator's own words. Free-form and never
    /// interpreted — but framed into the canonical form, so it cannot be
    /// edited after the fact under a seal that still verifies.
    Reason: string
}

/// A sealed deploy record together with its retirement. What a relying
/// party is handed for a deployment that has ended; the sealed record
/// inside is unchanged and verifies exactly as it did before.
type RetiredDeployRecord = {
    Record: SealedDeployRecord
    Retirement: DeployRetirement
}

[<RequireQualifiedAccess>]
module DeployRetirement =

    /// Schema version this build of the substrate emits.
    [<Literal>]
    let SchemaVersion = 1

    /// Framing version, prefixed to the retirement's canonical form.
    /// Bumping it invalidates every existing seal by construction.
    [<Literal>]
    let FramingVersion = "toolup.deployretirement.v1"

    /// Build a retirement at the current schema version.
    let create
        (deployRecordDigest: string)
        (terminalOpDigest: string)
        (ledgerHeadDigest: string)
        (ledgerRecordCount: int64)
        (retiredBy: string)
        (retiredAt: string)
        (reason: string)
        : DeployRetirement =
        {
            SchemaVersion = SchemaVersion
            DeployRecordDigest = deployRecordDigest
            TerminalOpDigest = terminalOpDigest
            LedgerHeadDigest = ledgerHeadDigest
            LedgerRecordCount = ledgerRecordCount
            RetiredBy = retiredBy
            RetiredAt = retiredAt
            Reason = reason
        }

    /// The canonical text a retirement's seal is taken over.
    ///
    /// Length-framed field by field with the same injective scheme Phase
    /// 656 uses, and for the same reason: without it two distinct
    /// retirements could canonicalise to the same text by concatenation,
    /// and a signature over them would be meaningless.
    let canonicalForm (retirement: DeployRetirement) : string =
        let builder = StringBuilder()
        let frame = ProvenanceFraming.frame builder

        frame FramingVersion
        frame (string retirement.SchemaVersion)
        frame retirement.DeployRecordDigest
        frame retirement.TerminalOpDigest
        frame retirement.LedgerHeadDigest
        frame (string retirement.LedgerRecordCount)
        frame retirement.RetiredBy
        frame retirement.RetiredAt
        frame retirement.Reason

        builder.ToString()

    /// Whether this retirement is the retirement OF the record digesting
    /// to `deployRecordDigest`.
    ///
    /// Case-insensitive on hex, matching every other digest comparison in
    /// this substrate. A retirement that names a different record is not a
    /// weaker claim about this one — it is a claim about something else,
    /// and the caller must be able to say so.
    let bindsRecord (deployRecordDigest: string) (retirement: DeployRetirement) : bool =
        String.Equals(retirement.DeployRecordDigest, deployRecordDigest, StringComparison.OrdinalIgnoreCase)

    /// Whether this retirement is bound to the ledger head named by
    /// `headDigest` at `recordCount`.
    ///
    /// Both halves are checked because both are inside Phase 658's signed
    /// head bytes: a retirement naming the right digest at the wrong count
    /// is bound to a chain nobody wrote.
    let bindsHead (headDigest: string) (recordCount: int64) (retirement: DeployRetirement) : bool =
        String.Equals(retirement.LedgerHeadDigest, headDigest, StringComparison.OrdinalIgnoreCase)
        && retirement.LedgerRecordCount = recordCount

    /// One rendered line naming what was retired, by whom, when, and at
    /// which head. The operator-facing form of the whole record — a
    /// refusal an operator cannot read the reason for is a refusal they
    /// will disable.
    let describe (retirement: DeployRetirement) : string =
        let reason =
            if String.IsNullOrWhiteSpace retirement.Reason then
                "no reason recorded"
            else
                retirement.Reason

        $"deploy record {retirement.DeployRecordDigest} was retired by '{retirement.RetiredBy}' at {retirement.RetiredAt} ({reason}); its audit ledger was closed at head {retirement.LedgerHeadDigest} after {retirement.LedgerRecordCount} record(s), by terminal op {retirement.TerminalOpDigest}"