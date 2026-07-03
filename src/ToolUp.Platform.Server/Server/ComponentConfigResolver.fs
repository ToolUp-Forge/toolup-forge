// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ComponentConfigResolver

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 289 — component-scoped configuration resolution ───────────
//
// The server-tier half of `ComponentConfig` (the Core type carries the
// section + the pure env-var-name derivation). Two surfaces:
//
//   1. `resolve` — merge a component's declared config section with its
//      id-scoped env overrides (`TOOLUP_COMPONENT__<id>__<key>`), so a
//      deployment overrides exactly one component's configuration by id.
//      Only *declared* keys are overridable — an override targets a key
//      the component already knows about, keeping the surface addressable
//      + typo-catchable rather than an open bag.
//
//   2. `overrideValidator` — an `IConfigValidator` that runs through the
//      existing Phase 9m preflight aggregator and fails startup when a
//      `TOOLUP_COMPONENT__*` override targets no known component + key
//      (a typo'd id or an undeclared key), with a readable message that
//      names the offending variable. A deployment with no overrides
//      passes silently (GP 11).
//
// Both take their environment accessors as parameters (rather than
// reading `Environment` directly) so the merge + the validation are pure
// over an injected view of the environment — deterministic and testable
// without mutating process-global state. `fromEnvironment` /
// `environmentReader` supply the production accessors.

/// Read a single environment variable by name — `None` when unset or
/// blank (a blank override is treated as absent, never as an empty
/// override value).
type EnvReader = string -> string option

/// Enumerate every environment variable as `(name, value)` — used by the
/// validator to find override variables that target no known component.
type EnvEnumerator = unit -> (string * string) list

/// Production `EnvReader` over `Environment.GetEnvironmentVariable`.
let environmentReader: EnvReader =
    fun name ->
        match Environment.GetEnvironmentVariable name with
        | null -> None
        | v when String.IsNullOrWhiteSpace v -> None
        | v -> Some v

/// Production `EnvEnumerator` over `Environment.GetEnvironmentVariables`.
let environmentEnumerator: EnvEnumerator =
    fun () ->
        Environment.GetEnvironmentVariables()
        |> Seq.cast<System.Collections.DictionaryEntry>
        |> Seq.choose (fun entry ->
            match entry.Key, entry.Value with
            | (:? string as k), (:? string as v) -> Some(k, v)
            | _ -> None)
        |> List.ofSeq

/// Merge a declared `ComponentConfig` with its id-scoped env overrides:
/// for every declared key, an override at
/// `ComponentConfig.envVarName Component key` wins; an unset override
/// leaves the declared value. Only declared keys are overridable, so an
/// override that names an undeclared key does not silently appear here —
/// it is surfaced by `overrideValidator` at preflight instead. A section
/// whose keys carry no overrides is returned structurally unchanged
/// (GP 11).
let resolve (readEnv: EnvReader) (declared: ComponentConfig) : ComponentConfig =
    let merged =
        declared.Values
        |> Map.map (fun key declaredValue ->
            match readEnv (ComponentConfig.envVarName declared.Component key) with
            | Some overridden -> overridden
            | None -> declaredValue)

    { declared with Values = merged }

/// The set of env-var names that are *valid* overrides for the given
/// known component sections — one per (component, declared key). Any
/// `TOOLUP_COMPONENT__*` variable outside this set targets no known
/// component + key.
let private validOverrideNames (known: ComponentConfig list) : Set<string> =
    known
    |> List.collect (fun section ->
        section.Values
        |> Map.toList
        |> List.map (fun (key, _) -> ComponentConfig.envVarName section.Component key))
    |> Set.ofList

/// The `TOOLUP_COMPONENT__*` override variables present in the
/// environment that target no known component + declared key. Each is a
/// deployment typo (wrong id, or a key the component never declared) —
/// the value that silently does nothing.
let unrecognisedOverrides (enumEnv: EnvEnumerator) (known: ComponentConfig list) : string list =
    let valid = validOverrideNames known

    enumEnv ()
    |> List.map fst
    |> List.filter (fun name -> name.StartsWith(ComponentConfig.EnvVarPrefix, StringComparison.Ordinal))
    |> List.filter (fun name -> not (valid.Contains name))
    |> List.distinct
    |> List.sort

/// An `IConfigValidator` that fails preflight when a
/// `TOOLUP_COMPONENT__*` override targets no known component + declared
/// key. The message names each offending variable so the operator can
/// fix the id / key rather than have the override silently do nothing. A
/// deployment with no such stray overrides returns `Ok` (GP 11); the
/// validator wires into the standard Phase 9m aggregator via
/// `services.AddSingleton<IConfigValidator>(...)`.
type ComponentConfigOverrideValidator(enumEnv: EnvEnumerator, known: ComponentConfig list) =
    /// Stable registration name (the `IConfigValidator` identity key).
    static member val Name = "component-config-overrides" with get

    interface IConfigValidator with
        member _.Name = ComponentConfigOverrideValidator.Name
        member _.Timeout = IConfigValidator.defaultTimeout

        member _.Validate() = async {
            match unrecognisedOverrides enumEnv known with
            | [] -> return Ok
            | strays ->
                let listing = strays |> String.concat ", "

                return
                    Error(
                        sprintf
                            "Component config override(s) target no composed component or declared key: %s. Each %s* variable must match a known ComponentId + a key that component declares (see ComponentConfig.envVarName); a stray override silently does nothing. Fix the id / key, or declare the key on the owning component."
                            listing
                            ComponentConfig.EnvVarPrefix
                    )
        }

/// The override preflight validator over the production environment
/// enumerator. Compose it into a deployment that binds `ComponentConfig`
/// sections so a typo'd id-scoped override fails startup loudly.
let overrideValidator (known: ComponentConfig list) : IConfigValidator =
    ComponentConfigOverrideValidator(environmentEnumerator, known) :> IConfigValidator