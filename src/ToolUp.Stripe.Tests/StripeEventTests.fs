module ToolUp.Stripe.Webhook.Tests.StripeEventTests

open System
open System.Security.Cryptography
open System.Text
open Expecto
open ToolUp.Stripe.Webhook

/// Build a Stripe-shaped event envelope: `{ "id", "type", "data": { "object": {...} } }`.
let private envelope (eventType: string) (object': string) : string =
    sprintf """{"id":"evt_test","type":"%s","data":{"object":%s}}""" eventType object'

let private secret = "whsec_test_32_byte_minimum_padding"

let private signed (now: DateTimeOffset) (body: string) : string =
    let timestamp = now.ToUnixTimeSeconds()
    let payload = sprintf "%d.%s" timestamp body
    use h = new HMACSHA256(Encoding.UTF8.GetBytes secret)

    let sigHex =
        Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes payload)).ToLowerInvariant()

    sprintf "t=%d,v1=%s" timestamp sigHex

[<Tests>]
let tests =
    testList "StripeEvent" [
        test "customer.created decodes to CustomerCreated with the customer id" {
            let body = envelope "customer.created" """{"id":"cus_123"}"""

            match StripeEvent.decode body with
            | Ok(CustomerCreated p) -> Expect.equal p.CustomerId "cus_123" "customer id"
            | other -> failwithf "expected CustomerCreated, got %A" other
        }
        test "customer.subscription.created decodes to SubscriptionCreated with sub/customer/status" {
            let body =
                envelope "customer.subscription.created" """{"id":"sub_1","customer":"cus_1","status":"active"}"""

            match StripeEvent.decode body with
            | Ok(SubscriptionCreated p) ->
                Expect.equal p.SubscriptionId "sub_1" "sub id"
                Expect.equal p.CustomerId "cus_1" "customer id"
                Expect.equal p.Status "active" "status"
            | other -> failwithf "expected SubscriptionCreated, got %A" other
        }
        test "customer.subscription.updated decodes to SubscriptionUpdated" {
            let body =
                envelope "customer.subscription.updated" """{"id":"sub_2","customer":"cus_2","status":"past_due"}"""

            match StripeEvent.decode body with
            | Ok(SubscriptionUpdated p) ->
                Expect.equal p.SubscriptionId "sub_2" "sub id"
                Expect.equal p.Status "past_due" "status"
            | other -> failwithf "expected SubscriptionUpdated, got %A" other
        }
        test "customer.subscription.deleted decodes to SubscriptionDeleted" {
            let body =
                envelope "customer.subscription.deleted" """{"id":"sub_3","customer":"cus_3","status":"canceled"}"""

            match StripeEvent.decode body with
            | Ok(SubscriptionDeleted p) -> Expect.equal p.Status "canceled" "status"
            | other -> failwithf "expected SubscriptionDeleted, got %A" other
        }
        test "invoice.paid decodes to InvoicePaid with amount_paid" {
            let body =
                envelope "invoice.paid" """{"id":"in_1","customer":"cus_1","amount_paid":2500}"""

            match StripeEvent.decode body with
            | Ok(InvoicePaid p) ->
                Expect.equal p.InvoiceId "in_1" "invoice id"
                Expect.equal p.Amount 2500L "amount"
            | other -> failwithf "expected InvoicePaid, got %A" other
        }
        test "invoice.payment_failed decodes to InvoicePaymentFailed with amount_due" {
            let body =
                envelope "invoice.payment_failed" """{"id":"in_2","customer":"cus_2","amount_due":999}"""

            match StripeEvent.decode body with
            | Ok(InvoicePaymentFailed p) -> Expect.equal p.Amount 999L "amount_due"
            | other -> failwithf "expected InvoicePaymentFailed, got %A" other
        }
        test "checkout.session.completed decodes to CheckoutSessionCompleted" {
            let body =
                envelope "checkout.session.completed" """{"id":"cs_1","customer":"cus_9"}"""

            match StripeEvent.decode body with
            | Ok(CheckoutSessionCompleted p) ->
                Expect.equal p.SessionId "cs_1" "session id"
                Expect.equal p.CustomerId "cus_9" "customer id"
            | other -> failwithf "expected CheckoutSessionCompleted, got %A" other
        }
        test "unknown event type decodes to Unknown carrying the original JSON" {
            let body = envelope "radar.early_fraud_warning.created" """{"id":"issfr_1"}"""

            match StripeEvent.decode body with
            | Ok(Unknown raw) -> Expect.equal raw body "raw JSON preserved verbatim"
            | other -> failwithf "expected Unknown, got %A" other
        }
        test "body with no type decodes to Unknown (permissive default)" {
            match StripeEvent.decode "{}" with
            | Ok(Unknown raw) -> Expect.equal raw "{}" "raw preserved"
            | other -> failwithf "expected Unknown, got %A" other
        }
        test "known type with no data.object decodes leniently to default-valued payload" {
            // Mirrors the legacy passthrough body shape: a known type but
            // no data envelope. Must NOT error — empty payload fields.
            let body = """{"type":"customer.subscription.created","id":"evt_test"}"""

            match StripeEvent.decode body with
            | Ok(SubscriptionCreated p) -> Expect.equal p.SubscriptionId "" "no data.object → empty id"
            | other -> failwithf "expected lenient SubscriptionCreated, got %A" other
        }
        test "unparseable JSON returns Error (maps to BodyParseError)" {
            match StripeEvent.decode "{ this is not json" with
            | Error msg -> Expect.isNonEmpty msg "descriptive message"
            | Ok ev -> failwithf "expected Error, got %A" ev
        }
        test "structurally-malformed payload for a known type returns Error" {
            // amount_paid present but a string, not a number → GetInt64 throws.
            let body = envelope "invoice.paid" """{"id":"in_1","amount_paid":"not-a-number"}"""

            match StripeEvent.decode body with
            | Error msg -> Expect.isNonEmpty msg "descriptive message"
            | Ok ev -> failwithf "expected Error for malformed amount, got %A" ev
        }
        test "verify threads the typed Event onto VerifiedEvent" {
            let now = DateTimeOffset.UtcNow

            let body =
                envelope "customer.subscription.updated" """{"id":"sub_v","customer":"cus_v","status":"active"}"""

            let header = signed now body

            match WebhookSigner.verifyWith now secret body header with
            | Ok verified ->
                Expect.equal verified.Body body "raw body retained"

                match verified.Event with
                | SubscriptionUpdated p -> Expect.equal p.SubscriptionId "sub_v" "typed event populated"
                | other -> failwithf "expected SubscriptionUpdated event, got %A" other
            | Error e -> failwithf "expected Ok, got %A" e
        }
        test "verify surfaces a malformed body as BodyParseError" {
            let now = DateTimeOffset.UtcNow
            let body = """{"type":"invoice.paid","data":{"object":{"amount_paid":"nope"}}}"""
            let header = signed now body

            match WebhookSigner.verifyWith now secret body header with
            | Error(BodyParseError msg) -> Expect.isNonEmpty msg "descriptive message"
            | other -> failwithf "expected BodyParseError, got %A" other
        }
    ]