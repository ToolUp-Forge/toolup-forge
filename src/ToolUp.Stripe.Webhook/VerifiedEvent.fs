namespace ToolUp.Stripe.Webhook

/// Output of a successful `WebhookSigner.verify` call.
///
/// `Body` is the raw request body (the bytes the HMAC was computed
/// against); `Event` is the typed decode of that body into the
/// `StripeEvent` catalogue. `Event` was added additively — existing
/// consumers that route from `Body` keep working byte-for-byte; new
/// consumers switch on `Event` for exhaustive, type-safe handling.
/// Code against this record rather than positional fields so future
/// additive fields don't break you.
type VerifiedEvent = {
    /// The raw request body, as received. The HMAC was computed
    /// against these exact bytes (UTF-8).
    Body: string
    /// Stripe-issued event timestamp (Unix seconds). Already
    /// freshness-checked by the verifier.
    Timestamp: int64
    /// Typed decode of `Body`. An event `type` outside the catalogue
    /// (or a body with no `type`) decodes to `StripeEvent.Unknown`
    /// carrying the original JSON — never an error.
    Event: StripeEvent
}