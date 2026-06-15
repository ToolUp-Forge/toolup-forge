namespace ToolUp.Stripe.Server

open System
open System.Net.Http
open System.Threading.Tasks

/// Checkout-session mode (`mode=` on `/v1/checkout/sessions`).
type CheckoutMode =
    | Subscription
    | Payment
    | Setup

module CheckoutMode =
    let toForm =
        function
        | Subscription -> "subscription"
        | Payment -> "payment"
        | Setup -> "setup"

/// Who the checkout session is for — an existing customer id, or an
/// email Stripe attaches a new/looked-up customer to.
type CheckoutCustomer =
    | CustomerId of string
    | CustomerEmail of string

/// Minimal checkout-session request. Owns just the fields the signup →
/// checkout path needs; richer Stripe options (discounts, tax,
/// metadata) are not modelled here — callers needing them consume
/// `Stripe.net` directly.
type CheckoutRequest = {
    /// Stripe price id (`price_…`) for the single line item.
    PriceId: string
    /// Quantity of the line item (typically 1).
    Quantity: int
    /// Session mode.
    Mode: CheckoutMode
    /// URL Stripe redirects to on success.
    SuccessUrl: string
    /// URL Stripe redirects to on cancel.
    CancelUrl: string
    /// Customer the session is for.
    Customer: CheckoutCustomer
}

/// Checkout-session creator — the signup → checkout path. POSTs to
/// `/v1/checkout/sessions` and returns the hosted checkout URL the
/// consumer redirects the user to. BCL `HttpClient` only.
module Checkout =
    /// Create a checkout session. `client` is injected for testing; the
    /// public `createSession` uses the shared client.
    let createSessionWith (client: HttpClient) (request: CheckoutRequest) (apiKey: string) : Task<Result<Uri, string>> = task {
        let form = [
            "mode", CheckoutMode.toForm request.Mode
            "success_url", request.SuccessUrl
            "cancel_url", request.CancelUrl
            "line_items[0][price]", request.PriceId
            "line_items[0][quantity]", string request.Quantity
            match request.Customer with
            | CustomerId id -> "customer", id
            | CustomerEmail email -> "customer_email", email
        ]

        let! result = StripeHttp.postForm client apiKey "/v1/checkout/sessions" form
        return result |> Result.bind (fun root -> StripeHttp.readUri root "url")
    }

    /// Create a checkout session using the process-shared `HttpClient`.
    let createSession (request: CheckoutRequest) (apiKey: string) : Task<Result<Uri, string>> =
        createSessionWith (StripeHttp.shared ()) request apiKey