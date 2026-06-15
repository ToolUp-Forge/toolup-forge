namespace ToolUp.Stripe.Server

open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ToolUp.Stripe.Webhook

/// Integration seam letting a consumer re-stamp a user's tier on a
/// billing event **without** `ToolUp.Stripe.Server` depending on cookie
/// internals. The handler owns HTTP + verification; the consumer's sink
/// implementation owns the cookie crypto (typically by calling
/// `ToolUp.Stripe.TierToken.Cookie.issue`). The interface deliberately
/// references no `TierToken` type, so the `Server` package stays
/// cookie-agnostic and the two packages compose only in consumer code
/// (GP 1).
///
/// The sink receives the verified event + `HttpContext` so the consumer
/// can resolve which user the event maps to (consumer domain logic) and
/// decide the new tier. It fires **after** the consumer handler returns
/// `Ok` and **only** on tier-changing events — never on a verification
/// failure, never on a handler error, never on a replayed event.
type ITierTokenSink =
    /// Invoked after a successful handler on a tier-changing event.
    abstract OnBillingEvent: event: VerifiedEvent -> ctx: HttpContext -> Task<unit>

module TierTokenSink =
    /// Events that can change a user's entitlement tier. The sink fires
    /// only on these, so a consumer's implementation isn't called on
    /// every webhook (invoices, customer creation, the unknown tail).
    let isTierChanging (event: StripeEvent) : bool =
        match event with
        | SubscriptionCreated _
        | SubscriptionUpdated _
        | SubscriptionDeleted _
        | CheckoutSessionCompleted _ -> true
        | CustomerCreated _
        | InvoicePaid _
        | InvoicePaymentFailed _
        | Unknown _ -> false