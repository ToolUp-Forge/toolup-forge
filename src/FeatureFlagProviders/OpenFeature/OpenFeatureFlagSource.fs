// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.FeatureFlagProviders.OpenFeature

open OpenFeature
open OpenFeature.Model
open OpenFeature.Constant
open ToolUp.Platform

/// `IFlagSource` (Phase 239) backed by the **OpenFeature** standard — the
/// opt-in companion that lets a deployment back the flag-evaluation path
/// with its existing flag system (LaunchDarkly / Flagsmith / Unleash /
/// …) without that system implementing the write-centric
/// `IFeatureFlagStore`. The deployment registers its OpenFeature provider
/// through the OpenFeature API; this adapter maps a ToolUp
/// `AccessContext` to an OpenFeature `EvaluationContext` and resolves the
/// declared flag with the correctly-typed evaluation.
///
/// Defers (`None`) on any `ErrorType` (e.g. the provider doesn't know the
/// flag), so resolution falls through to the ToolUp declared default —
/// the additive precedence the seam guarantees (GP 11).
type OpenFeatureFlagSource(client: FeatureClient) =

    /// Resolve through the process-wide OpenFeature client
    /// (`Api.Instance.GetClient()`).
    new() = OpenFeatureFlagSource(Api.Instance.GetClient())

    static member private buildContext(ctx: AccessContext) : EvaluationContext =
        let b = EvaluationContext.Builder()

        match ctx.Subject with
        | AuthenticatedUser userId -> b.SetTargetingKey(userId) |> ignore
        | TeamMember(userId, teamId) -> b.SetTargetingKey(userId).Set("teamId", Value(teamId)) |> ignore
        | AnonymousSession sid -> b.SetTargetingKey(sid) |> ignore
        | ClaimBearer _ -> ()

        b.Build()

    interface IFlagSource with
        member _.Resolve(flag, ctx) = async {
            let evalCtx = OpenFeatureFlagSource.buildContext ctx

            match flag.DefaultValue with
            | FlagValue.Bool dflt ->
                let! d = client.GetBooleanDetailsAsync(flag.Key, dflt, evalCtx) |> Async.AwaitTask

                if d.ErrorType = ErrorType.None then
                    return Some(FlagValue.Bool d.Value)
                else
                    return None
            | FlagValue.Variant(options, dflt) ->
                let! d = client.GetStringDetailsAsync(flag.Key, dflt, evalCtx) |> Async.AwaitTask

                if d.ErrorType = ErrorType.None then
                    return Some(FlagValue.Variant(options, d.Value))
                else
                    return None
        }