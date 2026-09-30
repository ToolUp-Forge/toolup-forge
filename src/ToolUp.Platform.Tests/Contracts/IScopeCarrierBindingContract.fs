// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IScopeCarrierBindingContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts.IJobSchedulerContract

// ─── IScopeCarrierBinding contract pack (Phase 935) ─────────────────
//
// A scheduler that carries resolved scopes (`IScopeCarrierBinding`) stamps a
// typed `Schedule`'s scope into a token its bound `ScopeCarrier` issues, and
// redeems the token through the carrier bound when the job fires. What any
// implementation must honour, whatever it schedules on:
//
//   * the BOUND carrier is the one a fire redeems through — a carrier over
//     another key ring re-mints nothing (the job runs anonymous), and
//     binding a carrier over the ring that issued the token re-mints it
//     again, in the same process;
//   * a scheduler nothing has bound re-mints what it issued within its own
//     lifetime, and a job it scheduled runs anonymous after a restart — a
//     fail closed, never a guess.
//
// `factory` opens schedulers over one durable store with NOTHING bound (the
// pack binds), so the same `RestartableScheduler` shape the scheduler pack
// uses serves, with `Open` leaving the carrier alone. The test pack sits
// inside the platform's trust boundary (`InternalsVisibleTo`), so it builds
// carriers over key rings of its own, as composition does over the
// deployment's.

let tests (name: string) (factory: unit -> RestartableScheduler) =

    let recorder () =
        let seen = Collections.Concurrent.ConcurrentQueue<JobContext>()

        let handler =
            { new IJobHandler with
                member _.Execute ctx = async {
                    seen.Enqueue ctx
                    return JobResult.Success
                }
            }

        seen, handler

    let waitFor (timeoutMs: int) (predicate: unit -> bool) : bool =
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
        let mutable ok = predicate ()

        while not ok && DateTime.UtcNow < deadline do
            Threading.Thread.Sleep 25
            ok <- predicate ()

        ok

    let mint (scopeId: string) : ResolvedScope =
        StorageScopeResolver.ScopeResolution.ofStorageScope {
            ScopeId = scopeId
            Container = "team-container-" + scopeId
            Persist = true
        }

    let ringCarrier (ring: BlobStorage.IBlobStorage) =
        ScopeCarrier.ofKeyRepository (BlobXmlRepository(ring))

    let bind (scheduler: IJobScheduler) (carrier: ScopeCarrier) =
        match box scheduler with
        | :? IScopeCarrierBinding as binding -> binding.BindScopeCarrier carrier
        | _ -> failtest "the scheduler under test is not an IScopeCarrierBinding"

    let schedule (scheduler: IJobScheduler) (scope: ResolvedScope) : JobId =
        let registration: JobRegistration = {
            ScopeId = scope.ScopeId
            Handler = "carried"
            Payload = "{}"
            Trigger = Manual
            Idempotency = None
            RetryPolicy = JobRetryPolicy.defaults
            ShardKey = None
            Precision = Minute
            CreatedBy = "alice"
            Tags = Map.empty
        }

        match scheduler.Schedule(scope, registration) |> Async.RunSynchronously with
        | Ok id -> id
        | Error e -> failtestf "typed Schedule: %A" e

    /// Fire the job and return the scope its handler ran under.
    let fireAndRead (scheduler: IJobScheduler) (scopeId: string) (jobId: JobId) : ResolvedScope =
        let seen, handler = recorder ()
        scheduler.RegisterHandler("carried", handler)

        match scheduler.TriggerOnce(scopeId, jobId, "alice") |> Async.RunSynchronously with
        | Ok() -> ()
        | Error e -> failtestf "TriggerOnce: %s" e

        Expect.isTrue (waitFor 30_000 (fun () -> not seen.IsEmpty)) "the handler ran"
        (Seq.head seen).Scope

    testList $"{name} — the scope carrier binding (Phase 935)" [

        test "the bound carrier is the one a fire redeems through" {
            let binding = factory ()
            let scope = mint binding.ScopeId

            let ringRoot =
                IO.Path.Combine(IO.Path.GetTempPath(), "toolup-carrier-ring-" + Guid.NewGuid().ToString("N"))

            IO.Directory.CreateDirectory ringRoot |> ignore

            let ring = LocalFileStorage.LocalFileStorage(ringRoot) :> BlobStorage.IBlobStorage

            let first = binding.Open()
            first.RegisterHandler("carried", recorder () |> snd)
            bind first (ringCarrier ring)
            let jobId = schedule first scope

            Expect.equal (fireAndRead first binding.ScopeId jobId) scope "under the carrier that issued the token"

            // Another key ring did not issue it: nothing re-mints.
            bind first (ScopeCarrier.ephemeral ())

            Expect.isTrue
                (fireAndRead first binding.ScopeId jobId).IsAnonymous
                "a carrier over another key ring re-mints nothing"

            // The issuing ring again — a fresh carrier over it, as after a
            // restart — re-mints the same scope.
            bind first (ringCarrier ring)
            Expect.equal (fireAndRead first binding.ScopeId jobId) scope "rebinding the issuing ring re-mints"

            binding.Close first
        }

        test "a scheduler nothing has bound re-mints within its lifetime, and runs a restarted job anonymous" {
            let binding = factory ()
            let scope = mint binding.ScopeId

            let first = binding.Open()
            first.RegisterHandler("carried", recorder () |> snd)
            let jobId = schedule first scope
            Expect.equal (fireAndRead first binding.ScopeId jobId) scope "within the scheduler's own lifetime"
            binding.Close first

            let second = binding.Open()

            Expect.isTrue
                (fireAndRead second binding.ScopeId jobId).IsAnonymous
                "after a restart with no key ring bound, the job runs anonymous — never a guess"

            binding.Close second
        }
    ]