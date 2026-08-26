module ToolUp.Platform.Tests.InProcess.WorkProvenanceSourceTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.InProcess.BuildTranscriptTests

// ─── Phase 712 — the upstream work record as a read-only wire contract ──
//
// The work/build tier of the contract shape the fact/data tier already
// ships. Five properties carry the phase and each has its own arm:
//
//   * read-only by construction — no member of the seam answers with
//     `unit`, so there is no write surface to gate;
//   * bounded, never truncated — an over-cap request and an over-cap
//     ANSWER are both refused typed, naming what was asked and the
//     limit, and the same walk under a roomier cap returns everything,
//     which is what makes the refusal a refusal rather than a
//     truncation dressed up as one;
//   * withheld is not absent — a suppressed record crosses as a marker
//     carrying its ref, its kind and the policy that refused it, and
//     that answer is distinguishable from "nothing recorded here";
//   * an unknown kind crosses INTACT — `Other` is the case that keeps a
//     foreign vocabulary from being dropped or coerced at the boundary;
//   * an uncomposed deployment is unchanged — nothing is registered,
//     nothing is asked, the deploy record's canonical bytes are
//     byte-for-byte what they were, and the reading records the honest
//     reason rather than a blank.

// ── Doubles ──────────────────────────────────────────────────────────

let private system = "work-system"

let private ref' (id: string) : WorkRecordRef = WorkRecordRef.create system id

/// A source system double: a fixed record table, a configurable
/// coverage answer, and a lookup counter so an arm can prove the
/// uncomposed path asked NOTHING rather than asked and got nothing.
type private FakeWorkSource
    (caps: WorkProvenanceCaps, records: Map<string, WorkRecordAnswer>, coverage: string -> Result<WorkCoverage, string>)
    =

    let mutable lookups = 0
    let mutable coverageCalls = 0

    member _.Lookups = lookups
    member _.CoverageCalls = coverageCalls

    interface IWorkProvenanceSource with
        member _.SourceSystem() = system

        member _.GetCaps() = async { return caps }

        member _.GetRecord reference = async {
            lookups <- lookups + 1

            return
                records
                |> Map.tryFind reference.RecordId
                |> Option.defaultValue WorkRecordAnswer.Absent
        }

        member this.GetAncestors request =
            WorkProvenanceSource.walkOverLookups (this :> IWorkProvenanceSource) request

        member _.Covering upstreamReference = async {
            coverageCalls <- coverageCalls + 1
            return coverage upstreamReference
        }

/// A source that declares a cap and then answers with more than it —
/// the implementation the bounded decorator exists to catch, because a
/// caller holding its answer cannot tell a trimmed chain from a
/// complete one.
type private OverCapWorkSource(caps: WorkProvenanceCaps, page: WorkAncestorPage) =
    interface IWorkProvenanceSource with
        member _.SourceSystem() = system
        member _.GetCaps() = async { return caps }
        member _.GetRecord _ = async { return WorkRecordAnswer.Absent }
        member _.GetAncestors _ = async { return Result.Ok page }
        member _.Covering _ = async { return Result.Ok WorkCoverage.NoCoveringRecord }

// ── Seed ─────────────────────────────────────────────────────────────
//
// w1 --> w2 --> w3        (w3 carries a kind this SDK has never heard of)
//  \
//   --> w-secret          (the source system refuses it)
//
// and `w-missing`, which the source system simply does not hold.

let private secretMarker: WithheldWorkRecord = {
    Ref = ref' "w-secret"
    Kind = WorkRecordKind.Reviewed
    PolicyRef = "restricted/internal-review"
}

let private w3: WorkRecord = {
    Ref = ref' "w3"
    Kind = WorkRecordKind.Other "cherry-picked"
    ContentDigest = "cc33"
    Parents = []
    Verdict = Some "accepted"
    Label = "the root of the chain"
}

let private w2: WorkRecord = {
    Ref = ref' "w2"
    Kind = WorkRecordKind.Reviewed
    ContentDigest = "bb22"
    Parents = [ ref' "w3" ]
    Verdict = None
    Label = "reviewed"
}

let private w1: WorkRecord = {
    Ref = ref' "w1"
    Kind = WorkRecordKind.Authored
    ContentDigest = "aa11"
    Parents = [ ref' "w2"; ref' "w-secret" ]
    Verdict = Some "green"
    Label = "the head"
}

let private table =
    Map.ofList [
        "w1", WorkRecordAnswer.Found w1
        "w2", WorkRecordAnswer.Found w2
        "w3", WorkRecordAnswer.Found w3
        "w-secret", WorkRecordAnswer.Withheld secretMarker
    ]

let private noCoverage (_: string) = Result.Ok WorkCoverage.NoCoveringRecord

let private sourceWith (caps: WorkProvenanceCaps) = FakeWorkSource(caps, table, noCoverage)

let private bounded (source: FakeWorkSource) =
    WorkProvenanceSource.bounded (source :> IWorkProvenanceSource)

let private ancestorsOf (id: string) (depth: int) : WorkAncestorRequest = { Root = ref' id; Depth = depth }

// ── A deploy record fixture, exactly as a pre-712 deployment builds one ──

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

let private recordWith (provenance: DeployProvenance) =
    DeployRecord.create "deploy-1" "tenant-1" "build-1" manifest provenance

let private filledProvenance =
    DeployProvenance.none
    |> DeployProvenance.withUpstreamProvenanceDigest "sha256:upstream"

// ── Reflection helper for the read-only pin ──────────────────────────

/// Strip `Async<…>` and then a `Result<…, _>` wrapper, leaving the type
/// a method actually answers with.
let private answerType (returnType: Type) =
    let stripGeneric (definition: Type) (t: Type) =
        if t.IsGenericType && t.GetGenericTypeDefinition() = definition then
            Some(t.GetGenericArguments()[0])
        else
            None

    match stripGeneric typedefof<Async<_>> returnType with
    | None -> None
    | Some inner ->
        match stripGeneric typedefof<Result<_, _>> inner with
        | Some ok -> Some ok
        | None -> Some inner

let tests =
    testList "IWorkProvenanceSource (Phase 712)" [

        // ── Read-only by construction ────────────────────────────────

        testList "read-only shape" [
            // The pin below rests entirely on `answerType` unwrapping
            // `Async<Result<_, _>>` down to the value a method answers
            // with. An unwrap that stopped one layer short would still
            // satisfy "not unit" for every member — a probe answering a
            // slightly different question than the one asked. So pin the
            // unwrap itself against a known member first.
            test "the answer-type unwrap reaches the value, not its wrapper" {
                let getAncestors = typeof<IWorkProvenanceSource>.GetMethod "GetAncestors"

                Expect.equal
                    (answerType getAncestors.ReturnType)
                    (Some typeof<WorkAncestorPage>)
                    "Async<Result<WorkAncestorPage, _>> must unwrap to the page"
            }

            test "no member of the seam answers with unit" {
                let methods = typeof<IWorkProvenanceSource>.GetMethods()

                Expect.isGreaterThan
                    methods.Length
                    0
                    "the seam must expose methods — an empty interface would pass vacuously"

                for method' in methods do
                    // A `unit` answer is the shape a mutation takes: a
                    // caller that gets nothing back asked for an effect,
                    // not a value. There is no such member here and this
                    // arm is what keeps it that way. F# compiles a
                    // unit-returning member to `void`, so both spellings
                    // are checked.
                    Expect.notEqual
                        method'.ReturnType.FullName
                        "System.Void"
                        $"{method'.Name} answers with unit — that is a mutation's shape, and this seam is read-only"

                    Expect.notEqual
                        (answerType method'.ReturnType)
                        (Some typeof<unit>)
                        $"{method'.Name} answers with Async<unit> — that is a mutation's shape too"
            }
        ]

        // ── Bounded, never truncated ─────────────────────────────────

        testList "bounded ancestor walk" [
            testCaseAsync "a depth above the declared cap is refused, naming the request and the cap"
            <| async {
                let caps = {
                    WorkProvenanceCaps.defaults with
                        MaxDepth = 4
                }

                let source = bounded (sourceWith caps)

                let! answer = source.GetAncestors(ancestorsOf "w1" 5)

                match answer with
                | Result.Error(WorkDepthExceedsCap(requested, cap)) ->
                    Expect.equal requested 5 "the refusal names what was asked"
                    Expect.equal cap 4 "and the cap that refused it"
                | other -> failtestf "expected a depth-cap refusal, got %A" other
            }

            testCaseAsync "a depth below one is refused rather than answered with the bare root"
            <| async {
                let source = bounded (sourceWith WorkProvenanceCaps.defaults)

                let! answer = source.GetAncestors(ancestorsOf "w1" 0)

                match answer with
                | Result.Error(WorkDepthInvalid requested) ->
                    Expect.equal requested 0 "the refusal names what was asked"
                | other -> failtestf "expected an invalid-depth refusal, got %A" other
            }

            testCaseAsync "a walk above the record cap is refused whole, never truncated"
            <| async {
                let narrow = { MaxDepth = 10; MaxRecords = 3 }

                let source = bounded (sourceWith narrow)
                let! answer = source.GetAncestors(ancestorsOf "w1" 3)

                match answer with
                | Result.Error(WorkAncestorsExceedRecordCap(records, cap)) ->
                    Expect.isGreaterThan records 3 "the refusal names how many the walk actually reached"
                    Expect.equal cap 3 "and the cap it exceeded"
                | other -> failtestf "expected a record-cap refusal, got %A" other

                // The same walk under a cap that admits it returns EVERY
                // record — so the refusal above was a refusal, not a
                // truncation dressed up as one.
                let roomy = { MaxDepth = 10; MaxRecords = 100 }

                let generous = bounded (sourceWith roomy)
                let! full = generous.GetAncestors(ancestorsOf "w1" 3)

                match full with
                | Result.Ok page ->
                    Expect.equal
                        (page.Records |> List.map (fun r -> r.Ref.RecordId) |> List.sort)
                        [ "w1"; "w2"; "w3" ]
                        "the complete answer carries the whole readable chain"

                    Expect.equal
                        (WorkAncestorPage.size page)
                        4
                        "readable plus withheld is what the cap is taken against"

                    Expect.equal page.Depth 3 "the bound the answer was produced under is echoed"
                    Expect.equal page.Root (ref' "w1") "rooted where the walk was rooted"
                | other -> failtestf "expected the complete chain, got %A" other
            }

            testCaseAsync "a source that answers above its own declared cap is refused, not passed on"
            <| async {
                // The decorator's reason for existing: an implementation
                // that trimmed its own walk hands back an answer a caller
                // cannot distinguish from a complete one.
                let caps = { MaxDepth = 10; MaxRecords = 2 }

                let oversized: WorkAncestorPage = {
                    Root = ref' "w1"
                    Records = [ w1; w2; w3 ]
                    Withheld = []
                    Depth = 3
                }

                let source =
                    WorkProvenanceSource.bounded (OverCapWorkSource(caps, oversized) :> IWorkProvenanceSource)

                let! answer = source.GetAncestors(ancestorsOf "w1" 3)

                match answer with
                | Result.Error(WorkAncestorsOverDeclaredCap(records, cap)) ->
                    Expect.equal records 3 "the refusal names what the source returned"
                    Expect.equal cap 2 "and the cap the source itself declared"
                | other -> failtestf "expected an over-declared-cap refusal, got %A" other
            }

            testCaseAsync "a shallower walk stops at the declared depth rather than running to the root"
            <| async {
                let source = bounded (sourceWith WorkProvenanceCaps.defaults)
                let! answer = source.GetAncestors(ancestorsOf "w1" 1)

                match answer with
                | Result.Ok page ->
                    Expect.equal
                        (page.Records |> List.map (fun r -> r.Ref.RecordId))
                        [ "w1" ]
                        "one hop reaches the head and no further"

                    Expect.isEmpty page.Withheld "and nothing below it, refused or otherwise"
                | other -> failtestf "expected a one-hop chain, got %A" other
            }

            test "every refusal names both what was asked and the limit" {
                let described =
                    [
                        WorkDepthInvalid 0
                        WorkDepthExceedsCap(11, 10)
                        WorkAncestorsExceedRecordCap(4, 3)
                        WorkAncestorsOverDeclaredCap(3, 2)
                    ]
                    |> List.map WorkProvenanceError.describe

                for text in described do
                    Expect.isTrue (text.Length > 0) "a refusal with no text is a refusal a caller cannot act on"

                Expect.stringContains (described[1]) "11" "the depth refusal names the request"
                Expect.stringContains (described[1]) "10" "and the cap"
                Expect.stringContains (described[2]) "4" "the record refusal names what the walk reached"
                Expect.stringContains (described[2]) "3" "and the cap"
            }
        ]

        // ── Withheld is not absent ───────────────────────────────────

        testList "withheld is not absent" [
            testCaseAsync "a refused record crosses as a marker, and an unheld ref reads Absent"
            <| async {
                let source = sourceWith WorkProvenanceCaps.defaults :> IWorkProvenanceSource

                let! withheld = source.GetRecord(ref' "w-secret")

                match withheld with
                | WorkRecordAnswer.Withheld marker ->
                    Expect.equal marker.Ref (ref' "w-secret") "the marker names the record"
                    Expect.equal marker.Kind WorkRecordKind.Reviewed "and its kind"

                    Expect.equal
                        marker.PolicyRef
                        "restricted/internal-review"
                        "and the policy that refused it — never the content"
                | other -> failtestf "expected a withheld marker, got %A" other

                let! missing = source.GetRecord(ref' "w-missing")

                Expect.equal
                    missing
                    WorkRecordAnswer.Absent
                    "a ref the source holds nothing for is Absent, not an empty Found"

                Expect.notEqual
                    withheld
                    missing
                    "a refusal and an absence must not be the same answer — that IS the property"
            }

            testCaseAsync "a walk seals a refused record's content and keeps its place in the chain"
            <| async {
                let source = bounded (sourceWith WorkProvenanceCaps.defaults)
                let! answer = source.GetAncestors(ancestorsOf "w1" 3)

                match answer with
                | Result.Ok page ->
                    Expect.isFalse
                        (page.Records |> List.exists (fun r -> r.Ref.RecordId = "w-secret"))
                        "the refused record's content does not cross"

                    Expect.isTrue
                        (page.Withheld
                         |> List.exists (fun w -> w.Ref = ref' "w-secret" && w.PolicyRef = "restricted/internal-review"))
                        "it crosses as a marker instead"

                    Expect.isTrue
                        (page.Records
                         |> List.exists (fun r -> r.Ref.RecordId = "w1" && r.Parents |> List.contains (ref' "w-secret")))
                        "chain SHAPE survives the refusal — the head still names it as a parent"

                    Expect.isFalse
                        (page.Records |> List.exists (fun r -> r.Ref.RecordId = "w-missing"))
                        "and a record nobody holds appears nowhere at all"
                | other -> failtestf "expected a chain, got %A" other
            }
        ]

        // ── An unknown kind crosses intact ───────────────────────────

        testList "unknown kinds" [
            testCaseAsync "a kind this SDK has never heard of round-trips unchanged"
            <| async {
                let source = bounded (sourceWith WorkProvenanceCaps.defaults)
                let! answer = source.GetAncestors(ancestorsOf "w1" 3)

                match answer with
                | Result.Ok page ->
                    let foreign = page.Records |> List.find (fun r -> r.Ref.RecordId = "w3")

                    Expect.equal
                        foreign.Kind
                        (WorkRecordKind.Other "cherry-picked")
                        "the foreign kind crosses intact — not dropped, not coerced into a known case"

                    Expect.equal
                        (WorkRecordKind.label foreign.Kind)
                        "cherry-picked"
                        "and renders as the source system's own word"

                    Expect.equal foreign.Verdict (Some "accepted") "the recorded verdict is carried verbatim"
                | other -> failtestf "expected a chain, got %A" other
            }

            test "the closed kinds keep stable tokens and the open one is not one of them" {
                Expect.equal
                    ([
                        WorkRecordKind.Authored
                        WorkRecordKind.Reviewed
                        WorkRecordKind.Verified
                        WorkRecordKind.Released
                     ]
                     |> List.map WorkRecordKind.label)
                    [ "authored"; "reviewed"; "verified"; "released" ]
                    "the small closed set the platform understands structurally"

                Expect.notEqual
                    (WorkRecordKind.Other "authored")
                    WorkRecordKind.Authored
                    "a foreign label that happens to read like a known kind is still a foreign kind"
            }
        ]

        // ── Unattested is recorded, never dropped ────────────────────

        testList "unattested is recorded with its reason" [
            testCaseAsync "no source composed — nothing is asked, and the reading says so"
            <| async {
                let record = recordWith filledProvenance
                let! reading = WorkProvenance.attest NoWorkProvenanceSource record

                Expect.equal
                    reading.Head
                    (WorkAttestation.Unattested WorkUnattestedReason.SourceAbsent)
                    "'nobody was asked' is a different fact from 'asked, and nothing was found'"

                Expect.equal reading.SourceSystem "" "no source system was consulted"

                Expect.equal
                    reading.UpstreamReference
                    (Some "sha256:upstream")
                    "the slot the reading was taken against is echoed either way"

                Expect.isFalse (DeployWorkAttestation.isAttested reading) "and it is not an attestation"
            }

            testCaseAsync "the default mode carries no implementation to consult"
            <| async {
                // "Registers nothing" is structural: the default case has
                // no source behind it, so there is nothing to start, wire
                // or call.
                Expect.isNone
                    (WorkProvenanceSource.ofMode NoWorkProvenanceSource)
                    "the default mode holds no source — nothing to register, nothing to pay for (GP 13)"

                let probe = sourceWith WorkProvenanceCaps.defaults
                let! _ = WorkProvenance.attest NoWorkProvenanceSource (recordWith filledProvenance)

                Expect.equal probe.CoverageCalls 0 "and an uncomposed deployment asks nothing of anybody"
                Expect.equal probe.Lookups 0 "no lookups either"
            }

            testCaseAsync "a deploy that recorded no upstream reference is unattested for that reason"
            <| async {
                let source = sourceWith WorkProvenanceCaps.defaults
                let mode = ComposedWorkProvenanceSource(source :> IWorkProvenanceSource)

                let! reading = WorkProvenance.attest mode (recordWith DeployProvenance.none)

                Expect.equal
                    reading.Head
                    (WorkAttestation.Unattested WorkUnattestedReason.UpstreamReferenceUnrecorded)
                    "there was nothing to look the work up by, and the reading says which gap this is"

                Expect.equal source.CoverageCalls 0 "so the source is not asked a question with no subject"
                Expect.equal reading.SourceSystem system "though the source consulted is still named"
            }

            testCaseAsync "sources no work record covers are reported unattested, never dropped"
            <| async {
                let cases: ((string -> Result<WorkCoverage, string>) * WorkUnattestedReason) list = [
                    (fun _ -> Result.Ok WorkCoverage.NotTracked), WorkUnattestedReason.NotTracked
                    (fun _ -> Result.Ok WorkCoverage.NoCoveringRecord), WorkUnattestedReason.NoCoveringRecord
                    (fun _ -> Result.Error "ledger unreachable"), WorkUnattestedReason.LookupFailed "ledger unreachable"
                ]

                for (coverage, expected) in cases do
                    let source =
                        FakeWorkSource(WorkProvenanceCaps.defaults, table, coverage) :> IWorkProvenanceSource

                    let! reading =
                        WorkProvenance.attest (ComposedWorkProvenanceSource source) (recordWith filledProvenance)

                    Expect.equal
                        reading.Head
                        (WorkAttestation.Unattested expected)
                        "the deploy is accounted for WITH its reason — an account listing only attested members reads as complete and is not"

                    Expect.stringContains
                        (WorkAttestation.describe reading.Head)
                        "unattested"
                        "and says so in the operator-facing text"

                // The five reasons are five different facts, and none of
                // them collapses into another.
                let reasons = [
                    WorkUnattestedReason.SourceAbsent
                    WorkUnattestedReason.UpstreamReferenceUnrecorded
                    WorkUnattestedReason.NotTracked
                    WorkUnattestedReason.NoCoveringRecord
                    WorkUnattestedReason.LookupFailed "boom"
                ]

                Expect.equal
                    (reasons
                     |> List.map WorkUnattestedReason.describe
                     |> List.distinct
                     |> List.length)
                    5
                    "each gap has its own account"
            }

            testCaseAsync "a covered deploy references the head work record, which is then walkable"
            <| async {
                let coverage (reference: string) : Result<WorkCoverage, string> =
                    if reference = "sha256:upstream" then
                        Result.Ok(WorkCoverage.Covered(ref' "w1"))
                    else
                        Result.Ok WorkCoverage.NotTracked

                let source = FakeWorkSource(WorkProvenanceCaps.defaults, table, coverage)
                let mode = ComposedWorkProvenanceSource(source :> IWorkProvenanceSource)
                let! attested = WorkProvenance.attestRecord mode (recordWith filledProvenance)

                Expect.isTrue (DeployWorkAttestation.isAttested attested.Work) "the deploy stands on recorded work"

                match WorkAttestation.head attested.Work.Head with
                | None -> failtest "expected a head reference"
                | Some head ->
                    Expect.equal head (ref' "w1") "the head record the source system named"

                    // The point of the whole phase: the head is a
                    // reference a reader can WALK, not a digest it can
                    // only compare.
                    let! chain =
                        (WorkProvenanceSource.bounded (source :> IWorkProvenanceSource)).GetAncestors {
                            Root = head
                            Depth = 3
                        }

                    match chain with
                    | Result.Ok page ->
                        Expect.equal
                            (page.Records |> List.map (fun r -> r.Ref.RecordId) |> List.sort)
                            [ "w1"; "w2"; "w3" ]
                            "and the chain behind it is traversable"
                    | other -> failtestf "expected a walkable chain from the head, got %A" other
            }
        ]

        // ── An uncomposed deployment is unchanged ────────────────────

        testList "an uncomposed deployment is unchanged" [
            test "the deploy record is not widened — the companion sits beside it" {
                // 656 records why: three more fields on the record would
                // retype its constructor, break every literal
                // construction, and invalidate every existing seal. This
                // arm fails if a later phase reaches for the obvious
                // shape instead.
                Expect.equal
                    (Microsoft.FSharp.Reflection.FSharpType.GetRecordFields typeof<DeployRecord>
                     |> Array.map _.Name
                     |> Array.toList
                     |> List.sort)
                    [ "BuildId"; "DeployId"; "Manifest"; "Provenance"; "SchemaVersion"; "TenantId" ]
                    "DeployRecord must stay exactly as 656 shipped it — the work companion embeds, never widens"
            }

            testCaseAsync "taking the reading leaves the record's canonical bytes untouched"
            <| async {
                let record = recordWith filledProvenance
                let before = DeployRecord.canonicalForm record

                let sealer = StubSealer("secret") :> IDeployRecordSealer
                let! sealed' = sealer.Seal(DeployRecords.canonicalBytes record)

                let seal =
                    match sealed' with
                    | Ok value -> value
                    | Error reason -> failtestf "the fixture failed to seal: %s" reason

                let! attested = WorkProvenance.attestRecord NoWorkProvenanceSource record

                Expect.equal
                    (DeployRecord.canonicalForm attested.Record)
                    before
                    "the reading is a reading — it does not rewrite the record it read"

                Expect.equal
                    (attested.Record.Provenance.UpstreamProvenanceDigest)
                    (Some "sha256:upstream")
                    "the opaque slot stays authoritative and unchanged"

                let! verified =
                    DeployRecords.verifySeal sealer {
                        Record = attested.Record
                        Seal = seal
                    }

                match verified with
                | Ok() -> ()
                | Error failures ->
                    failtestf
                        "a seal taken before the reading must still verify after it: %A"
                        (failures |> List.map DeployRecords.DeployRecordVerificationFailure.describe)
            }

            test "the identity reading records the honest reason rather than a blank" {
                Expect.equal
                    DeployWorkAttestation.none.Head
                    (WorkAttestation.Unattested WorkUnattestedReason.SourceAbsent)
                    "'nothing asked' is recorded as itself, never as a blank that reads as 'asked and clean'"

                Expect.isFalse (DeployWorkAttestation.isAttested DeployWorkAttestation.none) "and is not an attestation"

                Expect.stringContains
                    (DeployWorkAttestation.describe DeployWorkAttestation.none)
                    "no work provenance source was composed"
                    "the operator-facing text says which gap it is"
            }
        ]
    ]