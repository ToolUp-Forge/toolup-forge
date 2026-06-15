namespace ToolUp.Stripe.Server

open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Giraffe
open ToolUp.Stripe.Webhook

/// Compose-time options for the webhook handler. Extensible record so
/// later additive seams slot in without changing the call shape; a
/// deployment that wants the defaults uses `Routes.stripeWebhook`.
type WebhookOptions = {
    /// Event-id deduplication store. Defaults to the in-process
    /// `InMemoryIdempotencyStore`; a multi-instance deployment supplies
    /// a durable store.
    Store: IWebhookIdempotencyStore
    /// Optional tier-token sink. When supplied, it is invoked after a
    /// successful handler on a tier-changing event (before the `200`).
    /// `None` by default — a deployment that doesn't bill on tiers pays
    /// nothing.
    TierTokenSink: ITierTokenSink option
}

module WebhookOptions =
    /// Fresh default options — a NEW in-memory idempotency store each
    /// call (a function, not a shared value, so two mounted handlers
    /// don't silently share dedup state), no tier-token sink.
    let create () : WebhookOptions = {
        Store = InMemoryIdempotencyStore()
        TierTokenSink = None
    }

    /// Replace the idempotency store.
    let withStore (store: IWebhookIdempotencyStore) (options: WebhookOptions) : WebhookOptions = {
        options with
            Store = store
    }

    /// Supply a tier-token sink, invoked after a successful handler on a
    /// tier-changing event.
    let withTierTokenSink (sink: ITierTokenSink) (options: WebhookOptions) : WebhookOptions = {
        options with
            TierTokenSink = Some sink
    }

/// Giraffe / ASP.NET Core webhook wiring.
module Routes =
    [<Literal>]
    let StripeSignatureHeader = "Stripe-Signature"

    /// HTTP status for each verification failure. `TimestampDrift` maps
    /// to 408 (Request Timeout), matching Stripe's freshness-window
    /// convention; every other verification failure is a 400.
    let private statusForError (err: WebhookError) : int =
        match err with
        | TimestampDrift _ -> 408
        | MalformedHeader
        | SignatureMismatch
        | BodyParseError _
        | UnknownEventType _ -> 400

    /// Human-readable reason for a non-200 path. Contains NO secret
    /// material (no signature bytes, no secret, no full body).
    let private describe (err: WebhookError) : string =
        match err with
        | MalformedHeader -> "malformed Stripe-Signature header"
        | TimestampDrift seconds -> sprintf "timestamp drift %ds outside the 5-minute window" seconds
        | SignatureMismatch -> "signature mismatch"
        | BodyParseError message -> sprintf "body parse error: %s" message
        | UnknownEventType eventType -> sprintf "unknown event type: %s" eventType

    /// Read the raw request body (the exact bytes the HMAC covers).
    let private readRawBody (ctx: HttpContext) : Task<string> = task {
        use reader =
            new StreamReader(
                ctx.Request.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks = false,
                leaveOpen = true
            )

        return! reader.ReadToEndAsync()
    }

    /// Extract the Stripe event id (`evt_…`) from the body root for
    /// deduplication. `None` when absent — such an event is processed
    /// without dedup rather than rejected.
    let private tryEventId (body: string) : string option =
        try
            use doc = JsonDocument.Parse body

            match doc.RootElement.TryGetProperty "id" with
            | true, v when v.ValueKind = JsonValueKind.String ->
                match v.GetString() with
                | null
                | "" -> None
                | s -> Some s
            | _ -> None
        with _ ->
            None

    /// The production webhook handler with full control over options.
    ///
    /// Reads the raw body + `Stripe-Signature` header, verifies via
    /// `WebhookSigner`, deduplicates on the event id, invokes the
    /// consumer handler with the typed `VerifiedEvent`, and maps the
    /// outcome to HTTP status:
    ///   * `200` — handler returned `Ok` (or a replayed event id
    ///     short-circuited without re-invoking the handler).
    ///   * `400` — malformed header / signature mismatch / body parse
    ///     error.
    ///   * `408` — timestamp outside the freshness window.
    ///   * `500` — handler returned `Error` (logged; no secret leak).
    let stripeWebhookWith
        (options: WebhookOptions)
        (config: StripeConfig)
        (handler: VerifiedEvent -> HttpContext -> Task<Result<unit, string>>)
        : HttpHandler =
        // Surface the single-instance idempotency default loudly. The
        // default `InMemoryIdempotencyStore` dedups per-process only;
        // under N replicas each instance claims the same event
        // independently and the consumer handler fires N times
        // (duplicate billing). The Stripe companion is not
        // `ServerConfig`-integrated, so we cannot read `ReplicaCount`
        // here to fail closed the way `JobSchedulerInstanceValidator`
        // does — instead we warn once on first use. A multi-instance
        // deployment composes a durable store via `WebhookOptions.withStore`.
        let usingInMemoryStore =
            match box options.Store with
            | :? InMemoryIdempotencyStore -> true
            | _ -> false

        let warnedInMemory = ref false

        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            let logger = ctx.GetLogger "ToolUp.Stripe.Server.Routes"

            if usingInMemoryStore && not warnedInMemory.Value then
                warnedInMemory.Value <- true

                logger.LogWarning(
                    "Stripe webhook is using the in-process InMemoryIdempotencyStore (dev default). Event-id deduplication is per-process and is lost on restart — under multiple replicas the same event is processed once per instance (e.g. duplicate billing operations). Compose a durable store via WebhookOptions.withStore for any multi-instance deployment."
                )

            let! body = readRawBody ctx

            let header =
                match ctx.Request.Headers.TryGetValue StripeSignatureHeader with
                | true, values -> string values
                | _ -> ""

            let finish (code: int) (payload: string) : HttpHandler = setStatusCode code >=> text payload

            match WebhookSigner.verify config.WebhookSecret body header with
            | Error err ->
                logger.LogWarning("Stripe webhook verification failed: {Reason}", describe err)
                return! finish (statusForError err) "verification failed" next ctx
            | Ok verified ->
                let! claimed =
                    match tryEventId body with
                    | Some eventId -> options.Store.TryClaim eventId |> Async.StartImmediateAsTask
                    | None ->
                        // Silent-path observability: an event with no usable
                        // `id` is processed WITHOUT dedup. A Stripe redelivery
                        // of the same event would then be processed again.
                        logger.LogWarning(
                            "Stripe webhook event has no usable 'id' field — processing WITHOUT idempotency dedup. A redelivery of this event will be processed again."
                        )

                        Task.FromResult true

                if not claimed then
                    logger.LogInformation("Stripe webhook replay ignored — short-circuit 200")
                    return! finish 200 "ok (replay)" next ctx
                else
                    let! result = handler verified ctx

                    match result with
                    | Ok() ->
                        // Fire the tier-token sink (if supplied) after a
                        // successful handler, only on tier-changing events,
                        // before the 200.
                        match options.TierTokenSink with
                        | Some sink when TierTokenSink.isTierChanging verified.Event ->
                            do! sink.OnBillingEvent verified ctx
                        | _ -> ()

                        return! finish 200 "ok" next ctx
                    | Error message ->
                        logger.LogError("Stripe webhook handler error: {Error}", message)
                        return! finish 500 "handler error" next ctx
        }

    /// The production webhook handler with default options (in-memory
    /// idempotency). The common entry point for a single-instance
    /// deployment.
    let stripeWebhook
        (config: StripeConfig)
        (handler: VerifiedEvent -> HttpContext -> Task<Result<unit, string>>)
        : HttpHandler =
        stripeWebhookWith (WebhookOptions.create ()) config handler