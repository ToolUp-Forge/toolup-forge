module ToolUp.AI.AIProviderEnvValidator

open System
open ToolUp.AI
open ToolUp.Platform.ConfigValidation

// ─── Phase 9m.A AI provider env-var preflight ────────────────────────
//
// Catches the operator-typo class of "TOOLUP_AI_PROVIDER=claud" — set
// to a value that does not match any provider this deployment can
// resolve. The typo'd value is silently ignored at runtime (per-request
// resolution falls through to the platform fallback or
// NoProviderConfigured) so the operator has no signal until the first
// chat request returns mysteriously wrong results.
//
// This validator runs at compose end against `IAIProviderFactory` —
// the live source of "what providers does this deployment know about?"
// — and emits a Warning naming the available provider ids. It never
// aborts startup (Warning, not Error): the runtime fall-through is the
// existing behaviour, and a Warning + log line is enough to expose the
// misconfiguration without making the SDK opinionated about whether
// the operator should be punished for the typo.
//
// Companion auto-registration: `composeAI` (in `AICompose.fs`) wires
// this in unconditionally. The validator self-skips with Ok when
// TOOLUP_AI_PROVIDER is unset — zero cost for deployments that don't
// rely on the env var (GP 13 — lightweight default).

let private envVarName = ToolUp.Platform.ConfigKeys.Names.aiProvider

let private readEnv () =
    match Environment.GetEnvironmentVariable envVarName with
    | null
    | "" -> None
    | v -> Some(v.Trim())

let private knownProviderIds (factory: IAIProviderFactory) : string list =
    let available = factory.Available |> List.map _.Id

    let platformId = factory.PlatformDescriptor |> Option.map _.Id |> Option.toList

    (platformId @ available) |> List.distinct

type private Impl(factory: IAIProviderFactory, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "ai-provider-env"
        member _.Timeout = timeout

        member _.Validate() = async {
            match readEnv () with
            | None -> return Ok
            | Some configured ->
                let known = knownProviderIds factory

                if List.contains configured known then
                    return Ok
                else
                    let knownList =
                        if known.IsEmpty then
                            "<none — this deployment has no platform provider or BYOK builders registered>"
                        else
                            known |> List.sort |> String.concat ", "

                    return
                        Warning(
                            sprintf
                                "%s='%s' does not match any AI provider known to this deployment. Available provider ids: %s. The configured value is silently ignored — per-request resolution falls through to the platform fallback (or fails with NoProviderConfigured). Likely cause: typo or copy-paste from another deployment with different providers wired."
                                envVarName
                                configured
                                knownList
                        )
        }

/// Construct a validator bound to the given factory. `composeAI` calls
/// this unconditionally — the validator self-skips with Ok when the env
/// var is unset.
let create (factory: IAIProviderFactory) : IConfigValidator = Impl(factory) :> IConfigValidator