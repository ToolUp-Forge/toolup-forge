// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IJobScopeReissueContract

open System
open Expecto
open ToolUp.Platform

// ─── IJobScopeReissue contract pack (Phase 991) ──────────────────────
//
// A scheduler that re-issues an existing job's carried scope
// (`IJobScopeReissue`, reached through `JobScopeReissue.reissue`) must
// honour, whatever it schedules on:
//
//   * a job scheduled with no scope token, re-issued under a resolved scope
//     for its own shard, redeems that scope at its next dispatch, and keeps
//     its id and the caller's tags;
//   * a scope that does not own the job's shard — one resolved for another
//     shard, or the anonymous scope — is refused with `ScopeDoesNotOwnJob`
//     and the job's definition is left exactly as it was;
//   * a job that does not exist is reported (`JobNotFound`), never created.
//
// `factory` opens one scheduler under test. `Bind` binds the carrier the
// pack redeems through — the scheduler's own `IScopeCarrierBinding`, or the
// wrapped scheduler's for a decorator — so the pack can read the scope a
// dispatch of the job would run under without firing it.

/// One scheduler under test.
type ReissueSubject = {
    /// The scheduler the pack calls `JobScopeReissue.reissue` on.
    Scheduler: IJobScheduler
    /// Bind the carrier the scheduler stamps and redeems tokens with.
    Bind: ScopeCarrier -> unit
    /// Tear the scheduler down after the case.
    Dispose: unit -> unit
}

[<Literal>]
let private HandlerName = "contract.scope-reissue"

let private nullHandler =
    { new IJobHandler with
        member _.Execute _ = async { return JobResult.Success }
    }

let private mint (scopeId: string) : ResolvedScope =
    StorageScopeResolver.ScopeResolution.ofStorageScope {
        ScopeId = scopeId
        Container = "team-container-" + scopeId
        Persist = true
    }

let private newScopeId () = "team-" + Guid.NewGuid().ToString "N"

/// A job scheduled the way every job was before its caller could carry a
/// scope: the string overload, so no token.
let private unscopedJob (scheduler: IJobScheduler) (scopeId: string) : JobId =
    let registration: JobRegistration = {
        ScopeId = scopeId
        Handler = HandlerName
        Payload = ""
        Trigger = CronTrigger "0 6 * * 1"
        Idempotency = None
        RetryPolicy = JobRetryPolicy.defaults
        ShardKey = None
        Precision = JobPrecision.Minute
        CreatedBy = "contract"
        Tags = Map [ "source", "contract" ]
    }

    match scheduler.Schedule registration |> Async.RunSynchronously with
    | Ok jobId -> jobId
    | Error e -> failtestf "schedule failed: %A" e

let tests (name: string) (factory: unit -> ReissueSubject) =

    /// Run `body` over a fresh subject with a carrier the pack holds.
    let using (body: IJobScheduler -> ScopeCarrier -> unit) =
        let subject = factory ()

        try
            let carrier = ScopeCarrier.ephemeral ()
            subject.Bind carrier
            subject.Scheduler.RegisterHandler(HandlerName, nullHandler)
            body subject.Scheduler carrier
        finally
            subject.Dispose()

    let definitionOf (scheduler: IJobScheduler) (scopeId: string) (jobId: JobId) =
        match scheduler.Get(scopeId, jobId) |> Async.RunSynchronously with
        | Some definition -> definition
        | None -> failtest "the job disappeared"

    let redeemed (scheduler: IJobScheduler) (carrier: ScopeCarrier) (scopeId: string) (jobId: JobId) =
        CarriedJobScope.ofDefinition carrier (definitionOf scheduler scopeId jobId)

    let reissue (scheduler: IJobScheduler) scope scopeId jobId =
        JobScopeReissue.reissue scheduler scope scopeId jobId |> Async.RunSynchronously

    testList $"IJobScopeReissue contract — {name}" [

        test "a job re-issued under its shard's resolved scope redeems that scope, keeping its id and tags" {
            using (fun scheduler carrier ->
                let scopeId = newScopeId ()
                let scope = mint scopeId
                let jobId = unscopedJob scheduler scopeId

                Expect.equal (redeemed scheduler carrier scopeId jobId) ResolvedScope.anonymous "it starts anonymous"
                Expect.equal (reissue scheduler scope scopeId jobId) (Ok()) "the re-issue is accepted"

                Expect.equal
                    (redeemed scheduler carrier scopeId jobId)
                    scope
                    "a dispatch now redeems the shard's scope"

                let definition = definitionOf scheduler scopeId jobId
                Expect.equal definition.JobId jobId "the same job"
                Expect.equal (definition.Tags.TryFind "source") (Some "contract") "the caller's tags are kept")
        }

        test "a scope resolved for another shard is refused, and the job is left exactly as it was" {
            using (fun scheduler carrier ->
                let scopeId = newScopeId ()
                let elsewhere = mint (newScopeId ())
                let jobId = unscopedJob scheduler scopeId
                let before = definitionOf scheduler scopeId jobId

                match reissue scheduler elsewhere scopeId jobId with
                | Error(ScopeReissueError.ScopeDoesNotOwnJob(offered, owned)) ->
                    Expect.equal offered elsewhere.ScopeId "the refusal names the scope offered"
                    Expect.equal owned scopeId "and the shard it does not own"
                | other -> failtestf "expected a cross-shard refusal, got %A" other

                Expect.equal (definitionOf scheduler scopeId jobId) before "the definition is untouched"

                Expect.equal
                    (redeemed scheduler carrier scopeId jobId)
                    ResolvedScope.anonymous
                    "and it still runs anonymous")
        }

        test "the anonymous scope owns no shard" {
            using (fun scheduler _ ->
                let scopeId = newScopeId ()
                let jobId = unscopedJob scheduler scopeId

                match reissue scheduler ResolvedScope.anonymous scopeId jobId with
                | Error(ScopeReissueError.ScopeDoesNotOwnJob _) -> ()
                | other -> failtestf "expected the anonymous scope to be refused, got %A" other)
        }

        test "a job that does not exist is reported, never created" {
            using (fun scheduler _ ->
                let scopeId = newScopeId ()
                let unknown = Guid.NewGuid()

                Expect.equal
                    (reissue scheduler (mint scopeId) scopeId unknown)
                    (Error(ScopeReissueError.JobNotFound(scopeId, unknown)))
                    "JobNotFound"

                Expect.isEmpty (scheduler.ListJobs scopeId |> Async.RunSynchronously) "and nothing was scheduled")
        }
    ]