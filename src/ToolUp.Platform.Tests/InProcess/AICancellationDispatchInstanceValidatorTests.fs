module ToolUp.Platform.Tests.InProcess.AICancellationDispatchInstanceValidatorTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.AI

// ─── Phase 6n — AI cancel / client-tool-dispatch multi-instance ──────
//
// `AICancellationRegistry` and `ClientToolDispatchRegistry` are
// per-process singletons. Behind a load balancer with >1 replica a
// cancel POST or a client-tool-result POST that lands on a replica
// other than the one running the agent loop 404s — the Cancel button
// silently does nothing and client-tool round-trips hang to timeout.
//
// TWO things about the shipped ladder are worth stating here, because
// the Phase 6n shard's prose describes a DIFFERENT one and a reader
// arriving from the roadmap will expect the shard's version:
//
//   * the un-attested multi-replica case is `Error` (a refusal), not
//     `Warning`. The shard says "Warning (not Error)"; the shipped
//     validator, `ServerConfig.AcceptStickyRoutedAiInMultiInstance`'s
//     own doc comment ("`AICancellationDispatchInstanceValidator`
//     refuses startup") and `docs/operations/env-vars.md` ("Lowers a
//     startup preflight refusal to a warning") all agree on `Error`.
//   * the escape hatch therefore DEGRADES the refusal to a `Warning`
//     rather than clearing it to `Ok`. That is the deliberate design:
//     sticky routing is an attestation about a load balancer, not a
//     guarantee the SDK can verify, so the residual risk stays visible
//     in the `/dev/inspect` Validators panel.
//
// These tests pin the shipped ladder. Changing it is a deliberate
// behaviour change and should turn this pack red.

let private cfg (replicaCount: int) (escapeHatch: bool) : ServerConfig = {
    ServerConfig.defaults with
        ReplicaCount = replicaCount
        AcceptStickyRoutedAiInMultiInstance = escapeHatch
}

let private validator (config: ServerConfig) : IConfigValidator =
    AICancellationDispatchInstanceValidator.AICancellationDispatchInstanceValidator(config) :> IConfigValidator

let private validate (config: ServerConfig) : ValidationResult =
    (validator config).Validate() |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "Phase 6n — AI cancel/dispatch multi-instance validator" [

        test "ReplicaCount=1 → Ok (single-instance is the safe default)" {
            let result = validate (cfg 1 false)
            Expect.equal result Ok "one replica owns every agent loop, cancel and tool-result"
        }

        test "ReplicaCount=1 + escape hatch set → Ok (the hatch is inert single-instance)" {
            let result = validate (cfg 1 true)
            Expect.equal result Ok "attesting sticky routing on one replica changes nothing"
        }

        test "ReplicaCount=2 + no escape hatch → Error (refusal)" {
            match validate (cfg 2 false) with
            | Error msg ->
                Expect.stringContains msg "ReplicaCount = 2" "names the replica count"
                Expect.stringContains msg "AICancellationRegistry" "names the cancel registry"
                Expect.stringContains msg "ClientToolDispatchRegistry" "names the dispatch registry"
                Expect.stringContains msg "404" "names the failure mode"

                Expect.stringContains
                    msg
                    "AcceptStickyRoutedAiInMultiInstance"
                    "documents the escape hatch by field name"

                Expect.stringContains
                    msg
                    "TOOLUP_ACCEPT_STICKY_ROUTED_AI_MULTI_INSTANCE=1"
                    "documents the env var that flips it"

                Expect.stringContains msg "distributed registry" "points at the architectural fix"
                Expect.stringContains msg "/dev/inspect" "tells the operator where to verify"
            | other -> failtestf "expected Error, got %A" other
        }

        test "ReplicaCount=10 + no escape hatch → Error (any N>1, not just 2)" {
            match validate (cfg 10 false) with
            | Error msg -> Expect.stringContains msg "ReplicaCount = 10" "reports the configured count"
            | other -> failtestf "expected Error, got %A" other
        }

        test "ReplicaCount=2 + escape hatch → Warning (degraded, not cleared)" {
            match validate (cfg 2 true) with
            | Warning msg ->
                Expect.stringContains msg "ReplicaCount = 2" "names the replica count"

                Expect.stringContains
                    msg
                    "AcceptStickyRoutedAiInMultiInstance = true"
                    "records that the operator attested sticky routing"

                Expect.stringContains msg "Residual risk" "keeps the residual risk visible"
            | other ->
                failtestf "expected Warning — the attestation DEGRADES the refusal, it does not clear it — got %A" other
        }

        test "ReplicaCount=100 + escape hatch → Warning (attestation holds at any N)" {
            match validate (cfg 100 true) with
            | Warning _ -> ()
            | other -> failtestf "expected Warning, got %A" other
        }

        test "no-AI deployments are unaffected — the validator ships in the AI tier" {
            // This validator carries no "is AI composed?" knob: it keys
            // purely on `ReplicaCount`. What makes a non-AI deployment
            // safe at any replica count is that the validator is only
            // ever REGISTERED inside the AI compose branch
            // (`AICompose.fs`, alongside the two registries it is about)
            // and only ever SHIPPED in the AI-tier assembly — a
            // platform-only composition neither references nor loads it.
            //
            // Moving the type down into the platform tier would make it
            // registrable by a non-AI composition, where it would refuse
            // startup for a deployment that has no cancel/dispatch
            // registries at all. That is the regression this pins.
            let assemblyName =
                typeof<AICancellationDispatchInstanceValidator.AICancellationDispatchInstanceValidator>.Assembly
                    .GetName()
                    .Name

            Expect.equal assemblyName "ToolUp.AI.Server" "the validator stays in the AI tier"
        }

        test "Validator metadata is well-formed" {
            let v = validator (cfg 1 false)
            Expect.equal v.Name "ai-cancel-dispatch-instance" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
            Expect.equal v.Timeout IConfigValidator.defaultTimeout "defaults to the shared preflight timeout"
        }

        test "Timeout override is honoured" {
            let v =
                AICancellationDispatchInstanceValidator.AICancellationDispatchInstanceValidator(
                    cfg 1 false,
                    TimeSpan.FromSeconds 7.0
                )
                :> IConfigValidator

            Expect.equal v.Timeout (TimeSpan.FromSeconds 7.0) "the optional ctor argument wins"
        }
    ]