module ToolUp.Stripe.Server.Tests.StripeWebhookHandlerTests

// FS0044: the WebHostBuilder-based TestServer ctor is deprecated in
// .NET 10 but remains the standard minimal Giraffe test-host pattern.
#nowarn "44"

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Giraffe
open ToolUp.Stripe.Webhook
open ToolUp.Stripe.Server

let private secret = "whsec_test_32_byte_minimum_padding"

let private config: StripeConfig = {
    WebhookSecret = secret
    ApiKey = "sk_test_unused"
}

/// Sign `body` at `now` and return the `Stripe-Signature` header value.
let private signHeader (now: DateTimeOffset) (body: string) : string =
    let timestamp = now.ToUnixTimeSeconds()
    let payload = sprintf "%d.%s" timestamp body
    use h = new HMACSHA256(Encoding.UTF8.GetBytes secret)

    let sigHex =
        Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes payload)).ToLowerInvariant()

    sprintf "t=%d,v1=%s" timestamp sigHex

/// Spin up a Giraffe TestServer mounting `webApp` at POST /webhook.
let private makeServer (webApp: HttpHandler) : TestServer =
    let builder =
        (new WebHostBuilder())
            .ConfigureServices(fun services ->
                services.AddGiraffe() |> ignore
                services.AddLogging() |> ignore)
            .Configure(fun app -> app.UseGiraffe(POST >=> route "/webhook" >=> webApp))

    new TestServer(builder)

/// POST a body + signature header and return the response.
let private post (server: TestServer) (sigHeader: string) (body: string) : Task<HttpResponseMessage> = task {
    use client = server.CreateClient()
    use req = new HttpRequestMessage(HttpMethod.Post, "/webhook")
    req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
    req.Headers.TryAddWithoutValidation("Stripe-Signature", sigHeader) |> ignore
    return! client.SendAsync req
}

let private okHandler
    (calls: int ref)
    : VerifiedEvent -> Microsoft.AspNetCore.Http.HttpContext -> Task<Result<unit, string>> =
    fun _ _ -> task {
        System.Threading.Interlocked.Increment calls |> ignore
        return Ok()
    }

let private signedBody (eventId: string) : string =
    sprintf
        """{"id":"%s","type":"customer.subscription.updated","data":{"object":{"id":"sub_1","customer":"cus_1","status":"active"}}}"""
        eventId

[<Tests>]
let tests =
    testList "stripeWebhook handler" [
        test "correctly-signed event invokes the handler and returns 200" {
            let calls = ref 0
            use server = makeServer (Routes.stripeWebhook config (okHandler calls))
            let body = signedBody "evt_ok"
            let header = signHeader DateTimeOffset.UtcNow body

            let resp = (post server header body).Result

            Expect.equal resp.StatusCode HttpStatusCode.OK "200 on Ok"
            Expect.equal calls.Value 1 "handler invoked once"
        }
        test "tampered body / bad signature returns 400 and never invokes the handler" {
            let calls = ref 0
            use server = makeServer (Routes.stripeWebhook config (okHandler calls))
            let body = signedBody "evt_bad"
            let header = signHeader DateTimeOffset.UtcNow body
            // Verify against a different body than was signed.
            let tampered = signedBody "evt_pwned"

            let resp = (post server header tampered).Result

            Expect.equal resp.StatusCode HttpStatusCode.BadRequest "400 on signature mismatch"
            Expect.equal calls.Value 0 "handler not invoked"
        }
        test "stale timestamp returns 408" {
            let calls = ref 0
            use server = makeServer (Routes.stripeWebhook config (okHandler calls))
            let body = signedBody "evt_stale"
            // Signed 10 minutes ago — outside the 5-minute window.
            let header = signHeader (DateTimeOffset.UtcNow.AddMinutes(-10.0)) body

            let resp = (post server header body).Result

            Expect.equal resp.StatusCode HttpStatusCode.RequestTimeout "408 on timestamp drift"
            Expect.equal calls.Value 0 "handler not invoked"
        }
        test "replayed event id invokes the handler once and returns 200 both times" {
            let calls = ref 0
            use server = makeServer (Routes.stripeWebhook config (okHandler calls))
            let body = signedBody "evt_replay"
            let header = signHeader DateTimeOffset.UtcNow body

            let first = (post server header body).Result
            let second = (post server header body).Result

            Expect.equal first.StatusCode HttpStatusCode.OK "first 200"
            Expect.equal second.StatusCode HttpStatusCode.OK "replay 200"
            Expect.equal calls.Value 1 "handler invoked exactly once across the replay"
        }
        test "handler Error produces 500" {
            let errHandler: VerifiedEvent -> Microsoft.AspNetCore.Http.HttpContext -> Task<Result<unit, string>> =
                fun _ _ -> task { return Error "domain failure" }

            use server = makeServer (Routes.stripeWebhook config errHandler)
            let body = signedBody "evt_err"
            let header = signHeader DateTimeOffset.UtcNow body

            let resp = (post server header body).Result

            Expect.equal resp.StatusCode HttpStatusCode.InternalServerError "500 on handler Error"
        }
    ]