// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Reporting.Tests.Fakes

open System
open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Reporting
open ToolUp.Reporting.IReportTemplateStore

// ─── In-memory doubles for the seams Phase 534 composes ──────────────
//
// The real `IBlobStorage` (`ToolUp.Platform.Testing.standardBlobStorage`)
// and the real `DataObjectStore` over it are used wherever possible, so
// the store tests exercise the shipped blob layout rather than a
// stand-in that agrees with it. Only the two seams with a network on the
// other side — the notification sink and the address book — are faked,
// plus the scheduler, whose in-process implementation runs a hosted
// timer this pack has no business starting.

/// Records every envelope handed to it, and answers with whatever the
/// caller programmed. `Responses` is a queue: the first `Send` takes the
/// first entry, and a run out of entries takes `Fallback` — which is how
/// the retry test says "fail, fail, then succeed" without a clock.
type RecordingSink(?fallback: SinkResult) =
    let sent = ResizeArray<string * NotificationEnvelope>()
    let responses = ConcurrentQueue<SinkResult>()
    let fallback = defaultArg fallback (SinkResult.Delivered(Some "test-message-id"))

    member _.Program(result: SinkResult) = responses.Enqueue result

    /// Every `(scopeId, envelope)` in the order it was sent.
    member _.Sent = sent |> List.ofSeq

    /// Just the email envelopes, which is what a report delivery is.
    member _.Emails =
        sent
        |> Seq.choose (fun (scope, envelope) ->
            match envelope.Notification with
            | TransactionalEmail email -> Some(scope, email)
            | _ -> None)
        |> List.ofSeq

    /// The subject lines of every inline email sent, in order. The
    /// cheapest way to tell a delivery from a failure warning.
    member this.Subjects =
        this.Emails
        |> List.choose (fun (_, email) ->
            match email.Content with
            | InlineEmail(subject, _, _) -> Some subject
            | TemplatedEmail _ -> None)

    interface INotificationSink with
        member _.Kind = NotificationKind.SinkKind.Email
        member _.Provider = "Recording"

        member _.Send(scopeId, envelope) = async {
            lock sent (fun () -> sent.Add(scopeId, envelope))

            match responses.TryDequeue() with
            | true, programmed -> return programmed
            | _ -> return fallback
        }

/// Resolves an address for exactly the `(userId, scopeId)` pairs it was
/// given. Everyone else resolves to `None`, which the address-book
/// contract calls a skip rather than a failure — the condition the
/// delivery path has to distinguish from an error.
type StubAddressBook(known: (string * string) list) =
    let known = Set.ofList known

    interface INotificationAddressBook with
        member _.ResolveEmail(userId, scopeId) = async {
            return
                if known.Contains(userId, scopeId) then
                    Some {
                        Address = $"{userId}@example.test"
                        DisplayName = None
                    }
                else
                    None
        }

        member _.ResolvePhone(_, _) = async { return None }
        member _.ResolvePushTokens(_, _) = async { return [] }

/// In-memory `IReportTemplateStore`, scope-keyed.
type InMemoryTemplateStore() =
    let templates = ConcurrentDictionary<string * TemplateId, ReportTemplate>()

    member _.Seed(scopeId: string, template: ReportTemplate) =
        templates[(scopeId, template.Id)] <- template

    interface IReportTemplateStore with
        member _.List scopeId = async {
            return
                templates
                |> Seq.filter (fun kv -> fst kv.Key = scopeId)
                |> Seq.map _.Value
                |> List.ofSeq
        }

        member _.Get(scopeId, id) = async {
            match templates.TryGetValue((scopeId, id)) with
            | true, template -> return Some template
            | _ -> return None
        }

        member _.Save(scopeId, template) = async {
            templates[(scopeId, template.Id)] <- template
            return Ok template
        }

        member _.Delete(scopeId, id) = async {
            templates.TryRemove((scopeId, id)) |> ignore
            return Ok()
        }

/// In-memory `IJobScheduler` that records rather than dispatches.
///
/// The in-process scheduler runs a hosted timer and a persistent store;
/// what these tests need is the *bookkeeping* half — did a create
/// register a job with the right cron and payload, did a pause disable
/// it, did run-now fire it — with the handler invoked directly so its
/// behaviour is observed rather than raced.
type RecordingScheduler() =
    let jobs = ConcurrentDictionary<JobId, JobDefinition>()
    let handlers = ConcurrentDictionary<string, IJobHandler>()
    let triggered = ResizeArray<JobId * string>()

    member _.Jobs = jobs.Values |> List.ofSeq
    member _.Triggered = triggered |> List.ofSeq
    member _.Handlers = handlers.Keys |> List.ofSeq

    member _.JobFor(subscriptionId: string) =
        jobs.Values
        |> Seq.tryFind (fun j -> j.Tags.TryFind "subscriptionId" = Some subscriptionId)

    interface IJobScheduler with
        member _.RegisterHandler(name, handler) = handlers[name] <- handler

        member this.RegisterHandlerAsync(name, handler) = async {
            (this :> IJobScheduler).RegisterHandler(name, handler)
            return Ok()
        }

        member _.Schedule registration = async {
            // Mirror the real scheduler's two refusals the API handler
            // maps onto typed errors: an unparseable cron, and an
            // unregistered handler name.
            match registration.Trigger with
            | CronTrigger expr ->
                match CronExpression.tryParse expr with
                | Error reason -> return Result.Error(InvalidCron(expr, reason))
                | Ok _ ->
                    if not (handlers.ContainsKey registration.Handler) then
                        return Result.Error(HandlerNotRegistered registration.Handler)
                    else
                        // Idempotency by key, as the contract promises:
                        // re-scheduling an existing subscription returns
                        // the existing job rather than a duplicate.
                        let existing =
                            registration.Idempotency
                            |> Option.bind (fun key ->
                                jobs.Values
                                |> Seq.tryFind (fun j -> j.Idempotency |> Option.map _.Key = Some key.Key))

                        match existing with
                        | Some job ->
                            jobs[job.JobId] <- {
                                job with
                                    Trigger = registration.Trigger
                                    Payload = registration.Payload
                                    RetryPolicy = registration.RetryPolicy
                                    Tags = registration.Tags
                            }

                            return Result.Ok job.JobId
                        | None ->
                            let jobId = Guid.NewGuid()

                            jobs[jobId] <- {
                                JobId = jobId
                                ScopeId = registration.ScopeId
                                Handler = registration.Handler
                                Payload = registration.Payload
                                Trigger = registration.Trigger
                                Idempotency = registration.Idempotency
                                RetryPolicy = registration.RetryPolicy
                                ShardKey = registration.ShardKey
                                Precision = registration.Precision
                                Status = JobStatus.Active
                                CreatedAt = DateTime.UtcNow
                                CreatedBy = registration.CreatedBy
                                NextRunAt = None
                                LastRunAt = None
                                LastRunStatus = None
                                LastRunError = None
                                ConsecutiveFailures = 0
                                Tags = registration.Tags
                            }

                            return Result.Ok jobId
            | _ -> return Result.Error(ScheduleError.StorageFailure "this fake only schedules cron triggers")
        }

        member _.Cancel(_, jobId) = async {
            jobs.TryRemove jobId |> ignore
            return ()
        }

        member _.Disable(_, jobId) = async {
            match jobs.TryGetValue jobId with
            | true, job -> jobs[jobId] <- { job with Status = JobStatus.Disabled }
            | _ -> ()

            return ()
        }

        member _.Enable(_, jobId) = async {
            match jobs.TryGetValue jobId with
            | true, job -> jobs[jobId] <- { job with Status = JobStatus.Active }
            | _ -> ()

            return ()
        }

        member _.Get(_, jobId) = async {
            match jobs.TryGetValue jobId with
            | true, job -> return Some job
            | _ -> return None
        }

        member _.ListJobs scopeId = async {
            return jobs.Values |> Seq.filter (fun j -> j.ScopeId = scopeId) |> List.ofSeq
        }

        member _.GetRecentRuns(_, _, _) = async { return [] }

        member _.TriggerOnce(_, jobId, byUserId) = async {
            match jobs.TryGetValue jobId with
            | true, _ ->
                lock triggered (fun () -> triggered.Add(jobId, byUserId))
                return Result.Ok()
            | _ -> return Result.Error "unknown job"
        }

        member _.NotifyEventWritten(_, _, _) = async { return () }

/// Collects every `SubscriptionRunAudit` the job handler emits.
type AuditCollector() =
    let entries = ResizeArray<SubscriptionRunAudit>()
    member _.Entries = entries |> List.ofSeq
    member _.Last = entries |> Seq.tryLast

    member _.Callback: AuditOnSubscriptionRun =
        fun entry -> async { lock entries (fun () -> entries.Add entry) }

/// A `JobContext` for one dispatch attempt. Only the fields the
/// subscription handler reads carry meaning; the rest are the shapes the
/// scheduler would stamp.
let jobContext (scopeId: string) (attempt: int) (payload: string) : JobContext = {
    JobId = Guid.NewGuid()
    ScopeId = scopeId
    // The shape the real scheduler synthesises: scope identity on the
    // subject, unrestricted permissions — a cron run has no caller whose
    // authority it could inherit.
    AccessContext = AccessContext.unrestricted (TeamMember("_system", scopeId))
    Attempt = attempt
    Trigger = CronTrigger "0 6 * * 1"
    TriggerSource = ScheduledByCron
    ScheduledAt = DateTime.UtcNow
    RunningAt = DateTime.UtcNow
    Payload = payload
    DeadLetterDestination = None
}