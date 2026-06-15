module ToolUp.Stripe.Server.Tests.TierTokenSinkTests

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ToolUp.Stripe.Webhook
open ToolUp.Stripe.Server
open ToolUp.Stripe.TierToken

let private secret = "whsec_test_32_byte_minimum_padding"

let private config: StripeConfig = {
    WebhookSecret = secret
    ApiKey = "sk_test_unused"
}

let private signHeader (now: DateTimeOffset) (body: string) : string =
    let timestamp = now.ToUnixTimeSeconds()
    let payload = sprintf "%d.%s" timestamp body
    use h = new HMACSHA256(Encoding.UTF8.GetBytes secret)

    let sigHex =
        Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes payload)).ToLowerInvariant()

    sprintf "t=%d,v1=%s" timestamp sigHex

let private makeServer (webApp: HttpHandler) : TestServer =
    let builder =
        (new WebHostBuilder())
            .ConfigureServices(fun services ->
                services.AddGiraffe() |> ignore
                services.AddLogging() |> ignore)
            .Configure(fun app -> app.UseGiraffe(POST >=> route "/webhook" >=> webApp))

    new TestServer(builder)

let private post (server: TestServer) (sigHeader: string) (body: string) : Task<HttpResponseMessage> = task {
    use client = server.CreateClient()
    use req = new HttpRequestMessage(HttpMethod.Post, "/webhook")
    req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
    req.Headers.TryAddWithoutValidation("Stripe-Signature", sigHeader) |> ignore
    return! client.SendAsync req
}

let private okHandler: VerifiedEvent -> HttpContext -> Task<Result<unit, string>> =
    fun _ _ -> task { return Ok() }

let private subscriptionBody =
    """{"id":"evt_sub","type":"customer.subscription.updated","data":{"object":{"id":"sub_1","customer":"cus_1","status":"active"}}}"""

let private customerBody =
    """{"id":"evt_cust","type":"customer.created","data":{"object":{"id":"cus_2"}}}"""

/// Worked example: a sink that re-stamps the tier cookie via
/// `ToolUp.Stripe.TierToken.Cookie.issue` — proving `Server` and
/// `TierToken` compose in consumer code with no direct dependency
/// between the two packages.
type CookieIssuingSink(calls: int ref) =
    let cookieConfig: CookieConfig = {
        CookieName = "tier"
        InsecureCookiesEnvVar = None
    }

    let signingKey = Encoding.UTF8.GetBytes "tier-cookie-secret-32-bytes-min!"

    interface ITierTokenSink with
        member _.OnBillingEvent (event: VerifiedEvent) (ctx: HttpContext) : Task<unit> = task {
            Interlocked.Increment calls |> ignore
            // Resolve the new tier from the event (toy mapping).
            let tier =
                match event.Event with
                | SubscriptionDeleted _ -> Tier.Free
                | _ -> Tier.Personal

            Cookie.issue cookieConfig ctx tier 3600 signingKey |> ignore
        }

[<Tests>]
let tests =
    testList "ITierTokenSink" [
        test "sink fires on a tier-changing event and re-stamps the cookie" {
            let calls = ref 0
            let sink = CookieIssuingSink(calls) :> ITierTokenSink

            let options = WebhookOptions.create () |> WebhookOptions.withTierTokenSink sink

            use server = makeServer (Routes.stripeWebhookWith options config okHandler)
            let header = signHeader DateTimeOffset.UtcNow subscriptionBody

            let resp = (post server header subscriptionBody).Result

            Expect.equal resp.StatusCode HttpStatusCode.OK "200"
            Expect.equal calls.Value 1 "sink fired once"

            let setCookie =
                let ok, values = resp.Headers.TryGetValues "Set-Cookie"
                if ok then String.Join(";", values) else ""

            Expect.stringContains setCookie "tier=" "tier cookie re-stamped via TierToken.Cookie"
        }
        test "sink is not invoked when none is supplied (Phase 140 behaviour)" {
            // No sink → no failure, no cookie. Uses the default entry point.
            use server = makeServer (Routes.stripeWebhook config okHandler)
            let header = signHeader DateTimeOffset.UtcNow subscriptionBody

            let resp = (post server header subscriptionBody).Result

            Expect.equal resp.StatusCode HttpStatusCode.OK "200"
            let ok, _ = resp.Headers.TryGetValues "Set-Cookie"
            Expect.isFalse ok "no cookie issued without a sink"
        }
        test "sink is not invoked on a non-tier-changing event" {
            let calls = ref 0
            let sink = CookieIssuingSink(calls) :> ITierTokenSink

            let options = WebhookOptions.create () |> WebhookOptions.withTierTokenSink sink

            use server = makeServer (Routes.stripeWebhookWith options config okHandler)
            let header = signHeader DateTimeOffset.UtcNow customerBody

            let resp = (post server header customerBody).Result

            Expect.equal resp.StatusCode HttpStatusCode.OK "200"
            Expect.equal calls.Value 0 "sink not fired on customer.created"
        }
        test "sink is not invoked on a verification failure" {
            let calls = ref 0
            let sink = CookieIssuingSink(calls) :> ITierTokenSink

            let options = WebhookOptions.create () |> WebhookOptions.withTierTokenSink sink

            use server = makeServer (Routes.stripeWebhookWith options config okHandler)
            // Sign one body, submit a different one → signature mismatch.
            let header = signHeader DateTimeOffset.UtcNow subscriptionBody
            let resp = (post server header customerBody).Result

            Expect.equal resp.StatusCode HttpStatusCode.BadRequest "400"
            Expect.equal calls.Value 0 "sink not fired on verification failure"
        }
        test "sink is not invoked when the handler returns Error" {
            let calls = ref 0
            let sink = CookieIssuingSink(calls) :> ITierTokenSink

            let options = WebhookOptions.create () |> WebhookOptions.withTierTokenSink sink

            let errHandler: VerifiedEvent -> HttpContext -> Task<Result<unit, string>> =
                fun _ _ -> task { return Error "boom" }

            use server = makeServer (Routes.stripeWebhookWith options config errHandler)
            let header = signHeader DateTimeOffset.UtcNow subscriptionBody

            let resp = (post server header subscriptionBody).Result

            Expect.equal resp.StatusCode HttpStatusCode.InternalServerError "500"
            Expect.equal calls.Value 0 "sink not fired when handler errors"
        }
    ]