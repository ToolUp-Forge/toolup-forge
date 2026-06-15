namespace ToolUp.Stripe.Server

open System
open System.Net.Http
open System.Threading.Tasks

/// Customer-Portal session opener — the "open billing portal" button.
/// POSTs to `/v1/billing_portal/sessions` and returns the portal URL the
/// consumer redirects the user to. BCL `HttpClient` only.
module CustomerPortal =
    /// Open a Customer-Portal session for `customerId`, returning to
    /// `returnUrl` when the user leaves the portal. `client` is injected
    /// for testing; the public `openSessionLink` uses the shared client.
    let openSessionLinkWith
        (client: HttpClient)
        (customerId: string)
        (returnUrl: string)
        (apiKey: string)
        : Task<Result<Uri, string>> =
        task {
            let! result =
                StripeHttp.postForm client apiKey "/v1/billing_portal/sessions" [
                    "customer", customerId
                    "return_url", returnUrl
                ]

            return result |> Result.bind (fun root -> StripeHttp.readUri root "url")
        }

    /// Open a Customer-Portal session using the process-shared
    /// `HttpClient`.
    let openSessionLink (customerId: string) (returnUrl: string) (apiKey: string) : Task<Result<Uri, string>> =
        openSessionLinkWith (StripeHttp.shared ()) customerId returnUrl apiKey