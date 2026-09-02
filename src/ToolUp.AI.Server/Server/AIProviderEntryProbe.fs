// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.AIProviderEntryProbe

open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI

// ─── Phase 43.C — the AI tier's IProviderEntryProbe ───────────────
//
// The first implementation of the platform's `IProviderEntryProbe`
// seam: exercise a configured `ProviderEntry` by building its provider
// through `IAIProviderFactory.TryResolveByLabel` and making one small
// real call.
//
// **`TryResolveByLabel` deliberately, not a new mechanism.** That is
// the same entry point the settings UI's "Test connection" already
// uses, and the phase spec is explicit about reusing its shape. Two
// consequences worth stating: the probe exercises the REAL resolution
// chain (routing rule bypassed, entry looked up by label, key read
// from `ISecretStore` at the request scope), so a probe that passes
// means a chat turn against that entry would resolve; and there is no
// second code path to keep in step with the first.
//
// **The call is deliberately tiny.** A one-word system prompt and a
// `ping` user message — the same payload `AISettingsHandler`'s
// TestConnection sends. Providers still bill for it, which is exactly
// why the live-status probe's default cadence is hourly rather than
// five-minutely.
//
// **Model list.** `ProviderProbeOutcome.Models` is populated from the
// resolved provider's descriptor (`SupportedModels` plus the model the
// built provider actually reports through `Capabilities.Model`), not
// from a vendor "list models" endpoint. Every shipped `IAIProvider`
// declares its catalogue and none of them expose a portable model-list
// call, so a descriptor read is the honest answer here; a probe that
// invented a vendor endpoint per provider would be a second surface to
// maintain for a field the UI renders as a hint.

/// Minimal probe message. Same content as the settings handler's
/// test-connection payload so the two cost the same.
let private probeMessage: AIProviderMessage = {
    Role = "user"
    Content = "ping"
    ToolCalls = []
    ToolResults = []
    Parts = []
}

/// System prompt that keeps the response inside the ~100-token budget
/// the phase specifies. Providers charge for the round-trip either
/// way; this bounds the output half of it.
let private probeSystemPrompt = Some "Reply with a single word."

/// Reconstruct the `AccessContext` whose `configScope` is exactly the
/// given `StorageScope`.
///
/// The probe runs from a background job where no request principal
/// exists, and `IAIProviderFactory` resolves against an
/// `AccessContext` rather than a scope — so the scope has to be
/// projected BACK onto a subject. The container prefix is what carries
/// that information (`team-{id}` / `user-{id}`, minted by
/// `AccessContext.configScope` itself), so the round-trip is exact for
/// both shapes forge produces.
///
/// A container matching neither prefix is a `ClaimBearer` scope, whose
/// subject carries a whole `ShareTokenClaim` that cannot be
/// reconstructed from a `StorageScope` alone — so this returns `None`
/// rather than fabricating one. Guessing would have the probe read a
/// DIFFERENT scope's profile and secrets, which is a GP-4 isolation
/// break dressed as a convenience.
///
/// `"system"` stands in for the team-member user id because no user is
/// online during a scheduled probe — the same convention the OAuth
/// refresh audit uses, and nothing on this path reads it for authority
/// (`configScope` keys a `TeamMember` on the team id alone).
let scopeAccessContext (scope: StorageScope) : AccessContext option =
    if scope.Container.StartsWith "team-" then
        Some(AccessContext.unrestricted (TeamMember("system", scope.ScopeId)))
    elif scope.Container.StartsWith "user-" then
        Some(AccessContext.unrestricted (AuthenticatedUser scope.ScopeId))
    else
        None

/// Build the probe over an `IAIProviderFactory`.
///
/// `accessContextFor` maps a `StorageScope` back onto the
/// `AccessContext` the factory resolves against. The probe runs from a
/// background job where no request principal exists, so it cannot read
/// one from DI — it synthesises one for the scope, which is the same
/// posture `JobContext.AccessContext` takes (a scope, not a user's
/// authority).
let create
    (factory: IAIProviderFactory)
    (accessContextFor: StorageScope -> AccessContext option)
    : IProviderEntryProbe =
    { new IProviderEntryProbe with
        member _.Probe(scope, entry) = async {
            match accessContextFor scope with
            | None ->
                return
                    Error
                        $"Cannot probe scope '{scope.Container}': no AccessContext can be reconstructed for it (probes support team- and user-scoped containers)."
            | Some accessContext ->

                let declaredModels =
                    factory.Available @ factory.PlatformDescriptors
                    |> List.tryFind (fun d -> d.Id = entry.ProviderId)
                    |> Option.map _.SupportedModels
                    |> Option.defaultValue []

                try
                    let! resolved = factory.TryResolveByLabel(accessContext, entry.Label)

                    match resolved with
                    | Error err ->
                        // Could not even build a provider — a missing key,
                        // an unknown provider id. `Error`, not
                        // `Ok { Reachable = false }`: nothing was
                        // attempted upstream, and the two drive different
                        // UI copy.
                        return Error(ProviderResolutionError.toMessage err)
                    | Ok provider ->
                        let! response =
                            provider.SendMessage([ probeMessage ], [], probeSystemPrompt, None, RetryPolicy.noRetry)

                        match response with
                        | Ok _ ->
                            let models =
                                (provider.Capabilities.Model :: declaredModels)
                                |> List.distinct
                                |> List.filter (System.String.IsNullOrWhiteSpace >> not)

                            return
                                Ok {
                                    ProviderProbeOutcome.reachable with
                                        Models = models
                                }
                        | Error err ->
                            // The upstream was reached and said no — a bad
                            // key, an exhausted quota, a retired model.
                            // `Ok` with `Reachable = false`.
                            return Ok(ProviderProbeOutcome.failed (AIProviderError.toMessage err))
                with ex ->
                    // Contract: a probe never throws. A vendor client that
                    // raises must not be able to take a background job
                    // down, and a caller distinguishing "attempted and
                    // failed" from "could not attempt" needs this to land
                    // on the former — the call WAS attempted.
                    return Ok(ProviderProbeOutcome.failed ex.Message)
        }
    }