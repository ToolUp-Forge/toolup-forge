// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AuditSinks.LedgerScopedExport

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AuditSinks.ChainedLedger
open ToolUp.Platform.AuditSinks.LedgerChain
open ToolUp.Platform.BlobStorage

// ─── The segment of the chain one party is entitled to ──────────────────
//
// The ledger is a single chain written by one deployment on behalf of
// several contributing parties. Each party wants its own segment: the
// records its scope entitles it to, verifiable in its own hands, archived
// on its own terms. The naive answer — filter the records and hand over
// the survivors — destroys exactly the property the chain existed to
// provide. A filtered list of records does not chain: every elision
// breaks a link, so the party cannot tell a legitimate omission from a
// deletion, and the exporter can drop anything it finds inconvenient.
//
// **So the export is not a filtered list; it is the WHOLE chain with the
// out-of-scope records replaced by witnesses.** Every position in the
// source chain appears, in order. An in-scope position appears in full.
// An out-of-scope position appears as its sequence, its digest, and the
// facets it was tagged with — enough for the verifier to walk the links
// through it, and not the record. The chain evidence therefore survives
// the filtering, which is what makes completeness-within-scope provable
// rather than asserted.
//
// **What the export discloses about records it withholds, stated
// plainly.** A holder learns how many records exist, where the withheld
// ones sit, each one's digest, and each one's facet labels. It does not
// learn any record's content. The digest is preimage-resistant, so it
// discloses nothing directly — with the honest caveat that a digest is a
// confirmation oracle for a guessed record, which matters only where a
// record body is low-entropy and the guesser already knows the ledger's
// canonical framing.
//
// **Why the facet labels must travel, and what they cost.** Without them
// a selective omission is silent: an exporter downgrades an in-scope
// record to a withheld witness and the chain still walks perfectly.
// With them the verifier refuses — an entry withheld under a facet the
// holder's scope names is a scope violation, reported by position. The
// price is that classification labels leak while content does not, and
// that is the trade made deliberately. Note the limit exactly: a
// withheld entry's facets are ASSERTED by the exporter, not proved to a
// holder who cannot recompute the digest. They are nonetheless bound —
// the digest commits to them (`LedgerChain.canonicalBytes`) — so a false
// claim is falsifiable by anyone who can obtain the record, including
// another party whose own scope covers it. Detectability by an auditor,
// then, not unilateral proof by the recipient.
//
// **A rejected design, recorded because it looks better than it is.**
// A withheld witness could carry every field except `Payload`, plus the
// payload's digest, letting the holder recompute the record digest
// itself and closing the assertion gap above. It would require the chain
// to frame `digest(payload)` rather than the payload — changing every
// digest in every ledger already written, and invalidating every head
// signature already taken. A property the deployment does not have today
// is not worth a migration that breaks the property it does.
//
// **No new cryptography (GP 1).** SHA-256 for the content id, through
// the envelope module's own helper; signing is `IStatementEnvelopeSigner`,
// the seam the rest of this SDK's statements are signed through; the
// ledger head's signature is Phase 658's and is carried verbatim, never
// re-taken. This module adds a shape, not a primitive.
//
// **Nothing here is reachable unless a deployment asks for it (GP 13).**
// No composition changes, no DI registration, no route: a deployment
// that never exports pays nothing, and one that never tags a record
// exports the honest answer that no party is entitled to anything.

// ── 677.A — the scope model ─────────────────────────────────────────────

/// What one party is entitled to see: its identity, and the facet
/// vocabulary its entitlement is expressed in.
///
/// **Deployment-configured, and only the facets are load-bearing.** The
/// contributor scopes a multi-party disclosure policy already declares
/// are the natural source for the facet names where a deployment has
/// them; a deployment that has none names its own. Nothing here depends
/// on where the names came from, which is what keeps this usable in the
/// general case.
type PartyScope = {
    /// The party this scope belongs to. Recorded on the export and
    /// checked against the holder's own claim at verification time, so a
    /// correctly-formed export for a DIFFERENT party is refused rather
    /// than read.
    PartyId: string
    /// The facets this party is entitled to. A record is in scope iff it
    /// carries at least one of them.
    ///
    /// **The empty set entitles nothing.** Fail-closed in both
    /// directions: an untagged record is visible to no scope, and a
    /// scope naming no facet sees no record. Neither absence reads as
    /// "everything".
    Facets: Set<string>
}

module PartyScope =
    /// A scope over a facet list, deduplicated by the set.
    let create (partyId: string) (facets: string seq) : PartyScope = {
        PartyId = partyId
        Facets = Set.ofSeq facets
    }

    /// The scope's facets in canonical order — what an export declares.
    let facetList (scope: PartyScope) : string list =
        scope.Facets
        |> Set.toList
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

    /// The visibility predicate over an already-tagged facet list.
    let admitsFacets (scope: PartyScope) (facets: string list) : bool =
        facets |> List.exists (fun facet -> scope.Facets.Contains facet)

    /// The visibility predicate over a record. Data-driven: it reads the
    /// facets the writer committed at append time and never inspects
    /// `Payload`.
    let sees (scope: PartyScope) (record: LedgerRecord) : bool =
        record |> facetsOf |> admitsFacets scope

// ── 677.B — the export shape ────────────────────────────────────────────

/// One position in the source chain, as the export carries it.
type ScopedExportEntry =
    /// A record the party's scope entitles it to, verbatim.
    | DisclosedRecord of record: LedgerRecord
    /// A position the party's scope does not reach: its sequence, its
    /// digest, and the facets it was tagged with. The digest is what the
    /// verifier walks the chain through; the facets are what stop a
    /// silent downgrade (see the header).
    | WithheldRecord of sequence: int64 * digest: string * facets: string list

module ScopedExportEntry =
    /// The position this entry claims.
    let sequence =
        function
        | DisclosedRecord record -> record.Sequence
        | WithheldRecord(sequence, _, _) -> sequence

    /// The digest at this position — recomputed for a disclosed record
    /// by the verifier, taken as claimed here.
    let digest =
        function
        | DisclosedRecord record -> record.Digest
        | WithheldRecord(_, digest, _) -> digest

    /// The facets at this position.
    let facets =
        function
        | DisclosedRecord record -> facetsOf record
        | WithheldRecord(_, _, facets) -> if obj.ReferenceEquals(facets, null) then [] else facets

/// A verified segment of the chain, scoped to one party.
type ScopedLedgerExport = {
    /// Wire version of this export shape, so a reader refuses a document
    /// it was not written to interpret rather than parsing it hopefully.
    SchemaVersion: int
    /// The party the export was taken for.
    PartyId: string
    /// The facets the filter was applied under, in canonical order. The
    /// export states the scope it CLAIMS to have been filtered by; the
    /// verifier checks it against the scope the holder brings.
    Facets: string list
    /// Every position in the source chain, in order — disclosed records
    /// in full, out-of-scope positions as witnesses.
    Entries: ScopedExportEntry list
    /// The source ledger's head, verbatim: its record count, its head
    /// digest, and its signature when one was taken. Carried, never
    /// re-taken — the signature is over Phase 658's head bytes and
    /// remains checkable against the same public key by a party holding
    /// only this document.
    Head: LedgerHead
}

/// What a scoped export was found to be.
///
/// Four cases, and none of them can be read as another: a caller must
/// never be able to mistake "I could not read this" for "this is wrong",
/// nor either for a pass.
type ScopedExportVerification =
    /// The chain evidence holds across every position, the head agrees
    /// with the walk, and the disclosure matches the holder's scope
    /// exactly — nothing extra, nothing missing.
    | ExportIntact of partyId: string * disclosed: int * withheld: int * recordCount: int64 * headDigest: string
    /// The chain evidence does not hold. Carries the FIRST break,
    /// positioned, in the ledger's own vocabulary.
    | ExportBrokenAt of LedgerBreak
    /// The chain evidence holds, but the filter was not applied as
    /// declared: a record disclosed that this scope does not reach, or a
    /// position withheld that it does. `Position` is `None` for a
    /// mismatch about the export as a whole rather than one entry.
    | ExportScopeViolation of position: int64 option * detail: string
    /// The document could not be READ far enough to judge — a malformed
    /// envelope, a statement of another shape, an unparseable predicate.
    /// Never a pass.
    | ExportUnreadable of position: string * reason: string

module ScopedExportVerification =
    /// `true` only for `ExportIntact`. Exists so no caller writes
    /// `<> ExportBrokenAt _` and treats an unreadable document as
    /// verified.
    let isIntact =
        function
        | ExportIntact _ -> true
        | _ -> false

    let describe =
        function
        | ExportIntact(partyId, disclosed, withheld, recordCount, headDigest) ->
            sprintf
                "scoped export intact for %s: %d disclosed, %d withheld, %d records, head %s"
                partyId
                disclosed
                withheld
                recordCount
                headDigest
        | ExportBrokenAt breakage ->
            sprintf "chain evidence broken at position %d: %s" breakage.Position breakage.Detail
        | ExportScopeViolation(Some position, detail) -> sprintf "scope violation at position %d: %s" position detail
        | ExportScopeViolation(None, detail) -> sprintf "scope violation: %s" detail
        | ExportUnreadable(position, reason) -> sprintf "unreadable at %s: %s" position reason

/// The wire version this module writes and reads.
[<Literal>]
let SchemaVersion = 1

let private jsonOptions = FableConverters.create ()

let private coerceFacetList (facets: string list) =
    if obj.ReferenceEquals(facets, null) then [] else facets

// ── Projection ──────────────────────────────────────────────────────────

/// Project a read chain into one party's scoped export. Pure — the IO
/// entry below reads the ledger and calls this, and a test calls it
/// directly.
///
/// The head is carried exactly as stored. Where it disagrees with the
/// records, `exportFor` refuses before reaching here; a caller reaching
/// this function directly is responsible for what it passes, and the
/// verifier will say so.
let project (scope: PartyScope) (head: LedgerHead) (records: LedgerRecord list) : ScopedLedgerExport = {
    SchemaVersion = SchemaVersion
    PartyId = scope.PartyId
    Facets = PartyScope.facetList scope
    Entries =
        records
        |> List.map (fun record ->
            if PartyScope.sees scope record then
                DisclosedRecord record
            else
                WithheldRecord(record.Sequence, record.Digest, facetsOf record))
    Head = head
}

/// Read a stored ledger and project one party's export from it.
///
/// **Refuses to export from a chain that does not verify.** An export
/// taken from a broken ledger is a document a counterparty can never
/// make sound, and handing one over converts the exporter's problem into
/// the recipient's. The break is named in the refusal, so the operator
/// is told what to fix rather than that something went wrong.
let exportFor
    (settings: ChainedLedgerSettings)
    (storage: IBlobStorage)
    (scope: PartyScope)
    : Async<Result<ScopedLedgerExport, string>> =
    async {
        match! ChainedLedger.read settings storage with
        | Error message -> return Error message
        | Ok stored ->
            match verifyRecords stored.Records stored.TornTail with
            | Error breakage ->
                return
                    Error(
                        sprintf
                            "refusing to export a scoped segment: the source chain breaks at position %d (%s)"
                            breakage.Position
                            breakage.Detail
                    )
            | Ok(recordCount, headDigest) ->
                match stored.Head with
                | None ->
                    // Legitimate only on a ledger nothing was ever
                    // written to, and even there the export would carry
                    // no head evidence at all. Refused rather than
                    // fabricated: a synthesised head is a claim the
                    // ledger never made.
                    return Error "refusing to export a scoped segment: the ledger has no head pointer"
                | Some head when head.RecordCount <> recordCount || head.HeadDigest <> headDigest ->
                    return
                        Error(
                            sprintf
                                "refusing to export a scoped segment: the head pointer records %d/%s and the chain walks to %d/%s"
                                head.RecordCount
                                head.HeadDigest
                                recordCount
                                headDigest
                        )
                | Some head -> return Ok(project scope head stored.Records)
    }

// ── The pure verifier ───────────────────────────────────────────────────

/// Verify a scoped export against the scope the HOLDER claims — never
/// the one the document asserts.
///
/// **Every input is the holder's.** The document names a party and a
/// facet set; if those were the ones checked against, an export could
/// satisfy any expectation simply by declaring it. So the scope is a
/// parameter, the document's own declaration is compared to it, and a
/// disagreement is a violation rather than an adjustment.
///
/// The walk is the ledger's own (`LedgerChain.verifyRecords`) with the
/// witnesses spliced in: a disclosed record has its digest recomputed
/// and its link checked; a withheld position contributes its claimed
/// digest as the next record's expected predecessor. A missing or
/// permuted POSITION is therefore caught for withheld and disclosed
/// entries alike, and the head's signed record count catches a truncated
/// tail — which is precisely why Phase 658 signs the count beside the
/// digest.
///
/// An in-scope record dropped to a witness is reported as a scope
/// violation rather than as a chain break, deliberately: the chain is
/// sound in that case, and calling it broken would send a reader looking
/// for tampering when what happened was under-disclosure.
let verifyExport (scope: PartyScope) (export: ScopedLedgerExport) : ScopedExportVerification =
    let entries =
        if obj.ReferenceEquals(export.Entries, null) then
            []
        else
            export.Entries

    // The entry walk, as a local so the precondition checks below can
    // read as a flat ladder of refusals with one exit each. It is
    // reached only from the final `else`, where the head is known
    // non-null.
    let verifyEntries () =
        let observed = entries |> List.map ScopedExportEntry.sequence |> Set.ofList

        let positionalBreak (index: int64) (found: int64) =
            // Present elsewhere is a permutation; present nowhere is a
            // deletion. The same discrimination the ledger's own walk
            // draws, and for the same reason.
            {
                Position = index
                Sequence = Some found
                Kind =
                    if observed.Contains index then
                        ReorderedRecord
                    else
                        DroppedRecord
                Detail = sprintf "expected sequence %d at position %d, found %d" index index found
            }

        let rec walk (index: int64) (previousDigest: string) (disclosed: int) (withheld: int) remaining =
            match remaining with
            | [] ->
                if export.Head.RecordCount <> index then
                    // The head's record count is inside the signed head
                    // bytes, so a tail lopped off the export contradicts
                    // a signature the exporter cannot re-take.
                    ExportBrokenAt {
                        Position = index
                        Sequence = None
                        Kind = DroppedRecord
                        Detail =
                            sprintf
                                "the head records %d entries and the export carries %d"
                                export.Head.RecordCount
                                index
                    }
                elif export.Head.HeadDigest <> previousDigest then
                    ExportBrokenAt {
                        Position = index
                        Sequence = None
                        Kind = BrokenLink
                        Detail =
                            sprintf
                                "the head names %s and the export's entries walk to %s"
                                export.Head.HeadDigest
                                previousDigest
                    }
                else
                    ExportIntact(export.PartyId, disclosed, withheld, index, previousDigest)
            | entry :: rest ->
                let sequence = ScopedExportEntry.sequence entry

                if sequence <> index then
                    ExportBrokenAt(positionalBreak index sequence)
                else
                    match entry with
                    | DisclosedRecord record ->
                        let recomputed = computeDigest record

                        if recomputed <> record.Digest then
                            ExportBrokenAt {
                                Position = index
                                Sequence = Some sequence
                                Kind = TamperedRecord
                                Detail = sprintf "stored digest %s, recomputed %s" record.Digest recomputed
                            }
                        elif record.PreviousDigest <> previousDigest then
                            ExportBrokenAt {
                                Position = index
                                Sequence = Some sequence
                                Kind = BrokenLink
                                Detail =
                                    sprintf
                                        "record chains to %s, predecessor digest is %s"
                                        record.PreviousDigest
                                        previousDigest
                            }
                        elif not (PartyScope.sees scope record) then
                            ExportScopeViolation(
                                Some index,
                                sprintf
                                    "a record tagged [%s] is disclosed to a scope holding [%s]"
                                    (facetsOf record |> String.concat "; ")
                                    (PartyScope.facetList scope |> String.concat "; ")
                            )
                        else
                            walk (index + 1L) record.Digest (disclosed + 1) withheld rest
                    | WithheldRecord(_, digest, entryFacets) ->
                        let entryFacets = coerceFacetList entryFacets

                        if PartyScope.admitsFacets scope entryFacets then
                            ExportScopeViolation(
                                Some index,
                                sprintf
                                    "a record tagged [%s] is withheld from a scope entitled to it — the export is incomplete within its own scope"
                                    (entryFacets |> String.concat "; ")
                            )
                        else
                            walk (index + 1L) digest disclosed (withheld + 1) rest

        walk 0L genesisDigest 0 0 entries

    if export.SchemaVersion <> SchemaVersion then
        ExportUnreadable(
            "export/schemaVersion",
            sprintf
                "the export declares schema version %d, this reader understands %d"
                export.SchemaVersion
                SchemaVersion
        )
    elif export.PartyId <> scope.PartyId then
        ExportScopeViolation(
            None,
            sprintf "the export was taken for party '%s' and is being checked as '%s'" export.PartyId scope.PartyId
        )
    elif coerceFacetList export.Facets <> PartyScope.facetList scope then
        ExportScopeViolation(
            None,
            sprintf
                "the export declares facets [%s] and this scope holds [%s]"
                (coerceFacetList export.Facets |> String.concat "; ")
                (PartyScope.facetList scope |> String.concat "; ")
        )
    elif obj.ReferenceEquals(export.Head, null) then
        ExportUnreadable("export/head", "the export carries no head")
    else
        verifyEntries ()

/// Every record the export DISCLOSES that this scope does not reach.
/// Empty on a conforming export — the structural form of the leakage
/// assertion, available to a caller that wants the offending records
/// rather than the first verdict.
let disclosedOutOfScope (scope: PartyScope) (export: ScopedLedgerExport) : LedgerRecord list =
    let entries =
        if obj.ReferenceEquals(export.Entries, null) then
            []
        else
            export.Entries

    entries
    |> List.choose (function
        | DisclosedRecord record when not (PartyScope.sees scope record) -> Some record
        | _ -> None)

/// Every position the export WITHHOLDS that this scope is entitled to —
/// the omission the facet labels exist to make visible.
let withheldInScope (scope: PartyScope) (export: ScopedLedgerExport) : (int64 * string list) list =
    let entries =
        if obj.ReferenceEquals(export.Entries, null) then
            []
        else
            export.Entries

    entries
    |> List.choose (function
        | WithheldRecord(sequence, _, facets) ->
            let facets = coerceFacetList facets

            if PartyScope.admitsFacets scope facets then
                Some(sequence, facets)
            else
                None
        | _ -> None)

/// Every record the export discloses, in chain order — what the party
/// actually archives.
let disclosedRecords (export: ScopedLedgerExport) : LedgerRecord list =
    let entries =
        if obj.ReferenceEquals(export.Entries, null) then
            []
        else
            export.Entries

    entries
    |> List.choose (function
        | DisclosedRecord record -> Some record
        | _ -> None)

// ── 677.C — the export as a signed, stock-verifiable statement ──────────

/// The versioned predicate type URI, in the shape this SDK's other
/// attestations use.
///
/// **Its own type, and not the evidence bundle's.** A verifier keys on
/// `predicateType` to decide what shape it is about to read; publishing
/// a second shape under an existing URI would make that key meaningless.
/// A ledger segment says what one party's slice of one chain contains,
/// which is a different claim from either of the two already published.
[<Literal>]
let PredicateType =
    "https://toolup-forge.io/attestations/scoped-audit-ledger-export/v1"

/// The in-toto subject name for a scoped export.
[<Literal>]
let SubjectName = "scoped-audit-ledger-export"

/// The canonical form the content id addresses and the predicate
/// carries — one string, so the bytes a stock tool hashes and the bytes
/// inside the statement cannot differ.
///
/// Canonicalised through the ledger's own JSON canonicaliser (properties
/// ordinally sorted at every depth, array order preserved), so the id is
/// a function of the value rather than of whichever serialiser produced
/// it.
let canonicalForm (export: ScopedLedgerExport) : string =
    JsonSerializer.Serialize(export, jsonOptions) |> canonicaliseJson

/// The canonical bytes a stock DSSE tool hashes to check the subject
/// claim.
let canonicalBytes (export: ScopedLedgerExport) : byte[] =
    Encoding.UTF8.GetBytes(canonicalForm export)

/// The content id: lowercase-hex SHA-256 over the canonical bytes. The
/// same primitive the envelope module already uses — no new
/// cryptography enters here.
let contentId (export: ScopedLedgerExport) : string =
    DsseEnvelope.sha256Hex (canonicalBytes export)

/// The in-toto subject: the export's content id under the `sha256`
/// digest key, which is what the id genuinely is.
let subjectFor (export: ScopedLedgerExport) : InTotoSubject = {
    Name = SubjectName
    Digest = [ "sha256", contentId export ]
}

/// The predicate JSON — the canonical form, verbatim.
let predicateJson (export: ScopedLedgerExport) : string = canonicalForm export

/// The unsigned in-toto statement. Exposed for tests and for a caller
/// that signs through its own path.
let statementJson (export: ScopedLedgerExport) : string =
    DsseEnvelope.statementJson [ subjectFor export ] PredicateType (predicateJson export)

/// Wrap a scoped export as a DSSE-signed in-toto statement.
///
/// **One signature, and it is not the ledger head's.** This one binds
/// THIS DOCUMENT to the key that produced it; the head signature inside
/// the export binds the source chain and is carried verbatim. A holder
/// checks both, against two different keys, for two different claims —
/// which is why neither is re-expressed in terms of the other.
let sign (signer: IStatementEnvelopeSigner) (export: ScopedLedgerExport) : Async<Result<DsseEnvelope, string>> =
    DsseEnvelope.sign signer [ subjectFor export ] PredicateType (predicateJson export)

/// What a holder requires of a scoped-export envelope. `expectedContentId`
/// is an id the holder independently possesses; `None` skips the subject
/// check, which is right only when it has no independent handle.
let expectation (expectedContentId: string option) : EnvelopeExpectation = {
    PredicateType = PredicateType
    SubjectDigest = expectedContentId
}

/// Read an export out of a predicate that has already been
/// signature-verified, or out of the crypto-free document reader below.
let readExport (predicate: string) : Result<ScopedLedgerExport, EnvelopeVerdict> =
    try
        let export = JsonSerializer.Deserialize<ScopedLedgerExport>(predicate, jsonOptions)

        if obj.ReferenceEquals(export.Head, null) then
            Error(EnvelopeMalformed "predicate is not a scoped ledger export (no head)")
        else
            // A list field absent from a stripped document deserialises
            // to `null` on this converter set, and a null F# list throws
            // on every list operation. Coerced here so a stripped
            // document reaches the verifier as the empty thing it claims
            // to be and is refused BY NAME rather than by exception.
            Ok {
                export with
                    Facets = coerceFacetList export.Facets
                    Entries =
                        (if obj.ReferenceEquals(export.Entries, null) then
                             []
                         else
                             export.Entries)
                        |> List.map (function
                            | DisclosedRecord record -> DisclosedRecord(LedgerRecord.coerceFacets record)
                            | WithheldRecord(sequence, digest, facets) ->
                                WithheldRecord(sequence, digest, coerceFacetList facets))
            }
    with ex ->
        Error(EnvelopeMalformed(sprintf "predicate is not a readable scoped ledger export: %s" ex.Message))

/// Read a scoped export out of a DSSE document **without checking its
/// signature**, and verify it against the holder's scope.
///
/// **Named for the hazard.** This reads a payload nobody has
/// authenticated, which is legitimate for one reason: the answer makes
/// no claim about authorship. It says whether the document is internally
/// consistent and correctly scoped — whether the chain evidence holds,
/// the head agrees, and the filter matches. A tampered document fails
/// it; a wholly fabricated one passes it, honestly, because "this is a
/// well-formed export" and "this export is yours" are different
/// questions and only the second needs a key. A holder that wants both
/// runs the stock DSSE signature check alongside.
let verifyDocument (scope: PartyScope) (json: string) : ScopedExportVerification =
    match DsseEnvelope.parse json with
    | Error reason -> ExportUnreadable("document/envelope", sprintf "the DSSE envelope could not be read: %s" reason)
    | Ok envelope ->
        if envelope.PayloadType <> DsseEnvelope.InTotoPayloadType then
            ExportUnreadable(
                "document/payloadType",
                sprintf
                    "the envelope declares payload type '%s' where an in-toto statement is '%s'"
                    envelope.PayloadType
                    DsseEnvelope.InTotoPayloadType
            )
        else
            match DsseEnvelope.readStatement envelope with
            | Error verdict -> ExportUnreadable("document/statement", EnvelopeVerdict.describe verdict)
            | Ok statement ->
                if statement.PredicateType <> PredicateType then
                    ExportUnreadable(
                        "document/predicateType",
                        sprintf
                            "the statement declares predicate type '%s', which is not the scoped-export type '%s' — a reader is told what it is holding rather than what it is not"
                            statement.PredicateType
                            PredicateType
                    )
                else
                    match readExport statement.PredicateJson with
                    | Error verdict -> ExportUnreadable("document/predicate", EnvelopeVerdict.describe verdict)
                    | Ok export ->
                        match verifyExport scope export with
                        | ExportIntact _ as intact ->
                            // The subject is the holder's claim check and
                            // it is checked LAST, so a document that is
                            // internally broken is reported where it
                            // broke rather than as a subject mismatch.
                            let addressed = contentId export

                            if statement.SubjectDigests |> List.contains addressed then
                                intact
                            else
                                ExportUnreadable(
                                    "document/subject",
                                    sprintf
                                        "the statement publishes subject digest(s) '%s' and the export inside it is addressed '%s' — a correctly-shaped statement about a different export"
                                        (statement.SubjectDigests |> String.concat ", ")
                                        addressed
                                )
                        | other -> other