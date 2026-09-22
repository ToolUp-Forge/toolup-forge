// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Stripe.Server.Tests.ReconciliationContractTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Payments
open ToolUp.Stripe.Server

// ─── Phase 210 — billing reconciliation contract pack ────────────────
//
// The provider read is stubbed at the `HttpMessageHandler` seam
// (`StripeHttpWrapperTests` / `StripeBillingProviderTests` precedent) rather
// than by faking `IPaymentProvider`, so the pack exercises the SHIPPED
// `StripeBillingProvider.GetSubscriptionStatus` parse as well as the diff.
// A fake provider would have let a mis-parsed `/v1/subscriptions` response
// pass as in-sync, which is exactly the failure this phase exists to catch.

let private apiKey = "sk_test_RECONCILE_SECRET_do_not_leak"

let private config: StripeConfig = {
    WebhookSecret = "whsec_reconcile_32_byte_minimum_pad"
    ApiKey = apiKey
}

/// Stub `HttpMessageHandler` that answers per request, so one client can
/// serve several customers in a single sweep. Captures every request URI.
type private StubHandler(responder: HttpRequestMessage -> HttpResponseMessage) =
    inherit HttpMessageHandler()
    let uris = ResizeArray<string>()

    /// Every request URI this handler answered, in order.
    member _.Uris = uris

    override _.SendAsync(request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
        uris.Add(request.RequestUri.ToString())
        Task.FromResult(responder request)

let private json (status: HttpStatusCode) (body: string) =
    let resp = new HttpResponseMessage(status)
    resp.Content <- new StringContent(body, Encoding.UTF8, "application/json")
    resp

/// A `/v1/subscriptions` list response carrying one subscription.
let private subscriptionBody (stripeStatus: string) (planId: string) =
    sprintf
        """{"object":"list","data":[{"id":"sub_1","status":"%s","items":{"data":[{"price":{"id":"%s"}}]}}]}"""
        stripeStatus
        planId

/// A `/v1/subscriptions` list response carrying no subscription at all.
let private noSubscriptionBody = """{"object":"list","data":[]}"""

/// Provider whose every read answers `body`.
let private providerReturning (body: string) : IPaymentProvider * StubHandler =
    let handler = new StubHandler(fun _ -> json HttpStatusCode.OK body)
    StripeBillingProvider(config, new HttpClient(handler)) :> IPaymentProvider, handler

/// Provider that answers per customer id, matched out of the request URI.
let private providerPerCustomer (bodies: (string * string) list) : IPaymentProvider * StubHandler =
    let handler =
        new StubHandler(fun request ->
            let uri = request.RequestUri.ToString()

            match
                bodies
                |> List.tryFind (fun (customerId, _) -> uri.Contains("customer=" + customerId))
            with
            | Some(_, body) -> json HttpStatusCode.OK body
            | None -> json HttpStatusCode.NotFound """{"error":{"message":"No such customer"}}""")

    StripeBillingProvider(config, new HttpClient(handler)) :> IPaymentProvider, handler

/// Recording divergence sink — the assertion subject of most of this pack.
type private RecordingSink() =
    let seen = ResizeArray<ReconcileOutcome>()
    member _.Seen = List.ofSeq seen

    interface IBillingDivergenceSink with
        member _.OnDivergence(outcome) = async { seen.Add outcome }

/// Sink that throws, to prove one customer's sink fault does not abandon
/// the sweep.
type private ThrowingSink() =
    interface IBillingDivergenceSink with
        member _.OnDivergence(_outcome) = async { failwith "sink exploded" }

let private readers (customers: string list) (local: string -> SubscriptionStatus option) : ReconcileReaders = {
    EnumerateCustomers = fun () -> async { return customers }
    ReadLocalStatus = fun customerId -> async { return local customerId }
}

/// Frozen clock — the hourly idempotency bucket is asserted, never raced
/// against a real hour boundary.
let private frozen = DateTime(2026, 9, 23, 11, 30, 0, DateTimeKind.Utc)

let private reconcilerWith
    (provider: IPaymentProvider)
    (rs: ReconcileReaders)
    (sink: IBillingDivergenceSink)
    (store: IWebhookIdempotencyStore)
    : IBillingReconciler =
    BillingReconciler(provider, rs, sink, store, (fun () -> frozen)) :> IBillingReconciler

/// The one-customer case every classification test wants.
let private reconcileOne
    (body: string)
    (local: SubscriptionStatus option)
    : ReconcileOutcome * ReconcileOutcome list * StubHandler =
    let provider, handler = providerReturning body
    let sink = RecordingSink()
    let store = InMemoryIdempotencyStore() :> IWebhookIdempotencyStore

    let reconciler =
        reconcilerWith provider (readers [ "cus_1" ] (fun _ -> local)) sink store

    let outcome = reconciler.ReconcileCustomer "cus_1" |> Async.RunSynchronously
    outcome, sink.Seen, handler

[<Tests>]
let tests =
    testList "Billing reconciliation (Phase 210)" [

        // ── The five outcomes ──

        test "in-sync customer produces InSync and never calls the sink" {
            let outcome, seen, handler =
                reconcileOne (subscriptionBody "active" "price_pro") (Some(ActiveSubscription "price_pro"))

            match outcome with
            | InSync("cus_1", ActiveSubscription "price_pro") -> ()
            | other -> failtestf "expected InSync active/price_pro, got %A" other

            Expect.isEmpty seen "an in-sync customer must produce no sink call"
            Expect.stringContains handler.Uris[0] "/v1/subscriptions" "reads through GetSubscriptionStatus"
            Expect.stringContains handler.Uris[0] "customer=cus_1" "for the named customer"
        }

        test "local entitlement the provider does not back is LocalAhead" {
            let outcome, seen, _ =
                reconcileOne noSubscriptionBody (Some(ActiveSubscription "price_pro"))

            match outcome with
            | LocalAhead("cus_1", ActiveSubscription "price_pro", NoSubscription) -> ()
            | other -> failtestf "expected LocalAhead, got %A" other

            Expect.equal (List.length seen) 1 "a genuine divergence calls the sink exactly once"
            Expect.equal seen[0] outcome "the sink receives the outcome it was classified as"
        }

        test "provider entitlement the local view has not caught up with is StripeAhead" {
            let outcome, seen, _ = reconcileOne (subscriptionBody "active" "price_pro") None

            match outcome with
            | StripeAhead("cus_1", None, ActiveSubscription "price_pro") -> ()
            | other -> failtestf "expected StripeAhead, got %A" other

            Expect.equal (List.length seen) 1 "a genuine divergence calls the sink exactly once"
        }

        test "a plan change at the same entitlement strength is StripeAhead" {
            // Same rank on both sides — the provider is authoritative about
            // WHICH plan, so this must not read as in-sync.
            let outcome, seen, _ =
                reconcileOne (subscriptionBody "active" "price_pro") (Some(ActiveSubscription "price_basic"))

            match outcome with
            | StripeAhead("cus_1", Some(ActiveSubscription "price_basic"), ActiveSubscription "price_pro") -> ()
            | other -> failtestf "expected StripeAhead on a plan change, got %A" other

            Expect.equal (List.length seen) 1 "a plan change is a divergence"
        }

        test "provider-side cancellation against a live local tier is StripeCancelled" {
            let outcome, seen, _ =
                reconcileOne (subscriptionBody "canceled" "price_pro") (Some(ActiveSubscription "price_pro"))

            match outcome with
            | StripeCancelled("cus_1", ActiveSubscription "price_pro") -> ()
            | other -> failtestf "expected StripeCancelled, got %A" other

            Expect.equal (List.length seen) 1 "a missed cancellation is a divergence"
        }

        test "a provider read failure is Unreadable and calls no sink" {
            let handler =
                new StubHandler(fun _ -> json HttpStatusCode.InternalServerError """{"error":{"message":"api down"}}""")

            let provider =
                StripeBillingProvider(config, new HttpClient(handler)) :> IPaymentProvider

            let sink = RecordingSink()

            let reconciler =
                reconcilerWith
                    provider
                    (readers [ "cus_1" ] (fun _ -> Some(ActiveSubscription "price_pro")))
                    sink
                    (InMemoryIdempotencyStore() :> IWebhookIdempotencyStore)

            let outcome = reconciler.ReconcileCustomer "cus_1" |> Async.RunSynchronously

            match outcome with
            | Unreadable("cus_1", reason) ->
                Expect.isFalse (reason.Contains apiKey) "the reason must never carry the API key"
            | other -> failtestf "expected Unreadable, got %A" other

            Expect.isEmpty sink.Seen "an unreadable customer is not a divergence to correct"
        }

        test "a throwing local reader is Unreadable, not an escaping exception" {
            let provider, _ = providerReturning (subscriptionBody "active" "price_pro")
            let sink = RecordingSink()

            let rs: ReconcileReaders = {
                EnumerateCustomers = fun () -> async { return [ "cus_1" ] }
                ReadLocalStatus = fun _ -> async { return failwith "local store unreachable" }
            }

            let reconciler =
                reconcilerWith provider rs sink (InMemoryIdempotencyStore() :> IWebhookIdempotencyStore)

            let outcome = reconciler.ReconcileCustomer "cus_1" |> Async.RunSynchronously

            match outcome with
            | Unreadable("cus_1", reason) -> Expect.stringContains reason "local store unreachable" "reason"
            | other -> failtestf "expected Unreadable, got %A" other
        }

        // ── Entitlement, not raw status ──

        test "no local entitlement against a cancelled provider subscription is in sync" {
            // Both grant nothing. Classifying this as a divergence would
            // hand the sink a correction with nothing to correct, once an
            // hour, for every customer who ever cancelled.
            let outcome, seen, _ = reconcileOne (subscriptionBody "canceled" "price_pro") None

            match outcome with
            | InSync("cus_1", Canceled) -> ()
            | other -> failtestf "expected InSync, got %A" other

            Expect.isEmpty seen "nothing grants anything on either side"
        }

        // ── Idempotency ──

        test "a second run in the same hour bucket does not re-apply the correction" {
            let provider, _ = providerReturning noSubscriptionBody
            let sink = RecordingSink()
            let store = InMemoryIdempotencyStore() :> IWebhookIdempotencyStore

            let reconciler =
                reconcilerWith provider (readers [ "cus_1" ] (fun _ -> Some(ActiveSubscription "price_pro"))) sink store

            let first = reconciler.ReconcileCustomer "cus_1" |> Async.RunSynchronously
            let second = reconciler.ReconcileCustomer "cus_1" |> Async.RunSynchronously

            Expect.equal (List.length sink.Seen) 1 "the sink fires once per customer per status per bucket"

            // The classification is still returned truthfully on the second
            // pass — the caller asked what the state IS, not what was done.
            Expect.equal second first "the second run reports the same divergence"
        }

        test "the claim key is recon-prefixed, per customer, per status, per UTC hour" {
            let key =
                Reconciliation.idempotencyKey "cus_1" (ActiveSubscription "price_pro") frozen

            Expect.equal key "recon:cus_1:active:price_pro:2026092311" "documented key convention"

            // A different hour is a different claim — that is what lets a
            // standing divergence be re-applied rather than applied once.
            let nextHour =
                Reconciliation.idempotencyKey "cus_1" (ActiveSubscription "price_pro") (frozen.AddHours 1.0)

            Expect.notEqual key nextHour "the hour bucket must move the key"

            // ...and it can never collide with a real Stripe event id.
            Expect.isTrue (key.StartsWith "recon:") "synthesised keys stay out of Stripe's evt_ id space"
        }

        // ── The sweep ──

        test "ReconcileAll walks every customer and reports one outcome each" {
            let provider, handler =
                providerPerCustomer [
                    "cus_sync", subscriptionBody "active" "price_pro"
                    "cus_ahead", noSubscriptionBody
                    "cus_gone", subscriptionBody "canceled" "price_pro"
                ]

            let sink = RecordingSink()

            // Every one of them is locally `price_pro`; only the provider
            // side differs, which is what makes the three classifications.
            let reconciler =
                reconcilerWith
                    provider
                    (readers [ "cus_sync"; "cus_ahead"; "cus_gone" ] (fun _ -> Some(ActiveSubscription "price_pro")))
                    sink
                    (InMemoryIdempotencyStore() :> IWebhookIdempotencyStore)

            let outcomes = reconciler.ReconcileAll() |> Async.RunSynchronously

            Expect.equal (List.length outcomes) 3 "one outcome per enumerated customer"
            Expect.equal handler.Uris.Count 3 "one provider read per customer"

            Expect.equal
                (outcomes |> List.filter Reconciliation.isDivergence |> List.length)
                2
                "only the two drifted customers are divergences"

            Expect.equal (List.length sink.Seen) 2 "and only they reach the sink"
        }

        test "one customer's sink fault does not abandon the rest of the sweep" {
            let provider, _ = providerReturning noSubscriptionBody

            let reconciler =
                reconcilerWith
                    provider
                    (readers [ "cus_1"; "cus_2" ] (fun _ -> Some(ActiveSubscription "price_pro")))
                    (ThrowingSink())
                    (InMemoryIdempotencyStore() :> IWebhookIdempotencyStore)

            let outcomes = reconciler.ReconcileAll() |> Async.RunSynchronously

            Expect.equal (List.length outcomes) 2 "the sweep completes"

            Expect.all
                outcomes
                (fun o ->
                    match o with
                    | Unreadable _ -> true
                    | _ -> false)
                "a throwing sink is reported per customer, never raised out of the sweep"
        }

        test "a failing enumerator raises rather than reporting an empty, clean sweep" {
            let provider, _ = providerReturning noSubscriptionBody

            let rs: ReconcileReaders = {
                EnumerateCustomers = fun () -> async { return failwith "customer index unavailable" }
                ReadLocalStatus = fun _ -> async { return None }
            }

            let reconciler =
                reconcilerWith provider rs (RecordingSink()) (InMemoryIdempotencyStore() :> IWebhookIdempotencyStore)

            Expect.throws
                (fun () -> reconciler.ReconcileAll() |> Async.RunSynchronously |> ignore)
                "an empty sweep would read as 'no drift found'"
        }

        // ── The job registration (GP 13) ──

        test "jobRegistration is a cron registration nothing schedules on its own" {
            let reg =
                Reconciliation.jobRegistration "tenant-a" "compose" Reconciliation.DefaultCronExpression

            Expect.equal reg.Handler Reconciliation.HandlerName "stable handler name"
            Expect.equal reg.Trigger (CronTrigger "0 * * * *") "hourly by default, matching the claim bucket"
            Expect.equal reg.ScopeId "tenant-a" "registered under the caller's scope"
            Expect.equal reg.Precision Minute "the in-process scheduler's declared floor"
            Expect.equal reg.Payload "" "the sweep takes its subjects from the enumerator"

            match reg.Idempotency with
            | Some key -> Expect.equal key.Key "_stripe.billing.reconcile:tenant-a" "re-compose reuses the job"
            | None -> failtest "the registration must be idempotent by (handler, scope)"
        }

        test "the audit-only default sink records and applies nothing" {
            let lines = ResizeArray<string>()

            let logger =
                { new ILogger with
                    member _.Debug(_) = ()
                    member _.Info(_) = ()
                    member _.Warn(message) = lines.Add message
                    member _.Error(_, _) = ()
                }

            let sink = AuditOnlyDivergenceSink(logger) :> IBillingDivergenceSink

            sink.OnDivergence(LocalAhead("cus_1", ActiveSubscription "price_pro", NoSubscription))
            |> Async.RunSynchronously

            Expect.equal lines.Count 1 "one audit line per divergence"
            Expect.stringContains lines[0] "cus_1" "naming the customer"
            Expect.stringContains lines[0] "local-ahead" "and the direction of the drift"
        }
    ]