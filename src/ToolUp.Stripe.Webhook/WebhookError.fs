namespace ToolUp.Stripe.Webhook

/// Failure modes for `WebhookSigner.verify`.
///
/// Callers map these to HTTP status:
///   * `MalformedHeader` → 400
///   * `TimestampDrift _` → 408 (Request Timeout, matching Stripe's
///     own freshness-window convention)
///   * `SignatureMismatch` → 400
///   * `BodyParseError _` → 400 (lands once Phase 04 ships the typed
///     event model; v0.1.0-alpha never returns this)
///   * `UnknownEventType _` → 400 (also Phase 04-only)
type WebhookError =
    /// `Stripe-Signature` header missing the required `t=` / `v1=`
    /// fields, or the `t=` value didn't parse as an integer.
    | MalformedHeader
    /// `|now - timestamp|` exceeded the 5-minute window. Payload
    /// carries `(now - timestamp)` in seconds (positive = stale,
    /// negative = clock-skew in the future).
    | TimestampDrift of seconds: int64
    /// HMAC signature recomputed from the body did not match the
    /// `v1=` field. Constant-time compared.
    | SignatureMismatch
    /// Body failed to decode against the typed `StripeEvent` model
    /// (Phase 04+). v0.1.0-alpha doesn't decode and never returns this.
    | BodyParseError of message: string
    /// Body decoded but the event `type` wasn't in the catalogue and
    /// the `Unknown` fallback case is disabled. Phase 04+.
    | UnknownEventType of eventType: string