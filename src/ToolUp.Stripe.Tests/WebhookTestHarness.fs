module ToolUp.Stripe.Server.Tests.WebhookTestHarness

// Shared webhook test harness — the signing, hosting and POST helpers used by
// more than one test file in this project. Lifted here (Phase 211) from
// `StripeWebhookHandlerTests`, where they were `let private` and so could not be
// reused by a second file; the alternative was a verbatim copy, and two copies of
// a signing helper drift apart exactly when a signature test matters most.

// FS0044: the WebHostBuilder-based TestServer ctor is deprecated in
// .NET 10 but remains the standard minimal Giraffe test-host pattern.
#nowarn "44"

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ToolUp.Stripe.Server

/// The signing secret every webhook test signs and verifies with. At least
/// 32 UTF-8 bytes, so `WebhookSigner`'s secret-strength gate admits it.
let secret = "whsec_test_32_byte_minimum_padding"

/// `StripeConfig` carrying `secret`. The API key is never exercised by the
/// webhook path — nothing in these tests makes an outbound Stripe call.
let config: StripeConfig = {
    WebhookSecret = secret
    ApiKey = "sk_test_unused"
}

/// Sign `body` at `now` and return the `Stripe-Signature` header value.
let signHeader (now: DateTimeOffset) (body: string) : string =
    let timestamp = now.ToUnixTimeSeconds()
    let payload = sprintf "%d.%s" timestamp body
    use h = new HMACSHA256(Encoding.UTF8.GetBytes secret)

    let sigHex =
        Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes payload)).ToLowerInvariant()

    sprintf "t=%d,v1=%s" timestamp sigHex

/// Spin up a Giraffe TestServer mounting `webApp` at POST /webhook.
let makeServer (webApp: HttpHandler) : TestServer =
    let builder =
        (new WebHostBuilder())
            .ConfigureServices(fun services ->
                services.AddGiraffe() |> ignore
                services.AddLogging() |> ignore)
            .Configure(fun app -> app.UseGiraffe(POST >=> route "/webhook" >=> webApp))

    new TestServer(builder)

/// POST a body + signature header and return the response.
let post (server: TestServer) (sigHeader: string) (body: string) : Task<HttpResponseMessage> = task {
    use client = server.CreateClient()
    use req = new HttpRequestMessage(HttpMethod.Post, "/webhook")
    req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
    req.Headers.TryAddWithoutValidation("Stripe-Signature", sigHeader) |> ignore
    return! client.SendAsync req
}