module ToolUp.Platform.Tests.InProcess.JobSchedulerInstanceValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

let private cfg (scheduler: JobSchedulerMode) (replicaCount: int) (escapeHatch: bool) : ServerConfig = {
    ServerConfig.defaults with
        JobScheduler = scheduler
        ReplicaCount = replicaCount
        AcceptInProcessSchedulerInMultiInstance = escapeHatch
}

let private validate (config: ServerConfig) : ValidationResult =
    let v =
        JobSchedulerInstanceValidator.JobSchedulerInstanceValidator(config) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "Phase 6l.F — JobScheduler multi-instance validator" [

        test "NoJobScheduler + any replica count → Ok" {
            let result = validate (cfg NoJobScheduler 5 false)
            Expect.equal result Ok "no scheduler, no problem"
        }

        test "InProcessJobScheduler + ReplicaCount=1 → Ok (single-instance)" {
            let result = validate (cfg InProcessJobScheduler 1 false)
            Expect.equal result Ok "single instance is the default safe path"
        }

        test "InProcessJobScheduler + ReplicaCount=2 + no escape hatch → Error" {
            let result = validate (cfg InProcessJobScheduler 2 false)

            match result with
            | Error msg ->
                Expect.stringContains msg "InProcessJobScheduler" "names the offending scheduler"
                Expect.stringContains msg "ReplicaCount = 2" "names the replica count"
                Expect.stringContains msg "duplicate" "explains the consequence"
                Expect.stringContains msg "Phase 9c.A" "points at the architectural fix"

                Expect.stringContains msg "AcceptInProcessSchedulerInMultiInstance" "documents the escape hatch"
            | other -> failtestf "expected Error, got %A" other
        }

        test "InProcessJobScheduler + ReplicaCount=10 → Error (any N>1)" {
            let result = validate (cfg InProcessJobScheduler 10 false)

            match result with
            | Error _ -> ()
            | other -> failtestf "expected Error, got %A" other
        }

        test "InProcessJobScheduler + ReplicaCount=2 + escape hatch → Ok" {
            let result = validate (cfg InProcessJobScheduler 2 true)
            Expect.equal result Ok "explicit opt-in passes"
        }

        test "NoJobScheduler + ReplicaCount=10 → Ok (no scheduler means no duplication)" {
            let result = validate (cfg NoJobScheduler 10 false)
            Expect.equal result Ok "scheduler off, replica count irrelevant"
        }

        test "Validator metadata is well-formed" {
            let v =
                JobSchedulerInstanceValidator.JobSchedulerInstanceValidator(cfg InProcessJobScheduler 1 false)
                :> IConfigValidator

            Expect.equal v.Name "job-scheduler-instance" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]