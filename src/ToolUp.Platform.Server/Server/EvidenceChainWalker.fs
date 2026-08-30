// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Security.Cryptography
open System.Text

// ─── Phase 713 — the join, with every break reported as data ─────────
//
// The wire shape this walk answers in lives in `ToolUp.Platform.Core`
// (`EvidenceChainTypes.fs`), together with the reasoning about why a
// break is a first-class value. This file is the walk itself: the seam a
// deployment composes, the default that walks nothing, and the shipped
// implementation that turns the substrates a deployment already records
// into one ordered traversal.
//
// **Orchestration, not infrastructure.** Every hop below is answered by
// a verifier that already exists and is already tested: the upstream
// work seam's own attestation (Phase 712), the deploy record's own
// transcript and closure joins (Phase 656 / 659), its own seal check,
// the boot preflight's verdict (Phase 657), a compliance evidence pack
// read (Phase 187) and a hash-chained ledger read (Phase 658). This file
// mints NO verification logic. A hop's link is that substrate's own
// answer, re-labelled — never a second opinion about it.
//
// **The verdict is about the LINK, not the node.** A transcript in hand
// that no deploy record names is a recorded artefact with nothing
// joining it to this deployment, so its hop reads `LinkAbsent` and says
// exactly that. Read every mapping below that way.
//
// **Nothing composes this by default.** `NoEvidenceChainWalker` is the
// default mode and it is not a null object that answers emptily — it
// carries no implementation at all, so an uncomposed deployment
// registers nothing: no DI singleton, no middleware, no route, no
// background work, no allocation (GP 13). Its verification report reads
// the chain section as `NotComposed`, and everything else about it is
// byte-for-byte what it was before this surface existed (GP 11).
//
// **The walk mutates nothing (GP 6).** Every source is read; not one is
// written. The single artefact a walk produces is the audited-read row
// — the same posture the deployment verification report takes, for the
// same reason: producing evidence should itself leave evidence.
//
// **Six portability rules (GP 12).** Identity by value — records of
// strings in, a record of strings out; no live handles. Async at the
// boundary. Failure as data (`Result` over a typed error, never an
// exception; a source that throws is caught and reported as a broken
// hop). Stateless between calls, so a substrate refresh takes effect on
// the next walk with nothing to invalidate. No cross-shard ordering
// promise. No timing-precision boundary.

/// The substrate a deployment composes for the walk.
///
/// Every member is optional and absence is honest throughout: a
/// deployment supplying none of it gets a complete hop list of absences,
/// which is exactly what it should get.
///
/// The two downstream members are thunks because reading them is I/O and
/// a chain that is never walked should not pay for it — and because the
/// chained ledger and the pack's signer both live in assemblies
/// DOWNSTREAM of this one, so a reference to either would invert the
/// dependency graph (GP 1). The rest are values because they are already
/// in hand by the time the sources are built.
type EvidenceChainSources = {
    /// The upstream work provenance seam (Phase 712).
    /// `NoWorkProvenanceSource` is the identity.
    Work: WorkProvenanceMode
    /// The build transcript this deployment was built under, when it
    /// holds one.
    Transcript: BuildTranscript option
    /// The resolved dependency closure, when it holds one.
    Closure: DependencyClosure option
    /// The sealed deploy record this deployment ran from.
    Deploy: SealedDeployRecord option
    /// The sealer that can verify that record's seal. `None` when no
    /// sealer is composed — the seal is then carried rather than
    /// checked, which is an absent link and not a broken one.
    Sealer: IDeployRecordSealer option
    /// The boot verification verdict the composition root holds, mapped
    /// into the chain's own vocabulary. `None` when the deployment never
    /// ran the boot preflight.
    Boot: BootVerificationReading option
    /// Read the compliance evidence pack covering this deployment.
    /// `None` when no pack generator is composed. `Error` means the pack
    /// could not be assembled at all.
    Pack: (unit -> Async<Result<EvidencePackReading, string>>) option
    /// Read this deployment's position in the hash-chained audit ledger.
    /// `None` when no chained ledger is composed. `Error` means the
    /// ledger could not be READ — distinct from a walk that found a
    /// break, which arrives as `Ok (LedgerBroken …)`.
    Ledger: (unit -> Async<Result<LedgerPositionReading, string>>) option
}

[<RequireQualifiedAccess>]
module EvidenceChainSources =

    /// Sources naming nothing — every hop reads absent. Behaviourally
    /// identical to composing no walker at all; useful where a value is
    /// required rather than an option.
    let none: EvidenceChainSources = {
        Work = NoWorkProvenanceSource
        Transcript = None
        Closure = None
        Deploy = None
        Sealer = None
        Boot = None
        Pack = None
        Ledger = None
    }

    /// Derive the pack-read thunk from a composed generator and the
    /// request it should assemble under.
    ///
    /// Provided so the pack hop is answered by the real generator rather
    /// than by a reading a composition root hand-rolls: the mapping from
    /// a signed manifest to `PackSigned` / `PackUnsigned` is the one
    /// distinction the hop rests on, and it should have exactly one
    /// definition. A generator that is the shipped no-op returns
    /// `GeneratorDisabled`, which reads as an absent link rather than a
    /// broken one — nothing was composed, so nothing failed.
    let packReadOf
        (generator: IEvidencePackGenerator)
        (request: EvidencePackRequest)
        : unit -> Async<Result<EvidencePackReading, string>> =
        fun () -> async {
            match! generator.Generate request with
            | Ok pack ->
                let entries = List.length pack.Manifest.Entries

                let digest =
                    Convert.ToHexString(SHA256.HashData pack.ManifestBytes).ToLowerInvariant()

                match pack.Signature with
                | Some _ -> return Ok(EvidencePackReading.PackSigned(digest, entries))
                | None -> return Ok(EvidencePackReading.PackUnsigned(digest, entries))
            | Error GeneratorDisabled -> return Error "no evidence pack generator is composed"
            | Error other -> return Error(EvidencePackError.describe other)
        }

/// Walks one deployment's evidence chain in a single ordered traversal.
///
/// **Every member is a query.** There is no member that writes and none
/// that answers with `unit` — a `unit` answer is the shape a mutation
/// takes, so its absence is what a shipped test asserts over this
/// interface's methods. A deployment cannot expose a write path by
/// composing this seam, whatever it wires behind it.
type IEvidenceChainWalker =
    /// The bounds this walker declares. Cheap and constant — a caller
    /// reads it once and sizes its request, instead of discovering the
    /// limit as a refusal.
    ///
    /// Async despite being constant for the shipped walker, per GP 12
    /// rule 2 and to match the upstream work seam's own `GetCaps`: a
    /// walker that derived its bounds from a remote policy would
    /// otherwise have to break the contract to ship.
    abstract GetCaps: unit -> Async<EvidenceChainCaps>

    /// Walk the chain.
    ///
    /// **Always returns the FULL ordered hop list**, failures included,
    /// for any deployment whose request is within the declared caps. The
    /// `Error` arm is reserved for a walk that could not be performed AS
    /// ASKED — an over-cap request, an over-cap closure — never for a
    /// deployment that composed little or nothing.
    abstract Walk: request: EvidenceChainRequest -> Async<Result<EvidenceChain, EvidenceChainError>>

/// Whether a deployment composes an evidence chain walker.
///
/// The default is `NoEvidenceChainWalker`, which carries no
/// implementation — so there is nothing to register, nothing to start,
/// and nothing to pay for (GP 13). Composing a walker is the deliberate
/// act; not composing one is free and stays honest about it.
type EvidenceChainWalkerMode =
    /// The default. No chain is walked, nothing is registered, and the
    /// verification report's chain section reads `NotComposed`.
    | NoEvidenceChainWalker
    /// A composed walker.
    | ComposedEvidenceChainWalker of IEvidenceChainWalker

[<AutoOpen>]
module private EvidenceChainHops =

    /// How many enumeration lines one hop carries.
    ///
    /// A closure can legitimately carry more unattested entries than a
    /// reader takes in at once. The truncation is STATED rather than
    /// silent — the withheld count rides as its own finding line — which
    /// is a different act from a short hop list: the verdict and its
    /// counts are always over the whole set, and it is only the
    /// per-item enumeration that is bounded.
    [<Literal>]
    let FindingCap = 20

    /// Cap an enumeration, saying how many lines were withheld. A silent
    /// truncation would let a large closure present as a small one.
    let capFindings (findings: string list) : string list =
        let shown = findings |> List.truncate FindingCap
        let withheld = List.length findings - List.length shown

        if withheld > 0 then
            shown
            @ [
                sprintf "(%d further line(s) not listed; the verdict's counts are over all %d)" withheld findings.Length
            ]
        else
            shown

    // ── Phase 716 — what each hop's linkage says must be enumerated ──
    //
    // Every derivation below reads LINKAGE — the parent refs a work
    // record carries, the attestation state of each closure entry, the
    // index a ledger read reached — and never the findings the hop
    // rendered. That separation is the completeness claim's whole value:
    // an expectation read back out of the render is satisfied by
    // definition, so it measures nothing.

    /// The positions an ancestor page's own parent refs name, each
    /// carrying the declared bound that legitimately holds it back, if
    /// any.
    ///
    /// **Two bounds, and everything else is required.** A parent named
    /// by a record sitting at the requested depth's frontier was never
    /// fetched — the level naming it was walked and the level resolving
    /// it was not — so the depth the caller asked for accounts for it. A
    /// record whose rendered line falls past the enumeration cap is
    /// accounted for by that cap, which states its own truncation. A
    /// parent ref that is neither is one the linkage names and the walk
    /// did not carry: the source answered `Absent` and the ancestor walk
    /// moved on, which is exactly the silent stop this claim exists to
    /// surface.
    ///
    /// **Where the page RECORDED the break, the position comes from the
    /// marker and from nowhere else.** A page carrying severed-edge
    /// markers has already said which joins it could not follow, so this
    /// derivation reads them rather than re-inferring them from what did
    /// not come back — the link tier's verdict and this tier's missing
    /// position are then two renderings of one recording and cannot drift
    /// apart. Each such position is keyed on the EDGE, because that is
    /// what the marker identifies and because naming a broken edge is not
    /// enumerating the record behind it: the record's kind, label and own
    /// parents are all on the far side of the break, so the position
    /// stays unaccounted-for while the break stands, which is precisely
    /// what `Incomplete` is reserved for.
    ///
    /// The `Absent`-inferred fallback below is kept for a source with its
    /// own native ancestor walk, which may resolve a page without ever
    /// producing a marker. Such a page reads exactly as it did before
    /// this phase.
    let ancestorPositions (page: WorkAncestorPage) : EnumerationPosition list =
        let hop = EvidenceChain.UpstreamWorkRecordHop

        /// The refs the page itself recorded as severed — excluded from
        /// the inferred derivation below so one break is never reported
        /// twice under two different keys.
        let severedRefs =
            page.Severed |> List.map (fun edge -> edge.Ref.RecordId) |> Set.ofList

        let severedPositions =
            page.Severed
            |> List.map (fun edge ->
                EvidenceEnumeration.required hop EvidenceEnumeration.WorkAncestorKind (SeveredWorkEdge.key edge))

        let recordsById =
            page.Records
            |> List.map (fun record -> record.Ref.RecordId, record)
            |> Map.ofList

        // Distance from the root over the edges the page itself carries.
        // The `seen` guard is load-bearing rather than defensive: a
        // source system whose records cite each other cyclically would
        // otherwise walk forever inside a pure function.
        let rec distances (frontier: string list) (level: int) (seen: Map<string, int>) : Map<string, int> =
            match frontier with
            | [] -> seen
            | _ ->
                let seen = frontier |> List.fold (fun acc id -> Map.add id level acc) seen

                let next =
                    frontier
                    |> List.collect (fun id ->
                        match recordsById |> Map.tryFind id with
                        | Some record -> record.Parents |> List.map _.RecordId
                        | None -> [])
                    |> List.filter (fun id -> not (seen |> Map.containsKey id))
                    |> List.distinct

                distances next (level + 1) seen

        let distance = distances [ page.Root.RecordId ] 0 Map.empty

        // The order `ancestorFindings` renders in — readable records,
        // then withheld markers — so a position's rendered index is
        // computed rather than guessed at from what came back.
        let renderIndex =
            (page.Records |> List.map (fun record -> record.Ref.RecordId))
            @ (page.Withheld |> List.map (fun marker -> marker.Ref.RecordId))
            |> List.mapi (fun index id -> id, index)
            |> Map.ofList

        /// The shallowest record naming this id as a parent, which is the
        /// level at which the walk would have resolved it.
        let namedAt (id: string) =
            page.Records
            |> List.filter (fun record -> record.Parents |> List.exists (fun parent -> parent.RecordId = id))
            |> List.choose (fun record -> distance |> Map.tryFind record.Ref.RecordId)
            |> function
                | [] -> None
                | levels -> Some(List.min levels)

        let inferred =
            page.Root.RecordId
            :: (page.Records
                |> List.collect (fun record -> record.Parents |> List.map _.RecordId))
            |> List.distinct
            |> List.filter (fun id -> not (severedRefs.Contains id))

        let inferredPositions =
            inferred
            |> List.map (fun id ->
                match renderIndex |> Map.tryFind id with
                | Some index when index >= FindingCap ->
                    EvidenceEnumeration.bounded
                        hop
                        EvidenceEnumeration.WorkAncestorKind
                        id
                        EvidenceEnumeration.EnumerationCapBound
                | Some _ -> EvidenceEnumeration.required hop EvidenceEnumeration.WorkAncestorKind id
                | None ->
                    match namedAt id with
                    | Some level when level + 1 >= page.Depth ->
                        EvidenceEnumeration.bounded
                            hop
                            EvidenceEnumeration.WorkAncestorKind
                            id
                            EvidenceEnumeration.WorkDepthBound
                    | _ -> EvidenceEnumeration.required hop EvidenceEnumeration.WorkAncestorKind id)

        inferredPositions @ severedPositions

    /// The deploy this walk is about, for a break that needs to name a
    /// position and has only the deployment to name.
    let deployPosition (sources: EvidenceChainSources) : string =
        match sources.Deploy with
        | Some signedRecord -> signedRecord.Record.DeployId
        | None -> "unknown deploy"

    // ── Hop 1 — the upstream work record ─────────────────────────────

    /// One rendered line per ancestor the work walk reached.
    let private ancestorFindings (page: WorkAncestorPage) : string list =
        let readable =
            page.Records
            |> List.map (fun record ->
                sprintf
                    "%s [%s] %s"
                    (WorkRecordRef.describe record.Ref)
                    (WorkRecordKind.label record.Kind)
                    (if record.Label = "" then "(no label)" else record.Label))

        let withheld =
            page.Withheld
            |> List.map (fun marker ->
                sprintf
                    "%s [%s] WITHHELD under policy %s"
                    (WorkRecordRef.describe marker.Ref)
                    (WorkRecordKind.label marker.Kind)
                    marker.PolicyRef)

        // Appended last, so a readable or withheld record's rendered
        // index — which `ancestorPositions` computes against the declared
        // enumeration cap — is exactly where it was before this line
        // existed.
        let severed = page.Severed |> List.map SeveredWorkEdge.describe

        readable @ withheld @ severed

    /// The join from the deploy to the work that authored its sources.
    ///
    /// Answered by the work seam's own attestation, which never drops a
    /// deploy: it is attested by reference, or unattested carrying which
    /// of five reasons applies. Four of those five are absences of a
    /// join; the fifth — the source was asked and failed — is a break,
    /// because the substrate is composed and would not answer, which is
    /// precisely the state a deployment reaches by breaking its own
    /// evidence.
    /// Answers with the hop's outcome AND the positions its own linkage
    /// says the enumeration must account for. Every branch that resolves
    /// no ancestor page names no positions — there is no enumeration to
    /// be complete about — and the branch that resolves one derives them
    /// from the page's parent refs.
    ///
    /// **A page that resolved and lost an edge is broken, not linked.**
    /// The doctrine this walk is built on is that absent and broken are
    /// different claims, both sayable; it was applied at every hop except
    /// inside this one's own page, where an unresolvable parent used to
    /// leave the hop reading exactly as it reads when nothing is severed.
    /// A parent ref IS a recorded join, so a page carrying one that does
    /// not hold reads broken at that ref's position.
    let walkWorkRecord
        (sources: EvidenceChainSources)
        (depth: int)
        : Async<EvidenceHopOutcome * EnumerationPosition list> =
        async {
            match sources.Deploy with
            | None ->
                return
                    EvidenceHopOutcome.bare (
                        EvidenceLink.LinkAbsent
                            "no sealed deploy record is composed, so there is no upstream reference to look the authoring work up by"
                    ),
                    []
            | Some signedRecord ->
                let! attestation = WorkProvenance.attest sources.Work signedRecord.Record

                match attestation.Head with
                | WorkAttestation.Unattested(WorkUnattestedReason.LookupFailed reason) ->
                    return
                        EvidenceHopOutcome.bare (
                            EvidenceLink.LinkBroken(
                                defaultArg attestation.UpstreamReference (deployPosition sources),
                                sprintf "the composed work provenance source was asked and would not answer: %s" reason
                            )
                        ),
                        []
                | WorkAttestation.Unattested reason ->
                    return EvidenceHopOutcome.bare (EvidenceLink.LinkAbsent(WorkUnattestedReason.describe reason)), []
                | WorkAttestation.AttestedBy head ->
                    match WorkProvenanceSource.ofMode sources.Work with
                    | None ->
                        // Unreachable while `attest` only attests through
                        // a composed source, and stated rather than
                        // assumed: a future mode that attested without
                        // one would otherwise silently render as linked
                        // with no ancestors.
                        return
                            EvidenceHopOutcome.bare (
                                EvidenceLink.LinkAbsent
                                    "an upstream work record is named and no source is composed to read it from"
                            ),
                            []
                    | Some source ->
                        match! source.GetRecord head with
                        | WorkRecordAnswer.Withheld marker ->
                            return EvidenceHopOutcome.bare (EvidenceLink.LinkWithheld marker.PolicyRef), []
                        | WorkRecordAnswer.Absent ->
                            return
                                EvidenceHopOutcome.bare (
                                    EvidenceLink.LinkBroken(
                                        WorkRecordRef.describe head,
                                        "the source system named this record as covering the deploy's sources and then holds no record under that ref"
                                    )
                                ),
                                []
                        | WorkRecordAnswer.Found record ->
                            let! ancestors = source.GetAncestors { Root = head; Depth = depth }

                            let detail =
                                sprintf
                                    "the deploy's sources are covered by %s work record %s in %s"
                                    (WorkRecordKind.label record.Kind)
                                    (WorkRecordRef.describe head)
                                    (if attestation.SourceSystem = "" then
                                         "an unnamed source system"
                                     else
                                         attestation.SourceSystem)

                            match ancestors with
                            | Ok page ->
                                // The join from the deploy to the head
                                // holds; what may not hold is a join
                                // INSIDE the page, between two records
                                // the page itself named. A recorded join
                                // that fails is a break, and a parent ref
                                // is a recorded join — so a page carrying
                                // severed-edge markers reads broken at
                                // the first of them, in walk order, for
                                // the reason the ledger hop names only
                                // the first break: everything past it is
                                // a page already known to be short.
                                //
                                // Derived from the SAME markers the
                                // enumeration's missing positions come
                                // from, so the two tiers state one
                                // recording twice rather than observing
                                // the page twice.
                                let link =
                                    match page.Severed with
                                    | [] -> EvidenceLink.Linked(head.RecordId, detail)
                                    | edge :: _ ->
                                        EvidenceLink.LinkBroken(
                                            WorkRecordRef.describe edge.Ref,
                                            sprintf
                                                "%s, and the ancestor walk behind it lost %d recorded edge(s): %s named %s as a parent and the source system holds no record under that ref, so this page is shorter than its own records say it is"
                                                detail
                                                (List.length page.Severed)
                                                (WorkRecordRef.describe edge.NamedBy)
                                                (WorkRecordRef.describe edge.Ref)
                                        )

                                return
                                    {
                                        Link = link
                                        Findings = capFindings (ancestorFindings page)
                                    },
                                    ancestorPositions page
                            | Error error ->
                                // The head resolved; the ancestor walk did
                                // not. The join to the deploy still holds,
                                // so the hop is linked and the refusal is
                                // reported as the finding it is rather than
                                // demoting a link that is genuinely there.
                                //
                                // The enumeration nevertheless did not
                                // happen, and every refusal this seam
                                // raises is a DECLARED bound. One bounded
                                // position says so, rather than letting a
                                // refused ancestor walk read as a complete
                                // enumeration of nothing.
                                return
                                    {
                                        Link = EvidenceLink.Linked(head.RecordId, detail)
                                        Findings = [
                                            sprintf
                                                "the ancestor walk was refused: %s"
                                                (WorkProvenanceError.describe error)
                                        ]
                                    },
                                    [
                                        // Keyed on the enumeration that
                                        // did not happen rather than on
                                        // the head, whose id the link
                                        // reference already names — a
                                        // position the render accounts
                                        // for is not a position anything
                                        // held back.
                                        EvidenceEnumeration.bounded
                                            EvidenceChain.UpstreamWorkRecordHop
                                            EvidenceEnumeration.WorkAncestorKind
                                            $"ancestors-of {head.RecordId}"
                                            EvidenceEnumeration.WorkAncestorBound
                                    ]
        }

    // ── Hop 2 — the build transcript ─────────────────────────────────

    /// The join from the deploy record to the transcript it was built
    /// under, answered by the deploy substrate's own digest comparison.
    let walkTranscript (sources: EvidenceChainSources) : EvidenceHopOutcome =
        match sources.Transcript, sources.Deploy with
        | None, _ ->
            EvidenceHopOutcome.bare (
                EvidenceLink.LinkAbsent
                    "no build transcript is composed, so what this deployment was built from is unrecorded"
            )
        | Some _, None ->
            EvidenceHopOutcome.bare (
                EvidenceLink.LinkAbsent
                    "a build transcript is recorded and no deploy record names its digest, so nothing joins it to this deployment"
            )
        | Some transcript, Some signedRecord ->
            let computed = DeployRecords.transcriptDigest transcript

            match DeployRecords.verifyTranscript transcript signedRecord.Record.Provenance with
            | Ok() ->
                let dependencies = BuildTranscript.canonicalDependencies transcript

                {
                    Link =
                        EvidenceLink.Linked(
                            computed,
                            sprintf
                                "the deploy record names transcript %s, which is the transcript in hand — toolchain %s %s over %d resolved dependencies"
                                computed
                                (if transcript.Toolchain.Name = "" then
                                     "(unnamed)"
                                 else
                                     transcript.Toolchain.Name)
                                (if transcript.Toolchain.Version = "" then
                                     "(unversioned)"
                                 else
                                     transcript.Toolchain.Version)
                                (List.length dependencies)
                        )
                    Findings = [
                        sprintf
                            "entry point %s (%s)"
                            (if transcript.EntryPoint.Path = "" then
                                 "(none recorded)"
                             else
                                 transcript.EntryPoint.Path)
                            (if transcript.EntryPoint.ContentDigest = "" then
                                 "no digest recorded"
                             else
                                 transcript.EntryPoint.ContentDigest)
                    ]
                }
            | Error failures ->
                let describe =
                    failures |> List.map DeployRecords.DeployRecordVerificationFailure.describe

                let broken =
                    failures
                    |> List.exists (function
                        | DeployRecords.TranscriptDigestMismatch _ -> true
                        | _ -> false)

                if broken then
                    {
                        Link =
                            EvidenceLink.LinkBroken(
                                computed,
                                "the deploy record's transcript digest is not the digest of the transcript in hand"
                            )
                        Findings = describe
                    }
                else
                    // `TranscriptNotRecorded` — a transcript exists and the
                    // record names none. Nothing is broken; there is simply
                    // no join, which is a bound rather than a finding.
                    {
                        Link =
                            EvidenceLink.LinkAbsent
                                "the deploy record carries no transcript digest, so nothing joins the transcript in hand to it"
                        Findings = describe
                    }

    // ── Hop 3 — the dependency closure ───────────────────────────────

    /// One line per closure entry that stands on no attested upstream
    /// release, carrying the reason. Reported rather than counted: an
    /// unattested entry is the half of a closure a reader most needs to
    /// see, and a bare count is not actionable.
    ///
    /// Split into the entry's key and its reason so the enumeration's
    /// POSITIONS and its rendered LINES are produced from one traversal
    /// in one order. Two traversals that drifted apart would compute a
    /// position's rendered index against lines it does not correspond to.
    let private unattestedEntries (closure: DependencyClosure) : (string * string) list =
        closure.Entries
        |> List.choose (fun entry ->
            match entry.Attestation with
            | AttestedBy _ -> None
            | Unattested reason -> Some(sprintf "%s %s" entry.Id entry.Version, UnattestedReason.describe reason))

    let private unattestedFindings (closure: DependencyClosure) : string list =
        unattestedEntries closure
        |> List.map (fun (key, reason) -> sprintf "%s — %s" key reason)

    /// The positions a linked closure's own attestation states name: one
    /// per entry standing on no attested upstream release, because those
    /// are the entries the enumeration exists to carry. An entry past the
    /// declared enumeration cap is held back by that cap, which states
    /// its own truncation in the render.
    ///
    /// The key carries the version as well as the id, so two packages
    /// sharing a name prefix cannot account for one another.
    let private closurePositions (closure: DependencyClosure) : EnumerationPosition list =
        unattestedEntries closure
        |> List.mapi (fun index (key, _) ->
            if index >= FindingCap then
                EvidenceEnumeration.bounded
                    EvidenceChain.DependencyClosureHop
                    EvidenceEnumeration.ClosureEntryKind
                    key
                    EvidenceEnumeration.EnumerationCapBound
            else
                EvidenceEnumeration.required
                    EvidenceChain.DependencyClosureHop
                    EvidenceEnumeration.ClosureEntryKind
                    key)

    /// The join from the deploy record to the closure its build
    /// resolved, answered by the deploy substrate's own slot comparison.
    ///
    /// Answers with the hop's outcome AND the positions the closure's own
    /// attestation states name. **Only the branch where the join HOLDS
    /// names any.** A closure nothing binds to this deployment is not an
    /// enumeration the walk owes a reader — the hop says so in one line,
    /// and claiming its entries were missing would report the absent join
    /// twice under two different names.
    let walkClosure (sources: EvidenceChainSources) : EvidenceHopOutcome * EnumerationPosition list =
        match sources.Closure, sources.Deploy with
        | None, _ ->
            EvidenceHopOutcome.bare (
                EvidenceLink.LinkAbsent
                    "no dependency closure is composed, so which resolved packages this deployment stands on is unrecorded"
            ),
            []
        | Some _, None ->
            EvidenceHopOutcome.bare (
                EvidenceLink.LinkAbsent
                    "a dependency closure is recorded and no deploy record binds its digest, so nothing joins it to this deployment"
            ),
            []
        | Some closure, Some signedRecord ->
            let computed = DeployRecords.closureDigest closure
            let entries = List.length closure.Entries

            let attested =
                closure.Entries
                |> List.filter (fun entry ->
                    match entry.Attestation with
                    | AttestedBy _ -> true
                    | Unattested _ -> false)
                |> List.length

            match DeployRecords.verifyClosure closure signedRecord.Record.Provenance with
            | Ok() ->
                {
                    Link =
                        EvidenceLink.Linked(
                            computed,
                            sprintf
                                "the deploy record binds closure %s, which is the closure in hand — %d resolved entries, %d standing on an attested upstream release"
                                computed
                                entries
                                attested
                        )
                    Findings = capFindings (unattestedFindings closure)
                },
                closurePositions closure
            | Error failures ->
                let describe =
                    failures |> List.map DeployRecords.DeployRecordVerificationFailure.describe

                let broken =
                    failures
                    |> List.exists (function
                        | DeployRecords.ClosureDigestMismatch _ -> true
                        | _ -> false)

                if broken then
                    {
                        Link =
                            EvidenceLink.LinkBroken(
                                computed,
                                "the deploy record's upstream-provenance slot does not carry the digest of the closure in hand — either the slot was filled with something else, which is legitimate, or this is not the closure the record was sealed over"
                            )
                        Findings = describe
                    },
                    []
                else
                    {
                        Link =
                            EvidenceLink.LinkAbsent
                                "the deploy record's upstream-provenance slot is empty, so nothing binds the closure in hand to it"
                        Findings = describe
                    },
                    []

    // ── Hop 4 — the sealed deploy record ─────────────────────────────

    /// The join from the deploy record to its own seal.
    ///
    /// A record with no sealer composed to check it is an ABSENT link,
    /// not a broken one: the seal is carried rather than checked, and
    /// nothing has failed. A sealer that refuses is the break.
    let walkDeployRecord (sources: EvidenceChainSources) : Async<EvidenceHopOutcome> = async {
        match sources.Deploy, sources.Sealer with
        | None, _ ->
            return
                EvidenceHopOutcome.bare (
                    EvidenceLink.LinkAbsent
                        "no sealed deploy record is composed, so what this deployment ran is unrecorded"
                )
        | Some signedRecord, None ->
            return {
                Link =
                    EvidenceLink.LinkAbsent(
                        sprintf
                            "deploy %s carries a '%s' seal and no sealer is composed to verify it, so the seal is carried rather than checked"
                            signedRecord.Record.DeployId
                            signedRecord.Seal.Scheme
                    )
                Findings = [
                    sprintf
                        "seal minted under key %s at %s"
                        signedRecord.Seal.KeyId
                        (signedRecord.Seal.SealedAt.ToString "o")
                ]
            }
        | Some signedRecord, Some sealer ->
            let! outcome = DeployRecords.verifySeal sealer signedRecord |> Async.Catch

            match outcome with
            | Choice2Of2 ex ->
                return
                    EvidenceHopOutcome.bare (
                        EvidenceLink.LinkBroken(
                            signedRecord.Record.DeployId,
                            sprintf "the seal check raised: %s" ex.Message
                        )
                    )
            | Choice1Of2(Ok()) ->
                return {
                    Link =
                        EvidenceLink.Linked(
                            signedRecord.Record.DeployId,
                            sprintf
                                "the '%s' seal minted under key %s covers this record's canonical bytes"
                                signedRecord.Seal.Scheme
                                signedRecord.Seal.KeyId
                        )
                    Findings = [
                        sprintf "build %s, tenant %s" signedRecord.Record.BuildId signedRecord.Record.TenantId
                        sprintf
                            "sealed at %s, claiming %s"
                            (signedRecord.Seal.SealedAt.ToString "o")
                            signedRecord.Seal.Claim
                    ]
                }
            | Choice1Of2(Error failures) ->
                return {
                    Link =
                        EvidenceLink.LinkBroken(
                            signedRecord.Record.DeployId,
                            "the seal does not cover this deploy record's canonical bytes"
                        )
                    Findings = failures |> List.map DeployRecords.DeployRecordVerificationFailure.describe
                }
    }

    // ── Hop 5 — the boot verification verdict ────────────────────────

    /// The join from the sealed composition to the one that actually
    /// booted, carried from the boot preflight rather than re-derived.
    let walkBootVerification (sources: EvidenceChainSources) : EvidenceHopOutcome =
        match sources.Boot with
        | None ->
            EvidenceHopOutcome.bare (
                EvidenceLink.LinkAbsent
                    "this deployment did not run the boot verification preflight, so nothing joins the sealed composition to the one that is running"
            )
        | Some(BootVerificationReading.BootVerified detail) ->
            EvidenceHopOutcome.bare (EvidenceLink.Linked(deployPosition sources, detail))
        | Some(BootVerificationReading.BootUnsealed reason) -> EvidenceHopOutcome.bare (EvidenceLink.LinkAbsent reason)
        | Some(BootVerificationReading.BootRejected(position, detail)) ->
            EvidenceHopOutcome.bare (EvidenceLink.LinkBroken(position, detail))

    // ── Hop 6 — the compliance evidence pack ─────────────────────────

    /// The join from this deployment to a signed compliance pack over
    /// its audit trail.
    ///
    /// An UNSIGNED pack is an absent link: the bundle exists and nothing
    /// binds it to this deployment, because no envelope signer is
    /// composed. Reading it as a link would credit the deployment for
    /// tamper evidence it does not have.
    let walkEvidencePack (sources: EvidenceChainSources) : Async<EvidenceHopOutcome> = async {
        match sources.Pack with
        | None ->
            return
                EvidenceHopOutcome.bare (
                    EvidenceLink.LinkAbsent
                        "no compliance evidence pack is composed, so this deployment's audit trail is not assembled into a reviewable bundle"
                )
        | Some read ->
            let! outcome = read () |> Async.Catch

            match outcome with
            | Choice2Of2 ex ->
                return
                    EvidenceHopOutcome.bare (
                        EvidenceLink.LinkBroken(
                            deployPosition sources,
                            sprintf "the evidence pack read raised: %s" ex.Message
                        )
                    )
            | Choice1Of2(Error reason) ->
                return
                    EvidenceHopOutcome.bare (
                        EvidenceLink.LinkBroken(
                            deployPosition sources,
                            sprintf "the evidence pack could not be assembled: %s" reason
                        )
                    )
            | Choice1Of2(Ok(EvidencePackReading.PackWithheld policy)) ->
                return EvidenceHopOutcome.bare (EvidenceLink.LinkWithheld policy)
            | Choice1Of2(Ok(EvidencePackReading.PackUnsigned(digest, entries))) ->
                return
                    EvidenceHopOutcome.bare (
                        EvidenceLink.LinkAbsent(
                            sprintf
                                "a pack of %d segment(s) assembled to manifest %s and no export envelope signer is composed, so nothing binds the bundle to this deployment"
                                entries
                                digest
                        )
                    )
            | Choice1Of2(Ok(EvidencePackReading.PackSigned(digest, entries))) ->
                return
                    EvidenceHopOutcome.bare (
                        EvidenceLink.Linked(
                            digest,
                            sprintf
                                "a signed manifest pins %d content-addressed segment(s) of this deployment's evidence"
                                entries
                        )
                    )
    }

    // ── Hop 7 — the ledger position ──────────────────────────────────

    /// The join from this deployment's evidence to a position in the
    /// hash-chained audit ledger — where the walk ends.
    ///
    /// Answers with the hop's outcome AND the position the ledger read
    /// reached, where it reached one. A ledger index is the one position
    /// a hop enumerates through its join key rather than through a
    /// finding line, which is why the completeness check reads a hop's
    /// whole rendered surface and not only its findings.
    let walkLedgerPosition (sources: EvidenceChainSources) : Async<EvidenceHopOutcome * EnumerationPosition list> = async {
        let indexPosition (position: int64) = [
            EvidenceEnumeration.required
                EvidenceChain.LedgerPositionHop
                EvidenceEnumeration.LedgerIndexKind
                (string position)
        ]

        match sources.Ledger with
        | None ->
            return
                EvidenceHopOutcome.bare (
                    EvidenceLink.LinkAbsent
                        "no hash-chained audit ledger is composed, so this deployment's evidence is anchored nowhere and carries no tamper evidence"
                ),
                []
        | Some read ->
            let! outcome = read () |> Async.Catch

            match outcome with
            | Choice2Of2 ex ->
                return
                    EvidenceHopOutcome.bare (
                        EvidenceLink.LinkBroken(deployPosition sources, sprintf "the ledger read raised: %s" ex.Message)
                    ),
                    []
            | Choice1Of2(Error reason) ->
                // Composed and would not answer. Reading this as absence
                // would make breaking your own ledger the cheapest way to
                // end the chain quietly.
                return
                    EvidenceHopOutcome.bare (
                        EvidenceLink.LinkBroken(
                            deployPosition sources,
                            sprintf "the ledger is composed and could not be read: %s" reason
                        )
                    ),
                    []
            | Choice1Of2(Ok(LedgerPositionReading.LedgerUnrecorded reason)) ->
                return EvidenceHopOutcome.bare (EvidenceLink.LinkAbsent reason), []
            | Choice1Of2(Ok(LedgerPositionReading.LedgerBroken(position, detail))) ->
                return
                    {
                        Link = EvidenceLink.LinkBroken(string position, detail)
                        Findings = [
                            sprintf
                                "records 0..%d chain cleanly; everything after the break is unevidenced"
                                (max 0L (position - 1L))
                        ]
                    },
                    indexPosition position
            | Choice1Of2(Ok(LedgerPositionReading.LedgerRecorded(position, headDigest))) ->
                return
                    {
                        Link =
                            EvidenceLink.Linked(
                                string position,
                                sprintf "this deployment's evidence chains to ledger position %d" position
                            )
                        Findings = [ sprintf "chain head '%s'" headDigest ]
                    },
                    indexPosition position
    }

/// The shipped walker over the substrates a deployment already records.
///
/// Bounded on both declared axes and refusing on both: a request outside
/// the declared depth range is refused before anything is fetched, and a
/// closure larger than the declared cap refuses the walk once it has
/// been counted. It never returns what it managed to reach — a partial
/// chain reads as a complete one, which is the failure this contract
/// exists to make impossible.
type DefaultEvidenceChainWalker(sources: EvidenceChainSources, caps: EvidenceChainCaps, clock: unit -> DateTime) =

    /// Lowercase-hex SHA-256 over the chain's canonical form. Server-side
    /// because `System.Security.Cryptography` is not Fable-compilable;
    /// the canonical form it hashes is declared in `Platform.Core`, so
    /// any host recomputes the same digest from the same chain.
    static member VerdictDigest(hops: EvidenceHop list) : string =
        let bytes = Encoding.UTF8.GetBytes(EvidenceChain.canonicalForm hops)
        Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    interface IEvidenceChainWalker with
        member _.GetCaps() = async { return caps }

        member _.Walk request = async {
            if request.WorkDepth < 1 then
                return Result.Error(ChainWorkDepthInvalid request.WorkDepth)
            elif request.WorkDepth > caps.MaxWorkDepth then
                return Result.Error(ChainWorkDepthExceedsCap(request.WorkDepth, caps.MaxWorkDepth))
            else
                let closureSize =
                    match sources.Closure with
                    | Some closure -> List.length closure.Entries
                    | None -> 0

                if closureSize > caps.MaxClosureEntries then
                    return Result.Error(ChainClosureExceedsCap(closureSize, caps.MaxClosureEntries))
                else
                    let! work, workPositions = walkWorkRecord sources request.WorkDepth
                    let! deploy = walkDeployRecord sources
                    let! pack = walkEvidencePack sources
                    let! ledger, ledgerPositions = walkLedgerPosition sources
                    let closure, closureExpected = walkClosure sources

                    let hops =
                        EvidenceChain.hops {
                            UpstreamWorkRecord = work
                            BuildTranscript = walkTranscript sources
                            DependencyClosure = closure
                            DeployRecord = deploy
                            BootVerification = walkBootVerification sources
                            EvidencePack = pack
                            LedgerPosition = ledger
                        }

                    return
                        Result.Ok {
                            SchemaVersion = EvidenceChain.SchemaVersion
                            Actor = request.Actor
                            WalkedAt = clock ()
                            Hops = hops
                            Outcome = EvidenceChain.outcomeOf hops
                            VerdictDigest = DefaultEvidenceChainWalker.VerdictDigest hops
                            // Derived from the linkage each hop resolved,
                            // then measured against what those hops
                            // rendered. The two are separate arguments
                            // because they must come from separate
                            // places: an expectation read back out of the
                            // render is satisfied by construction.
                            Enumeration =
                                EvidenceEnumeration.assess (workPositions @ closureExpected @ ledgerPositions) hops
                        }
        }

[<RequireQualifiedAccess>]
module EvidenceChainWalker =

    /// Scope the audited-read row is recorded under. The chain is
    /// deployment-wide and belongs to no tenant (GP 4).
    [<Literal>]
    let PlatformScopeId = "_platform"

    /// The walker a mode carries, if any. `NoEvidenceChainWalker` has
    /// none — there is no implementation behind the default case, which
    /// is what makes "registers nothing" structural rather than a
    /// promise about an empty implementation's behaviour.
    let ofMode (mode: EvidenceChainWalkerMode) : IEvidenceChainWalker option =
        match mode with
        | NoEvidenceChainWalker -> None
        | ComposedEvidenceChainWalker walker -> Some walker

    /// A walker over these sources at the shipped default caps.
    let create (sources: EvidenceChainSources) : IEvidenceChainWalker =
        DefaultEvidenceChainWalker(sources, EvidenceChainCaps.defaults, fun () -> DateTime.UtcNow)
        :> IEvidenceChainWalker

    /// A walker over these sources at declared caps, with an injected
    /// clock. The shape a test — or a deployment whose chains are
    /// genuinely larger — reaches for.
    let createWith
        (caps: EvidenceChainCaps)
        (clock: unit -> DateTime)
        (sources: EvidenceChainSources)
        : IEvidenceChainWalker =
        DefaultEvidenceChainWalker(sources, caps, clock) :> IEvidenceChainWalker

    /// The audited-read record.
    ///
    /// Awaited rather than fire-and-forget, for the reason the
    /// verification report awaits its own: `IAuditLog.Record` is
    /// documented best-effort and swallows its own failures, so awaiting
    /// costs nothing in the failure case — and a caller that exits
    /// immediately afterwards would otherwise race the write. A walk
    /// that left no trace because the process was faster than its own
    /// audit sink is the one outcome this row exists to prevent.
    let recordWalk (services: IServiceProvider) (chain: EvidenceChain) : Async<unit> = async {
        match services.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as auditLog ->
            let payload: EvidenceChainWalkedPayload = {
                Actor = chain.Actor
                Outcome = EvidenceChainOutcome.label chain.Outcome
                VerdictDigest = chain.VerdictDigest
                Hops =
                    chain.Hops
                    |> List.map (fun hop -> sprintf "%s=%s" hop.Id (EvidenceLink.label hop.Link))
                OccurredAt = DateTimeOffset.UtcNow
            }

            do! auditLog.Record(PlatformScopeId, EvidenceChainWalked payload)
        | _ -> ()
    }

    /// Walk the chain AND record the read — the entry a composed
    /// deployment calls. Exactly one audit row per completed walk.
    ///
    /// **A refused walk records nothing**, because nothing was walked.
    /// Recording refusals here would fill the trail the chain's own
    /// ledger hop reads with rows about walks that never happened, which
    /// is the same reasoning the verification report's gated endpoint
    /// applies to a refused caller.
    let run
        (services: IServiceProvider)
        (mode: EvidenceChainWalkerMode)
        (request: EvidenceChainRequest)
        : Async<Result<EvidenceChain, EvidenceChainError>> =
        async {
            match ofMode mode with
            | None ->
                // No walker composed. The honest answer is a complete hop
                // list of absences, not an error — and no audit row,
                // because nothing was read.
                let hops =
                    EvidenceChain.hops (
                        EvidenceChain.allAbsent "no evidence chain walker is composed in this deployment"
                    )

                return
                    Result.Ok {
                        SchemaVersion = EvidenceChain.SchemaVersion
                        Actor = request.Actor
                        WalkedAt = DateTime.UtcNow
                        Hops = hops
                        Outcome = EvidenceChain.outcomeOf hops
                        VerdictDigest = DefaultEvidenceChainWalker.VerdictDigest hops
                        // No walker, so no linkage names anything and the
                        // enumeration is complete over an empty set. That
                        // is not a claim the deployment recorded much —
                        // the chain's own outcome says `ChainUnrecorded`,
                        // and the two answer different questions.
                        Enumeration = EvidenceEnumeration.assess [] hops
                    }
            | Some walker ->
                match! walker.Walk request with
                | Result.Error error -> return Result.Error error
                | Result.Ok chain ->
                    do! recordWalk services chain
                    return Result.Ok chain
        }