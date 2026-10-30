// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AIProviders.Tests.Tests.AISettingsRevertTests

// ─── Phase 1105 — a refused revert is not reported as "not saved" ──
//
// `SaveInstance` writes a rotated key provisionally, verifies it, and on a
// failed verification reverts the profile and puts the previous key back.
// The refusal it answers ("the API key was not saved") is true only when
// that undo landed. A secret store that refuses the restoring write leaves
// the UNVERIFIED key at rest, so the answer must say the change could not
// be reverted. Offline: the probe is a stub, nothing reaches a vendor.

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.AI
open ToolUp.AI.DefaultAIProviderFactory
open ToolUp.AIProviders.Tests.Support.InMemoryStores

let private descriptor: AIProviderDescriptor = {
    Id = ClaudeAIProvider.ProviderId
    DisplayName = "Claude"
    SupportedModels = ClaudeAIProvider.KnownModels
    DefaultModel = ClaudeAIProvider.DefaultModel
    Capabilities = {
        Streaming = true
        ToolUse = true
        Vision = true
        SupportsPromptCaching = true
        SupportsTriage = true
        TriageModelId = Some ClaudeAIProvider.DefaultModel
        ProviderName = ClaudeAIProvider.ProviderId
        Model = ClaudeAIProvider.DefaultModel
    }
}

/// A probe that rejects every candidate key, so every rotation reverts.
let private rejectingProbe =
    { new IProviderEntryProbe with
        member _.Probe(_, _) = async {
            return
                Ok {
                    Reachable = false
                    Models = []
                    RateLimitHeadroom = None
                    SpendToDate = None
                    Diagnostic = Some "invalid x-api-key"
                }
        }
    }

/// Wraps `inner`; once `refuse` is set, every `SetSecret` is refused.
let private refusingAfter (inner: ISecretStore) (refuse: bool ref) =
    { new ISecretStore with
        member _.GetSecret(s, k) = inner.GetSecret(s, k)

        member _.SetSecret(s, k, v) =
            if refuse.Value then
                async { return Error "store is read-only" }
            else
                inner.SetSecret(s, k, v)

        member _.DeleteSecret(s, k) = inner.DeleteSecret(s, k)
        member _.ListKeys s = inner.ListKeys s
    }

let private apiOver (secrets: ISecretStore) =
    let profile = InMemoryProviderProfile() :> IProviderProfile

    let builder = {
        Descriptor = descriptor
        Build = ClaudeAIProvider.createWithApiKeyAndModel
    }

    let factory =
        DefaultAIProviderFactory.create [ builder ] profile secrets StrictBYOK [] None

    let services = ServiceCollection()

    services.AddSingleton<AccessContext>(AccessContext.unrestricted (AuthenticatedUser "user-1"))
    |> ignore

    services.AddSingleton<ISecretStore>(secrets) |> ignore
    services.AddSingleton<IProviderEntryProbe>(rejectingProbe) |> ignore
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- services.BuildServiceProvider()
    AISettingsHandler.aiSettingsApi factory profile ctx

let private save (key: string) : AIProviderInstanceSaveRequest = {
    Label = "primary"
    ProviderId = ClaudeAIProvider.ProviderId
    Model = None
    ApiKey = Some key
}

let tests =
    testList "Phase 1105 — AI settings revert" [

        testCaseAsync "a verified-failed rotation whose revert lands says the key was not saved"
        <| async {
            let api = apiOver (InMemorySecretStore() :> ISecretStore)

            match! api.SaveInstance(save "sk-bad") with
            | Error e -> Expect.stringContains e "was not saved" "the undo landed, so the answer is true"
            | Ok() -> failtest "a key that failed verification must not save"
        }

        testCaseAsync "a refused revert is reported, never as 'not saved'"
        <| async {
            let refuse = ref false
            let inner = InMemorySecretStore() :> ISecretStore
            let secrets = refusingAfter inner refuse

            // No prior key, so the revert must DELETE the candidate. Arm the
            // refusal as soon as the provisional write has landed: the store
            // accepts the candidate, then refuses the undo.
            let armed =
                { new ISecretStore with
                    member _.GetSecret(s, k) = secrets.GetSecret(s, k)

                    member _.SetSecret(s, k, v) = async {
                        let! result = secrets.SetSecret(s, k, v)
                        refuse.Value <- true
                        return result
                    }

                    member _.DeleteSecret(s, k) = async {
                        if refuse.Value then
                            return Error "store is read-only"
                        else
                            return! secrets.DeleteSecret(s, k)
                    }

                    member _.ListKeys s = secrets.ListKeys s
                }

            let api = apiOver armed

            match! api.SaveInstance(save "sk-bad") with
            | Error e ->
                Expect.isFalse
                    (e.Contains "was not saved")
                    "a revert that did not land must not claim nothing was saved"

                Expect.stringContains e "could NOT be fully reverted" "the answer says the undo failed"
                Expect.stringContains e "store is read-only" "and carries the store's refusal"
            | Ok() -> failtest "a key that failed verification must not save"
        }
    ]