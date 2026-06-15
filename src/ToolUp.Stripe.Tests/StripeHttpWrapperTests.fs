module ToolUp.Stripe.Server.Tests.StripeHttpWrapperTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Stripe.Server

let private apiKey = "sk_test_SECRET_KEY_VALUE_do_not_leak"

/// Captured request shape — taken inside `SendAsync` before `HttpClient`
/// disposes the request + its content.
type CapturedRequest = {
    Method: HttpMethod
    Uri: string
    AuthScheme: string
    Body: string
}

/// Stub `HttpMessageHandler` driven by a responder. Captures the last
/// request's shape (eagerly, so assertions survive disposal).
type StubHandler(responder: HttpRequestMessage -> HttpResponseMessage) =
    inherit HttpMessageHandler()
    member val Last: CapturedRequest option = None with get, set

    override this.SendAsync(request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
        let body =
            match request.Content with
            | null -> ""
            | content -> content.ReadAsStringAsync().Result

        this.Last <-
            Some {
                Method = request.Method
                Uri = request.RequestUri.ToString()
                AuthScheme =
                    match request.Headers.Authorization with
                    | null -> ""
                    | auth -> auth.Scheme
                Body = body
            }

        Task.FromResult(responder request)

let private jsonResponse (status: HttpStatusCode) (body: string) : HttpRequestMessage -> HttpResponseMessage =
    fun _ ->
        let resp = new HttpResponseMessage(status)
        resp.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        resp

let private clientWith (responder: HttpRequestMessage -> HttpResponseMessage) : HttpClient * StubHandler =
    let handler = new StubHandler(responder)
    new HttpClient(handler), handler

[<Tests>]
let tests =
    testList "Stripe HTTP wrappers" [
        test "openSessionLink returns the portal Uri on success" {
            let body = """{"id":"bps_1","url":"https://billing.stripe.com/p/session/xyz"}"""
            let client, handler = clientWith (jsonResponse HttpStatusCode.OK body)

            let result =
                (CustomerPortal.openSessionLinkWith client "cus_1" "https://app.example.com/account" apiKey).Result

            match result with
            | Ok uri -> Expect.equal (uri.ToString()) "https://billing.stripe.com/p/session/xyz" "portal uri"
            | Error e -> failwithf "expected Ok, got %s" e

            // Request shape: POST, correct path, bearer auth.
            match handler.Last with
            | Some req ->
                Expect.equal req.Method HttpMethod.Post "POST"
                Expect.stringContains req.Uri "/v1/billing_portal/sessions" "path"
                Expect.equal req.AuthScheme "Bearer" "bearer scheme"
                Expect.stringContains req.Body "customer=cus_1" "customer field"
            | None -> failwith "no request captured"
        }
        test "openSessionLink surfaces a Stripe error without leaking the API key" {
            let body =
                """{"error":{"message":"No such customer: cus_missing","type":"invalid_request_error"}}"""

            let client, _ = clientWith (jsonResponse HttpStatusCode.PaymentRequired body)

            let result =
                (CustomerPortal.openSessionLinkWith client "cus_missing" "https://app.example.com" apiKey).Result

            match result with
            | Error msg ->
                Expect.stringContains msg "No such customer: cus_missing" "Stripe message surfaced"
                Expect.isFalse (msg.Contains apiKey) "API key NOT echoed in the error"
            | Ok uri -> failwithf "expected Error, got %A" uri
        }
        test "openSessionLink maps a network failure to Error" {
            let client, _ =
                clientWith (fun _ -> raise (HttpRequestException "connection refused"))

            let result =
                (CustomerPortal.openSessionLinkWith client "cus_1" "https://app.example.com" apiKey).Result

            match result with
            | Error msg -> Expect.stringContains msg "Stripe request failed" "network failure mapped"
            | Ok uri -> failwithf "expected Error, got %A" uri
        }
        test "createSession returns the checkout Uri on success" {
            let body =
                """{"id":"cs_test_1","url":"https://checkout.stripe.com/c/pay/cs_test_1"}"""

            let client, handler = clientWith (jsonResponse HttpStatusCode.OK body)

            let request: CheckoutRequest = {
                PriceId = "price_123"
                Quantity = 1
                Mode = CheckoutMode.Subscription
                SuccessUrl = "https://app.example.com/ok"
                CancelUrl = "https://app.example.com/cancel"
                Customer = CheckoutCustomer.CustomerEmail "user@example.com"
            }

            let result = (Checkout.createSessionWith client request apiKey).Result

            match result with
            | Ok uri -> Expect.equal (uri.ToString()) "https://checkout.stripe.com/c/pay/cs_test_1" "checkout uri"
            | Error e -> failwithf "expected Ok, got %s" e

            match handler.Last with
            | Some req ->
                Expect.stringContains req.Uri "/v1/checkout/sessions" "path"
                Expect.stringContains req.Body "mode=subscription" "mode field"
                Expect.stringContains req.Body "customer_email=user" "customer_email field"
                Expect.stringContains req.Body "price" "line item price field"
            | None -> failwith "no request captured"
        }
        test "createSession maps a Stripe error the same way" {
            let body =
                """{"error":{"message":"No such price: price_bad","type":"invalid_request_error"}}"""

            let client, _ = clientWith (jsonResponse HttpStatusCode.BadRequest body)

            let request: CheckoutRequest = {
                PriceId = "price_bad"
                Quantity = 1
                Mode = CheckoutMode.Payment
                SuccessUrl = "https://app.example.com/ok"
                CancelUrl = "https://app.example.com/cancel"
                Customer = CheckoutCustomer.CustomerId "cus_1"
            }

            let result = (Checkout.createSessionWith client request apiKey).Result

            match result with
            | Error msg ->
                Expect.stringContains msg "No such price: price_bad" "Stripe message surfaced"
                Expect.isFalse (msg.Contains apiKey) "API key NOT echoed"
            | Ok uri -> failwithf "expected Error, got %A" uri
        }
    ]