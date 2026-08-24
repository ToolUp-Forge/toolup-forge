// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.GroundedAnswerChainJoinTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AuditSinks.ChainedLedger
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.AI
open ToolUp.AI.AnswerVerifier
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.BuildTranscriptTests

// ─── Phase 680 — the grounded-answer chain join ──────────────────────
//
// The estate holds two verifiable chains and, until this phase, no walk
// between them. The serve-tier chain (boot verification into the
// hash-chained audit ledger) ends at "a runtime action happened"; the
// grounding chain (provenance traversal into signed certificates) ends
// at "this fact was produced this way". The answer-verification verdict
// is the one event that is both, so its audit row is where they meet.
//
// The load-bearing test here is the WALK, and it is deliberately a
// single end-to-end probe rather than four unit assertions that each
// prove a field is non-empty. A join is only worth the row it costs if
// every hop actually resolves, so this one starts at a served answer,
// reads its audit record back THROUGH the codec registry (so a missing
// codec entry fails here, not in production), resolves every fact id it
// names in a real fact store, matches its seal id against the sealed
// composition a real preflight affirmed, and verifies the ledger the
// row was replicated into. A hop that silently resolved to nothing
// would fail rather than pass quietly.
//
// The negative direction is probed too: a deployment composing no audit
// log records no row at all, and one composing no anchors records the
// row with both join fields honestly `None` rather than blank strings.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private scopeId = "team-chain-join"

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

// ─── The fact tier ───────────────────────────────────────────────────

let private draft (sku: string) (value: decimal) (inputHash: string) : FactDraft = {
    Subject = {
        Hierarchy = "product"
        Path = [ sku ]
    }
    Metric = MetricRef "revenue"
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

/// A real fact store with two asserted facts, and the ids they landed
/// under. The ids are content-addressed by the store — the test never
/// invents one, so the walk below resolves what the store actually holds.
let private factTier () =
    let store =
        BlobFactStore.create (InMemoryBlobStorage()) (InMemoryEventStore.InMemoryEventStore() :> IEventStore)

    let assertFact sku value hash =
        match store.Assert(scopeId, draft sku value hash) |> Async.RunSynchronously with
        | Ok fact -> fact
        | Error e -> failtestf "fixture could not assert a fact: %s" e

    let widgets = assertFact "widgets" 21800m "h-widgets"
    let gadgets = assertFact "gadgets" 14250m "h-gadgets"
    store, widgets, gadgets

/// Project a stored fact onto the retrieved-source shape the answer path
/// hands the verifier, carrying the store's own id.
let private factSource (fact: Fact) (rendering: string) : RetrievedSource = {
    DocumentId = ""
    DocumentName = ""
    Snippet = rendering
    Score = 1.0
    Origin = Fact
    LocationHint = None
    OriginalRef = None
    Scope = None
    ChunkId = None
    FactId = Some fact.FactId
    FactRendering = Some rendering
    FactFreshness = Some FactFresh
    FactSupersededBy = None
    Span = None
}

// ─── The boot tier ───────────────────────────────────────────────────

let private manifest: DeployManifest = {
    DeployManifest.empty with
        App = {
            Name = "Grounded"
            Slug = "grounded"
            Region = "eu-west"
        }
}

let private composition: CompositionManifest =
    CompositionManifest.build
        [ CompositionManifest.moduleEntry ("Reports", ComponentId.ofModule "reports") ]
        [ CompositionManifest.companionImplEntry "IAuditSink" "ledger" ]
        [ CompositionManifest.dataTypeEntry "sales" ] [] [ CompositionManifest.knob "AnswerGate" "Annotate" ]

let private sealer = StubSealer "chain-join-secret" :> IDeployRecordSealer

/// Seal a deploy record and a composition binding over `composition`, run
/// the preflight, and return the affirmative result with its binding.
///
/// Genuinely sealed and genuinely verified rather than a hand-built
/// literal: the seal id the audit row carries is only meaningful if a
/// preflight would in fact affirm the composition it names.
let private verifiedBoot () =
    let record =
        DeployRecord.create "deploy-1" "tenant-1" "build-1" manifest DeployProvenance.none

    let sealedRecord, sealedBinding =
        async {
            let! seal = sealer.Seal(DeployRecords.canonicalBytes record)

            let seal =
                match seal with
                | Ok s -> s
                | Error e -> failwithf "fixture could not seal the record: %s" e

            let! sealedBinding =
                BootVerificationPreflight.bindingFor record composition
                |> BootVerificationPreflight.sealBinding sealer

            match sealedBinding with
            | Ok b -> return { Record = record; Seal = seal }, b
            | Error e -> return failwithf "fixture could not seal the binding: %s" e
        }
        |> Async.RunSynchronously

    let options: BootVerificationOptions = {
        Profile = CompositionProfile.Standard
        Policy = BootVerificationPolicy.LogAndServe
        Sealer = sealer
        Locate = (fun _ -> None)
        Record = Some sealedRecord
        Binding = Some sealedBinding
        Transcript = None
        AuditLog = None
        ScopeId = BootVerificationPreflight.PlatformScopeId
    }

    match BootVerificationPreflight.run options composition |> Async.RunSynchronously with
    | Ok result -> result, sealedBinding
    | Error result -> failtestf "fixture boot did not verify: %A" result.Verdict

// ─── The serve tier ──────────────────────────────────────────────────

let private annotateGate =
    Some {
        Mode = AnswerGateAnnotate
        Verifier = NumericFidelityVerifier()
    }

/// An `IAuditLog` over its own event store, so the recorded row makes the
/// full serialise → persist → decode round trip the codec registry owns.
let private auditTier () =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    events, AuditLog.EventStoreAuditLog(events, silentLogger) :> IAuditLog

let private runStage (join: AnswerAuditJoin) (sources: RetrievedSource list) (answer: string) =
    runVerificationStageWithJoin
        annotateGate
        None
        sources
        answer
        None
        None
        join
        scopeId
        (Guid.NewGuid())
        (Guid.NewGuid())
        "prov"
        "model"
        silentLogger
    |> Async.RunSynchronously

/// The single answer-verification row on the trail, or a failure naming
/// what was there instead.
let private soleRow (auditLog: IAuditLog) =
    let trail = auditLog.GetAuditTrail(scopeId, None, None) |> Async.RunSynchronously

    match
        trail
        |> List.filter (function
            | AnswerVerificationPassed _
            | AnswerVerificationFlagged _ -> true
            | _ -> false)
    with
    | [ row ] -> row
    | other -> failtestf "expected exactly one answer-verification row, got %i: %A" (List.length other) other

let private payloadOf =
    function
    | AnswerVerificationPassed p
    | AnswerVerificationFlagged p -> p
    | other -> failtestf "not an answer-verification row: %A" other

// ─── The ledger ──────────────────────────────────────────────────────

let private ledgerSettings: ChainedLedgerSettings = {
    Container = "audit-ledger"
    PathPrefix = Some "chain-join"
}

let private newLedger () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-chain-join-tests", Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
    storage, create "ledger-chain-join" ledgerSettings storage

// ─── Tests ───────────────────────────────────────────────────────────

let tests =
    testList "Phase 680 grounded-answer chain join" [

        // ── The walk ──────────────────────────────────────────────────

        test "the full join: a served answer's audit row walks to its facts, its certificate, and its composition seal" {
            let store, widgets, gadgets = factTier ()
            let bootResult, sealedBinding = verifiedBoot ()
            let auditEvents, auditLog = auditTier ()

            // Anchors: the seal a real preflight affirmed, plus a
            // certificate ref a deployment holding the certificate
            // substrate would report. Composed over the boot-derived
            // anchors rather than replacing them, so the seal id under
            // test is still the one `fromBootVerification` computed.
            let bootAnchors =
                AnswerProvenanceAnchors.fromBootVerification bootResult (Some sealedBinding)

            let certificateRef = "grounding-certificate/v1:cert-abc123"

            let anchors =
                { new IAnswerProvenanceAnchors with
                    member _.CompositionSealId = bootAnchors.CompositionSealId

                    member _.TryCertificateRef(_, _, citedFactIds) = async {
                        // A certificate is only meaningful over a chain
                        // that exists; report none for an answer citing
                        // nothing rather than a ref covering nothing.
                        return
                            if List.isEmpty citedFactIds then
                                None
                            else
                                Some certificateRef
                    }
                }

            let sources = [ factSource widgets "£21,800"; factSource gadgets "£14,250" ]

            let answer, verdict =
                runStage
                    {
                        AuditLog = Some auditLog
                        Anchors = Some anchors
                    }
                    sources
                    "Widgets took £21,800 and gadgets £14,250 over the quarter."

            Expect.equal (verdict |> Option.map _.Unmatched) (Some 0) "both figures verified"
            Expect.stringContains answer "£21,800" "a fully verified answer is untouched"

            // ── Hop 0: the row exists, and decoded back through the
            //    codec registry. A missing codec entry fails HERE.
            let row = soleRow auditLog

            Expect.isTrue
                (match row with
                 | AnswerVerificationPassed _ -> true
                 | _ -> false)
                "a clean answer records the affirmative case, not the flagged one"

            let payload = payloadOf row
            Expect.equal payload.Verified 2 "two verified figures"
            Expect.equal payload.Unmatched 0 "no unmatched figures"
            Expect.equal payload.FactsInScope 2 "two facts were in scope"
            Expect.equal payload.Mode "Annotate" "the gate mode rides the row"

            // ── Hop 1: row → the facts it cites. Every id resolves in
            //    the store that minted it.
            Expect.equal
                payload.CitedFactIds
                (List.sort [ widgets.FactId; gadgets.FactId ])
                "the row names exactly the facts the figures matched"

            for factId in payload.CitedFactIds do
                match store.Get(scopeId, factId) |> Async.RunSynchronously with
                | Some fact -> Expect.equal fact.FactId factId "the cited id resolves to its own fact"
                | None -> failtestf "cited fact id %s does not resolve in the fact store" factId

            // ── Hop 2: the chain head is recomputable by anyone holding
            //    the ids — that is what makes it a join key.
            Expect.equal
                payload.ProvenanceChainHead
                (provenanceChainHead payload.CitedFactIds)
                "the chain head is the digest of the cited ids"

            Expect.isSome payload.ProvenanceChainHead "an answer citing facts has a chain head"

            // ── Hop 3: row → the certificate covering that chain.
            Expect.equal payload.CertificateRef (Some certificateRef) "the certificate ref rides the row"

            // ── Hop 4: row → the sealed composition this process
            //    affirmed at boot, and that binding still verifies.
            Expect.equal
                payload.CompositionSealId
                (Some sealedBinding.Binding.DeployRecordDigest)
                "the seal id names the sealed composition the preflight affirmed"

            Expect.equal
                (sealedBinding.Binding.DeployRecordDigest)
                (BootVerificationPreflight.bindingFor
                    (DeployRecord.create "deploy-1" "tenant-1" "build-1" manifest DeployProvenance.none)
                    composition)
                    .DeployRecordDigest
                "the seal id recomputes from the deploy record the binding names"

            // ── Hop 5: the row is chainable. Replicated into the
            //    hash-chained ledger, the ledger verifies.
            let storage, sink = newLedger ()

            let envelope = AuditEnvelope.fromScopeId scopeId DateTime.UtcNow row

            match sink.Deliver [ envelope ] |> Async.RunSynchronously with
            | Ok() -> ()
            | Error e -> failtestf "the ledger refused the answer-verification row: %s" e

            match verify ledgerSettings storage None |> Async.RunSynchronously with
            | Ok(LedgerVerified(count, _, _)) -> Expect.equal count 1L "the ledger holds exactly the delivered row"
            | other -> failtestf "the ledger did not verify after delivery: %A" other

            // The persisted `IEventStore` side of the audit log holds the
            // same one row — the projection the codec wrote is what the
            // decode above read, not a cached object.
            let persisted = auditEvents.ReadAll scopeId |> Async.RunSynchronously

            Expect.isTrue
                (persisted |> List.exists (fun e -> e.EventType = "AnswerVerificationPassed"))
                "the row persisted under its own wire event type"
        }

        // ── The refusal direction ─────────────────────────────────────

        test "an unverified number records the flagged case, with the token's fact-match status" {
            let store, widgets, _ = factTier ()
            ignore store
            let _, auditLog = auditTier ()

            let _, verdict =
                runStage (AnswerAuditJoin.auditOnly auditLog) [ factSource widgets "£21,800" ] "Revenue was £25,000."

            Expect.equal (verdict |> Option.map _.Unmatched) (Some 1) "the figure is unmatched"

            let row = soleRow auditLog

            Expect.isTrue
                (match row with
                 | AnswerVerificationFlagged _ -> true
                 | _ -> false)
                "an unmatched figure records the flagged case"

            let payload = payloadOf row
            Expect.equal payload.Unmatched 1 "one unmatched figure"
            Expect.equal payload.Verified 0 "nothing verified"
            Expect.equal payload.CitedFactIds [] "an answer that matched no fact cites none"
            Expect.isNone payload.ProvenanceChainHead "a chain head over nothing would name no chain"

            match payload.Tokens with
            | [ token ] ->
                Expect.equal token.Token "£25,000" "the flagged token rides the row verbatim"
                Expect.equal token.Verdict "unmatched" "with its fact-match status"
                Expect.isNone token.MatchedFactId "and no matched fact"
            | other -> failtestf "expected one token, got %A" other
        }

        test "a token with no facts in scope is unverifiable, not unmatched" {
            let _, auditLog = auditTier ()

            runStage (AnswerAuditJoin.auditOnly auditLog) [] "Revenue was £25,000."
            |> ignore

            let payload = payloadOf (soleRow auditLog)
            Expect.equal payload.Unverifiable 1 "the token could not be checked"
            Expect.equal payload.Unmatched 0 "which is not the same as unmatched"
            Expect.equal payload.FactsInScope 0 "and the row says why"

            Expect.equal
                (payload.Tokens |> List.map _.Verdict)
                [ "no-facts-in-scope" ]
                "the token's status names the reason"

            Expect.isTrue
                (match soleRow auditLog with
                 | AnswerVerificationPassed _ -> true
                 | _ -> false)
                "nothing was unmatched, so the affirmative case is the honest one"
        }

        // ── Honest absence ────────────────────────────────────────────

        test "with no anchors composed, the row records honest absence rather than a placeholder" {
            let _, widgets, _ = factTier ()
            let _, auditLog = auditTier ()

            runStage
                (AnswerAuditJoin.auditOnly auditLog)
                [ factSource widgets "£21,800" ]
                "Widgets took £21,800 over the quarter."
            |> ignore

            let payload = payloadOf (soleRow auditLog)

            Expect.isNone payload.CertificateRef "no certificate substrate composed ⇒ no ref"
            Expect.isNone payload.CompositionSealId "no sealed composition ⇒ no seal id"

            // The half that does NOT depend on an anchor is unaffected —
            // the walk into the fact tier survives a deployment that
            // composes neither certificates nor a sealed composition.
            Expect.equal
                payload.CitedFactIds
                [ widgets.FactId ]
                "the fact join is derived from the verdict, not an anchor"

            Expect.isSome payload.ProvenanceChainHead "and so is the chain head"
        }

        test "a drifted boot names no seal id, even though a binding is present" {
            let bootResult, sealedBinding = verifiedBoot ()

            let drifted = {
                bootResult with
                    Verdict = BootVerificationVerdict.Drifted [ ComponentAdded("module", "surprise") ]
            }

            let anchors =
                AnswerProvenanceAnchors.fromBootVerification drifted (Some sealedBinding)

            Expect.isNone
                anchors.CompositionSealId
                "naming the seal would assert exactly what the boot check declined to affirm"

            let affirmative =
                AnswerProvenanceAnchors.fromBootVerification bootResult (Some sealedBinding)

            Expect.isSome affirmative.CompositionSealId "the affirmative verdict does name it — the probe can fail"
        }

        test "Phase 694 — a boot that could not compare the canonical methods names no seal id either" {
            // The one call site where `isAffirmative` and `isFullyCompared`
            // must part company. A `VerifiedUnrecorded` boot is affirmative
            // and the process serves; what it could not compare is the
            // selector deciding which method's lineage a method-less query
            // resolves to — i.e. what the numbers on THIS row mean. Naming
            // the seal here would restate Phase 694's silent equality at
            // the one surface where the selector is material.
            let bootResult, sealedBinding = verifiedBoot ()

            let unrecorded = {
                bootResult with
                    Verdict =
                        BootVerificationVerdict.VerifiedUnrecorded [
                            CanonicalMethodUnrecorded("revenue", Some "computed:rollup:2")
                        ]
            }

            Expect.isTrue
                (BootVerificationVerdict.isAffirmative unrecorded.Verdict)
                "the verdict IS affirmative — the process serves, and this test is not asserting otherwise"

            let anchors =
                AnswerProvenanceAnchors.fromBootVerification unrecorded (Some sealedBinding)

            Expect.isNone
                anchors.CompositionSealId
                "and the anchor still declines: re-seal the binding to get it back, which is the same one act the upgrade already needs"
        }

        // ── The unchanged deployment ──────────────────────────────────

        test "a deployment composing no audit log records no row at all" {
            let _, widgets, _ = factTier ()
            let auditEvents, auditLog = auditTier ()

            let answer, verdict =
                runStage AnswerAuditJoin.none [ factSource widgets "£21,800" ] "Widgets took £21,800 over the quarter."

            Expect.equal answer "Widgets took £21,800 over the quarter." "the answer path is untouched"
            Expect.equal (verdict |> Option.map _.Verified) (Some 1) "the verdict is still produced"

            let trail = auditLog.GetAuditTrail(scopeId, None, None) |> Async.RunSynchronously
            Expect.isEmpty trail "no audit log in the join ⇒ nothing recorded"

            let persisted = auditEvents.ReadAll scopeId |> Async.RunSynchronously
            Expect.isEmpty persisted "and nothing persisted"
        }

        test "the pre-680 entry point still records nothing" {
            let _, widgets, _ = factTier ()
            let _, auditLog = auditTier ()

            // The delegating overload takes no join, so there is nothing
            // it could record even with an audit log composed elsewhere.
            let answer, _ =
                runVerificationStage
                    annotateGate
                    None
                    [ factSource widgets "£21,800" ]
                    "Widgets took £21,800 over the quarter."
                    None
                    None
                    scopeId
                    (Guid.NewGuid())
                    (Guid.NewGuid())
                    "prov"
                    "model"
                    silentLogger
                |> Async.RunSynchronously

            Expect.equal answer "Widgets took £21,800 over the quarter." "unchanged"

            Expect.isEmpty
                (auditLog.GetAuditTrail(scopeId, None, None) |> Async.RunSynchronously)
                "the pre-680 signature has no join to record through"
        }

        // ── The chain head ────────────────────────────────────────────

        testList "provenanceChainHead" [
            test "is deterministic over the same ids" {
                Expect.equal (provenanceChainHead [ "a"; "b" ]) (provenanceChainHead [ "a"; "b" ]) "same ids, same head"
            }

            test "distinguishes different id sets" {
                Expect.notEqual
                    (provenanceChainHead [ "a"; "b" ])
                    (provenanceChainHead [ "a"; "c" ])
                    "a different chain gets a different head"
            }

            test "is None over no ids" {
                Expect.isNone (provenanceChainHead []) "a digest over nothing would look like a chain head"
            }
        ]
    ]