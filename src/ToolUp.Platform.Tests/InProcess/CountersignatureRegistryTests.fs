module ToolUp.Platform.Tests.InProcess.CountersignatureRegistryTests

open System
open System.Security.Cryptography
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.InterPlatform
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 676 — the N-party countersignature registry ───────────────
//
// Phase 480 proved a bilateral approval binds to content. This pack is
// about the two axes that phase pinned and this one opens: the subject
// (any content-hashed artefact, not one template type) and the party
// count (a roster, not a pair). Six kinds of case, in the order they
// carry weight:
//
//   1. **The mutation probe.** An approval of subject S must be worth
//      nothing for S′. Measured on the hash itself, on the evaluation,
//      and through a live store.
//   2. **The roster probe, which is the NEW one.** Approvals gathered
//      from {A,B} must be worth nothing for {A,B,C}. Without this a
//      party could be added to an agreement it never saw and the
//      agreement would read as complete — the same defect as an edit
//      keeping its approval, one level up.
//   3. **Completeness is EVERY party.** At N=3, two approvals are not
//      an approval. This is the case a bilateral evaluator cannot even
//      express, so it is the one that says the generalisation is real.
//   4. **The signature is real, and it binds.** Records are signed with
//      genuine P-256 keys; a record edited after signing is refused and
//      NOT stored. Each refusal is paired with a control showing the
//      unedited record is accepted, so "everything is refused" cannot
//      pass as a fix.
//   5. **Revocation takes effect on the next evaluation, and deletes
//      nothing.** The withdrawn approval is still readable afterwards —
//      the trail is the artefact.
//   6. **N=2 parity (GP 11).** The bilateral surface's answer and the
//      generic answer are compared over a scenario matrix, so the
//      claim that Phase 480 is this evaluation at N=2 is measured
//      rather than asserted.

// ─── Real key material ───────────────────────────────────────────────
//
// Genuine P-256 keys, not a stub: the claim under test is that a
// countersignature is a signature over the record's canonical bytes, and
// a stub signer returning "ok" would let every case here pass against a
// registry that checked nothing cryptographic at all.

[<Literal>]
let private partyA = "party-alpha"

[<Literal>]
let private partyB = "party-beta"

[<Literal>]
let private partyC = "party-gamma"

/// A party with no key material at all — the "cannot sign" arm.
[<Literal>]
let private partyKeyless = "party-keyless"

/// An ES256 signer over an in-memory key set. Deliberately the whole of
/// the crypto in this pack: everything else is the registry's own
/// canonical encoding and its evaluation.
type private TestSigner(parties: string list) =
    let keys =
        parties
        |> List.map (fun partyId ->
            let ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            partyId, ec)
        |> dict

    interface ICountersignatureSigner with
        member _.Sign(partyId, message) = async {
            match keys.TryGetValue partyId with
            | true, ec -> return Ok(Convert.ToBase64String(ec.SignData(message, HashAlgorithmName.SHA256)))
            | false, _ -> return Error $"no signing key for party '{partyId}'"
        }

        member _.Verify(partyId, message, signature) = async {
            match keys.TryGetValue partyId with
            | false, _ -> return Error $"no public key for party '{partyId}'"
            | true, ec ->
                try
                    if ec.VerifyData(message, Convert.FromBase64String signature, HashAlgorithmName.SHA256) then
                        return Ok()
                    else
                        return Error "signature did not verify"
                with _ ->
                    return Error "signature is not well formed"
        }

// ─── Subjects ────────────────────────────────────────────────────────

[<Literal>]
let private headKind = "composition-head"

let private payload (text: string) = Encoding.UTF8.GetBytes text

/// The subject under agreement.
let private headV1 =
    CountersignatureSubject.ofCanonicalBytes headKind "release-head" (payload "manifest-v1")

/// The edit. Same kind, same id, different content — the change a party
/// would make after gathering approvals, and the one an approval naming
/// only the id would not catch.
let private headV2 =
    CountersignatureSubject.ofCanonicalBytes headKind "release-head" (payload "manifest-v2")

let private skew = Countersignature.defaultSkew
let private now = DateTimeOffset.FromUnixTimeSeconds 1_800_000_000L

let private freshRegistry parties : ICountersignatureRegistry =
    BlobCountersignatureRegistry(InMemoryBlobStorage() :> IBlobStorage, TestSigner(parties))
    :> ICountersignatureRegistry

let private request roster acting action subject : CountersignatureRequest = {
    Subject = subject
    Roster = roster
    ActingPartyId = acting
    Action = action
    NotBefore = None
    ExpiresAt = None
}

let private issue (registry: ICountersignatureRegistry) req =
    match registry.Issue req |> Async.RunSynchronously with
    | Ok record -> record
    | Error e -> failtestf "Issuing a record must succeed in a fixture, got %A" e

let private accept (registry: ICountersignatureRegistry) record =
    match registry.Accept record |> Async.RunSynchronously with
    | Ok() -> ()
    | Error e -> failtestf "Accepting a well-signed record must succeed in a fixture, got %A" e

let private recordsOf (registry: ICountersignatureRegistry) (subject: CountersignatureSubject) =
    registry.Records(Some(subject.Kind, subject.SubjectId))
    |> Async.RunSynchronously

/// A hand-built record, for the evaluation cases that have no business
/// touching a store or a key. `Signature` plays no part in
/// `Countersignature.status`, which is the point of keeping the decision
/// out of the store.
let private unsignedRecord roster acting action subject issuedAt : CountersignatureRecord = {
    Subject = subject
    Roster = Countersignature.roster roster
    ActingPartyId = acting
    Action = action
    IssuedAt = issuedAt
    NotBefore = issuedAt
    ExpiresAt = None
    Signature = "evaluation-does-not-read-this"
}

let private approvalsFrom roster subject =
    roster
    |> List.map (fun partyId -> unsignedRecord roster partyId SubjectApproved subject now)

// ─── 1. Subjects are content-addressed, kind-tagged and id-tagged ────

let subjectTests =
    testList "Phase 676 - a subject's hash IS its content" [
        test "an edit produces a different hash" {
            Expect.notEqual
                headV1.ContentHash
                headV2.ContentHash
                "editing the content must change the hash — this is the whole invalidation mechanism, not a nicety"
        }

        test "the same content under a different kind hashes differently" {
            let asManifest =
                CountersignatureSubject.ofCanonicalBytes "capability-manifest" "release-head" (payload "manifest-v1")

            Expect.notEqual
                headV1.ContentHash
                asManifest.ContentHash
                "the kind tag is inside the hashed bytes, so one artefact registered under two kinds must not share an approval"
        }

        test "the same content under a different id hashes differently" {
            let other =
                CountersignatureSubject.ofCanonicalBytes headKind "other-head" (payload "manifest-v1")

            Expect.notEqual headV1.ContentHash other.ContentHash "the subject id is inside the hashed bytes"
        }

        test "the hash is stable across recomputation" {
            let again =
                CountersignatureSubject.ofCanonicalBytes headKind "release-head" (payload "manifest-v1")

            Expect.equal
                again
                headV1
                "the encoder must be deterministic — an approval is worthless if re-deriving the subject moves the hash"
        }

        test "the digest is named in the value" {
            Expect.stringStarts
                headV1.ContentHash
                "sha256:"
                "the algorithm is named in the hash so a future digest change is a visible discontinuity"
        }

        // The length prefix's job: a value cannot impersonate a field
        // boundary. Without it, a crafted id could collide with a
        // different (kind, id) pair over shifted content.
        test "a delimiter-bearing id cannot shift a field boundary" {
            let crafted =
                CountersignatureSubject.ofCanonicalBytes headKind "release\n11:head" (payload "manifest-v1")

            Expect.notEqual
                crafted.ContentHash
                headV1.ContentHash
                "a length-prefixed encoding must not be forgeable by embedding the delimiter in a value"
        }

        test "a roster is canonicalised, so order and duplicates do not make new agreements" {
            Expect.equal
                (Countersignature.roster [ partyB; partyA; partyA ])
                (Countersignature.roster [ partyA; partyB ])
                "the same parties listed differently are one agreement"
        }
    ]

// ─── 2. Completeness is EVERY enrolled party ─────────────────────────

let completenessTests =
    testList "Phase 676 - approval is live only under the whole roster" [
        test "three parties, three approvals, countersigned" {
            let roster = [ partyA; partyB; partyC ]

            match Countersignature.status skew roster headV1 now (approvalsFrom roster headV1) with
            | Countersigned _ -> ()
            | other -> failtestf "a fully-approved roster must be countersigned, got %A" other
        }

        // The case a bilateral evaluator cannot express at all.
        test "three parties, two approvals, NOT countersigned" {
            let roster = [ partyA; partyB; partyC ]

            let records = [
                unsignedRecord roster partyA SubjectApproved headV1 now
                unsignedRecord roster partyB SubjectApproved headV1 now
            ]

            match Countersignature.status skew roster headV1 now records with
            | CountersignaturePending [ awaiting ] ->
                Expect.equal awaiting partyC "the one party that has not approved is named"
            | other -> failtestf "two of three approvals must not approve, got %A" other
        }

        test "a proposal and a review confer no permission" {
            let roster = [ partyA; partyB ]

            for action in [ SubjectProposed; SubjectReviewed ] do
                let records = [
                    unsignedRecord roster partyA SubjectApproved headV1 now
                    unsignedRecord roster partyB action headV1 now
                ]

                match Countersignature.status skew roster headV1 now records with
                | CountersignaturePending [ awaiting ] ->
                    Expect.equal awaiting partyB $"%A{action} is a recorded step, not an agreement"
                | other -> failtestf "%A must confer no permission, got %A" action other
        }

        test "effectiveFrom is the LATEST party's start, not the earliest" {
            let roster = [ partyA; partyB; partyC ]
            let later = now.AddHours 4.0

            let records = [
                unsignedRecord roster partyA SubjectApproved headV1 now
                unsignedRecord roster partyB SubjectApproved headV1 now
                unsignedRecord roster partyC SubjectApproved headV1 later
            ]

            match Countersignature.status skew roster (headV1) (later.AddMinutes 1.0) records with
            | Countersigned from ->
                Expect.equal from later "the agreement became complete when the LAST party joined it"
            | other -> failtestf "expected a countersigned status, got %A" other
        }

        // "Everyone has agreed" must not be satisfiable by there being
        // no one. A fail-open here would make an unrostered subject
        // universally approved.
        test "an empty roster is pending, never approved" {
            match Countersignature.status skew [] headV1 now [] with
            | CountersignaturePending [] -> ()
            | other -> failtestf "an empty roster must fail closed, got %A" other
        }

        test "an approval not yet in force is pending, not expired" {
            let roster = [ partyA; partyB ]

            let records = [
                unsignedRecord roster partyA SubjectApproved headV1 now
                {
                    unsignedRecord roster partyB SubjectApproved headV1 now with
                        NotBefore = now.AddDays 7.0
                }
            ]

            match Countersignature.status skew roster headV1 now records with
            | CountersignaturePending [ awaiting ] ->
                Expect.equal awaiting partyB "nothing is wrong — the start date has not arrived"
            | other -> failtestf "a future start date must read as pending, got %A" other
        }
    ]

// ─── 3. Invalidation: content and roster ─────────────────────────────

let invalidationTests =
    testList "Phase 676 - what a change invalidates" [
        test "approvals of one version are worth nothing for an edit of it" {
            let roster = [ partyA; partyB; partyC ]

            match Countersignature.status skew roster headV2 now (approvalsFrom roster headV1) with
            | CountersignaturePending awaiting ->
                Expect.equal
                    (List.sort awaiting)
                    (List.sort roster)
                    "an edit is structurally unapproved — EVERY party's approval fails to carry over, not merely one"
            | other -> failtestf "an edited subject must not be approved, got %A" other
        }

        // The new axis. Adding a party must not silently inherit the
        // approvals the smaller roster gathered.
        test "adding a party re-opens approval" {
            let pair = [ partyA; partyB ]
            let trio = [ partyA; partyB; partyC ]

            match Countersignature.status skew trio headV1 now (approvalsFrom pair headV1) with
            | CountersignaturePending awaiting ->
                Expect.equal
                    (List.sort awaiting)
                    (List.sort trio)
                    "a record signed under one roster is not evidence about a different agreement — including for the parties common to both"
            | other -> failtestf "a roster change must re-open approval, got %A" other
        }

        test "removing a party re-opens approval too" {
            let pair = [ partyA; partyB ]
            let trio = [ partyA; partyB; partyC ]

            match Countersignature.status skew pair headV1 now (approvalsFrom trio headV1) with
            | CountersignaturePending awaiting ->
                Expect.equal
                    (List.sort awaiting)
                    (List.sort pair)
                    "the roster is part of what was agreed, in both directions"
            | other -> failtestf "narrowing a roster must re-open approval, got %A" other
        }

        // The control for the two cases above: the identical scenario
        // with the roster left alone releases. Without it, "the roster
        // change was refused" would pass equally against an evaluator
        // that had broken and started refusing everything.
        test "control - the same records under their own roster DO approve" {
            let pair = [ partyA; partyB ]

            match Countersignature.status skew pair headV1 now (approvalsFrom pair headV1) with
            | Countersigned _ -> ()
            | other -> failtestf "the unchanged agreement must still be countersigned, got %A" other
        }

        test "another agreement's records are not ours" {
            let ours = [ partyA; partyB ]
            let theirs = [ partyB; partyC ]

            match Countersignature.status skew ours headV1 now (approvalsFrom theirs headV1) with
            | CountersignaturePending awaiting ->
                Expect.equal
                    (List.sort awaiting)
                    (List.sort ours)
                    "a disjoint federation's approvals are not evidence here"
            | other -> failtestf "expected pending, got %A" other
        }
    ]

// ─── 4. Revocation and expiry, and the never-delete posture ──────────

let revocationTests =
    testList "Phase 676 - revocation is a record, and it is terminal" [
        test "a revocation from ANY party stops the agreement" {
            let roster = [ partyA; partyB; partyC ]

            let records =
                approvalsFrom roster headV1
                @ [ unsignedRecord roster partyC SubjectRevoked headV1 (now.AddHours 1.0) ]

            match Countersignature.status skew roster headV1 (now.AddHours 2.0) records with
            | CountersignatureRevoked(partyId, _) -> Expect.equal partyId partyC "the revoking party is named"
            | other -> failtestf "a revocation must stop the agreement, got %A" other
        }

        test "a same-second tie resolves towards revocation" {
            let roster = [ partyA; partyB ]

            let records = [
                unsignedRecord roster partyA SubjectApproved headV1 now
                unsignedRecord roster partyB SubjectApproved headV1 now
                unsignedRecord roster partyB SubjectRevoked headV1 now
            ]

            match Countersignature.status skew roster headV1 now records with
            | CountersignatureRevoked(partyId, _) ->
                Expect.equal
                    partyId
                    partyB
                    "two records stamped in the same second is exactly where failing closed matters"
            | other -> failtestf "a same-second tie must fail closed, got %A" other
        }

        test "a party may re-approve after revoking" {
            let roster = [ partyA; partyB ]

            let records = [
                unsignedRecord roster partyA SubjectApproved headV1 now
                unsignedRecord roster partyB SubjectRevoked headV1 now
                unsignedRecord roster partyB SubjectApproved headV1 (now.AddHours 1.0)
            ]

            match Countersignature.status skew roster headV1 (now.AddHours 2.0) records with
            | Countersigned _ -> ()
            | other ->
                failtestf
                    "evaluation reads each party's LATEST record — a revocation is not a permanent bar, got %A"
                    other
        }

        test "an expired approval is not a live one" {
            let roster = [ partyA; partyB ]

            let records = [
                unsignedRecord roster partyA SubjectApproved headV1 now
                {
                    unsignedRecord roster partyB SubjectApproved headV1 now with
                        ExpiresAt = Some(now.AddHours 1.0)
                }
            ]

            match Countersignature.status skew roster headV1 (now.AddHours 2.0) records with
            | CountersignatureExpired(partyId, _) -> Expect.equal partyId partyB "the expired party is named"
            | other -> failtestf "an expired approval must not be live, got %A" other
        }

        test "revocation beats expiry in precedence" {
            let roster = [ partyA; partyB ]

            let records = [
                {
                    unsignedRecord roster partyA SubjectApproved headV1 now with
                        ExpiresAt = Some(now.AddHours 1.0)
                }
                unsignedRecord roster partyB SubjectRevoked headV1 now
            ]

            match Countersignature.status skew roster headV1 (now.AddHours 2.0) records with
            | CountersignatureRevoked _ -> ()
            | other -> failtestf "the fail-closed order is revoked, then expired, then pending, got %A" other
        }

        // The trail is the artefact a regulated buyer asks for. A store
        // that forgot a withdrawn approval could not answer "who agreed,
        // and when did they stop".
        test "revoking deletes nothing - the withdrawn approval is still readable" {
            let registry = freshRegistry [ partyA; partyB ]
            let roster = [ partyA; partyB ]
            let approval = issue registry (request roster partyA SubjectApproved headV1)

            issue registry (request roster partyA SubjectRevoked headV1) |> ignore

            let held = recordsOf registry headV1

            Expect.equal (List.length held) 2 "both the approval and its revocation are held"

            Expect.isTrue
                (held
                 |> List.exists (fun r -> r.Signature = approval.Signature && r.Action = SubjectApproved))
                "the withdrawn approval is still in the trail — revocation is a record, not an erasure"
        }
    ]

// ─── 5. The signature is real, and it binds ──────────────────────────

let signatureTests =
    testList "Phase 676 - a record that is not signed is not a record" [
        test "a party with no signing material cannot issue" {
            let registry = freshRegistry [ partyA; partyB ]
            let roster = [ partyA; partyKeyless ]

            match
                registry.Issue(request roster partyKeyless SubjectApproved headV1)
                |> Async.RunSynchronously
            with
            | Error(CountersignatureUnsigned _) -> ()
            | other -> failtestf "an unsigned approval is not an approval, got %A" other
        }

        test "…and nothing was stored on the way out" {
            let registry = freshRegistry [ partyA; partyB ]
            let roster = [ partyA; partyKeyless ]

            registry.Issue(request roster partyKeyless SubjectApproved headV1)
            |> Async.RunSynchronously
            |> ignore

            Expect.isEmpty (recordsOf registry headV1) "a refused issue must leave no trace to be counted later"
        }

        test "control - a keyed party issues, and the record is stored" {
            let registry = freshRegistry [ partyA; partyB ]
            let roster = [ partyA; partyB ]
            issue registry (request roster partyA SubjectApproved headV1) |> ignore

            Expect.equal
                (List.length (recordsOf registry headV1))
                1
                "the control must actually store, or the case above proves nothing"
        }

        test "a record edited after signing is refused" {
            let registry = freshRegistry [ partyA; partyB ]
            let roster = [ partyA; partyB ]
            let record = issue registry (request roster partyA SubjectApproved headV1)
            let fresh = freshRegistry [ partyA; partyB ]

            // Every covered field, one at a time: the signature must
            // bind ALL of them, not merely the subject.
            let tampered = [
                "Subject", { record with Subject = headV2 }
                "Roster",
                {
                    record with
                        Roster = Countersignature.roster [ partyA; partyB; partyC ]
                }
                "Action", { record with Action = SubjectRevoked }
                "IssuedAt",
                {
                    record with
                        IssuedAt = record.IssuedAt.AddHours 1.0
                }
                "NotBefore",
                {
                    record with
                        NotBefore = record.NotBefore.AddHours 1.0
                }
                "ExpiresAt",
                {
                    record with
                        ExpiresAt = Some(record.IssuedAt.AddDays 1.0)
                }
            ]

            for label, edited in tampered do
                match fresh.Accept edited |> Async.RunSynchronously with
                | Error(CountersignatureUnverified _) -> ()
                | other -> failtestf "editing %s after signing must be refused, got %A" label other
        }

        test "control - the UNedited record is accepted" {
            let registry = freshRegistry [ partyA; partyB ]
            let roster = [ partyA; partyB ]
            let record = issue registry (request roster partyA SubjectApproved headV1)

            // A second registry over the same signer keys, standing in
            // for the other party's deployment receiving the record.
            match (freshRegistry [ partyA; partyB ]).Accept record |> Async.RunSynchronously with
            | Error(CountersignatureUnverified _) -> ()
            | Error e -> failtestf "the control must not fail for another reason, got %A" e
            | Ok() -> failtest "a record signed by a DIFFERENT key set must not verify here"

            // …and against its own registry, it does.
            accept registry record
        }

        test "a forged signature is refused" {
            let registry = freshRegistry [ partyA; partyB ]
            let roster = [ partyA; partyB ]
            let record = issue registry (request roster partyA SubjectApproved headV1)

            match
                registry.Accept {
                    record with
                        Signature = Convert.ToBase64String(payload "forged")
                }
                |> Async.RunSynchronously
            with
            | Error(CountersignatureUnverified _) -> ()
            | other -> failtestf "a forged signature must be refused, got %A" other
        }

        test "an off-roster actor is refused at issue and at accept" {
            let registry = freshRegistry [ partyA; partyB; partyC ]
            let roster = [ partyA; partyB ]

            match
                registry.Issue(request roster partyC SubjectApproved headV1)
                |> Async.RunSynchronously
            with
            | Error(CountersignatureRejected _) -> ()
            | other -> failtestf "a party may not sign into an agreement it is not on, got %A" other

            let onRoster =
                issue registry (request [ partyA; partyC ] partyC SubjectApproved headV1)

            match
                registry.Accept {
                    onRoster with
                        Roster = Countersignature.roster roster
                }
                |> Async.RunSynchronously
            with
            | Error _ -> ()
            | Ok() -> failtest "a record whose actor is off its own roster must not be admitted"
        }
    ]

// ─── 6. The store: content-addressed, idempotent, scoped ─────────────

let storeTests =
    testList "Phase 676 - the blob-backed default store" [
        test "re-accepting the same record is idempotent" {
            let registry = freshRegistry [ partyA; partyB ]
            let roster = [ partyA; partyB ]
            let record = issue registry (request roster partyA SubjectApproved headV1)

            for _ in 1..3 do
                accept registry record

            Expect.equal
                (List.length (recordsOf registry headV1))
                1
                "delivery is at-least-once wherever these travel; a retry must not read as a second approval"
        }

        test "records are scoped to their subject id" {
            let registry = freshRegistry [ partyA; partyB ]
            let roster = [ partyA; partyB ]

            let other =
                CountersignatureSubject.ofCanonicalBytes headKind "other-head" (payload "manifest-v1")

            issue registry (request roster partyA SubjectApproved headV1) |> ignore
            issue registry (request roster partyA SubjectApproved other) |> ignore

            Expect.equal (List.length (recordsOf registry headV1)) 1 "the decision-time read sees one subject"
            Expect.equal (List.length (recordsOf registry other)) 1 "…and so does the other"

            Expect.equal
                (List.length (registry.Records None |> Async.RunSynchronously))
                2
                "the queue read sees everything held"
        }

        test "a subject id that could escape its directory is refused" {
            let registry = freshRegistry [ partyA; partyB ]
            let roster = [ partyA; partyB ]

            let escaping =
                CountersignatureSubject.create headKind "../../etc/passwd" $"sha256:{String('0', 64)}"

            match
                registry.Issue(request roster partyA SubjectApproved escaping)
                |> Async.RunSynchronously
            with
            | Error(CountersignatureRejected _) -> ()
            | other -> failtestf "an id that would become a blob name outside the store must be refused, got %A" other
        }

        test "Status is derived from the records, not stored" {
            let registry = freshRegistry [ partyA; partyB ]
            let roster = [ partyA; partyB ]

            let pending =
                registry.Status(roster, headV1, now.AddYears 1) |> Async.RunSynchronously

            match pending with
            | CountersignaturePending _ -> ()
            | other -> failtestf "an empty store is pending, got %A" other

            issue registry (request roster partyA SubjectApproved headV1) |> ignore
            issue registry (request roster partyB SubjectApproved headV1) |> ignore

            match registry.Status(roster, headV1, DateTimeOffset.UtcNow) |> Async.RunSynchronously with
            | Countersigned _ -> ()
            | other -> failtestf "…and it becomes countersigned as the records land, got %A" other

            // The same records, asked about a roster that has since
            // grown: derived state moves, stored state would not.
            match
                registry.Status([ partyA; partyB; partyC ], headV1, DateTimeOffset.UtcNow)
                |> Async.RunSynchronously
            with
            | CountersignaturePending _ -> ()
            | other -> failtestf "a derived status must re-open on a roster change, got %A" other
        }

        test "a record's content address covers its signature" {
            let roster = Countersignature.roster [ partyA; partyB ]
            let record = unsignedRecord roster partyA SubjectApproved headV1 now

            Expect.notEqual
                (CountersignatureCanonical.recordId record)
                (CountersignatureCanonical.recordId { record with Signature = "different" })
                "two records differing only in signature must stay distinct in the store"
        }

        test "the two domain separators are distinct" {
            Expect.notEqual
                CountersignatureCanonical.subjectDomain
                CountersignatureCanonical.recordDomain
                "a signature over a subject encoding must not be replayable as a record encoding"
        }
    ]

// ─── 7. The queue projection (data, not a view) ──────────────────────

let queueTests =
    testList "Phase 676 - the queue an operator surface renders" [
        test "one entry per (subject, roster), naming every party's latest action" {
            let roster = Countersignature.roster [ partyA; partyB; partyC ]

            let records = [
                unsignedRecord roster partyA SubjectProposed headV1 now
                unsignedRecord roster partyA SubjectApproved headV1 (now.AddHours 1.0)
                unsignedRecord roster partyB SubjectReviewed headV1 now
            ]

            match CountersignatureQueue.project skew (now.AddHours 2.0) records with
            | [ entry ] ->
                Expect.equal entry.Roster roster "the entry carries the canonical roster"

                Expect.equal
                    entry.Actions
                    [ partyA, Some SubjectApproved; partyB, Some SubjectReviewed; partyC, None ]
                    "every enrolled party appears, silent ones named rather than omitted"

                match entry.Status with
                | CountersignaturePending awaiting ->
                    Expect.equal (List.sort awaiting) [ partyB; partyC ] "the two who have not agreed"
                | other -> failtestf "expected pending, got %A" other
            | other -> failtestf "expected exactly one queue entry, got %d" (List.length other)
        }

        test "one agreement per roster, so a roster change is visible as a separate row" {
            let pair = Countersignature.roster [ partyA; partyB ]
            let trio = Countersignature.roster [ partyA; partyB; partyC ]

            let records =
                unsignedRecord pair partyA SubjectApproved headV1 now
                :: (approvalsFrom trio headV1)

            let entries = CountersignatureQueue.project skew now records

            Expect.equal (List.length entries) 2 "the two agreements over one subject are two rows, not one merged one"

        }

        test "the projection is storage-order-independent" {
            let roster = Countersignature.roster [ partyA; partyB ]

            let records = [
                unsignedRecord roster partyA SubjectApproved headV2 now
                unsignedRecord roster partyB SubjectApproved headV1 now
                unsignedRecord roster partyA SubjectApproved headV1 now
            ]

            Expect.equal
                (CountersignatureQueue.project skew now records)
                (CountersignatureQueue.project skew now (List.rev records))
                "two machines holding the same records in a different order must render the same queue"
        }
    ]

// ─── 8. N=2 parity with Phase 480 (GP 11) ────────────────────────────
//
// The bilateral surface is now this evaluation with the roster pinned at
// two. That claim is measured here rather than asserted: the same
// scenario matrix is run through `TemplateApproval.status` and through
// `Countersignature.status` over the projection, and the two must agree
// case for case. Its complement — that the bilateral surface itself is
// unchanged — is the whole of the Phase 480 pack, which runs unmodified.

let private bilateralRecord acting counterparty action version issuedAt : TemplateApprovalRecord = {
    TemplateId = "reach"
    TemplateVersion = version
    ActingPeerId = acting
    CounterpartyPeerId = counterparty
    Action = action
    IssuedAt = issuedAt
    NotBefore = issuedAt
    ExpiresAt = None
    Signature = "evaluation-does-not-read-this"
}

/// The two vocabularies' verdicts, reduced to a comparable shape.
let private bilateralShape (status: BilateralApprovalStatus) =
    match status with
    | BilaterallyApproved from -> "approved", from.ToString "O", []
    | ApprovalPending awaiting -> "pending", "", List.sort awaiting
    | ApprovalRevoked(peerId, at) -> "revoked", at.ToString "O", [ peerId ]
    | ApprovalExpired(peerId, at) -> "expired", at.ToString "O", [ peerId ]

let private genericShape (status: CountersignatureStatus) =
    match status with
    | Countersigned from -> "approved", from.ToString "O", []
    | CountersignaturePending awaiting -> "pending", "", List.sort awaiting
    | CountersignatureRevoked(partyId, at) -> "revoked", at.ToString "O", [ partyId ]
    | CountersignatureExpired(partyId, at) -> "expired", at.ToString "O", [ partyId ]

[<Literal>]
let private versionA = "sha256:aaaa"

[<Literal>]
let private versionB = "sha256:bbbb"

let private scenarios: (string * TemplateApprovalRecord list) list = [
    "nothing recorded", []
    "only the local party approved", [ bilateralRecord partyA partyB TemplateApproved versionA now ]
    "only the remote party approved", [ bilateralRecord partyB partyA TemplateApproved versionA now ]
    "both approved",
    [
        bilateralRecord partyA partyB TemplateApproved versionA now
        bilateralRecord partyB partyA TemplateApproved versionA now
    ]
    "both approved, one later",
    [
        bilateralRecord partyA partyB TemplateApproved versionA now
        bilateralRecord partyB partyA TemplateApproved versionA (now.AddHours 3.0)
    ]
    "a review is not an approval",
    [
        bilateralRecord partyA partyB TemplateApproved versionA now
        bilateralRecord partyB partyA TemplateReviewed versionA now
    ]
    "a proposal is not an approval",
    [
        bilateralRecord partyA partyB TemplateProposed versionA now
        bilateralRecord partyB partyA TemplateApproved versionA now
    ]
    "the remote party revoked",
    [
        bilateralRecord partyA partyB TemplateApproved versionA now
        bilateralRecord partyB partyA TemplateApproved versionA now
        bilateralRecord partyB partyA TemplateRevoked versionA (now.AddHours 1.0)
    ]
    "the local party revoked",
    [
        bilateralRecord partyA partyB TemplateApproved versionA now
        bilateralRecord partyB partyA TemplateApproved versionA now
        bilateralRecord partyA partyB TemplateRevoked versionA (now.AddHours 1.0)
    ]
    "a same-second revocation tie",
    [
        bilateralRecord partyA partyB TemplateApproved versionA now
        bilateralRecord partyB partyA TemplateApproved versionA now
        bilateralRecord partyB partyA TemplateRevoked versionA now
    ]
    "an expired approval",
    [
        bilateralRecord partyA partyB TemplateApproved versionA now
        {
            bilateralRecord partyB partyA TemplateApproved versionA now with
                ExpiresAt = Some(now.AddHours 1.0)
        }
    ]
    "a not-yet-effective approval",
    [
        bilateralRecord partyA partyB TemplateApproved versionA now
        {
            bilateralRecord partyB partyA TemplateApproved versionA now with
                NotBefore = now.AddDays 7.0
        }
    ]
    "approvals for a DIFFERENT version",
    [
        bilateralRecord partyA partyB TemplateApproved versionB now
        bilateralRecord partyB partyA TemplateApproved versionB now
    ]
    "approvals naming a third party",
    [
        bilateralRecord partyA partyC TemplateApproved versionA now
        bilateralRecord partyC partyA TemplateApproved versionA now
    ]
    "a re-approval after revoking",
    [
        bilateralRecord partyA partyB TemplateApproved versionA now
        bilateralRecord partyB partyA TemplateRevoked versionA now
        bilateralRecord partyB partyA TemplateApproved versionA (now.AddHours 1.0)
    ]
]

let parityTests =
    testList "Phase 676 - the bilateral surface IS this evaluation at N=2" [
        for label, records in scenarios do
            test label {
                let asOf = now.AddHours 2.0

                let bilateral = TemplateApproval.status skew partyA partyB versionA asOf records

                let generic =
                    Countersignature.status
                        skew
                        [ partyA; partyB ]
                        (TemplateCountersignature.subject versionA)
                        asOf
                        (records |> List.map TemplateCountersignature.record)

                Expect.equal
                    (bilateralShape bilateral)
                    (genericShape generic)
                    "the bilateral evaluation and the N-party evaluation must agree — they are one implementation now, and a divergence here means the projection lost something"
            }

        // The matrix above compares two answers; this asserts the
        // matrix is not vacuous. Every status case must appear, or a
        // projection that collapsed everything to one verdict would
        // pass the whole list.
        test "the matrix exercises every status case" {
            let shapes =
                scenarios
                |> List.map (fun (_, records) ->
                    TemplateApproval.status skew partyA partyB versionA (now.AddHours 2.0) records
                    |> bilateralShape
                    |> fun (kind, _, _) -> kind)
                |> List.distinct
                |> List.sort

            Expect.equal
                shapes
                [ "approved"; "expired"; "pending"; "revoked" ]
                "a parity matrix that only ever produced one verdict would agree with anything"
        }

        test "the projection carries the roster, so a pair is one agreement however it is written" {
            let ab =
                TemplateCountersignature.record (bilateralRecord partyA partyB TemplateApproved versionA now)

            let ba =
                TemplateCountersignature.record (bilateralRecord partyB partyA TemplateApproved versionA now)

            Expect.equal
                ab.Roster
                ba.Roster
                "Phase 480's 'records must name both parties, in either role' filter IS the roster filter"
        }

        test "the signer bridge carries a bilateral signer into the generic seam" {
            // Not a re-test of the crypto — that is Phase 480's —
            // but of the adapter: a deployment that has established
            // key custody for its template approvals can register
            // the generic registry over the same keys.
            let bridged =
                TemplateCountersignature.signer
                    { new ITemplateApprovalSigner with
                        member _.Sign(peerId, _) = async { return Ok $"signed-by-{peerId}" }

                        member _.Verify(_, _, signature) = async {
                            return
                                if signature.StartsWith "signed-by-" then
                                    Ok()
                                else
                                    Error "no"
                        }
                    }

            Expect.equal
                (bridged.Sign(partyA, payload "x") |> Async.RunSynchronously)
                (Ok $"signed-by-{partyA}")
                "the bridge passes the party id and the bytes through unchanged"

            Expect.equal
                (bridged.Verify(partyA, payload "x", "forged") |> Async.RunSynchronously)
                (Error "no")
                "…and passes a refusal back unchanged"
        }
    ]