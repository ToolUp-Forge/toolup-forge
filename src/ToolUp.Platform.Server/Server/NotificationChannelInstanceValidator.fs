module ToolUp.Platform.NotificationChannelInstanceValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Multi-instance + in-memory notification channel ────────────────
//
// `TeamScopeResolver` caches the active-team pointer for 5 minutes and
// evicts it event-driven on `MembershipChanged` notifications. With the
// default in-memory `INotificationChannel`, a `RemoveMember` /
// `SetActiveTeam` on instance A never reaches instance B — so on every
// other replica a removed user keeps team-scoped data access for up to
// the 5-minute sliding-cache window. The default works on a single dev
// box and silently degrades a security boundary the moment the
// deployment is scaled out, with no signal.
//
// Mirrors `JobSchedulerInstanceValidator`: operator-declared
// multi-instance via `ServerConfig.ReplicaCount`. Warning (not Error)
// — the same soft-touch posture the other topology validators use;
// some Team deployments legitimately accept the short staleness window
// or invalidate out-of-band. Only the shipped distributed backend
// (`RedisNotifications`) clears it.

/// Config validator that warns when a team-scoped, multi-instance
/// deployment relies on the in-memory notification channel — across
/// replicas the membership-cache eviction never propagates, so a
/// removed user retains access until the cache expires.
type NotificationChannelInstanceValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "notification-channel-instance"
        member _.Timeout = timeout

        member _.Validate() = async {
            let multiInstance = config.ReplicaCount > 1
            let teamScoped = DeploymentConfig.hasTeamScope config

            let isDistributed =
                match config.Notifications with
                | RedisNotifications _ -> true
                | NotificationsAuto
                | NoNotifications
                | NoNotificationsExplicit
                | InMemoryNotifications -> false

            if multiInstance && teamScoped && not isDistributed then
                return
                    Warning(
                        sprintf
                            "ServerConfig.Surfaces = %s + ReplicaCount = %d + Notifications = %A. TeamScopeResolver evicts its 5-minute active-team / membership cache on MembershipChanged notifications, but the in-memory notification channel does not cross instance boundaries — a RemoveMember or SetActiveTeam handled on one replica never invalidates the others, so a removed user keeps team-scoped data access for up to 5 minutes on every other instance. Set ServerConfig.Notifications = RedisNotifications \"<connection-string>\" (the src/NotificationChannels/Redis companion) so eviction propagates across replicas, or run a single instance."
                            (DeploymentConfig.surfacesLabel config)
                            config.ReplicaCount
                            config.Notifications
                    )
            else
                return Ok
        }