// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.RunJobTests

// The laws of the one handler shape: a malformed payload is permanent, a
// terminal run failure is permanent, any other failure is transient; the
// job-payload codec round-trips the request types; and every handler name
// in the grounding and modelling plane is byte-for-byte what the
// schedulers have persisted.

open System
open Expecto
open ToolUp.Platform
open ToolUp.Facts

let private silentLogger =
    { new ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(_: string) = ()
        member _.Error(_: string, _: exn option) = ()
    }

let private jobCtx (payload: string) : JobContext = {
    JobId = Guid.NewGuid()
    ScopeId = "team-1"
    AccessContext = AccessContext.unrestricted (AuthenticatedUser "system")
    Attempt = 1
    Trigger = Manual
    Scope = ResolvedScope.anonymous
    TriggerSource = ScheduledManually "system"
    ScheduledAt = DateTime.UtcNow
    RunningAt = DateTime.UtcNow
    Payload = payload
    DeadLetterDestination = None
}

type Probe = { Target: string; Count: int }

/// A job whose run returns `outcome`, recording that it ran.
let private probeJob (outcome: Result<unit, RunFailure>) =
    let ran = ref 0

    let job =
        RunJob.ofPayload<Probe> "_test.probe" (fun _ request -> async {
            ran.Value <- ran.Value + request.Count
            return outcome
        })

    job, ran

let private execute (job: RunJob<Probe>) (payload: string) =
    (RunJob.handler silentLogger job).Execute(jobCtx payload)

let private wellFormed = JobPayload.serialise { Target = "x"; Count = 1 }

let tests =
    testList "RunJob — one handler shape" [

        testCaseAsync "a malformed payload is a PermanentFailure and the run is never called"
        <| async {
            let job, ran = probeJob (Ok())
            let! result = execute job "{ not json"

            match result with
            | PermanentFailure message -> Expect.stringContains message "malformed" "the failure says why"
            | other -> failtestf "expected PermanentFailure, got %A" other

            Expect.equal ran.Value 0 "nothing ran"
        }

        testCaseAsync "a null payload is malformed, not a run over a null request"
        <| async {
            let job, ran = probeJob (Ok())
            let! result = execute job "null"
            Expect.isTrue (result.IsPermanentFailure) "null is not a request"
            Expect.equal ran.Value 0 "nothing ran"
        }

        testCaseAsync "RunFailure.Terminal is a PermanentFailure"
        <| async {
            let job, ran = probeJob (Error(RunFailure.Terminal "the artifact is gone"))
            let! result = execute job wellFormed
            Expect.equal result (PermanentFailure "the artifact is gone") "terminal ends the job"
            Expect.equal ran.Value 1 "the run ran once"
        }

        testCaseAsync "RunFailure.Retryable is a TransientFailure"
        <| async {
            let job, _ = probeJob (Error(RunFailure.Retryable "storage blipped"))
            let! result = execute job wellFormed
            Expect.equal result (TransientFailure "storage blipped") "retryable leaves it to the retry policy"
        }

        testCaseAsync "a run that succeeds is Success"
        <| async {
            let job, _ = probeJob (Ok())
            let! result = execute job wellFormed
            Expect.equal result Success "success"
        }

        test "JobPayload round-trips every request type the plane's handlers parse" {
            let roundTrips (value: 'T) =
                Expect.equal (JobPayload.parse<'T> (JobPayload.serialise value)) (Ok value) (typeof<'T>.Name)

            roundTrips (
                {
                    ScopeId = "team-1"
                    ArtifactKeyHash = "abc"
                }
                : PromotionEvaluationRequest
            )

            roundTrips {
                ReevaluationSweepRequest.ScopeId = "team-1"
                EvaluatedBy = "system"
            }

            roundTrips { RecomputeJobPayload.factId = "f-1" }
            roundTrips { Target = "y"; Count = 3 }
        }

        test "JobPayload reads a payload written by the per-envelope serialisers it replaced" {
            // The persisted shape every platform handler wrote: the SDK's
            // `FableConverters` JSON. A job queued before the upgrade parses.
            let persisted = """{"ScopeId":"team-1","ArtifactKeyHash":"abc"}"""

            Expect.equal
                (JobPayload.parse<PromotionEvaluationRequest> persisted)
                (Ok(
                    {
                        ScopeId = "team-1"
                        ArtifactKeyHash = "abc"
                    }
                    : PromotionEvaluationRequest
                ))
                "an already-queued payload parses unchanged"

            Expect.equal
                (JobPayload.parse<RecomputeJobPayload> """{"factId":"f-9"}""")
                (Ok { factId = "f-9" })
                "the recompute payload keeps its camel-cased field"
        }

        test "every handler name is byte-for-byte what the schedulers have persisted" {
            let byKind =
                RunKind.all
                |> List.choose (fun k -> RunKind.handlerName k |> Option.map (fun n -> k, n))

            Expect.equal
                byKind
                [
                    RunKind.ModelFit, "_platform.modelfit.run"
                    RunKind.Evaluation, "_platform.modeleval.run"
                    RunKind.PromotionDecision, "_platform.modelpromotion.evaluate"
                    RunKind.Revintage, "_platform.dataset.revintage"
                ]
                "the four kinds a scheduler runs"

            Expect.equal ModelFitJobHandler.HandlerName "_platform.modelfit.run" "fit"
            Expect.equal ModelEvaluationJobHandler.HandlerName "_platform.modeleval.run" "evaluation"
            Expect.equal ModelPromotionJobHandler.HandlerName "_platform.modelpromotion.evaluate" "promotion"
            Expect.equal DatasetRevintageJobHandler.HandlerName "_platform.dataset.revintage" "revintage"
            Expect.equal ModelFitBatch.ItemHandlerName "_platform.modelfit.batchitem" "batch item"
            Expect.equal ModelScoringJobHandler.HandlerName "_platform.modelscore.run" "scoring"
            Expect.equal VintageReevaluationJobHandler.HandlerName "_platform.modeleval.revintage" "re-evaluation sweep"
            Expect.equal RecomputeJobHandler.HandlerName "_facts.recompute.run" "fact recompute"
        }

        test "every kind has a distinct label" {
            let labels = RunKind.all |> List.map RunKind.label
            Expect.equal (List.distinct labels) labels "no two kinds share a frame label"
            Expect.equal labels.Length 9 "nine kinds"
        }
    ]