module ToolUp.Stripe.Server.Tests.StripeBillingProviderTests

open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform.Payments
open ToolUp.Stripe.Server

// ─── Phase 240 — direct-billing IPaymentProvider over Stripe ─────────

let private apiKey = "sk_test_SECRET_do_not_leak"

let private config: StripeConfig = {
    WebhookSecret = "whsec_x"
    ApiKey = apiKey
}

type private StubHandler(responder: HttpRequestMessage -> HttpResponseMessage) =
    inherit HttpMessageHandler()
    member val LastUri = "" with get, set

    override this.SendAsync(request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
        this.LastUri <- request.RequestUri.ToString()
        Task.FromResult(responder request)

let private clientReturning (status: HttpStatusCode) (body: string) : HttpClient * StubHandler =
    let handler =
        new StubHandler(fun _ ->
            let resp = new HttpResponseMessage(status)
            resp.Content <- new StringContent(body, Encoding.UTF8, "application/json")
            resp)

    new HttpClient(handler), handler

let private providerWith (status: HttpStatusCode) (body: string) : IPaymentProvider * StubHandler =
    let client, handler = clientReturning status body
    StripeBillingProvider(config, client) :> IPaymentProvider, handler

let private req: PaymentCheckoutRequest = {
    PriceId = "price_1"
    Quantity = 1
    CustomerId = None
    CustomerEmail = Some "user@example.com"
    TenantId = None
    SuccessUrl = "https://app/ok"
    CancelUrl = "https://app/no"
}

[<Tests>]
let tests =
    testList "StripeBillingProvider (Phase 240)" [
        test "checkout returns the hosted URL + hits the checkout endpoint" {
            let body = """{"id":"cs_1","url":"https://checkout.stripe.com/c/pay/abc"}"""
            let provider, handler = providerWith HttpStatusCode.OK body
            let result = (provider.CreateCheckoutSession req |> Async.RunSynchronously)

            match result with
            | Ok s -> Expect.equal s.Url "https://checkout.stripe.com/c/pay/abc" "checkout url"
            | Error e -> failtestf "expected Ok, got %A" e

            Expect.stringContains handler.LastUri "/v1/checkout/sessions" "checkout path"
        }

        test "checkout with neither customer id nor email fails without an HTTP call" {
            let provider, handler = providerWith HttpStatusCode.OK "{}"

            let result =
                provider.CreateCheckoutSession {
                    req with
                        CustomerId = None
                        CustomerEmail = None
                }
                |> Async.RunSynchronously

            match result with
            | Error(ProviderError _) -> ()
            | other -> failtestf "expected ProviderError, got %A" other

            Expect.equal handler.LastUri "" "no HTTP call made"
        }

        test "portal returns the portal URL" {
            let body = """{"id":"bps_1","url":"https://billing.stripe.com/p/session/xyz"}"""
            let provider, _ = providerWith HttpStatusCode.OK body

            let result =
                provider.CreatePortalSession("cus_1", "https://app/account")
                |> Async.RunSynchronously

            match result with
            | Ok s -> Expect.equal s.Url "https://billing.stripe.com/p/session/xyz" "portal url"
            | Error e -> failtestf "expected Ok, got %A" e
        }

        test "subscription status maps active → ActiveSubscription with plan id" {
            let body =
                """{"object":"list","data":[{"status":"active","items":{"data":[{"price":{"id":"price_pro"}}]}}]}"""

            let provider, _ = providerWith HttpStatusCode.OK body
            let result = provider.GetSubscriptionStatus "cus_1" |> Async.RunSynchronously
            Expect.equal result (Ok(ActiveSubscription "price_pro")) "active subscription"
        }

        test "subscription status maps empty list → NoSubscription" {
            let body = """{"object":"list","data":[]}"""
            let provider, _ = providerWith HttpStatusCode.OK body
            let result = provider.GetSubscriptionStatus "cus_1" |> Async.RunSynchronously
            Expect.equal result (Ok NoSubscription) "no subscription"
        }

        test "Stripe error surfaces as ProviderError" {
            let body =
                """{"error":{"message":"No such customer","type":"invalid_request_error"}}"""

            let provider, _ = providerWith HttpStatusCode.PaymentRequired body
            let result = provider.GetSubscriptionStatus "cus_missing" |> Async.RunSynchronously

            match result with
            | Error(ProviderError msg) -> Expect.isFalse (msg.Contains apiKey) "no api key leak"
            | other -> failtestf "expected ProviderError, got %A" other
        }
    ]