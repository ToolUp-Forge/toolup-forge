module ToolUp.Platform.Tests.InProcess.MultiInstanceAdminCoherenceValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 236 — multi-instance admin-store coherence validator ─────

let private run (cfg: ServerConfig) : ValidationResult =
    (MultiInstanceAdminCoherenceValidator.MultiInstanceAdminCoherenceValidator(cfg) :> IConfigValidator).Validate()
    |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "Phase 236 — multi-instance admin coherence" [

        test "single instance → Ok" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.team
                    ReplicaCount = 1
            }

            Expect.equal (run cfg) Ok "no warning on a single instance"
        }

        test "multi-instance + team-scoped → Warning names admin + permission stores" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.team
                    ReplicaCount = 2
            }

            match run cfg with
            | Warning msg ->
                Expect.stringContains msg "Platform-Admin store" "names the admin store"
                Expect.stringContains msg "permission store" "names the permission / team store"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "multi-instance + anonymous (no auth / team / webhooks / dsr) → Ok" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.anonymous
                    ReplicaCount = 2
            }

            Expect.equal (run cfg) Ok "no in-process admin subsystems composed"
        }

        test "multi-instance + webhooks enabled → Warning names the dispatcher" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.anonymous
                    ReplicaCount = 2
                    Webhooks = EnabledWebhooks
            }

            match run cfg with
            | Warning msg -> Expect.stringContains msg "webhook dispatcher" "names the webhook dispatcher"
            | other -> failtestf "expected Warning, got %A" other
        }
    ]