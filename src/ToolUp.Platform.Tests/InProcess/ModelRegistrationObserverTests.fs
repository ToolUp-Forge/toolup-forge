// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ModelRegistrationObserverTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore

// ─── Phase 651 — the registration observer seam ─────────────────────────
//
// The seam's whole claim is that "a new artifact exists" becomes something
// the platform notices, exactly once, without a consumer remembering to
// ask — and that noticing it can never damage the registration. So the
// tests are arranged around the four ways that claim could be false:
//
//   * it fires on a REPLAY, making a retried job look like a stream of new
//     artifacts (the idempotent early-return in `BlobModelRegistry.Register`
//     is the specific path pinned here);
//   * an observer's failure changes the registration the caller sees, or
//     stops the observers after it;
//   * the policy binding needs per-deployment glue to actually run, i.e.
//     composing it is not sufficient;
//   * composing nothing is not the same as the pre-651 registry.
//
// The end-to-end arms run over the REAL blob-backed registry and the real
// Phase 645 evaluator, because "register → evaluate → auto-promote" is a
// claim about those parts fitting together; a stubbed evaluator would prove
// only that the decorator can call a function.

let private t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

let private silentLogger =
    { new ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(_: string) = ()
        member _.Error(_: string, _: exn option) = ()
    }

type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

/// An evaluation runner whose one read raises — the Phase 645 "policy
/// evaluation error" arm, reached here through registration rather than
/// through an explicit `Evaluate` call.
type private ThrowingEvaluationRunner() =
    interface IModelEvaluationRunner with
        member _.Evaluate _ = failwith "not reachable in this test"
        member _.GetTrackRecord(_, _) = async { return failwith "the metric source is unavailable" }
        member _.Compare _ = failwith "not reachable in this test"
        member _.GetComparison(_, _) = failwith "not reachable in this test"
        member _.RegisterReevaluation(_, _, _, _) = failwith "not reachable in this test"
        member _.ListReevaluationRegistrations _ = failwith "not reachable in this test"

/// A registry that delegates everything and declares NO novelty capability
/// — an outside implementation predating Phase 651. Exists to exercise the
/// decorator's documented fallback: still observed, still not on a replay.
type private OpaqueRegistry(inner: IModelRegistry) =
    interface IModelRegistry with
        member _.Register(scopeId, outcome, registeredBy, annotations, notes) =
            inner.Register(scopeId, outcome, registeredBy, annotations, notes)

        member _.Get(scopeId, keyHash) = inner.Get(scopeId, keyHash)

        member _.QueryBySpecHash(scopeId, specHash) =
            inner.QueryBySpecHash(scopeId, specHash)

        member _.QueryByDatasetVersion(scopeId, datasetVersion) =
            inner.QueryByDatasetVersion(scopeId, datasetVersion)

        member _.QueryByStatus(scopeId, status) = inner.QueryByStatus(scopeId, status)

        member _.QueryPage(scopeId, query, cursor, limit) =
            inner.QueryPage(scopeId, query, cursor, limit)

        member _.TransitionStatus(scopeId, keyHash, target, callerRole, actorUserId) =
            inner.TransitionStatus(scopeId, keyHash, target, callerRole, actorUserId)

        member _.AttachProvenance(scopeId, keyHash, attachments, signature) =
            inner.AttachProvenance(scopeId, keyHash, attachments, signature)

        member _.AttachmentLimits = inner.AttachmentLimits

/// Records what it was handed; optionally raises first.
type private CapturingObserver(name: string, raises: bool) =
    let seen = ResizeArray<string * ModelArtifact>()
    member _.Seen = List.ofSeq seen
    member _.Count = seen.Count

    interface IModelRegistrationObserver with
        member _.Name = name

        member _.OnRegistered(scopeId, artifact) = async {
            seen.Add(scopeId, artifact)

            if raises then
                failwith "observer blew up"
        }

let private scope = "team-1"

type private Stack = {
    Registry: IModelRegistry
    DataObjects: IDataObjectStore
    Audit: RecordingAuditLog
}

let private freshStack () : Stack =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-registration-observer-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let dataObjects = DataObjectStore(blob) :> IDataObjectStore
    let audit = RecordingAuditLog()

    {
        Registry = BlobModelRegistry.create dataObjects audit
        DataObjects = dataObjects
        Audit = audit
    }

let private observerDeps (stack: Stack) : ModelRegistrationObserverDeps = {
    Audit = stack.Audit
    Logger = silentLogger
}

let private policyDeps (stack: Stack) (evaluations: IModelEvaluationRunner option) : PromotionPolicyDeps = {
    Registry = stack.Registry
    Evaluations = evaluations
    DataObjects = stack.DataObjects
    Audit = stack.Audit
    Now = fun () -> t0
}

[<Literal>]
let private SpecPayload = "observer-spec"

/// The fit outcome for one seed — built separately from the registration so
/// a test can register the SAME outcome twice and exercise the idempotent
/// early-return, which is what "a replay" means here.
let private outcomeFor (seed: int64) (diagnostics: (string * float) list) : FitOutcome =
    let spec = ModelSpecRef.ofPayload SpecPayload

    let compositeKey =
        FitCompositeKey.compute spec.SpecHash $"{scope}/panel@v1" seed "reference" "1.0.0"

    {
        CompositeKey = compositeKey
        ArtifactRef = {
            ArtifactId = $"artifact-{seed}"
            ContentHash = compositeKey.Hash
            ByteLength = 42L
        }
        Diagnostics = Map.ofList diagnostics
        GateVerdicts = []
        DurationMs = 1L
        CostUnits = 0.0
    }

let private registerVia (registry: IModelRegistry) (outcome: FitOutcome) = async {
    match! registry.Register(scope, outcome, "u1", Map.empty, "") with
    | Ok artifact -> return artifact
    | Error e -> return failwithf "register failed: %s" (ModelRegistryError.describe e)
}

let private approve (stack: Stack) (keyHash: string) = async {
    match! stack.Registry.TransitionStatus(scope, keyHash, ModelArtifactStatus.Approved, Owner, "u1") with
    | Ok artifact -> return artifact
    | Error e -> return failwithf "approve failed: %s" (ModelRegistryError.describe e)
}

let private statusOf (stack: Stack) (keyHash: string) = async {
    match! stack.Registry.Get(scope, keyHash) with
    | Ok artifact -> return artifact.Status
    | Error e -> return failwithf "get failed: %s" (ModelRegistryError.describe e)
}

let private observerFailureRows (audit: RecordingAuditLog) =
    audit.Events
    |> List.choose (fun (_, e) ->
        match e with
        | ModelRegistrationObserverFailed p -> Some p
        | _ -> None)

/// The canonical policy under test — the Phase 645 shape a standing refit
/// declares: the error measure must improve on the incumbent's and the fit
/// score must not have drifted far from it.
let private tolerantPolicy = {
    PromotionPolicy.create "auto-refit" with
        PromoteWhen = [
            {
                Metric = "error"
                Direction = MetricDirection.LowerIsBetter
                Comparator = PromotionComparator.ImprovesOnIncumbentBy 0.0
            }
            {
                Metric = "score"
                Direction = MetricDirection.HigherIsBetter
                Comparator = PromotionComparator.NoWorseThanIncumbentBy 0.05
            }
        ]
        Grant = PromotionPolicy.promotionGrant
}

let tests =
    testList "Phase 651 — registry registration observer" [

        // ─── The seam itself ────────────────────────────────────────────

        testAsync "an arriving artifact is observed once, with the record as stored" {
            let stack = freshStack ()
            let observer = CapturingObserver("capture", false)

            let registry =
                ModelRegistrationObservers.decorate (observerDeps stack) [ observer ] stack.Registry

            let outcome = outcomeFor 1L [ "error", 0.4 ]
            let! artifact = registerVia registry outcome

            Expect.equal observer.Count 1 "the arrival is observed exactly once"

            let observedScope, observedArtifact = observer.Seen.Head
            Expect.equal observedScope scope "the observer is told which scope the artifact landed in"

            // The observer receives the STORED record, not the outcome it
            // was built from: the status the lifecycle assigned and the
            // store-assigned version are both present, which is what makes
            // an observer able to judge without a second read.
            Expect.equal
                observedArtifact.CompositeKey.Hash
                artifact.CompositeKey.Hash
                "the observer sees the artifact that was registered"

            Expect.equal
                observedArtifact.Status
                ModelArtifactStatus.Fitted
                "carrying the lifecycle status it was born in"

            Expect.equal observedArtifact.Version 1 "and the version the store minted"
        }

        testAsync "a replayed registration observes nothing" {
            // The hazard this phase names by file and line: `Register` is
            // idempotent, so the second call returns the existing artifact
            // through an early return that appends nothing. A seam that
            // fired there would report a new artifact every time a job
            // retried.
            let stack = freshStack ()
            let observer = CapturingObserver("capture", false)

            let registry =
                ModelRegistrationObservers.decorate (observerDeps stack) [ observer ] stack.Registry

            let outcome = outcomeFor 1L [ "error", 0.4 ]

            let! first = registerVia registry outcome
            let! second = registerVia registry outcome
            let! third = registerVia registry outcome

            Expect.equal
                second.CompositeKey.Hash
                first.CompositeKey.Hash
                "the replay returns the same artifact (the idempotent contract still holds)"

            Expect.equal second.Version 1 "and appends no version"
            Expect.equal third.Version 1 "however many times it is replayed"
            Expect.equal observer.Count 1 "but only the ARRIVAL is observed"
        }

        testAsync "an observer failure is isolated, audited, and never reaches the registrar" {
            let stack = freshStack ()
            let failing = CapturingObserver("explodes", true)
            let following = CapturingObserver("follows", false)

            let registry =
                ModelRegistrationObservers.decorate (observerDeps stack) [ failing; following ] stack.Registry

            let outcome = outcomeFor 1L [ "error", 0.4 ]

            // The registration must succeed — the artifact was durable
            // before any observer ran, so reporting a failure here would be
            // reporting one for work that completed.
            match! registry.Register(scope, outcome, "u1", Map.empty, "") with
            | Error e -> failwithf "the registration must not fail: %s" (ModelRegistryError.describe e)
            | Ok artifact ->
                Expect.equal artifact.Version 1 "the artifact is registered exactly as it would have been"

                let! stored = statusOf stack artifact.CompositeKey.Hash
                Expect.equal stored ModelArtifactStatus.Fitted "and readable from the registry afterwards"

                Expect.equal failing.Count 1 "the failing observer ran"
                Expect.equal following.Count 1 "and one observer's failure does not stop the next"

                // Isolation without a record is indistinguishable from an
                // observer that had nothing to do.
                let rows = observerFailureRows stack.Audit
                Expect.hasLength rows 1 "the isolated failure is audited"
                Expect.equal rows.Head.Observer "explodes" "the row names WHICH observer failed"

                Expect.equal
                    rows.Head.CompositeKeyHash
                    artifact.CompositeKey.Hash
                    "and which artifact's arrival it was observing"

                Expect.stringContains rows.Head.Reason "observer blew up" "carrying the failure's own message"
        }

        testAsync "composing no observers returns the registry itself" {
            // Byte-identical behaviour for an un-composed deployment is not
            // "an equivalent wrapper" — it is the same object, with no
            // extra indirection on any call (GP 11 / GP 13).
            let stack = freshStack ()

            let decorated =
                ModelRegistrationObservers.decorate (observerDeps stack) [] stack.Registry

            Expect.isTrue
                (Object.ReferenceEquals(decorated, stack.Registry))
                "decorating with no observers hands back the very registry it was given"
        }

        testAsync "a registry declaring no novelty capability is still observed, and still not on a replay" {
            // The documented fallback for an outside `IModelRegistry`
            // predating Phase 651. Weaker under concurrency than the
            // default registry's own report — and the alternative of never
            // firing would make composition silently do nothing.
            let stack = freshStack ()
            let opaque = OpaqueRegistry(stack.Registry) :> IModelRegistry
            let observer = CapturingObserver("capture", false)

            let registry =
                ModelRegistrationObservers.decorate (observerDeps stack) [ observer ] opaque

            let outcome = outcomeFor 1L [ "error", 0.4 ]
            let! _ = registerVia registry outcome
            Expect.equal observer.Count 1 "the arrival is observed"

            let! _ = registerVia registry outcome
            Expect.equal observer.Count 1 "and the replay is not"
        }

        // ─── The policy binding, end to end ─────────────────────────────

        testAsync "composing promotion policies auto-promotes an arriving artifact with no consumer wiring" {
            let stack = freshStack ()

            let registry, evaluator =
                ModelPromotionPolicyObserver.compose [ tolerantPolicy ] (policyDeps stack None) silentLogger

            // The incumbent is registered and approved the ordinary Phase
            // 453 way; the challenger simply ARRIVES. Nothing below calls
            // `Evaluate` — that is the phase.
            let! incumbent = registerVia registry (outcomeFor 1L [ "error", 0.50; "score", 0.80 ])
            let! _ = approve stack incumbent.CompositeKey.Hash

            let! challenger = registerVia registry (outcomeFor 2L [ "error", 0.40; "score", 0.82 ])

            let! challengerStatus = statusOf stack challenger.CompositeKey.Hash

            Expect.equal
                challengerStatus
                ModelArtifactStatus.Approved
                "registering inside tolerance promoted the artifact, with no per-deployment glue"

            let! incumbentStatus = statusOf stack incumbent.CompositeKey.Hash
            Expect.equal incumbentStatus ModelArtifactStatus.Retired "and the incumbent it displaced was retired"

            match! evaluator.GetDecision(scope, challenger.CompositeKey.Hash) with
            | None -> failwith "the evaluation that ran on arrival must leave a decision record"
            | Some decision ->
                Expect.equal
                    decision.Verdict
                    (PromotionPolicyVerdict.name PromotionPolicyVerdict.AutoPromote)
                    "the recorded verdict is AutoPromote"

                Expect.isTrue decision.TransitionApplied "and the transition it drove landed"

                Expect.equal
                    decision.SupersededKeyHash
                    (Some incumbent.CompositeKey.Hash)
                    "naming what the promotion displaced"

            Expect.isEmpty (observerFailureRows stack.Audit) "nothing was isolated — the observer ran cleanly"
        }

        testAsync "composing promotion policies queues an arriving artifact outside tolerance" {
            let stack = freshStack ()

            let registry, evaluator =
                ModelPromotionPolicyObserver.compose [ tolerantPolicy ] (policyDeps stack None) silentLogger

            let! incumbent = registerVia registry (outcomeFor 1L [ "error", 0.50; "score", 0.80 ])
            let! _ = approve stack incumbent.CompositeKey.Hash

            // The error improved, but the fit score drifted far outside the
            // declared stability tolerance.
            let! challenger = registerVia registry (outcomeFor 2L [ "error", 0.40; "score", 0.55 ])

            let! challengerStatus = statusOf stack challenger.CompositeKey.Hash

            Expect.equal
                challengerStatus
                ModelArtifactStatus.Fitted
                "an out-of-tolerance arrival stays where the lifecycle holds an uncurated fit"

            let! incumbentStatus = statusOf stack incumbent.CompositeKey.Hash
            Expect.equal incumbentStatus ModelArtifactStatus.Approved "and the incumbent still stands"

            // The curation queue is a registry query narrowed by the
            // standing decision — so an arrival that queued is IN it.
            let! queued = evaluator.ListQueuedForCuration scope

            Expect.isTrue
                (queued
                 |> List.exists (fun entry -> entry.Artifact.CompositeKey.Hash = challenger.CompositeKey.Hash))
                "the queued arrival appears in the curation queue"
        }

        testAsync "a policy whose metric source raises queues on arrival rather than promoting" {
            // The fail-safe path, reached through registration. The
            // evaluator's own guard turns the raise into a queued decision,
            // so the observer never fails and nothing is isolated — the
            // artifact simply waits for a human.
            let stack = freshStack ()

            let policy = {
                tolerantPolicy with
                    Source = PromotionMetricSource.LatestEvaluation
            }

            let deps =
                policyDeps stack (Some(ThrowingEvaluationRunner() :> IModelEvaluationRunner))

            let registry, evaluator =
                ModelPromotionPolicyObserver.compose [ policy ] deps silentLogger

            let! artifact = registerVia registry (outcomeFor 1L [ "error", 0.01; "score", 0.99 ])

            let! status = statusOf stack artifact.CompositeKey.Hash
            Expect.equal status ModelArtifactStatus.Fitted "an unevaluable policy never promotes"

            match! evaluator.GetDecision(scope, artifact.CompositeKey.Hash) with
            | None -> failwith "the fail-safe outcome must still be recorded"
            | Some decision ->
                Expect.equal
                    decision.Verdict
                    (PromotionPolicyVerdict.name PromotionPolicyVerdict.QueueForCuration)
                    "the policy observer's failure path lands as queue-for-curation"

                Expect.isFalse decision.TransitionApplied "and moved nothing"
        }
    ]