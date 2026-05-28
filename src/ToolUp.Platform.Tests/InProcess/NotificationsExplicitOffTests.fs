module ToolUp.Platform.Tests.InProcess.NotificationsExplicitOffTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 58 — NoNotificationsExplicit server-side wiring ─────────────
//
// The client-side EventSource-skip behaviour is gated by
// `BundleConstants.notificationsDisabledExplicitly` (a Vite-define-time
// boolean) and exercised by `NotificationClient.ensureConnected`. Both
// live in the Fable-compiled `ToolUp.Platform.Client` tree and are not
// reachable from this .NET test runner — they are verified by the
// HelloWorld sample's Fable build (acceptance criteria in the phase
// migration doc).
//
// What we cover here is the .NET-side composition wiring that the
// `NoNotificationsExplicit` mode depends on:
//   * `NotificationMode.resolve` passes the explicit mode through
//     unchanged (regression check — `NotificationsAuto` is the only
//     case that re-resolves).
//   * `NotificationChannelInstanceValidator` treats the explicit mode
//     as non-distributed, matching the in-memory / `NoNotifications`
//     shape (so team-scoped + multi-instance deployments still see the
//     "membership-cache eviction does not cross replicas" warning).
//   * `RedisNotifications` remains the only mode the validator
//     considers distributed (regression check — easy to accidentally
//     drop a case from the `isDistributed` discriminator).

let private cfg (mode: PlatformMode) (replicas: int) (notifications: NotificationMode) : ServerConfig = {
    ServerConfig.defaults with
        Surfaces = Surfaces.fromLegacyMode mode
        ReplicaCount = replicas
        Notifications = notifications
}

let private validate (config: ServerConfig) : ValidationResult =
    let v =
        NotificationChannelInstanceValidator.NotificationChannelInstanceValidator(config) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "Phase 58 — NoNotificationsExplicit composition" [

        test "NotificationMode.resolve passes NoNotificationsExplicit through unchanged" {
            let resolved =
                NotificationMode.resolve NoNotificationsExplicit InProcessJobScheduler MultiTeam [ "ai" ]

            // Even with a job scheduler, MultiTeam mode, and a
            // declared consumer — all triggers that would flip
            // `NotificationsAuto` to `InMemoryNotifications` — the
            // explicit mode is a hard pin and must come through
            // unchanged.
            Expect.equal resolved NoNotificationsExplicit "explicit pin overrides every auto-detect trigger"
        }

        test "NotificationMode.resolve passes NoNotificationsExplicit through under the lightweight defaults" {
            let resolved =
                NotificationMode.resolve NoNotificationsExplicit NoJobScheduler Individual []

            Expect.equal resolved NoNotificationsExplicit "explicit pin preserved when no auto trigger fires"
        }

        test "Validator: Single instance with NoNotificationsExplicit → Ok" {
            Expect.equal
                (validate (cfg Team 1 NoNotificationsExplicit))
                Ok
                "single instance has no cross-replica problem regardless of channel"
        }

        test "Validator: Multi-instance Team + NoNotificationsExplicit → Warning (treated as non-distributed)" {
            // The explicit-off mode does not magically make a
            // multi-instance team-scoped deployment safe — there is
            // still no transport for `MembershipChanged` to ride
            // across replicas. The validator must catch this with the
            // same warning shape it emits for `InMemoryNotifications`
            // and the lightweight `NoNotifications` default.
            match validate (cfg Team 3 NoNotificationsExplicit) with
            | Warning msg ->
                Expect.stringContains msg "Team" "names the mode"
                Expect.stringContains msg "RedisNotifications" "points at the distributed fix"
                Expect.stringContains msg "removed user" "explains the security consequence"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Validator: Multi-instance MultiTeam + NoNotificationsExplicit → Warning" {
            // MultiTeam shares the team-scope shape with Team; the
            // validator must catch both.
            match validate (cfg MultiTeam 2 NoNotificationsExplicit) with
            | Warning msg -> Expect.stringContains msg "MultiTeam" "names the mode"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Validator: Multi-instance Individual + NoNotificationsExplicit → Ok (no membership cache)" {
            // Individual mode has no team-scope membership cache to
            // evict, so the cross-replica-propagation concern does
            // not apply regardless of the channel choice.
            Expect.equal
                (validate (cfg Individual 4 NoNotificationsExplicit))
                Ok
                "membership cache is Team/MultiTeam only"
        }

        test "Validator: RedisNotifications stays the only distributed mode (regression on isDistributed)" {
            // Easy regression: someone adds a new `NotificationMode`
            // case and forgets to extend the `isDistributed` match.
            // We pin the contract by asserting Redis is the only mode
            // that lifts the warning under Team + multi-instance.
            for mode in
                [
                    NotificationsAuto
                    NoNotifications
                    NoNotificationsExplicit
                    InMemoryNotifications
                ] do
                match validate (cfg Team 3 mode) with
                | Warning _ -> ()
                | other -> failtestf "expected Warning for non-distributed mode %A, got %A" mode other

            Expect.equal
                (validate (cfg Team 3 (RedisNotifications "localhost:6379")))
                Ok
                "Redis is the lone distributed mode"
        }
    ]