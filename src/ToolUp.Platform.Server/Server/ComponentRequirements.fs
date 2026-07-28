// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ComponentRequirementsPreflight

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Secrets

// ─── Phase 432 — component requirements: derivation + presence preflight ─
//
// The Server-tier half of `ComponentRequirements` (the Core file carries
// the requirement TYPES + the pure signature algebra). Three surfaces:
//
//   1. **Derivation (432.B)** — `fromComponentConfig` reads a component's
//      Phase 289 `ComponentConfig` section and derives its knob
//      requirements from it. A component that already binds config by id
//      therefore declares nothing and cannot drift: the requirement set
//      IS the section. Secrets have no equivalent registration to read (a
//      companion resolves them inside its own `create`), so they are the
//      residual DECLARED half a companion contributes alongside its Phase
//      282 capability, merged on top by `derive`.
//
//   2. **Presence preflight (432.C)** — every REQUIRED secret resolves in
//      `ISecretStore` (**existence only** — see the redaction note) and
//      every REQUIRED knob binds, aggregated into ONE typed report naming
//      the `ComponentId` + the requirement. Registered through the Phase
//      9m aggregator, so it rides the same startup gate as every other
//      `IConfigValidator` and aborts the boot on `Error`.
//
//   3. **Opt-in wiring** — `serviceRegistration` returns the same
//      `IServiceCollection -> IServiceCollection` closure the Phase 281
//      `CompositionValidator.serviceRegistration` does, so a composition
//      root folds it into the extension `ServiceConfig` hook the same
//      way. Nothing is registered unless something is actually required,
//      so a deployment that declares no requirements composes a
//      byte-for-byte identical `services` (GP 11 / GP 13).
//
// **Two validators, two classes (Phase 585).** The requirement check
// splits cleanly along the class boundary the preflight aggregator
// already draws, and it must, because a validator is classified as a
// whole:
//
//   * `component-config-requirements` is **structural-class** (carries
//     `IStructuralClassValidator`). It is a pure in-memory sweep over the
//     already-resolved config sections — no socket, no dependency,
//     microseconds — so `ServerConfig.SkipPreflight` has nothing to
//     usefully skip, and booting a composition whose required knobs are
//     unbound is not a choice an emergency boot is asking for.
//   * `component-secret-requirements` is **external-probe class**
//     (deliberately UNMARKED, hence skippable). It calls
//     `ISecretStore.GetSecret`, and the composed store may be a remote
//     vault companion (Azure Key Vault, a cloud secret manager) that can
//     be slow or down. That is precisely the dependency-outage case
//     `SkipPreflight` exists to ride through, so marking it structural
//     would take an emergency-boot lever away for a probe that can block.
//     It is not marked security-class either: it reads no secret VALUE
//     and enforces no auth invariant — its bypass costs an early warning,
//     never a hole. (In-process stores make the probe microseconds, but a
//     validator's class must be correct for the worst store a deployment
//     can compose, not the default one.)
//
// **The redaction property is structural, not a convention.** The probe's
// return type is `Async<bool>`; the value `GetSecret` yields is tested for
// blankness inside the probe and never leaves it. No requirement type has
// a field a value could occupy, and every report string is built from
// `SecretRequirement.describe` (scope + key + class). There is therefore
// no path by which a value reaches a report or a log — asserted by the
// test pack with a planted sentinel, not merely intended.

/// Existence check over a secret store: `scope -> key -> present`.
/// Deliberately `Async<bool>` — the probe is the ONLY place a secret's
/// value is read, and it converts to a boolean before returning, so no
/// caller can hold one. Injected (rather than the store itself) so the
/// checks are pure over a testable view and a deployment can substitute a
/// cheaper existence primitive if its store has one.
type SecretPresenceProbe = string -> string -> Async<bool>

/// A missing requirement, as data: which component, which requirement
/// (its NAME and class — never its value), and how bad. `DefectError`
/// aborts startup; `DefectWarning` logs and continues, mirroring the
/// Phase 281 `CompositionDefect` split the aggregator already understands.
type RequirementDefect = {
    /// The component that requires it.
    Component: ComponentId
    /// `secret` or `config`.
    Kind: string
    /// The requirement's name — a secret's `scope/key` or a knob's path.
    /// Never a value.
    Requirement: string
    /// The requirement's class / type token (`api-key`, `int`, …).
    Class: string
    Severity: CompositionDefectSeverity
    /// The operator-facing line, already naming the component + the
    /// requirement + what to do about it.
    Message: string
}

/// Stable `IConfigValidator.Name` for the required-knob binding check.
/// Structural-class (Phase 585) — `SkipPreflight` does NOT bypass it.
[<Literal>]
let ConfigValidatorName = "component-config-requirements"

/// Stable `IConfigValidator.Name` for the required-secret presence probe.
/// External-probe class — unmarked, so `SkipPreflight` skips it like any
/// other companion probe that reaches a dependency which may be down.
[<Literal>]
let SecretValidatorName = "component-secret-requirements"

// ── 432.B — derivation from the Phase 289 config binding ──────────────

/// Best-effort knob-type inference from a declared default. A Phase 289
/// section is a `Map<string,string>`, so the type is not declared — it is
/// read off the default's shape. Deterministic and total; anything it
/// cannot recognise is `StringKnob`, and a knob with no default to read
/// yields `OpaqueKnob` rather than a guess. A component that wants an
/// exact type declares the requirement instead, and the declaration wins
/// at merge.
let inferKnobType (declaredValue: string) : ConfigKnobType =
    if String.IsNullOrWhiteSpace declaredValue then
        OpaqueKnob
    else
        let trimmed = declaredValue.Trim()

        let isBool =
            String.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
            || String.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)

        if isBool then
            BoolKnob
        elif fst (Int64.TryParse trimmed) then
            IntKnob
        elif trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) then
            UriKnob
        elif trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) then
            UriKnob
        else
            StringKnob

/// Derive a component's config requirements from its declared Phase 289
/// section: one `ConfigRequirement` per declared key, typed by inference
/// from the declared default. A key whose declared value is BLANK carries
/// no default — the section declares the knob exists but leaves the value
/// to the deployment — so it derives as `NoKnobDefault` and the presence
/// preflight requires it to be bound (by an id-scoped
/// `TOOLUP_COMPONENT__<id>__<key>` override). A key with a real declared
/// value always binds, so it is never a preflight failure.
///
/// Derivation over declaration: nothing is hand-listed here, so the
/// requirement set cannot drift from what the component actually binds.
let fromComponentConfig (section: ComponentConfig) : ComponentRequirements =
    let config =
        section.Values
        |> Map.toList
        |> List.sortBy fst
        |> List.map (fun (key, declared) ->
            if String.IsNullOrWhiteSpace declared then
                {
                    Path = key
                    KnobType = OpaqueKnob
                    Default = NoKnobDefault
                    Purpose =
                        "declared by the component's config section with no default — the deployment must supply it"
                }
            else
                {
                    Path = key
                    KnobType = inferKnobType declared
                    Default = KnobDefault declared
                    Purpose = "declared by the component's config section"
                })

    {
        Component = section.Component
        Secrets = []
        Config = config
    }

/// Derive a `RequirementsSignature` from every declared Phase 289 config
/// section — the derived half of 432.B, before any declaration is folded
/// on top.
let fromComponentConfigs (sections: ComponentConfig list) : RequirementsSignature =
    sections |> List.map fromComponentConfig |> ComponentRequirements.signatureOf

/// The 432.B fold: derive what can be derived from the config bindings,
/// then merge the residual DECLARED requirements (a companion's secret
/// requirements, or an exact knob type it wants to pin) on top.
/// Declaration wins on a colliding key / path, because it carries the
/// real class and purpose derivation could only infer.
let derive (sections: ComponentConfig list) (declared: RequirementsSignature) : RequirementsSignature =
    ComponentRequirements.mergeSignature (fromComponentConfigs sections) declared

// ── 432.C — presence checks ───────────────────────────────────────────

/// A `SecretPresenceProbe` over a composed `ISecretStore`. The value
/// `GetSecret` returns is tested for blankness (a stored-but-blank secret
/// is treated as absent — it satisfies nothing) and immediately
/// discarded: the probe returns a boolean and retains nothing, so no
/// caller can reach a value.
let probeOf (store: ISecretStore) : SecretPresenceProbe =
    fun scope key -> async {
        let! resolved = store.GetSecret(scope, key)
        return resolved |> Option.exists (String.IsNullOrWhiteSpace >> not)
    }

/// A probe that reports every secret absent — the honest posture for a
/// deployment with no secret store composed at all. Every required secret
/// then fails preflight naming its component, which is the correct report:
/// the credentials genuinely cannot resolve.
let unavailableProbe: SecretPresenceProbe = fun _ _ -> async { return false }

/// Whether a resolved config section binds a non-blank value at `path`.
/// The section passed here is the ALREADY-RESOLVED one (declared defaults
/// merged with id-scoped env overrides by `ComponentConfigResolver
/// .resolve`), so this is the final bound state the component will read.
let private bindsKnob (resolved: ComponentConfig list) (componentId: ComponentId) (path: string) : bool =
    resolved
    |> List.tryFind (fun section -> section.Component = componentId)
    |> Option.bind (ComponentConfig.tryValue path)
    |> Option.exists (String.IsNullOrWhiteSpace >> not)

/// The required-knob defects: every `NoKnobDefault` requirement whose
/// component's resolved section does not bind a non-blank value. Pure and
/// in-process — this is what makes the config validator structural-class.
let configDefects (resolved: ComponentConfig list) (signature: RequirementsSignature) : RequirementDefect list =
    ComponentRequirements.all signature
    |> List.collect (fun reqs ->
        ComponentRequirements.requiredConfig reqs
        |> List.filter (fun knob -> not (bindsKnob resolved reqs.Component knob.Path))
        |> List.map (fun knob -> {
            Component = reqs.Component
            Kind = "config"
            Requirement = knob.Path
            Class = ConfigKnobType.toWireString knob.KnobType
            Severity = DefectError
            Message =
                sprintf
                    "Component '%s' requires config knob %s, which binds no value. Purpose: %s. Supply it as a declared default on the component's config section, or set the id-scoped override %s."
                    (ComponentId.value reqs.Component)
                    (ConfigRequirement.describe knob)
                    knob.Purpose
                    (ComponentConfig.envVarName reqs.Component knob.Path)
        }))

/// The secret defects: every declared secret requirement whose key does
/// not resolve in the store. A `RequiredRequirement` miss is a
/// `DefectError` (startup aborts); an `OptionalRequirement` miss is a
/// `DefectWarning` (the component degrades and the operator is told).
/// Probes run in parallel — a deployment with a remote vault should not
/// pay one round-trip per requirement serially inside the aggregator's
/// budget.
let secretDefects (probe: SecretPresenceProbe) (signature: RequirementsSignature) : Async<RequirementDefect list> = async {
    let pending =
        ComponentRequirements.all signature
        |> List.collect (fun reqs -> reqs.Secrets |> List.map (fun secret -> reqs.Component, secret))

    let! results =
        pending
        |> List.map (fun (componentId, secret) -> async {
            let! present = probe secret.Scope secret.Key
            return componentId, secret, present
        })
        |> Async.Parallel

    return
        results
        |> Array.toList
        |> List.filter (fun (_, _, present) -> not present)
        |> List.map (fun (componentId, secret, _) ->
            let severity =
                if SecretRequirement.isRequired secret then
                    DefectError
                else
                    DefectWarning

            let consequence =
                if SecretRequirement.isRequired secret then
                    "The component cannot function without it, so startup is aborted rather than failing mid-request."
                else
                    "The requirement is optional — the component degrades and startup continues."

            {
                Component = componentId
                Kind = "secret"
                Requirement = secret.Scope + "/" + secret.Key
                Class = SecretClass.toWireString secret.Class
                Severity = severity
                Message =
                    sprintf
                        "Component '%s' requires secret %s, which does not resolve in the composed ISecretStore. Purpose: %s. %s Provision the key under that scope in whichever secret store this deployment composes."
                        (ComponentId.value componentId)
                        (SecretRequirement.describe secret)
                        secret.Purpose
                        consequence
            })
}

/// Render a defect list into one preflight message, each line prefixed
/// with its kind so an operator can tell a missing credential from an
/// unbound knob at a glance. Built only from `Message`, which is itself
/// built only from names + classes.
let private renderDefects (defects: RequirementDefect list) : string =
    defects
    |> List.map (fun d -> sprintf "[%s] %s" d.Kind d.Message)
    |> String.concat "\n"

/// Translate a defect list into a `ValidationResult`: any `DefectError`
/// aborts (`Error`), a clean sweep is `Ok`, warnings-only surface as
/// `Warning` (an optional requirement missing is a warning, never a
/// failure).
let toValidationResult (defects: RequirementDefect list) : ValidationResult =
    let errors = defects |> List.filter (fun d -> d.Severity = DefectError)

    if not errors.IsEmpty then
        Error(renderDefects errors)
    else
        match defects with
        | [] -> Ok
        | warnings -> Warning(renderDefects warnings)

/// The **structural-class** validator: every required config knob binds.
/// A pure in-memory sweep over the already-resolved sections, so
/// `ServerConfig.SkipPreflight` does not bypass it (Phase 585) — an
/// emergency boot loses nothing by running it, and booting a composition
/// whose required knobs are unbound is not what that lever is for.
type ComponentConfigRequirementsValidator(resolved: ComponentConfig list, signature: RequirementsSignature) =
    interface IConfigValidator with
        member _.Name = ConfigValidatorName
        member _.Timeout = IConfigValidator.defaultTimeout
        member _.Validate() = async { return toValidationResult (configDefects resolved signature) }

    interface IStructuralClassValidator

/// The **external-probe class** validator: every required secret resolves
/// in the composed `ISecretStore`. Deliberately unmarked, so
/// `SkipPreflight` skips it — the store may be a remote vault whose
/// outage is exactly what that lever exists to ride through. It reads no
/// secret VALUE and enforces no auth invariant, so its bypass costs an
/// early warning, never a hole.
type ComponentSecretRequirementsValidator(probe: SecretPresenceProbe, signature: RequirementsSignature) =
    interface IConfigValidator with
        member _.Name = SecretValidatorName
        member _.Timeout = IConfigValidator.defaultTimeout

        member _.Validate() = async {
            let! defects = secretDefects probe signature
            return toValidationResult defects
        }

/// Every requirement defect in one sweep — the typed report the two
/// validators split between them. Offered as a single call for tooling
/// (a deploy preflight CLI, a `/dev/inspect` panel) that wants the whole
/// picture rather than the two class-separated halves.
let check
    (probe: SecretPresenceProbe)
    (resolved: ComponentConfig list)
    (signature: RequirementsSignature)
    : Async<RequirementDefect list> =
    async {
        let! secrets = secretDefects probe signature
        return configDefects resolved signature @ secrets
    }

/// The opt-in registration (432.C): folds the requirement validators into
/// the Phase 9m aggregator so they run at the same startup gate as every
/// other `IConfigValidator`. Returned as an
/// `IServiceCollection -> IServiceCollection` closure so a composition
/// root folds it into the extension `ServiceConfig` hook exactly the way
/// it registers the Phase 281 well-formedness validator.
///
/// **Each half is registered only when something requires it** (GP 13):
/// a signature with no secret requirements registers no probe, one with no
/// required knobs registers no knob check, and an EMPTY signature —
/// including the `ServerApp.empty |> ServerApp.run` base case — registers
/// nothing at all, leaving `services` byte-for-byte what it was (GP 11).
let serviceRegistration
    (probe: SecretPresenceProbe)
    (resolved: ComponentConfig list)
    (signature: RequirementsSignature)
    : IServiceCollection -> IServiceCollection =
    fun services ->
        if ComponentRequirements.anyRequiredConfig signature then
            services.AddSingleton<IConfigValidator>(
                ComponentConfigRequirementsValidator(resolved, signature) :> IConfigValidator
            )
            |> ignore

        if ComponentRequirements.anySecrets signature then
            services.AddSingleton<IConfigValidator>(
                ComponentSecretRequirementsValidator(probe, signature) :> IConfigValidator
            )
            |> ignore

        services

/// `serviceRegistration` over a composed `ISecretStore` — the production
/// shape. Pass the store the deployment composed; the probe reads
/// existence only.
let serviceRegistrationWithStore
    (store: ISecretStore)
    (resolved: ComponentConfig list)
    (signature: RequirementsSignature)
    : IServiceCollection -> IServiceCollection =
    serviceRegistration (probeOf store) resolved signature