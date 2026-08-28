// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Reporting.Tests.SubscriptionTests

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Reporting
open ToolUp.Reporting.Tests.Fakes

// ─── Phase 534 — scheduled report subscriptions ──────────────────────
//
// The four behaviours the phase's acceptance criteria name — cron
// round-trip, render + deliver, retry on failure, scope isolation —
// plus the compose diagnostic and the producer registry's refusals.
//
// Everything below runs against the SHIPPED store (blob-backed, over
// `ToolUp.Platform.Testing`'s in-memory `IBlobStorage`) and the SHIPPED
// `DataObjectStore`. Only the two seams with a network behind them (the
// sink, the address book) and the scheduler are doubles.

let private scopeA = "team-alpha"
let private scopeB = "team-beta"

let private templateId = "quarterly"

let private template: ReportTemplate = {
    Id = templateId
    DisplayName = "Quarterly summary"
    Format = Markdown
    Body = Encoding.UTF8.GetBytes "# {{title}}\n\nRevenue: {{revenue}}\n"
    Placeholders = [
        {
            Key = "title"
            DisplayName = "Title"
            Kind = Text
            Required = true
        }
        {
            Key = "revenue"
            DisplayName = "Revenue"
            Kind = Text
            Required = true
        }
    ]
    Version = 1
}

let private quarterParameter: ReportParameterSchema = {
    Key = "quarter"
    DisplayName = "Quarter"
    Kind = ChoiceParameter [ "Q1"; "Q2"; "Q3"; "Q4" ]
    Required = true
}

/// A producer that resolves its parameters into the fixture template's
/// two slots. Deliberately trivial: what is under test is the substrate
/// around it, not the report.
let private revenueProducer =
    ReportProducer.create "test.revenue" "Revenue summary" [ quarterParameter ] (fun _scope parameters -> async {
        let quarter = parameters.TryFind "quarter" |> Option.defaultValue "?"

        return
            Ok {
                TemplateId = templateId
                Values = Map [ "title", TextValue $"Revenue — {quarter}"; "revenue", TextValue "1,234" ]
                FileNameStem = None
            }
    })

let private newSubscription: NewReportSubscription = {
    DisplayName = "Monday revenue"
    ProducerKey = "test.revenue"
    Parameters = Map [ "quarter", "Q3" ]
    Schedule = "0 6 * * 1"
    RecipientUserIds = [ "alice" ]
    Format = Markdown
    Enabled = true
}

let private retryPolicy = {
    JobRetryPolicy.defaults with
        MaxAttempts = 3
}

/// One wired-up world. Every test builds its own, so nothing leaks
/// between cases.
type private World(?sink: RecordingSink, ?addresses: (string * string) list) =
    let blobs = ToolUp.Platform.Testing.Fakes.standardBlobStorage ()
    let subscriptions = ReportSubscriptionStore.create blobs

    let artefacts =
        ToolUp.Platform.DataObjectStore.DataObjectStore(blobs) :> IDataObjectStore

    let producers = ReportProducerRegistry()
    let templates = InMemoryTemplateStore()
    let renderers = ReportingCompose.buildDefaultRegistry ()
    let scheduler = RecordingScheduler()
    let audit = AuditCollector()
    let sink = defaultArg sink (RecordingSink())

    let addressBook =
        StubAddressBook(defaultArg addresses [ "alice", scopeA; "alice", scopeB; "bob", scopeA ])

    do
        producers.Register revenueProducer |> ignore
        templates.Seed(scopeA, template)
        templates.Seed(scopeB, template)

    member _.Subscriptions = subscriptions
    member _.Producers = producers
    member _.Templates = templates
    member _.Scheduler = scheduler
    member _.Audit = audit
    member _.Sink = sink
    member _.Artefacts = artefacts

    member _.JobDeps: ReportSubscriptionJobDeps = {
        Subscriptions = subscriptions
        Producers = producers
        Templates = templates
        Renderers = renderers
        Artefacts = artefacts
        AddressBook = addressBook
        Sink = sink
        Audit = audit.Callback
        Config = ReportApiConfig.defaults
        RetryPolicy = retryPolicy
        Disclosure = None
    }

    member _.ApiDeps: ReportSubscriptionApiHandler.ReportSubscriptionApiDeps = {
        Subscriptions = subscriptions
        Producers = producers
        Scheduler = scheduler
        RetryPolicy = retryPolicy
        Precision = JobPrecision.Minute
    }

    member this.Handler = ReportSubscriptionJobHandler.create this.JobDeps

    /// The API handler, with the job handler already registered against
    /// the scheduler — which is what a composition root does, and what
    /// `Schedule` refuses without.
    member this.Api(scopeId: string) =
        (scheduler :> IJobScheduler).RegisterHandler(ReportSubscription.JobHandlerName, this.Handler)

        ReportSubscriptionApiHandler.create this.ApiDeps "operator" scopeId

let private run = Async.RunSynchronously

let private expectOk result =
    match result with
    | Ok value -> value
    | Error e -> failtestf "expected Ok, got Error %A" e

let private expectError result =
    match result with
    | Error e -> e
    | Ok value -> failtestf "expected Error, got Ok %A" value

[<Tests>]
let tests =
    testList "Phase 534 — scheduled report subscriptions" [

        testList "534.A — producer registry" [

            test "a producer is discoverable by key and by descriptor" {
                let registry = ReportProducerRegistry()
                registry.Register revenueProducer |> expectOk

                Expect.isSome (registry.TryResolve "test.revenue") "the registered key resolves"
                Expect.isNone (registry.TryResolve "test.absent") "an unregistered key resolves to None"

                Expect.equal
                    (registry.Descriptors |> List.map _.Key)
                    [ "test.revenue" ]
                    "the descriptor set is what an admin surface lists"
            }

            test "a contested key is refused, naming both claimants" {
                let registry = ReportProducerRegistry()
                registry.Register revenueProducer |> expectOk

                let rival =
                    ReportProducer.create "test.revenue" "Someone else's revenue" [] (fun _ _ -> async {
                        return Error "never called"
                    })

                let error = registry.Register rival |> expectError

                Expect.stringContains error "already registered" "the refusal says why"
                Expect.stringContains error "Revenue summary" "it names the incumbent"
                Expect.stringContains error "Someone else's revenue" "and the challenger"

                Expect.equal
                    (registry.TryResolve "test.revenue" |> Option.map _.Descriptor.DisplayName)
                    (Some "Revenue summary")
                    "the incumbent is not displaced by the refused registration"
            }

            test "parameter validation refuses a missing required value and an out-of-set choice" {
                let schema = [ quarterParameter ]

                Expect.equal
                    (ReportSubscription.validateParameters schema Map.empty)
                    (Error(InvalidParameter("quarter", "required, but no value was supplied")))
                    "an absent required parameter is refused"

                // Whitespace is the same condition as absence — a form
                // posts "" for a field the user left alone.
                Expect.isError
                    (ReportSubscription.validateParameters schema (Map [ "quarter", "  " ]))
                    "a blank required parameter is refused"

                let outOfSet =
                    ReportSubscription.validateParameters schema (Map [ "quarter", "Q9" ])
                    |> expectError

                match outOfSet with
                | InvalidParameter("quarter", reason) ->
                    Expect.stringContains reason "Q9" "the refusal quotes the offending value"
                    Expect.stringContains reason "Q1" "and enumerates the permitted set"
                | other -> failtestf "expected InvalidParameter, got %A" other

                Expect.equal
                    (ReportSubscription.validateParameters schema (Map [ "quarter", "Q3" ]))
                    (Ok())
                    "a valid value passes"
            }

            test "an unrestricted producer accepts any format; a restricted one does not" {
                Expect.equal (ReportSubscription.validateFormat [] Docx) (Ok()) "no declaration means no restriction"

                Expect.equal
                    (ReportSubscription.validateFormat [ Markdown; Html ] Markdown)
                    (Ok())
                    "a declared format is accepted"

                Expect.equal
                    (ReportSubscription.validateFormat [ Markdown; Html ] Docx)
                    (Error(UnsupportedFormat(Docx, [ Markdown; Html ])))
                    "an undeclared format is refused, naming what IS served"
            }
        ]

        testList "534.A — the subscription store" [

            test "a subscription round-trips through the blob store with its cron intact" {
                let world = World()
                let api = world.Api scopeA

                let created = api.CreateSubscription newSubscription |> run |> expectOk

                Expect.equal created.Schedule "0 6 * * 1" "the cron expression survives the write"
                Expect.equal created.ScopeId scopeA "the scope is stamped server-side"
                Expect.equal created.CreatedBy "operator" "so is the principal"
                Expect.equal created.LastRun NeverRun "a fresh subscription has never run"

                let read = world.Subscriptions.Get(scopeA, created.Id) |> run |> Option.get

                Expect.equal read created "the record read back is the record written"

                Expect.equal
                    (api.ListSubscriptions() |> run |> List.map _.Id)
                    [ created.Id ]
                    "and it lists at its scope"
            }

            test "an unparseable cron is refused at create, not discovered at the first tick" {
                let world = World()
                let api = world.Api scopeA

                let error =
                    api.CreateSubscription {
                        newSubscription with
                            Schedule = "every monday please"
                    }
                    |> run
                    |> expectError

                match error with
                | InvalidSchedule(expr, reason) ->
                    Expect.equal expr "every monday please" "the refusal round-trips the expression"
                    Expect.isNotEmpty reason "and carries the parser's reason"
                | other -> failtestf "expected InvalidSchedule, got %A" other

                Expect.isEmpty (world.Subscriptions.List scopeA |> run) "nothing was persisted"
                Expect.isEmpty world.Scheduler.Jobs "and nothing was scheduled"
            }

            test "a subscription with no recipients is refused" {
                let world = World()

                let error =
                    (world.Api scopeA).CreateSubscription {
                        newSubscription with
                            RecipientUserIds = []
                    }
                    |> run
                    |> expectError

                Expect.equal error NoRecipients "a report delivered to nobody is a mistake, not a posture"
            }

            test "a subscription naming an unregistered producer is refused" {
                let world = World()

                let error =
                    (world.Api scopeA).CreateSubscription {
                        newSubscription with
                            ProducerKey = "test.absent"
                    }
                    |> run
                    |> expectError

                Expect.equal error (UnknownProducer "test.absent") "the key is named back"
            }

            test "creating a subscription registers a cron job carrying its id" {
                let world = World()

                let created =
                    (world.Api scopeA).CreateSubscription newSubscription |> run |> expectOk

                let job =
                    world.Scheduler.JobFor created.Id
                    |> Option.defaultWith (fun () -> failtest "no job was registered for the subscription")

                Expect.equal job.Handler ReportSubscription.JobHandlerName "under the platform handler name"
                Expect.equal job.Trigger (CronTrigger "0 6 * * 1") "with the subscription's schedule"
                Expect.equal job.ScopeId scopeA "at the subscription's scope"
                Expect.equal job.RetryPolicy retryPolicy "carrying the composed retry policy"

                Expect.equal
                    (ReportSubscriptionJobHandler.decodePayload job.Payload
                     |> Option.map _.SubscriptionId)
                    (Some created.Id)
                    "and a payload the handler can decode back to the subscription id"
            }

            test "re-saving a subscription updates its job rather than accumulating one per save" {
                let world = World()
                let api = world.Api scopeA
                let created = api.CreateSubscription newSubscription |> run |> expectOk

                let updated =
                    api.UpdateSubscription(
                        created.Id,
                        {
                            newSubscription with
                                Schedule = "30 7 * * 2"
                        }
                    )
                    |> run
                    |> expectOk

                Expect.equal updated.Schedule "30 7 * * 2" "the new schedule is stored"
                Expect.equal updated.CreatedAt created.CreatedAt "provenance is preserved across an update"
                Expect.equal (List.length world.Scheduler.Jobs) 1 "the idempotency key kept it to one job"

                Expect.equal
                    (world.Scheduler.JobFor created.Id |> Option.map _.Trigger)
                    (Some(CronTrigger "30 7 * * 2"))
                    "and that one job took the new cron"
            }

            test "pausing disables the job and keeps the record; resuming re-enables it" {
                let world = World()
                let api = world.Api scopeA
                let created = api.CreateSubscription newSubscription |> run |> expectOk

                let paused = api.SetSubscriptionEnabled(created.Id, false) |> run |> expectOk
                Expect.isFalse paused.Enabled "the record records the pause"

                Expect.equal
                    (world.Scheduler.JobFor created.Id |> Option.map _.Status)
                    (Some JobStatus.Disabled)
                    "and the job is disabled rather than cancelled"

                let resumed = api.SetSubscriptionEnabled(created.Id, true) |> run |> expectOk
                Expect.isTrue resumed.Enabled "resuming flips it back"

                Expect.equal
                    (world.Scheduler.JobFor created.Id |> Option.map _.Status)
                    (Some JobStatus.Active)
                    "and re-enables the job"
            }

            test "deleting cancels the job and removes the record" {
                let world = World()
                let api = world.Api scopeA
                let created = api.CreateSubscription newSubscription |> run |> expectOk

                api.DeleteSubscription created.Id |> run |> expectOk

                Expect.isNone (world.Subscriptions.Get(scopeA, created.Id) |> run) "the record is gone"
                Expect.isNone (world.Scheduler.JobFor created.Id) "and so is its job"
            }

            test "run-now fires the job through the scheduler rather than inline" {
                let world = World()
                let api = world.Api scopeA
                let created = api.CreateSubscription newSubscription |> run |> expectOk

                api.RunSubscriptionNow created.Id |> run |> expectOk

                Expect.equal (List.length world.Scheduler.Triggered) 1 "exactly one manual fire"

                Expect.equal
                    (world.Scheduler.Triggered |> List.map snd)
                    [ "operator" ]
                    "attributed to the calling principal, so the run history says who asked"
            }
        ]

        testList "534.B — execution" [

            test "a scheduled run renders, versions the artefact, and delivers to the recipients" {
                let world = World()

                let created =
                    (world.Api scopeA).CreateSubscription newSubscription |> run |> expectOk

                let result =
                    world.Handler.Execute(jobContext scopeA 1 (ReportSubscriptionJobHandler.encodePayload created.Id))
                    |> run

                Expect.equal result Success "the run succeeded"

                // Delivered.
                Expect.equal (List.length world.Sink.Emails) 1 "one email was sent"
                let scope, email = world.Sink.Emails |> List.head
                Expect.equal scope scopeA "at the subscription's scope"
                Expect.equal email.RecipientUserIds [ "alice" ] "to the resolvable recipient"

                Expect.isSome email.CorrelationId "carrying a correlation id so a retry de-duplicates at the vendor"

                // Versioned, and browsable through the ordinary
                // data-object surface.
                let objectId = ReportSubscription.artefactObjectId created.Id
                let stored = world.Artefacts.Get(scopeA, objectId) |> run

                match stored with
                | Ok(artefact, bytes) ->
                    Expect.equal artefact.Version 1 "the first run is version 1"

                    Expect.stringContains
                        (Encoding.UTF8.GetString bytes)
                        "Revenue — Q3"
                        "and the artefact is the rendered report, with the producer's parameters applied"
                | Error e -> failtestf "expected the artefact to be readable, got %A" e

                // Recorded on the subscription.
                let after = world.Subscriptions.Get(scopeA, created.Id) |> run |> Option.get

                match after.LastRun with
                | RunSucceeded(_, key, version, deliveredTo) ->
                    Expect.equal key objectId "the outcome points at the artefact"
                    Expect.equal version 1 "at the version it wrote"
                    Expect.equal deliveredTo 1 "and records who it reached"
                | other -> failtestf "expected RunSucceeded, got %A" other

                // Audited.
                match world.Audit.Last with
                | Some entry ->
                    Expect.equal entry.SubscriptionId created.Id "the audit names the subscription"
                    Expect.equal entry.Failure None "and records no failure"
                    Expect.equal entry.DeliveredTo 1 "with the delivery count"
                | None -> failtest "the run emitted no audit event"
            }

            test "a second run appends a version — the run history IS the version chain" {
                let world = World()

                let created =
                    (world.Api scopeA).CreateSubscription newSubscription |> run |> expectOk

                let payload = ReportSubscriptionJobHandler.encodePayload created.Id

                world.Handler.Execute(jobContext scopeA 1 payload) |> run |> ignore
                world.Handler.Execute(jobContext scopeA 1 payload) |> run |> ignore

                let versions =
                    world.Artefacts.ListVersions(scopeA, ReportSubscription.artefactObjectId created.Id)
                    |> run

                Expect.equal (List.length versions) 2 "two runs, two versions"
            }

            test "a recipient with no resolvable address is skipped, not a failure" {
                // bob has no address at scopeA in this world.
                let world = World(addresses = [ "alice", scopeA ])

                let created =
                    (world.Api scopeA).CreateSubscription {
                        newSubscription with
                            RecipientUserIds = [ "alice"; "bob" ]
                    }
                    |> run
                    |> expectOk

                let result =
                    world.Handler.Execute(jobContext scopeA 1 (ReportSubscriptionJobHandler.encodePayload created.Id))
                    |> run

                Expect.equal result Success "the run still succeeds"

                let _, email = world.Sink.Emails |> List.head
                Expect.equal email.RecipientUserIds [ "alice" ] "only the reachable recipient is addressed"

                match world.Audit.Last with
                | Some entry ->
                    Expect.equal entry.DeliveredTo 1 "one delivered"
                    Expect.equal entry.SkippedRecipients 1 "one skipped, and visible to an operator"
                | None -> failtest "no audit event"
            }

            test "a subscription nobody can be reached at fails terminally, naming the condition" {
                let world = World(addresses = [])

                let created =
                    (world.Api scopeA).CreateSubscription newSubscription |> run |> expectOk

                let result =
                    world.Handler.Execute(jobContext scopeA 1 (ReportSubscriptionJobHandler.encodePayload created.Id))
                    |> run

                match result with
                | PermanentFailure reason ->
                    Expect.stringContains reason "no recipient" "the reason names what is wrong"
                | other -> failtestf "expected PermanentFailure, got %A" other
            }

            test "a producer that cannot resolve fails permanently — waiting will not help" {
                let world = World()

                world.Producers.Register(
                    ReportProducer.create "test.broken" "Broken" [] (fun _ _ -> async {
                        return Error "the upstream dataset was retired"
                    })
                )
                |> expectOk

                let created =
                    (world.Api scopeA).CreateSubscription {
                        newSubscription with
                            ProducerKey = "test.broken"
                            Parameters = Map.empty
                    }
                    |> run
                    |> expectOk

                let result =
                    world.Handler.Execute(jobContext scopeA 1 (ReportSubscriptionJobHandler.encodePayload created.Id))
                    |> run

                match result with
                | PermanentFailure reason ->
                    Expect.stringContains reason "the upstream dataset was retired" "the producer's reason survives"
                | other -> failtestf "expected PermanentFailure, got %A" other

                // No REPORT was delivered — but the terminal failure did
                // announce itself, which is the whole point of the
                // warning path and not an absence of delivery.
                Expect.equal
                    world.Sink.Subjects
                    [ "Monday revenue — scheduled report failed" ]
                    "the only thing sent was the failure warning"
            }

            test "a deleted or paused subscription is a clean no-op, never a dead-letter" {
                let world = World()
                let api = world.Api scopeA
                let created = api.CreateSubscription newSubscription |> run |> expectOk
                let payload = ReportSubscriptionJobHandler.encodePayload created.Id

                api.SetSubscriptionEnabled(created.Id, false) |> run |> expectOk |> ignore

                Expect.equal
                    (world.Handler.Execute(jobContext scopeA 1 payload) |> run)
                    Success
                    "a paused subscription does nothing and reports success"

                api.DeleteSubscription created.Id |> run |> expectOk

                Expect.equal
                    (world.Handler.Execute(jobContext scopeA 1 payload) |> run)
                    Success
                    "so does one deleted between the tick and the dispatch"

                Expect.isEmpty world.Sink.Emails "and neither delivered anything"
            }

            test "a malformed payload fails permanently" {
                let world = World()

                match world.Handler.Execute(jobContext scopeA 1 "{\"nonsense\":true}") |> run with
                | PermanentFailure _ -> ()
                | other -> failtestf "expected PermanentFailure, got %A" other
            }
        ]

        testList "534.B — retry" [

            test "a transient sink failure retries, then goes terminal on the last attempt" {
                let sink = RecordingSink()
                sink.Program(SinkResult.TransientFailure "vendor 503")
                sink.Program(SinkResult.TransientFailure "vendor 503")
                sink.Program(SinkResult.TransientFailure "vendor 503")

                let world = World(sink = sink)

                let created =
                    (world.Api scopeA).CreateSubscription newSubscription |> run |> expectOk

                let payload = ReportSubscriptionJobHandler.encodePayload created.Id

                // Attempts 1 and 2 of 3: the scheduler should try again.
                for attempt in 1..2 do
                    match world.Handler.Execute(jobContext scopeA attempt payload) |> run with
                    | TransientFailure reason ->
                        Expect.stringContains reason "vendor 503" "the vendor's reason rides the failure"
                    | other -> failtestf "attempt %d: expected TransientFailure, got %A" attempt other

                let midway = world.Subscriptions.Get(scopeA, created.Id) |> run |> Option.get

                match midway.LastRun with
                | RunFailed(_, _, terminal) -> Expect.isFalse terminal "a retryable attempt is not recorded as terminal"
                | other -> failtestf "expected RunFailed, got %A" other

                // Attempt 3 of 3 exhausts the composed policy.
                match world.Handler.Execute(jobContext scopeA 3 payload) |> run with
                | PermanentFailure _ -> ()
                | other -> failtestf "attempt 3: expected PermanentFailure, got %A" other

                let after = world.Subscriptions.Get(scopeA, created.Id) |> run |> Option.get

                match after.LastRun with
                | RunFailed(_, _, terminal) -> Expect.isTrue terminal "the last attempt is recorded as terminal"
                | other -> failtestf "expected RunFailed, got %A" other

                match world.Audit.Last with
                | Some entry ->
                    Expect.isTrue entry.Terminal "and audited as terminal"
                    Expect.equal entry.Attempt 3 "at the attempt that exhausted the budget"
                | None -> failtest "no audit event"

                // The terminal attempt announces itself — the people who
                // would have received the report are told it did not
                // arrive.
                Expect.contains
                    world.Sink.Subjects
                    "Monday revenue — scheduled report failed"
                    "the terminal failure sent a warning notification"

                Expect.equal
                    (world.Sink.Subjects |> List.filter (fun s -> s.EndsWith "failed") |> List.length)
                    1
                    "exactly one warning — the retryable attempts stayed quiet"
            }

            test "a permanent sink failure is terminal on the first attempt" {
                let sink = RecordingSink()
                sink.Program(SinkResult.PermanentFailure "recipient rejected")

                let world = World(sink = sink)

                let created =
                    (world.Api scopeA).CreateSubscription newSubscription |> run |> expectOk

                match
                    world.Handler.Execute(jobContext scopeA 1 (ReportSubscriptionJobHandler.encodePayload created.Id))
                    |> run
                with
                | PermanentFailure reason ->
                    Expect.stringContains reason "recipient rejected" "the sink's reason survives"
                | other -> failtestf "expected PermanentFailure, got %A" other

                Expect.contains
                    world.Sink.Subjects
                    "Monday revenue — scheduled report failed"
                    "and it announces immediately rather than after a retry budget it will not use"
            }

            test "a sink-level skip is a success, not a retry" {
                let sink = RecordingSink()
                sink.Program(SinkResult.Skipped "team preferences disabled email")

                let world = World(sink = sink)

                let created =
                    (world.Api scopeA).CreateSubscription newSubscription |> run |> expectOk

                Expect.equal
                    (world.Handler.Execute(jobContext scopeA 1 (ReportSubscriptionJobHandler.encodePayload created.Id))
                     |> run)
                    Success
                    "the sink decided correctly not to deliver; retrying reaches the same decision"

                match world.Audit.Last with
                | Some entry -> Expect.equal entry.DeliveredTo 0 "and the audit is honest that nobody received it"
                | None -> failtest "no audit event"
            }
        ]

        testList "534 — scope isolation (GP 4)" [

            test "a subscription created at one scope is invisible at another" {
                let world = World()

                let created =
                    (world.Api scopeA).CreateSubscription newSubscription |> run |> expectOk

                Expect.isEmpty ((world.Api scopeB).ListSubscriptions() |> run) "scope B lists nothing"

                Expect.isNone
                    (world.Subscriptions.Get(scopeB, created.Id) |> run)
                    "and cannot read scope A's record by id"
            }

            test "an operation naming another scope's id reports not-found, never someone else's data" {
                let world = World()

                let created =
                    (world.Api scopeA).CreateSubscription newSubscription |> run |> expectOk

                let apiB = world.Api scopeB

                Expect.equal
                    (apiB.DeleteSubscription created.Id |> run)
                    (Error(SubscriptionNotFound created.Id))
                    "delete refuses"

                Expect.equal
                    (apiB.RunSubscriptionNow created.Id |> run)
                    (Error(SubscriptionNotFound created.Id))
                    "run-now refuses"

                Expect.equal
                    (apiB.SetSubscriptionEnabled(created.Id, false) |> run)
                    (Error(SubscriptionNotFound created.Id))
                    "and so does pause — the same answer a genuinely absent id gets, so enumeration learns nothing"

                Expect.isSome
                    (world.Subscriptions.Get(scopeA, created.Id) |> run)
                    "and the subscription at scope A is untouched by all three"
            }

            test "a dispatch carrying another scope's context finds nothing and does nothing" {
                let world = World()

                let created =
                    (world.Api scopeA).CreateSubscription newSubscription |> run |> expectOk

                let result =
                    world.Handler.Execute(jobContext scopeB 1 (ReportSubscriptionJobHandler.encodePayload created.Id))
                    |> run

                Expect.equal result Success "the lookup is scoped to the dispatch context"
                Expect.isEmpty world.Sink.Emails "so nothing crossed the scope boundary"
            }

            test "the store writes into the scope it is told, not the one on the record" {
                let world = World()

                let forged = {
                    Id = "forged"
                    ScopeId = scopeB // a caller asserting someone else's scope
                    DisplayName = "Forged"
                    ProducerKey = "test.revenue"
                    Parameters = Map.empty
                    Schedule = "0 6 * * 1"
                    RecipientUserIds = [ "alice" ]
                    Format = Markdown
                    Enabled = true
                    LastRun = NeverRun
                    CreatedBy = "attacker"
                    CreatedAt = DateTimeOffset.UtcNow
                }

                let saved = world.Subscriptions.Save(scopeA, forged) |> run |> expectOk

                Expect.equal saved.ScopeId scopeA "the argument scope wins over the record's own field"
                Expect.isNone (world.Subscriptions.Get(scopeB, "forged") |> run) "nothing landed at the asserted scope"
                Expect.isSome (world.Subscriptions.Get(scopeA, "forged") |> run) "it landed where the caller was"
            }
        ]

        testList "534.C — the management gate" [

            test "the gate refuses every mutation and leaves the reads alone" {
                let world = World()

                let created =
                    (world.Api scopeA).CreateSubscription newSubscription |> run |> expectOk

                let denied =
                    world.Api scopeA
                    |> ReportSubscriptionApiHandler.withManagementGate (fun () -> async { return false })

                let expectDenied name result =
                    match result with
                    | Error(SubscriptionNotAuthorised reason) ->
                        Expect.equal
                            reason
                            ReportSubscriptionApiHandler.SubscriptionManagementDenied
                            $"{name} returns the shared refusal constant"
                    | other -> failtestf "%s: expected the management refusal, got %A" name other

                denied.CreateSubscription newSubscription |> run |> expectDenied "create"

                denied.UpdateSubscription(created.Id, newSubscription)
                |> run
                |> expectDenied "update"

                denied.SetSubscriptionEnabled(created.Id, false) |> run |> expectDenied "pause"
                denied.DeleteSubscription created.Id |> run |> expectDenied "delete"
                denied.RunSubscriptionNow created.Id |> run |> expectDenied "run-now"

                Expect.equal
                    (denied.ListSubscriptions() |> run |> List.length)
                    1
                    "listing is untouched — seeing whether reports work is the ordinary operator question"

                Expect.equal (denied.ListProducers() |> run |> List.length) 1 "and so is listing producers"

                Expect.isSome
                    (world.Subscriptions.Get(scopeA, created.Id) |> run)
                    "no refused mutation reached the store"
            }

            test "the gate is consulted per call, not snapshotted at construction" {
                let world = World()
                let mutable permitted = false

                let gated =
                    world.Api scopeA
                    |> ReportSubscriptionApiHandler.withManagementGate (fun () -> async { return permitted })

                Expect.isError (gated.CreateSubscription newSubscription |> run) "denied while the predicate says no"

                permitted <- true

                Expect.isOk
                    (gated.CreateSubscription newSubscription |> run)
                    "and permitted the moment it changes its mind — no restart, no rebuild"
            }
        ]

        testList "534.D — compose" [

            test "composing subscriptions without the seams they need fails loudly, naming all of them" {
                let world = World()

                let named =
                    try
                        ReportingCompose.withReportSubscriptions
                            [ "IJobScheduler"; "IDataObjectStore" ]
                            world.JobDeps
                            world.ApiDeps
                        |> ignore

                        failtest "a deployment missing a seam must not get a silently dead feature"
                    with ReportingCompose.ReportSubscriptionsNotComposable missing ->
                        missing

                Expect.equal
                    named
                    [ "IJobScheduler"; "IDataObjectStore" ]
                    "every missing seam is named at once — reporting them one restart at a time is the failure mode"

                let message = ReportingCompose.subscriptionDiagnostic [ "IJobScheduler" ]
                Expect.stringContains message "IJobScheduler" "the diagnostic names the seam"
                Expect.stringContains message "ServerConfig.JobScheduler" "and how to supply it"
            }

            test "composing with every seam present yields a handler and an API factory" {
                let world = World()

                let handler, apiFactory =
                    ReportingCompose.withReportSubscriptions [] world.JobDeps world.ApiDeps

                Expect.isNotNull (box handler) "the job handler is returned for registration"

                let api = apiFactory "operator" scopeA
                Expect.isEmpty (api.ListSubscriptions() |> run) "and the API factory produces a scoped handler"
            }

            test "a retry policy that differs between the two handlers is refused at compose" {
                let world = World()

                let mismatched = {
                    world.ApiDeps with
                        RetryPolicy = { retryPolicy with MaxAttempts = 7 }
                }

                Expect.throwsT<ReportingCompose.ReportSubscriptionsNotComposable>
                    (fun () -> ReportingCompose.withReportSubscriptions [] world.JobDeps mismatched |> ignore)
                    "the handler's terminal-failure judgement and the job's retry budget must be one value"
            }
        ]
    ]