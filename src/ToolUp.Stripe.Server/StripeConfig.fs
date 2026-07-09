namespace ToolUp.Stripe.Server

open System
open System.Text
open ToolUp.Platform.ConfigValidation

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

/// Startup preflight for a Stripe-billing deployment: refuses to boot when
/// `WebhookSecret` is empty, not `whsec_…`-shaped, or below the minimum
/// strength. A blank webhook secret is the highest-severity misconfiguration
/// this substrate has — `WebhookSigner` computes `HMAC-SHA256(key=secret)`,
/// and an HMAC with an empty key is publicly computable, so an unset secret
/// lets anyone forge a "verified" `checkout.session.completed` / `invoice.paid`
/// event → fake payment state and free paid-tier upgrades. Turning that from a
/// silent runtime forgery-enabler into a loud startup refusal is the point.
///
/// **Security-class (`ISecurityClassValidator`).** The bypass under
/// `ServerConfig.SkipPreflight = true` is itself the hole — a single boolean
/// must never silently re-open the forged-payment surface, so this validator
/// runs even when preflight is otherwise skipped and still aborts on `Error`.
///
/// **Not `ServerConfig`-integrated.** The Stripe companion has no compose
/// contribution of its own, so a billing deployment wires this validator into
/// its own composition root with
/// `ServerApp.withConfigValidator (StripeConfigValidator config)` (the Phase 9m
/// aggregator runs it at compose end). Registering it *is* the "billing-active"
/// signal — a deployment that never bills never composes it and pays nothing
/// (GP 13).
type StripeConfigValidator(config: StripeConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    // The forged-payment surface a blank secret exposes must not be
    // silently re-openable by flipping SkipPreflight — security-class.
    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = "stripe-webhook-secret"
        member _.Timeout = timeout

        member _.Validate() = async {
            let secret = config.WebhookSecret

            if String.IsNullOrEmpty secret then
                return
                    Error
                        "ToolUp.Stripe: WebhookSecret is empty. Webhook signature verification computes HMAC-SHA256 with this value as the key, and an HMAC with an empty key is publicly computable — any caller could forge a fully-'verified' checkout.session.completed / invoice.paid event and drive fake payment state or free paid-tier upgrades. Set WebhookSecret to the 'whsec_…' value from the Stripe dashboard's webhook endpoint configuration (read it through ISecretStore, never hard-coded) before starting."
            elif not (secret.StartsWith("whsec_", StringComparison.Ordinal)) then
                return
                    Error
                        "ToolUp.Stripe: WebhookSecret is not 'whsec_…'-shaped — it does not look like a Stripe webhook signing secret (a truncated copy, or the API key pasted by mistake, would land here). Copy the exact 'whsec_…' value from the Stripe dashboard's webhook endpoint configuration."
            elif Encoding.UTF8.GetByteCount secret < 32 then
                return
                    Error(
                        sprintf
                            "ToolUp.Stripe: WebhookSecret is %d bytes; a Stripe webhook signing secret is ~35+ bytes. A key this short weakens the HMAC — re-copy the full 'whsec_…' value without truncation."
                            (Encoding.UTF8.GetByteCount secret)
                    )
            else
                return Ok
        }