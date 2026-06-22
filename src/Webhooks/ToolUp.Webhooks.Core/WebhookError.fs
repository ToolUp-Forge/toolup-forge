namespace ToolUp.Webhooks

/// Failure modes of inbound-webhook verification + dispatch. A verify
/// error is fail-closed: the registered handler is never invoked.
type WebhookError =
    /// The signature header was present but could not be parsed into the
    /// elements the scheme requires (e.g. Stripe-style `t=,v1=`).
    | MalformedHeader
    /// The scheme's signature header was absent.
    | MissingSignature
    /// `|now - timestamp|` exceeded the scheme's freshness window
    /// (replay protection). Carries the observed drift in seconds.
    | TimestampDrift of driftSeconds: int64
    /// The recomputed HMAC did not match the provided signature.
    | SignatureMismatch
    /// The verified event's id was already processed (idempotent
    /// redelivery). Surfaced so the route can ack without re-running.
    | DuplicateDelivery
    /// No handler is registered for the request's `kind`.
    | NoHandlerRegistered of kind: string