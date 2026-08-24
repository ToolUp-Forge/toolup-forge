// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DeploymentVerificationReportTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DeploymentVerification
open ToolUp.Platform.AuditSinks.ChainedLedger

// ─── Phase 686 — the one-command deployment verification report ──────
//
// The report composes verifiers that already exist — five at Phase 686,
// six since Phase 693 added module seam authority. So this pack
// deliberately does NOT re-prove those verifiers — each has its own pack,
// and duplicating them here would produce a suite that goes green when
// the composition is wrong as long as the pieces are right. What it
// probes is the composition itself, and specifically the three claims a
// composed report makes that its pieces do not:
//
//   * **absence is not a pass.** The bare-deployment probe asserts every
//     section reads `NotComposed` AND that the run exits ZERO. Both halves
//     matter and they pull against each other: a report that reddened on
//     absence would be turned off in CI, and one that greened an absent
//     section would be worse than no report at all.
//
//   * **a seeded failure lands in the RIGHT section.** One probe per
//     section, each seeding a failure in exactly one substrate against a
//     deployment where the other four are healthy, then asserting that the
//     named section is adverse and **every other section is not**. A
//     report that failed globally on any failure would pass a weaker
//     version of this test and be useless to the assessor it is for, who
//     needs to know which evidence broke.
//
//   * **the two states a boolean would fold together stay apart.** An
//     issuance log behind no integrity gate, a ledger head that cannot be
//     judged, and an envelope declaring nothing are each read, each
//     non-affirmative, and each a DIFFERENT non-affirmative — probed by
//     asserting the verdict LABEL rather than a truthiness.
//
// The ledger half runs against a real `ChainedLedgerAuditSink` over real
// file storage, perturbed at the byte level — the shape the Phase 658 and
// 685 packs use — so the ledger section is answered by whatever survives
// the tamper rather than by an in-memory list the tamper never touched.

// ─── Harness ─────────────────────────────────────────────────────────

let private scope = "_platform"

/// An `IAuditLog` honouring both filters, so a probe cannot pass against
/// a read path that ignores them.
type private RecordingAuditLog() =
    let recorded = ConcurrentQueue<string * AuditEvent>()

    member _.All = recorded |> Seq.map snd |> List.ofSeq

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Enqueue((scopeId, audit)) }

        member _.GetAuditTrail(scopeId, _dateRange, eventType) = async {
            return
                recorded
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> Seq.filter (fun e ->
                    match eventType with
                    | None -> true
                    | Some wanted -> AuditEvent.eventTypeName e = wanted)
                |> List.ofSeq
                |> List.rev
        }

/// Phase 693 — a job handler that does nothing, so a probe module can
/// declare a job registration (and therefore imply `IJobScheduler`)
/// without a scheduler being involved.
type private NoopSeamJobHandler() =
    interface IJobHandler with
        member _.Execute _ = async { return JobResult.Success }

/// Phase 693 — an audit log the seam gate's deny observer can write to
/// without a probe asserting on it. The refusal path is Phase 688's and
/// is proved there; what the mapper probes is the mirror it produces.
type private SilentSeamAuditLog() =
    interface IAuditLog with
        member _.Record(_, _) = async { return () }
        member _.GetAuditTrail(_, _, _) = async { return [] }

/// A service provider over an explicit type→instance table. Small on
/// purpose: the report resolves exactly two services (the evidence and
/// the audit log) and a full container would hide which.
type private StubServices(entries: (Type * obj) list) =
    interface IServiceProvider with
        member _.GetService(serviceType) =
            entries
            |> List.tryFind (fun (t, _) -> t = serviceType)
            |> Option.map snd
            |> Option.defaultValue null

let private servicesWith (evidence: IDeploymentVerificationEvidence option) (auditLog: IAuditLog option) =
    StubServices(
        [
            match evidence with
            | Some e -> typeof<IDeploymentVerificationEvidence>, box e
            | None -> ()
            match auditLog with
            | Some a -> typeof<IAuditLog>, box a
            | None -> ()
        ]
    )
    :> IServiceProvider

let private sectionOf (report: DeploymentVerificationReport) (id: string) =
    report.Sections
    |> List.tryFind (fun s -> s.Id = id)
    |> Option.defaultWith (fun () -> failtestf "section '%s' is missing from the report" id)

let private labelOf (report: DeploymentVerificationReport) (id: string) =
    VerificationSectionVerdict.label (sectionOf report id).Verdict

let private allSectionIds = [
    BootSealSection
    GroundingContinuitySection
    AuditLedgerSection
    CertificateIssuanceSection
    AnswerJoinSection
    // Phase 693. Appended, matching `buildReport`'s own ordering — the
    // canonical form is order-sensitive, and a list here that disagreed
    // with the report's would make every ordered assertion below assert
    // something other than what it reads as.
    SeamAuthoritySection
]

// ─── Healthy sources ─────────────────────────────────────────────────

let private healthyBootSeal =
    BootSealVerified("verified", "refuse-on-drift", "the running composition matches the sealed one")

let private healthyContinuity = GroundingContinuous("seal-abc", 4, 2, "digest-live")

let private healthyLedger () = async { return Ok(LedgerChainVerified(12L, "head-digest", "valid (key-1 / Ed25519)")) }

let private healthyCertificates () = async {
    return
        Ok {
            Issued = 3
            Recent = [ "digest-1  subject-1  (detached-jws, key key-1)" ]
            LogIntegrityChecked = true
        }
}

let private healthyAnswerJoins () = async {
    return
        Ok {
            Rows = 7
            Rejoined = 7
            Mismatched = []
            Unanchored = 1
        }
}

/// A composition that declared seam sets AND checked them, with every
/// derived reach admitted — the one shape that earns `Verified` on the
/// Phase 693 section. Both halves are load-bearing and the probes below
/// remove each in turn.
let private healthySeamAuthority = {
    Profile = "verified"
    DeclarationMandatory = true
    Components = [
        {
            AuthorityComponent = ComponentId.ofModule "reference-service"
            DeclaredGrant = SeamGrant.ofInterfaces [ "IEntityStore"; "IAuditSink" ]
            DerivedReach = [ SeamId.ofInterface "IEntityStore"; SeamId.ofInterface "IAuditSink" ]
            ComposedHere = true
        }
        {
            AuthorityComponent = ComponentId.ofModule "reader"
            DeclaredGrant = SeamGrant.ofInterfaces [ "IEntityStore" ]
            DerivedReach = [ SeamId.ofInterface "IEntityStore" ]
            ComposedHere = true
        }
    ]
    Verification = SeamAuthorityAdmitted(2, 3)
}

/// A deployment composing every substrate, all healthy.
let private fullEvidence
    (bootSeal: BootSealIntegrity option)
    (continuity: GroundingContinuityIntegrity option)
    (ledger: (unit -> Async<Result<LedgerIntegrity, string>>) option)
    (certificates: (unit -> Async<Result<CertificateIssuanceIntegrity, string>>) option)
    (answerJoins: (unit -> Async<Result<AnswerJoinIntegrity, string>>) option)
    =
    DeploymentVerificationEvidence.create
        (Some(defaultArg bootSeal healthyBootSeal))
        (Some(defaultArg continuity healthyContinuity))
        (Some(defaultArg ledger healthyLedger))
        (Some(defaultArg certificates healthyCertificates))
        (Some(defaultArg answerJoins healthyAnswerJoins))
    |> DeploymentVerificationEvidence.withSeamAuthority (Some healthySeamAuthority)

/// The same composition with the seam-authority member replaced.
let private withSeam (seam: SeamAuthorityIntegrity) =
    fullEvidence None None None None None
    |> DeploymentVerificationEvidence.withSeamAuthority (Some seam)

let private healthyEvidence () = fullEvidence None None None None None

let private runReport (evidence: IDeploymentVerificationEvidence) (auditLog: IAuditLog option) =
    DeploymentVerificationReport.run (servicesWith (Some evidence) auditLog) "probe"
    |> Async.RunSynchronously

/// Assert that exactly `expected` is adverse and every other section is
/// not. The load-bearing half is the second: a report that reddened
/// everywhere would satisfy the first on its own.
let private onlyAdverse (report: DeploymentVerificationReport) (expected: string) =
    let adverse =
        report.Sections
        |> List.filter (fun s -> VerificationSectionVerdict.isAdverse s.Verdict)
        |> List.map _.Id

    Expect.equal adverse [ expected ] "exactly the seeded section is adverse"

    Expect.equal
        report.Outcome
        DeploymentVerificationOutcome.FailuresPresent
        "an adverse section drives the outcome to FailuresPresent"

    Expect.equal (exitCode report) 1 "an adverse section exits non-zero"

// ─── Real chained ledger, for the ledger probes ──────────────────────

let private ledgerSettings: ChainedLedgerSettings = {
    Container = "audit-ledger"
    PathPrefix = Some "verification"
}

let private newLedgerStorage () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-deployment-verification", Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

/// Append a handful of real audit rows through a real chained sink.
let private seedLedger (storage: IBlobStorage) = async {
    let sink = create "verification-ledger" ledgerSettings storage

    let batch =
        [ 0..4 ]
        |> List.map (fun i ->
            AuditEnvelope.fromScopeId
                scope
                (DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc).AddSeconds(float i))
                (DeploymentVerified {
                    Actor = sprintf "seed-%d" i
                    Outcome = "all-composed-verified"
                    VerdictDigest = sprintf "digest-%d" i
                    Sections = []
                    ExitCode = 0
                    OccurredAt = DateTimeOffset(DateTime(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc))
                }))

    match! sink.Deliver batch with
    | Ok() -> return ()
    | Error message -> return failwithf "ledger seed failed: %s" message
}

let private segmentName (storage: IBlobStorage) = async {
    let! names = storage.List(ledgerSettings.Container, "verification/records/")

    return
        names
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> List.exactlyOne
}

/// Drop one record from the middle of the stored segment — the byte-level
/// perturbation the Phase 658 pack uses, so the ledger section is
/// answered by what survives rather than by an in-memory copy.
let private dropMiddleRecord (storage: IBlobStorage) = async {
    let! name = segmentName storage

    match! storage.Download(ledgerSettings.Container, name) with
    | Error message -> return failwithf "segment read failed: %s" message
    | Ok bytes ->
        let lines =
            Encoding.UTF8.GetString bytes
            |> fun text -> text.Split '\n'
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.toList

        let survivors =
            lines
            |> List.mapi (fun i l -> i, l)
            |> List.filter (fun (i, _) -> i <> 2)
            |> List.map snd

        match! storage.Upload(ledgerSettings.Container, name, Encoding.UTF8.GetBytes(String.Join("\n", survivors))) with
        | Ok _ -> return ()
        | Error message -> return failwithf "segment write failed: %s" message
}

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 686 — deployment verification report" [

        // ── Probe 1: the full deployment ──────────────────────────────

        testList "a deployment composing every substrate" [
            test "reports every section affirmative and exits zero" {
                let report = runReport (healthyEvidence ()) None

                Expect.equal
                    (allSectionIds |> List.map (labelOf report))
                    [ "verified"; "verified"; "verified"; "verified"; "verified"; "verified" ]
                    "every composed and healthy section verifies"

                Expect.equal
                    report.Outcome
                    DeploymentVerificationOutcome.AllComposedVerified
                    "the outcome is all-composed-verified"

                Expect.equal (exitCode report) 0 "a healthy deployment exits zero"
            }

            test "still states what it does not prove" {
                let report = runReport (healthyEvidence ()) None

                Expect.isGreaterThan report.NotProved.Length 0 "the not-proved statements are always present"

                // The whole point of the artefact: a fully-verified report
                // still carries its bounds. A report that dropped them
                // when everything passed would be at its most misleading
                // exactly when it is most likely to be quoted.
                let postBoot = report.NotProved |> List.find (fun s -> s.Id = "post-boot-mutation")

                Expect.isSome postBoot.Narrowing "composing the grounding seal NARROWS the post-boot caveat"

                Expect.isTrue
                    (postBoot.Statement.Length > 0)
                    "narrowing does not delete the statement — the bound is smaller, not gone"
            }

            test "records one audited-read row carrying the verdict digest" {
                let audit = RecordingAuditLog()
                let report = runReport (healthyEvidence ()) (Some(audit :> IAuditLog))

                match audit.All with
                | [ DeploymentVerified payload ] ->
                    Expect.equal payload.Actor "probe" "the row names who ran the report"
                    Expect.equal payload.VerdictDigest report.VerdictDigest "the row commits to the report's digest"
                    Expect.equal payload.ExitCode 0 "the row carries the exit code the run would return"

                    Expect.equal
                        payload.Sections
                        (allSectionIds |> List.map (fun id -> sprintf "%s=verified" id))
                        "the row carries one id=label entry per section"
                | other -> failtestf "expected exactly one DeploymentVerified row, got %A" other
            }

            test "the row carries the digest and not the detail" {
                // The reason the row is safe to keep. A regression that
                // widened it into a second copy of the report would move
                // a deployment-wide evidence summary onto a surface with
                // its own readers and its own export paths.
                let audit = RecordingAuditLog()
                runReport (healthyEvidence ()) (Some(audit :> IAuditLog)) |> ignore

                match audit.All with
                | [ DeploymentVerified payload ] ->
                    let flattened = String.Join(" ", payload.Sections)

                    Expect.isFalse (flattened.Contains "head-digest") "no section detail reaches the audit row"
                | other -> failtestf "expected exactly one DeploymentVerified row, got %A" other
            }

            test "the verdict digest is stable across runs and moves with a verdict" {
                let first = runReport (healthyEvidence ()) None
                let second = runReport (healthyEvidence ()) None

                Expect.equal
                    first.VerdictDigest
                    second.VerdictDigest
                    "two runs against an unchanged deployment digest identically — the clock is excluded"

                let moved =
                    runReport
                        (fullEvidence
                            (Some(BootSealUnsealed("standard", "log-and-serve", "no seal")))
                            None
                            None
                            None
                            None)
                        None

                Expect.notEqual first.VerdictDigest moved.VerdictDigest "a changed section verdict changes the digest"
            }
        ]

        // ── Probe 2: the bare deployment ──────────────────────────────

        testList "a bare deployment composing nothing" [
            test "reads every section as absent and exits ZERO" {
                let report =
                    DeploymentVerificationReport.run (servicesWith None None) "probe"
                    |> Async.RunSynchronously

                Expect.equal
                    (allSectionIds |> List.map (labelOf report))
                    [
                        "not-composed"
                        "not-composed"
                        "not-composed"
                        "not-composed"
                        "not-composed"
                        "not-composed"
                    ]
                    "an unregistered evidence seam degrades to absent sections throughout, not an error"

                Expect.equal
                    report.Outcome
                    DeploymentVerificationOutcome.NothingComposed
                    "the outcome names the emptiness rather than claiming a pass"

                // Both halves of the contract, and they pull against each
                // other. A report that reddened here would be switched
                // off in CI; one that greened the SECTIONS would be worse
                // than having no report.
                Expect.equal (exitCode report) 0 "absence is not a failure"

                Expect.isFalse
                    (report.Sections
                     |> List.exists (fun s -> VerificationSectionVerdict.isAffirmative s.Verdict))
                    "and absence is emphatically not a pass"
            }

            test "each absent section names what would have to be composed" {
                let report =
                    DeploymentVerificationReport.run (servicesWith None None) "probe"
                    |> Async.RunSynchronously

                for id in allSectionIds do
                    let detail = VerificationSectionVerdict.detail (sectionOf report id).Verdict

                    Expect.isGreaterThan
                        detail.Length
                        20
                        (sprintf "section '%s' explains its absence rather than stating it" id)
            }

            test "the not-proved statements stand un-narrowed" {
                let report =
                    DeploymentVerificationReport.run (servicesWith None None) "probe"
                    |> Async.RunSynchronously

                Expect.isTrue
                    (report.NotProved |> List.forall (fun s -> s.Narrowing.IsNone))
                    "with nothing composed, nothing narrows any bound"
            }

            test "a partially-composed deployment is neither empty nor fully verified" {
                let evidence =
                    DeploymentVerificationEvidence.create (Some healthyBootSeal) None None None None

                let report = runReport evidence None

                Expect.equal (labelOf report BootSealSection) "verified" "the composed section verifies"
                Expect.equal (labelOf report AuditLedgerSection) "not-composed" "the absent sections stay absent"

                Expect.equal
                    report.Outcome
                    DeploymentVerificationOutcome.AllComposedVerified
                    "'all COMPOSED verified' says nothing about the five that were not"

                Expect.equal (exitCode report) 0 "a partial deployment with no failure exits zero"
            }
        ]

        // ── Probe 3: one seeded failure per section ───────────────────

        testList "a seeded failure lands in its own section" [
            test "boot seal — drift" {
                let evidence =
                    fullEvidence
                        (Some(
                            BootSealRejected(
                                "verified",
                                "refuse-on-drift",
                                "the running composition drifted from the sealed one",
                                [ "component 'X' is present and was not sealed" ],
                                true
                            )
                        ))
                        None
                        None
                        None
                        None

                let report = runReport evidence None
                onlyAdverse report BootSealSection

                Expect.equal (labelOf report BootSealSection) "failed" "a drifted boot verdict fails its section"

                Expect.isNonEmpty
                    (sectionOf report BootSealSection).Findings
                    "the drift findings ride through to the report"
            }

            test "grounding continuity — divergence" {
                let evidence =
                    fullEvidence
                        None
                        (Some(GroundingDiverged("seal-abc", 4, "grounding continuity broke at position 2")))
                        None
                        None
                        None

                let report = runReport evidence None
                onlyAdverse report GroundingContinuitySection

                Expect.stringContains
                    (VerificationSectionVerdict.detail (sectionOf report GroundingContinuitySection).Verdict)
                    "position 2"
                    "the source verdict's own positioned account survives into the section"
            }

            test "audit ledger — a real dropped record, positioned" {
                let storage = newLedgerStorage ()
                seedLedger storage |> Async.RunSynchronously
                dropMiddleRecord storage |> Async.RunSynchronously

                let source = deploymentVerificationSource ledgerSettings storage None
                let evidence = fullEvidence None None (Some source) None None
                let report = runReport evidence None

                onlyAdverse report AuditLedgerSection

                Expect.equal (labelOf report AuditLedgerSection) "failed" "a broken chain fails the ledger section"

                Expect.stringContains
                    (VerificationSectionVerdict.detail (sectionOf report AuditLedgerSection).Verdict)
                    "position"
                    "the section names the position the chain broke at"
            }

            test "certificate issuance — the log refuses to verify" {
                let source () = async { return Error "the backing chain breaks at position 2" }
                let evidence = fullEvidence None None None (Some source) None
                let report = runReport evidence None

                onlyAdverse report CertificateIssuanceSection

                // The Phase 685 discipline at report scope. If a refusing
                // log read as "issued nothing", breaking your own ledger
                // would be the cheapest way to answer an inconvenient
                // question — and it would exit zero doing it.
                Expect.equal
                    (labelOf report CertificateIssuanceSection)
                    "unreadable"
                    "a log that will not verify is unreadable, never 'issued nothing'"
            }

            test "answer-verification join — a head that does not recompute" {
                let source () = async {
                    return
                        Ok {
                            Rows = 7
                            Rejoined = 6
                            Mismatched = [ "task 1234: the row records provenance head 'a' and recomputes to 'b'" ]
                            Unanchored = 0
                        }
                }

                let evidence = fullEvidence None None None None (Some source)
                let report = runReport evidence None

                onlyAdverse report AnswerJoinSection

                Expect.isNonEmpty
                    (sectionOf report AnswerJoinSection).Findings
                    "the mismatched rows are enumerated, not merely counted"
            }

            test "a gatherer whose source raises reports unreadable rather than taking the report down" {
                let source () : Async<Result<LedgerIntegrity, string>> = async { return failwith "storage unreachable" }

                let evidence = fullEvidence None None (Some source) None None
                let report = runReport evidence None

                onlyAdverse report AuditLedgerSection

                Expect.equal
                    (labelOf report AuditLedgerSection)
                    "unreadable"
                    "a raising source is contained in its own section"
            }
        ]

        // ── The distinctions a boolean would fold away ─────────────────

        testList "read-but-unaffirmed sections stay distinct from verified ones" [
            test "an issuance log behind no integrity gate is observed, not verified" {
                let source () = async {
                    return
                        Ok {
                            Issued = 3
                            Recent = []
                            LogIntegrityChecked = false
                        }
                }

                let report = runReport (fullEvidence None None None (Some source) None) None

                Expect.equal
                    (labelOf report CertificateIssuanceSection)
                    "observed"
                    "enumeration with no integrity gate is the deployment's own assertion"

                Expect.equal
                    report.Outcome
                    DeploymentVerificationOutcome.PartiallyVerified
                    "and it depresses the outcome below all-composed-verified"

                Expect.equal (exitCode report) 0 "while remaining a zero-exit state — nothing failed"
            }

            test "an envelope declaring nothing is observed, not verified" {
                let evidence =
                    fullEvidence None (Some(GroundingContinuous("seal-abc", 0, 0, "digest-empty"))) None None None

                let report = runReport evidence None

                Expect.equal
                    (labelOf report GroundingContinuitySection)
                    "observed"
                    "continuity over an empty envelope holds trivially and verifies nothing"
            }

            test "a ledger head that cannot be judged is unreadable, not verified" {
                let source () = async {
                    return Ok(LedgerHeadUnverifiable(9L, "head", "head is signed but no verifier was supplied"))
                }

                let report = runReport (fullEvidence None None (Some source) None None) None

                // The cheapest attack on this section would be to withhold
                // the verifier. It exits non-zero instead.
                Expect.equal
                    (labelOf report AuditLedgerSection)
                    "unreadable"
                    "withholding the verifier does not buy a pass"

                Expect.equal (exitCode report) 1 "and does not buy a zero exit either"
            }

            test "a ledger head whose signature is invalid is failed, not merely unreadable" {
                let source () = async { return Ok(LedgerHeadRejected(9L, "head", "INVALID (key-1 / Ed25519)")) }

                let report = runReport (fullEvidence None None (Some source) None None) None

                Expect.equal
                    (labelOf report AuditLedgerSection)
                    "failed"
                    "a signature that is present and does not verify is a finding, not an incomplete read"
            }

            test "an empty ledger is observed, not verified" {
                let source () = async { return Ok(LedgerChainVerified(0L, "genesis", "unsigned")) }
                let report = runReport (fullEvidence None None (Some source) None None) None

                Expect.equal
                    (labelOf report AuditLedgerSection)
                    "observed"
                    "a composed ledger holding nothing has verified nothing"
            }

            test "an unsealed boot verdict is observed, not verified and not failed" {
                let evidence =
                    fullEvidence
                        (Some(BootSealUnsealed("standard", "log-and-serve", "started without a sealed deploy record")))
                        None
                        None
                        None
                        None

                let report = runReport evidence None

                Expect.equal (labelOf report BootSealSection) "observed" "an unsealed start is honest, not adverse"
                Expect.equal (exitCode report) 0 "and does not redden CI"
            }
        ]

        // ── The pure folds ────────────────────────────────────────────

        testList "the outcome and exit-code folds" [
            test "a NotComposed section neither inflates nor depresses the outcome" {
                let sections = [
                    {
                        Id = "a"
                        Title = "a"
                        Verdict = VerificationSectionVerdict.Verified "ok"
                        Findings = []
                    }
                    {
                        Id = "b"
                        Title = "b"
                        Verdict = VerificationSectionVerdict.NotComposed "absent"
                        Findings = []
                    }
                ]

                Expect.equal
                    (outcomeOf sections)
                    DeploymentVerificationOutcome.AllComposedVerified
                    "absence is not evidence in either direction"
            }

            test "adverse beats observed beats verified" {
                let mk verdict = {
                    Id = "x"
                    Title = "x"
                    Verdict = verdict
                    Findings = []
                }

                Expect.equal
                    (outcomeOf [
                        mk (VerificationSectionVerdict.Verified "ok")
                        mk (VerificationSectionVerdict.Observed "meh")
                    ])
                    DeploymentVerificationOutcome.PartiallyVerified
                    "one observed section is enough to withhold all-composed-verified"

                Expect.equal
                    (outcomeOf [
                        mk (VerificationSectionVerdict.Observed "meh")
                        mk (VerificationSectionVerdict.Failed "bad")
                    ])
                    DeploymentVerificationOutcome.FailuresPresent
                    "one failure dominates everything else"
            }

            test "the empty section list is NothingComposed rather than a vacuous pass" {
                Expect.equal
                    (outcomeOf [])
                    DeploymentVerificationOutcome.NothingComposed
                    "nothing to report is never 'all verified'"
            }

            test "the canonical form is injective over free-text detail" {
                // A delimiter-only canonical form would let two different
                // verdict sets frame to identical bytes, and the digest
                // would then commit to neither.
                let mk id detail = {
                    Id = id
                    Title = "t"
                    Verdict = VerificationSectionVerdict.Verified detail
                    Findings = []
                }

                Expect.notEqual
                    (canonicalForm [ mk "a" "x"; mk "b" "y" ] [])
                    (canonicalForm [ mk "a" "xb"; mk "" "y" ] [])
                    "length-prefixed fields keep two different section sets distinct"
            }

            test "the canonical form frames with LF regardless of platform" {
                // `AppendLine` would emit `Environment.NewLine`, so the same
                // report would frame to different bytes on Windows and Linux
                // and the digest would stop being a property of the report.
                // A digest that depends on where it was computed cannot be
                // recomputed by an auditor, which is the whole point of it.
                let form =
                    canonicalForm [
                        {
                            Id = "a"
                            Title = "a"
                            Verdict = VerificationSectionVerdict.Verified "ok"
                            Findings = []
                        }
                    ] [
                        {
                            Id = "n"
                            Statement = "s"
                            Narrowing = None
                        }
                    ]

                Expect.isFalse (form.Contains '\r') "no carriage return reaches the digested bytes"
                Expect.stringContains form "\n" "and the framing newline is present"
            }

            test "the canonical form excludes the findings, the clock and the actor" {
                let withFindings = {
                    Id = "a"
                    Title = "a"
                    Verdict = VerificationSectionVerdict.Verified "ok"
                    Findings = [ "one"; "two" ]
                }

                Expect.equal
                    (canonicalForm [ withFindings ] [])
                    (canonicalForm [ { withFindings with Findings = [] } ] [])
                    "the digest names the verdict SET, so enumeration detail does not move it"
            }
        ]

        // ── The CLI surface ───────────────────────────────────────────

        testList "the CI entry point" [
            test "the flag is detected and does not disturb the existing modes" {
                Expect.equal
                    (StartupModes.detect [| "app.exe"; "--verify-deployment" |])
                    StartupModes.VerifyDeployment
                    "the flag selects the verification mode"

                Expect.equal
                    (StartupModes.detect [| "app.exe" |])
                    StartupModes.NormalBoot
                    "an ordinary argv still boots normally"

                Expect.equal
                    (StartupModes.detect [| "app.exe"; "--verify-deployment"; "--print-config" |])
                    StartupModes.PrintConfig
                    "the never-fails action still wins a confused invocation"

                Expect.equal
                    (StartupModes.detect [| "app.exe"; "--verify-deployment"; "--validate-config" |])
                    StartupModes.ValidateConfig
                    "and validate-config still precedes verification"
            }

            test "the rendered report names every section and every bound" {
                let report = runReport (healthyEvidence ()) None
                let rendered = render report

                for id in allSectionIds do
                    Expect.stringContains rendered (sectionOf report id).Title (sprintf "section '%s' is rendered" id)

                Expect.stringContains
                    rendered
                    "What this report does NOT prove"
                    "the bounds are rendered, not only carried"

                Expect.stringContains rendered report.VerdictDigest "the digest is quotable from the rendered form"
            }
        ]

        // ── The composition-root mappers ──────────────────────────────

        testList "the boot-verdict mapper" [
            let resultWith verdict : BootVerificationResult = {
                Verdict = verdict
                Profile = CompositionProfile.Verified
                Policy = BootVerificationPolicy.RefuseOnDrift
                RefusedStart = false
            }

            test "maps an affirmative verdict to a verified seal" {
                match ServerApp.bootSealEvidence (Ok(resultWith BootVerificationVerdict.Verified)) with
                | BootSealVerified _ -> ()
                | other -> failtestf "expected BootSealVerified, got %A" other
            }

            test "reads the REFUSED arm rather than discarding it" {
                // `Error` means the policy refused the start, not that the
                // check produced nothing. A mapper that dropped this arm
                // would omit the report's most important section exactly
                // when it matters most.
                let refused = {
                    resultWith (BootVerificationVerdict.Drifted []) with
                        RefusedStart = true
                }

                match ServerApp.bootSealEvidence (Error refused) with
                | BootSealRejected(_, _, _, _, refusedStart) ->
                    Expect.isTrue refusedStart "the refusal rides through to the report"
                | other -> failtestf "expected BootSealRejected, got %A" other
            }

            test "Phase 694 — keeps a partly-uncomparable verdict separate from a plain verification" {
                // Folding `VerifiedUnrecorded` into `BootSealVerified`
                // would restate one tier up the very blind spot Phase 694
                // closed: the report would say "verified" about a binding
                // that could not speak to the canonical-method selectors.
                let verdict =
                    BootVerificationVerdict.VerifiedUnrecorded [
                        CanonicalMethodUnrecorded("revenue", Some "computed:rollup:2")
                    ]

                let evidence = ServerApp.bootSealEvidence (Ok(resultWith verdict))

                match evidence with
                | BootSealVerifiedUnrecorded(_, _, _, unrecorded) ->
                    Expect.stringContains
                        (unrecorded |> String.concat " | ")
                        "revenue"
                        "each declaration that could not be compared rides through by name"
                | other -> failtestf "expected BootSealVerifiedUnrecorded, got %A" other

                let section =
                    DeploymentVerificationReport.gatherBootSeal (fullEvidence (Some evidence) None None None None)

                Expect.equal
                    (VerificationSectionVerdict.label section.Verdict)
                    "observed"
                    "composed, read, and part of its check not performed — neither a pass nor a failure"

                Expect.isFalse
                    (VerificationSectionVerdict.isAdverse section.Verdict)
                    "and not adverse: an upgrade that turned every sealed deployment's report red would be worse than the blind spot it closed"

                Expect.isNonEmpty section.Findings "the gap is visible, so its one-act remedy is legible"
            }

            test "keeps unsealed separate from rejected" {
                match ServerApp.bootSealEvidence (Ok(resultWith (BootVerificationVerdict.Unsealed "no record"))) with
                | BootSealUnsealed(_, _, reason) -> Expect.stringContains reason "no record" "the reason survives"
                | other -> failtestf "expected BootSealUnsealed, got %A" other
            }
        ]

        testList "the ledger mapper" [
            test "an unverifiable head and an invalid one map to different cases" {
                let unverifiable =
                    toLedgerIntegrity (
                        LedgerHeadUntrusted(3L, "head", HeadSignatureUnverifiable("Ed25519", "no verifier supplied"))
                    )

                let invalid =
                    toLedgerIntegrity (LedgerHeadUntrusted(3L, "head", HeadSignatureInvalid("key-1", "Ed25519")))

                match unverifiable, invalid with
                | LedgerHeadUnverifiable _, LedgerHeadRejected _ -> ()
                | u, i -> failtestf "expected Unverifiable / Rejected, got %A / %A" u i
            }

            test "a clean verified head maps through with its record count" {
                match toLedgerIntegrity (LedgerVerified(11L, "head", HeadUnsigned)) with
                | LedgerChainVerified(records, digest, signature) ->
                    Expect.equal records 11L "the record count survives"
                    Expect.equal digest "head" "the head digest survives"
                    Expect.stringContains signature "unsigned" "an unsigned head says so rather than reading as valid"
                | other -> failtestf "expected LedgerChainVerified, got %A" other
            }
        ]

        testList "the answer-join re-derivation" [
            let payloadWith citedFactIds head : AnswerVerificationPayload = {
                TaskId = Guid.NewGuid()
                ConversationId = Guid.NewGuid()
                Mode = "Annotate"
                Verified = 1
                Unmatched = 0
                Unverifiable = 0
                FactsInScope = 1
                Tokens = []
                CitedFactIds = citedFactIds
                ProvenanceChainHead = head
                CertificateRef = None
                CompositionSealId = None
                ProviderName = "p"
                ProviderModel = "m"
                OccurredAt = DateTimeOffset.UtcNow
            }

            test "a row whose head derives from its own ids rejoins" {
                let ids = [ "fact-a"; "fact-b" ]
                let head = ToolUp.AI.AnswerVerifier.provenanceChainHead ids

                Expect.isOk
                    (ToolUp.AI.AnswerVerifier.rejoinAnswerVerification (payloadWith ids head))
                    "the recorded head recomputes from the ids on the same row"
            }

            test "a row whose head was written by something else does not" {
                Expect.isError
                    (ToolUp.AI.AnswerVerifier.rejoinAnswerVerification (
                        payloadWith
                            [ "fact-a" ]
                            (Some "0000000000000000000000000000000000000000000000000000000000000000")
                    ))
                    "a head that does not recompute is a finding"
            }

            test "an answer citing no fact and naming no head is honest, not a failure" {
                Expect.isOk
                    (ToolUp.AI.AnswerVerifier.rejoinAnswerVerification (payloadWith [] None))
                    "no facts in scope means no chain head, and that is the correct record"
            }

            test "a null cited-id list from a pre-field row does not fault the walk" {
                // The STJ additive-field hazard: a row persisted before the
                // field existed deserialises its list as null, and a null
                // F# list faults on every list operation.
                Expect.isOk
                    (ToolUp.AI.AnswerVerifier.rejoinAnswerVerification (
                        payloadWith (Unchecked.defaultof<string list>) None
                    ))
                    "a null list coerces to empty rather than taking the section down"
            }
        ]

        // ── Phase 693: the seam-authority section ─────────────────────
        //
        // Three claims, and the middle one is what the phase is for.
        //
        //   * **the seeded drift lands here and nowhere else.** A
        //     component reaching past its declaration reddens exactly the
        //     seam section against a deployment whose other five are
        //     healthy, and the exit-code contract is unchanged.
        //   * **declaring is not enforcing, and enforcing nothing is not
        //     enforcement.** Phase 691 shipped the gate's production call
        //     site, but invoking it is per-deployment — so a composition
        //     that declared grants and never checked them, and one that
        //     checked a composition declaring nothing, are both READ and
        //     both non-affirmative, in wording that says which. Probed by
        //     the verdict LABEL, so a truthiness fold cannot pass them.
        //   * **the mirror is derived from a real composition.** The
        //     adapter runs over genuine `ServerModule` values and its
        //     roster and counts are recomputed from `reachOf`, so a root
        //     cannot overstate its coverage by passing a flattering
        //     number and the section cannot drift from Phase 438's
        //     `Needs` projection.

        testList "the seam-authority section" [
            let seamLabel report = labelOf report SeamAuthoritySection

            let seamDetail report =
                VerificationSectionVerdict.detail (sectionOf report SeamAuthoritySection).Verdict

            test "a declared-and-admitted composition verifies" {
                let report = runReport (healthyEvidence ()) None

                Expect.equal
                    (seamLabel report)
                    "verified"
                    "the check ran, grants were declared, every reach was admitted"

                Expect.stringContains
                    (seamDetail report)
                    "profile verified"
                    "the verdict names the profile binding it was checked under"

                Expect.stringContains
                    (seamDetail report)
                    "seam declaration mandatory"
                    "and whether declaring a seam set was mandatory or advisory"
            }

            test "the section enumerates each component's declared set beside its derived reach" {
                let findings =
                    (sectionOf (runReport (healthyEvidence ()) None) SeamAuthoritySection).Findings

                Expect.hasLength findings 2 "one line per component in the composition"

                Expect.stringContains
                    (List.head findings)
                    "declared declared{IAuditSink,IEntityStore}"
                    "the declared set is rendered in the Phase 688 vocabulary"

                Expect.stringContains
                    (List.head findings)
                    "reaches {IAuditSink,IEntityStore}"
                    "beside the reach the registrations imply — the gap between them is the review surface"
            }

            test "a component reaching past its declaration reddens this section and no other" {
                let refused = {
                    healthySeamAuthority with
                        Verification =
                            SeamAuthorityRefused(
                                "1 reach(es) were refused: a component reached a seam it did not declare",
                                [
                                    "component 'reader' may not resolve seam 'ISecretStore': it declared {IEntityStore}"
                                ]
                            )
                }

                let report = runReport (withSeam refused) None

                onlyAdverse report SeamAuthoritySection

                Expect.stringContains
                    (String.Join(" ", (sectionOf report SeamAuthoritySection).Findings))
                    "ISecretStore"
                    "the refusal enumerates the seam that was reached, not just a count"
            }

            test "a profile that could not be bound is a failure, not an absence" {
                // The state a composition reaches by declaring the
                // verified profile and supplying no grants. Reading it as
                // NotComposed would let a deployment answer a mandatory
                // check by withholding its input.
                let refused = {
                    healthySeamAuthority with
                        Components = []
                        Verification =
                            SeamAuthorityRefused(
                                "the composition could not be bound to the profile it declared, so no reach was checked",
                                [ "the verified composition profile requires a SeamGrantSignature" ]
                            )
                }

                onlyAdverse (runReport (withSeam refused) None) SeamAuthoritySection
            }

            test "grants declared and never checked are OBSERVED, not verified" {
                // The claim the phase exists to keep honest. The SDK's
                // enforcement is real; this deployment does not call it,
                // so the declarations bound nothing here.
                let unenforced = {
                    healthySeamAuthority with
                        Verification = SeamAuthorityUnenforced
                }

                let report = runReport (withSeam unenforced) None

                Expect.equal (seamLabel report) "observed" "declaring is not enforcing"

                Expect.stringContains
                    (seamDetail report)
                    "routes through the seam gate"
                    "and the verdict says which half is missing rather than implying the bound holds"

                Expect.equal (exitCode report) 0 "an unenforced declaration is not adverse — nothing failed"

                Expect.isFalse
                    (VerificationSectionVerdict.isAffirmative (sectionOf report SeamAuthoritySection).Verdict)
                    "and emphatically not a pass"
            }

            test "a check that admitted an all-unrestricted composition is OBSERVED, not verified" {
                // The Phase 688 additive floor. Every component resolves
                // to UnrestrictedSeams, so the gate could not have
                // refused anything — crediting that as a verification
                // would credit a check that had nothing to check.
                let floor = {
                    healthySeamAuthority with
                        Profile = "standard"
                        DeclarationMandatory = false
                        Components =
                            healthySeamAuthority.Components
                            |> List.map (fun entry -> {
                                entry with
                                    DeclaredGrant = UnrestrictedSeams
                            })
                }

                let report = runReport (withSeam floor) None

                Expect.equal (seamLabel report) "observed" "an admission over an undeclared composition is structural"

                Expect.stringContains
                    (seamDetail report)
                    "additive floor"
                    "and the verdict names why it is not a confinement result"

                Expect.stringContains
                    (seamDetail report)
                    "seam declaration advisory"
                    "the advisory posture is reported as such under the standard profile"
            }

            test "a composition that declared nothing and checked nothing says both" {
                let bare = {
                    healthySeamAuthority with
                        Components = []
                        Verification = SeamAuthorityUnenforced
                }

                let report = runReport (withSeam bare) None

                Expect.equal (seamLabel report) "observed" "composed, read, and with nothing to affirm"

                Expect.stringContains
                    (seamDetail report)
                    "whatever the container will hand it"
                    "the honest reading of a composition that bounds nothing"
            }

            test "a deployment supplying no seam evidence reads absent, and the section stays optional" {
                // The five-source evidence value predates Phase 693 and
                // carries no seam member. It must still build a report,
                // and its sixth section must read as the deployment's own
                // boundary rather than as an error.
                let preExisting =
                    DeploymentVerificationEvidence.create
                        (Some healthyBootSeal)
                        (Some healthyContinuity)
                        (Some healthyLedger)
                        (Some healthyCertificates)
                        (Some healthyAnswerJoins)

                let report = runReport preExisting None

                Expect.equal
                    (seamLabel report)
                    "not-composed"
                    "an evidence value that says nothing about seams is absent, not broken"

                Expect.equal (exitCode report) 0 "and absence keeps the exit-code contract"

                Expect.isTrue
                    (report.NotProved
                     |> List.find (fun st -> st.Id = "seam-reach-is-a-subset-claim")
                     |> _.Narrowing
                     |> Option.isNone)
                    "with nothing composed the subset-claim bound stands whole"
            }

            test "withGroundingContinuity carries the seam member through" {
                // The default path, not an edge case:
                // `ServerApp.withDeploymentVerificationEvidence` calls
                // this wither on EVERY registration to derive the
                // continuity section from the container. A wither that
                // rebuilt the value without the member it does not name
                // would delete the sixth section for every root that
                // supplies both — silently, and by default.
                let report =
                    healthyEvidence ()
                    |> DeploymentVerificationEvidence.withGroundingContinuity (Some healthyContinuity)
                    |> fun evidence -> runReport evidence None

                Expect.equal (seamLabel report) "verified" "replacing one member must not drop another"
            }

            test "withSeamAuthority carries the other five members through" {
                let report =
                    healthyEvidence ()
                    |> DeploymentVerificationEvidence.withSeamAuthority (Some healthySeamAuthority)
                    |> fun evidence -> runReport evidence None

                Expect.equal
                    (allSectionIds |> List.map (labelOf report))
                    [ "verified"; "verified"; "verified"; "verified"; "verified"; "verified" ]
                    "the seam wither replaces exactly one member and preserves the rest"
            }

            test "composing the section narrows the subset-claim statement without deleting it" {
                let statement =
                    (runReport (healthyEvidence ()) None).NotProved
                    |> List.find (fun st -> st.Id = "seam-reach-is-a-subset-claim")

                Expect.isSome statement.Narrowing "a composed section narrows the bound"

                Expect.isGreaterThan
                    statement.Statement.Length
                    0
                    "narrowing shrinks the bound, it never removes the statement"
            }

            test "a grant declared for a component this deployment does not compose is reported, not dropped" {
                let stale = {
                    healthySeamAuthority with
                        Components =
                            healthySeamAuthority.Components
                            @ [
                                {
                                    AuthorityComponent = ComponentId.ofModule "retired-module"
                                    DeclaredGrant = SeamGrant.ofInterfaces [ "ISecretStore" ]
                                    DerivedReach = []
                                    ComposedHere = false
                                }
                            ]
                }

                let findings =
                    (sectionOf (runReport (withSeam stale) None) SeamAuthoritySection).Findings

                Expect.stringContains
                    (String.Join(" ", findings))
                    "does not compose"
                    "a stale grant still reads as governance, so it is named rather than silently omitted"
            }

            test "the per-component enumeration is capped and says how many it withheld" {
                let many = {
                    healthySeamAuthority with
                        Components =
                            [ 1..25 ]
                            |> List.map (fun i -> {
                                AuthorityComponent = ComponentId.ofModule (sprintf "component-%02d" i)
                                DeclaredGrant = SeamGrant.ofInterfaces [ "IEntityStore" ]
                                DerivedReach = [ SeamId.ofInterface "IEntityStore" ]
                                ComposedHere = true
                            })
                }

                let findings =
                    (sectionOf (runReport (withSeam many) None) SeamAuthoritySection).Findings

                Expect.hasLength
                    findings
                    (DeploymentVerificationReport.SeamAuthorityComponentCap + 1)
                    "the cap plus one line accounting for what it withheld"

                Expect.stringContains
                    (List.last findings)
                    "5 further component(s) not listed"
                    "a silent truncation would let a large composition present as a small one"
            }
        ]

        // ── Phase 693: the mirror is derived from a real composition ───

        testList "the seam-authority mapper" [
            let jobModule () =
                ServerModule.create "Jobs"
                |> ServerModule.withComponentId "jobs-service"
                |> ServerModule.withJobHandler ("scan", NoopSeamJobHandler(), CronTrigger "0 8 * * *")

            let jobsComponent = ComponentId.ofModule "jobs-service"

            /// An effect envelope for the probe module, so the Phase 300
            /// half never has an opinion of its own here — without one
            /// the profile resolves `SeamAuthorityGate.disabled` and the
            /// seam question is never asked.
            let effectEnvelope = Map.ofList [ jobsComponent, CompanionCapability.identity ]

            test "the component roster and the counts are recomputed, never reported by the caller" {
                let modules = [ jobModule (); ServerModule.create "Empty" ]

                let mirror =
                    SeamAuthorityEnforcement.deploymentVerificationEvidence
                        CompositionProfile.Standard
                        None
                        modules
                        (Some(Ok()))

                Expect.hasLength mirror.Components 2 "one entry per composed module"

                let expectedSeams =
                    modules
                    |> List.sumBy (fun m -> (SeamAuthorityEnforcement.reachOf m).ReachedSeams.Length)

                match mirror.Verification with
                | SeamAuthorityAdmitted(components, seams) ->
                    Expect.equal components 2 "the component count comes from the composition"

                    Expect.equal
                        seams
                        expectedSeams
                        "and the seam count is recomputed from the Phase 438 Needs projection, not passed in"
                | other -> failtestf "expected SeamAuthorityAdmitted, got %A" other
            }

            test "an undeclared composition mirrors as unrestricted throughout — the additive floor" {
                let mirror =
                    SeamAuthorityEnforcement.deploymentVerificationEvidence
                        CompositionProfile.Standard
                        None
                        [ jobModule () ]
                        (Some(Ok()))

                Expect.isFalse
                    (mirror.Components |> List.exists (fun e -> SeamGrant.isDeclared e.DeclaredGrant))
                    "a composition with no SeamGrantSignature declares nothing (GP 11)"

                Expect.isFalse mirror.DeclarationMandatory "and the standard profile does not demand it"
                Expect.equal mirror.Profile "standard" "the profile label rides through"
            }

            test "a root that never ran the check mirrors as unenforced" {
                // `None`, not a flag: the fact IS the absence of a
                // result, so there is no value a root could pass that
                // claims enforcement it did not perform.
                let grants =
                    Map.ofList [ jobsComponent, SeamGrant.ofInterfaces [ "IJobScheduler" ] ]

                let mirror =
                    SeamAuthorityEnforcement.deploymentVerificationEvidence
                        CompositionProfile.Verified
                        (Some grants)
                        [ jobModule () ]
                        None

                Expect.equal
                    mirror.Verification
                    SeamAuthorityUnenforced
                    "no result means no enforcement, and it says so"

                Expect.isTrue mirror.DeclarationMandatory "the verified profile makes declaration mandatory"

                Expect.isTrue
                    (mirror.Components |> List.forall (fun e -> SeamGrant.isDeclared e.DeclaredGrant))
                    "the declarations are still reported — they exist, they simply bound nothing"

                Expect.equal
                    (labelOf (runReport (withSeam mirror) None) SeamAuthoritySection)
                    "observed"
                    "and end to end that reads as observed, never as a verification"
            }

            test "the mirror's reach agrees with the projection the gate itself reads" {
                let m = jobModule ()

                let mirror =
                    SeamAuthorityEnforcement.deploymentVerificationEvidence CompositionProfile.Standard None [ m ] None

                Expect.equal
                    (mirror.Components |> List.head |> _.DerivedReach |> Set.ofList)
                    ((SeamAuthorityEnforcement.reachOf m).ReachedSeams |> Set.ofList)
                    "the report and the gate must read one declaration-to-substrate map, never two"
            }

            test "a real refusal mirrors with its own account and the enumeration behind it" {
                let modules = [ jobModule () ]

                // Grant the module a seam it does NOT reach and withhold
                // the one it does, so the refusal is produced by the real
                // gate rather than constructed here.
                let grants = Map.ofList [ jobsComponent, SeamGrant.ofInterfaces [ "IAuditSink" ] ]

                let outcome =
                    SeamAuthorityEnforcement.verifyAudited
                        (SilentSeamAuditLog() :> IAuditLog)
                        scope
                        CompositionProfile.Standard
                        (Some effectEnvelope)
                        (Some grants)
                        modules

                Expect.isError outcome "the composition genuinely reaches a seam it did not declare"

                let mirror =
                    SeamAuthorityEnforcement.deploymentVerificationEvidence
                        CompositionProfile.Standard
                        (Some grants)
                        modules
                        (Some outcome)

                match mirror.Verification with
                | SeamAuthorityRefused(detail, findings) ->
                    Expect.stringContains detail "refused" "the summary says the check refused"
                    Expect.isNonEmpty findings "and the enumeration behind it is carried, not folded into the summary"
                | other -> failtestf "expected SeamAuthorityRefused, got %A" other

                Expect.equal
                    (labelOf (runReport (withSeam mirror) None) SeamAuthoritySection)
                    "failed"
                    "and a real refusal reddens the section end to end"
            }
        ]
    ]