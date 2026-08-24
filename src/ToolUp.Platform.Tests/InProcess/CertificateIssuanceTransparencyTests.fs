// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.CertificateIssuanceTransparencyTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.AuditSinks.ChainedLedger
open ToolUp.Platform.AuditSinks.LedgerChain
open ToolUp.ArtefactSigning
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts

// ─── Phase 685 — certificate issuance transparency ───────────────────────
//
// A certificate has always verified in the holder's hand and been
// enumerable by nobody. This pack probes the property that closes the gap:
// every issuance appends one identifier-only row, an inclusion check reads
// that log back with THREE distinct verdicts, and the enumeration surface
// answers "what has this deployment certified".
//
// Two things are probed rather than argued, because both are the kind of
// claim that quietly stops being true:
//
//   * **offline verification is untouched.** The whole value of a
//     certificate is that it outlives access to the issuer, so a check that
//     needed the log would have taken more than it gave. The probe issues
//     the SAME certificate with and without a log and compares the sealed
//     bytes, rather than asserting that verification still passes — which
//     it would even if the bytes had moved.
//   * **not-found and log-unverifiable are different answers.** A
//     deployment can make an inconvenient inclusion query fail by breaking
//     its own ledger; if that read as "not issued", tampering would look
//     like evidence against the certificate. The 658 probe below holds both
//     ends: the chain verifier positions the drop, and the integrity-gated
//     log refuses to answer at all.

/// Minimal in-memory `ISecretStore` — the signer auto-provisions its key
/// into it and the verifier resolves the public half back out.
type private InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
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
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

/// An `IAuditLog` that honours BOTH the scope and the event-type filter.
///
/// A fake that ignored the filters would let the scope-isolation probe pass
/// against a read path that does not isolate, which is the one thing that
/// probe exists to establish.
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
                |> List.rev // most recent first, matching IEventStore.ReadAll
        }

let private scopeA = "team-issuer"
let private scopeB = "team-other"

let private jsonOptions =
    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draft metric inputHash value : FactDraft = {
    Subject = {
        Hierarchy = "geography"
        Path = [ "uk" ]
    }
    Metric = MetricRef metric
    Value = Scalar value
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ inputHash ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Disclosure.Surfaceable
}

/// A frozen clock, so two issuances of the same subject produce
/// byte-identical bodies and the byte-for-byte probe is measuring the
/// audit log rather than the wall clock.
let private frozen () =
    DateTime(2026, 8, 23, 10, 30, 0, DateTimeKind.Utc)

let private keyId = "grounding-v1"

/// The collaborators an issuer needs, over a fresh store.
let private substrate () =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage.InMemoryBlobStorage()) events
    let gate = FactDisclosureGate.create store events
    let lineage = LineageStore.EventStoreLineageStore(events) :> ILineageStore

    let graph =
        ProvenanceGraph.createWithFacts lineage (FactStoreEvidenceSource.create store)

    graph, store, gate, events

let private seed (store: IFactStore) scopeId metric inputHash value = async {
    match! store.Assert(scopeId, draft metric inputHash value) with
    | Ok(f: Fact) -> return f.FactId
    | Error e -> return failtestf "seeding the fact store failed: %s" e
}

let private signerOver (secrets: ISecretStore) =
    DefaultArtefactSigner.createSystem secrets (AuditLog.NoOpAuditLog() :> IAuditLog) keyId Ed25519

let private issued =
    function
    | Ok(c: GroundingCertificate) -> c
    | Error e -> failtestf "issue must succeed: %s" (CertificateError.describe e)

// ─── Chained-ledger plumbing (the Phase 658 half of the probe) ───────────

let private ledgerSettings: ChainedLedgerSettings = {
    Container = "audit-ledger"
    PathPrefix = Some "issuance"
}

let private newLedgerStorage () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-issuance-transparency", Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage

let private segmentName (storage: IBlobStorage) = async {
    let! names = storage.List(ledgerSettings.Container, "issuance/records/")

    return
        names
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> List.exactlyOne
}

let private readLines (storage: IBlobStorage) (name: string) = async {
    match! storage.Download(ledgerSettings.Container, name) with
    | Error message -> return failwithf "segment read failed: %s" message
    | Ok bytes ->
        return
            Encoding.UTF8.GetString bytes
            |> fun text -> text.Split '\n'
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.toList
}

/// Rewrite the ledger's single segment through `transform` — the same
/// storage-level perturbation shape the 658 pack uses, so the probe
/// exercises the surface the sink writes through.
let private perturb (storage: IBlobStorage) (transform: string list -> string list) = async {
    let! name = segmentName storage
    let! lines = readLines storage name

    match!
        storage.Upload(ledgerSettings.Container, name, Encoding.UTF8.GetBytes(String.Join("\n", transform lines)))
    with
    | Ok _ -> return ()
    | Error message -> return failwithf "segment write failed: %s" message
}

/// An `IAuditLog` reading events back OUT of the ledger's stored records —
/// so the inclusion check below is answered by whatever bytes survive a
/// perturbation, not by an in-memory list the tamper never touched.
type private LedgerBackedAuditLog(storage: IBlobStorage) =
    interface IAuditLog with
        member _.Record(_scopeId, _audit) = async { return () }

        member _.GetAuditTrail(scopeId, _dateRange, eventType) = async {
            let! name = segmentName storage
            let! lines = readLines storage name

            return
                lines
                |> List.map (fun line -> JsonSerializer.Deserialize<LedgerRecord>(line, jsonOptions))
                |> List.filter (fun r ->
                    r.ScopeId = scopeId
                    && (match eventType with
                        | None -> true
                        | Some wanted -> r.EventType = wanted))
                |> List.choose (fun r ->
                    match LedgerRecord.decodePayload<LedgerPayload> jsonOptions r with
                    | Ok payload -> Some payload.Event
                    | Error _ -> None)
                |> List.rev
        }

/// Deliver the recorded issuance rows into a real chained ledger.
let private writeToLedger (storage: IBlobStorage) (events: (string * AuditEvent) list) = async {
    let sink = create "issuance-ledger" ledgerSettings storage

    let batch =
        events
        |> List.mapi (fun i (scopeId, event) ->
            AuditEnvelope.fromScopeId
                scopeId
                (DateTime(2026, 8, 23, 10, 30, 0, DateTimeKind.Utc).AddSeconds(float i))
                event)

    match! sink.Deliver batch with
    | Ok() -> return ()
    | Error message -> return failwithf "ledger delivery failed: %s" message
}

let tests =
    testList "Phase 685 — certificate issuance transparency" [

        testCaseAsync "an issuance appends ONE identifier-only row — digest, subject, key id, and no body"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let signer = signerOver secrets
            let audit = RecordingAuditLog()
            let graph, store, gate, events = substrate ()

            let issuer =
                GroundingCertificate.createIssuerWithClockAudited
                    graph
                    store
                    gate
                    events
                    (Some signer)
                    (audit :> IAuditLog)
                    frozen

            let! factId = seed store scopeA "revenue" "h1" 100m
            let! result = issuer.Issue(scopeA, "auditor", FactCertificate factId, 5)
            let certificate = issued result

            let rows =
                audit.All
                |> List.choose (function
                    | AuditEvent.CertificateIssued p -> Some p
                    | _ -> None)

            let row =
                Expect.wantSome (List.tryExactlyOne rows) "exactly one issuance row is appended"

            Expect.equal
                row.Digest
                (GroundingCertificate.certificateDigest certificate.Body)
                "the row's digest is the one a holder recomputes from the bytes they hold"

            Expect.equal row.Subject factId "the row names the subject the certificate is rooted at"
            Expect.equal row.KeyId keyId "and the key the seal was made under"
            Expect.equal row.Seal GroundingCertificate.DetachedJwsSeal "the direct path is recorded as such"
            Expect.equal row.Format GroundingCertificate.Format "and the interchange format version"

            Expect.equal
                row.OccurredAt
                certificate.Body.IssuedAt
                "the row carries the CERTIFICATE's stamp, not a second clock"

            // No body. The certificate's chain went through the disclosure
            // predicate at the export surface; the audit row is a different
            // surface, and copying any of that content onto it would move
            // it somewhere the predicate never ran.
            let serialised = JsonSerializer.Serialize(row, jsonOptions)

            Expect.isFalse (serialised.Contains "rollup") "the row carries no method identity from the chain"
            Expect.isFalse (serialised.Contains "Fact") "nor any node kind — there are no nodes on it at all"
            Expect.isFalse (serialised.Contains "geography") "nor any subject-hierarchy content"
        }

        testCaseAsync "the issuance is found on the log — and a certificate from elsewhere is not"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let signer = signerOver secrets
            let audit = RecordingAuditLog()
            let graph, store, gate, events = substrate ()

            let issuer =
                GroundingCertificate.createIssuerAudited graph store gate events (Some signer) audit

            let! factId = seed store scopeA "revenue" "h1" 100m
            let! result = issuer.Issue(scopeA, "auditor", FactCertificate factId, 5)
            let certificate = issued result

            let log = GroundingCertificate.auditTrailLog audit

            match! GroundingCertificate.checkInclusion log scopeA certificate with
            | CertificateIncluded issuance ->
                Expect.equal
                    issuance.Digest
                    (GroundingCertificate.certificateDigest certificate.Body)
                    "the included row is the certificate's own"

                Expect.equal issuance.Subject factId "and names its subject"
            | verdict -> failtestf "expected inclusion, got: %s" (CertificateInclusionVerdict.describe verdict)

            // The same certificate against a deployment that never issued
            // it. This is the probe's own falsifier — an inclusion check
            // that cannot report absence proves nothing about presence.
            let elsewhere = GroundingCertificate.auditTrailLog (RecordingAuditLog())

            match! GroundingCertificate.checkInclusion elsewhere scopeA certificate with
            | CertificateNotIssued -> ()
            | verdict ->
                failtestf
                    "a foreign certificate must be not-issued, got: %s"
                    (CertificateInclusionVerdict.describe verdict)
        }

        testCaseAsync "offline verification is byte-for-byte unaffected and needs no log at all"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let signer = signerOver secrets
            let verifier = DefaultArtefactVerifier.create secrets

            // Two issuers over two identically-seeded stores, one logging
            // and one not, on the same frozen clock.
            let issueWith (audit: IAuditLog option) = async {
                let graph, store, gate, events = substrate ()

                let issuer =
                    match audit with
                    | Some a ->
                        GroundingCertificate.createIssuerWithClockAudited graph store gate events (Some signer) a frozen
                    | None -> GroundingCertificate.createIssuerWithClock graph store gate events (Some signer) frozen

                let! factId = seed store scopeA "revenue" "h1" 100m
                let! result = issuer.Issue(scopeA, "auditor", FactCertificate factId, 5)
                return issued result
            }

            let audit = RecordingAuditLog()
            let! logged = issueWith (Some(audit :> IAuditLog))
            let! plain = issueWith None

            Expect.equal
                (GroundingCertificate.canonicalBytes logged.Body)
                (GroundingCertificate.canonicalBytes plain.Body)
                "the sealed bytes are identical whether or not an issuance log is composed"

            // And the plain path still verifies with nothing but the
            // certificate and a public key — no log reachable, none needed.
            match! GroundingCertificate.verify verifier plain with
            | Ok() -> ()
            | Error e -> failtestf "offline verification must not need the log: %s" (VerificationError.describe e)

            Expect.isEmpty
                (audit.All
                 |> List.filter (fun e -> AuditEvent.eventTypeName e = "CertificateIssued")
                 |> List.skip 1)
                "the unlogged issuer recorded nothing on the logged issuer's trail"
        }

        testCaseAsync "the enumeration surface lists what was issued, scope-filtered by the audit read path"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let signer = signerOver secrets
            let audit = RecordingAuditLog()
            let graph, store, gate, events = substrate ()

            let issuer =
                GroundingCertificate.createIssuerAudited graph store gate events (Some signer) audit

            let! a1 = seed store scopeA "revenue" "h1" 100m
            let! a2 = seed store scopeA "margin" "h2" 42m
            let! b1 = seed store scopeB "revenue" "h3" 7m

            let! _ = issuer.Issue(scopeA, "auditor", FactCertificate a1, 5)
            let! _ = issuer.Issue(scopeA, "auditor", FactCertificate a2, 5)
            let! _ = issuer.Issue(scopeB, "auditor", FactCertificate b1, 5)

            let log = GroundingCertificate.auditTrailLog audit

            match! GroundingCertificate.listIssued log scopeA with
            | Error reason -> failtestf "enumeration must succeed: %s" reason
            | Ok issuances ->
                Expect.hasLength issuances 2 "both scope-A issuances are enumerable"

                Expect.sequenceEqual
                    (issuances |> List.map _.Subject |> List.sort)
                    (List.sort [ a1; a2 ])
                    "and are the two this scope issued"

                Expect.isFalse
                    (issuances |> List.exists (fun i -> i.Subject = b1))
                    "the other scope's issuance is not visible here (GP 4)"

            match! GroundingCertificate.listIssued log scopeB with
            | Error reason -> failtestf "enumeration must succeed: %s" reason
            | Ok issuances ->
                let only =
                    Expect.wantSome (List.tryExactlyOne issuances) "scope B issued exactly one"

                Expect.equal only.Subject b1 "and it is that scope's own"
        }

        testCaseAsync "a log whose integrity cannot be established refuses to answer, rather than reporting not-issued"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let signer = signerOver secrets
            let audit = RecordingAuditLog()
            let graph, store, gate, events = substrate ()

            let issuer =
                GroundingCertificate.createIssuerAudited graph store gate events (Some signer) audit

            let! factId = seed store scopeA "revenue" "h1" 100m
            let! result = issuer.Issue(scopeA, "auditor", FactCertificate factId, 5)
            let certificate = issued result

            // Precondition — the SAME digest against the SAME rows is
            // included when integrity holds. Without this the verdict below
            // could be produced by a log that simply has nothing in it.
            let sound =
                GroundingCertificate.auditTrailLogWithIntegrity audit (fun () -> async { return Ok() })

            match! GroundingCertificate.checkInclusion sound scopeA certificate with
            | CertificateIncluded _ -> ()
            | verdict ->
                failtestf "precondition: expected inclusion, got %s" (CertificateInclusionVerdict.describe verdict)

            let broken =
                GroundingCertificate.auditTrailLogWithIntegrity audit (fun () -> async {
                    return Error "chain broken at record 3"
                })

            match! GroundingCertificate.checkInclusion broken scopeA certificate with
            | IssuanceLogUnverifiable reason ->
                Expect.stringContains reason "record 3" "the verdict carries the ledger's own reason"
            | verdict ->
                failtestf
                    "a broken log must not be reported as evidence about the certificate, got: %s"
                    (CertificateInclusionVerdict.describe verdict)

            // And enumeration refuses on the same terms — an empty list
            // from a broken log would read as "this deployment has issued
            // nothing".
            match! GroundingCertificate.listIssued broken scopeA with
            | Error _ -> ()
            | Ok issuances -> failtestf "enumeration over a broken log must refuse, got %d rows" (List.length issuances)
        }

        testCaseAsync "PROBE: an issuance dropped from a chained ledger reports not-issued, against a chain 658 flags"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let signer = signerOver secrets
            let audit = RecordingAuditLog()
            let graph, store, gate, events = substrate ()

            let issuer =
                GroundingCertificate.createIssuerAudited graph store gate events (Some signer) audit

            let! keptId = seed store scopeA "revenue" "h1" 100m
            let! droppedId = seed store scopeA "margin" "h2" 42m
            let! laterId = seed store scopeA "volume" "h3" 7m

            let! keptResult = issuer.Issue(scopeA, "auditor", FactCertificate keptId, 5)
            let! droppedResult = issuer.Issue(scopeA, "auditor", FactCertificate droppedId, 5)
            let! _ = issuer.Issue(scopeA, "auditor", FactCertificate laterId, 5)
            let kept = issued keptResult
            let dropped = issued droppedResult

            let droppedDigest = GroundingCertificate.certificateDigest dropped.Body

            // All three issuances into a real chained ledger.
            //
            // The suppressed row is deliberately a MIDDLE one. Removing the
            // last record leaves the chain short of its own head pointer,
            // which the 658 verifier reports as an untrusted head — a real
            // finding, but a different one, and it would let this probe
            // claim the drop class without ever exercising it.
            let storage = newLedgerStorage ()
            let! trail = (audit :> IAuditLog).GetAuditTrail(scopeA, None, Some "CertificateIssued")
            do! writeToLedger storage (trail |> List.rev |> List.map (fun e -> scopeA, e))

            let ledgerLog =
                GroundingCertificate.auditTrailLog (LedgerBackedAuditLog(storage) :> IAuditLog)

            // Precondition: the ledger verifies and the target IS included.
            // A tamper probe whose "before" state was already wrong proves
            // nothing about the perturbation.
            match! verify ledgerSettings storage None with
            | Ok(LedgerVerified(count, _, _)) -> Expect.equal count 3L "all three issuance rows are on the chain"
            | Ok other -> failtestf "precondition: expected a verified ledger, got %A" other
            | Error error -> failtestf "precondition: ledger unreadable — %s" error

            match! GroundingCertificate.checkInclusion ledgerLog scopeA dropped with
            | CertificateIncluded _ -> ()
            | verdict ->
                failtestf
                    "precondition: the issuance must be included before the drop, got %s"
                    (CertificateInclusionVerdict.describe verdict)

            // Suppress the second issuance — exactly the act the log exists
            // to make visible.
            do! perturb storage (List.mapi (fun i line -> i, line) >> List.filter (fst >> (<>) 1) >> List.map snd)

            // (a) The 658 verifier positions it as a dropped record.
            match! verify ledgerSettings storage None with
            | Ok(LedgerBroken breakage) ->
                Expect.equal breakage.Kind DroppedRecord "the chain verifier names the suppression as a dropped record"
                Expect.equal breakage.Position 1L "and positions it where the issuance should have been"
            | Ok other -> failtestf "the suppression must break the chain, got %A" other
            | Error error -> failtestf "ledger unreadable — %s" error

            // (b) Reading the surviving rows, the suppressed certificate is
            // not on the log — while the one that was left alone still is.
            match! GroundingCertificate.checkInclusionOfDigest ledgerLog scopeA droppedDigest with
            | CertificateNotIssued -> ()
            | verdict ->
                failtestf
                    "the suppressed issuance must read as not-issued, got %s"
                    (CertificateInclusionVerdict.describe verdict)

            match! GroundingCertificate.checkInclusion ledgerLog scopeA kept with
            | CertificateIncluded _ -> ()
            | verdict ->
                failtestf
                    "the surviving issuance must still be included, got %s"
                    (CertificateInclusionVerdict.describe verdict)

            // (c) Which is why the two halves belong together: gated on the
            // chain's own verdict, the log declines to answer at all — the
            // suppression is reported against the LEDGER, not against the
            // certificate.
            let gated =
                GroundingCertificate.auditTrailLogWithIntegrity (LedgerBackedAuditLog(storage) :> IAuditLog) (fun () -> async {
                    match! verify ledgerSettings storage None with
                    | Ok(LedgerVerified _) -> return Ok()
                    | Ok(LedgerHeadUntrusted(_, _, signature)) ->
                        return Error(sprintf "ledger head untrusted: %A" signature)
                    | Ok(LedgerBroken breakage) ->
                        return Error(sprintf "%A at position %d" breakage.Kind breakage.Position)
                    | Error error -> return Error error
                })

            match! GroundingCertificate.checkInclusionOfDigest gated scopeA droppedDigest with
            | IssuanceLogUnverifiable reason ->
                Expect.stringContains reason "DroppedRecord" "the refusal carries the chain verifier's own class"
            | verdict ->
                failtestf
                    "against a flagged chain the log must refuse, not testify: %s"
                    (CertificateInclusionVerdict.describe verdict)
        }

        testCaseAsync "the attested issue path logs too — the log does not depend on which seal was used"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore

            let signer =
                ApplicationSigning.inProcess secrets (AuditLog.NoOpAuditLog() :> IAuditLog) keyId EcdsaP256 "system"
                |> ApplicationSigning.create

            let audit = RecordingAuditLog()
            let graph, store, gate, events = substrate ()

            let issuer =
                GroundingCertificate.createAttestedIssuerAudited graph store gate events (Some signer) audit

            let! factId = seed store scopeA "revenue" "h1" 100m

            match! issuer.Issue(scopeA, "auditor", FactCertificate factId, 5) with
            | Error e -> failtestf "attested issue must succeed: %s" (CertificateError.describe e)
            | Ok certificate ->
                let log = GroundingCertificate.auditTrailLog audit

                match! GroundingCertificate.checkInclusionAttested log scopeA certificate with
                | CertificateIncluded issuance ->
                    Expect.equal
                        issuance.Seal
                        GroundingCertificate.ApplicationSeal
                        "the row records which seal produced it"

                    Expect.equal
                        issuance.Digest
                        (GroundingCertificate.certificateDigest certificate.Body)
                        "over the same canonical body both paths seal, so one log serves both"
                | verdict ->
                    failtestf "the attested issuance must be logged: %s" (CertificateInclusionVerdict.describe verdict)
        }

        testCaseAsync "a refused issuance appends nothing — the log lists only documents that exist"
        <| async {
            let audit = RecordingAuditLog()
            let graph, store, gate, events = substrate ()

            // No signing substrate composed: issuance refuses (GP 13).
            let issuer =
                GroundingCertificate.createIssuerAudited graph store gate events None audit

            let! factId = seed store scopeA "revenue" "h1" 100m

            match! issuer.Issue(scopeA, "auditor", FactCertificate factId, 5) with
            | Ok _ -> failtest "issuance must refuse with no signer"
            | Error SigningUnavailable -> ()
            | Error e -> failtestf "unexpected refusal: %s" (CertificateError.describe e)

            Expect.isEmpty
                (audit.All
                 |> List.filter (fun e -> AuditEvent.eventTypeName e = "CertificateIssued"))
                "a certificate that was never sealed is not on the issuance log"
        }
    ]