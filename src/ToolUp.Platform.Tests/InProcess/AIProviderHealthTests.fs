module ToolUp.Platform.Tests.InProcess.AIProviderHealthTests

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts

/// Minimal in-process `ISecretStore` for the AI-provider probe tests.
/// Returns a fixed value for any (scopeId, key) lookup so the probe's
/// "key resolved → Healthy" path is exercised without depending on a
/// real secret backend or real env vars.
type private FakeSecretStore(value: string) =
    interface ISecretStore with
        member _.GetSecret(_scopeId, _key) = async { return Some value }

        member _.SetSecret(_scopeId, _key, _value) = async { return Error "test fake" }

        member _.DeleteSecret(_scopeId, _key) = async { return Error "test fake" }

        member _.ListKeys(_scopeId) = async { return [] }

let claudeTests =
    let factory () =
        let secrets = FakeSecretStore("test-key") :> ISecretStore
        ClaudeAIProviderHealth.create secrets

    IHealthCheckContract.tests "ClaudeAIProviderHealth" factory Healthy

let openAiTests =
    let factory () =
        let secrets = FakeSecretStore("test-key") :> ISecretStore
        OpenAIProviderHealth.create secrets

    IHealthCheckContract.tests "OpenAIProviderHealth" factory Healthy