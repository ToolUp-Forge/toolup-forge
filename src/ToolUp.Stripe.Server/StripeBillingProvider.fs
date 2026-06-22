namespace ToolUp.Stripe.Server

open System
open System.Net.Http
open System.Text.Json
open ToolUp.Platform.Payments

/// Direct-billing `IPaymentProvider` over Stripe — the Phase 240
/// merchant-model-neutral impl. Bills on the deployment's own Stripe
/// account (no Connect account, no application fee); the Connect-shaped
/// commercial flow stays a separate concern. Delegates to the existing
/// `Checkout` / `CustomerPortal` wrappers + a subscriptions GET. BCL
/// `HttpClient` only — no `Stripe.net` (GP 1). `httpClient` is injected
/// for testing; the default uses the process-shared client.
type StripeBillingProvider(config: StripeConfig, ?httpClient: HttpClient) =
    let client = defaultArg httpClient (StripeHttp.shared ())

    let toCheckoutCustomer (req: PaymentCheckoutRequest) : Result<CheckoutCustomer, PaymentError> =
        match req.CustomerId, req.CustomerEmail with
        | Some id, _ -> Ok(CheckoutCustomer.CustomerId id)
        | None, Some email -> Ok(CheckoutCustomer.CustomerEmail email)
        | None, None -> Error(ProviderError "checkout requires CustomerId or CustomerEmail")

    /// Read `items.data[0].price.id` from a Stripe subscription object.
    let planIdOf (sub: JsonElement) : string =
        match sub.TryGetProperty "items" with
        | true, items ->
            match items.TryGetProperty "data" with
            | true, data when data.ValueKind = JsonValueKind.Array && data.GetArrayLength() > 0 ->
                match data.[0].TryGetProperty "price" with
                | true, price ->
                    match price.TryGetProperty "id" with
                    | true, id when id.ValueKind = JsonValueKind.String -> id.GetString()
                    | _ -> ""
                | _ -> ""
            | _ -> ""
        | _ -> ""

    /// Map the first subscription in a `/v1/subscriptions` list response
    /// to the neutral `SubscriptionStatus`.
    let parseStatus (root: JsonElement) : SubscriptionStatus =
        match root.TryGetProperty "data" with
        | true, data when data.ValueKind = JsonValueKind.Array && data.GetArrayLength() > 0 ->
            let sub = data.[0]

            let status =
                match sub.TryGetProperty "status" with
                | true, s when s.ValueKind = JsonValueKind.String -> s.GetString()
                | _ -> ""

            match status with
            | "active"
            | "trialing" -> ActiveSubscription(planIdOf sub)
            | "past_due"
            | "unpaid" -> PastDue
            | "canceled"
            | "incomplete_expired" -> Canceled
            | _ -> NoSubscription
        | _ -> NoSubscription

    interface IPaymentProvider with
        member _.CreateCheckoutSession(req) = async {
            match toCheckoutCustomer req with
            | Error e -> return Error e
            | Ok customer ->
                let checkoutReq: CheckoutRequest = {
                    PriceId = req.PriceId
                    Quantity = req.Quantity
                    Mode = CheckoutMode.Subscription
                    SuccessUrl = req.SuccessUrl
                    CancelUrl = req.CancelUrl
                    Customer = customer
                }

                let! r = Checkout.createSessionWith client checkoutReq config.ApiKey |> Async.AwaitTask

                return
                    r
                    |> Result.mapError ProviderError
                    |> Result.map (fun uri -> ({ Url = uri.ToString() }: CheckoutSession))
        }

        member _.CreatePortalSession(customerId, returnUrl) = async {
            let! r =
                CustomerPortal.openSessionLinkWith client customerId returnUrl config.ApiKey
                |> Async.AwaitTask

            return
                r
                |> Result.mapError ProviderError
                |> Result.map (fun uri -> ({ Url = uri.ToString() }: PortalSession))
        }

        member _.GetSubscriptionStatus(customerId) = async {
            let path =
                sprintf "/v1/subscriptions?customer=%s&status=all&limit=1" (Uri.EscapeDataString customerId)

            let! r = StripeHttp.getJson client config.ApiKey path |> Async.AwaitTask
            return r |> Result.mapError ProviderError |> Result.map parseStatus
        }