// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ModelPromotionPolicyTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore

// ─── Phase 645 — registry promotion policies ────────────────────────────
//
// Two halves, deliberately tested apart.
//
// The **pure judge** (`PromotionJudge.judge`) is exercised directly, with no
// store and no clock, because every fail-safe claim this phase makes is a
// claim about that function: an unevaluable tolerance queues, an empty
// `PromoteWhen` queues rather than promoting vacuously, and a floor that
// could not be measured never rejects. Asserting those through the envelope
// would prove the envelope reached the right verdict on one path, not that
// the function cannot reach the wrong one.
//
// The **evaluator** is exercised over the real blob-backed registry, so the
// transitions it drives are real Phase 453 versioned writes judged by the
// real Phase 644 seam. It asserts:
//   * a fit within tolerance auto-promotes, and the promotion is attributed
//     to the POLICY (author kind `policy`, author id = the policy id) —
//     i.e. it went through 644's seam rather than around it;
//   * supersession is explicit: the displaced incumbent is retired, the
//     decision names it, the deltas that justified it are recoverable, and
//     a `ModelArtifactSuperseded` row is emitted;
//   * a fit outside tolerance queues, stays `Fitted`, and appears in the
//     curation queue — which is a registry query narrowed by the decision;
//   * a policy evaluation error queues rather than promoting, whether the
//     source raised or was never composed;
//   * with no declared policy everything queues;
//   * a policy holding no transition grant still judges and records, and
//     moves nothing — the grant is the deployment's declaration, not the
//     policy's own claim.

let private t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

let private silentLogger =
    { new ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(_: string) = ()
        member _.Error(_: string, _: exn option) = ()
    }

/// Records every `Record` call so a test can assert audit shape + count.
type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

/// An evaluation runner that raises on the one method a policy reads — the
/// "policy evaluation error" arm. Everything else is unreachable here and
/// says so rather than returning a plausible empty value.
type private ThrowingEvaluationRunner() =
    interface IModelEvaluationRunner with
        member _.Evaluate _ = failwith "not reachable in this test"
        member _.GetTrackRecord(_, _) = async { return failwith "the metric source is unavailable" }
        member _.Compare _ = failwith "not reachable in this test"
        member _.GetComparison(_, _) = failwith "not reachable in this test"
        member _.RegisterReevaluation(_, _, _, _) = failwith "not reachable in this test"
        member _.ListReevaluationRegistrations _ = failwith "not reachable in this test"

let private scope = "team-1"

type private Stack = {
    Registry: IModelRegistry
    DataObjects: IDataObjectStore
    Audit: RecordingAuditLog
}

let private freshStack () : Stack =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-promotion-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let dataObjects = DataObjectStore(blob) :> IDataObjectStore
    let audit = RecordingAuditLog()

    {
        Registry = BlobModelRegistry.create dataObjects audit
        DataObjects = dataObjects
        Audit = audit
    }

let private depsOf (stack: Stack) (evaluations: IModelEvaluationRunner option) : PromotionPolicyDeps = {
    Registry = stack.Registry
    Evaluations = evaluations
    DataObjects = stack.DataObjects
    Audit = stack.Audit
    Now = fun () -> t0
}

[<Literal>]
let private SpecPayload = "promotion-spec"

/// Register an artifact carrying declared diagnostics. The diagnostics are
/// what the policy judges, so a test states them directly rather than
/// arranging for a provider to produce them — the arithmetic that would
/// stand between the two is not what is under test.
let private register (stack: Stack) (seed: int64) (diagnostics: (string * float) list) = async {
    let spec = ModelSpecRef.ofPayload SpecPayload

    let compositeKey =
        FitCompositeKey.compute spec.SpecHash $"{scope}/panel@v1" seed "reference" "1.0.0"

    let outcome: FitOutcome = {
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

    match! stack.Registry.Register(scope, outcome, "u1", Map.empty, "") with
    | Ok artifact -> return artifact
    | Error e -> return failwithf "register failed: %s" (ModelRegistryError.describe e)
}

/// Promote an artifact the ordinary Phase 453 way — the incumbent a policy
/// then judges a challenger against.
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

let private attributedRows (audit: RecordingAuditLog) =
    audit.Events
    |> List.choose (fun (_, e) ->
        match e with
        | ModelArtifactTransitionAttributed p -> Some p
        | _ -> None)

let private policyRows (audit: RecordingAuditLog) =
    audit.Events
    |> List.choose (fun (_, e) ->
        match e with
        | ModelPromotionPolicyEvaluated p -> Some p
        | _ -> None)

let private supersededRows (audit: RecordingAuditLog) =
    audit.Events
    |> List.choose (fun (_, e) ->
        match e with
        | ModelArtifactSuperseded p -> Some p
        | _ -> None)

/// The canonical policy under test: promote a challenger whose error
/// measure improves on the incumbent's and whose fit score has not drifted
/// far from it — an improvement tolerance and a stability tolerance
/// together, which is the shape a standing refit actually declares.
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

let private evaluateOk (evaluator: ModelPromotionPolicyEvaluator) (keyHash: string) = async {
    match! evaluator.Evaluate(scope, keyHash) with
    | Ok decision -> return decision
    | Error e -> return failwithf "evaluation failed: %s" (PromotionPolicyError.describe e)
}

let tests =
    testList "Phase 645 — registry promotion policies" [

        // ─── The pure judge ─────────────────────────────────────────────

        test "judge auto-promotes a candidate inside every declared tolerance" {
            let observed = Map.ofList [ "error", 0.40; "score", 0.82 ]
            let incumbent = Map.ofList [ "error", 0.50; "score", 0.80 ]

            let verdict, checks, reason =
                PromotionJudge.judge tolerantPolicy observed (Some incumbent)

            Expect.equal verdict PromotionPolicyVerdict.AutoPromote "within tolerance auto-promotes"
            Expect.hasLength checks 2 "both declared tolerances are evidenced"
            Expect.isTrue (checks |> List.forall _.Satisfied) "every check is satisfied"
            Expect.isTrue (checks |> List.forall _.Evaluable) "every check is evaluable"
            Expect.stringContains reason "within every declared tolerance" "the reason names the outcome"
        }

        test "judge queues a candidate outside a declared tolerance" {
            // The error improves, but the fit score drifted 0.20 against an
            // 0.05 stability tolerance.
            let observed = Map.ofList [ "error", 0.40; "score", 0.60 ]
            let incumbent = Map.ofList [ "error", 0.50; "score", 0.80 ]

            let verdict, checks, reason =
                PromotionJudge.judge tolerantPolicy observed (Some incumbent)

            Expect.equal verdict PromotionPolicyVerdict.QueueForCuration "outside tolerance queues"

            let drifted = checks |> List.find (fun c -> c.Metric = "score")
            Expect.isTrue drifted.Evaluable "the drifted tolerance WAS judged"
            Expect.isFalse drifted.Satisfied "and it is not satisfied"
            Expect.stringContains reason "outside declared tolerance" "the reason names the outcome"
        }

        test "judge never promotes vacuously on an empty PromoteWhen" {
            // An empty conjunction is vacuously TRUE in ordinary logic. If
            // that reading leaked in here, a policy declaring no promotion
            // tolerances would promote everything — the one place the
            // mathematically natural reading is the dangerous one.
            let policy = PromotionPolicy.create "declares-nothing"

            let verdict, checks, reason =
                PromotionJudge.judge policy (Map.ofList [ "error", 0.0 ]) None

            Expect.equal verdict PromotionPolicyVerdict.QueueForCuration "an empty PromoteWhen queues"
            Expect.isEmpty checks "there was nothing to evidence"
            Expect.stringContains reason "declares no promotion tolerances" "the reason says so plainly"
        }

        test "judge queues when a tolerance cannot be evaluated" {
            let missingMetric =
                PromotionJudge.judge tolerantPolicy (Map.ofList [ "score", 0.80 ]) (Some(Map.ofList [ "score", 0.80 ]))

            match missingMetric with
            | verdict, checks, reason ->
                Expect.equal verdict PromotionPolicyVerdict.QueueForCuration "a missing metric queues"

                let absent = checks |> List.find (fun c -> c.Metric = "error")
                Expect.isFalse absent.Evaluable "the absent metric is unevaluable"
                Expect.isFalse absent.Satisfied "an unjudged tolerance is never counted as met"
                Expect.stringContains reason "could not be evaluated" "the reason distinguishes absence from failure"

            // No incumbent at all: every incumbent-relative tolerance is
            // unevaluable, so the FIRST artifact for a spec is curated by a
            // human rather than promoted against nothing.
            let noIncumbent =
                PromotionJudge.judge tolerantPolicy (Map.ofList [ "error", 0.1; "score", 0.9 ]) None

            match noIncumbent with
            | verdict, _, _ -> Expect.equal verdict PromotionPolicyVerdict.QueueForCuration "no incumbent queues"

            // NaN is not "present": it fails every comparison in both
            // directions, so treating it as a value would silently report a
            // tolerance as unsatisfied when it was never judged.
            let nanObserved =
                PromotionJudge.judge
                    tolerantPolicy
                    (Map.ofList [ "error", nan; "score", 0.9 ])
                    (Some(Map.ofList [ "error", 0.5; "score", 0.9 ]))

            match nanObserved with
            | verdict, checks, _ ->
                Expect.equal verdict PromotionPolicyVerdict.QueueForCuration "a NaN metric queues"
                let nanCheck = checks |> List.find (fun c -> c.Metric = "error")
                Expect.isFalse nanCheck.Evaluable "NaN is unevaluable, not unsatisfied"
        }

        test "judge rejects only a PROVEN floor breach" {
            let policy = {
                PromotionPolicy.create "floored" with
                    RejectUnless = [
                        {
                            Metric = "error"
                            Direction = MetricDirection.LowerIsBetter
                            Comparator = PromotionComparator.AtMost 1.0
                        }
                    ]
                    PromoteWhen = [
                        {
                            Metric = "error"
                            Direction = MetricDirection.LowerIsBetter
                            Comparator = PromotionComparator.AtMost 0.5
                        }
                    ]
            }

            let breached = PromotionJudge.judge policy (Map.ofList [ "error", 2.0 ]) None

            match breached with
            | verdict, _, reason ->
                Expect.equal verdict PromotionPolicyVerdict.Reject "a measured floor breach rejects"
                Expect.stringContains reason "declared floor breached" "the reason names the floor"

            // `Reject` retires, and `Retired` is terminal — so a floor that
            // could not be MEASURED must never reject. Proving something is
            // bad and failing to measure it are different findings.
            let unmeasurable = PromotionJudge.judge policy Map.empty None

            match unmeasurable with
            | verdict, _, _ ->
                Expect.equal
                    verdict
                    PromotionPolicyVerdict.QueueForCuration
                    "an unmeasurable floor queues, never rejects"

            let clear = PromotionJudge.judge policy (Map.ofList [ "error", 0.25 ]) None

            match clear with
            | verdict, _, _ ->
                Expect.equal verdict PromotionPolicyVerdict.AutoPromote "clearing floor and tolerance promotes"
        }

        test "comparators read the declared direction" {
            let stability direction = {
                Metric = "m"
                Direction = direction
                Comparator = PromotionComparator.NoWorseThanIncumbentBy 0.10
            }

            let judgeOne tolerance observed incumbent =
                PromotionJudge.check (Map.ofList [ "m", observed ]) (Some(Map.ofList [ "m", incumbent ])) tolerance

            // HigherIsBetter: a drop of 0.05 is inside a 0.10 tolerance; 0.20 is not.
            Expect.isTrue
                (judgeOne (stability MetricDirection.HigherIsBetter) 0.95 1.00).Satisfied
                "small drop tolerated"

            Expect.isFalse
                (judgeOne (stability MetricDirection.HigherIsBetter) 0.80 1.00).Satisfied
                "large drop refused"

            // LowerIsBetter: worse means LARGER, so the tolerance flips.
            Expect.isTrue
                (judgeOne (stability MetricDirection.LowerIsBetter) 1.05 1.00).Satisfied
                "small rise tolerated"

            Expect.isFalse (judgeOne (stability MetricDirection.LowerIsBetter) 1.20 1.00).Satisfied "large rise refused"

            // An improvement always satisfies a stability tolerance.
            Expect.isTrue
                (judgeOne (stability MetricDirection.LowerIsBetter) 0.10 1.00).Satisfied
                "improvement tolerated"

            let band = {
                Metric = "m"
                Direction = MetricDirection.HigherIsBetter
                Comparator = PromotionComparator.WithinFractionOfIncumbent 0.10
            }

            // Two-sided: a sharp move in the FAVOURABLE direction is as much
            // a reason to look as one against.
            Expect.isTrue (judgeOne band 1.05 1.00).Satisfied "inside the band"
            Expect.isFalse (judgeOne band 1.50 1.00).Satisfied "a favourable jump still leaves the band"
            Expect.isFalse (judgeOne band 0.50 1.00).Satisfied "an adverse drop leaves the band"

            // Absolute comparators need no incumbent at all.
            let absolute = {
                Metric = "m"
                Direction = MetricDirection.HigherIsBetter
                Comparator = PromotionComparator.AtLeast 0.5
            }

            let noIncumbent = PromotionJudge.check (Map.ofList [ "m", 0.7 ]) None absolute
            Expect.isTrue noIncumbent.Evaluable "an absolute comparator is evaluable without an incumbent"
            Expect.isTrue noIncumbent.Satisfied "and it holds"
        }

        // ─── The evaluator, over the real registry ──────────────────────

        testAsync "a refit within tolerance auto-promotes and supersedes the incumbent explicitly" {
            let stack = freshStack ()

            let evaluator =
                ModelPromotionPolicyEvaluator.create [ tolerantPolicy ] (depsOf stack None)

            let! incumbent = register stack 1L [ "error", 0.50; "score", 0.80 ]
            let! _ = approve stack incumbent.CompositeKey.Hash
            let! challenger = register stack 2L [ "error", 0.40; "score", 0.82 ]

            let! decision = evaluateOk evaluator challenger.CompositeKey.Hash

            Expect.equal
                decision.Verdict
                (PromotionPolicyVerdict.name PromotionPolicyVerdict.AutoPromote)
                "the verdict is AutoPromote"

            Expect.isTrue decision.TransitionApplied "the promotion landed"
            Expect.equal decision.PolicyId "auto-refit" "the decision names the deciding policy"
            Expect.equal decision.PolicyVersion 1 "and its declared version"

            let! challengerStatus = statusOf stack challenger.CompositeKey.Hash
            Expect.equal challengerStatus ModelArtifactStatus.Approved "the challenger is approved"

            // Supersession — explicit, recorded, and actually applied. A
            // second Approved artifact for the same spec would mean a
            // consumer silently resolves a different model.
            Expect.equal
                decision.SupersededKeyHash
                (Some incumbent.CompositeKey.Hash)
                "the decision names what it displaced"

            Expect.equal decision.SupersessionRefusal "" "and nothing refused the retirement"

            let! incumbentStatus = statusOf stack incumbent.CompositeKey.Hash
            Expect.equal incumbentStatus ModelArtifactStatus.Retired "the incumbent is retired"

            let deltas = PromotionDecision.supersessionDeltas decision
            Expect.hasLength deltas 2 "both metrics carry a candidate/incumbent delta"

            let errorDelta = deltas |> List.find (fun (m, _, _, _) -> m = "error")

            match errorDelta with
            | _, observed, reference, delta ->
                Expect.floatClose Accuracy.high observed 0.40 "the observed error is recorded"
                Expect.floatClose Accuracy.high reference 0.50 "the incumbent's error is recorded"
                Expect.floatClose Accuracy.high delta -0.10 "the justifying delta is derivable"

            let superseded = supersededRows stack.Audit
            Expect.hasLength superseded 1 "one supersession event is emitted"
            Expect.equal superseded.Head.SupersededKeyHash incumbent.CompositeKey.Hash "naming the displaced artifact"
            Expect.equal superseded.Head.SupersedingKeyHash challenger.CompositeKey.Hash "and the one that displaced it"
            Expect.isTrue superseded.Head.Retired "and recording that the retirement completed"

            // Both transitions went through the Phase 644 seam AS THE
            // POLICY — this is the assertion that the phase added a case
            // rather than a second state machine.
            let attributed = attributedRows stack.Audit |> List.filter _.Admitted

            Expect.hasLength attributed 2 "the promotion and the retirement are both attributed"

            Expect.isTrue
                (attributed
                 |> List.forall (fun r -> r.AuthorKind = "policy" && r.AuthorId = "auto-refit"))
                "every policy-driven transition is attributed to the policy"

            Expect.isTrue
                (attributed |> List.forall (fun r -> r.Channel = "local"))
                "a policy verdict is authored data-side, so it arrives on the local channel"
        }

        testAsync "a refit outside tolerance queues for curation and stays where the lifecycle holds it" {
            let stack = freshStack ()

            let evaluator =
                ModelPromotionPolicyEvaluator.create [ tolerantPolicy ] (depsOf stack None)

            let! incumbent = register stack 1L [ "error", 0.50; "score", 0.80 ]
            let! _ = approve stack incumbent.CompositeKey.Hash
            // The error improved, but the fit score drifted well outside the
            // declared stability tolerance.
            let! challenger = register stack 2L [ "error", 0.40; "score", 0.55 ]

            let! decision = evaluateOk evaluator challenger.CompositeKey.Hash

            Expect.equal
                decision.Verdict
                (PromotionPolicyVerdict.name PromotionPolicyVerdict.QueueForCuration)
                "the verdict is QueueForCuration"

            Expect.isFalse decision.TransitionApplied "a queue moves nothing"

            let! challengerStatus = statusOf stack challenger.CompositeKey.Hash
            Expect.equal challengerStatus ModelArtifactStatus.Fitted "the challenger stays Fitted"

            let! incumbentStatus = statusOf stack incumbent.CompositeKey.Hash
            Expect.equal incumbentStatus ModelArtifactStatus.Approved "and the incumbent is untouched"

            Expect.isEmpty (attributedRows stack.Audit) "no transition was attempted at all"

            // The queue is a registry query narrowed by the standing
            // decision — consumable by any UI with no new status.
            let! queue = evaluator.ListQueuedForCuration scope
            Expect.hasLength queue 1 "exactly the queued artifact is listed"
            Expect.equal queue.Head.Artifact.CompositeKey.Hash challenger.CompositeKey.Hash "the right artifact"

            Expect.equal
                queue.Head.Artifact.Status
                ModelArtifactStatus.Fitted
                "listed from the status the lifecycle already holds an uncurated fit in"

            Expect.stringContains
                queue.Head.Decision.Reason
                "outside declared tolerance"
                "and the entry carries the evidence-backed reason"

            // A human curating it out of the queue removes it, with no
            // second bookkeeping surface to update.
            let! _ = approve stack challenger.CompositeKey.Hash
            let! afterCuration = evaluator.ListQueuedForCuration scope
            Expect.isEmpty afterCuration "a curated artifact leaves the queue by its status alone"
        }

        testAsync "a policy evaluation error queues rather than promoting" {
            // The metric source raises. Nothing about the candidate is
            // known, so the only safe verdict is the human's.
            let stack = freshStack ()

            let policy = {
                tolerantPolicy with
                    Source = PromotionMetricSource.LatestEvaluation
            }

            let evaluator =
                ModelPromotionPolicyEvaluator.create
                    [ policy ]
                    (depsOf stack (Some(ThrowingEvaluationRunner() :> IModelEvaluationRunner)))

            let! incumbent = register stack 1L [ "error", 0.50; "score", 0.80 ]
            let! _ = approve stack incumbent.CompositeKey.Hash
            let! challenger = register stack 2L [ "error", 0.01; "score", 0.99 ]

            let! decision = evaluateOk evaluator challenger.CompositeKey.Hash

            Expect.equal
                decision.Verdict
                (PromotionPolicyVerdict.name PromotionPolicyVerdict.QueueForCuration)
                "a raising metric source queues"

            Expect.isFalse decision.TransitionApplied "and promotes nothing"
            Expect.stringContains decision.Reason "policy evaluation failed" "the reason names the failure"

            let! status = statusOf stack challenger.CompositeKey.Hash

            Expect.equal
                status
                ModelArtifactStatus.Fitted
                "a candidate that would have sailed through on its diagnostics is NOT promoted on unread metrics"

            // The same fail-safe with the source simply never composed —
            // a configuration mistake, not a fault, and it must not promote
            // either.
            let stack2 = freshStack ()

            let evaluator2 =
                ModelPromotionPolicyEvaluator.create [ policy ] (depsOf stack2 None)

            let! artifact = register stack2 3L [ "error", 0.01; "score", 0.99 ]
            let! decision2 = evaluateOk evaluator2 artifact.CompositeKey.Hash

            Expect.equal
                decision2.Verdict
                (PromotionPolicyVerdict.name PromotionPolicyVerdict.QueueForCuration)
                "an uncomposed metric source queues"

            Expect.stringContains decision2.Reason "IModelEvaluationRunner" "and says what is missing"
        }

        testAsync "with no declared policy everything queues" {
            let stack = freshStack ()
            let evaluator = ModelPromotionPolicyEvaluator.create [] (depsOf stack None)

            let! artifact = register stack 1L [ "error", 0.0; "score", 1.0 ]
            let! decision = evaluateOk evaluator artifact.CompositeKey.Hash

            Expect.equal
                decision.Verdict
                (PromotionPolicyVerdict.name PromotionPolicyVerdict.QueueForCuration)
                "the fail-safe default queues"

            Expect.equal decision.PolicyId "" "no policy id is invented"
            Expect.stringContains decision.Reason "no promotion policy is declared" "and the reason says why"

            let! status = statusOf stack artifact.CompositeKey.Hash
            Expect.equal status ModelArtifactStatus.Fitted "a perfect artifact is still not auto-promoted"

            // The decision is recorded even so — "no policy governed this"
            // is an answer an operator needs, and an absent record is not
            // one.
            let! queue = evaluator.ListQueuedForCuration scope
            Expect.hasLength queue 1 "it is queued for a human"

            let rows = policyRows stack.Audit
            Expect.hasLength rows 1 "and a verdict event is emitted for subscribers"
            Expect.equal rows.Head.Verdict "QueueForCuration" "carrying the verdict"
            Expect.isFalse rows.Head.TransitionApplied "and recording that nothing moved"
        }

        testAsync "a proven floor breach is rejected through the same seam" {
            let stack = freshStack ()

            let policy = {
                PromotionPolicy.create "floored" with
                    RejectUnless = [
                        {
                            Metric = "error"
                            Direction = MetricDirection.LowerIsBetter
                            Comparator = PromotionComparator.AtMost 1.0
                        }
                    ]
                    Grant = PromotionPolicy.promotionGrant
            }

            let evaluator = ModelPromotionPolicyEvaluator.create [ policy ] (depsOf stack None)
            let! artifact = register stack 1L [ "error", 5.0 ]
            let! decision = evaluateOk evaluator artifact.CompositeKey.Hash

            Expect.equal
                decision.Verdict
                (PromotionPolicyVerdict.name PromotionPolicyVerdict.Reject)
                "a measured floor breach rejects"

            Expect.isTrue decision.TransitionApplied "and the rejection is applied"

            let! status = statusOf stack artifact.CompositeKey.Hash
            Expect.equal status ModelArtifactStatus.Retired "the rejected artifact is retired"

            let attributed = attributedRows stack.Audit |> List.filter _.Admitted
            Expect.hasLength attributed 1 "the retirement is one attributed transition"
            Expect.equal attributed.Head.AuthorKind "policy" "authored by the policy"
            Expect.equal attributed.Head.ToStatus "Retired" "into Retired"

            let! queue = evaluator.ListQueuedForCuration scope
            Expect.isEmpty queue "a rejected artifact is not left in a queue nobody will clear"
        }

        testAsync "a policy holding no grant judges and records but moves nothing" {
            // Phase 644 takes the grant as a parameter precisely so an
            // author cannot carry one it controls. A policy record IS the
            // deployment's declaration — and one that declares no grant is a
            // legitimate dry run, not a broken policy.
            let stack = freshStack ()

            let dryRun = {
                tolerantPolicy with
                    PolicyId = "dry-run"
                    Grant = ModelTransitionAuthority.none
            }

            let evaluator = ModelPromotionPolicyEvaluator.create [ dryRun ] (depsOf stack None)

            let! incumbent = register stack 1L [ "error", 0.50; "score", 0.80 ]
            let! _ = approve stack incumbent.CompositeKey.Hash
            let! challenger = register stack 2L [ "error", 0.40; "score", 0.82 ]

            let! decision = evaluateOk evaluator challenger.CompositeKey.Hash

            Expect.equal
                decision.Verdict
                (PromotionPolicyVerdict.name PromotionPolicyVerdict.AutoPromote)
                "the judgment still reaches AutoPromote"

            Expect.isFalse decision.TransitionApplied "but nothing was applied"

            Expect.stringContains
                decision.TransitionRefusal
                "not granted the authority"
                "and the seam's refusal is recorded verbatim"

            let! status = statusOf stack challenger.CompositeKey.Hash
            Expect.equal status ModelArtifactStatus.Fitted "the challenger did not move"

            let! incumbentStatus = statusOf stack incumbent.CompositeKey.Hash
            Expect.equal incumbentStatus ModelArtifactStatus.Approved "and neither did the incumbent"

            Expect.equal decision.SupersededKeyHash None "an unapplied promotion supersedes nothing"
            Expect.isEmpty (supersededRows stack.Audit) "and emits no supersession event"
        }

        testAsync "the evaluation job handler maps a malformed payload and a missing artifact" {
            let stack = freshStack ()

            let evaluator =
                ModelPromotionPolicyEvaluator.create [ tolerantPolicy ] (depsOf stack None)

            let handler = ModelPromotionJobHandler.create evaluator silentLogger

            let jobCtx (payload: string) : JobContext = {
                JobId = Guid.NewGuid()
                ScopeId = scope
                AccessContext = AccessContext.unrestricted (AuthenticatedUser "system")
                Attempt = 1
                Trigger = Manual
                TriggerSource = ScheduledManually "system"
                ScheduledAt = DateTime.UtcNow
                RunningAt = DateTime.UtcNow
                Payload = payload
                DeadLetterDestination = None
            }

            let! artifact = register stack 1L [ "error", 0.4; "score", 0.9 ]

            let good =
                PromotionEvaluationEnvelope.serialiseRequest {
                    ScopeId = scope
                    ArtifactKeyHash = artifact.CompositeKey.Hash
                }

            let! ok = handler.Execute(jobCtx good)
            Expect.equal ok Success "a well-formed request runs to Success"

            let! malformed = handler.Execute(jobCtx "{ not json")

            match malformed with
            | PermanentFailure _ -> ()
            | other -> failtestf "a malformed payload must be a PermanentFailure; got %A" other

            let unknown =
                PromotionEvaluationEnvelope.serialiseRequest {
                    ScopeId = scope
                    ArtifactKeyHash = "no-such-artifact"
                }

            let! missing = handler.Execute(jobCtx unknown)

            match missing with
            | PermanentFailure _ -> ()
            | other -> failtestf "an unknown artifact is terminal; expected PermanentFailure, got %A" other
        }
    ]