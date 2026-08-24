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
// The report composes five verifiers that already exist. So this pack
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
                    [ "verified"; "verified"; "verified"; "verified"; "verified" ]
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
                    ]
                    "an unregistered evidence seam degrades to five absent sections, not an error"

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
                    "'all COMPOSED verified' says nothing about the four that were not"

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
    ]