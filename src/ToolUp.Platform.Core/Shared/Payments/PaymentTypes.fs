// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Payments

// ─── IPaymentProvider — merchant-model-neutral payment substrate ─────
//
// The thin, vendor-neutral payment slice. A standalone consumer
// deployment bills its own customers on its own payment account (no
// platform fee, no Connect account) — the direct-billing shape that the
// Connect-shaped commercial `IPaymentGateway` (every method takes a
// tenantId + application fee) does not model cleanly. `IPaymentProvider`
// prescribes neither merchant model: `TenantId` is optional, so the same
// interface admits both a direct-billing impl (`StripeBillingProvider`
// in ToolUp.Stripe.Server) and a future Connect impl.
//
// GP 1 — no vendor type appears on this surface (the Stripe impl keeps
// `Stripe.net` out entirely; BCL HttpClient only). GP 7 — async at every
// boundary. GP 12 — identity by value (string ids), errors as data.

/// Why a payment operation failed. Never carries secret material.
type PaymentError =
    /// No payment provider is wired / configured for this deployment.
    | ProviderNotConfigured
    /// The provider rejected or failed the call. The message is the
    /// provider's sanitised error text (never an API key).
    | ProviderError of message: string

/// A hosted-checkout session the consumer redirects the user to.
type CheckoutSession = { Url: string }

/// A billing-portal session the consumer redirects the user to.
type PortalSession = { Url: string }

/// The deployment's view of a customer's subscription.
type SubscriptionStatus =
    /// No subscription on record for the customer.
    | NoSubscription
    /// An active (or trialing) subscription on the named plan/price.
    | ActiveSubscription of planId: string
    /// A subscription that exists but is past due on payment.
    | PastDue
    /// A subscription that has been cancelled.
    | Canceled

/// A checkout request. `TenantId` is optional — a direct-billing
/// deployment leaves it `None` (bills on its own account, no fee); a
/// Connect-shaped impl may populate it. Exactly one of `CustomerId` /
/// `CustomerEmail` identifies who the session is for.
type PaymentCheckoutRequest = {
    /// Provider price/plan id for the single line item.
    PriceId: string
    /// Line-item quantity (typically 1).
    Quantity: int
    /// Existing provider customer id, when known.
    CustomerId: string option
    /// Email to attach a new/looked-up customer to, when there is no id.
    CustomerEmail: string option
    /// Optional tenant id — `None` for direct billing; populated by a
    /// Connect-shaped impl. The substrate prescribes neither model.
    TenantId: string option
    /// URL the provider redirects to on success.
    SuccessUrl: string
    /// URL the provider redirects to on cancel.
    CancelUrl: string
}

/// Merchant-model-neutral payment provider. Implemented by a companion
/// (Stripe direct-billing first); the SDK core holds only this contract,
/// never a vendor type.
type IPaymentProvider =
    /// Create a hosted-checkout session for the request.
    abstract CreateCheckoutSession: PaymentCheckoutRequest -> Async<Result<CheckoutSession, PaymentError>>
    /// Open a billing-portal session for an existing customer.
    abstract CreatePortalSession: customerId: string * returnUrl: string -> Async<Result<PortalSession, PaymentError>>
    /// Read a customer's current subscription status.
    abstract GetSubscriptionStatus: customerId: string -> Async<Result<SubscriptionStatus, PaymentError>>