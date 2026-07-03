// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform.ConfigValidation

// ─── Composition well-formedness preflight (CompositionValidator) ────
//
// Fail fast, at compose time, on a *malformed composition* — before the
// app serves traffic. Forge already enforces individual structural
// invariants at startup (the authorization classifier refuses to boot on
// an unclassified API method; the `IConfigValidator` preflight aggregator
// runs registered validators; a duplicate audit-sink `Name` is a fatal
// compose error). This validator generalises that posture into a first-
// party `IConfigValidator`-family check over the `CompositionManifest`
// (Phase 280) — the machine-readable enumeration of *what was actually
// composed*, keyed by stable `ComponentId` (Phase 279). Because it reads
// the manifest (derived from the live registry, never a hand-declared
// list), it cannot drift from what the deployment really composed.
//
// **Rules as declared data (Phase 294-ready).** The rule set is a single
// `rules` list of `CompositionRule` records — each a rule code + severity
// + description + a pure evaluator. The runtime check is generated from
// that list; Phase 294 exposes the same list as an introspectable
// `ruleManifest` so an external pre-build checker validates against
// forge's own rules with no re-encoding and no divergence.
//
// **GP 11 / GP 13.** The check is a pure in-memory sweep over a handful
// of component entries; a well-formed composition yields no defects and
// the validator returns `Ok` (logged at Info like every preflight probe).
// It is registered as a non-security-class validator, so the existing
// `ServerConfig.SkipPreflight` emergency-boot lever bypasses it — no new
// always-on gate a deployment cannot switch off.
//
// **Generic substrate (GP 1).** Zero vendor / domain / tree-language
// vocabulary: the rules read only `ComponentId`, `ComponentKind`,
// display labels, and declared tool→module reference edges.

/// Severity of a composition well-formedness defect. Mirrors the
/// `ValidationResult` Error / Warning split the aggregator understands:
/// `DefectError` aborts startup, `DefectWarning` logs and continues.
type CompositionDefectSeverity =
    | DefectError
    | DefectWarning

/// One structural defect found in a composed application: the stable rule
/// code that flagged it (Phase 294 exports these), its severity, and a
/// human-readable, alternative-enumerating message.
type CompositionDefect = {
    RuleCode: string
    Severity: CompositionDefectSeverity
    Message: string
}

/// Cross-reference edges the bare `CompositionManifest` does not itself
/// carry: which module each tool declares as its owning `SourceModule`.
/// Supplied alongside the manifest so the orphaned-reference rule can
/// resolve a tool's declared owner against the registered module set.
/// Empty (the base case — a deployment that composes no AI tools) makes
/// that rule a no-op (GP 13).
type CompositionReferences = {
    /// Tool display `Name` × its declared `AIToolDefinition.SourceModule`.
    ToolSources: (string * string) list
}

module CompositionReferences =
    /// The reference set of a composition that declares no tool→module
    /// edges — the reference rules degrade to no-ops against it.
    let empty: CompositionReferences = { ToolSources = [] }

/// Composition well-formedness rule set + the `IConfigValidator` that runs
/// it at preflight. The rules are declared data (`rules`); the runtime
/// check and — from Phase 294 — the introspectable manifest both read the
/// same list, so they cannot diverge.
module CompositionValidator =

    /// Stable `IConfigValidator.Name` for the composition well-formedness
    /// check. Non-security-class, so `SkipPreflight` bypasses it.
    [<Literal>]
    let ValidatorName = "composition-well-formedness"

    /// A single declared well-formedness rule. `Evaluate` is pure: it maps
    /// the composed surface (+ its reference edges) to zero or more defect
    /// messages, each already alternative-enumerating. The runtime check
    /// tags every message with this rule's `Code` + `Severity`; Phase 294's
    /// `ruleManifest` projects `Code` / `Severity` / `Description` for
    /// external introspection — one definition, two readers.
    type CompositionRule = {
        Code: string
        Severity: CompositionDefectSeverity
        Description: string
        Evaluate: CompositionManifest -> CompositionReferences -> string list
    }

    /// A reserved / platform-owned `SourceModule` is not a consumer module
    /// and is exempt from the orphaned-reference rule: an empty string, a
    /// `_`-prefixed reserved source (`_platform.*`, `_scheduling`,
    /// `_sample`, …), or the SDK's own `ToolUp.Platform`. Mirrors the
    /// `ServerApp.componentIdForModule` contract (it returns `None` for
    /// exactly these).
    let private isReservedSource (source: string) : bool =
        String.IsNullOrWhiteSpace source
        || source.StartsWith "_"
        || source = "ToolUp.Platform"

    /// Human label for a component kind, used in defect messages.
    let private kindLabel (kind: ComponentKind) : string =
        match kind with
        | ModuleComponent -> "module"
        | CompanionComponent -> "companion"
        | DataTypeComponent -> "datatype"
        | ToolComponent -> "tool"

    // ── Rule evaluators (pure) ──────────────────────────────────────────

    /// Global identity uniqueness: every composed unit — module, companion
    /// slot / impl, datatype, tool — must resolve to a distinct
    /// `ComponentId`. A collision breaks every introspection / telemetry-
    /// correlation surface that keys on the id.
    let private evalDuplicateComponentId (manifest: CompositionManifest) (_: CompositionReferences) : string list =
        CompositionManifest.allComponents manifest
        |> List.groupBy _.Id
        |> List.choose (fun (id, entries) ->
            if List.length entries > 1 then
                let colliders =
                    entries
                    |> List.map (fun e -> sprintf "%s '%s'" (kindLabel e.Kind) e.Label)
                    |> String.concat ", "

                Some(
                    sprintf
                        "Duplicate ComponentId '%s' shared by %d composed units: %s. Every composed unit must resolve to a unique identity — declare an explicit id on the colliding unit's registration surface (ServerModule.withComponentId for a module; a distinct DataType.Id / AIToolDefinition.Name / companion sub-id otherwise) to disambiguate."
                        (ComponentId.value id)
                        (List.length entries)
                        colliders
                )
            else
                None)

    /// Slot-local addressability: within a single multi-impl companion slot
    /// (audit sinks, notification sinks, health checks, config validators,
    /// smoke tests) every implementation must declare a distinct sub-id
    /// (its `Name` / `Kind`), so it is addressable by its stable
    /// `ComponentId` and never by list position (Phase 279 rule). This is
    /// the slot-local complement to the global duplicate-id rule — it names
    /// the offending slot and enumerates its registered sub-ids so the
    /// operator sees exactly which impl to rename.
    let private evalCompanionSlotLegality (manifest: CompositionManifest) (_: CompositionReferences) : string list =
        manifest.CompanionSlots
        |> List.choose (fun e -> e.Impl |> Option.map (fun sub -> e.Label, sub))
        |> List.groupBy fst
        |> List.collect (fun (slot, pairs) ->
            let subIds = pairs |> List.map snd

            subIds
            |> List.countBy id
            |> List.choose (fun (sub, n) ->
                if n > 1 then
                    Some(
                        sprintf
                            "Companion slot '%s' has %d implementations sharing sub-id '%s'. Each implementation in a multi-impl slot must declare a unique Name/Kind so its ComponentId is addressable (identity is never positional). Registered sub-ids in this slot: %s. Rename the duplicate so every implementation is distinct."
                            slot
                            n
                            sub
                            (subIds |> List.map (sprintf "'%s'") |> String.concat ", ")
                    )
                else
                    None))

    /// Orphaned tool reference: a tool whose declared `SourceModule` names a
    /// consumer module that was never composed. Reserved / platform-owned
    /// sources (`_platform.*`, `ToolUp.Platform`, empty) are exempt — they
    /// intentionally resolve to no registered module. Every other
    /// `SourceModule` must match a registered module's display name.
    let private evalOrphanedToolReference (manifest: CompositionManifest) (refs: CompositionReferences) : string list =
        let moduleNames = manifest.Modules |> List.map _.Label |> Set.ofList

        refs.ToolSources
        |> List.filter (fun (_, source) -> not (isReservedSource source))
        |> List.filter (fun (_, source) -> not (moduleNames.Contains source))
        |> List.map (fun (toolName, source) ->
            let alternatives =
                if moduleNames.IsEmpty then
                    "no modules are composed"
                else
                    moduleNames |> Set.toList |> List.map (sprintf "'%s'") |> String.concat ", "

            sprintf
                "Tool '%s' declares SourceModule '%s', which resolves to no registered module. A tool's SourceModule must name a composed module (or a reserved '_'-prefixed platform source). Registered modules: %s. Register the owning module, correct the SourceModule, or use a reserved platform source."
                toolName
                source
                alternatives)

    /// The declared rule set — the single source of truth the runtime check
    /// is generated from and (Phase 294) the introspectable manifest
    /// projects. Adding a rule here extends both readers in lock-step.
    let rules: CompositionRule list = [
        {
            Code = "duplicate-component-id"
            Severity = DefectError
            Description =
                "Every composed unit (module, companion slot / impl, datatype, tool) must resolve to a unique ComponentId."
            Evaluate = evalDuplicateComponentId
        }
        {
            Code = "companion-slot-legality"
            Severity = DefectError
            Description =
                "Within a single multi-impl companion slot, every implementation must declare a unique sub-id (Name / Kind) so it is addressable by ComponentId, never by position."
            Evaluate = evalCompanionSlotLegality
        }
        {
            Code = "orphaned-tool-reference"
            Severity = DefectError
            Description =
                "A tool's declared SourceModule must resolve to a registered module or a reserved platform source."
            Evaluate = evalOrphanedToolReference
        }
    ]

    /// Run every rule against a composed surface + its reference edges,
    /// returning the flat, rule-tagged defect list. Pure.
    let checkWith (refs: CompositionReferences) (manifest: CompositionManifest) : CompositionDefect list =
        rules
        |> List.collect (fun rule ->
            rule.Evaluate manifest refs
            |> List.map (fun message -> {
                RuleCode = rule.Code
                Severity = rule.Severity
                Message = message
            }))

    /// `checkWith` against the empty reference set — for a composition that
    /// declares no tool→module edges (the base case).
    let check (manifest: CompositionManifest) : CompositionDefect list =
        checkWith CompositionReferences.empty manifest

    /// Render a defect list into one preflight message. Each defect is
    /// prefixed with its rule code so the operator can grep the offending
    /// invariant.
    let private renderDefects (defects: CompositionDefect list) : string =
        defects
        |> List.map (fun d -> sprintf "[%s] %s" d.RuleCode d.Message)
        |> String.concat "\n"

    /// Translate a defect list into a `ValidationResult`: any `DefectError`
    /// aborts (`Error`); a defect-free composition is `Ok`; warnings-only
    /// surface as `Warning`.
    let toValidationResult (defects: CompositionDefect list) : ValidationResult =
        let errors = defects |> List.filter (fun d -> d.Severity = DefectError)

        if not errors.IsEmpty then
            Error(renderDefects errors)
        else
            match defects with
            | [] -> Ok
            | warnings -> Warning(renderDefects warnings)

    /// The first-party `IConfigValidator` that runs the well-formedness
    /// rules over the composed manifest at preflight. Non-security-class:
    /// `SkipPreflight` bypasses it (the emergency-boot lever), so it adds
    /// no always-on gate a deployment cannot switch off (GP 11).
    type CompositionWellFormednessValidator(manifest: CompositionManifest, refs: CompositionReferences) =
        interface IConfigValidator with
            member _.Name = ValidatorName
            member _.Timeout = IConfigValidator.defaultTimeout
            member _.Validate() = async { return toValidationResult (checkWith refs manifest) }

    /// A `services` registration that adds the well-formedness validator so
    /// the Phase 9m aggregator runs it alongside every companion
    /// `IConfigValidator`. Returned as an `IServiceCollection ->
    /// IServiceCollection` closure so the composition root folds it into
    /// the extension `ServiceConfig` hook the same way it registers other
    /// on-demand companions. Built from the live manifest + tool-reference
    /// edges, so it checks exactly what was composed.
    let serviceRegistration
        (manifest: CompositionManifest)
        (refs: CompositionReferences)
        : IServiceCollection -> IServiceCollection =
        fun services ->
            services.AddSingleton<IConfigValidator>(
                CompositionWellFormednessValidator(manifest, refs) :> IConfigValidator
            )