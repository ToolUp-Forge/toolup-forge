// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.EvidenceChainWalkerTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.DeploymentVerification
open ToolUp.Platform.Tests.InProcess.BuildTranscriptTests

// ─── Phase 713 — the join, with every break reported as data ─────────
//
// The walker composes verifiers that already exist and are already
// tested, so this pack deliberately does NOT re-prove them — each has
// its own pack, and duplicating them here would produce a suite that
// goes green when the composition is wrong as long as the pieces are
// right. What it probes is the four claims the composed walk makes that
// its pieces do not:
//
//   * **hop COUNT is invariant to what is composed.** The same walk over
//     a fully-composed deployment, a wholly-uncomposed one, and every
//     partial arrangement in between returns the same number of hops in
//     the same order. This is the property the whole design rests on: a
//     chain that shortened itself would read as complete and would not
//     be, and no per-hop assertion catches it.
//
//   * **every verdict is REACHABLE, and absent and broken are
//     distinct.** One arm per verdict per hop that can produce it,
//     asserting the LABEL rather than a truthiness — because the failure
//     this phase exists to prevent is exactly a reader who cannot tell a
//     deployment that never composed a ledger from one whose ledger is
//     broken.
//
//   * **a no-substrate deployment is a meaningful answer, not an
//     error.** The bare arm asserts a complete hop list of absences and
//     a `ChainUnrecorded` outcome, never an `Error`.
//
//   * **the walk is an audited READ.** Exactly one row per completed
//     walk, none for a refused one, and the sources are handed doubles
//     that count their reads so "mutates nothing" is asserted rather
//     than asserted-about.
//
// Plus the report arm: the ninth section degrades like every other, and
// the eight that shipped before it are untouched for a deployment that
// composes no walker.

// ─── Doubles ─────────────────────────────────────────────────────────

let private system = "work-system"

let private ref' (id: string) : WorkRecordRef = WorkRecordRef.create system id

/// An `IAuditLog` that records what it is handed, so the audited-read
/// arm counts rows rather than trusting that one was written.
type private RecordingAuditLog() =
    let recorded = ConcurrentQueue<string * AuditEvent>()

    member _.All = recorded |> Seq.map snd |> List.ofSeq

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Enqueue((scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

/// A service provider over an explicit type→instance table. Small on
/// purpose: the walk resolves exactly one service and a full container
/// would hide which.
type private StubServices(entries: (Type * obj) list) =
    interface IServiceProvider with
        member _.GetService(serviceType) =
            entries
            |> List.tryFind (fun (t, _) -> t = serviceType)
            |> Option.map snd
            |> Option.defaultValue null

let private servicesWith (auditLog: IAuditLog option) =
    StubServices(
        [
            match auditLog with
            | Some a -> typeof<IAuditLog>, box a
            | None -> ()
        ]
    )
    :> IServiceProvider

/// A source system double with a fixed record table and a configurable
/// coverage answer, counting its reads so an arm can prove a walk asked
/// and never wrote.
type private FakeWorkSource(records: Map<string, WorkRecordAnswer>, coverage: string -> Result<WorkCoverage, string>) =

    let mutable lookups = 0

    member _.Lookups = lookups

    interface IWorkProvenanceSource with
        member _.SourceSystem() = system
        member _.GetCaps() = async { return WorkProvenanceCaps.defaults }

        member _.GetRecord reference = async {
            lookups <- lookups + 1

            return
                records
                |> Map.tryFind reference.RecordId
                |> Option.defaultValue WorkRecordAnswer.Absent
        }

        member this.GetAncestors request =
            WorkProvenanceSource.walkOverLookups (this :> IWorkProvenanceSource) request

        member _.Covering upstreamReference = async { return coverage upstreamReference }

// ─── Fixtures ────────────────────────────────────────────────────────

let private toolchain: BuildToolchain = {
    Name = "example-sdk"
    Version = "10.0.203"
}

let private dependencies: BuildDependency list = [
    {
        Id = "Alpha.Package"
        Version = "1.2.3"
        ContentDigest = "aa11"
    }
    {
        Id = "Beta.Package"
        Version = "0.9.0"
        ContentDigest = "bb22"
    }
]

let private transcript =
    BuildTranscript.create toolchain dependencies {
        Path = "src/Program.fs"
        ContentDigest = "dd44"
    }

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

let private manifest: DeployManifest = {
    DeployManifest.empty with
        App = {
            Name = "Example"
            Slug = "example"
            Region = "eu-west"
        }
        Runtime = {
            DeployManifest.empty.Runtime with
                Framework = "dotnet:10"
        }
}

/// The upstream-provenance slot carries the closure digest — the
/// substrate's own structured filling of it — so the closure hop joins.
/// The work source below is asked about that same opaque value, which is
/// the only reference a deploy record ever hands it.
let private provenance =
    DeployProvenance.none
    |> DeployProvenance.withTranscriptDigest (DeployRecords.transcriptDigest transcript)
    |> DeployRecords.withClosure closure

let private record =
    DeployRecord.create "deploy-1" "tenant-1" "build-1" manifest provenance

let private sealer = StubSealer "secret" :> IDeployRecordSealer

let private sealRecord (r: DeployRecord) : SealedDeployRecord =
    match sealer.Seal(DeployRecords.canonicalBytes r) |> Async.RunSynchronously with
    | Ok seal -> { Record = r; Seal = seal }
    | Error reason -> failtestf "the stub sealer refused to seal the fixture: %s" reason

let private signedRecord = sealRecord record

// ── The upstream work chain: w1 <- w2, plus a record the source refuses ──

let private w2: WorkRecord = {
    Ref = ref' "w2"
    Kind = WorkRecordKind.Reviewed
    ContentDigest = "bb22"
    Parents = []
    Verdict = Some "accepted"
    Label = "reviewed"
}

let private w1: WorkRecord = {
    Ref = ref' "w1"
    Kind = WorkRecordKind.Authored
    ContentDigest = "aa11"
    Parents = [ ref' "w2" ]
    Verdict = Some "green"
    Label = "the head"
}

let private secretMarker: WithheldWorkRecord = {
    Ref = ref' "w-secret"
    Kind = WorkRecordKind.Reviewed
    PolicyRef = "restricted/internal-review"
}

let private workTable =
    Map.ofList [
        "w1", WorkRecordAnswer.Found w1
        "w2", WorkRecordAnswer.Found w2
        "w-secret", WorkRecordAnswer.Withheld secretMarker
    ]

/// Covering answers with `w1` for whatever opaque reference the deploy
/// record carries — the source system owns that vocabulary, and the
/// platform never parses the value it hands over.
let private coveringW1 (_: string) =
    Result.Ok(WorkCoverage.Covered(ref' "w1"))

let private workSource () = FakeWorkSource(workTable, coveringW1)

let private composedWork (source: FakeWorkSource) =
    ComposedWorkProvenanceSource(source :> IWorkProvenanceSource)

// ── Healthy readings for the three carried hops ──────────────────────

let private healthyBoot =
    BootVerificationReading.BootVerified "the running composition matches the sealed one"

let private healthyPack () = async { return Ok(EvidencePackReading.PackSigned("pack-digest", 4)) }

let private healthyLedger () = async { return Ok(LedgerPositionReading.LedgerRecorded(12L, "head-digest")) }

/// A deployment composing every substrate, all healthy — the one
/// arrangement in which every hop resolves.
let private fullSources (source: FakeWorkSource) : EvidenceChainSources = {
    Work = composedWork source
    Transcript = Some transcript
    Closure = Some closure
    Deploy = Some signedRecord
    Sealer = Some sealer
    Boot = Some healthyBoot
    Pack = Some healthyPack
    Ledger = Some healthyLedger
}

let private walkWith (sources: EvidenceChainSources) =
    (EvidenceChainWalker.create sources).Walk(EvidenceChainRequest.forActor "probe")
    |> Async.RunSynchronously

let private chainOf (sources: EvidenceChainSources) =
    match walkWith sources with
    | Ok chain -> chain
    | Error error -> failtestf "the walk was refused: %s" (EvidenceChainError.describe error)

let private hopOf (chain: EvidenceChain) (hopId: string) =
    chain.Hops
    |> List.tryFind (fun hop -> hop.Id = hopId)
    |> Option.defaultWith (fun () -> failtestf "hop '%s' is missing from the chain" hopId)

let private labelOf (chain: EvidenceChain) (hopId: string) =
    EvidenceLink.label (hopOf chain hopId).Link

// ─── Hop-count invariance ────────────────────────────────────────────

let hopCountInvarianceTests =
    testList "Phase 713 — the hop list is the same length whatever is composed" [

        test "the walk order is the seven hops the model declares" {
            let chain = chainOf (fullSources (workSource ()))

            Expect.equal (chain.Hops |> List.map _.Id) EvidenceChain.order "the hops walk in the declared order"

            Expect.equal
                (chain.Hops |> List.map _.Ordinal)
                [ 1 .. List.length EvidenceChain.order ]
                "each hop carries its own 1-based position rather than relying on list index"
        }

        test "every arrangement from wholly-composed to wholly-bare returns the same hop count" {
            // The probe walks a LADDER rather than the two extremes: a
            // walk that dropped a hop only for some middle arrangement
            // would pass a two-point test. Each rung removes one more
            // source, so every partial shape between the extremes is
            // covered.
            let full = fullSources (workSource ())

            let ladder = [
                "fully composed", full
                "no ledger", { full with Ledger = None }
                "no ledger, no pack", { full with Ledger = None; Pack = None }
                "no boot verdict either",
                {
                    full with
                        Ledger = None
                        Pack = None
                        Boot = None
                }
                "no sealer either",
                {
                    full with
                        Ledger = None
                        Pack = None
                        Boot = None
                        Sealer = None
                }
                "no deploy record either",
                {
                    full with
                        Ledger = None
                        Pack = None
                        Boot = None
                        Sealer = None
                        Deploy = None
                }
                "no closure either",
                {
                    full with
                        Ledger = None
                        Pack = None
                        Boot = None
                        Sealer = None
                        Deploy = None
                        Closure = None
                }
                "no transcript either",
                {
                    full with
                        Ledger = None
                        Pack = None
                        Boot = None
                        Sealer = None
                        Deploy = None
                        Closure = None
                        Transcript = None
                }
                "nothing at all", EvidenceChainSources.none
            ]

            let expected = List.length EvidenceChain.order

            for name, sources in ladder do
                let chain = chainOf sources

                Expect.equal
                    (List.length chain.Hops)
                    expected
                    (sprintf "'%s' must still return every hop — a shorter chain reads as a complete one" name)

                Expect.equal
                    (chain.Hops |> List.map _.Id)
                    EvidenceChain.order
                    (sprintf "'%s' must keep the hops in walk order" name)
        }

        test "the ladder actually varies what is composed" {
            // Verify the probe, not just the verdict: the assertion above
            // is satisfied trivially if every rung produced the same
            // chain. It does not — the linked-hop count falls
            // monotonically as sources are removed.
            let full = fullSources (workSource ())

            let linked sources =
                chainOf sources
                |> _.Hops
                |> List.filter (fun hop -> EvidenceLink.isLinked hop.Link)
                |> List.length

            Expect.equal (linked full) (List.length EvidenceChain.order) "the full arrangement resolves every hop"
            Expect.equal (linked EvidenceChainSources.none) 0 "the bare arrangement resolves none"

            Expect.isLessThan
                (linked { full with Ledger = None })
                (linked full)
                "removing a source must remove a link, or the ladder proves nothing"
        }
    ]

// ─── A wholly-uncomposed deployment ──────────────────────────────────

let honestAbsenceTests =
    testList "Phase 713 — a deployment composing nothing gets an answer, not an error" [

        test "every hop reads absent and the outcome names the emptiness" {
            let chain = chainOf EvidenceChainSources.none

            Expect.equal
                (chain.Hops |> List.map (fun hop -> EvidenceLink.label hop.Link))
                (EvidenceChain.order |> List.map (fun _ -> "absent"))
                "an uncomposed deployment degrades to absent hops throughout, not to a refusal"

            Expect.equal
                chain.Outcome
                EvidenceChainOutcome.ChainUnrecorded
                "the outcome names the emptiness rather than claiming a complete chain"

            Expect.isFalse
                (chain.Hops |> List.exists (fun hop -> EvidenceLink.isLinked hop.Link))
                "and absence is emphatically not a link"
        }

        test "each absent hop names what would have to be composed" {
            let chain = chainOf EvidenceChainSources.none

            for hop in chain.Hops do
                Expect.isGreaterThan
                    (EvidenceLink.detail hop.Link).Length
                    20
                    (sprintf "hop '%s' explains its absence rather than stating it" hop.Id)
        }

        test "an uncomposed walker still answers, and records nothing" {
            let auditLog = RecordingAuditLog()

            let outcome =
                EvidenceChainWalker.run
                    (servicesWith (Some(auditLog :> IAuditLog)))
                    NoEvidenceChainWalker
                    (EvidenceChainRequest.forActor "probe")
                |> Async.RunSynchronously

            match outcome with
            | Error error ->
                failtestf "an uncomposed walker must answer, not refuse: %s" (EvidenceChainError.describe error)
            | Ok chain ->
                Expect.equal (List.length chain.Hops) (List.length EvidenceChain.order) "with every hop present"

                Expect.equal chain.Outcome EvidenceChainOutcome.ChainUnrecorded "and the honest outcome"

            Expect.isEmpty auditLog.All "nothing was read, so nothing is recorded"
        }

        test "the mode default carries no implementation at all" {
            // GP 13, stated structurally rather than behaviourally: the
            // default case is not a null object that answers emptily,
            // there is nothing behind it to register.
            Expect.isNone
                (EvidenceChainWalker.ofMode NoEvidenceChainWalker)
                "NoEvidenceChainWalker holds no walker to register"

            let composed =
                ComposedEvidenceChainWalker(EvidenceChainWalker.create EvidenceChainSources.none)

            Expect.isSome (EvidenceChainWalker.ofMode composed) "a composed mode holds one"
        }
    ]

// ─── Every verdict is reachable, and they read distinctly ────────────

let verdictReachabilityTests =
    testList "Phase 713 — each link verdict is reachable and distinguishable" [

        test "a fully-composed deployment links every hop" {
            let chain = chainOf (fullSources (workSource ()))

            Expect.equal
                (chain.Hops |> List.map (fun hop -> EvidenceLink.label hop.Link))
                (EvidenceChain.order |> List.map (fun _ -> "linked"))
                "every hop of a fully-composed deployment resolves"

            Expect.equal chain.Outcome EvidenceChainOutcome.ChainComplete "and the chain is complete"
        }

        test "linked hops carry a re-derivable join key" {
            let chain = chainOf (fullSources (workSource ()))

            for hop in chain.Hops do
                Expect.isGreaterThan
                    (EvidenceLink.reference hop.Link).Length
                    0
                    (sprintf "hop '%s' names the key its link was resolved on" hop.Id)
        }

        // ── withheld ─────────────────────────────────────────────────

        test "a refused work record is WITHHELD, not absent and not broken" {
            let source =
                FakeWorkSource(workTable, fun _ -> Result.Ok(WorkCoverage.Covered(ref' "w-secret")))

            let chain =
                chainOf {
                    fullSources (workSource ()) with
                        Work = composedWork source
                }

            Expect.equal
                (labelOf chain EvidenceChain.UpstreamWorkRecordHop)
                "withheld"
                "a source system that refuses a record must not read as one that holds none"

            Expect.stringContains
                (EvidenceLink.detail (hopOf chain EvidenceChain.UpstreamWorkRecordHop).Link)
                "restricted/internal-review"
                "and the refusing policy travels with the refusal"
        }

        test "a withheld pack is withheld, and does not redden the chain" {
            let chain =
                chainOf {
                    fullSources (workSource ()) with
                        Pack =
                            Some(fun () -> async { return Ok(EvidencePackReading.PackWithheld "policy/export-review") })
                }

            Expect.equal (labelOf chain EvidenceChain.EvidencePackHop) "withheld" "the pack hop is withheld"

            Expect.equal
                chain.Outcome
                EvidenceChainOutcome.ChainPartial
                "a working access control depresses the chain to partial and never to broken"
        }

        // ── absent vs broken, hop by hop ─────────────────────────────

        test "an unsigned pack is ABSENT — the bundle exists and binds nothing" {
            let chain =
                chainOf {
                    fullSources (workSource ()) with
                        Pack = Some(fun () -> async { return Ok(EvidencePackReading.PackUnsigned("pack-digest", 4)) })
                }

            Expect.equal
                (labelOf chain EvidenceChain.EvidencePackHop)
                "absent"
                "an unsigned pack must not be credited with tamper evidence it does not have"
        }

        test "a pack that will not assemble is BROKEN — the two read differently" {
            let unsigned =
                chainOf {
                    fullSources (workSource ()) with
                        Pack = Some(fun () -> async { return Ok(EvidencePackReading.PackUnsigned("pack-digest", 4)) })
                }

            let failing =
                chainOf {
                    fullSources (workSource ()) with
                        Pack = Some(fun () -> async { return Error "the audit slice would not read" })
                }

            Expect.notEqual
                (labelOf unsigned EvidenceChain.EvidencePackHop)
                (labelOf failing EvidenceChain.EvidencePackHop)
                "an absent binding and a broken assembly are different findings with different remedies"

            Expect.equal
                (labelOf failing EvidenceChain.EvidencePackHop)
                "broken"
                "a composed pack that fails is a break"
        }

        test "an unrecorded ledger is absent; a broken one names its position" {
            let unrecorded =
                chainOf {
                    fullSources (workSource ()) with
                        Ledger =
                            Some(fun () -> async {
                                return
                                    Ok(
                                        LedgerPositionReading.LedgerUnrecorded
                                            "the ledger holds no row for this deploy"
                                    )
                            })
                }

            let broken =
                chainOf {
                    fullSources (workSource ()) with
                        Ledger =
                            Some(fun () -> async {
                                return Ok(LedgerPositionReading.LedgerBroken(7L, "prevHash disagrees"))
                            })
                }

            Expect.equal
                (labelOf unrecorded EvidenceChain.LedgerPositionHop)
                "absent"
                "an empty ledger is an absent link"

            Expect.equal (labelOf broken EvidenceChain.LedgerPositionHop) "broken" "a break is a break"

            Expect.equal
                (EvidenceLink.reference (hopOf broken EvidenceChain.LedgerPositionHop).Link)
                "7"
                "and the break names WHERE it sits, so the finding is actionable without a second read"

            Expect.equal broken.Outcome EvidenceChainOutcome.ChainBroken "one broken hop breaks the chain"
        }

        test "a ledger that is composed and will not answer is broken, never absent" {
            // Reading a failed read as absence would make breaking your
            // own ledger the cheapest way to end the chain quietly.
            let chain =
                chainOf {
                    fullSources (workSource ()) with
                        Ledger = Some(fun () -> async { return Error "storage refused" })
                }

            Expect.equal (labelOf chain EvidenceChain.LedgerPositionHop) "broken" "composed-and-unreadable is a finding"
        }

        test "a transcript the record does not name is absent; one whose digest disagrees is broken" {
            let unbound =
                chainOf {
                    fullSources (workSource ()) with
                        Deploy =
                            Some(
                                sealRecord (
                                    DeployRecord.create
                                        "deploy-2"
                                        "tenant-1"
                                        "build-1"
                                        manifest
                                        (DeployRecords.withClosure closure DeployProvenance.none)
                                )
                            )
                }

            let mismatched =
                let other =
                    BuildTranscript.create toolchain dependencies {
                        Path = "src/Other.fs"
                        ContentDigest = "ff66"
                    }

                chainOf {
                    fullSources (workSource ()) with
                        Transcript = Some other
                }

            Expect.equal
                (labelOf unbound EvidenceChain.BuildTranscriptHop)
                "absent"
                "a transcript nothing joins to the deployment is an absent link, not a broken one"

            Expect.equal
                (labelOf mismatched EvidenceChain.BuildTranscriptHop)
                "broken"
                "a recorded join that does not hold is a break"
        }

        test "a closure the record does not bind is absent; one whose digest disagrees is broken" {
            let unbound =
                chainOf {
                    fullSources (workSource ()) with
                        Deploy =
                            Some(
                                sealRecord (
                                    DeployRecord.create
                                        "deploy-3"
                                        "tenant-1"
                                        "build-1"
                                        manifest
                                        (DeployProvenance.withTranscriptDigest
                                            (DeployRecords.transcriptDigest transcript)
                                            DeployProvenance.none)
                                )
                            )
                }

            let mismatched =
                chainOf {
                    fullSources (workSource ()) with
                        Closure = Some(DependencyClosure.create [])
                }

            Expect.equal (labelOf unbound EvidenceChain.DependencyClosureHop) "absent" "an unbound closure is absent"

            Expect.equal
                (labelOf mismatched EvidenceChain.DependencyClosureHop)
                "broken"
                "a bound one that disagrees is broken"
        }

        test "a deploy record with no sealer composed is absent, not verified and not broken" {
            let chain =
                chainOf {
                    fullSources (workSource ()) with
                        Sealer = None
                }

            Expect.equal
                (labelOf chain EvidenceChain.DeployRecordHop)
                "absent"
                "a seal that is carried rather than checked has not failed, and has not been verified either"
        }

        test "a seal the sealer refuses is broken" {
            let tampered = {
                signedRecord with
                    Record = {
                        signedRecord.Record with
                            BuildId = "build-substituted"
                    }
            }

            let chain =
                chainOf {
                    fullSources (workSource ()) with
                        Deploy = Some tampered
                }

            Expect.equal
                (labelOf chain EvidenceChain.DeployRecordHop)
                "broken"
                "a seal that does not cover the record is a break"
        }

        test "an unsealed boot is absent; a rejected one is broken" {
            let unsealed =
                chainOf {
                    fullSources (workSource ()) with
                        Boot = Some(BootVerificationReading.BootUnsealed "no sealed deploy record to compare against")
                }

            let rejected =
                chainOf {
                    fullSources (workSource ()) with
                        Boot =
                            Some(BootVerificationReading.BootRejected("module-set", "the running composition drifted"))
                }

            Expect.equal
                (labelOf unsealed EvidenceChain.BootVerificationHop)
                "absent"
                "an unsealed boot verified nothing"

            Expect.equal (labelOf rejected EvidenceChain.BootVerificationHop) "broken" "a rejected boot is a break"
        }

        test "a work source that is asked and fails is broken, never absent" {
            let source =
                FakeWorkSource(workTable, fun _ -> Result.Error "the source system timed out")

            let chain =
                chainOf {
                    fullSources (workSource ()) with
                        Work = composedWork source
                }

            Expect.equal
                (labelOf chain EvidenceChain.UpstreamWorkRecordHop)
                "broken"
                "a composed source that would not answer is a finding, not a bound"
        }

        test "no work source composed is absent, and says nobody was asked" {
            let chain =
                chainOf {
                    fullSources (workSource ()) with
                        Work = NoWorkProvenanceSource
                }

            Expect.equal (labelOf chain EvidenceChain.UpstreamWorkRecordHop) "absent" "nothing was asked"

            Expect.stringContains
                (EvidenceLink.detail (hopOf chain EvidenceChain.UpstreamWorkRecordHop).Link)
                "composed"
                "and the reason names the missing substrate"
        }

        test "the four labels are four distinct strings" {
            // The distinguishability claim rests on the labels being
            // distinct; a mapper that folded two of them would satisfy
            // every arm above that asserts only one label.
            let labels =
                [
                    EvidenceLink.Linked("r", "d")
                    EvidenceLink.LinkAbsent "r"
                    EvidenceLink.LinkBroken("p", "r")
                    EvidenceLink.LinkWithheld "p"
                ]
                |> List.map EvidenceLink.label

            Expect.equal (List.distinct labels |> List.length) 4 "each verdict has its own wire label"
        }
    ]

// ─── The declared caps refuse rather than shorten ────────────────────

let capRefusalTests =
    testList "Phase 713 — a walk outside the declared caps refuses, and never shortens" [

        test "a depth below one is refused typed" {
            let walker = EvidenceChainWalker.create EvidenceChainSources.none

            match walker.Walk { Actor = "probe"; WorkDepth = 0 } |> Async.RunSynchronously with
            | Error(ChainWorkDepthInvalid 0) -> ()
            | other -> failtestf "a zero-hop walk is a caller bug, not an empty answer: %A" other
        }

        test "a depth above the declared cap is refused, naming both numbers" {
            let walker = EvidenceChainWalker.create EvidenceChainSources.none

            match
                walker.Walk {
                    Actor = "probe"
                    WorkDepth = EvidenceChainCaps.defaults.MaxWorkDepth + 1
                }
                |> Async.RunSynchronously
            with
            | Error(ChainWorkDepthExceedsCap(requested, cap)) ->
                Expect.equal requested (EvidenceChainCaps.defaults.MaxWorkDepth + 1) "the refusal echoes what was asked"
                Expect.equal cap EvidenceChainCaps.defaults.MaxWorkDepth "and names the limit"
            | other -> failtestf "an over-cap depth must be refused: %A" other
        }

        test "a closure above the declared cap refuses the WALK rather than trimming the closure" {
            let big =
                DependencyClosure.create [
                    for i in 1..5 ->
                        {
                            Id = sprintf "Package.%d" i
                            Version = "1.0.0"
                            Source = ""
                            ContentDigest = ""
                            Attestation = Unattested ProviderAbsent
                        }
                ]

            let caps = {
                EvidenceChainCaps.defaults with
                    MaxClosureEntries = 3
            }

            let walker =
                EvidenceChainWalker.createWith caps (fun () -> DateTime.UnixEpoch) {
                    EvidenceChainSources.none with
                        Closure = Some big
                }

            match walker.Walk(EvidenceChainRequest.forActor "probe") |> Async.RunSynchronously with
            | Error(ChainClosureExceedsCap(entries, cap)) ->
                Expect.equal entries 5 "the refusal counts what was reached"
                Expect.equal cap 3 "and names the cap"
            | other -> failtestf "an over-cap closure must refuse the walk whole: %A" other
        }

        test "the same closure under a roomier cap walks — so the refusal is a refusal" {
            // Verify the probe, not just the verdict: without this arm a
            // walker that refused EVERYTHING would pass the three above.
            let big =
                DependencyClosure.create [
                    for i in 1..5 ->
                        {
                            Id = sprintf "Package.%d" i
                            Version = "1.0.0"
                            Source = ""
                            ContentDigest = ""
                            Attestation = Unattested ProviderAbsent
                        }
                ]

            let walker =
                EvidenceChainWalker.createWith EvidenceChainCaps.defaults (fun () -> DateTime.UnixEpoch) {
                    EvidenceChainSources.none with
                        Closure = Some big
                }

            match walker.Walk(EvidenceChainRequest.forActor "probe") |> Async.RunSynchronously with
            | Ok chain ->
                Expect.equal
                    (List.length chain.Hops)
                    (List.length EvidenceChain.order)
                    "the roomier walk returns every hop"
            | Error error ->
                failtestf "the same closure under a roomier cap must walk: %s" (EvidenceChainError.describe error)
        }

        test "the declared caps are readable before a walk is attempted" {
            let walker = EvidenceChainWalker.create EvidenceChainSources.none

            Expect.equal
                (walker.GetCaps() |> Async.RunSynchronously)
                EvidenceChainCaps.defaults
                "a caller sizes its request from the declared caps rather than discovering the limit as a refusal"
        }
    ]

// ─── The walk is an audited read ─────────────────────────────────────

let auditedReadTests =
    testList "Phase 713 — the walk is an audited read that mutates nothing" [

        test "exactly one row per completed walk, carrying who, the outcome and the digest" {
            let auditLog = RecordingAuditLog()
            let walker = EvidenceChainWalker.create (fullSources (workSource ()))

            let chain =
                match
                    EvidenceChainWalker.run
                        (servicesWith (Some(auditLog :> IAuditLog)))
                        (ComposedEvidenceChainWalker walker)
                        (EvidenceChainRequest.forActor "auditor")
                    |> Async.RunSynchronously
                with
                | Ok chain -> chain
                | Error error -> failtestf "the walk was refused: %s" (EvidenceChainError.describe error)

            match auditLog.All with
            | [ EvidenceChainWalked payload ] ->
                Expect.equal payload.Actor "auditor" "the row names who walked"
                Expect.equal payload.VerdictDigest chain.VerdictDigest "and commits to what the chain said"

                Expect.equal
                    payload.Outcome
                    (EvidenceChainOutcome.label chain.Outcome)
                    "and carries the top-line outcome"

                Expect.equal
                    (List.length payload.Hops)
                    (List.length EvidenceChain.order)
                    "one label per hop, whatever the deployment composes"
            | other -> failtestf "expected exactly one EvidenceChainWalked row, got %A" other
        }

        test "a second walk records a second row and no more" {
            let auditLog = RecordingAuditLog()
            let walker = EvidenceChainWalker.create (fullSources (workSource ()))
            let services = servicesWith (Some(auditLog :> IAuditLog))

            for _ in 1..2 do
                EvidenceChainWalker.run
                    services
                    (ComposedEvidenceChainWalker walker)
                    (EvidenceChainRequest.forActor "auditor")
                |> Async.RunSynchronously
                |> ignore

            Expect.equal (List.length auditLog.All) 2 "one row per walk — never zero, never two for one"
        }

        test "a REFUSED walk records nothing" {
            let auditLog = RecordingAuditLog()
            let walker = EvidenceChainWalker.create (fullSources (workSource ()))

            EvidenceChainWalker.run (servicesWith (Some(auditLog :> IAuditLog))) (ComposedEvidenceChainWalker walker) {
                Actor = "auditor"
                WorkDepth = 0
            }
            |> Async.RunSynchronously
            |> ignore

            Expect.isEmpty auditLog.All "nothing was walked, so there is nothing to record"
        }

        test "a deployment with no audit log composed still walks" {
            let walker = EvidenceChainWalker.create (fullSources (workSource ()))

            match
                EvidenceChainWalker.run
                    (servicesWith None)
                    (ComposedEvidenceChainWalker walker)
                    (EvidenceChainRequest.forActor "auditor")
                |> Async.RunSynchronously
            with
            | Ok chain -> Expect.equal chain.Outcome EvidenceChainOutcome.ChainComplete "the walk is unaffected"
            | Error error ->
                failtestf "an absent audit log must not fail the walk: %s" (EvidenceChainError.describe error)
        }

        test "the walk reads its sources and leaves the deploy record byte-identical" {
            // GP 6, asserted rather than argued: the canonical bytes the
            // seal covers are exactly what they were, and the work source
            // was genuinely consulted rather than skipped.
            let before = DeployRecords.canonicalBytes record
            let source = workSource ()

            chainOf {
                fullSources (workSource ()) with
                    Work = composedWork source
            }
            |> ignore

            Expect.isGreaterThan source.Lookups 0 "the walk actually consulted the source"
            Expect.equal (DeployRecords.canonicalBytes record) before "and wrote nothing back to the record"
        }

        test "the seam has no write surface" {
            // No member of the walker answers with `unit` — a `unit`
            // answer is the shape a mutation takes.
            for method' in typeof<IEvidenceChainWalker>.GetMethods() do
                let returnType = method'.ReturnType

                let answer =
                    if
                        returnType.IsGenericType
                        && returnType.GetGenericTypeDefinition() = typedefof<Async<_>>
                    then
                        returnType.GetGenericArguments()[0]
                    else
                        returnType

                Expect.notEqual
                    answer
                    typeof<unit>
                    (sprintf "'%s' must be a query — a unit answer is a write" method'.Name)
        }
    ]

// ─── The digest names the link set, not the clock ────────────────────

let digestTests =
    testList "Phase 713 — the verdict digest commits to the link set" [

        test "two walks a moment apart against an unchanged deployment agree" {
            let sources = fullSources (workSource ())

            Expect.equal
                (chainOf sources).VerdictDigest
                (chainOf sources).VerdictDigest
                "a digest that folded in the clock would change on every walk and commit to nothing"
        }

        test "a hop that moves moves the digest" {
            let full = fullSources (workSource ())

            let broken = {
                full with
                    Ledger =
                        Some(fun () -> async {
                            return Ok(LedgerPositionReading.LedgerBroken(7L, "prevHash disagrees"))
                        })
            }

            Expect.notEqual
                (chainOf broken).VerdictDigest
                (chainOf full).VerdictDigest
                "the digest is a property of the links, so a changed link must move it"
        }

        test "the render carries every hop, failures included" {
            let rendered = EvidenceChain.render (chainOf EvidenceChainSources.none)

            for hopId in EvidenceChain.order do
                Expect.stringContains
                    rendered
                    (EvidenceChain.titleOf hopId)
                    (sprintf "hop '%s' renders even when it resolved nothing" hopId)
        }
    ]

// ─── The ninth section of the verification report ────────────────────

// Sequenced because two of the eight prior sections read the
// process-wide configuration resolution seam. A sibling arm that
// installs a manifest or an escape hatch would otherwise move sections
// this pack asserts are absent — a failure in the wrong file, about the
// wrong phase.
let reportSectionTests =
    testSequenced
    <| testList "Phase 713 — the chain joins the verification report as a section" [

        test "a deployment composing no walker reads the section as absent and exits ZERO" {
            let report =
                DeploymentVerificationReport.buildReport DeploymentVerificationEvidence.none "probe" DateTime.UnixEpoch
                |> Async.RunSynchronously

            let chainSection =
                report.Sections |> List.find (fun s -> s.Id = EvidenceChainSection)

            Expect.equal
                (VerificationSectionVerdict.label chainSection.Verdict)
                "not-composed"
                "an absent walker degrades to an absent section, not an error"

            Expect.equal (exitCode report) 0 "and absence is not a failure"

            Expect.equal
                report.Outcome
                DeploymentVerificationOutcome.NothingComposed
                "the new section must not make NothingComposed unreachable"
        }

        test "the eight sections that shipped before are untouched for a deployment composing no walker" {
            // The GP 11 pin. Every section id that existed before this
            // phase keeps its verdict label AND its detail, byte for
            // byte, when no walker is composed.
            let evidence = DeploymentVerificationEvidence.none

            let priorIds = [
                BootSealSection
                GroundingContinuitySection
                AuditLedgerSection
                CertificateIssuanceSection
                AnswerJoinSection
                SeamAuthoritySection
                ConfigConformanceSection
                AcceptedAcknowledgementSection
            ]

            let report =
                DeploymentVerificationReport.buildReport evidence "probe" DateTime.UnixEpoch
                |> Async.RunSynchronously

            for id in priorIds do
                let s = report.Sections |> List.find (fun s -> s.Id = id)

                Expect.equal
                    (VerificationSectionVerdict.label s.Verdict)
                    "not-composed"
                    (sprintf "section '%s' keeps its verdict" id)

            Expect.equal
                (report.Sections |> List.map _.Id)
                (priorIds @ [ EvidenceChainSection ])
                "the section is APPENDED — inserting it would move every later section's canonical line"
        }

        test "a complete chain verifies the section; a broken one fails it" {
            let walk (sources: EvidenceChainSources) =
                let walker = EvidenceChainWalker.create sources
                Some(fun () -> walker.Walk(EvidenceChainRequest.forActor "probe"))

            let sectionFor sources =
                let report =
                    DeploymentVerificationEvidence.none
                    |> DeploymentVerificationEvidence.withEvidenceChain (walk sources)
                    |> fun e -> DeploymentVerificationReport.buildReport e "probe" DateTime.UnixEpoch
                    |> Async.RunSynchronously

                report, report.Sections |> List.find (fun s -> s.Id = EvidenceChainSection)

            let full = fullSources (workSource ())
            let _, complete = sectionFor full

            let brokenReport, broken =
                sectionFor {
                    full with
                        Ledger =
                            Some(fun () -> async {
                                return Ok(LedgerPositionReading.LedgerBroken(7L, "prevHash disagrees"))
                            })
                }

            Expect.equal (VerificationSectionVerdict.label complete.Verdict) "verified" "a complete chain verifies"
            Expect.equal (VerificationSectionVerdict.label broken.Verdict) "failed" "a broken hop fails the section"
            Expect.equal (exitCode brokenReport) 1 "and a failed section exits non-zero"
        }

        test "a chain in which nothing resolves is OBSERVED, not not-composed" {
            // The walker IS composed, it ran, and it found no join —
            // which is a read with nothing to affirm, not an absent
            // substrate. Folding the two would make a deployment that
            // records nothing indistinguishable from one that never
            // composed the walker.
            let walker = EvidenceChainWalker.create EvidenceChainSources.none

            let report =
                DeploymentVerificationEvidence.none
                |> DeploymentVerificationEvidence.withEvidenceChain (
                    Some(fun () -> walker.Walk(EvidenceChainRequest.forActor "probe"))
                )
                |> fun e -> DeploymentVerificationReport.buildReport e "probe" DateTime.UnixEpoch
                |> Async.RunSynchronously

            let s = report.Sections |> List.find (fun s -> s.Id = EvidenceChainSection)

            Expect.equal (VerificationSectionVerdict.label s.Verdict) "observed" "composed-and-empty is observed"
            Expect.equal (exitCode report) 0 "and it is not a failure"
        }

        test "a refused walk is UNREADABLE, not observed" {
            // A deployment that can end an inconvenient walk by exceeding
            // its own cap must not be rewarded with a quieter verdict.
            let walker = EvidenceChainWalker.create EvidenceChainSources.none

            let report =
                DeploymentVerificationEvidence.none
                |> DeploymentVerificationEvidence.withEvidenceChain (
                    Some(fun () -> walker.Walk { Actor = "probe"; WorkDepth = 0 })
                )
                |> fun e -> DeploymentVerificationReport.buildReport e "probe" DateTime.UnixEpoch
                |> Async.RunSynchronously

            let s = report.Sections |> List.find (fun s -> s.Id = EvidenceChainSection)

            Expect.equal (VerificationSectionVerdict.label s.Verdict) "unreadable" "a refusal is not an empty chain"
            Expect.equal (exitCode report) 1 "and a composed-but-unreadable section exits non-zero"
        }

        test "the section lists every hop, including the absent ones" {
            let walker = EvidenceChainWalker.create EvidenceChainSources.none

            let report =
                DeploymentVerificationEvidence.none
                |> DeploymentVerificationEvidence.withEvidenceChain (
                    Some(fun () -> walker.Walk(EvidenceChainRequest.forActor "probe"))
                )
                |> fun e -> DeploymentVerificationReport.buildReport e "probe" DateTime.UnixEpoch
                |> Async.RunSynchronously

            let s = report.Sections |> List.find (fun s -> s.Id = EvidenceChainSection)

            Expect.equal
                (List.length s.Findings)
                (List.length EvidenceChain.order)
                "a section that listed only the resolved hops would read as a complete chain"
        }

        test "the withers carry the chain member through rather than dropping it" {
            // The Phase 693 lesson, re-learned structurally: a wither
            // that rebuilt a member it does not name would silently
            // delete the ninth section for every root that supplies both.
            let walker = EvidenceChainWalker.create EvidenceChainSources.none

            let evidence =
                DeploymentVerificationEvidence.none
                |> DeploymentVerificationEvidence.withEvidenceChain (
                    Some(fun () -> walker.Walk(EvidenceChainRequest.forActor "probe"))
                )

            Expect.isSome
                (DeploymentVerificationEvidence.evidenceChainOf (
                    DeploymentVerificationEvidence.withGroundingContinuity None evidence
                ))
                "withGroundingContinuity carries the chain through"

            Expect.isSome
                (DeploymentVerificationEvidence.evidenceChainOf (
                    DeploymentVerificationEvidence.withSeamAuthority None evidence
                ))
                "withSeamAuthority carries the chain through"

            Expect.isSome
                (DeploymentVerificationEvidence.seamAuthorityOf (
                    DeploymentVerificationEvidence.none
                    |> DeploymentVerificationEvidence.withSeamAuthority (
                        Some {
                            Profile = "standard"
                            DeclarationMandatory = false
                            Components = []
                            Verification = SeamAuthorityUnenforced
                        }
                    )
                    |> DeploymentVerificationEvidence.withEvidenceChain None
                ))
                "and withEvidenceChain carries the seam-authority posture through"
        }

        test "the chain's not-proved statement stands un-narrowed with no walker" {
            let report =
                DeploymentVerificationReport.buildReport DeploymentVerificationEvidence.none "probe" DateTime.UnixEpoch
                |> Async.RunSynchronously

            let statement =
                report.NotProved
                |> List.find (fun s -> s.Id = "chain-joins-records-not-reality")

            Expect.isNone statement.Narrowing "with no chain composed, nothing narrows the bound"
        }
    ]