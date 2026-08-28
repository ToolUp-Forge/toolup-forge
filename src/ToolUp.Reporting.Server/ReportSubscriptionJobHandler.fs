namespace ToolUp.Reporting

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Reporting.IReportTemplateStore
open ToolUp.Reporting.RendererRegistry

// ─── Phase 534.B — subscription execution ────────────────────────────
//
// The `IJobHandler` registered under
// `ReportSubscription.JobHandlerName`. One dispatch = one subscription
// run:
//
//   resolve subscription  → skip cleanly when disabled or deleted
//   resolve producer      → parameters into a render request
//   render                → through the Phase 23 registry, so a
//                           subscription and an interactive render
//                           reach the same renderer by the same route
//   persist               → IDataObjectStore, versioned: the run
//                           history IS the object's version chain
//   deliver               → INotificationAddressBook resolves who can
//                           actually be reached, INotificationSink
//                           delivers
//   record                → the outcome lands on the subscription
//
// **Stateless between invocations** (GP 12 rule 4). Everything the
// handler needs arrives through `JobContext` plus the dependencies it
// closed over at compose time. Nothing is cached across runs; a
// deactivated grain or a restarted actor re-reads the subscription and
// behaves identically.
//
// ─── Failure posture, and the one thing it cannot see ────────────────
//
// Failures ride the scheduler's retry policy: a transient one (the sink
// reported a 5xx, the blob store was briefly unreachable) returns
// `TransientFailure` and the scheduler backs off per the job's
// `JobRetryPolicy`. A permanent one — a producer that cannot answer for
// these parameters, a template that no longer exists, a format no
// renderer serves — returns `PermanentFailure`, because none of those
// resolve by waiting.
//
// The terminal-failure obligation ("warning notification + audit
// event") needs to know an attempt was the LAST one, and `JobContext`
// carries `Attempt` but not `MaxAttempts` — the retry policy lives on
// the persisted `JobDefinition`, which the handler never receives.
// Rather than guess, the handler is handed the same `JobRetryPolicy`
// the compose registers the job with, so the two cannot drift: the
// compose surface takes one policy and uses it for both. A
// `PermanentFailure` is terminal by definition; a `TransientFailure` is
// terminal when `ctx.Attempt >= MaxAttempts`.

/// Audit payload for one subscription run. Handed to the caller-supplied
/// callback so audit-sink wiring stays caller-side, exactly as
/// `ReportApiHandler`'s `AuditOnRender` does.
type SubscriptionRunAudit = {
    SubscriptionId: SubscriptionId
    ScopeId: string
    ProducerKey: ReportProducerKey
    Format: TemplateFormat
    /// `None` on a failed run.
    OutputSize: int option
    /// Recipients an address was resolved for and the sink accepted.
    DeliveredTo: int
    /// Recipients the run did not reach — no registered address at this
    /// scope, or a sink-level skip. Not an error (the address-book
    /// contract calls it a skip), but an operator wondering why three of
    /// five people never see the report needs it surfaced.
    SkippedRecipients: int
    Attempt: int
    /// `None` on success; the reason on failure.
    Failure: string option
    /// True when no further attempt will be made for this run.
    Terminal: bool
}

/// Side-effect callback invoked after every subscription run, succeeded
/// or failed.
type AuditOnSubscriptionRun = SubscriptionRunAudit -> Async<unit>

/// Everything the subscription job needs, gathered so the compose
/// surface passes one value rather than a dozen positional arguments.
type ReportSubscriptionJobDeps = {
    Subscriptions: IReportSubscriptionStore
    Producers: ReportProducerRegistry
    Templates: IReportTemplateStore
    Renderers: RendererRegistry
    Artefacts: IDataObjectStore
    AddressBook: INotificationAddressBook
    Sink: INotificationSink
    Audit: AuditOnSubscriptionRun
    Config: ReportApiConfig
    /// The retry policy the job is registered with. Read only to decide
    /// whether a transient failure was the last attempt — see the
    /// terminal-failure note above.
    RetryPolicy: JobRetryPolicy
    /// Applied to a `NarrativeValue` before rendering, exactly as the
    /// interactive path applies it. `None` when the deployment composes
    /// no fact tier (GP 13).
    Disclosure: (IFactDisclosureGate * string) option
}

module ReportSubscriptionJobHandler =

    /// The job payload: a subscription id and nothing else. Every other
    /// fact about the run is read from the subscription at dispatch
    /// time, so editing a subscription changes its next run rather than
    /// leaving a stale copy baked into a persisted job payload.
    type SubscriptionJobPayload = { SubscriptionId: SubscriptionId }

    /// A run that got as far as producing bytes.
    type private RunSuccess = {
        Bytes: int
        ObjectKey: string
        Version: int
        Delivered: int
        Skipped: int
    }

    /// A run that did not. `Terminal` is the handler's own judgement —
    /// "waiting will not help" — before the attempt count is consulted.
    type private RunFailure = { Terminal: bool; Reason: string }

    let private jsonOptions = FableConverters.create ()

    /// Serialise the payload for `JobRegistration.Payload`. Exposed so
    /// the API handler's schedule path and this module's parse path
    /// cannot disagree about the shape.
    let encodePayload (id: SubscriptionId) : string =
        JsonSerializer.Serialize({ SubscriptionId = id }, jsonOptions)

    let decodePayload (payload: string) : SubscriptionJobPayload option =
        try
            if String.IsNullOrWhiteSpace payload then
                None
            else
                let parsed =
                    JsonSerializer.Deserialize<SubscriptionJobPayload>(payload, jsonOptions)

                if String.IsNullOrWhiteSpace parsed.SubscriptionId then
                    None
                else
                    Some parsed
        with _ ->
            None

    let internal extensionFor =
        function
        | Markdown -> "md"
        | Html -> "html"
        | Pdf -> "pdf"
        | Docx -> "docx"
        | Xlsx -> "xlsx"
        | Pptx -> "pptx"

    /// Name for the run: the producer's stem when it offered one, else
    /// the subscription's display name, so an operator recognises the
    /// mail without decoding an id.
    let internal artefactName (subscription: ReportSubscription) (stem: string option) =
        stem
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue subscription.DisplayName

    /// The body of the delivered mail. Deliberately a pointer, never an
    /// attachment: the artefact is already a versioned object behind a
    /// scope-isolated read path, and attaching it would copy scope-owned
    /// content into a vendor's mail store — outside every retention and
    /// audit control the deployment has. A deployment that wants
    /// attachments composes a sink that fetches the object.
    let internal deliveryContent
        (subscription: ReportSubscription)
        (name: string)
        (objectKey: string)
        (version: int)
        : EmailContent =
        let body =
            StringBuilder()
                .AppendLine($"Your scheduled report '{subscription.DisplayName}' is ready.")
                .AppendLine()
                .AppendLine($"Format : {extensionFor subscription.Format}")
                .AppendLine($"Stored : {objectKey} (version {version})")
                .AppendLine()
                .AppendLine("Retrieve it through your deployment's report history.")
                .ToString()

        InlineEmail($"{name} — scheduled report", body, None)

    let internal failureContent (subscription: ReportSubscription) (reason: string) : EmailContent =
        let body =
            StringBuilder()
                .AppendLine($"The scheduled report '{subscription.DisplayName}' did not run.")
                .AppendLine()
                .AppendLine($"Reason: {reason}")
                .AppendLine()
                .AppendLine(
                    "No further attempts will be made for this run. The subscription remains active for its next scheduled time."
                )
                .ToString()

        InlineEmail($"{subscription.DisplayName} — scheduled report failed", body, None)

    // ─── The run, as a Result pipeline ───────────────────────────────

    /// Deliver to every recipient the address book can reach.
    ///
    /// A recipient with no registered address is *skipped*, per the
    /// address-book contract — that is a configuration fact, and
    /// dead-lettering a report because one of five people never
    /// registered an address would be the wrong trade. A subscription
    /// where NOBODY is reachable is a different thing: it rendered and
    /// reached no one, so it fails terminally, naming that condition.
    let private deliver
        (deps: ReportSubscriptionJobDeps)
        (subscription: ReportSubscription)
        (name: string)
        (objectKey: string)
        (version: int)
        : Async<Result<int * int, RunFailure>> =
        async {
            let! resolved =
                subscription.RecipientUserIds
                |> List.map (fun userId -> async {
                    let! address = deps.AddressBook.ResolveEmail(userId, subscription.ScopeId)
                    return userId, Option.isSome address
                })
                |> Async.Sequential

            let reachable = resolved |> Array.filter snd |> Array.map fst |> Array.toList
            let total = List.length subscription.RecipientUserIds
            let skipped = total - List.length reachable

            if List.isEmpty reachable then
                return
                    Error {
                        Terminal = true
                        Reason =
                            "no recipient has a resolvable address at this scope — the report rendered but could not be delivered"
                    }
            else
                let envelope =
                    NotificationEnvelope.create
                        subscription.ScopeId
                        (TransactionalEmail {
                            RecipientUserIds = reachable
                            Content = deliveryContent subscription name objectKey version
                            // Keyed on the artefact VERSION, so a
                            // scheduler retry of one run de-duplicates
                            // at the vendor while next week's run does
                            // not.
                            CorrelationId = Some $"report-subscription:{subscription.Id}:{version}"
                        })

                let! result = deps.Sink.Send(subscription.ScopeId, envelope)

                match result with
                | SinkResult.Delivered _ -> return Ok(List.length reachable, skipped)
                // The sink decided not to deliver — team preferences
                // off, vendor-side dedup. That is a correct decision,
                // not a failure: retrying reaches the same one.
                | SinkResult.Skipped _ -> return Ok(0, total)
                | SinkResult.TransientFailure error -> return Error { Terminal = false; Reason = error }
                | SinkResult.PermanentFailure error -> return Error { Terminal = true; Reason = error }
        }

    let private persist
        (deps: ReportSubscriptionJobDeps)
        (subscription: ReportSubscription)
        (template: ReportTemplate)
        (name: string)
        (bytes: byte[])
        : Async<Result<DataObject, RunFailure>> =
        async {
            let metadata =
                Map [
                    "subscriptionId", subscription.Id
                    "producerKey", subscription.ProducerKey
                    "format", extensionFor subscription.Format
                    "displayName", name
                    "mimeType", ReportApiConfig.mimeFor template.Format deps.Config
                ]

            let! saved =
                deps.Artefacts.Save(
                    subscription.ScopeId,
                    ReportSubscription.artefactObjectId subscription.Id,
                    bytes,
                    ReportSubscription.ArtefactDataType,
                    subscription.CreatedBy,
                    metadata,
                    VersioningPolicy.Versioned
                )

            return
                match saved with
                | Result.Ok artefact -> Ok artefact
                // Storage is the classic transient — the next attempt
                // may well succeed, so this one does not declare itself
                // terminal and lets the attempt count decide.
                | Result.Error e ->
                    Error {
                        Terminal = false
                        Reason = $"could not persist the run artefact: %A{e}"
                    }
        }

    let private render
        (deps: ReportSubscriptionJobDeps)
        (subscription: ReportSubscription)
        (request: ReportRenderRequest)
        : Async<Result<ReportTemplate * byte[], RunFailure>> =
        async {
            let! templateOpt = deps.Templates.Get(subscription.ScopeId, request.TemplateId)

            match templateOpt with
            | None ->
                return
                    Error {
                        Terminal = true
                        Reason = $"template '{request.TemplateId}' does not exist at this scope"
                    }
            | Some stored ->
                // The SUBSCRIPTION's format wins over the stored
                // template's: a subscription is a request for this
                // report in this format, and the format was validated
                // against the producer's declaration at create time.
                let template = {
                    stored with
                        Format = subscription.Format
                }

                match deps.Renderers.Route template.Format with
                | Result.Error routing ->
                    return
                        Error {
                            Terminal = true
                            Reason = RenderError.toMessage routing
                        }
                | Result.Ok renderer ->
                    let! values =
                        ReportApiHandler.resolveValuesFor
                            deps.Disclosure
                            subscription.ScopeId
                            renderer
                            template.Format
                            request.Values

                    let! rendered = renderer.Render(template, values)

                    return
                        match rendered with
                        | Result.Ok bytes -> Ok(template, bytes)
                        | Result.Error e ->
                            Error {
                                Terminal = true
                                Reason = RenderError.toMessage e
                            }
        }

    /// The whole run, from producer resolution to delivery. Every step
    /// short-circuits into the same `RunFailure`, so `Execute` below has
    /// exactly one success path and one failure path to account for.
    let private runOnce
        (deps: ReportSubscriptionJobDeps)
        (subscription: ReportSubscription)
        : Async<Result<RunSuccess, RunFailure>> =
        async {
            match deps.Producers.TryResolve subscription.ProducerKey with
            | None ->
                return
                    Error {
                        Terminal = true
                        Reason = SubscriptionError.toMessage (UnknownProducer subscription.ProducerKey)
                    }
            | Some producer ->
                let! resolved = producer.Resolve subscription.ScopeId subscription.Parameters

                match resolved with
                | Error reason ->
                    return
                        Error {
                            Terminal = true
                            Reason = $"report producer could not resolve: {reason}"
                        }
                | Ok request ->
                    let! rendered = render deps subscription request

                    match rendered with
                    | Error failure -> return Error failure
                    | Ok(template, bytes) ->
                        let name = artefactName subscription request.FileNameStem
                        let! stored = persist deps subscription template name bytes

                        match stored with
                        | Error failure -> return Error failure
                        | Ok artefact ->
                            let objectKey = ReportSubscription.artefactObjectId subscription.Id
                            let! delivery = deliver deps subscription name objectKey artefact.Version

                            match delivery with
                            | Error failure ->
                                return
                                    Error {
                                        failure with
                                            Reason = $"delivery failed: {failure.Reason}"
                                    }
                            | Ok(delivered, skipped) ->
                                return
                                    Ok {
                                        Bytes = bytes.Length
                                        ObjectKey = objectKey
                                        Version = artefact.Version
                                        Delivered = delivered
                                        Skipped = skipped
                                    }
        }

    /// Record the outcome on the subscription. Best-effort by design: a
    /// run that rendered and delivered has done its job, and failing to
    /// write the bookkeeping afterwards must not turn a delivered report
    /// into a retried one — the retry would deliver it twice.
    let private recordOutcome
        (deps: ReportSubscriptionJobDeps)
        (subscription: ReportSubscription)
        (outcome: SubscriptionRunOutcome)
        : Async<unit> =
        async {
            let! _ =
                deps.Subscriptions.Save(subscription.ScopeId, { subscription with LastRun = outcome })
                |> Async.Catch

            return ()
        }

    /// Build the handler. Every dependency is supplied rather than
    /// resolved from a service locator inside `Execute`, so the handler
    /// is directly exercisable against in-memory sinks and stores —
    /// which is what the acceptance criteria ask for, and what makes the
    /// retry and scope-isolation behaviours testable at all.
    let create (deps: ReportSubscriptionJobDeps) : IJobHandler =
        { new IJobHandler with
            member _.Execute ctx = async {
                match decodePayload ctx.Payload with
                // A malformed payload will not parse on the next attempt
                // either.
                | None -> return PermanentFailure "subscription job payload did not carry a subscription id"
                | Some payload ->
                    let! existing = deps.Subscriptions.Get(ctx.ScopeId, payload.SubscriptionId)

                    match existing with
                    // Deleted between the scheduler's tick and this
                    // dispatch. The job has nothing to do and never
                    // will; `Success` avoids dead-lettering a job whose
                    // only fault is that its owner cancelled it. Note
                    // the lookup is at `ctx.ScopeId`, so a job carrying
                    // another scope's id lands here too (GP 4).
                    | None -> return Success
                    // Paused. Same reasoning: not an error, nothing to do.
                    | Some subscription when not subscription.Enabled -> return Success
                    | Some subscription ->
                        let! outcome = runOnce deps subscription

                        match outcome with
                        | Ok success ->
                            do!
                                recordOutcome
                                    deps
                                    subscription
                                    (RunSucceeded(
                                        DateTimeOffset.UtcNow,
                                        success.ObjectKey,
                                        success.Version,
                                        success.Delivered
                                    ))

                            do!
                                deps.Audit {
                                    SubscriptionId = subscription.Id
                                    ScopeId = subscription.ScopeId
                                    ProducerKey = subscription.ProducerKey
                                    Format = subscription.Format
                                    OutputSize = Some success.Bytes
                                    DeliveredTo = success.Delivered
                                    SkippedRecipients = success.Skipped
                                    Attempt = ctx.Attempt
                                    Failure = None
                                    Terminal = false
                                }

                            return Success

                        | Error failure ->
                            // Terminal either because the handler judged
                            // it unrecoverable, or because the retry
                            // budget the compose registered is spent.
                            let terminal = failure.Terminal || ctx.Attempt >= deps.RetryPolicy.MaxAttempts

                            do!
                                recordOutcome
                                    deps
                                    subscription
                                    (RunFailed(DateTimeOffset.UtcNow, failure.Reason, terminal))

                            do!
                                deps.Audit {
                                    SubscriptionId = subscription.Id
                                    ScopeId = subscription.ScopeId
                                    ProducerKey = subscription.ProducerKey
                                    Format = subscription.Format
                                    OutputSize = None
                                    DeliveredTo = 0
                                    SkippedRecipients = List.length subscription.RecipientUserIds
                                    Attempt = ctx.Attempt
                                    Failure = Some failure.Reason
                                    Terminal = terminal
                                }

                            // A terminal failure is the operator's
                            // problem now, so it is announced rather
                            // than left in a log: the people who would
                            // have received the report are told it did
                            // not arrive. Best-effort — a warning that
                            // itself fails must not re-run the report.
                            if terminal && not (List.isEmpty subscription.RecipientUserIds) then
                                let warning =
                                    NotificationEnvelope.create
                                        subscription.ScopeId
                                        (TransactionalEmail {
                                            RecipientUserIds = subscription.RecipientUserIds
                                            Content = failureContent subscription failure.Reason
                                            CorrelationId =
                                                Some $"report-subscription-failed:{subscription.Id}:{ctx.JobId}"
                                        })

                                let! _ = deps.Sink.Send(subscription.ScopeId, warning) |> Async.Catch
                                ()

                            return
                                if terminal then
                                    PermanentFailure failure.Reason
                                else
                                    TransientFailure failure.Reason
            }
        }