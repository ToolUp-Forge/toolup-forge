namespace ToolUp.Stripe.Webhook

open System.Text.Json

/// Customer payload for `customer.created`. Minimal — the customer id
/// is all a billing consumer needs to correlate to its own user.
type CustomerPayload = {
    /// Stripe customer id (`cus_…`), from `data.object.id`.
    CustomerId: string
}

/// Subscription payload for the `customer.subscription.*` lifecycle.
type SubscriptionPayload = {
    /// Stripe subscription id (`sub_…`), from `data.object.id`.
    SubscriptionId: string
    /// Owning customer id (`cus_…`), from `data.object.customer`.
    CustomerId: string
    /// Subscription status (`active` / `past_due` / `canceled` / …),
    /// from `data.object.status`. Empty when the event omits it.
    Status: string
}

/// Invoice payload for `invoice.paid` / `invoice.payment_failed`.
type InvoicePayload = {
    /// Stripe invoice id (`in_…`), from `data.object.id`.
    InvoiceId: string
    /// Owning customer id (`cus_…`), from `data.object.customer`.
    CustomerId: string
    /// Amount in the currency's smallest unit (cents), from
    /// `data.object.amount_paid` (paid) / `amount_due` (failed).
    Amount: int64
}

/// Checkout-session payload for `checkout.session.completed`.
type CheckoutSessionPayload = {
    /// Checkout session id (`cs_…`), from `data.object.id`.
    SessionId: string
    /// Customer id created/attached by the session
    /// (`cus_…`), from `data.object.customer`. Empty for
    /// guest checkouts that didn't create a customer.
    CustomerId: string
}

/// Typed Stripe webhook event. Covers the lifecycle set a paid-tier
/// deployment routinely handles; `Unknown` absorbs everything else so
/// the surface is forward-compatible (an event type outside the
/// catalogue is never an error — exactly as the raw-body passthrough
/// behaved before this model existed).
type StripeEvent =
    | CustomerCreated of CustomerPayload
    | SubscriptionCreated of SubscriptionPayload
    | SubscriptionUpdated of SubscriptionPayload
    | SubscriptionDeleted of SubscriptionPayload
    | InvoicePaid of InvoicePayload
    | InvoicePaymentFailed of InvoicePayload
    | CheckoutSessionCompleted of CheckoutSessionPayload
    /// Event `type` outside the curated catalogue (or a body with no
    /// `type` discriminator). Carries the original JSON so a consumer
    /// can hand-route it if it wants the long tail of Stripe events.
    | Unknown of rawJson: string

/// STJ decode of a verified webhook body into a typed `StripeEvent`.
///
/// Decode contract (deliberately permissive — matches the prior
/// raw-body passthrough):
///   * Unparseable JSON → `Error message` (the caller maps to
///     `WebhookError.BodyParseError`).
///   * `type` absent / not a string / not in the catalogue →
///     `Ok (Unknown rawJson)`. Never an error.
///   * `type` in the catalogue → the matching payload, read leniently
///     from `data.object`. Missing sub-objects / fields default to
///     empty / zero rather than failing — Stripe events vary in shape
///     and a billing consumer keys off the ids it finds. A field that
///     IS present but structurally wrong for its slot (e.g. a string
///     where an integer amount is expected) surfaces as `Error`.
///
///     Forward-compat trade-off (the cost of the leniency above): if
///     Stripe later adds a field to a *catalogued* event type, this
///     decoder reads it as the empty/zero default rather than surfacing
///     it — i.e. a genuinely-new field is silently dropped from the
///     typed payload. A consumer that must act on a newly-added field
///     should not wait for the catalogue to grow it; verify the webhook
///     signature, then read the field from the raw body directly (the
///     same bytes the HMAC covered, also available verbatim on the
///     `Unknown` case's `rawJson`).
module StripeEvent =
    /// Read a string property; empty string when the object is absent,
    /// the property is absent / null, or it's a non-string kind. Never
    /// throws — leniency for shape drift.
    let private str (objOpt: JsonElement option) (name: string) : string =
        match objOpt with
        | None -> ""
        | Some obj ->
            match obj.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.String ->
                match v.GetString() with
                | null -> ""
                | s -> s
            | _ -> ""

    /// Read an integer property; 0 when the object or property is
    /// absent. A property that IS present but isn't a JSON number
    /// throws (`GetInt64` on a non-number) — caught by `decode` and
    /// surfaced as a parse error, which is the intended "structurally
    /// malformed for a known type" signal.
    let private int64OrZero (objOpt: JsonElement option) (name: string) : int64 =
        match objOpt with
        | None -> 0L
        | Some obj ->
            match obj.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.Null -> 0L
            | true, v -> v.GetInt64()
            | _ -> 0L

    /// Resolve `data.object`, the Stripe envelope's payload slot.
    /// `None` when either hop is absent — callers then build a
    /// default-valued payload (leniency), they do not error.
    let private dataObject (root: JsonElement) : JsonElement option =
        match root.TryGetProperty "data" with
        | true, d when d.ValueKind = JsonValueKind.Object ->
            match d.TryGetProperty "object" with
            | true, o when o.ValueKind = JsonValueKind.Object -> Some o
            | _ -> None
        | _ -> None

    /// Decode a raw webhook body into a typed event.
    let decode (rawJson: string) : Result<StripeEvent, string> =
        try
            use doc = JsonDocument.Parse rawJson
            let root = doc.RootElement

            let typeStr =
                match root.TryGetProperty "type" with
                | true, t when t.ValueKind = JsonValueKind.String ->
                    match t.GetString() with
                    | null -> ""
                    | s -> s
                | _ -> ""

            // `data.object` for the known cases. Absent → all payload
            // fields default (empty / zero); the helpers handle `None`.
            let obj = dataObject root

            let subscription () = {
                SubscriptionId = str obj "id"
                CustomerId = str obj "customer"
                Status = str obj "status"
            }

            match typeStr with
            | "customer.created" -> Ok(CustomerCreated { CustomerId = str obj "id" })
            | "customer.subscription.created" -> Ok(SubscriptionCreated(subscription ()))
            | "customer.subscription.updated" -> Ok(SubscriptionUpdated(subscription ()))
            | "customer.subscription.deleted" -> Ok(SubscriptionDeleted(subscription ()))
            | "invoice.paid" ->
                Ok(
                    InvoicePaid {
                        InvoiceId = str obj "id"
                        CustomerId = str obj "customer"
                        Amount = int64OrZero obj "amount_paid"
                    }
                )
            | "invoice.payment_failed" ->
                Ok(
                    InvoicePaymentFailed {
                        InvoiceId = str obj "id"
                        CustomerId = str obj "customer"
                        Amount = int64OrZero obj "amount_due"
                    }
                )
            | "checkout.session.completed" ->
                Ok(
                    CheckoutSessionCompleted {
                        SessionId = str obj "id"
                        CustomerId = str obj "customer"
                    }
                )
            | _ -> Ok(Unknown rawJson)
        with ex ->
            Error(sprintf "Stripe event body failed to decode: %s" ex.Message)