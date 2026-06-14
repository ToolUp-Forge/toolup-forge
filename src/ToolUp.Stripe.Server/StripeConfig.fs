namespace ToolUp.Stripe.Server

/// Stripe configuration consumed by the server-side wrappers.
///
/// Phase 05 (`Routes.stripeWebhook`) consumes `WebhookSecret`.
/// Phase 06 (`CustomerPortal.openSessionLink` / `Checkout.createSession`)
/// consumes `ApiKey`.
type StripeConfig = {
    /// The `whsec_…` value from the Stripe dashboard's webhook
    /// configuration.
    WebhookSecret: string
    /// The `sk_live_…` or `sk_test_…` value from the Stripe
    /// dashboard's API keys page.
    ApiKey: string
}