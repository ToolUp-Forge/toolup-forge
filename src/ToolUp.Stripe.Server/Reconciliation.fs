// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Stripe.Server

open System
open ToolUp.Platform
open ToolUp.Platform.Payments

// ─── Phase 210 — billing reconciliation ──────────────────────────────
//
// Webhooks get missed. A deployment is down for a minute, an idempotency
// store drops a claim, a signing secret rotates mid-flight — and the local
// entitlement view silently stops matching what the payment provider
// believes. Nothing in the webhook path re-derives the truth, because the
// webhook path only ever hears about events it actually received.
//
// The reconciler closes that gap by reading the authoritative subscription
// state back out of the provider on a schedule and comparing it with the
// consumer's local view. It is the difference between a billing demo and a
// billing system you would trust with revenue.
//
// ── What this package owns, and what it does not (GP 1) ──
// The SDK owns the MECHANISM: the read, the diff, the classification, the
// idempotency key, the job registration shape. It owns neither half of the
// domain data the diff needs, and does not pretend to:
//
//   * the user↔customer map — which of the deployment's users a provider
//     customer id belongs to — is consumer domain logic; and
//   * the local entitlement view itself, because there is no local
//     entitlement STORE in this package to diff against. The local tier is
//     a signed browser cookie (`ToolUp.Stripe.TierToken.Cookie`) or an
//     offline capability token — neither of which is enumerable from a
//     background thread with no request in hand.
//
// So the consumer supplies BOTH readers (`ReconcileReaders`) and the sink
// that applies the correction (`IBillingDivergenceSink`).
//
// ── Why not `ITierTokenSink` ──
// `ITierTokenSink.OnBillingEvent` takes an `HttpContext` — it resolves WHICH
// user the event maps to out of the request. A cron tick has no request, and
// a synthesised `DefaultHttpContext` would defeat the seam rather than use
// it (the consumer would be handed a context with no cookies, no user and no
// connection, and would have to detect that). `IBillingDivergenceSink` is the
// `HttpContext`-free counterpart; the reconciler never calls `ITierTokenSink`.
//
// ── Provider neutrality — a note for whoever moves this ──
// Nothing below is Stripe-shaped: the read goes through `IPaymentProvider`
// and the diff is over the neutral `SubscriptionStatus`. `IBillingReconciler`
// lives in this package only because Stripe is today's sole `IPaymentProvider`
// implementation, and a substrate interface with exactly one provider is a
// generalisation nobody has tested. When a second provider lands, this belongs
// in the platform core beside `IPaymentProvider` — the move is a namespace
// change plus a type-forward, and the only Stripe-specific thing that would
// stay behind is the `IWebhookIdempotencyStore` dependency.
//
// ── GP 13 — zero cost when unregistered ──
// Nothing here is constructed, scheduled or touched unless a consumer builds
// a `BillingReconciler` and registers `Reconciliation.jobRegistration` with
// its scheduler. A deployment that does not is byte-for-byte unchanged.

/// The result of reconciling ONE customer's local entitlement view against
/// the payment provider's authoritative subscription state.
///
/// Every case carries the customer id, because the divergence sink's whole
/// job is to apply a correction to a particular customer and it is handed
/// nothing else.
///
/// The comparison is over ENTITLEMENT, not over the raw `SubscriptionStatus`
/// value: `NoSubscription` and `Canceled` both mean "grants nothing", so a
/// local view holding neither against a provider reporting the other is
/// `InSync` and produces no sink call. That is also where the webhook path's
/// "skip non-tier-changing events" filter goes: reading subscription state
/// directly has no non-tier-bearing traffic to skip in the first place.
type ReconcileOutcome =
    /// Local and provider agree on what the customer is entitled to.
    /// No correction is needed and the divergence sink is not called.
    | InSync of customerId: string * status: SubscriptionStatus
    /// The local view grants MORE than the provider backs — an entitlement
    /// the customer is no longer paying for. The revenue-leak direction.
    | LocalAhead of customerId: string * local: SubscriptionStatus * remote: SubscriptionStatus
    /// The provider grants entitlement the local view does not reflect —
    /// a missed activation, or a plan change the local view never heard
    /// about. The customer-is-owed direction.
    | StripeAhead of customerId: string * local: SubscriptionStatus option * remote: SubscriptionStatus
    /// The provider reports the subscription cancelled while the local view
    /// still grants something — a missed cancellation.
    | StripeCancelled of customerId: string * local: SubscriptionStatus
    /// The customer's state could not be established: the provider call
    /// failed, or one of the consumer's readers threw. Carries the
    /// sanitised reason; never any secret material. NOT a divergence — the
    /// sink is not called, because there is no correction to apply to a
    /// state nobody could read. Retrying is the scheduler's job.
    | Unreadable of customerId: string * reason: string

/// The two consumer-supplied halves of the diff (GP 1). Functions rather
/// than interfaces: each is one operation, and a consumer typically closes
/// over its own store or ORM context to supply them.
type ReconcileReaders = {
    /// Every provider customer id the deployment wants reconciled. Called
    /// once per `ReconcileAll` sweep. A deployment with more customers than
    /// fit comfortably in one tick supplies a partitioned enumerator and
    /// registers several jobs rather than returning everything.
    EnumerateCustomers: unit -> Async<string list>
    /// The deployment's own view of a customer's entitlement, expressed in
    /// the neutral `SubscriptionStatus` vocabulary. `None` means "this
    /// deployment holds no entitlement for that customer" — which is
    /// equivalent to, and compares equal with, `NoSubscription`.
    ReadLocalStatus: string -> Async<SubscriptionStatus option>
}

/// Seam through which a reconcile hands a divergence back to the consumer
/// to be acted on — mint a fresh entitlement token, revoke a stale one,
/// notify the customer, write an audit row.
///
/// `HttpContext`-free by construction: it is called from a background job
/// with no request in flight, which is precisely why the request-scoped
/// `ITierTokenSink` cannot serve this path.
///
/// **Called only on genuine divergence.** `InSync` never reaches it, and
/// neither does `Unreadable`. It is also called at most once per customer,
/// per provider status, per UTC hour — see `Reconciliation.idempotencyKey`.
///
/// Implementations must be thread-safe and should not throw: an exception
/// here is reported as `Unreadable` for that customer, because the
/// reconciler cannot tell a sink fault from a read fault and will not
/// abandon the rest of the sweep for either.
type IBillingDivergenceSink =
    /// Apply (or record) the correction for one diverged customer.
    abstract OnDivergence: outcome: ReconcileOutcome -> Async<unit>

/// Periodic reconcile of local entitlement state against the payment
/// provider's authoritative view. See the file header for what this owns
/// and what the consumer owns.
type IBillingReconciler =
    /// Reconcile one customer: read the provider, diff against the local
    /// reader, classify, and — on a genuine divergence that wins its
    /// idempotency claim — call the divergence sink before returning.
    abstract ReconcileCustomer: customerId: string -> Async<ReconcileOutcome>
    /// Reconcile every customer the enumerator yields, in order, one at a
    /// time. One customer's failure is captured as `Unreadable` and does
    /// not abandon the sweep.
    abstract ReconcileAll: unit -> Async<ReconcileOutcome list>

/// The reconcile mechanism the SDK owns: the pure classification rule, the
/// idempotency-key convention, the audit rendering, and the cron
/// `JobRegistration` a consumer schedules. Nothing here holds state or
/// reaches the network — `BillingReconciler` is what binds it to a provider
/// and the consumer's readers.
module Reconciliation =

    /// Handler name the scheduler dispatches the reconcile under. Stable —
    /// it is persisted verbatim in every `JobRegistration.Handler` this
    /// module writes, so changing it would orphan already-scheduled jobs.
    [<Literal>]
    let HandlerName = "_stripe.billing.reconcile"

    /// Default cadence: the top of every hour.
    ///
    /// Deliberately matched to the hourly idempotency bucket below. A
    /// consumer may register a finer cadence — the reconcile itself is
    /// cheap and safe to run more often — but a correction is applied at
    /// most once per customer per provider status per UTC hour, so the
    /// extra ticks classify and report without re-applying.
    [<Literal>]
    let DefaultCronExpression = "0 * * * *"

    /// Stable one-token rendering of a provider status, for the
    /// idempotency key and for audit lines. Value-only (GP 12 rule 1).
    let statusToken (status: SubscriptionStatus) : string =
        match status with
        | NoSubscription -> "none"
        | ActiveSubscription planId -> "active:" + planId
        | PastDue -> "past_due"
        | Canceled -> "canceled"

    /// The idempotency key a reconcile-sourced correction claims through
    /// `IWebhookIdempotencyStore.TryClaim`: `recon:<customerId>:<status>:<yyyyMMddHH>`.
    ///
    /// **Why a convention rather than an event id.** `TryClaim` is keyed by
    /// the provider's event id, and a reconcile has none — the state was
    /// read, not delivered. The `recon:` prefix keeps a synthesised key out
    /// of the provider's `evt_…` id space, so a reconcile correction can
    /// never consume the claim of a real webhook event (or vice versa).
    ///
    /// **Why the hour bucket.** Without one, a key is either unique per
    /// tick — which claims nothing — or eternal, which would apply a
    /// correction once and never again however long the divergence
    /// persisted. Bucketing by UTC hour means a standing divergence is
    /// re-applied hourly while a single tick storm is applied once.
    ///
    /// `utcNow` is passed rather than read so the bucket is testable at an
    /// hour boundary instead of racing one.
    let idempotencyKey (customerId: string) (status: SubscriptionStatus) (utcNow: DateTime) : string =
        sprintf "recon:%s:%s:%s" customerId (statusToken status) (utcNow.ToString "yyyyMMddHH")

    /// One-line, secret-free rendering of an outcome, for audit and logs.
    let describe (outcome: ReconcileOutcome) : string =
        match outcome with
        | InSync(customerId, status) -> sprintf "in-sync %s (%s)" customerId (statusToken status)
        | LocalAhead(customerId, local, remote) ->
            sprintf "local-ahead %s: local %s, provider %s" customerId (statusToken local) (statusToken remote)
        | StripeAhead(customerId, local, remote) ->
            let localToken =
                match local with
                | Some s -> statusToken s
                | None -> "absent"

            sprintf "provider-ahead %s: local %s, provider %s" customerId localToken (statusToken remote)
        | StripeCancelled(customerId, local) ->
            sprintf "provider-cancelled %s: local %s still grants entitlement" customerId (statusToken local)
        | Unreadable(customerId, reason) -> sprintf "unreadable %s: %s" customerId reason

    /// Whether an outcome is a divergence the sink should be told about.
    /// `InSync` and `Unreadable` are not.
    let isDivergence (outcome: ReconcileOutcome) : bool =
        match outcome with
        | LocalAhead _
        | StripeAhead _
        | StripeCancelled _ -> true
        | InSync _
        | Unreadable _ -> false

    /// Entitlement strength: what a status actually GRANTS, paired with the
    /// plan it grants it on. `NoSubscription` and `Canceled` both grant
    /// nothing and so compare equal; a plan change compares unequal at the
    /// same strength, and classifies as the provider being ahead.
    let private entitlementOf (status: SubscriptionStatus option) : int * string =
        match status with
        | None
        | Some NoSubscription
        | Some Canceled -> 0, ""
        | Some PastDue -> 1, ""
        | Some(ActiveSubscription planId) -> 2, planId

    /// Classify a local view against the provider's authoritative status.
    /// Pure — the whole diff rule in one place, so the contract pack can
    /// exercise it without an `HttpClient`.
    let classify
        (customerId: string)
        (local: SubscriptionStatus option)
        (remote: SubscriptionStatus)
        : ReconcileOutcome =
        let localRank, localPlan = entitlementOf local
        let remoteRank, remotePlan = entitlementOf (Some remote)

        if localRank = remoteRank && localPlan = remotePlan then
            InSync(customerId, remote)
        else
            match remote, local with
            | Canceled, Some l -> StripeCancelled(customerId, l)
            | _ when localRank > remoteRank ->
                // `local` cannot be `None` here: `None` ranks 0, the floor.
                LocalAhead(customerId, Option.defaultValue NoSubscription local, remote)
            | _ -> StripeAhead(customerId, local, remote)

    /// Build the `JobRegistration` that runs a reconcile sweep on a cron.
    ///
    /// **Disabled unless registered (GP 13).** This returns a value; nothing
    /// is scheduled until the consumer passes it to `IJobScheduler.Schedule`
    /// and registers a handler under `HandlerName` that calls
    /// `IBillingReconciler.ReconcileAll`. The handler stays consumer-side
    /// because `IJobHandler` lives in the platform server layer, which this
    /// billing companion deliberately does not depend on — see the migration
    /// note for the ten lines it takes.
    ///
    /// Idempotent by construction: the registration's own `IdempotencyKey`
    /// is `(handler, scope)`, so re-running a compose step reuses the
    /// existing job instead of accumulating a second cron for the same scope.
    ///
    /// `Payload` is empty: the sweep takes its subjects from the reconciler's
    /// enumerator, so a dispatch needs nothing carried on it.
    let jobRegistration (scopeId: string) (createdBy: string) (cron: string) : JobRegistration = {
        ScopeId = scopeId
        Handler = HandlerName
        Payload = ""
        Trigger = CronTrigger cron
        Idempotency =
            Some {
                Key = sprintf "%s:%s" HandlerName scopeId
                TtlSeconds = 365 * 24 * 60 * 60
            }
        RetryPolicy = JobRetryPolicy.defaults
        // No affinity: one sweep job per scope, and the reconcile promises
        // no ordering across customers (GP 12 rule 5).
        ShardKey = None
        // The in-process scheduler's floor, declared rather than assumed
        // (GP 12 rule 6). An hourly billing sweep needs nothing finer.
        Precision = Minute
        CreatedBy = createdBy
        Tags = Map [ "substrate", "stripe-billing" ]
    }

/// Audit-only `IBillingDivergenceSink` — the registered default.
///
/// Records every divergence at warning level and applies nothing. It exists
/// so a deployment can turn the reconcile on and SEE its drift before it
/// commits to correcting it automatically, which is the order those two
/// decisions want to be taken in. A deployment that wants the correction
/// applied supplies its own sink.
type AuditOnlyDivergenceSink
    /// Build the audit-only sink over the deployment's logger.
    (logger: ILogger) =
    interface IBillingDivergenceSink with
        member _.OnDivergence(outcome: ReconcileOutcome) : Async<unit> = async {
            logger.Warn("[billing-reconcile] " + Reconciliation.describe outcome)
        }

/// `IBillingReconciler` over `IPaymentProvider` (Phase 240's
/// `StripeBillingProvider` today) plus the consumer's two readers.
///
/// `utcNow` is injected so the hourly idempotency bucket is testable;
/// it defaults to `DateTime.UtcNow` and must return UTC.
type BillingReconciler
    /// Build a reconciler over a payment provider, the consumer's two
    /// readers, the divergence sink and the idempotency store.
    (
        provider: IPaymentProvider,
        readers: ReconcileReaders,
        sink: IBillingDivergenceSink,
        idempotency: IWebhookIdempotencyStore,
        ?utcNow: unit -> DateTime
    ) =

    let now = defaultArg utcNow (fun () -> DateTime.UtcNow)

    /// Provider errors render without ever carrying credential material —
    /// `PaymentError.ProviderError` already holds the provider's sanitised
    /// text (the HTTP wrapper strips the key before it gets here).
    let describeError (err: PaymentError) =
        match err with
        | ProviderNotConfigured -> "no payment provider is configured"
        | ProviderError message -> message

    let reconcileOne (customerId: string) : Async<ReconcileOutcome> = async {
        try
            let! remote = provider.GetSubscriptionStatus customerId

            match remote with
            | Error err -> return Unreadable(customerId, describeError err)
            | Ok remoteStatus ->
                let! local = readers.ReadLocalStatus customerId
                let outcome = Reconciliation.classify customerId local remoteStatus

                if Reconciliation.isDivergence outcome then
                    let key = Reconciliation.idempotencyKey customerId remoteStatus (now ())
                    let! claimed = idempotency.TryClaim key

                    // A lost claim means this divergence has already been
                    // handed to the sink in this bucket. The CLASSIFICATION
                    // is still returned truthfully — the caller asked what
                    // the state is, not what was done about it.
                    if claimed then
                        do! sink.OnDivergence outcome

                return outcome
        with ex ->
            // A throwing reader, a throwing sink, a transport fault the
            // provider did not turn into a `PaymentError`: one customer's
            // failure is reported, never allowed to abandon the sweep.
            return Unreadable(customerId, ex.Message)
    }

    interface IBillingReconciler with
        member _.ReconcileCustomer(customerId: string) : Async<ReconcileOutcome> = reconcileOne customerId

        member _.ReconcileAll() : Async<ReconcileOutcome list> = async {
            // A failing ENUMERATOR is not caught: there is no customer id to
            // attribute it to, and reporting an empty sweep would read as
            // "no drift found". Let it propagate — the job's `RetryPolicy`
            // is the right handler for it.
            let! customers = readers.EnumerateCustomers()
            let outcomes = ResizeArray<ReconcileOutcome>(List.length customers)

            for customerId in customers do
                let! outcome = reconcileOne customerId
                outcomes.Add outcome

            return List.ofSeq outcomes
        }