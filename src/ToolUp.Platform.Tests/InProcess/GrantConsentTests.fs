// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.GrantConsentTests

// ─── Phase 552 — the consented-grant registry ────────────────────────
//
// Phase 551 gave a module a voice and then refused every grant that used
// it: `RequiresCounterpartyApproval` was inert at the write path AND at
// dispatch because nothing could produce the artifact it asks for. This
// pack proves the arm is now real, and — more importantly — that it is
// real in exactly the conservative way the phase claims.
//
// Six things, matching 552.A–E:
//
//   1. **The canonical payload binds meaning.** Every field that changes
//      what a record SAYS is covered by the signature. An approval's
//      signature must not be replayable as a revocation, nor a record for
//      alice's grant replayable onto bob's. The table drives this because
//      the interesting failure is a field someone forgets to include.
//   2. **The lifecycle is total and conservative.** `GrantConsent.current`
//      resolves supersession, ties, revocation, expiry and the pending
//      state without a store and without a clock beyond the one handed
//      in — so two app instances cannot disagree about who has access.
//   3. **Verification refuses on every trust ground**, and never falls
//      back: a tampered payload, an unregistered key, a key registered
//      for a DIFFERENT party, and an algorithm declared other than the
//      registered key's are four separate named refusals.
//   4. **Revocation is immediate at dispatch.** The property the phase
//      exists for: approve, dispatch allows; revoke, the very next call
//      refuses — no sweep, no cache, no second write.
//   5. **The store-absent floor is Phase 551's behaviour, unchanged.**
//      A deployment that composes no registry refuses every counterparty
//      grant, at the write path and at dispatch.
//   6. **The audit split holds.** A forged signature raises the tamper
//      alert; an ordinary revocation does not. A test that only counted
//      rows would pass either way, so these assert the event TYPES.
//
// **Non-vacuity.** Every dispatch case runs the real
// `GrantConsentStore.guardDispatchWithConsent` with a SYNCHRONOUS
// scheduler, so one call yields both the decision and the emitted row —
// a guard that refused without auditing, or audited without refusing,
// fails here rather than passing half of the assertion. The recording log
// accumulates and asserts nothing itself, so "exactly one row, of this
// type" is a real claim and not a tautology over an empty list.

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.GrantConsentStore

// ─── Fixtures ────────────────────────────────────────────────────────

let private Read = ModulePermission.Read

let private party = PartyRef.create "acme-dpo"
let private otherParty = PartyRef.create "other-dpo"
let private counterparty = GrantPolicy.RequiresCounterpartyApproval party
let private moduleName = "SkuAnalysis"
let private teamId = "team-a"
let private subjectId = "alice"

let private subject = ConsentSubject.create teamId subjectId moduleName

let private now = DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)

/// A shared HMAC key. Symmetric on purpose for most cases — it keeps the
/// signing side of the test honest without a key-generation dance — with
/// one ECDSA case proving the asymmetric arm is not decorative.
let private macKeyBytes = Encoding.UTF8.GetBytes "a-shared-consent-signing-key-32b"
let private macKeyBase64 = Convert.ToBase64String macKeyBytes
let private keyId = "party-key-1"

let private keyring = [
    {
        Party = party
        KeyId = keyId
        Material = ConsentHmacSha256 macKeyBase64
    }
]

let private verifier = verifierOver keyring

type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Recorded = List.ofSeq recorded

    member _.EventTypes =
        recorded |> Seq.map (snd >> AuditEvent.eventTypeName) |> List.ofSeq

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

/// Synchronous scheduler — decision and emission observed from one call.
let private runNow (work: Async<unit>) = Async.RunSynchronously work

let private registry =
    GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations [ moduleName, counterparty ]

/// Build a record and sign it with the shared MAC key, so the signature
/// covers the EXACT record that will be stored.
let private signed (draft: GrantConsentRecord) = {
    draft with
        Signature = ConsentSigning.signHmacSha256 macKeyBytes keyId draft
}

let private draftRecord id status supersedes expiresAt = {
    ConsentId = id
    Subject = subject
    Party = party
    Status = status
    IssuedAtUtc = now
    ExpiresAtUtc = expiresAt
    Signature = {
        KeyId = keyId
        DeclaredAlgorithm = "HmacSha256"
        Value = ""
        SignedAtUtc = now
    }
    Supersedes = supersedes
    RecordedBy = "admin-a"
}

let private approvedRecord id supersedes =
    signed (draftRecord id ConsentStatus.Approved supersedes None)

let private proposedRecord id =
    signed (draftRecord id ConsentStatus.Proposed None None)

let private revokedRecord id supersedes =
    signed (draftRecord id ConsentStatus.Revoked supersedes None)

let private freshConsentStore () =
    InMemoryGrantConsentStore() :> IGrantConsentStore

let private freshPermissionStore () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-grantconsent-perm-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    PermissionStore(LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage) :> IPermissionStore

/// The shipped write chain: the Phase 551 grant-policy decorator over a
/// real blob-backed store, with the Phase 552 oracle wired in. Composed
/// here exactly as `ComposeTeamRuntime` composes it, so a write in these
/// cases travels the same path a request does.
let private guardedStore (consentStore: IGrantConsentStore option) (auditLog: IAuditLog option) =
    let inner = freshPermissionStore ()

    let oracle =
        StoreCounterpartyConsentOracle(consentStore, verifier, (fun () -> now), ?auditLog = auditLog)
        :> GrantPolicyGuard.CounterpartyConsentOracle

    inner, GrantPolicyGuard.GrantPolicyPermissionStore(inner, registry, oracle, auditLog, None) :> IPermissionStore

let private liveGrantRecord = {
    State = GrantState.Active
    SatisfiedPolicy = counterparty
    Justification = "counterparty consent c-approved"
    ConsentedBy = None
}

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 552 — consented-grant registry" [

        // ── 1. The canonical payload binds meaning ───────────────────
        testList "canonical payload" [
            test "every meaning-bearing field changes the payload" {
                // If a field is NOT covered, a signature over one record
                // verifies over a different one — which is a replay, not a
                // formatting nit. Each perturbation below is a real attack
                // shape: promote a proposal to an approval, re-aim a
                // consent at another subject or party, strip an expiry,
                // detach a revocation's supersession edge.
                let baseline = draftRecord "c1" ConsentStatus.Proposed None None
                let basePayload = GrantConsentRecord.canonicalPayload baseline

                let perturbations = [
                    "status",
                    {
                        baseline with
                            Status = ConsentStatus.Approved
                    }
                    "subject",
                    {
                        baseline with
                            Subject = ConsentSubject.create teamId "bob" moduleName
                    }
                    "team",
                    {
                        baseline with
                            Subject = ConsentSubject.create "team-b" subjectId moduleName
                    }
                    "module",
                    {
                        baseline with
                            Subject = ConsentSubject.create teamId subjectId "OtherModule"
                    }
                    "party", { baseline with Party = otherParty }
                    "consentId", { baseline with ConsentId = "c2" }
                    "issuedAt",
                    {
                        baseline with
                            IssuedAtUtc = now.AddSeconds 1.0
                    }
                    "expiresAt",
                    {
                        baseline with
                            ExpiresAtUtc = Some(now.AddDays 1.0)
                    }
                    "supersedes", { baseline with Supersedes = Some "c0" }
                    "keyId",
                    {
                        baseline with
                            Signature = {
                                baseline.Signature with
                                    KeyId = "other-key"
                            }
                    }
                    "declaredAlgorithm",
                    {
                        baseline with
                            Signature = {
                                baseline.Signature with
                                    DeclaredAlgorithm = "EcdsaP256"
                            }
                    }
                ]

                for label, perturbed in perturbations do
                    Expect.notEqual
                        (GrantConsentRecord.canonicalPayload perturbed)
                        basePayload
                        $"changing '{label}' must change the signed bytes"
            }

            test "the signature VALUE is excluded — it is the output, not an input" {
                let baseline = draftRecord "c1" ConsentStatus.Approved None None

                let withSignature = {
                    baseline with
                        Signature = {
                            baseline.Signature with
                                Value = "deadbeef"
                        }
                }

                Expect.equal
                    (GrantConsentRecord.canonicalPayload withSignature)
                    (GrantConsentRecord.canonicalPayload baseline)
                    "a payload that covered its own signature could never be produced"
            }

            test "a separator smuggled into a field cannot re-frame a neighbour" {
                // Without escaping, a subject id of "alice\nmodule=Other"
                // would produce the same bytes as a record for a different
                // module — one signature, two meanings.
                let honest = {
                    draftRecord "c1" ConsentStatus.Approved None None with
                        Subject = ConsentSubject.create teamId "alice\nmodule=Other" moduleName
                }

                let other = {
                    draftRecord "c1" ConsentStatus.Approved None None with
                        Subject = ConsentSubject.create teamId "alice" "Other"
                }

                Expect.notEqual
                    (GrantConsentRecord.canonicalPayload honest)
                    (GrantConsentRecord.canonicalPayload other)
                    "field values are escaped, so no value can forge a separator"
            }

            test "ConsentStatus.ofToken never falls through to Approved" {
                // The shapes a corrupt blob, a truncated write, or a NEWER
                // deployment's status arm actually produce. None may land on
                // the one state that confers authority — and none may land on
                // `Revoked` either, which would assert a withdrawal that never
                // happened. `Proposed` is the honest fall-through.
                let mangled = [ ""; "   "; null; "ok"; "true"; "1"; "consented"; "approved-v2"; "app roved" ]

                for token in mangled do
                    Expect.equal
                        (ConsentStatus.ofToken token)
                        ConsentStatus.Proposed
                        $"'{token}' confers nothing and claims nothing"

                Expect.equal (ConsentStatus.ofToken "approved") ConsentStatus.Approved "the real token still parses"
                Expect.equal (ConsentStatus.ofToken "revoked") ConsentStatus.Revoked "as does revoked"

                // Case- and whitespace-insensitive, matching
                // `GrantState.ofToken` / `ModuleExposure.ofToken`. Pinned as a
                // deliberate property rather than left to be discovered: a
                // persisted token that round-trips through a case-mangling
                // layer must not silently become "not yet approved".
                Expect.equal
                    (ConsentStatus.ofToken " Approved ")
                    ConsentStatus.Approved
                    "token parse is case- and whitespace-insensitive, per the estate convention"
            }
        ]

        // ── 2. Lifecycle resolution ──────────────────────────────────
        testList "GrantConsent.current" [
            test "no records is a named denial, not an exception" {
                Expect.equal (GrantConsent.current now []) (Error ConsentDenial.NoConsentRecord) "empty registry"
            }

            test "a superseding revocation wins over the approval it withdraws" {
                let records = [ approvedRecord "c1" None; revokedRecord "c2" (Some "c1") ]

                Expect.equal (GrantConsent.current now records) (Error ConsentDenial.Revoked) "revocation is current"

                // …and the order the store happened to return them in must
                // not matter (GP 12 rule 5).
                Expect.equal
                    (GrantConsent.current now (List.rev records))
                    (Error ConsentDenial.Revoked)
                    "enumeration order cannot change who has access"
            }

            test "a same-instant tie resolves identically on every node" {
                // Both issued at `now`, neither superseding the other. The
                // ConsentId tiebreak is what stops two app instances
                // disagreeing; without it this is a coin flip.
                let a = approvedRecord "aaa" None
                let b = revokedRecord "bbb" None

                let forward = GrantConsent.current now [ a; b ]
                let reverse = GrantConsent.current now [ b; a ]

                Expect.equal forward reverse "the verdict is order-independent"
                Expect.equal forward (Error ConsentDenial.Revoked) "and it is the ordinally-later record"
            }

            test "a proposal confers nothing" {
                match GrantConsent.current now [ proposedRecord "c1" ] with
                | Error(ConsentDenial.NotApproved status) -> Expect.equal status "proposed" "names the state"
                | other -> failtestf "expected NotApproved, got %A" other
            }

            test "an expired approval is denied, and names when" {
                let expiring =
                    signed (draftRecord "c1" ConsentStatus.Approved None (Some(now.AddHours -1.0)))

                match GrantConsent.current now [ expiring ] with
                | Error(ConsentDenial.Expired at) -> Expect.equal at (now.AddHours -1.0) "names the expiry"
                | other -> failtestf "expected Expired, got %A" other
            }

            test "an approval with no expiry stays live" {
                Expect.isOk (GrantConsent.current (now.AddYears 5) [ approvedRecord "c1" None ]) "no expiry means none"
            }

            test "a record superseding an id the store no longer holds still counts" {
                // Otherwise a lost predecessor resurrects a withdrawn
                // consent — the store forgetting a row must never GRANT.
                let orphanRevocation = revokedRecord "c2" (Some "c1-gone")

                Expect.equal
                    (GrantConsent.current now [ orphanRevocation ])
                    (Error ConsentDenial.Revoked)
                    "the revocation is still evidence a party acted"
            }
        ]

        // ── 3. Verification (552.C) ──────────────────────────────────
        testList "signature verification" [
            test "a correctly signed record verifies" {
                Expect.equal (verifier.Verify(approvedRecord "c1" None)) (Ok()) "the happy path exists"
            }

            test "tampering with a covered field invalidates the signature" {
                // Sign a proposal, then promote it to an approval. This is
                // the exact attack the canonical payload's status coverage
                // exists to stop, tested end to end rather than on bytes.
                let proposal = proposedRecord "c1"

                let promoted = {
                    proposal with
                        Status = ConsentStatus.Approved
                }

                Expect.equal
                    (verifier.Verify promoted)
                    (Error ConsentDenial.SignatureInvalid)
                    "a proposal's signature does not authorise an approval"
            }

            test "an unregistered key id is refused by name" {
                let record = approvedRecord "c1" None

                let foreign = {
                    record with
                        Signature = {
                            record.Signature with
                                KeyId = "not-registered"
                        }
                }

                Expect.equal
                    (verifier.Verify foreign)
                    (Error(ConsentDenial.UnknownKey "not-registered"))
                    "no key, no verification — never a skip"
            }

            test "a key registered for ANOTHER party does not verify this one" {
                // The lookup is keyed by (party, keyId), so party is part
                // of the identity rather than a field checked afterwards
                // and forgotten.
                let record = {
                    approvedRecord "c1" None with
                        Party = otherParty
                }

                match verifier.Verify record with
                | Error(ConsentDenial.UnknownKey _) -> ()
                | other -> failtestf "expected UnknownKey for a foreign party, got %A" other
            }

            test "declaring an algorithm other than the registered key's is refused, not ignored" {
                // Silently verifying under the registered algorithm would
                // be "doing the right thing" while hiding a downgrade
                // attempt. The refusal names what was declared.
                let record = approvedRecord "c1" None

                let downgraded = {
                    record with
                        Signature = {
                            record.Signature with
                                DeclaredAlgorithm = "EcdsaP256"
                        }
                }

                Expect.equal
                    (verifier.Verify downgraded)
                    (Error(ConsentDenial.AlgorithmNotAllowed "EcdsaP256"))
                    "a disagreement is evidence, not noise"
            }

            test "malformed signature bytes deny rather than throw" {
                let record = approvedRecord "c1" None

                let garbage = {
                    record with
                        Signature = {
                            record.Signature with
                                Value = "this is not base64!!"
                        }
                }

                Expect.equal
                    (verifier.Verify garbage)
                    (Error ConsentDenial.SignatureInvalid)
                    "an exception escaping into dispatch would turn a denial into a 500"
            }

            test "the ECDSA P-256 arm is real, and rejects a tamper" {
                use ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256)
                let spki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo())

                let ecVerifier =
                    verifierOver [
                        {
                            Party = party
                            KeyId = "ec-key"
                            Material = ConsentEcdsaP256 spki
                        }
                    ]

                let draft = {
                    draftRecord "c1" ConsentStatus.Approved None None with
                        Signature = {
                            KeyId = "ec-key"
                            DeclaredAlgorithm = "EcdsaP256"
                            Value = ""
                            SignedAtUtc = now
                        }
                }

                let record = {
                    draft with
                        Signature = ConsentSigning.signEcdsaP256 ecdsa "ec-key" draft
                }

                Expect.equal (ecVerifier.Verify record) (Ok()) "sign/verify round-trips on the asymmetric arm"

                Expect.equal
                    (ecVerifier.Verify {
                        record with
                            Status = ConsentStatus.Revoked
                    })
                    (Error ConsentDenial.SignatureInvalid)
                    "and a re-purposed signature does not"
            }

            test "an empty keyring denies with UnknownKey, naming the id" {
                // The shipped default registration. It must say "you have
                // not registered this party's key" rather than either
                // admitting the record or failing in a way an operator
                // reads as a revocation.
                let empty = verifierOver []

                Expect.equal
                    (empty.Verify(approvedRecord "c1" None))
                    (Error(ConsentDenial.UnknownKey keyId))
                    "the default composition is fail-closed and diagnosable"
            }

            test "the denying verifier admits nothing" {
                Expect.equal
                    (denyingVerifier.Verify(approvedRecord "c1" None))
                    (Error ConsentDenial.NoVerifier)
                    "no verifier composed means nothing is checked, so nothing is admitted"
            }
        ]

        // ── 4. Lifecycle + resolution against a live store ───────────
        testList "the handshake, end to end" [
            testCaseAsync
                "propose → approve → live; revoke → denied at once"
                (async {
                    let store = freshConsentStore ()
                    let audit = RecordingAuditLog()
                    let auditLog = Some(audit :> IAuditLog)

                    let! proposed = GrantConsents.propose store verifier auditLog runNow (proposedRecord "c-proposed")

                    Expect.isOk proposed "a signed proposal is accepted"

                    let! stillPending = resolveLive (Some store) verifier auditLog runNow now subject party

                    Expect.equal
                        stillPending
                        (Error(ConsentDenial.NotApproved "proposed"))
                        "a proposal confers nothing at resolution"

                    let! approved =
                        GrantConsents.approve
                            store
                            verifier
                            auditLog
                            runNow
                            (approvedRecord "c-approved" (Some "c-proposed"))

                    Expect.isOk approved "a signed approval supersedes the proposal"

                    let! live = resolveLive (Some store) verifier auditLog runNow now subject party

                    match live with
                    | Ok r -> Expect.equal r.ConsentId "c-approved" "the approval is what speaks"
                    | Error e -> failtestf "expected live consent, got %A" e

                    let! revoked =
                        GrantConsents.revoke
                            store
                            verifier
                            auditLog
                            runNow
                            (revokedRecord "c-revoked" (Some "c-approved"))

                    Expect.isOk revoked "the withdrawal is accepted"

                    let! afterRevocation = resolveLive (Some store) verifier auditLog runNow now subject party

                    Expect.equal
                        afterRevocation
                        (Error ConsentDenial.Revoked)
                        "revocation takes effect at the next resolution, with no sweep in between"

                    // The whole ceremony is on the trail, in order, one row per
                    // act — not one row with an outcome field.
                    Expect.equal
                        audit.EventTypes
                        [ "GrantConsentProposed"; "GrantConsentApproved"; "GrantConsentRevoked" ]
                        "three acts, three rows"
                })

            testCaseAsync
                "an unverifiable record is never stored"
                (async {
                    // Verified BEFORE the write, so the registry cannot hold a
                    // record that could never be honoured.
                    let store = freshConsentStore ()
                    let audit = RecordingAuditLog()
                    let record = approvedRecord "c1" None

                    let tampered = {
                        record with
                            RecordedBy = "admin-a"
                            Status = ConsentStatus.Approved
                    }

                    let forged = {
                        tampered with
                            Signature = {
                                tampered.Signature with
                                    Value = "AAAA"
                            }
                    }

                    let! result = GrantConsents.approve store verifier (Some(audit :> IAuditLog)) runNow forged

                    Expect.equal result (Error ConsentDenial.SignatureInvalid) "refused"

                    let! listed = store.ListForSubject subject

                    match listed with
                    | Ok rs -> Expect.isEmpty rs "and nothing landed in the registry"
                    | Error e -> failtestf "list failed: %s" e

                    Expect.equal
                        audit.EventTypes
                        [ "GrantConsentVerificationDenied" ]
                        "the tamper alert fired, and no lifecycle row did"
                })

            testCaseAsync
                "lodging a record under the wrong status is refused"
                (async {
                    let store = freshConsentStore ()
                    let! result = GrantConsents.approve store verifier None runNow (proposedRecord "c1")

                    Expect.isError result "approve lodges approvals; it does not promote a proposal by relabelling it"
                })

            testCaseAsync
                "no store composed denies with NoConsentStore"
                (async {
                    let! resolved = resolveLive None verifier None runNow now subject party
                    Expect.equal resolved (Error ConsentDenial.NoConsentStore) "the Phase 551 floor, named"
                })

            testCaseAsync
                "a record filed for another subject does not answer this one"
                (async {
                    let store = freshConsentStore ()

                    let foreign = {
                        approvedRecord "c1" None with
                            Subject = ConsentSubject.create teamId "bob" moduleName
                    }

                    // Re-sign so the record is internally valid — the point is
                    // that ADDRESSING is checked, not that the signature fails.
                    let foreign = {
                        foreign with
                            Signature = ConsentSigning.signHmacSha256 macKeyBytes keyId foreign
                    }

                    do! store.Put foreign |> Async.Ignore

                    let! resolved = resolveLive (Some store) verifier None runNow now subject party

                    Expect.equal
                        resolved
                        (Error ConsentDenial.NoConsentRecord)
                        "the store scopes by subject, so alice sees none of bob's"
                })
        ]

        // ── 5. The write path (552.D, write half) ────────────────────
        testList "grant write" [
            testCaseAsync
                "a counterparty grant is refused with no consent"
                (async {
                    let audit = RecordingAuditLog()
                    let auditLog = Some(audit :> IAuditLog)
                    let store = freshConsentStore ()
                    let inner, guarded = guardedStore (Some store) auditLog

                    let! result =
                        grantWithCounterpartyApproval
                            guarded
                            registry
                            (Some store)
                            verifier
                            auditLog
                            now
                            teamId
                            subjectId
                            moduleName
                            [ Read ]
                            "quarterly review"

                    Expect.equal result (Error ConsentDenial.NoConsentRecord) "no consent, no grant"

                    let! doc = inner.GetTeamPermissions teamId
                    Expect.isEmpty (Map.toList doc.Members) "and nothing was persisted"
                })

            testCaseAsync
                "a counterparty grant lands once consent is live"
                (async {
                    let audit = RecordingAuditLog()
                    let auditLog = Some(audit :> IAuditLog)
                    let store = freshConsentStore ()

                    let! _ = GrantConsents.approve store verifier auditLog runNow (approvedRecord "c-approved" None)

                    let inner, guarded = guardedStore (Some store) auditLog

                    let! result =
                        grantWithCounterpartyApproval
                            guarded
                            registry
                            (Some store)
                            verifier
                            auditLog
                            now
                            teamId
                            subjectId
                            moduleName
                            [ Read ]
                            "quarterly review"

                    Expect.equal result (Ok GrantWriteOutcome.Granted) "the arm Phase 551 could not reach now completes"

                    let! doc = inner.GetTeamPermissions teamId

                    let record =
                        doc.Grants |> Map.tryFind subjectId |> Option.bind (Map.tryFind moduleName)

                    match record with
                    | Some r ->
                        Expect.equal r.State GrantState.Active "the grant is live"

                        Expect.equal
                            r.SatisfiedPolicy
                            counterparty
                            "and it records the EXACT arm it satisfied, party included"

                        Expect.stringContains
                            r.Justification
                            "c-approved"
                            "the permission document names the artifact that satisfied the policy"
                    | None -> failtest "expected a grant record"
                })

            testCaseAsync
                "the Phase 551 write guard still refuses a forged Active record"
                (async {
                    // The document half without the consent half. Phase 551
                    // recorded this exact shape as refusable-while-552-is-
                    // unshipped; it stays refusable AFTER 552, because the
                    // registry is the other half and nothing was lodged in it.
                    let audit = RecordingAuditLog()
                    let auditLog = Some(audit :> IAuditLog)
                    let store = freshConsentStore ()
                    let _, guarded = guardedStore (Some store) auditLog

                    let! written =
                        guarded.SetTeamPermissions(
                            teamId,
                            {
                                TeamPermissions.empty with
                                    Members = Map.ofList [ subjectId, Map.ofList [ moduleName, [ Read ] ] ]
                                    Grants = Map.ofList [ subjectId, Map.ofList [ moduleName, liveGrantRecord ] ]
                            }
                        )

                    Expect.isError written "forging the record alone changes nothing"

                    // The Phase 551 decorator emits fire-and-forget per the
                    // IAuditLog contract, so poll briefly rather than assume
                    // scheduling order — a flake here would be a false red,
                    // and a permanently-absent row still fails.
                    let mutable waited = 0

                    while List.isEmpty audit.Recorded && waited < 100 do
                        do! Async.Sleep 20
                        waited <- waited + 1

                    Expect.contains audit.EventTypes "GrantPolicyRefused" "and the refusal is audited (GP 6)"
                })

            testCaseAsync
                "with no consent registry composed, the arm behaves exactly as Phase 551 shipped"
                (async {
                    let audit = RecordingAuditLog()
                    let auditLog = Some(audit :> IAuditLog)
                    let _, guarded = guardedStore None auditLog

                    let! written =
                        guarded.SetTeamPermissions(
                            teamId,
                            {
                                TeamPermissions.empty with
                                    Members = Map.ofList [ subjectId, Map.ofList [ moduleName, [ Read ] ] ]
                                    Grants = Map.ofList [ subjectId, Map.ofList [ moduleName, liveGrantRecord ] ]
                            }
                        )

                    match written with
                    | Error msg ->
                        Expect.stringContains
                            msg
                            "GRANT-POLICY-COUNTERPARTY-UNAVAILABLE"
                            "the pre-552 refusal, unchanged and by name"
                    | Ok() -> failtest "a deployment with no consent registry must refuse every counterparty grant"
                })
        ]

        // ── 6. Dispatch (552.D) ──────────────────────────────────────
        testList "dispatch" [
            test "an empty registry short-circuits before anything is read" {
                // GP 11 / GP 13: a deployment declaring no policy pays
                // nothing here. Passing a `None` audit log and a scheduler
                // that would fail the test proves it is never reached.
                let explode _ =
                    failtest "an all-default deployment must not schedule anything"

                let result =
                    guardDispatchWithConsent
                        GrantPolicyGuard.ModuleGrantPolicyRegistry.empty
                        Map.empty
                        Map.empty
                        None
                        explode
                        "team-a"
                        subjectId
                        moduleName

                Expect.equal result (Ok()) "byte-parity with the pre-551 path"
            }

            test "both halves present ⇒ allowed, with no refusal row" {
                let audit = RecordingAuditLog()

                let result =
                    guardDispatchWithConsent
                        registry
                        (Map.ofList [ moduleName, liveGrantRecord ])
                        (Map.ofList [ moduleName, Ok() ])
                        (Some(audit :> IAuditLog))
                        runNow
                        "team-a"
                        subjectId
                        moduleName

                Expect.equal result (Ok()) "a live consent plus an Active record is the whole condition"
                Expect.isEmpty audit.Recorded "an admission emits nothing"
            }

            test "a revoked consent refuses the very next call, naming the revocation" {
                let audit = RecordingAuditLog()

                let result =
                    guardDispatchWithConsent
                        registry
                        (Map.ofList [ moduleName, liveGrantRecord ])
                        (Map.ofList [ moduleName, Error ConsentDenial.Revoked ])
                        (Some(audit :> IAuditLog))
                        runNow
                        "team-a"
                        subjectId
                        moduleName

                match result with
                | Ok() -> failtest "a withdrawn consent must refuse"
                | Error payload ->
                    Expect.equal payload.InertReason "consent-revoked" "the reason is the CONSENT denial, not a stub"

                    Expect.equal
                        payload.DeclaredPolicy
                        (GrantPolicy.toToken counterparty)
                        "and it names the declared policy"

                // The decision AND the row from one call — a guard that
                // refused without auditing fails here.
                Expect.equal audit.EventTypes [ "UnconsentedGrantRefused" ] "exactly one refusal row"
            }

            test "a forged Active record with no consent is refused" {
                let audit = RecordingAuditLog()

                let result =
                    guardDispatchWithConsent
                        registry
                        (Map.ofList [ moduleName, liveGrantRecord ])
                        (Map.ofList [ moduleName, Error ConsentDenial.NoConsentRecord ])
                        (Some(audit :> IAuditLog))
                        runNow
                        "team-a"
                        subjectId
                        moduleName

                match result with
                | Ok() -> failtest "the document half alone must not confer authority"
                | Error payload -> Expect.equal payload.InertReason "consent-no-record" "named honestly"
            }

            test "a live consent with NO grant record is still refused, and says so" {
                // The opposite failure, and it needs its own reason string:
                // "the counterparty agreed and nobody wrote the grant" and
                // "the grant exists and consent was withdrawn" are opposite
                // operator actions.
                let audit = RecordingAuditLog()

                let result =
                    guardDispatchWithConsent
                        registry
                        Map.empty
                        (Map.ofList [ moduleName, Ok() ])
                        (Some(audit :> IAuditLog))
                        runNow
                        "team-a"
                        subjectId
                        moduleName

                match result with
                | Ok() -> failtest "consent alone is not a grant"
                | Error payload ->
                    Expect.equal payload.InertReason "no-grant-record" "distinguishable from a revocation"
            }

            test "a grant record satisfying a DIFFERENT counterparty does not carry over" {
                let audit = RecordingAuditLog()

                let forOtherParty = {
                    liveGrantRecord with
                        SatisfiedPolicy = GrantPolicy.RequiresCounterpartyApproval otherParty
                }

                let result =
                    guardDispatchWithConsent
                        registry
                        (Map.ofList [ moduleName, forOtherParty ])
                        (Map.ofList [ moduleName, Ok() ])
                        (Some(audit :> IAuditLog))
                        runNow
                        "team-a"
                        subjectId
                        moduleName

                Expect.isError result "two counterparties are incomparable, not interchangeable"
            }

            test "no stamped verdict at all reads as no store, never as consent" {
                // The shape a misconfiguration takes: the registry declares
                // a counterparty arm and the middleware stamped nothing.
                // Absence must be a denial.
                let audit = RecordingAuditLog()

                let result =
                    guardDispatchWithConsent
                        registry
                        (Map.ofList [ moduleName, liveGrantRecord ])
                        Map.empty
                        (Some(audit :> IAuditLog))
                        runNow
                        "team-a"
                        subjectId
                        moduleName

                match result with
                | Ok() -> failtest "an unstamped verdict must not admit"
                | Error payload -> Expect.equal payload.InertReason "consent-no-store" "fail-closed by default"
            }

            test "a non-counterparty arm delegates to the Phase 551 guard unchanged" {
                let ackRegistry =
                    GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations [
                        moduleName, GrantPolicy.RequiresAcknowledgement
                    ]

                let ackRecord = {
                    State = GrantState.Active
                    SatisfiedPolicy = GrantPolicy.RequiresAcknowledgement
                    Justification = "reviewed"
                    ConsentedBy = None
                }

                let audit = RecordingAuditLog()

                // Allowed, and — the point — allowed WITHOUT any consent
                // verdict, so Phase 552 has not made the other arms depend
                // on a registry they never needed.
                let allowed =
                    guardDispatchWithConsent
                        ackRegistry
                        (Map.ofList [ moduleName, ackRecord ])
                        Map.empty
                        (Some(audit :> IAuditLog))
                        runNow
                        "team-a"
                        subjectId
                        moduleName

                Expect.equal allowed (Ok()) "the acknowledgement arm is untouched by this phase"

                let refused =
                    guardDispatchWithConsent
                        ackRegistry
                        Map.empty
                        Map.empty
                        (Some(audit :> IAuditLog))
                        runNow
                        "team-a"
                        subjectId
                        moduleName

                match refused with
                | Ok() -> failtest "an unbacked acknowledgement grant must still refuse"
                | Error payload -> Expect.equal payload.InertReason "no-grant-record" "with Phase 551's own reason"
            }
        ]

        // ── 7. The audit split (552.E) ───────────────────────────────
        testList "audit" [
            testCaseAsync
                "an ordinary revocation raises NO tamper alert"
                (async {
                    // The split's whole value: if a revocation raised the
                    // forgery alert, the alert's rate would be dominated by
                    // routine operations and nobody would read it.
                    let store = freshConsentStore ()
                    let audit = RecordingAuditLog()
                    let auditLog = Some(audit :> IAuditLog)

                    let! _ = GrantConsents.approve store verifier None runNow (approvedRecord "c1" None)
                    let! _ = GrantConsents.revoke store verifier None runNow (revokedRecord "c2" (Some "c1"))

                    let! resolved = resolveLive (Some store) verifier auditLog runNow now subject party

                    Expect.equal resolved (Error ConsentDenial.Revoked) "denied"

                    Expect.isEmpty
                        audit.EventTypes
                        "a lifecycle denial is already described by the dispatch refusal row"
                })

            testCaseAsync
                "a forged signature DOES raise the tamper alert, with its evidence"
                (async {
                    let store = freshConsentStore ()
                    let audit = RecordingAuditLog()
                    let auditLog = Some(audit :> IAuditLog)

                    // Land a valid approval, then swap its signature under it —
                    // the shape of an operator (or a compromised credential)
                    // editing the stored artifact directly.
                    let good = approvedRecord "c1" None

                    do! store.Put good |> Async.Ignore

                    let forged = {
                        good with
                            Signature = { good.Signature with Value = "AAAA" }
                    }

                    do! store.Put forged |> Async.Ignore

                    let! resolved = resolveLive (Some store) verifier auditLog runNow now subject party

                    Expect.equal resolved (Error ConsentDenial.SignatureInvalid) "denied"

                    match audit.Recorded with
                    | [ _, GrantConsentVerificationDenied payload ] ->
                        Expect.equal payload.DenialCode "consent-signature-invalid" "names the ground"
                        Expect.equal payload.ConsentId "c1" "names the record"
                        Expect.equal payload.KeyId keyId "names the key presented"

                        Expect.equal
                            payload.DeclaredAlgorithm
                            "HmacSha256"
                            "and the declared algorithm, which is the downgrade evidence"
                    | other -> failtestf "expected exactly one GrantConsentVerificationDenied row, got %A" other
                })

            test "the trust/lifecycle split is a property of the denial, not of the call site" {
                // Pinned directly so a new denial arm has to make a
                // deliberate choice rather than inheriting one by omission.
                let trust = [
                    ConsentDenial.SubjectMismatch
                    ConsentDenial.PartyMismatch("a", "b")
                    ConsentDenial.UnknownKey "k"
                    ConsentDenial.AlgorithmNotAllowed "x"
                    ConsentDenial.SignatureInvalid
                ]

                let lifecycle = [
                    ConsentDenial.NoConsentStore
                    ConsentDenial.NoConsentRecord
                    ConsentDenial.NotApproved "proposed"
                    ConsentDenial.Expired now
                    ConsentDenial.Revoked
                    ConsentDenial.StoreUnavailable "boom"
                    ConsentDenial.NoVerifier
                ]

                for d in trust do
                    Expect.isTrue (ConsentDenial.isTrustFailure d) $"{ConsentDenial.code d} is a trust failure"

                for d in lifecycle do
                    Expect.isFalse (ConsentDenial.isTrustFailure d) $"{ConsentDenial.code d} is a lifecycle state"

                let codes = (trust @ lifecycle) |> List.map ConsentDenial.code

                Expect.equal
                    (List.distinct codes |> List.length)
                    (List.length codes)
                    "every denial code is distinct — an operator dashboards on these"
            }
        ]
    ]