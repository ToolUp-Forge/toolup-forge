namespace ToolUp.Reporting

open ToolUp.Platform

// ─── Phase 534.C — the subscription management contract ──────────────
//
// The typed RPC an admin surface consumes to list, create, pause and
// delete subscriptions, and to run one immediately without waiting for
// its next cron tick. Mirrors `IReportApi`'s shape and its gate posture
// exactly, because the two surfaces have the same security question and
// giving them different answers would be the drift.
//
// **Secure by default; scope stamped server-side.** Every method
// carries `[<RequiresClaim "scope">]`, the forge-conventional gate for
// a scope-owned surface that is never anonymous: against the default
// `ForgeAuthContext` resolver `HasClaim("scope", None)` resolves to
// exactly `not isAnonymous`, so the Phase 69d classifier refuses an
// unauthenticated caller before dispatch. No method takes a scope: the
// handler is built per-caller with the scope already resolved, and
// `NewReportSubscription` deliberately carries no `ScopeId` field, so
// there is no shape in which a client can assert a scope and have it
// silently ignored (GP 4).
//
// **Why not `[<RequiresRole "Owner">]`, when the phase says Owner /
// Admin gated.** The same reason Phase 619 gave for `IReportApi`: the
// first-party auth providers leave `AuthenticatedUser.Roles` empty, so
// any role string other than the server-resolved `"PlatformAdmin"` is a
// dead gate that denies every caller, admins included. A subscription
// is scope-owned rather than platform-owned, so `"PlatformAdmin"` would
// break per-team management in every deployment. The Owner/Admin intent
// lives in `ReportSubscriptionApiHandler.withManagementGate`, where the
// deployment supplies the predicate its own role model can answer — and
// where, per Phase 229's lesson, it is a gate somebody can point at
// rather than one that lives entirely in wiring.

/// Fable.Remoting contract for subscription management. Every method
/// returns `Async<Result<_, _>>` per the SDK convention.
type IReportSubscriptionApi = {
    /// Every report producer registered in this deployment, with the
    /// parameters each takes. What a create form is built from.
    /// **Gate: any authenticated caller at the resolved scope.** The
    /// descriptor set describes what the deployment can report on, which
    /// is scope-relevant business content, not a public catalogue.
    [<RequiresClaim "scope">]
    ListProducers: unit -> Async<ReportProducerDescriptor list>

    /// Every subscription at the resolved scope, with its last-run
    /// outcome.
    /// **Gate: any authenticated caller at the resolved scope.**
    [<RequiresClaim "scope">]
    ListSubscriptions: unit -> Async<ReportSubscription list>

    /// Create a subscription. The schedule, the parameters, the format
    /// and the recipient list are all validated against the named
    /// producer BEFORE anything is persisted or scheduled — a
    /// subscription that would fail at its first tick is refused while
    /// the caller is still present to be told why.
    /// **Gate: authenticated, PLUS the deployment's management
    /// predicate when `withManagementGate` is composed.** A subscription
    /// sends scope-owned content to a recipient list, so creating one is
    /// a privileged scope mutation.
    [<RequiresClaim "scope">]
    CreateSubscription: NewReportSubscription -> Async<Result<ReportSubscription, SubscriptionError>>

    /// Replace a subscription's authored fields. Same validation as
    /// create; `LastRun`, `CreatedBy` and `CreatedAt` are preserved from
    /// the stored record rather than taken from the caller.
    /// **Gate: identical to `CreateSubscription`.**
    [<RequiresClaim "scope">]
    UpdateSubscription: SubscriptionId * NewReportSubscription -> Async<Result<ReportSubscription, SubscriptionError>>

    /// Pause or resume. Separate from `UpdateSubscription` because
    /// pausing is the one management action an operator takes in a
    /// hurry, and making it a whole-record round-trip invites a
    /// lost-update race with whoever is editing the same subscription.
    /// **Gate: identical to `CreateSubscription`.**
    [<RequiresClaim "scope">]
    SetSubscriptionEnabled: SubscriptionId * bool -> Async<Result<ReportSubscription, SubscriptionError>>

    /// Delete a subscription and cancel its scheduled job. The run
    /// artefacts it already produced are versioned objects and are NOT
    /// deleted — deleting a subscription stops future reports; it does
    /// not retract the ones already delivered.
    /// **Gate: identical to `CreateSubscription`, and destructive
    /// besides.**
    [<RequiresClaim "scope">]
    DeleteSubscription: SubscriptionId -> Async<Result<unit, SubscriptionError>>

    /// Render and deliver immediately, without waiting for the next
    /// cron tick. Dispatched through the scheduler's `TriggerOnce`
    /// rather than by calling the handler inline, so a run-now takes the
    /// same retry policy, the same run history and the same audit trail
    /// as a scheduled run — an operator testing a subscription is
    /// testing the thing that will actually happen on Monday.
    /// **Gate: identical to `CreateSubscription`** — it causes a
    /// delivery to the subscription's recipients.
    [<RequiresClaim "scope">]
    RunSubscriptionNow: SubscriptionId -> Async<Result<unit, SubscriptionError>>
}