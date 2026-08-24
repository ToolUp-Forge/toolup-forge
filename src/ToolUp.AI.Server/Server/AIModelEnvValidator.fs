module ToolUp.AI.AIModelEnvValidator

open System
open ToolUp.AI
open ToolUp.Platform.ConfigValidation

// ─── Phase 9m.A AI model env-var preflight ───────────────────────────
//
// Sibling to `AIProviderEnvValidator` — catches an operator setting
// TOOLUP_AI_MODEL to a value the chosen provider does not list under
// its `SupportedModels`. Silent-fallthrough class: today the typo'd
// model produces a 400 / 404 from the upstream provider on the first
// chat request, with a stack trace surfaced to the user instead of
// the operator.
//
// Resolution strategy for the "which provider's SupportedModels do I
// check against?" question:
//   1. If TOOLUP_AI_PROVIDER is set AND matches a known descriptor,
//      check TOOLUP_AI_MODEL against that descriptor's SupportedModels
//      (+ DefaultModel for safety — operators sometimes paste the
//      DefaultModel literal explicitly).
//   2. If TOOLUP_AI_PROVIDER is unset OR set to an unknown value,
//      check TOOLUP_AI_MODEL against the union of every known
//      descriptor's SupportedModels. Common case: the operator only
//      sets TOOLUP_AI_MODEL, expecting the platform descriptor's
//      model.
// A mismatched provider (case 2 path) is the AIProviderEnvValidator's
// concern, not ours — we only emit the model-mismatch Warning.
//
// Companion auto-registration: same as AIProviderEnvValidator —
// always-on, self-skips with Ok when TOOLUP_AI_MODEL is unset.

let private modelEnvVarName = ToolUp.Platform.ConfigKeys.Names.aiModel
let private providerEnvVarName = ToolUp.Platform.ConfigKeys.Names.aiProvider

let private readEnv (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some(v.Trim())

let private knownDescriptors (factory: IAIProviderFactory) : AIProviderDescriptor list =
    let platform = factory.PlatformDescriptor |> Option.toList
    let available = factory.Available
    (platform @ available) |> List.distinctBy _.Id

let private modelsOfDescriptor (d: AIProviderDescriptor) : string list =
    d.DefaultModel :: d.SupportedModels |> List.distinct

type private Impl(factory: IAIProviderFactory, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "ai-model-env"
        member _.Timeout = timeout

        member _.Validate() = async {
            match readEnv modelEnvVarName with
            | None -> return Ok
            | Some configuredModel ->
                let descriptors = knownDescriptors factory
                let providerEnv = readEnv providerEnvVarName

                let scopedDescriptors =
                    match providerEnv with
                    | Some providerId ->
                        let matched = descriptors |> List.filter (fun d -> d.Id = providerId)

                        if matched.IsEmpty then descriptors else matched
                    | None -> descriptors

                let allModels =
                    scopedDescriptors |> List.collect modelsOfDescriptor |> List.distinct

                if List.contains configuredModel allModels then
                    return Ok
                else
                    let scopeNote =
                        match providerEnv with
                        | Some providerId when scopedDescriptors |> List.exists (fun d -> d.Id = providerId) ->
                            sprintf " for provider '%s'" providerId
                        | _ -> " across known providers"

                    let knownList =
                        if allModels.IsEmpty then
                            "<none — no provider descriptors with declared SupportedModels are registered>"
                        else
                            allModels |> List.sort |> String.concat ", "

                    return
                        Warning(
                            sprintf
                                "%s='%s' is not a known model%s. Known models: %s. Upstream provider will reject the call (HTTP 400 / 404) on the first chat request unless the model identifier was released after this build — in which case the value can be left as-is. Likely cause: typo or stale model id."
                                modelEnvVarName
                                configuredModel
                                scopeNote
                                knownList
                        )
        }

/// Construct a validator bound to the given factory. `composeAI` calls
/// this unconditionally — the validator self-skips with Ok when the env
/// var is unset.
let create (factory: IAIProviderFactory) : IConfigValidator = Impl(factory) :> IConfigValidator