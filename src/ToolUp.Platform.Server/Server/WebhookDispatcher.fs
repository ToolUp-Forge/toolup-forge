module ToolUp.Platform.WebhookDispatcher

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Channels
open Microsoft.Extensions.Hosting
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Tracing

// ─── Public surface ──────────────────────────────────────────────

/// Server-internal dispatcher. The post-write hook installed in
/// `compose` calls `Dispatch` for every event written to
/// `IEventStore`; the admin UI's `IWebhookApi.TestFire` calls
/// `TestFire`. Not exposed through any Fable.Remoting surface.
type IWebhookDispatcher =
    /// Enqueue an event for fan-out to every active subscription
    /// in the event's scope whose `EventTypes` filter accepts the
    /// type. Returns immediately — actual delivery runs on the
    /// background worker. The queue is bounded; if it's full the
    /// event is logged and dropped (back-pressure: better to skip
    /// than to OOM, audit will show the gap).
    abstract Dispatch: event: ModuleEvent -> unit

    /// Synthesise a `WebhookTest` event and run a single delivery
    /// attempt to the named subscription. Returns the first-attempt
    /// outcome — no retries on test-fires. Used by the admin UI's
    /// "Test fire" button so an admin can verify wiring without
    /// triggering a real event.
    abstract TestFire: scopeId: string * subscriptionId: Guid -> Async<Result<WebhookTestResult, string>>

// ─── Internal types ──────────────────────────────────────────────

/// One unit of work on the dispatcher's queue. The event is matched
/// against every active subscription for its scope at dequeue time
/// (not enqueue time) so subscription edits made between enqueue and
/// dispatch take effect on the same event.
type private DispatchTask = { Event: ModuleEvent }

[<Literal>]
let private QueueCapacity = 1024

[<Literal>]
let private SignatureHeader = "X-ToolUp-Signature"

[<Literal>]
let private SourceModule = "_platform.webhooks"

[<Literal>]
let private TestEventType = "WebhookTest"

let private deliveryTimeout = TimeSpan.FromSeconds 30.0

// ─── HMAC ────────────────────────────────────────────────────────

/// Webhook signature emission + verification. The header value uses
/// Stripe's convention of `sha256=<hex>` so admins recognise the
/// format. During a Phase 235 secret-rotation grace window the
/// subscription has both a current and a previous secret, so the header
/// carries *both* signatures comma-separated (`sha256=<new>,sha256=<old>`);
/// a receiver still configured with the old secret finds a matching
/// signature and keeps verifying, while one updated to the new secret
/// matches the other. Once the grace window closes only the current
/// signature is emitted and the old secret stops verifying.
module WebhookSignature =
    /// HMAC-SHA256 over the raw POST body using `secret` as the key,
    /// rendered as `sha256=<lowerhex>`.
    let private signOne (secret: string) (body: byte[]) : string =
        use hmac = new HMACSHA256(Encoding.UTF8.GetBytes secret)
        let hash = hmac.ComputeHash body
        let hex = Convert.ToHexString(hash).ToLowerInvariant()
        $"sha256={hex}"

    /// Build the `X-ToolUp-Signature` header value: one `sha256=<hex>`
    /// per accepted secret, comma-joined. Order is current-first.
    let headerFor (secrets: string list) (body: byte[]) : string =
        secrets |> List.map (fun s -> signOne s body) |> String.concat ","

    /// The header a delivery at instant `now` should carry — signed with
    /// every secret the subscription's grace window currently accepts.
    let header (now: DateTime) (sub: WebhookSubscription) (body: byte[]) : string =
        headerFor (WebhookSubscription.acceptedSecrets now sub) body

    /// Receiver-side verification: does `headerValue` (the comma-joined
    /// signature header the dispatcher emitted) contain a signature
    /// matching HMAC-SHA256(`secret`, `body`)? Constant-time compare per
    /// candidate. External receivers reimplement this in their own
    /// stack; the SDK exposes it for same-deployment receivers + tests.
    let verifies (secret: string) (body: byte[]) (headerValue: string) : bool =
        let expected = signOne secret body |> Encoding.UTF8.GetBytes

        headerValue.Split ','
        |> Array.exists (fun candidate ->
            let candidateBytes = candidate.Trim() |> Encoding.UTF8.GetBytes

            candidateBytes.Length = expected.Length
            && CryptographicOperations.FixedTimeEquals(
                ReadOnlySpan<byte>(candidateBytes),
                ReadOnlySpan<byte>(expected)
            ))

// ─── Rate-limit key ──────────────────────────────────────────────

// Phase 9v — derive the `IRateLimiter` bucket key from the destination
// URL's host. Provider strings are namespaced `"webhook:<host>"` so
// connector descriptors (`"api.strava.com"`, `"openai"`) and webhook
// descriptors don't accidentally share a bucket when both happen to
// target the same host — webhook receivers and vendor APIs have
// unrelated quota shapes. Falls back to the raw target URL on parse
// failure; `WebhookSubscription` validates URLs at creation, so the
// fallback is defence-in-depth, not a hot path.
let private rateLimitKeyFor (sub: WebhookSubscription) : RateLimitKey =
    let host =
        match Uri.TryCreate(sub.TargetUrl, UriKind.Absolute) with
        | true, uri -> uri.Host
        | false, _ -> sub.TargetUrl

    {
        ScopeId = sub.ScopeId
        Provider = "webhook:" + host
        SubKey = None
    }

// ─── Outbound JSON ───────────────────────────────────────────────

/// Outbound payloads target third-party receivers (Slack, Zapier,
/// customer ingestion) — not the Fable admin UI — so use plain
/// bare `System.Text.Json` camelCase rather than `FableConverters`.
/// Receivers can document the wire format from the camelCase shape;
/// the persisted delivery log uses `FableConverters` separately.
let private outboundJsonOptions =
    let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    opts.Converters.Add(System.Text.Json.Serialization.JsonStringEnumConverter())
    opts

let private serialiseOutbound (payload: WebhookDeliveryPayload) : byte[] =
    let dto = {|
        deliveryId = payload.DeliveryId
        subscriptionId = payload.SubscriptionId
        deliveredAt = payload.DeliveredAt.ToUniversalTime().ToString("o")
        attempt = payload.Attempt
        event = {|
            id = payload.Event.Id
            occurredAt = payload.Event.OccurredAt.ToUniversalTime().ToString("o")
            scopeId = payload.Event.ScopeId
            sourceModule = payload.Event.SourceModule
            eventType = payload.Event.EventType
            payload = payload.Event.Payload
        |}
    |}

    System.Text.Json.JsonSerializer.Serialize(dto, outboundJsonOptions)
    |> Encoding.UTF8.GetBytes

// ─── Audit-event payloads ────────────────────────────────────────

/// Persisted audit-event payloads use `FableConverters` because the
/// admin UI deserialises them via Fable.Remoting/SimpleJson.
let private auditJsonOptions = FableConverters.create ()

let private toAuditJson (value: 'T) =
    JsonSerializer.Serialize(value, auditJsonOptions)

// ─── Single-attempt HTTP delivery ────────────────────────────────

/// Run one POST attempt. Returns a `WebhookDeliveryOutcome` —
/// `Success` for any 2xx, `Failure` for everything else (including
/// network errors and timeouts). Never throws.
let private deliverOnce
    (httpClient: HttpClient)
    (sub: WebhookSubscription)
    (payload: WebhookDeliveryPayload)
    : Async<WebhookDeliveryOutcome> =
    async {
        let body = serialiseOutbound payload
        // Phase 235 — sign with every secret the grace window accepts
        // (current + unexpired previous). Evaluated per attempt so a
        // grace window that closes mid-retry-loop drops the old secret.
        let signature = WebhookSignature.header DateTime.UtcNow sub body
        let stopwatch = System.Diagnostics.Stopwatch.StartNew()

        try
            use req = new HttpRequestMessage(HttpMethod.Post, sub.TargetUrl)
            req.Content <- new ByteArrayContent(body)
            req.Content.Headers.ContentType <- Headers.MediaTypeHeaderValue "application/json"
            req.Headers.TryAddWithoutValidation(SignatureHeader, signature) |> ignore

            req.Headers.TryAddWithoutValidation("X-ToolUp-Delivery-Id", payload.DeliveryId.ToString "N")
            |> ignore

            req.Headers.TryAddWithoutValidation("X-ToolUp-Event-Type", payload.Event.EventType)
            |> ignore

            use cts = new CancellationTokenSource(deliveryTimeout)

            let! response = httpClient.SendAsync(req, cts.Token) |> Async.AwaitTask

            stopwatch.Stop()
            let status = int response.StatusCode

            if response.IsSuccessStatusCode then
                return WebhookDeliveryOutcome.Success(status, stopwatch.ElapsedMilliseconds)
            else
                let! bodyText = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                let trimmed =
                    if bodyText.Length > 256 then
                        bodyText.Substring(0, 256) + "…"
                    else
                        bodyText

                return
                    WebhookDeliveryOutcome.Failure(
                        Some status,
                        $"HTTP {status}: {trimmed}",
                        stopwatch.ElapsedMilliseconds
                    )
        with
        | :? OperationCanceledException ->
            stopwatch.Stop()
            return WebhookDeliveryOutcome.Failure(None, "Timeout", stopwatch.ElapsedMilliseconds)
        | ex ->
            stopwatch.Stop()
            return WebhookDeliveryOutcome.Failure(None, ex.Message, stopwatch.ElapsedMilliseconds)
    }

// ─── Background dispatcher service ───────────────────────────────

/// `BackgroundService` that owns the in-process dispatch queue and the
/// retry / dead-letter / auto-disable state machine.
///
/// **Single-instance limitation (Phase 9c follow-up).** The queue is
/// in-process; running multiple silos with the same registry would
/// each consume the post-write hook and double-dispatch. Documented
/// in `TECHNICAL_GUIDE.md`; replacing with a durable queue (Akka,
/// Orleans Reminders, EventGrid) is the Phase 9c migration path.
type WebhookDispatcherService
    (
        registry: IWebhookRegistry,
        deliveryLog: IWebhookDeliveryLog,
        eventStore: IEventStore,
        httpClient: HttpClient,
        retryPolicy: WebhookRetryPolicy,
        logger: ILogger,
        activitySink: IActivitySink,
        // Phase 9v — resolved lazily because the limiter is constructed
        // downstream of the dispatcher in `compose` (transitive
        // dependency on `auditLog` / `resolvedMetricsSink`, both of
        // which depend on the post-webhook event-store decorator
        // chain). Cell-and-lookup mirrors `jobSchedulerLookup` in
        // `SDK.Server.fs`; by the time the BackgroundService's
        // `ExecuteAsync` runs, compose has populated the cell.
        getRateLimiter: unit -> IRateLimiter,
        // SSRF policy (operator allowlist from ServerConfig.WebhookUrlAllowedHosts).
        // Re-applied at delivery time to defeat DNS rebinding past the
        // registration-time guard in WebhookApiHandler.
        urlPolicy: WebhookUrlValidator.WebhookUrlPolicy
    ) =
    inherit BackgroundService()

    let queue =
        Channel.CreateBounded<DispatchTask>(
            BoundedChannelOptions(QueueCapacity, FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true)
        )

    let recordDelivery (scopeId: string) (delivery: WebhookDelivery) = async {
        let! _ = deliveryLog.Record(scopeId, delivery)
        return ()
    }

    let emitAudit (scopeId: string) (eventType: string) (payload: 'T) = async {
        try
            let evt = Events.create scopeId SourceModule eventType (toAuditJson payload)
            do! eventStore.Write evt
        with ex ->
            logger.Error(
                $"[WebhookDispatcher] event=audit_write_failed eventType={eventType} payloadType={typeof<'T>.Name} scope={scopeId}",
                Some ex
            )
    }

    /// Run the retry loop for one subscription/event pair. Records
    /// every attempt to the delivery log; on dead-letter emits a
    /// `WebhookDeliveryFailed` audit event and bumps
    /// `ConsecutiveFailures`; flips the subscription to `Disabled`
    /// (and emits `WebhookSubscriptionAutoDisabled`) once
    /// `DisableAfterConsecutiveFailures` is hit.
    let runDeliveryInner (sub: WebhookSubscription) (event: ModuleEvent) = async {
        let mutable attempt = 1
        let mutable finalOutcome = WebhookDeliveryOutcome.DeadLettered "no attempts run"

        // Phase 9l — child activity per (subscription, event) pair so
        // the trace tree shows one webhook delivery span (including
        // every retry) under whichever ancestor wrote the event. The
        // delivery span covers the full retry loop so an OTel viewer
        // shows "webhook foo: 3 attempts, 4.2s" as one node rather
        // than three independent dispatches.
        let deliveryActivityOpt =
            activitySink.StartActivity(sprintf "webhook %s" event.EventType, None)

        // Phase 9v — `Refused` from the limiter is a platform-side
        // throttling decision, not a receiver-health signal. Distinguish
        // it from delivery failures so the post-loop dead-letter path
        // can skip the ConsecutiveFailures bump and auto-disable (a
        // healthy receiver shouldn't get auto-disabled because the
        // operator's long-window quota happened to be exhausted).
        let mutable wasRateLimitRefused = false

        try
            while attempt <= retryPolicy.MaxAttempts do
                let delay = WebhookRetryPolicy.delayFor retryPolicy attempt

                if delay > TimeSpan.Zero then
                    do! Async.Sleep delay

                let payload: WebhookDeliveryPayload = {
                    DeliveryId = Guid.NewGuid()
                    SubscriptionId = sub.SubscriptionId
                    Event = event
                    DeliveredAt = DateTime.UtcNow
                    Attempt = attempt
                }

                // Phase 9v — gate per outbound POST on `IRateLimiter`.
                // `Proceed` / `DelayedBy` (held inside `Wait` until the
                // short window opens up) admit the delivery normally;
                // `Refused` (long-window quota exhausted) records a
                // failure row and exits the retry loop — retries inside
                // the same window would only re-refuse.
                let! rateLimitDecision = (getRateLimiter ()).Wait(rateLimitKeyFor sub)

                match rateLimitDecision with
                | Refused reason ->
                    let outcome =
                        WebhookDeliveryOutcome.Failure(None, sprintf "Rate-limit refused: %s" reason, 0L)

                    do!
                        recordDelivery sub.ScopeId {
                            DeliveryId = payload.DeliveryId
                            SubscriptionId = sub.SubscriptionId
                            EventId = Some event.Id
                            Attempt = attempt
                            AttemptedAt = payload.DeliveredAt
                            Outcome = outcome
                        }

                    finalOutcome <- outcome
                    wasRateLimitRefused <- true
                    attempt <- retryPolicy.MaxAttempts + 1 // exit
                | Proceed
                | DelayedBy _ ->
                    let! outcome = deliverOnce httpClient sub payload

                    do!
                        recordDelivery sub.ScopeId {
                            DeliveryId = payload.DeliveryId
                            SubscriptionId = sub.SubscriptionId
                            EventId = Some event.Id
                            Attempt = attempt
                            AttemptedAt = payload.DeliveredAt
                            Outcome = outcome
                        }

                    finalOutcome <- outcome

                    match outcome with
                    | WebhookDeliveryOutcome.Success _ ->
                        // Reset the consecutive-failure counter on success
                        // so a recovered receiver flips the subscription
                        // back out of the auto-disable trigger zone.
                        if sub.ConsecutiveFailures > 0 then
                            let! _ = registry.SetConsecutiveFailures(sub.ScopeId, sub.SubscriptionId, 0)

                            ()

                        attempt <- retryPolicy.MaxAttempts + 1 // exit
                    | _ -> attempt <- attempt + 1

            match finalOutcome with
            | WebhookDeliveryOutcome.Success _ -> return ()
            | _ ->
                // Dead-letter: record one more row marking the terminal
                // state and emit the audit event. Then update the
                // consecutive-failure counter and possibly auto-disable.
                let finalError =
                    match finalOutcome with
                    | WebhookDeliveryOutcome.Failure(_, msg, _) -> msg
                    | _ -> "unknown"

                do!
                    recordDelivery sub.ScopeId {
                        DeliveryId = Guid.NewGuid()
                        SubscriptionId = sub.SubscriptionId
                        EventId = Some event.Id
                        Attempt = retryPolicy.MaxAttempts
                        AttemptedAt = DateTime.UtcNow
                        Outcome = WebhookDeliveryOutcome.DeadLettered finalError
                    }

                do!
                    emitAudit sub.ScopeId WebhookEventTypes.DeliveryFailed {|
                        SubscriptionId = sub.SubscriptionId
                        EventId = event.Id
                        EventType = event.EventType
                        FinalError = finalError
                    |}

                // Phase 9v — rate-limit refusals don't count toward
                // auto-disable. The substrate already emitted its own
                // `RateLimitRefused` audit row from inside `Wait`, so
                // the operator has visibility into the throttle event
                // without the subscription being penalised.
                if not wasRateLimitRefused then
                    let newCount = sub.ConsecutiveFailures + 1

                    let! _ = registry.SetConsecutiveFailures(sub.ScopeId, sub.SubscriptionId, newCount)

                    if newCount >= retryPolicy.DisableAfterConsecutiveFailures then
                        let! _ = registry.UpdateStatus(sub.ScopeId, sub.SubscriptionId, WebhookStatus.Disabled)

                        do!
                            emitAudit sub.ScopeId WebhookEventTypes.SubscriptionAutoDisabled {|
                                SubscriptionId = sub.SubscriptionId
                                ConsecutiveFailures = newCount
                            |}
        finally
            deliveryActivityOpt |> Option.iter _.Dispose()
    }

    /// Gap audit pass-2 #1 (delivery-time) — SSRF re-validation wrapper around
    /// `runDeliveryInner`. The registration-time guard (WebhookApiHandler) can
    /// be defeated by DNS rebinding: a host that resolved to a public IP at
    /// registration is repointed at 127.0.0.1 / 169.254.169.254 (cloud IMDS)
    /// before delivery. Re-resolve + re-classify here so the dispatcher can't be
    /// weaponised as an SSRF vehicle. A refusal dead-letters immediately
    /// (re-resolution would only re-fail) and emits the standard DeliveryFailed
    /// audit; it does NOT bump ConsecutiveFailures — a platform-side block is
    /// not a receiver-health signal (mirrors the rate-limit-refused treatment).
    let runDelivery (sub: WebhookSubscription) (event: ModuleEvent) = async {
        match WebhookUrlValidator.validate urlPolicy sub.TargetUrl with
        | Ok() -> do! runDeliveryInner sub event
        | Error reason ->
            let blocked =
                sprintf "SSRF policy refused target at delivery (DNS rebinding guard): %s" reason

            do!
                recordDelivery sub.ScopeId {
                    DeliveryId = Guid.NewGuid()
                    SubscriptionId = sub.SubscriptionId
                    EventId = Some event.Id
                    Attempt = 1
                    AttemptedAt = DateTime.UtcNow
                    Outcome = WebhookDeliveryOutcome.DeadLettered blocked
                }

            do!
                emitAudit sub.ScopeId WebhookEventTypes.DeliveryFailed {|
                    SubscriptionId = sub.SubscriptionId
                    EventId = event.Id
                    EventType = event.EventType
                    FinalError = blocked
                |}

            logger.Warn
                $"[WebhookDispatcher] event=ssrf_blocked_at_delivery subscriptionId={sub.SubscriptionId} scope={sub.ScopeId}: {blocked}"
    }

    /// Match an event against every active subscription in its scope.
    /// Per-scope listing is the cheap path — we don't scan
    /// `ListAllActive` here because the event's scope is already
    /// known.
    let dispatchToScope (event: ModuleEvent) = async {
        let! subs = registry.ListSubscriptions event.ScopeId

        let matches =
            subs
            |> List.filter (fun s -> s.Status = WebhookStatus.Active)
            |> List.filter (WebhookSubscription.acceptsEventType event.EventType)

        // Run each subscription's delivery loop concurrently —
        // independent endpoints shouldn't queue behind each other.
        do!
            matches
            |> List.map (fun s -> runDelivery s event)
            |> Async.Parallel
            |> Async.Ignore
    }

    interface IWebhookDispatcher with
        member _.Dispatch(event) =
            if not (queue.Writer.TryWrite { Event = event }) then
                logger.Warn
                    $"[WebhookDispatcher] event=queue_full capacity={QueueCapacity} eventId={event.Id} eventType={event.EventType} sourceModule={event.SourceModule} scope={event.ScopeId}: dropping event"

                // Gap audit #7 — emit a structured audit row so the
                // gap is visible in the audit trail when operators
                // query by subscription. Without this row, "why did
                // subscription X miss event Y?" investigations have
                // no signal that the queue was the cause.
                let droppedPayload = {|
                    EventId = event.Id
                    EventType = event.EventType
                    SourceModule = event.SourceModule
                    ScopeId = event.ScopeId
                    QueueCapacity = QueueCapacity
                    Reason = "queue_full"
                |}

                Async.Start(emitAudit event.ScopeId "WebhookEventDropped" droppedPayload)

        member _.TestFire(scopeId, subscriptionId) = async {
            match! registry.GetSubscription(scopeId, subscriptionId) with
            | None -> return Error "Subscription not found."
            | Some sub ->
                let testEvent: ModuleEvent = {
                    Id = Guid.NewGuid()
                    OccurredAt = DateTime.UtcNow
                    ScopeId = sub.ScopeId
                    SourceModule = SourceModule
                    EventType = TestEventType
                    Payload = """{"message":"Test fire from ToolUp admin UI"}"""
                }

                let payload: WebhookDeliveryPayload = {
                    DeliveryId = Guid.NewGuid()
                    SubscriptionId = sub.SubscriptionId
                    Event = testEvent
                    DeliveredAt = DateTime.UtcNow
                    Attempt = 1
                }

                // Phase 9v — gate the test fire on `IRateLimiter` for
                // the same reason production deliveries gate: an admin
                // pressing "Test fire" during a quota-exhausted window
                // should observe the throttle, not burst past it.
                let! rateLimitDecision = (getRateLimiter ()).Wait(rateLimitKeyFor sub)

                let! outcome = async {
                    // Delivery-time SSRF re-validation (same DNS-rebinding guard
                    // as runDelivery) before the test fire leaves the process.
                    match WebhookUrlValidator.validate urlPolicy sub.TargetUrl with
                    | Error reason ->
                        return
                            WebhookDeliveryOutcome.Failure(
                                None,
                                sprintf "SSRF policy refused target at delivery (DNS rebinding guard): %s" reason,
                                0L
                            )
                    | Ok() ->
                        match rateLimitDecision with
                        | Refused reason ->
                            return WebhookDeliveryOutcome.Failure(None, sprintf "Rate-limit refused: %s" reason, 0L)
                        | Proceed
                        | DelayedBy _ -> return! deliverOnce httpClient sub payload
                }

                // Record the test-fire row so admins see it in the log
                // (event id None — the synthesised event isn't written
                // to IEventStore, so there's nothing to correlate to).
                do!
                    recordDelivery sub.ScopeId {
                        DeliveryId = payload.DeliveryId
                        SubscriptionId = sub.SubscriptionId
                        EventId = None
                        Attempt = 1
                        AttemptedAt = payload.DeliveredAt
                        Outcome = outcome
                    }

                let result =
                    match outcome with
                    | WebhookDeliveryOutcome.Success(status, latency) -> {
                        DeliveryId = payload.DeliveryId
                        StatusCode = Some status
                        LatencyMs = latency
                        Error = None
                      }
                    | WebhookDeliveryOutcome.Failure(status, error, latency) -> {
                        DeliveryId = payload.DeliveryId
                        StatusCode = status
                        LatencyMs = latency
                        Error = Some error
                      }
                    | WebhookDeliveryOutcome.DeadLettered err -> {
                        DeliveryId = payload.DeliveryId
                        StatusCode = None
                        LatencyMs = 0L
                        Error = Some err
                      }

                return Ok result
        }

    override _.ExecuteAsync(stoppingToken: CancellationToken) = task {
        while not stoppingToken.IsCancellationRequested do
            try
                let! task = queue.Reader.ReadAsync(stoppingToken).AsTask()
                // Fire each task without awaiting; deliveries to
                // independent subscriptions shouldn't head-of-line-block
                // each other. The retry loop's own Async.Sleep handles
                // pacing.
                Async.Start(dispatchToScope task.Event, stoppingToken)
            with
            | :? OperationCanceledException -> ()
            | ex -> logger.Error("[WebhookDispatcher] event=dequeue_loop_error: unexpected error", Some ex)
    }

// ─── Convenience constructor ─────────────────────────────────────

let create
    (registry: IWebhookRegistry)
    (deliveryLog: IWebhookDeliveryLog)
    (eventStore: IEventStore)
    (httpClient: HttpClient)
    (retryPolicy: WebhookRetryPolicy)
    (logger: ILogger)
    (activitySink: IActivitySink)
    (getRateLimiter: unit -> IRateLimiter)
    (urlPolicy: WebhookUrlValidator.WebhookUrlPolicy)
    : WebhookDispatcherService =
    new WebhookDispatcherService(
        registry,
        deliveryLog,
        eventStore,
        httpClient,
        retryPolicy,
        logger,
        activitySink,
        getRateLimiter,
        urlPolicy
    )