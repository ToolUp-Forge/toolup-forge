module ToolUp.Platform.Tests.InProcess.NotificationChannelInstanceValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

let private cfg (surfaces: SurfaceProfile list) (replicas: int) (notifications: NotificationMode) : ServerConfig = {
    ServerConfig.defaults with
        Surfaces = surfaces
        ReplicaCount = replicas
        Notifications = notifications
}

let private validate (config: ServerConfig) : ValidationResult =
    let v =
        NotificationChannelInstanceValidator.NotificationChannelInstanceValidator(config) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "Notification-channel multi-instance validator" [

        test "Single instance (ReplicaCount = 1) → Ok regardless of channel" {
            Expect.equal
                (validate (cfg Surfaces.team 1 NotificationsAuto))
                Ok
                "single instance has no cross-replica problem"
        }

        test "Multi-instance Individual mode → Ok (not team-scoped, no membership cache)" {
            Expect.equal
                (validate (cfg Surfaces.individual 3 NotificationsAuto))
                Ok
                "membership cache is Team/MultiTeam only"
        }

        test "Multi-instance Team + in-memory channel → Warning" {
            match validate (cfg Surfaces.team 3 InMemoryNotifications) with
            | Warning msg ->
                Expect.stringContains msg "Team" "names the mode"
                Expect.stringContains msg "RedisNotifications" "points at the distributed fix"
                Expect.stringContains msg "removed user" "explains the security consequence"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Multi-instance MultiTeam + auto (resolves in-memory) → Warning" {
            match validate (cfg Surfaces.multiTeam 2 NotificationsAuto) with
            | Warning msg -> Expect.stringContains msg "MultiTeam" "names the mode"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Multi-instance Team + RedisNotifications → Ok (distributed backend propagates eviction)" {
            Expect.equal
                (validate (cfg Surfaces.team 5 (RedisNotifications "localhost:6379")))
                Ok
                "redis channel crosses replicas"
        }

        test "Validator metadata is well-formed" {
            let v =
                NotificationChannelInstanceValidator.NotificationChannelInstanceValidator(
                    cfg Surfaces.team 3 InMemoryNotifications
                )
                :> IConfigValidator

            Expect.equal v.Name "notification-channel-instance" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]