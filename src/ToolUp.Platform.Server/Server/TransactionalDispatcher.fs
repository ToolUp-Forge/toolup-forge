module ToolUp.Platform.TransactionalDispatcher

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.Tracing

// ─── Phase 6l.C — recipient hashing for audit emission ──────────────

/// `SHA256(userId)[..8]` — 8 hex chars (4 bytes) of entropy is enough
/// to correlate the same recipient across multiple drop events while
/// keeping the audit trail PII-free. Pure function — no salt; the
/// goal is correlation, not unguessability (a determined adversary
/// who already knows a userId can verify whether they were dropped,
/// which is fine).
let private hashRecipient (userId: string) : string =
    if String.IsNullOrEmpty userId then
        ""
    else
        use sha = SHA256.Create()
        let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes userId)
        // First 4 bytes → 8 hex chars
        bytes |> Seq.take 4 |> Seq.map (sprintf "%02x") |> String.concat ""

// ─── Public surface ──────────────────────────────────────────────
//
// Phase 6f routes transactional envelopes (`TransactionalEmail`,
// `TransactionalSms`, `MobilePush`) out-of-band so they never touch
// the SSE wire and never expose PII through a cross-process
// notification channel. Two pieces:
//
//   * `DispatchingNotificationChannel` — `INotificationChannel`
//     decorator. Forwards every non-transactional publish to the
//     inner channel (SSE / Redis); intercepts transactional
//     publishes and enqueues them for `TransactionalDispatcher`. By
//     short-circuiting before the inner channel, the transactional
//     envelope never crosses the wire — no Redis pub/sub leak, no
//     SSE leak, single envelope identity for audit.
//
//   * `TransactionalDispatcher` — `BackgroundService`. Drains a
//     bounded `Channel<DispatchTask>`, looks up the matching sink
//     by `Kind`, runs the retry loop, emits a terminal-status log
//     line. Audit emission is stubbed in step (a) and lit up in
//     step (b) when the new audit cases land.
//
// **Single-instance limitation.** The bounded channel and retry
// timer are in-process. A multi-silo deployment running with a
// Redis `INotificationChannel` will have every silo see its own
// publishes; only the publishing silo dispatches transactional
// envelopes (because we bypass Redis for those kinds). At-most-once
// best-effort delivery; a publishing silo that crashes between
// enqueue and dispatch loses the envelope. Documented contract;
// distributed retry / leasing is Phase 9c follow-up.

[<Literal>]
let private QueueCapacity = 256

[<Literal>]
let private LogPrefix = "[TransactionalDispatcher]"

/// Source-module label used for the future `NotificationSent` /
/// `NotificationDeliveryFailed` audit events (Phase 6f step b). Lifted
/// here so step (b) finds the constant in place when audit emission
/// is wired through `IAuditLog.Record`.
[<Literal>]
let NotificationsSourceModule = "_platform.notifications"

/// One unit of work on the dispatcher's queue. Carries the same
/// envelope the channel decorator stamped — single envelope identity
/// across the SSE-equivalent path and the audit trail.
type private DispatchTask = {
    Envelope: NotificationEnvelope
    SinkKind: string
}

/// Pre-flight team-prefs check. Reads the effective
/// `_platform.notification_prefs` config for the publishing scope and
/// returns the boolean kill-switch for this kind. Fails closed on
/// missing document or unknown key — both surface as `false`, which
/// short-circuits to `Skipped "team_opted_out"` with no audit.
///
/// Reconstructs a minimal `StorageScope` from `scopeId` because the
/// existing `IConfigStore.GetRaw` surface is scope-typed, not
/// scope-id-typed. This avoids threading a full
/// `IStorageScopeResolver` through the dispatcher just to ask "is
/// this kind enabled".
let private isKindEnabledForScope
    (configStore: IConfigStore)
    (logger: ILogger)
    (scopeId: string)
    (kind: NotificationKind.SinkKind)
    : Async<bool> =
    async {
        let scope: StorageScope = {
            ScopeId = scopeId
            Container = scopeId
            Persist = true
        }

        try
            let! values = configStore.GetRaw(scope, ConfigKeys.NotificationPrefsModuleKey)

            let key =
                match kind with
                | NotificationKind.SinkKind.Email -> ConfigKeys.NotificationPrefsKeys.EmailEnabled
                | NotificationKind.SinkKind.Sms -> ConfigKeys.NotificationPrefsKeys.SmsEnabled
                | NotificationKind.SinkKind.Push _ -> ConfigKeys.NotificationPrefsKeys.PushEnabled

            match Map.tryFind key values with
            | Some "true" -> return true
            | Some _ -> return false
            | None -> return false
        with ex ->
            logger.Warn
                $"%s{LogPrefix} prefs read failed scope=%s{scopeId} kind=%s{NotificationKind.SinkKind.toWireString kind}: %s{ex.GetType().Name}: %s{ex.Message}"

            return false
    }

/// Map a transactional `Notification` to its `INotificationSink.Kind`
/// discriminator. Returns `None` for non-transactional kinds — the
/// decorator filters before enqueue, so this is the dispatcher's
/// internal lookup helper. `MobilePush` envelopes carry no platform
/// discriminator on the wire today, so they are routed to the SDK's
/// default push variant (`PushVariant.WebPush`) — the only push
/// companion shipped at 11.C.5. When a future phase enriches the
/// `MobilePush` payload to carry an explicit variant, this lookup
/// reads it directly.
let private sinkKindOf (n: Notification) : NotificationKind.SinkKind option =
    match n with
    | TransactionalEmail _ -> Some NotificationKind.SinkKind.Email
    | TransactionalSms _ -> Some NotificationKind.SinkKind.Sms
    | MobilePush _ -> Some(NotificationKind.SinkKind.Push NotificationKind.PushVariant.WebPush)
    | _ -> None

/// Extract recipient userIds + correlation id from a transactional
/// envelope for audit emission. Returns the empty list / `None` for
/// non-transactional kinds — those don't reach the dispatcher, so this
/// is a defensive default rather than a meaningful path.
let private auditFieldsOf (n: Notification) : string list * string option =
    match n with
    | TransactionalEmail e -> e.RecipientUserIds, e.CorrelationId
    | TransactionalSms s -> s.RecipientUserIds, s.CorrelationId
    | MobilePush p -> p.RecipientUserIds, p.CorrelationId
    | _ -> [], None

/// Reserved actor id used in audit emission when the publishing
/// caller's identity is not available to the dispatcher. Phase 6f
/// flows transactional publishes through `INotificationChannel.Publish`
/// without a request-scoped actor; system-driven sources (job
/// lifecycle, scheduled cleanups) account for the bulk of traffic.
/// A future overload could carry an actor id on the envelope.
[<Literal>]
let private SystemActorId = "system"

/// Run one full retry loop for a single envelope against a single
/// sink. Phase 6f step (b) — emits `NotificationSent` on `Delivered`
/// and `NotificationDeliveryFailed` on retry-exhausted `TransientFailure`
/// or first-attempt `PermanentFailure`. `Skipped` is silent
/// (configuration-driven no-op, no audit). Mirrors
/// `WebhookDispatcher.runDelivery` in retry shape.
///
/// `IAuditLog` writes are best-effort — `EventStoreAuditLog.Record`
/// catches and logs blob-store failures so a write failure cannot
/// surface here. Caller therefore needs no extra try/catch.
let private runDelivery
    (sink: INotificationSink)
    (configStore: IConfigStore)
    (auditLog: IAuditLog)
    (retry: TransactionalRetryPolicy)
    (logger: ILogger)
    (activitySink: IActivitySink)
    (envelope: NotificationEnvelope)
    : Async<unit> =
    async {
        // Phase 9l — child activity per envelope. Re-parents the trace
        // under the W3C `traceparent` stamped on the envelope by the
        // publisher (whichever request / job / webhook handler called
        // `INotificationChannel.Publish`); when the field is `None`
        // the activity inherits from `Activity.Current` instead, which
        // typically catches in-process publishes that share the
        // publisher's async-local context.
        let parentContext =
            envelope.TraceContext |> Option.bind ActivityContextHelpers.tryParseTraceparent

        // Wire-format string for `sink.Kind` — used for the audit
        // payload's `NotificationKind` field, log-line tagging, and
        // activity name. Computing it once keeps the `runDelivery`
        // body free of repeated `SinkKind.toWireString` calls.
        let sinkKindWire = NotificationKind.SinkKind.toWireString sink.Kind

        let deliveryActivityOpt =
            activitySink.StartActivity(sprintf "notify %s" sinkKindWire, parentContext)

        try
            // Pref kill-switch: a `false` toggle short-circuits before
            // any vendor call. No audit, no retry — the team has not
            // authorised outbound delivery for this kind.
            let! prefsAllow = isKindEnabledForScope configStore logger envelope.ScopeId sink.Kind

            if not prefsAllow then
                logger.Debug
                    $"%s{LogPrefix} skipped scope=%s{envelope.ScopeId} kind=%s{sinkKindWire} reason=team_opted_out envelopeId=%O{envelope.Id}"

                // Phase 6l.C — emit a `NotificationSilentlySkipped` audit
                // event so an operator who disabled notifications via the
                // team UI but a publish happened anyway can correlate
                // "thought email was on" against "actually dropped". PII-
                // free: recipient userIds are SHA256-truncated.
                let recipientUserIds, correlationId = auditFieldsOf envelope.Notification

                let payload: NotificationSilentlySkippedPayload = {
                    NotificationKind = sinkKindWire
                    ScopeId = envelope.ScopeId
                    Reason = "team_opted_out"
                    RecipientHashes = recipientUserIds |> List.map hashRecipient
                    CorrelationId = correlationId
                }

                do! auditLog.Record(envelope.ScopeId, NotificationSilentlySkipped payload)
            else
                let recipientUserIds, correlationId = auditFieldsOf envelope.Notification
                let mutable attempt = 1
                let mutable terminate = false

                while attempt <= retry.MaxAttempts && not terminate do
                    let delay = TransactionalRetryPolicy.delayFor retry attempt

                    if delay > TimeSpan.Zero then
                        do! Async.Sleep delay

                    let! result = async {
                        try
                            return! sink.Send(envelope.ScopeId, envelope)
                        with ex ->
                            return SinkResult.TransientFailure(ex.GetType().Name + ": " + ex.Message)
                    }

                    match result with
                    | SinkResult.Delivered vendorMessageId ->
                        logger.Info
                            $"%s{LogPrefix} delivered scope=%s{envelope.ScopeId} kind=%s{sinkKindWire} provider=%s{sink.Provider} attempt=%d{attempt}"

                        do!
                            auditLog.Record(
                                envelope.ScopeId,
                                NotificationSent {
                                    UserId = SystemActorId
                                    ScopeId = envelope.ScopeId
                                    NotificationKind = sinkKindWire
                                    Provider = sink.Provider
                                    RecipientUserIds = recipientUserIds
                                    VendorMessageId = vendorMessageId
                                    CorrelationId = correlationId
                                }
                            )

                        terminate <- true
                    | SinkResult.Skipped reason ->
                        // No audit, no retry — the sink decided this
                        // recipient/envelope is a no-op (no resolvable
                        // address, vendor dedup hit, etc.).
                        logger.Debug
                            $"%s{LogPrefix} sink_skipped scope=%s{envelope.ScopeId} kind=%s{sinkKindWire} reason=%s{reason}"

                        terminate <- true
                    | SinkResult.TransientFailure error ->
                        logger.Warn
                            $"%s{LogPrefix} transient_failure scope=%s{envelope.ScopeId} kind=%s{sinkKindWire} provider=%s{sink.Provider} attempt=%d{attempt} error=%s{error}"

                        attempt <- attempt + 1

                        if attempt > retry.MaxAttempts then
                            // Retry budget exhausted — emit the failure
                            // audit with the total attempt count so an
                            // operator can read "we tried N times" off
                            // the trail.
                            let attempts = attempt - 1

                            logger.Error(
                                $"%s{LogPrefix} delivery_failed scope=%s{envelope.ScopeId} kind=%s{sinkKindWire} provider=%s{sink.Provider} attempts=%d{attempts} error=%s{error}",
                                None
                            )

                            do!
                                auditLog.Record(
                                    envelope.ScopeId,
                                    NotificationDeliveryFailed {
                                        UserId = SystemActorId
                                        ScopeId = envelope.ScopeId
                                        NotificationKind = sinkKindWire
                                        Provider = sink.Provider
                                        RecipientUserIds = recipientUserIds
                                        Error = error
                                        Attempts = attempts
                                        CorrelationId = correlationId
                                    }
                                )
                    | SinkResult.PermanentFailure error ->
                        logger.Error(
                            $"%s{LogPrefix} delivery_failed scope=%s{envelope.ScopeId} kind=%s{sinkKindWire} provider=%s{sink.Provider} attempts=%d{attempt} error=%s{error}",
                            None
                        )

                        do!
                            auditLog.Record(
                                envelope.ScopeId,
                                NotificationDeliveryFailed {
                                    UserId = SystemActorId
                                    ScopeId = envelope.ScopeId
                                    NotificationKind = sinkKindWire
                                    Provider = sink.Provider
                                    RecipientUserIds = recipientUserIds
                                    Error = error
                                    Attempts = attempt
                                    CorrelationId = correlationId
                                }
                            )

                        terminate <- true
        finally
            deliveryActivityOpt |> Option.iter _.Dispose()
    }

/// Validate sink registrations at construction time. Two sinks of the
/// same `Kind` is a configuration error — the dispatcher would route
/// to the first match arbitrarily and silently drop the second. Throw
/// loudly so the deployment fails to start rather than running with
/// half its expected delivery.
let internal validateSinkRegistration (sinks: INotificationSink list) : unit =
    let duplicates =
        sinks
        |> List.groupBy (fun s -> NotificationKind.SinkKind.toWireString s.Kind)
        |> List.filter (fun (_, group) -> List.length group > 1)
        |> List.map fst

    if not (List.isEmpty duplicates) then
        let kinds = String.concat ", " duplicates

        failwithf
            "Multiple INotificationSink implementations registered for kind(s): %s. Phase 6f permits one sink per kind."
            kinds

/// `BackgroundService` instance hosted by ASP.NET Core's
/// `IHostedService` machinery. One per process; drains the bounded
/// queue on a worker loop, cancels cleanly on `StopAsync`. Enqueue is
/// driven externally by `DispatchingNotificationChannel.Publish`.
type TransactionalDispatcher
    (
        sinks: INotificationSink list,
        configStore: IConfigStore,
        auditLog: IAuditLog,
        logger: ILogger,
        retry: TransactionalRetryPolicy,
        activitySink: IActivitySink
    ) =
    inherit BackgroundService()

    do validateSinkRegistration sinks

    /// Bounded queue. Drop-write on overflow keeps `Publish` non-
    /// blocking even when the dispatcher is saturated; the audit
    /// trail will show a gap (no `NotificationSent` for the dropped
    /// envelope) and the operator's logs carry a `Warn`.
    let queue =
        Channel.CreateBounded<DispatchTask>(
            BoundedChannelOptions(QueueCapacity, FullMode = BoundedChannelFullMode.DropWrite)
        )

    /// Sinks indexed by the wire-format string of their `Kind` for
    /// O(1) dispatch lookup. Kept on the instance so the worker loop
    /// reads it without a closure capture of the constructor list.
    /// Keyed by `SinkKind.toWireString` so two push variants
    /// (`Push.WebPush` + `Push.Fcm`) hold distinct slots — the
    /// uniqueness validator uses the same key.
    let sinksByKind: Map<string, INotificationSink> =
        sinks
        |> List.map (fun s -> NotificationKind.SinkKind.toWireString s.Kind, s)
        |> Map.ofList

    /// Enqueue a transactional envelope. Called by the wrapping
    /// channel decorator at `Publish` time. Drop-write failure is
    /// logged at `Warn` but not surfaced to `Publish` — back-pressuring
    /// the channel is the wrong place to apply queue saturation.
    /// Returns `true` if accepted, `false` if dropped — the decorator
    /// uses the return value for telemetry but does not propagate it.
    member _.TryEnqueue(envelope: NotificationEnvelope) : bool =
        match sinkKindOf envelope.Notification with
        | None -> false // not transactional — decorator should have filtered
        | Some sinkKind ->
            let sinkKindWire = NotificationKind.SinkKind.toWireString sinkKind

            if not (Map.containsKey sinkKindWire sinksByKind) then
                logger.Debug
                    $"%s{LogPrefix} no_sink_registered kind=%s{sinkKindWire} scope=%s{envelope.ScopeId} envelopeId=%O{envelope.Id} — envelope ignored"

                false
            else
                let task = {
                    Envelope = envelope
                    SinkKind = sinkKindWire
                }

                if queue.Writer.TryWrite task then
                    true
                else
                    logger.Warn
                        $"%s{LogPrefix} queue_full kind=%s{sinkKindWire} scope=%s{envelope.ScopeId} envelopeId=%O{envelope.Id} — envelope dropped"

                    false

    /// True when `kind` has a registered sink. Used by the channel
    /// decorator to short-circuit before constructing an envelope —
    /// publishing a transactional kind with no registered sink in a
    /// deployment is pure waste otherwise.
    member _.HasSinkForKind(kind: string) : bool = Map.containsKey kind sinksByKind

    /// Async loop draining the queue. Runs until the
    /// `BackgroundService` cancellation token fires on shutdown.
    /// Each task fans out via `Async.Start` so a slow vendor call
    /// doesn't block sibling envelopes; `runDelivery` carries its
    /// own try/catch.
    member private _.DrainQueue(cancellationToken: CancellationToken) = async {
        try
            while not cancellationToken.IsCancellationRequested do
                let! hasNext = queue.Reader.WaitToReadAsync(cancellationToken).AsTask() |> Async.AwaitTask

                if hasNext then
                    let mutable task = Unchecked.defaultof<DispatchTask>

                    while queue.Reader.TryRead(&task) do
                        match Map.tryFind task.SinkKind sinksByKind with
                        | Some sink ->
                            Async.Start(
                                runDelivery sink configStore auditLog retry logger activitySink task.Envelope,
                                cancellationToken
                            )
                        | None ->
                            logger.Warn
                                $"%s{LogPrefix} sink_disappeared kind=%s{task.SinkKind} scope=%s{task.Envelope.ScopeId} envelopeId=%O{task.Envelope.Id} — envelope dropped"

                            // Gap audit #7 — emit a NotificationDeliveryFailed
                            // audit row so an operator querying the audit trail
                            // for "why didn't we send X" sees the dropped envelope
                            // (rather than only the buried Warn line). Recipient
                            // user-ids and correlation id come from the same
                            // helper used by every other audit emission below.
                            let recipientUserIds, correlationId = auditFieldsOf task.Envelope.Notification

                            Async.Start(
                                auditLog.Record(
                                    task.Envelope.ScopeId,
                                    NotificationDeliveryFailed {
                                        UserId = SystemActorId
                                        ScopeId = task.Envelope.ScopeId
                                        NotificationKind = task.SinkKind
                                        Provider = "<unregistered>"
                                        RecipientUserIds = recipientUserIds
                                        Error = "sink_unregistered"
                                        Attempts = 0
                                        CorrelationId = correlationId
                                    }
                                ),
                                cancellationToken
                            )
        with
        | :? OperationCanceledException -> ()
        | ex -> logger.Error($"%s{LogPrefix} drain_loop_failed: %s{ex.GetType().Name}: %s{ex.Message}", Some ex)
    }

    override this.ExecuteAsync(stoppingToken: CancellationToken) =
        // BackgroundService bridges Task-async; we drive our async
        // body explicitly so cancellation flows.
        Async.StartAsTask(this.DrainQueue stoppingToken, cancellationToken = stoppingToken) :> Task

    override this.StopAsync(cancellationToken: CancellationToken) =
        // Complete the writer so the drain loop exits its outer
        // `WaitToReadAsync` after the next pump, then defer to the
        // base implementation. F#'s `task { }` builder can't carry a
        // `base.X` call across the closure; doing the side-effect
        // directly and chaining the base task keeps the `base`
        // reference in the override's direct scope.
        queue.Writer.TryComplete() |> ignore
        base.StopAsync(cancellationToken)

/// `INotificationChannel` decorator that intercepts transactional
/// kinds at `Publish` time and routes them to a
/// `TransactionalDispatcher` queue, bypassing the inner channel
/// entirely. Non-transactional kinds pass through to the inner
/// channel unchanged.
///
/// **Why bypass the inner channel for transactional kinds:**
/// (1) PII safety — `EmailEnvelope` resolves recipient PII inside
///     the sink, but a Redis-backed inner channel would put the
///     envelope JSON on a pub/sub topic before any sink sees it.
///     Transactional kinds skip the wire entirely so no Redis pub/sub
///     leak is structurally possible.
/// (2) Single envelope identity — by stamping the envelope here and
///     handing the same instance to the dispatcher, the audit trail
///     and the dispatcher carry the same `Id` (no double-stamping
///     drift).
/// (3) SSE safety — even with the SSE filter in
///     `NotificationHandler.writeEnvelope`, defence-in-depth says the
///     envelope shouldn't be available to subscribe to in the first
///     place.
type DispatchingNotificationChannel(inner: INotificationChannel, dispatcher: TransactionalDispatcher) =
    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async {
            if NotificationKind.isTransactional notification then
                // Transactional kinds skip the wire channel entirely.
                // Stamp once here so the dispatcher and any future
                // audit emission share envelope identity. Phase 9l —
                // also stamp the W3C traceparent so the dispatcher's
                // `runDelivery` re-parents under the originating
                // request's span.
                let envelope =
                    NotificationEnvelope.createWithTraceContext
                        scopeId
                        notification
                        (ActivityContextHelpers.currentTraceparent ())

                dispatcher.TryEnqueue envelope |> ignore
            else
                do! inner.Publish(scopeId, notification)
        }

        member _.Subscribe(scopeId, handler) = inner.Subscribe(scopeId, handler)

        member _.Unsubscribe(subscriptionId) = inner.Unsubscribe(subscriptionId)