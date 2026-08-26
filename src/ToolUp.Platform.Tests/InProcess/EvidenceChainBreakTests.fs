// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.EvidenceChainBreakTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.ArtefactSigning
open ToolUp.Platform.Tests.InProcess.BuildTranscriptTests

// ─── The break-injection corpus ──────────────────────────────────────
//
// The link verdicts and the tamper detection are worth exactly what
// they can be shown to CATCH. This pack is the corpus that shows it:
// one synthetic chain in which every hop resolves, then one variant per
// break class, each asserting the specific verdict and the specific
// position — never merely "not intact".
//
// ── Why this exists as its own pack ──────────────────────────────────
//
// A check that runs, passes, and could not have failed is
// indistinguishable from one that works, right up until it matters.
// That failure mode has already been paid for once here, in a
// provenance check caught only by deliberately probing it rather than
// by reading it. It is worse in this substrate than anywhere else,
// because a decorative evidence check does not merely miss a defect: it
// produces a signed artefact asserting that a chain was verified.
//
// So every case below is DEMONSTRATED to fail against code lacking its
// check, by one of two methods, each named per case in the corpus file:
//
//   * **discriminating-twin** — for the walked chain, where the check
//     is a traversal rather than a value comparison. The case's own
//     assertion (hop H reads verdict V at position P) is re-applied to
//     the healthy baseline and must FAIL there; the baseline's
//     assertion (hop H is linked, at that reference) is re-applied to
//     the injected fixture and must FAIL there. A verdict that fired
//     unconditionally fails the first direction; one that could never
//     fire at all fails the second.
//
//   * **weakened-verifier** — for the bundle and the document, where
//     the checks are a fixed sequence of value comparisons. A
//     deliberately-weakened copy of that sequence, omitting exactly one
//     check and nothing else, reports the tampered document INTACT
//     where the shipped verifier reports it broken at a named position.
//     The weakened copy is itself pinned against the shipped verifier
//     with nothing omitted, so it is a faithful copy minus one check
//     rather than a strawman that would prove nothing.
//
// ── Where the corpus lives ───────────────────────────────────────────
//
// In `tests/fixtures/evidence-chain/`, in files, once. This pack READS
// them; it carries no inline table that could drift from them, and the
// two are checked against each other in BOTH directions — a case with
// no injector and an injector no case names are both failures. The
// identifiers are synthetic throughout and name no deployment, tenant,
// key or system that exists.
//
// ── What is deliberately NOT claimed ─────────────────────────────────
//
// One break class has no verdict to assert, and it is recorded as
// unproven rather than quietly kept: a severed ANCESTOR reference is
// dropped by the ancestor walk, so the hop stays linked and its
// enumeration is silently one line shorter. What is pinned there is the
// silence itself, so it sits on the record as a finding rather than
// waiting to be rediscovered.

// ─── Reading the corpus ──────────────────────────────────────────────

let private corpusDir =
    Path.Combine(AppContext.BaseDirectory, "fixtures", "evidence-chain")

/// The parsed corpus files, held for the lifetime of the pack — a
/// `JsonDocument`'s elements do not outlive the document.
let private corpusDocuments = ConcurrentDictionary<string, JsonDocument>()

let private corpus (name: string) : JsonElement =
    corpusDocuments
        .GetOrAdd(
            name,
            fun name ->
                let path = Path.Combine(corpusDir, name)

                if not (File.Exists path) then
                    failtestf
                        "the corpus fixture '%s' is missing from '%s' — this pack reads the corpus rather than reconstructing an equivalent inline, so an absent file is a failure and never a fallback"
                        name
                        corpusDir

                JsonDocument.Parse(File.ReadAllText path)
        )
        .RootElement

let private field (element: JsonElement) (name: string) : string =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
    | _ -> failtestf "a corpus entry is missing its '%s' field" name

let private intField (element: JsonElement) (name: string) : int =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32()
    | _ -> failtestf "a corpus entry is missing its '%s' field" name

let private flag (element: JsonElement) (name: string) : bool =
    match element.TryGetProperty name with
    | true, value -> value.ValueKind = JsonValueKind.True
    | _ -> false

let private items (element: JsonElement) (name: string) : JsonElement list =
    element.GetProperty(name).EnumerateArray() |> List.ofSeq

/// One case, as the corpus states it.
type private CorpusCase = {
    Id: string
    Class: string
    Hop: string
    Ordinal: int
    Verdict: string
    Position: string
    Outcome: string
    Falsification: string
    Unproven: bool
}

let private chainCaseElements = items (corpus "chain-break-cases.json") "cases"

let private chainCases: CorpusCase list =
    chainCaseElements
    |> List.map (fun case -> {
        Id = field case "id"
        Class = field case "class"
        Hop = field case "hop"
        Ordinal = intField case "ordinal"
        Verdict = field case "verdict"
        Position = field case "position"
        Outcome = field case "outcome"
        Falsification = field case "falsification"
        Unproven = flag case "unproven"
    })

let private chainCase (id: string) : CorpusCase =
    chainCases
    |> List.tryFind (fun case -> case.Id = id)
    |> Option.defaultWith (fun () -> failtestf "the corpus names no chain case '%s'" id)

let private chainCaseElement (id: string) : JsonElement =
    chainCaseElements |> List.find (fun element -> field element "id" = id)

let private bundleCaseElements = items (corpus "bundle-tamper-cases.json") "cases"

let private bundleCasesAt (level: string) =
    bundleCaseElements |> List.filter (fun case -> field case "level" = level)

let private bundleCaseElement (id: string) : JsonElement =
    bundleCaseElements |> List.find (fun element -> field element "id" = id)

let private walkDepth = intField (corpus "chain-break-cases.json") "walkDepth"

let private pairElements = items (corpus "absent-vs-broken-pairs.json") "pairs"

// ─── The synthetic deployment the corpus is injected into ────────────
//
// Every identifier here is invented for this corpus. Nothing names a
// real deployment, tenant, key, package or source system.

let private workSystem = "corpus-work-system"

let private ref' (recordId: string) : WorkRecordRef =
    WorkRecordRef.create workSystem recordId

let private parentRecord: WorkRecord = {
    Ref = ref' "wc-parent"
    Kind = WorkRecordKind.Reviewed
    ContentDigest = "corpus-parent-content"
    Parents = []
    Verdict = Some "accepted"
    Label = "the reviewed parent"
}

let private headRecord: WorkRecord = {
    Ref = ref' "wc-head"
    Kind = WorkRecordKind.Authored
    ContentDigest = "corpus-head-content"
    Parents = [ ref' "wc-parent" ]
    Verdict = Some "green"
    Label = "the authoring head"
}

let private restrictedMarker: WithheldWorkRecord = {
    Ref = ref' "wc-restricted"
    Kind = WorkRecordKind.Reviewed
    PolicyRef = "corpus-policy/restricted-review"
}

/// The whole table: the head, its parent, and a record the source holds
/// and refuses.
let private fullTable =
    Map.ofList [
        "wc-head", WorkRecordAnswer.Found headRecord
        "wc-parent", WorkRecordAnswer.Found parentRecord
        "wc-restricted", WorkRecordAnswer.Withheld restrictedMarker
    ]

/// The same table with the head's PARENT removed — the severed-ancestor
/// case, whose whole point is that nothing reports it.
let private severedAncestorTable = fullTable |> Map.remove "wc-parent"

/// A source-system double over a fixed record table and a configurable
/// coverage answer.
type private CorpusWorkSource(records: Map<string, WorkRecordAnswer>, coverage: string -> Result<WorkCoverage, string>)
    =

    interface IWorkProvenanceSource with
        member _.SourceSystem() = workSystem
        member _.GetCaps() = async { return WorkProvenanceCaps.defaults }

        member _.GetRecord reference = async {
            return
                records
                |> Map.tryFind reference.RecordId
                |> Option.defaultValue WorkRecordAnswer.Absent
        }

        member this.GetAncestors request =
            WorkProvenanceSource.walkOverLookups (this :> IWorkProvenanceSource) request

        member _.Covering upstreamReference = async { return coverage upstreamReference }

let private composedWork (records: Map<string, WorkRecordAnswer>) (coverage: string -> Result<WorkCoverage, string>) =
    ComposedWorkProvenanceSource(CorpusWorkSource(records, coverage) :> IWorkProvenanceSource)

let private coveringHead (_: string) =
    Result.Ok(WorkCoverage.Covered(ref' "wc-head"))

// ── The build side ───────────────────────────────────────────────────

let private toolchain: BuildToolchain = {
    Name = "corpus-sdk"
    Version = "10.0.203"
}

let private dependencies: BuildDependency list = [
    {
        Id = "Corpus.Alpha"
        Version = "1.2.3"
        ContentDigest = "corpus-alpha-content"
    }
    {
        Id = "Corpus.Beta"
        Version = "0.9.0"
        ContentDigest = "corpus-beta-content"
    }
]

let private transcript =
    BuildTranscript.create toolchain dependencies {
        Path = "src/CorpusProgram.fs"
        ContentDigest = "corpus-entry-content"
    }

/// A transcript that is not the one the record names — the digest
/// mismatch at the transcript hop.
let private substitutedTranscript =
    BuildTranscript.create toolchain dependencies {
        Path = "src/CorpusProgram.Substituted.fs"
        ContentDigest = "corpus-substituted-entry-content"
    }

let private closure =
    DependencyClosure.create [
        {
            Id = "Corpus.Alpha"
            Version = "1.2.3"
            Source = "https://packages.corpus.invalid"
            ContentDigest = "corpus-alpha-content"
            Attestation =
                AttestedBy {
                    OpId = "corpus-release-1"
                    ActDigest = "corpus-release-act"
                }
        }
        {
            Id = "Corpus.Beta"
            Version = "0.9.0"
            Source = ""
            ContentDigest = "corpus-beta-content"
            Attestation = Unattested ExternalPackage
        }
    ]

/// A closure that is not the one the record binds.
let private substitutedClosure = DependencyClosure.create []

let private manifest: DeployManifest = {
    DeployManifest.empty with
        App = {
            Name = "Corpus"
            Slug = "corpus"
            Region = "corpus-region"
        }
        Runtime = {
            DeployManifest.empty.Runtime with
                Framework = "dotnet:10"
        }
}

let private provenance =
    DeployProvenance.none
    |> DeployProvenance.withTranscriptDigest (DeployRecords.transcriptDigest transcript)
    |> DeployRecords.withClosure closure

let private record =
    DeployRecord.create "deploy-corpus-1" "tenant-corpus" "build-corpus-1" manifest provenance

let private sealer = StubSealer "corpus-seal-secret" :> IDeployRecordSealer

let private sealRecord (candidate: DeployRecord) : SealedDeployRecord =
    match sealer.Seal(DeployRecords.canonicalBytes candidate) |> Async.RunSynchronously with
    | Ok seal -> { Record = candidate; Seal = seal }
    | Error reason -> failtestf "the stub sealer refused to seal a corpus fixture: %s" reason

let private signedRecord = sealRecord record

/// The record edited AFTER it was sealed, so the seal no longer covers
/// its canonical bytes.
let private tamperedRecord = {
    signedRecord with
        Record = {
            signedRecord.Record with
                BuildId = "build-corpus-substituted"
        }
}

// ── The three carried readings ───────────────────────────────────────

let private healthyBoot =
    BootVerificationReading.BootVerified "the running composition is the sealed one"

let private healthyPack () = async { return Ok(EvidencePackReading.PackSigned("pack-manifest-corpus", 5)) }

let private healthyLedger () = async { return Ok(LedgerPositionReading.LedgerRecorded(4218L, "corpus-ledger-head")) }

/// The healthy baseline's sources: every substrate composed, and every
/// one of them healthy.
let private healthySources: EvidenceChainSources = {
    Work = composedWork fullTable coveringHead
    Transcript = Some transcript
    Closure = Some closure
    Deploy = Some signedRecord
    Sealer = Some sealer
    Boot = Some healthyBoot
    Pack = Some healthyPack
    Ledger = Some healthyLedger
}

// ─── The injector table ──────────────────────────────────────────────
//
// One entry per corpus case id. The MECHANISM of an injection is code —
// it manipulates typed values a JSON file cannot hold — while which
// cases exist, what each asserts and how each was falsified is the
// corpus. The two are checked against each other in both directions
// below, so neither can silently drift from the other.

let private chainInjectors: Map<string, EvidenceChainSources -> EvidenceChainSources> =
    Map.ofList [
        "work-head-severed",
        fun sources -> {
            sources with
                Work = composedWork fullTable (fun _ -> Result.Ok(WorkCoverage.Covered(ref' "wc-missing")))
        }

        "work-ancestor-severed",
        fun sources -> {
            sources with
                Work = composedWork severedAncestorTable coveringHead
        }

        "work-record-withheld",
        fun sources -> {
            sources with
                Work = composedWork fullTable (fun _ -> Result.Ok(WorkCoverage.Covered(ref' "wc-restricted")))
        }

        "work-source-absent",
        fun sources -> {
            sources with
                Work = NoWorkProvenanceSource
        }

        "work-source-unanswering",
        fun sources -> {
            sources with
                Work = composedWork fullTable (fun _ -> Result.Error "the corpus source system timed out")
        }

        "transcript-digest-mismatch",
        fun sources -> {
            sources with
                Transcript = Some substitutedTranscript
        }

        "transcript-absent", fun sources -> { sources with Transcript = None }

        "closure-digest-mismatch",
        fun sources -> {
            sources with
                Closure = Some substitutedClosure
        }

        "closure-absent", fun sources -> { sources with Closure = None }

        "deploy-seal-mismatch",
        fun sources -> {
            sources with
                Deploy = Some tamperedRecord
        }

        "deploy-sealer-absent", fun sources -> { sources with Sealer = None }

        "boot-composition-rejected",
        fun sources -> {
            sources with
                Boot =
                    Some(
                        BootVerificationReading.BootRejected(
                            "module-set",
                            "the running composition carries a module the seal does not"
                        )
                    )
        }

        "boot-absent", fun sources -> { sources with Boot = None }

        "pack-assembly-failed",
        fun sources -> {
            sources with
                Pack = Some(fun () -> async { return Error "the corpus audit slice would not read" })
        }

        "pack-unsigned",
        fun sources -> {
            sources with
                Pack = Some(fun () -> async { return Ok(EvidencePackReading.PackUnsigned("pack-manifest-corpus", 5)) })
        }

        "pack-withheld",
        fun sources -> {
            sources with
                Pack =
                    Some(fun () -> async { return Ok(EvidencePackReading.PackWithheld "corpus-policy/export-review") })
        }

        "ledger-broken-at-position",
        fun sources -> {
            sources with
                Ledger =
                    Some(fun () -> async {
                        return Ok(LedgerPositionReading.LedgerBroken(3071L, "the recorded previous hash disagrees"))
                    })
        }

        "ledger-unreadable",
        fun sources -> {
            sources with
                Ledger = Some(fun () -> async { return Error "the corpus ledger store refused the read" })
        }

        "ledger-absent", fun sources -> { sources with Ledger = None }
    ]

// ─── Walking ─────────────────────────────────────────────────────────

let private request: EvidenceChainRequest = {
    Actor = "corpus-assessor"
    WorkDepth = walkDepth
}

let private walk (sources: EvidenceChainSources) : EvidenceChain =
    match (EvidenceChainWalker.create sources).Walk request |> Async.RunSynchronously with
    | Ok chain -> chain
    | Error error -> failtestf "a corpus walk was refused: %s" (EvidenceChainError.describe error)

let private healthyChain = walk healthySources

let private injectedChain (case: CorpusCase) : EvidenceChain =
    match chainInjectors.TryFind case.Id with
    | Some inject -> walk (inject healthySources)
    | None -> failtestf "the corpus names chain case '%s' and this pack carries no injector for it" case.Id

let private hopOf (chain: EvidenceChain) (hopId: string) : EvidenceHop =
    chain.Hops
    |> List.tryFind (fun hop -> hop.Id = hopId)
    |> Option.defaultWith (fun () -> failtestf "hop '%s' is missing from a corpus chain" hopId)

/// Whether a chain says exactly what a case claims it says: the verdict
/// AND the position, at the hop and ordinal the case names. One
/// predicate, used both to assert a case and — pointed at the healthy
/// baseline — to falsify it.
let private saysWhatTheCaseClaims (case: CorpusCase) (chain: EvidenceChain) : bool =
    let hop = hopOf chain case.Hop

    EvidenceLink.label hop.Link = case.Verdict
    && EvidenceLink.reference hop.Link = case.Position
    && hop.Ordinal = case.Ordinal

// ─── Bundling ────────────────────────────────────────────────────────

let private observer = "corpus-deployment"
let private observedAt = DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc)

let private digest = EvidenceBundleExport.digest

let private healthyBundle =
    EvidenceBundleExport.bundleOf observer observedAt healthyChain

/// Re-derive EVERY digest downstream of a tamper — the chain's outcome,
/// the chain's own verdict digest and the bundle's content id.
///
/// This is what makes a tamper case worth running. A forger who edits a
/// hop and leaves four stale digests behind is caught by whichever the
/// verifier happens to reach first, which proves nothing about the
/// check the case is named for; re-deriving everything leaves exactly
/// one property violated.
let private rederive (bundle: EvidenceBundle) : EvidenceBundle =
    let rechained = {
        bundle with
            Chain = {
                bundle.Chain with
                    Outcome = EvidenceChain.outcomeOf bundle.Chain.Hops
                    VerdictDigest = digest (EvidenceChain.canonicalForm bundle.Chain.Hops)
            }
    }

    {
        rechained with
            ContentId = digest (EvidenceBundle.canonicalForm rechained)
    }

/// Re-address the bundle and nothing else — for the cases whose whole
/// subject is a digest INSIDE the document that the forger did not
/// refresh.
let private readdress (bundle: EvidenceBundle) : EvidenceBundle = {
    bundle with
        ContentId = digest (EvidenceBundle.canonicalForm bundle)
}

let private mapHop (hopId: string) (f: EvidenceHop -> EvidenceHop) (bundle: EvidenceBundle) : EvidenceBundle = {
    bundle with
        Chain = {
            bundle.Chain with
                Hops = bundle.Chain.Hops |> List.map (fun hop -> if hop.Id = hopId then f hop else hop)
        }
}

let private bundleInjectors: Map<string, EvidenceBundle -> EvidenceBundle> =
    Map.ofList [
        "schema-unreadable",
        fun bundle ->
            // The bundle's own schema field is deliberately NOT part of
            // the canonical form, so nothing downstream of it moves.
            {
                bundle with
                    SchemaVersion = EvidenceBundle.SchemaVersion + 1
            }

        "disposition-re-signed",
        fun bundle ->
            readdress {
                bundle with
                    NestedAttestationDisposition = "re-signed"
            }

        "hop-dropped",
        fun bundle ->
            rederive {
                bundle with
                    Chain = {
                        bundle.Chain with
                            Hops = bundle.Chain.Hops |> List.filter (fun hop -> hop.Ordinal <> 3)
                    }
            }

        "hops-reordered",
        fun bundle ->
            rederive {
                bundle with
                    Chain = {
                        bundle.Chain with
                            Hops =
                                bundle.Chain.Hops
                                |> List.rev
                                |> List.mapi (fun index hop -> { hop with Ordinal = index + 1 })
                    }
            }

        "hop-renumbered",
        fun bundle ->
            rederive {
                bundle with
                    Chain = {
                        bundle.Chain with
                            Hops =
                                bundle.Chain.Hops
                                |> List.map (fun hop -> if hop.Ordinal = 2 then { hop with Ordinal = 9 } else hop)
                    }
            }

        "hop-altered-after-the-walk",
        fun bundle ->
            // The chain's own verdict digest is left standing: it is the
            // property this case is named for.
            bundle
            |> mapHop EvidenceChain.EvidencePackHop (fun hop -> {
                hop with
                    Link = EvidenceLink.Linked("corpus-swapped-manifest", EvidenceLink.detail hop.Link)
            })
            |> readdress

        "outcome-flattered",
        fun bundle ->
            // A broken hop under an outcome still claiming completeness.
            // The chain digest and the content id are refreshed, so the
            // fold of its own hops is the only thing contradicting it.
            let broken =
                bundle
                |> mapHop EvidenceChain.LedgerPositionHop (fun hop -> {
                    hop with
                        Link = EvidenceLink.LinkBroken("3071", "the recorded previous hash disagrees")
                })

            let flattered = {
                broken with
                    Chain = {
                        broken.Chain with
                            Outcome = EvidenceChainOutcome.ChainComplete
                            VerdictDigest = digest (EvidenceChain.canonicalForm broken.Chain.Hops)
                    }
            }

            readdress flattered

        "claim-boundary-stripped", fun bundle -> readdress { bundle with NotProved = [] }

        "content-id-not-recomputed",
        fun bundle ->
            // Restated and offered under the id of the document it used
            // to be — deliberately not re-addressed, which is the case.
            {
                bundle with
                    NotProved =
                        bundle.NotProved
                        |> List.map (fun statement ->
                            if statement.Id = "uncomposed-substrate-is-silent" then
                                {
                                    statement with
                                        Statement = "everything not mentioned here was checked and is fine"
                                }
                            else
                                statement)
            }
    ]

let private injectedBundle (id: string) : EvidenceBundle =
    match bundleInjectors.TryFind id with
    | Some inject -> inject healthyBundle
    | None -> failtestf "the corpus names bundle case '%s' and this pack carries no injector for it" id

// ─── The deliberately-weakened verifier ──────────────────────────────
//
// A faithful copy of the shipped verifier's check SEQUENCE, in the
// shipped order, from which exactly one check can be omitted. Its
// fidelity is not assumed — an arm below pins it against the shipped
// verifier, with nothing omitted, over the baseline and every case in
// the corpus.

type private WeakenedCheck = {
    Id: string
    /// The position this check reports, when it fires.
    Fault: EvidenceBundle -> string option
}

let private weakenedChecks: WeakenedCheck list = [
    {
        Id = "schema"
        Fault =
            fun bundle ->
                if bundle.SchemaVersion <> EvidenceBundle.SchemaVersion then
                    Some "bundle/schemaVersion"
                else
                    None
    }
    {
        Id = "disposition"
        Fault =
            fun bundle ->
                if bundle.NestedAttestationDisposition <> EvidenceBundle.CarriedVerbatim then
                    Some "bundle/nestedAttestationDisposition"
                else
                    None
    }
    {
        Id = "hop-count"
        Fault =
            fun bundle ->
                if List.length bundle.Chain.Hops <> List.length EvidenceChain.order then
                    Some "bundle/chain/hops"
                else
                    None
    }
    {
        Id = "hop-order"
        Fault =
            fun bundle ->
                // Guarded on length, because with the count check omitted
                // this one would otherwise be handed two lists it cannot
                // zip — and a weakened copy that THREW where the shipped
                // verifier returns a verdict would falsify nothing.
                if List.length bundle.Chain.Hops <> List.length EvidenceChain.order then
                    None
                else
                    List.zip bundle.Chain.Hops EvidenceChain.order
                    |> List.indexed
                    |> List.tryPick (fun (index, (hop, expectedId)) ->
                        if hop.Id <> expectedId then
                            Some $"bundle/chain/hops[{index}]"
                        elif hop.Ordinal <> index + 1 then
                            Some $"bundle/chain/hops[{index}]"
                        else
                            None)
    }
    {
        Id = "outcome"
        Fault =
            fun bundle ->
                if bundle.Chain.Outcome <> EvidenceChain.outcomeOf bundle.Chain.Hops then
                    Some "bundle/chain/outcome"
                else
                    None
    }
    {
        Id = "verdict-digest"
        Fault =
            fun bundle ->
                if
                    bundle.Chain.VerdictDigest
                    <> digest (EvidenceChain.canonicalForm bundle.Chain.Hops)
                then
                    Some "bundle/chain/verdictDigest"
                else
                    None
    }
    {
        Id = "claim-boundary"
        Fault =
            fun bundle ->
                if List.isEmpty bundle.NotProved then
                    Some "bundle/notProved"
                else
                    None
    }
    {
        Id = "content-id"
        Fault =
            fun bundle ->
                if bundle.ContentId <> digest (EvidenceBundle.canonicalForm bundle) then
                    Some "bundle/contentId"
                else
                    None
    }
]

/// The verifier as it would be if one named check had never been
/// written. An empty `omit` is the faithful whole.
let private verifyOmitting (omit: string) (bundle: EvidenceBundle) : string option =
    weakenedChecks
    |> List.filter (fun check -> check.Id <> omit)
    |> List.tryPick (fun check -> check.Fault bundle)

let private shippedPosition (bundle: EvidenceBundle) : string option =
    match EvidenceBundleExport.verifyBundle bundle with
    | BundleIntegrity.Intact -> None
    | BundleIntegrity.BrokenAt(position, _) -> Some position

// ─── Part E — the corpus is one home, and it is complete ─────────────

let corpusPlacementTests =
    testList "Phase 715 — E: the corpus lives in one home and the tests read it" [

        test "every corpus file is present and declares which corpus it belongs to" {
            for name in
                [
                    "healthy-baseline.json"
                    "chain-break-cases.json"
                    "bundle-tamper-cases.json"
                    "absent-vs-broken-pairs.json"
                ] do
                Expect.equal
                    ((corpus name).GetProperty("corpus").GetString())
                    "evidence-chain-break-injection"
                    (sprintf "'%s' belongs to this corpus" name)
        }

        test "every corpus case has an injector, and every injector is named by a case" {
            // Both directions. A case with no injector is a claim nothing
            // exercises; an injector no case names is a break the corpus
            // does not enumerate, and would go unreported the day it
            // stopped firing.
            let declaredChain = chainCases |> List.map _.Id |> Set.ofList
            let implementedChain = chainInjectors |> Map.keys |> Set.ofSeq

            Expect.isEmpty
                (Set.difference declaredChain implementedChain |> Set.toList)
                "every chain case the corpus declares must have an injector"

            Expect.isEmpty
                (Set.difference implementedChain declaredChain |> Set.toList)
                "every chain injector must be named by a corpus case"

            let declaredBundle =
                bundleCasesAt "bundle" |> List.map (fun case -> field case "id") |> Set.ofList

            let implementedBundle = bundleInjectors |> Map.keys |> Set.ofSeq

            Expect.isEmpty
                (Set.difference declaredBundle implementedBundle |> Set.toList)
                "every bundle case the corpus declares must have an injector"

            Expect.isEmpty
                (Set.difference implementedBundle declaredBundle |> Set.toList)
                "every bundle injector must be named by a corpus case"
        }

        test "the corpus reaches every hop and every link verdict" {
            for hopId in EvidenceChain.order do
                let atHop = chainCases |> List.filter (fun case -> case.Hop = hopId)

                Expect.isTrue
                    (atHop |> List.exists (fun case -> case.Verdict = "broken"))
                    (sprintf
                        "hop '%s' must carry a break case — a hop with no break case is a verdict nothing has been shown to reach"
                        hopId)

                Expect.isTrue
                    (atHop |> List.exists (fun case -> case.Verdict = "absent"))
                    (sprintf "hop '%s' must carry an absence case, so the pair in part D has both halves" hopId)

            let verdicts = chainCases |> List.map _.Verdict |> Set.ofList

            for verdict in [ "broken"; "absent"; "withheld" ] do
                Expect.isTrue (verdicts.Contains verdict) (sprintf "the corpus must reach the '%s' verdict" verdict)
        }

        test "the corpus covers every structural position the bundle verifier can name" {
            let declared =
                bundleCasesAt "bundle"
                |> List.map (fun case -> field case "position")
                |> Set.ofList

            let reachable =
                weakenedChecks
                |> List.map (fun check ->
                    match check.Id with
                    | "schema" -> "bundle/schemaVersion"
                    | "disposition" -> "bundle/nestedAttestationDisposition"
                    | "hop-count" -> "bundle/chain/hops"
                    | "hop-order" -> "bundle/chain/hops[0]"
                    | "outcome" -> "bundle/chain/outcome"
                    | "verdict-digest" -> "bundle/chain/verdictDigest"
                    | "claim-boundary" -> "bundle/notProved"
                    | "content-id" -> "bundle/contentId"
                    | other -> other)
                |> Set.ofList

            Expect.isEmpty
                (Set.difference reachable declared |> Set.toList)
                "every structural fault the verifier can report must have a case in the corpus"
        }

        test "every identifier the corpus pins is synthetic" {
            // The visibility gate, asserted rather than remembered. Each
            // pinned position is a lowercase-hex digest, a decimal ledger
            // index, or a member of a reserved synthetic family.
            let reserved = [ "corpus"; "wc-"; "module-set" ]

            let isDigest (value: string) =
                value.Length = 64 && value |> Seq.forall Char.IsAsciiHexDigitLower

            let isIndex (value: string) =
                value.Length > 0 && value |> Seq.forall Char.IsAsciiDigit

            let foreign =
                chainCases
                |> List.map _.Position
                |> List.filter (fun position -> position <> "")
                |> List.filter (fun position ->
                    not (
                        isDigest position
                        || isIndex position
                        || reserved |> List.exists position.Contains
                    ))

            Expect.isEmpty
                foreign
                "no fixture may carry a real deployment, tenant, key or system name — every pinned identifier is synthetic"
        }
    ]

// ─── Part A — the healthy baseline, pinned ───────────────────────────

let healthyBaselineTests =
    testList "Phase 715 — A: the healthy baseline verifies end to end, and its digests are pinned" [

        test "every hop resolves, at the reference and the ordinal the corpus pins" {
            let baseline = corpus "healthy-baseline.json"

            Expect.equal
                (EvidenceChainOutcome.label healthyChain.Outcome)
                (field baseline "outcome")
                "the baseline is the one arrangement in which the whole walk resolves"

            let mismatches =
                items baseline "hops"
                |> List.collect (fun expected ->
                    let hopId = field expected "id"
                    let hop = hopOf healthyChain hopId
                    let verdict = EvidenceLink.label hop.Link
                    let reference = EvidenceLink.reference hop.Link
                    let pinnedVerdict = field expected "verdict"
                    let pinnedReference = field expected "reference"
                    let pinnedOrdinal = intField expected "ordinal"

                    [
                        if verdict <> pinnedVerdict then
                            sprintf "hop '%s' verdict: read '%s', corpus pins '%s'" hopId verdict pinnedVerdict
                        if reference <> pinnedReference then
                            sprintf "hop '%s' reference: read '%s', corpus pins '%s'" hopId reference pinnedReference
                        if hop.Ordinal <> pinnedOrdinal then
                            sprintf "hop '%s' ordinal: read %d, corpus pins %d" hopId hop.Ordinal pinnedOrdinal
                    ])

            Expect.equal (String.concat " | " mismatches) "" "the baseline must read exactly as the corpus pins it"
        }

        test "the chain's verdict digest and the bundle's content id are pinned" {
            // The drift alarm. Both are digests over canonical forms
            // declared in the shared tier; an unintended change to either
            // form silently re-addresses every artefact this substrate
            // has produced, and this arm refuses to let that happen
            // quietly.
            let baseline = corpus "healthy-baseline.json"

            let pins = [
                "the chain's verdict digest", healthyChain.VerdictDigest, field baseline "chainVerdictDigest"
                "the bundle's content id", healthyBundle.ContentId, field baseline "bundleContentId"
            ]

            let drifted =
                pins
                |> List.filter (fun (_, computed, pinned) -> computed <> pinned)
                |> List.map (fun (name, computed, pinned) ->
                    sprintf "%s: computed '%s', corpus pins '%s'" name computed pinned)

            Expect.equal
                (String.concat " | " drifted)
                ""
                "a pinned digest that moved means a canonical form changed — regenerate the pin deliberately, from the tree in which it changed, rather than copying the new value out of this message"
        }

        test "the healthy bundle is structurally intact and states what it does not prove" {
            let baseline = corpus "healthy-baseline.json"

            Expect.equal
                (BundleIntegrity.label (EvidenceBundleExport.verifyBundle healthyBundle))
                (field baseline "bundleIntegrity")
                "the baseline bundle verifies, so every tamper case below starts from a document that does"

            Expect.isNonEmpty
                healthyBundle.NotProved
                "and it carries its claim boundary on the clean case, which is the only run anybody reads"
        }

        test "the baseline's ancestor enumeration is pinned too" {
            // The count the severed-ancestor case is measured against, so
            // it is pinned rather than inferred.
            let baseline = corpus "healthy-baseline.json"

            Expect.equal
                (List.length (hopOf healthyChain EvidenceChain.UpstreamWorkRecordHop).Findings)
                (intField baseline "upstreamAncestorFindings")
                "the walk reaches the head and its parent at the corpus's declared depth"
        }
    ]

// ─── Part B — one variant per break class, at the chain level ────────

let chainBreakCorpusTests =
    testList "Phase 715 — B: every chain break class asserts its verdict AND its position" [

        for case in chainCases do
            if not case.Unproven then
                test (sprintf "%s — %s" case.Id case.Class) {
                    let chain = injectedChain case
                    let hop = hopOf chain case.Hop

                    Expect.equal
                        (EvidenceLink.label hop.Link)
                        case.Verdict
                        (sprintf "'%s' must reach the verdict the corpus names" case.Id)

                    Expect.equal
                        (EvidenceLink.reference hop.Link)
                        case.Position
                        (sprintf
                            "'%s' must name WHERE it sits — a finding a reader has to re-walk the chain to locate is not actionable"
                            case.Id)

                    Expect.equal
                        hop.Ordinal
                        case.Ordinal
                        (sprintf "'%s' must land at the hop position the corpus names" case.Id)

                    Expect.equal
                        (EvidenceChainOutcome.label chain.Outcome)
                        case.Outcome
                        (sprintf "'%s' must fold to the top-line outcome the corpus names" case.Id)

                    Expect.isGreaterThan
                        (EvidenceLink.detail hop.Link).Length
                        20
                        (sprintf "'%s' must account for itself rather than merely labelling itself" case.Id)

                    Expect.equal
                        (List.length chain.Hops)
                        (List.length EvidenceChain.order)
                        (sprintf
                            "'%s' must still return the whole hop list — an injection that shortened the chain would read as a healthier one"
                            case.Id)
                }

        test "a severed ANCESTOR reference produces no verdict — recorded as unproven" {
            // Part C's honest half: a break class that cannot be made to
            // fire is reported as such and its check treated as unproven,
            // never quietly kept. The ancestor walk drops a reference it
            // cannot resolve and returns what it did reach, so the hop
            // stays linked and only the enumeration behind it is shorter.
            let case = chainCase "work-ancestor-severed"

            Expect.isTrue case.Unproven "the corpus records this case as unproven"
            Expect.equal case.Falsification "unproven" "and names its falsification method accordingly"

            let chain = injectedChain case
            let hop = hopOf chain EvidenceChain.UpstreamWorkRecordHop

            Expect.equal
                (EvidenceLink.label hop.Link)
                "linked"
                "the hop reads exactly as it does when nothing is severed — which is the finding, not a pass"

            let expected =
                intField (chainCaseElement "work-ancestor-severed") "expectedAncestorFindings"

            Expect.equal
                (List.length hop.Findings)
                expected
                "the severed edge is dropped silently; the corpus pins the shortened enumeration so the silence is on the record"

            Expect.isLessThan
                (List.length hop.Findings)
                (List.length (hopOf healthyChain EvidenceChain.UpstreamWorkRecordHop).Findings)
                "and it really is shorter than the baseline's — otherwise this arm would pin nothing at all"
        }
    ]

// ─── Part C — falsification, at the chain level ──────────────────────

let chainFalsificationTests =
    testList "Phase 715 — C: every chain case is shown to fail against a chain lacking its break" [

        test "each case's own assertion fails against the healthy baseline" {
            // Direction one. A verdict that fired unconditionally — the
            // decorative check inverted — would satisfy every arm in part
            // B and fail here.
            let survivors =
                chainCases
                |> List.filter (fun case -> not case.Unproven)
                |> List.filter (fun case -> saysWhatTheCaseClaims case healthyChain)
                |> List.map _.Id

            Expect.isEmpty
                survivors
                "a case whose assertion also holds on the healthy baseline asserts nothing about the break it names"
        }

        test "the baseline's own reading fails against each injected fixture" {
            // Direction two, and the one that catches a DECORATIVE check:
            // a verdict that could never fire leaves the injected hop
            // reading exactly as the baseline's does.
            let unmoved =
                chainCases
                |> List.filter (fun case -> not case.Unproven)
                |> List.filter (fun case ->
                    let baseline = hopOf healthyChain case.Hop
                    let injected = hopOf (injectedChain case) case.Hop

                    EvidenceLink.label injected.Link = EvidenceLink.label baseline.Link
                    && EvidenceLink.reference injected.Link = EvidenceLink.reference baseline.Link)
                |> List.map _.Id

            Expect.isEmpty
                unmoved
                "a case whose injection leaves its hop reading as the baseline's has injected nothing the walk can see"
        }

        test "every case declares the method it was falsified by" {
            for case in chainCases do
                Expect.contains
                    [ "discriminating-twin"; "weakened-verifier"; "unproven" ]
                    case.Falsification
                    (sprintf
                        "case '%s' must name how it was demonstrated to fail against code lacking its check"
                        case.Id)
        }

        test "an injection moves only the hop it names" {
            // Verify the probe, not just the verdict. If an injection
            // moved several hops at once, part B's per-hop assertion
            // would be reading a side effect rather than the break — and
            // both arms above would still pass.
            let bleeding =
                chainCases
                |> List.filter (fun case -> not case.Unproven)
                |> List.collect (fun case ->
                    let injected = injectedChain case

                    EvidenceChain.order
                    |> List.filter (fun hopId -> hopId <> case.Hop)
                    |> List.filter (fun hopId ->
                        EvidenceLink.label (hopOf injected hopId).Link
                        <> EvidenceLink.label (hopOf healthyChain hopId).Link)
                    |> List.map (fun hopId -> sprintf "'%s' also moved hop '%s'" case.Id hopId))

            Expect.isEmpty bleeding "each injection perturbs exactly one join"
        }
    ]

// ─── Part B/C — the bundle level ─────────────────────────────────────

let bundleTamperCorpusTests =
    testList "Phase 715 — B: every bundle tamper class is reported at its own position" [

        for case in bundleCasesAt "bundle" do
            let id = field case "id"
            let className = field case "class"
            let expectedPosition = field case "position"

            test (sprintf "%s — %s" id className) {
                match EvidenceBundleExport.verifyBundle (injectedBundle id) with
                | BundleIntegrity.Intact -> failtestf "'%s' must not verify" id
                | BundleIntegrity.BrokenAt(position, reason) ->
                    Expect.equal
                        position
                        expectedPosition
                        (sprintf "'%s' must be reported at the position the corpus names, not merely refused" id)

                    Expect.isGreaterThan
                        reason.Length
                        20
                        (sprintf "'%s' must say what does not hold, not only that something does not" id)
            }
    ]

let bundleFalsificationTests =
    testList "Phase 715 — C: every bundle case passes a verifier that lacks its check" [

        test "the weakened verifier is a faithful copy when nothing is omitted" {
            // The probe is verified before it is trusted. With no check
            // omitted, the copy must agree with the SHIPPED verifier —
            // position for position — on the baseline and on every case
            // in the corpus. A strawman would agree with neither, and
            // would make every arm below meaningless.
            let subjects =
                ("healthy-baseline", healthyBundle)
                :: (bundleCasesAt "bundle"
                    |> List.map (fun case ->
                        let id = field case "id"
                        id, injectedBundle id))

            let disagreements =
                subjects
                |> List.filter (fun (_, bundle) -> verifyOmitting "" bundle <> shippedPosition bundle)
                |> List.map (fun (name, bundle) ->
                    sprintf
                        "%s: the copy says %A, the shipped verifier says %A"
                        name
                        (verifyOmitting "" bundle)
                        (shippedPosition bundle))

            Expect.isEmpty
                disagreements
                "the weakened copy must reproduce the shipped verifier exactly before it is weakened"
        }

        for case in bundleCasesAt "bundle" do
            let id = field case "id"
            let omit = field case "omitCheck"
            let expectedPosition = field case "position"

            test (sprintf "%s — passes a verifier lacking the '%s' check" id omit) {
                Expect.equal
                    (shippedPosition (injectedBundle id))
                    (Some expectedPosition)
                    (sprintf "the shipped verifier catches '%s'" id)

                Expect.isNone
                    (verifyOmitting omit (injectedBundle id))
                    (sprintf
                        "and a verifier written without the '%s' check passes it — which is what makes that check load-bearing rather than decorative"
                        omit)
            }

        test "omitting one check leaves every other one firing" {
            // Without this, the arms above would be satisfied by a
            // `verifyOmitting` that returned None for everything.
            for case in bundleCasesAt "bundle" do
                let omit = field case "omitCheck"
                let id = field case "id"

                let siblings =
                    bundleCasesAt "bundle"
                    |> List.filter (fun other -> field other "id" <> id)
                    |> List.filter (fun other -> field other "omitCheck" <> omit)
                    |> List.map (fun other -> field other "id")

                let stillCaught =
                    siblings
                    |> List.filter (fun siblingId -> (verifyOmitting omit (injectedBundle siblingId)).IsSome)

                Expect.equal
                    (List.length stillCaught)
                    (List.length siblings)
                    (sprintf "omitting '%s' must disable exactly that check and nothing else" omit)
        }
    ]

// ─── Part B/C — the document level ───────────────────────────────────
//
// Three classes no amount of structural checking reaches, because each
// is about the relationship between a document and a key, or between a
// document and what its holder already knew. Every one is asserted in
// BOTH halves: what the structural verifier says, and what the check
// that actually catches it says.

/// Minimal in-memory secret store — the signing key is provisioned into
/// it on first use.
type private CorpusSecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, value -> return Some value
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (scope, _) -> scope = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

let private corpusKeyId = "corpus-bundle-key"

/// A signer and the public half that checks it.
let private signingPair () =
    let secrets = CorpusSecretStore() :> ISecretStore
    let signer = DsseEnvelopeSigning.fromSecretStore secrets corpusKeyId Ed25519
    let audit = AuditLog.NoOpAuditLog() :> IAuditLog

    let artefactSigner =
        DefaultArtefactSigner.createSystem secrets audit corpusKeyId Ed25519

    async {
        let! publicKey = artefactSigner.VerifyKey()
        return signer, publicKey
    }

/// A statement publishing `subject`'s content id and carrying
/// `payload`'s predicate. The two are the same for an honest document,
/// and deliberately different for one case below.
let private statementOver (subject: EvidenceBundle) (payload: EvidenceBundle) : string =
    DsseEnvelope.statementJson
        [ EvidenceBundleExport.subjectFor subject ]
        EvidenceBundleExport.PredicateType
        (EvidenceBundleExport.predicateJson payload)

/// The chain altered and the bundle re-addressed over it — a document
/// that is perfectly well-formed and is about a different record set.
let private alteredBundle =
    healthyBundle
    |> mapHop EvidenceChain.DependencyClosureHop (fun hop -> {
        hop with
            Link =
                EvidenceLink.Linked(
                    EvidenceLink.reference hop.Link,
                    "a rather more flattering account of the same closure"
                )
    })
    |> rederive

let documentTamperCorpusTests =
    testList "Phase 715 — B/C: the tamper classes a structural check cannot reach" [

        test "resigned-over-altered-content — refused at the subject, and passed by the structural half" {
            async {
                let expectedPosition =
                    field (bundleCaseElement "resigned-over-altered-content") "position"

                let! signer, publicKey = signingPair ()

                // Signed for real, over a statement that publishes the
                // ORIGINAL subject digest and carries the altered bundle.
                match!
                    DsseEnvelope.sign
                        signer
                        [ EvidenceBundleExport.subjectFor healthyBundle ]
                        EvidenceBundleExport.PredicateType
                        (EvidenceBundleExport.predicateJson alteredBundle)
                with
                | Error reason -> return failtestf "the corpus document must sign: %s" reason
                | Ok envelope ->
                    match DsseEnvelopeSigning.verify publicKey (EvidenceBundleExport.expectation None) envelope with
                    | Error verdict ->
                        return
                            failtestf
                                "the re-signed document's signature must be VALID — that is the whole hazard: %A"
                                verdict
                    | Ok _ ->
                        match EvidenceBundleExport.verifyDocument (DsseEnvelope.toJson envelope) with
                        | BundleIntegrity.Intact ->
                            return
                                failtest "a validly-signed statement about a different bundle must not read as intact"
                        | BundleIntegrity.BrokenAt(position, _) ->
                            Expect.equal
                                position
                                expectedPosition
                                "the document reader names the subject, because that is what disagrees"

                            // Falsification: the code lacking the subject
                            // check IS the structural verifier, which
                            // never sees the statement at all.
                            Expect.equal
                                (EvidenceBundleExport.verifyBundle alteredBundle)
                                BundleIntegrity.Intact
                                "and the structural verifier — code that lacks the subject check by construction — passes the very same bundle"
            }
            |> Async.RunSynchronously
        }

        test "resigned-and-readdressed — structurally intact, and refused by the holder's own claim check" {
            async {
                let! signer, publicKey = signingPair ()

                match! EvidenceBundleExport.export signer alteredBundle with
                | Error reason -> return failtestf "the corpus document must sign: %s" reason
                | Ok envelope ->
                    Expect.equal
                        (EvidenceBundleExport.verifyDocument (DsseEnvelope.toJson envelope))
                        BundleIntegrity.Intact
                        "a re-signed, re-addressed forgery IS well-formed, and recording that honestly is the point rather than an omission"

                    Expect.notEqual
                        alteredBundle.ContentId
                        healthyBundle.ContentId
                        "it is a different record set, and it says so"

                    // What actually refuses it: the holder's own prior
                    // knowledge of the id, carried as the expectation.
                    match
                        DsseEnvelopeSigning.verify
                            publicKey
                            (EvidenceBundleExport.expectation (Some healthyBundle.ContentId))
                            envelope
                    with
                    | Ok _ ->
                        return
                            failtest
                                "a holder who possesses the original content id must not accept a document about another"
                    | Error(EnvelopeSubjectMismatch(expected, _)) ->
                        Expect.equal expected healthyBundle.ContentId "the refusal quotes the id the holder held"

                        // Falsification: the same verifier without the
                        // holder's id — that check omitted — accepts it.
                        match DsseEnvelopeSigning.verify publicKey (EvidenceBundleExport.expectation None) envelope with
                        | Ok _ -> ()
                        | Error other ->
                            return
                                failtestf
                                    "a holder with no independently-held id must accept it, or this case falsifies nothing: %A"
                                    other
                    | Error other -> return failtestf "the refusal must be a subject mismatch: %A" other
            }
            |> Async.RunSynchronously
        }

        test "inner-verifies-outer-does-not — a structural pass is not a signature pass" {
            async {
                let! signer, publicKey = signingPair ()

                match! EvidenceBundleExport.export signer healthyBundle with
                | Error reason -> return failtestf "the corpus document must sign: %s" reason
                | Ok envelope ->
                    // The payload is swapped for a DIFFERENT but wholly
                    // well-formed statement, leaving the signature over
                    // bytes that are no longer there.
                    let transplanted = {
                        envelope with
                            Payload =
                                Convert.ToBase64String(
                                    Encoding.UTF8.GetBytes(statementOver alteredBundle alteredBundle)
                                )
                    }

                    Expect.equal
                        (EvidenceBundleExport.verifyDocument (DsseEnvelope.toJson transplanted))
                        BundleIntegrity.Intact
                        "the inner statement verifies — it is self-consistent and its subject names the bundle it carries"

                    match DsseEnvelopeSigning.verify publicKey (EvidenceBundleExport.expectation None) transplanted with
                    | Ok _ -> return failtest "and the outer envelope must NOT verify"
                    | Error verdict ->
                        Expect.equal
                            verdict
                            EnvelopeSignatureInvalid
                            "the signature covers the pre-authentication encoding, so a transplanted payload breaks it"

                        // The falsification is the split itself: the
                        // structural verifier is code lacking any
                        // signature check, and it passes the very
                        // document whose signature does not validate. A
                        // reader who ran only one of the two must not be
                        // able to mistake it for both — so the report a
                        // structural pass prints says so, on the pass.
                        Expect.stringContains
                            (EvidenceBundleExport.verifyCommand (DsseEnvelope.toJson transplanted)).Report
                            "It says nothing about who signed it"
                            "the structural pass declares what it does not establish"
            }
            |> Async.RunSynchronously
        }
    ]

// ─── Part D — absent and broken, distinguishable at every hop ────────

let absentVsBrokenTests =
    testList "Phase 715 — D: absent and broken read differently at every hop" [

        test "the corpus pairs every hop in the walk" {
            let paired = pairElements |> List.map (fun pair -> field pair "hop")

            Expect.equal
                paired
                EvidenceChain.order
                "a pair per hop, in walk order — collapsing the two is the model's central risk, so the discrimination is pinned everywhere rather than wherever it was convenient"
        }

        for pair in pairElements do
            let hopId = field pair "hop"
            let absentId = field pair "absentCase"
            let brokenId = field pair "brokenCase"

            test (sprintf "%s — an absent join and a broken one are two different answers" hopId) {
                let absent = injectedChain (chainCase absentId)
                let broken = injectedChain (chainCase brokenId)

                let absentHop = hopOf absent hopId
                let brokenHop = hopOf broken hopId

                Expect.equal
                    (EvidenceLink.label absentHop.Link)
                    "absent"
                    (sprintf "the absent half of '%s' reads absent" hopId)

                Expect.equal
                    (EvidenceLink.label brokenHop.Link)
                    "broken"
                    (sprintf "the broken half of '%s' reads broken" hopId)

                Expect.equal
                    (EvidenceLink.reference absentHop.Link)
                    ""
                    "an absence names no position, because there is nothing there to point at"

                Expect.isGreaterThan
                    (EvidenceLink.reference brokenHop.Link).Length
                    0
                    "and a break names one, because a finding a reader must re-walk the chain to locate is not actionable"

                Expect.notEqual
                    (EvidenceLink.detail absentHop.Link)
                    (EvidenceLink.detail brokenHop.Link)
                    "the two accounts differ in prose as well as in label"

                Expect.notEqual
                    absent.Outcome
                    broken.Outcome
                    "the difference survives the fold to the top line — a break reddens the chain and an absence bounds it"

                Expect.notEqual
                    (EvidenceChain.render absent)
                    (EvidenceChain.render broken)
                    "and it survives the render, which is the surface an operator actually reads"
            }
    ]