// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.EvidenceEnumerationCompletenessTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.InProcess.BuildTranscriptTests

// ─── Phase 716 — the walk proves it enumerated everything ────────────
//
// The hop pack beside this one proves the walk visits every STAGE. It
// does not prove that, within a stage, the walk carried everything the
// stage's own linkage named — and a walk that stops at the first
// unresolvable parent ref renders as a clean short enumeration, which
// reads exactly like a genuinely short history.
//
// What this pack probes is the four claims that check makes:
//
//   * **the expected set is DERIVED from linkage.** The fixture below is
//     a three-record work chain, and the SAME deployment is walked at
//     three depths and against two source tables. The verdict moves with
//     the linkage rather than with anything configured, and the ladder is
//     asserted end to end so a derivation that always answered one way
//     could not pass.
//
//   * **a missing interior position is NAMED.** A source that holds the
//     head and not its parent is the motivating failure; the verdict
//     carries the unresolvable edge's own key, not a count.
//
//   * **a declared bound is not an omission.** The same absent parent
//     reads `Bounded` when the depth the caller asked for accounts for
//     it, and a walk refused at a cap is `Bounded` and never
//     `Incomplete`. Three states, and the ladder proves they are three.
//
//   * **a shorter render cannot pass.** Stripping a hop, and stripping
//     an enumeration line from a hop, each flip the verdict — including
//     when the expectation handed in is empty, because the stage tier is
//     read from the model's own declared order rather than from anything
//     a caller supplies.
//
// Plus the bundle arm: an exported bundle STATES the verdict, and a
// document whose stated verdict disagrees with the chain it qualifies is
// refused rather than read from whichever half a reader looked at.

// ─── Doubles ─────────────────────────────────────────────────────────

let private system = "work-system"

let private ref' (id: string) : WorkRecordRef = WorkRecordRef.create system id

/// A source system double over a fixed record table. A ref the table
/// does not hold answers `Absent`, which is the answer the ancestor walk
/// once dropped silently — and therefore the answer this pack exists to
/// make visible.
type private FakeWorkSource(records: Map<string, WorkRecordAnswer>) =
    interface IWorkProvenanceSource with
        member _.SourceSystem() = system
        member _.GetCaps() = async { return WorkProvenanceCaps.defaults }

        member _.GetRecord reference = async {
            return
                records
                |> Map.tryFind reference.RecordId
                |> Option.defaultValue WorkRecordAnswer.Absent
        }

        member this.GetAncestors request =
            WorkProvenanceSource.walkOverLookups (this :> IWorkProvenanceSource) request

        member _.Covering _ = async { return Result.Ok(WorkCoverage.Covered(ref' "w1")) }

// ─── Fixtures ────────────────────────────────────────────────────────

// The work chain: w1 <- w2 <- w3. Three levels, so a depth argument has
// somewhere to bite and the three verdicts are all reachable from one
// deployment.

let private record' (id: string) (parents: string list) : WorkRecord = {
    Ref = ref' id
    Kind = WorkRecordKind.Authored
    ContentDigest = id
    Parents = parents |> List.map ref'
    Verdict = None
    Label = id
}

let private w1 = record' "w1" [ "w2" ]
let private w2 = record' "w2" [ "w3" ]
let private w3 = record' "w3" []

/// Every record resolvable — the linkage and the enumeration can agree.
let private wholeTable =
    Map.ofList [
        "w1", WorkRecordAnswer.Found w1
        "w2", WorkRecordAnswer.Found w2
        "w3", WorkRecordAnswer.Found w3
    ]

/// The head and the grandparent resolve and the record BETWEEN them does
/// not. The ancestor walk asks for `w2` and is told `Absent`, so the
/// enumeration is one line where the linkage names two. The walk now
/// RECORDS that lost edge rather than moving on, so the hop reads broken
/// as well — the enumeration verdict this pack is about is derived from
/// the same recording, which is why the two agree here by construction
/// rather than by coincidence.
let private gappedTable =
    Map.ofList [ "w1", WorkRecordAnswer.Found w1; "w3", WorkRecordAnswer.Found w3 ]

let private closure =
    DependencyClosure.create [
        {
            Id = "Alpha.Package"
            Version = "1.2.3"
            Source = "https://packages.example"
            ContentDigest = "aa11"
            Attestation =
                AttestedBy {
                    OpId = "release-1"
                    ActDigest = "ee55"
                }
        }
        {
            Id = "Beta.Package"
            Version = "0.9.0"
            Source = ""
            ContentDigest = "bb22"
            Attestation = Unattested ExternalPackage
        }
    ]

/// The one closure entry the enumeration exists to carry, keyed as the
/// walk keys it — id and version, so two packages sharing a name prefix
/// cannot account for one another.
let private unattestedKey = "Beta.Package 0.9.0"

let private ledgerIndex = 12L

let private manifest: DeployManifest = {
    DeployManifest.empty with
        App = {
            Name = "Example"
            Slug = "example"
            Region = "eu-west"
        }
}

let private signedRecord =
    let record =
        DeployRecord.create
            "deploy-1"
            "tenant-1"
            "build-1"
            manifest
            (DeployProvenance.none |> DeployRecords.withClosure closure)

    let sealer = StubSealer "secret" :> IDeployRecordSealer

    match sealer.Seal(DeployRecords.canonicalBytes record) |> Async.RunSynchronously with
    | Ok seal -> { Record = record; Seal = seal }
    | Error reason -> failtestf "the stub sealer refused to seal the fixture: %s" reason

let private sourcesOver (table: Map<string, WorkRecordAnswer>) : EvidenceChainSources = {
    EvidenceChainSources.none with
        Work = ComposedWorkProvenanceSource(FakeWorkSource table :> IWorkProvenanceSource)
        Closure = Some closure
        Deploy = Some signedRecord
        Ledger = Some(fun () -> async { return Ok(LedgerPositionReading.LedgerRecorded(ledgerIndex, "head-digest")) })
}

let private walkAt (caps: EvidenceChainCaps) (table: Map<string, WorkRecordAnswer>) (depth: int) =
    let walker =
        EvidenceChainWalker.createWith caps (fun () -> DateTime.UnixEpoch) (sourcesOver table)

    walker.Walk { Actor = "probe"; WorkDepth = depth } |> Async.RunSynchronously

let private chainAt (table: Map<string, WorkRecordAnswer>) (depth: int) : EvidenceChain =
    match walkAt EvidenceChainCaps.defaults table depth with
    | Ok chain -> chain
    | Error error -> failtestf "the walk was refused: %s" (EvidenceChainError.describe error)

let private verdictAt table depth =
    EnumerationCompleteness.label (chainAt table depth).Enumeration

/// The one arrangement in which every derived position is enumerated:
/// the whole work chain within the requested depth, the unattested
/// closure entry rendered, the ledger index named by the join key.
let private completeChain () = chainAt wholeTable 3

// ─── A — the expectation is derived from linkage ─────────────────────

let derivationTests =
    testList "Phase 716 — the expected positions are derived from the chain's own linkage" [

        test "the verdict moves with the linkage, across depths and across source tables" {
            // Verify the probe, not just the verdict: a derivation that
            // always answered one way would pass any single arm below.
            // The SAME deployment produces all three verdicts, and which
            // one depends only on what the linkage named and what the
            // walk could reach.
            let ladder = [
                "whole chain, depth 1 — the caller's own depth holds the parent back", wholeTable, 1, "bounded"
                "whole chain, depth 2 — the grandparent is still beyond the bound", wholeTable, 2, "bounded"
                "whole chain, depth 3 — every named record is enumerated", wholeTable, 3, "complete"
                "gapped chain, depth 1 — the same bound still accounts for it", gappedTable, 1, "bounded"
                "gapped chain, depth 2 — nothing accounts for the missing parent", gappedTable, 2, "incomplete"
                "gapped chain, depth 3 — and it stays a finding as the bound widens", gappedTable, 3, "incomplete"
            ]

            for name, table, depth, expected in ladder do
                Expect.equal (verdictAt table depth) expected name
        }

        test "the ladder walks the same hops throughout — only the enumeration verdict moves" {
            // The completeness claim is about what a hop enumerated, not
            // about which hops exist. If the hop list moved with it, the
            // arms above would be re-proving Phase 713's invariant under
            // a new name.
            for table, depth in [ wholeTable, 1; wholeTable, 3; gappedTable, 2 ] do
                let chain = chainAt table depth

                Expect.equal
                    (chain.Hops |> List.map _.Id)
                    EvidenceChain.order
                    "every arrangement returns the full hop list in walk order"

            // The outcome is a separate axis from the enumeration verdict
            // — but the two are not INDEPENDENT, and since the ancestor
            // walk began recording the edges it cannot follow they move
            // together on the gapped table by construction: an
            // unresolvable parent is a recorded join that does not hold,
            // so the hop reads broken and the enumeration behind it
            // reads incomplete, from one recording. The depth ladder over
            // the WHOLE table is where the axes genuinely separate, and
            // it is asserted here rather than the gapped one.
            for depth in [ 1; 3 ] do
                Expect.equal
                    (EvidenceChainOutcome.label (chainAt wholeTable depth).Outcome)
                    (EvidenceChainOutcome.label (chainAt wholeTable 3).Outcome)
                    "narrowing the requested depth moves the enumeration verdict and not the chain's outcome"

            Expect.equal
                (EvidenceChainOutcome.label (chainAt gappedTable 2).Outcome)
                "chain-broken"
                "while a chain that LOST an edge its own records named is broken on both axes, and says so on each"
        }

        test "a deployment whose linkage names nothing is complete over an empty set" {
            let walker = EvidenceChainWalker.create EvidenceChainSources.none

            match walker.Walk(EvidenceChainRequest.forActor "probe") |> Async.RunSynchronously with
            | Ok chain ->
                Expect.equal
                    (EnumerationCompleteness.label chain.Enumeration)
                    "complete"
                    "an empty enumeration is complete; the chain's own outcome is what says it is empty"

                Expect.equal
                    chain.Outcome
                    EvidenceChainOutcome.ChainUnrecorded
                    "and the outcome still reports the emptiness, so the two are not confused"
            | Error error -> failtestf "an uncomposed deployment must answer: %s" (EvidenceChainError.describe error)
        }
    ]

// ─── B — the verdict names the missing positions ─────────────────────

let missingPositionTests =
    testList "Phase 716 — an interior position nothing accounts for is named" [

        test "the missing parent record is carried by key, not by count" {
            match (chainAt gappedTable 3).Enumeration with
            | EnumerationCompleteness.Incomplete(missing, reason) ->
                Expect.equal
                    (missing |> List.map _.Key)
                    [ "w1->w2" ]
                    "the EDGE the source would not resolve is named — which record named which unresolvable parent — so the finding is actionable from the one line"

                Expect.equal
                    (missing |> List.map _.Hop)
                    [ EvidenceChain.UpstreamWorkRecordHop ]
                    "and attributed to the hop whose enumeration dropped it"

                Expect.equal
                    (missing |> List.map _.Kind)
                    [ EvidenceEnumeration.WorkAncestorKind ]
                    "carrying what kind of position it is"

                Expect.isTrue
                    (missing |> List.forall (fun position -> Option.isNone position.Bound))
                    "a position with a declared bound is not a missing one"

                Expect.isGreaterThan reason.Length 20 "the reason explains the finding rather than restating the count"
            | other -> failtestf "a chain missing an interior position must report incomplete: %A" other
        }

        test "the description names the missing position too" {
            let described = EnumerationCompleteness.describe (chainAt gappedTable 3).Enumeration

            Expect.stringContains described "w2" "the one-line description is actionable on its own"
        }

        test "an omission outranks a bound that applied elsewhere" {
            // A cap somewhere else in the walk is not an excuse for a
            // position nothing accounts for; a fold that let one silence
            // the other would let a deployment buy silence by declaring a
            // bound it never needed.
            let hops = (completeChain ()).Hops

            let expected = [
                EvidenceEnumeration.required
                    EvidenceChain.DependencyClosureHop
                    EvidenceEnumeration.ClosureEntryKind
                    "Gamma.Package 3.0.0"
                EvidenceEnumeration.bounded
                    EvidenceChain.UpstreamWorkRecordHop
                    EvidenceEnumeration.WorkAncestorKind
                    "w9"
                    EvidenceEnumeration.WorkDepthBound
            ]

            match EvidenceEnumeration.assess expected hops with
            | EnumerationCompleteness.Incomplete(missing, _) ->
                Expect.equal
                    (missing |> List.map _.Key)
                    [ "Gamma.Package 3.0.0" ]
                    "the unexcused position is the finding, and the excused one is not folded into it"
            | other -> failtestf "an omission must outrank a bound: %A" other
        }
    ]

// ─── C — a cap is not a completeness failure ─────────────────────────

let boundedTests =
    testList "Phase 716 — a declared bound reads bounded, never incomplete" [

        test "the depth the caller asked for accounts for the parent beyond it" {
            match (chainAt wholeTable 1).Enumeration with
            | EnumerationCompleteness.Bounded bounds ->
                Expect.equal
                    (bounds |> List.map _.Bound)
                    [ EvidenceEnumeration.WorkDepthBound ]
                    "the bound that applied is named, so a reader knows which limit to raise"

                Expect.equal
                    (bounds |> List.map _.Hop)
                    [ EvidenceChain.UpstreamWorkRecordHop ]
                    "and which hop it applied to"

                Expect.isGreaterThan
                    (bounds |> List.sumBy _.Unenumerated)
                    0
                    "with how much it held back — a bound that could not say how much is a silence"
            | other -> failtestf "a walk stopped at the requested depth is bounded: %A" other
        }

        test "a walk REFUSED at a declared cap is bounded and never incomplete" {
            // The refusal itself is the caller being told, which is the
            // whole difference between a bound and an omission.
            let refusals = [
                "an over-cap depth", ChainWorkDepthExceedsCap(11, 10)
                "an invalid depth", ChainWorkDepthInvalid 0
                "an over-cap closure", ChainClosureExceedsCap(5, 3)
            ]

            for name, refusal in refusals do
                let verdict = EvidenceEnumeration.ofRefusal refusal

                Expect.equal
                    (EnumerationCompleteness.label verdict)
                    "bounded"
                    (sprintf "%s is a told limit, not an omission" name)

                Expect.isFalse
                    (EnumerationCompleteness.isComplete verdict)
                    (sprintf "%s is emphatically not a pass either" name)
        }

        test "the closure cap that refuses a real walk maps to that same bounded verdict" {
            // Verify the probe: `ofRefusal` is only worth anything if the
            // walker actually produces the refusal it reads.
            let caps = {
                EvidenceChainCaps.defaults with
                    MaxClosureEntries = 1
            }

            match walkAt caps wholeTable 3 with
            | Error(ChainClosureExceedsCap(entries, cap) as refusal) ->
                Expect.equal entries 2 "the refusal counts the closure it would not trim"
                Expect.equal cap 1 "and names the cap"

                match EvidenceEnumeration.ofRefusal refusal with
                | EnumerationCompleteness.Bounded bounds ->
                    Expect.equal
                        (bounds |> List.map _.Bound)
                        [ EvidenceEnumeration.ClosureCapBound ]
                        "the declared closure cap is what held the enumeration back"

                    Expect.equal
                        (bounds |> List.map _.Unenumerated)
                        [ 2 ]
                        "and the whole closure went unenumerated, which the verdict says rather than implies"
                | other -> failtestf "a cap-refused walk must read bounded: %A" other
            | other -> failtestf "an over-cap closure must refuse the walk whole: %A" other
        }

        test "the three verdicts are three, and none of them reads as another" {
            let labels = [
                EnumerationCompleteness.label EnumerationCompleteness.Complete
                EnumerationCompleteness.label (
                    EnumerationCompleteness.Bounded [
                        {
                            Hop = EvidenceChain.UpstreamWorkRecordHop
                            Bound = EvidenceEnumeration.WorkDepthBound
                            Unenumerated = 1
                        }
                    ]
                )
                EnumerationCompleteness.label (EnumerationCompleteness.Incomplete([], "nothing accounts for it"))
            ]

            Expect.equal (List.distinct labels) labels "collapsing any pair loses the reader's next action"

            Expect.equal labels [ "complete"; "bounded"; "incomplete" ] "and the wire labels are stable"

            Expect.isTrue
                (EnumerationCompleteness.isComplete EnumerationCompleteness.Complete)
                "only a complete enumeration reads complete"
        }
    ]

// ─── D — a shorter render cannot satisfy the derivation ──────────────

let shorterRenderTests =
    testList "Phase 716 — the derivation cannot be satisfied by rendering less" [

        test "stripping an enumeration line flips the verdict, naming the line's position" {
            let hops = (completeChain ()).Hops

            let expected = [
                EvidenceEnumeration.required
                    EvidenceChain.DependencyClosureHop
                    EvidenceEnumeration.ClosureEntryKind
                    unattestedKey
            ]

            Expect.equal
                (EnumerationCompleteness.label (EvidenceEnumeration.assess expected hops))
                "complete"
                "the rendered chain accounts for the position before it is stripped"

            let stripped =
                hops
                |> List.map (fun hop ->
                    if hop.Id = EvidenceChain.DependencyClosureHop then
                        { hop with Findings = [] }
                    else
                        hop)

            match EvidenceEnumeration.assess expected stripped with
            | EnumerationCompleteness.Incomplete(missing, _) ->
                Expect.equal
                    (missing |> List.map _.Key)
                    [ unattestedKey ]
                    "a render that carries less is measured against what the linkage named, not against itself"
            | other -> failtestf "a stripped enumeration must not pass: %A" other
        }

        test "a render carrying fewer HOPS fails whatever expectation it is handed" {
            // The stage tier is read from the model's own declared order
            // rather than from anything a caller supplies, so the empty
            // expectation — the most permissive one there is — still
            // cannot excuse a dropped hop.
            let shortened =
                (completeChain ()).Hops
                |> List.filter (fun hop -> hop.Id <> EvidenceChain.LedgerPositionHop)

            match EvidenceEnumeration.assess [] shortened with
            | EnumerationCompleteness.Incomplete(missing, _) ->
                Expect.equal
                    (missing |> List.map _.Key)
                    [ EvidenceChain.LedgerPositionHop ]
                    "the dropped stage is named"

                Expect.equal
                    (missing |> List.map _.Kind)
                    [ EvidenceEnumeration.HopKind ]
                    "as a stage rather than as an enumeration line"
            | other -> failtestf "a short hop list must not pass an empty expectation: %A" other
        }

        test "the full hop list under an empty expectation is complete — so the arm above measures the loss" {
            Expect.equal
                (EnumerationCompleteness.label (EvidenceEnumeration.assess [] (completeChain ()).Hops))
                "complete"
                "without this the arm above would pass against a check that refused everything"
        }

        test "a position is accounted for by the whole rendered surface, join key included" {
            // The ledger enumerates through its join key and nowhere
            // else; a check that read only the findings would report a
            // rendered position as missing.
            let ledgerHop =
                (completeChain ()).Hops
                |> List.find (fun hop -> hop.Id = EvidenceChain.LedgerPositionHop)

            Expect.isTrue
                (EvidenceEnumeration.accountsFor ledgerHop (string ledgerIndex))
                "the ledger index the read reached is named by the hop's join key"

            Expect.isFalse
                (EvidenceEnumeration.accountsFor ledgerHop "not-a-position-this-hop-names")
                "and a key the hop never rendered is not accounted for"
        }
    ]

// ─── The exported bundle states the verdict ──────────────────────────

let bundleStatementTests =
    testList "Phase 716 — an exported bundle states what its walk enumerated" [

        test "every bundle carries the verdict, including a complete one" {
            let observedAt = DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc)

            for label, chain in [ "complete", completeChain (); "incomplete", chainAt gappedTable 3 ] do
                let bundle = EvidenceBundleExport.bundleOf "deployment-under-test" observedAt chain

                let qualifier =
                    bundle.Qualifiers
                    |> List.tryFind (fun q -> q.Id = EvidenceBundle.EnumerationQualifierId)
                    |> Option.defaultWith (fun () ->
                        failtestf "a %s bundle must state its enumeration verdict too" label)

                Expect.equal qualifier.Verdict label "the bundle states the verdict its chain carries"

                Expect.isGreaterThan qualifier.Detail.Length 20 "and quotes the evidence rather than the conclusion"

                Expect.stringContains (EvidenceBundle.render bundle) label "the rendered bundle shows it to an operator"
        }

        test "the verdict rides where the content id covers it" {
            let observedAt = DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc)

            let bundle =
                EvidenceBundleExport.bundleOf "deployment-under-test" observedAt (completeChain ())

            let swapped = {
                bundle with
                    Qualifiers = [
                        EvidenceBundle.enumerationQualifier (
                            EnumerationCompleteness.Incomplete([], "a friendlier account of the same walk")
                        )
                    ]
            }

            Expect.notEqual
                (EvidenceBundleExport.digest (EvidenceBundle.canonicalForm swapped))
                bundle.ContentId
                "swapping the stated verdict re-addresses the bundle, so it cannot be swapped quietly"
        }

        test "a document whose stated verdict disagrees with its chain is refused" {
            let observedAt = DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc)

            let bundle =
                EvidenceBundleExport.bundleOf "deployment-under-test" observedAt (completeChain ())

            Expect.isTrue
                (BundleIntegrity.isIntact (EvidenceBundleExport.verifyBundle bundle))
                "the exported bundle verifies as it stands"

            // Re-addressed, so the mismatch is the ONLY thing left for
            // the verifier to find — a disagreement that presented as a
            // content-id fault would be reported in the wrong place.
            let disagreeing =
                let swapped = {
                    bundle with
                        Qualifiers = [
                            EvidenceBundle.enumerationQualifier (
                                EnumerationCompleteness.Incomplete([], "a friendlier account of the same walk")
                            )
                        ]
                }

                {
                    swapped with
                        ContentId = EvidenceBundleExport.digest (EvidenceBundle.canonicalForm swapped)
                }

            match EvidenceBundleExport.verifyBundle disagreeing with
            | BundleIntegrity.BrokenAt(position, reason) ->
                Expect.stringContains
                    position
                    EvidenceBundle.EnumerationQualifierId
                    "the finding names where the two halves disagree"

                Expect.stringContains reason "incomplete" "and quotes what the document says"
            | BundleIntegrity.Intact ->
                failtest "a document that says two things about what its walk enumerated says neither"
        }
    ]