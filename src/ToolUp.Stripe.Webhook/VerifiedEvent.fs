namespace ToolUp.Stripe.Webhook

/// Output of a successful `WebhookSigner.verify` call.
///
/// v0.1.0-alpha returns the raw body for the caller to route from;
/// Phase 04 adds a typed `Event: StripeEvent` field carrying the
/// decoded payload struct. Consumers should code against this record
/// rather than positional fields so the Phase 04 field addition
/// doesn't break them.
type VerifiedEvent = {
    /// The raw request body, as received. The HMAC was computed
    /// against these exact bytes (UTF-8).
    Body: string
    /// Stripe-issued event timestamp (Unix seconds). Already
    /// freshness-checked by the verifier.
    Timestamp: int64
}