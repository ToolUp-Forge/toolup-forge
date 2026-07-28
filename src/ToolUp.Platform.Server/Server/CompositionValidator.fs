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
// **Rule classes (Phase 585).** Every rule is declared in exactly one of
// two lists — `structuralRules` (in-process identity / integrity
// invariants; no external dependency; microseconds) and
// `externalProbeRules` (anything that reaches a dependency which may be
// down). `rules` is their concatenation, so a rule's class is fixed by
// *where it is declared* and cannot be misdeclared or drift. Each class
// registers its own `IConfigValidator`: the structural one carries the
// `IStructuralClassValidator` marker and therefore runs even under
// `ServerConfig.SkipPreflight`, while an external-probe one (none ship
// today) stays unmarked and skippable. Before Phase 585 both rode the
// single `SkipPreflight` switch, so an emergency boot taken to ride out a
// storage-sentinel outage silently disabled `duplicate-component-id`,
// `companion-slot-legality`, and `orphaned-tool-reference` as well —
// booting an app whose composed identities collide, which is not a choice
// any operator was asking for. `classifiedRuleManifest` exports the class
// alongside the Phase 294 descriptor fields so an external checker knows
// which invariants are unconditional.
//
// **GP 11 / GP 13.** The check is a pure in-memory sweep over a handful
// of component entries; a well-formed composition yields no defects and
// the validator returns `Ok` (logged at Info like every preflight probe).
// The default path (`SkipPreflight = false`) is byte-for-byte what it was
// — the same single validator, over the same rules, in the same order.
// Only the `SkipPreflight = true` path changes, and only by no longer
// switching off checks that cost nothing to run.
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

/// The introspectable projection of one well-formedness rule (Phase 294):
/// its stable code, severity, and description — everything an external
/// pre-build checker needs to validate a composition against forge's *own*
/// rules, without the executable predicate. Exposed as `ruleManifest`,
/// projected from the same `rules` list the runtime check runs, so the two
/// cannot diverge — one source of truth for "well-formed app," checkable at
/// authoring time (by the external tool) and at compose time (by forge).
type CompositionRuleDescriptor = {
    Code: string
    Severity: CompositionDefectSeverity
    Description: string
}

/// Phase 585 — the security classification of a well-formedness rule: is
/// it a structural invariant that must hold no matter how the deployment
/// booted, or a probe whose dependency an emergency boot may need to ride
/// past?
///
/// * `StructuralRule` — a pure in-process identity / integrity check over
///   the composed surface. No socket, no external dependency,
///   microseconds. Runs unconditionally: `ServerConfig.SkipPreflight`
///   does not and must not switch it off, because a composition whose
///   component identities collide is broken in a way no outage explains.
/// * `ExternalProbeRule` — reaches something outside the process, so it
///   can fail for reasons that have nothing to do with the composition
///   being malformed. Skippable under `SkipPreflight`, which is exactly
///   what that lever exists for.
///
/// The class is fixed by which declared list a rule appears in
/// (`CompositionValidator.structuralRules` / `externalProbeRules`), never
/// by a field a rule author could set wrongly.
type CompositionRuleClass =
    | StructuralRule
    | ExternalProbeRule

/// Phase 585 — the class-carrying projection of one well-formedness rule:
/// the Phase 294 descriptor fields plus the rule's `Class`, so an external
/// pre-build checker can tell which of forge's invariants a deployment can
/// never switch off (and therefore must satisfy before it will boot at
/// all) from those an emergency boot may skip. Exposed as
/// `CompositionValidator.classifiedRuleManifest`.
///
/// A separate record rather than a fourth field on
/// `CompositionRuleDescriptor`: adding a field to an F# record changes its
/// constructor signature, which is a breaking change for every consumer
/// that constructs one (and a removal under the public-API baseline gate).
/// Both projections read the same declared rule lists, so they cannot
/// diverge.
type ClassifiedCompositionRule = {
    Code: string
    Severity: CompositionDefectSeverity
    Description: string
    Class: CompositionRuleClass
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
    /// Phase 594 — the data-vocabulary packs this deployment pins
    /// (`ServerConfig.PinnedVocabularyPacks`). Empty (the base case — a
    /// deployment that pins nothing) makes both vocabulary rules no-ops
    /// (GP 13).
    PinnedVocabularyPacks: DataVocabularyPack list
    /// Phase 594 — the deployment's declared data-type schemas
    /// (`ServerConfig.DeclaredDataSchemas`), keyed by `TypeName`. The
    /// schema-drift rule compares each against the governing pinned pack
    /// entry; empty makes that rule a no-op. The squatting rule needs no
    /// declaration — it reads the registered names off the manifest.
    DataSchemas: VocabularyEntry list
}

module CompositionReferences =
    /// The reference set of a composition that declares no tool→module
    /// edges and pins no vocabulary — the reference rules degrade to no-ops
    /// against it.
    let empty: CompositionReferences = {
        ToolSources = []
        PinnedVocabularyPacks = []
        DataSchemas = []
    }

/// Composition well-formedness rule set + the `IConfigValidator` that runs
/// it at preflight. The rules are declared data (`rules`); the runtime
/// check and — from Phase 294 — the introspectable manifest both read the
/// same list, so they cannot diverge.
module CompositionValidator =

    /// Stable `IConfigValidator.Name` for the composition well-formedness
    /// check — the structural-class rules. Structural-class (Phase 585),
    /// so `SkipPreflight` does NOT bypass it.
    [<Literal>]
    let ValidatorName = "composition-well-formedness"

    /// Phase 585 — stable `IConfigValidator.Name` for the external-probe
    /// class of composition rules. Unmarked, so `SkipPreflight` skips it
    /// like any other companion probe. No rule ships in this class today,
    /// so the validator is not registered at all (GP 13) — the name is
    /// declared here so the first such rule inherits a stable identity
    /// rather than inventing one.
    [<Literal>]
    let ExternalProbeValidatorName = "composition-well-formedness (external probes)"

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

    /// Phase 594 — the distinct registered data-type names in a
    /// composition: the manifest's `DataType` labels (its `DataType.Id`s)
    /// unioned with any `DeclaredDataSchemas` `TypeName`. This is the
    /// name universe the vocabulary rules govern.
    let private registeredDataTypeNames (manifest: CompositionManifest) (refs: CompositionReferences) : string list =
        (manifest.DataTypes |> List.map _.Label)
        @ (refs.DataSchemas |> List.map _.TypeName)
        |> List.distinct

    /// Phase 594 — vocabulary namespace squatting: a registered data type
    /// whose name falls under a pinned pack's governed namespace but which
    /// the pack declares no entry for. A pinned pack governs its namespace
    /// as a closed set, so a governed name the pack does not sanction is a
    /// contradiction — the name means nothing agreed. Names outside every
    /// pinned namespace are unaffected (GP 13). Empty pin set ⇒ no-op.
    let private evalVocabularyTypeNameUnknown
        (manifest: CompositionManifest)
        (refs: CompositionReferences)
        : string list =
        let names = registeredDataTypeNames manifest refs

        refs.PinnedVocabularyPacks
        |> List.collect (fun pack ->
            names
            |> List.filter (fun name -> DataVocabulary.governs pack name && (DataVocabulary.tryEntry pack name).IsNone)
            |> List.map (fun name ->
                let entries =
                    match pack.Entries with
                    | [] -> "none"
                    | es -> es |> List.map (fun e -> sprintf "'%s'" e.TypeName) |> String.concat ", "

                sprintf
                    "Data type '%s' falls under pinned vocabulary pack '%s' (namespace '%s', version %s) but the pack declares no entry for it. A pinned pack governs its namespace as a closed set — declare '%s' in a new pack version, rename the data type out of the '%s' namespace, or unpin the pack. Pack entries: %s."
                    name
                    pack.Id
                    pack.Namespace
                    (DataVocabulary.versionString pack.Version)
                    name
                    pack.Namespace
                    entries))

    /// Phase 594 — vocabulary schema drift: a `DeclaredDataSchemas` schema
    /// whose `TypeName` matches a pinned pack entry but whose fields /
    /// value-types / units diverge from it. The pinned pack is the shared
    /// meaning of the name, so a declared schema that contradicts it fails,
    /// naming the pack entry + the specific drift. Empty declared-schema set
    /// or empty pin set ⇒ no-op (GP 13).
    let private evalVocabularySchemaMismatch (_: CompositionManifest) (refs: CompositionReferences) : string list =
        refs.PinnedVocabularyPacks
        |> List.collect (fun pack ->
            refs.DataSchemas
            |> List.choose (fun declared ->
                match DataVocabulary.tryEntry pack declared.TypeName with
                | Some packEntry ->
                    match DataVocabulary.schemaDrift declared packEntry with
                    | [] -> None
                    | drifts ->
                        Some(
                            sprintf
                                "Data type '%s' contradicts pinned vocabulary pack '%s' entry '%s' (version %s): %s. The pinned pack is the shared meaning of this name — align the data type's schema to the pack entry, or pin a pack version whose '%s' entry matches."
                                declared.TypeName
                                pack.Id
                                packEntry.TypeName
                                (DataVocabulary.versionString pack.Version)
                                (String.concat "; " drifts)
                                declared.TypeName
                        )
                | None -> None))

    /// Phase 585 — the structural-class rules: pure in-process identity /
    /// integrity invariants over the composed surface. Every rule shipped
    /// to date is one. Declaring a rule here IS its classification —
    /// there is no class field to set wrongly — and it makes the rule
    /// unconditional, so the bar for adding one is that it must be
    /// correct and fast on a machine with every external dependency
    /// unreachable. A rule that can block belongs in
    /// `externalProbeRules`, however important it is.
    let structuralRules: CompositionRule list = [
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
        {
            Code = "vocabulary-typename-unknown"
            Severity = DefectError
            Description =
                "A registered data type whose name falls under a pinned data-vocabulary pack's namespace must match a declared pack entry (a closed set); a governed name the pack does not sanction squats."
            Evaluate = evalVocabularyTypeNameUnknown
        }
        {
            Code = "vocabulary-schema-mismatch"
            Severity = DefectError
            Description =
                "A declared data-type schema whose TypeName matches a pinned data-vocabulary pack entry must not drift from it in fields, value-types, or units."
            Evaluate = evalVocabularySchemaMismatch
        }
    ]

    /// Phase 585 — the external-probe class: rules that reach a dependency
    /// outside the process and can therefore fail for reasons unrelated to
    /// the composition being malformed. Empty today, and deliberately so —
    /// every shipped invariant is answerable from the manifest already in
    /// memory. A rule declared here is skipped under
    /// `ServerConfig.SkipPreflight`, which is the correct treatment for a
    /// probe whose dependency an emergency boot is riding past.
    let externalProbeRules: CompositionRule list = []

    /// The declared rule set — the single source of truth the runtime check
    /// is generated from and (Phase 294) the introspectable manifest
    /// projects. Adding a rule to either class list extends every reader in
    /// lock-step. Structural first, so the order the runtime check and the
    /// manifest enumerate is stable.
    let rules: CompositionRule list = structuralRules @ externalProbeRules

    /// Phase 585 — the class of a rule, by code. `None` for a code this
    /// build does not ship (an external checker holding a stale rule set
    /// gets an honest "unknown", never a wrong classification).
    let tryRuleClass (code: string) : CompositionRuleClass option =
        if structuralRules |> List.exists (fun r -> r.Code = code) then
            Some StructuralRule
        elif externalProbeRules |> List.exists (fun r -> r.Code = code) then
            Some ExternalProbeRule
        else
            None

    /// Phase 294 — the introspectable rule manifest: every shipped
    /// well-formedness invariant as data (code + severity + description),
    /// projected from the same `rules` list `checkWith` runs. One source of
    /// truth — a rule cannot appear in the runtime check without appearing
    /// here, or vice versa (both read `rules`), so an external pre-build
    /// checker validates against forge's own invariants with no re-encoding
    /// and no drift. Order matches `rules`.
    let ruleManifest: CompositionRuleDescriptor list =
        rules
        |> List.map (fun rule -> {
            Code = rule.Code
            Severity = rule.Severity
            Description = rule.Description
        })

    /// Phase 585 — `ruleManifest` plus each rule's `Class`, so an external
    /// checker can distinguish the invariants a deployment can never switch
    /// off (structural — they hold even under `SkipPreflight`, so a
    /// violation means the app will not boot at all) from those an
    /// emergency boot may skip. Projected from the same two declared lists,
    /// in the same order as `rules` / `ruleManifest`.
    let classifiedRuleManifest: ClassifiedCompositionRule list = [
        for cls, ruleSet in [ StructuralRule, structuralRules; ExternalProbeRule, externalProbeRules ] do
            for rule in ruleSet ->
                {
                    Code = rule.Code
                    Severity = rule.Severity
                    Description = rule.Description
                    Class = cls
                }
    ]

    /// Run a rule list against a composed surface + its reference edges,
    /// returning the flat, rule-tagged defect list. Pure.
    let private evaluateRules
        (ruleSet: CompositionRule list)
        (refs: CompositionReferences)
        (manifest: CompositionManifest)
        : CompositionDefect list =
        ruleSet
        |> List.collect (fun rule ->
            rule.Evaluate manifest refs
            |> List.map (fun message -> {
                RuleCode = rule.Code
                Severity = rule.Severity
                Message = message
            }))

    /// Run every rule against a composed surface + its reference edges,
    /// returning the flat, rule-tagged defect list. Pure.
    let checkWith (refs: CompositionReferences) (manifest: CompositionManifest) : CompositionDefect list =
        evaluateRules rules refs manifest

    /// Phase 585 — `checkWith` restricted to one rule class. The two
    /// class-specific `IConfigValidator`s run through this, so what each
    /// evaluates is exactly what `classifiedRuleManifest` says it does.
    let checkClassWith
        (ruleClass: CompositionRuleClass)
        (refs: CompositionReferences)
        (manifest: CompositionManifest)
        : CompositionDefect list =
        let ruleSet =
            match ruleClass with
            | StructuralRule -> structuralRules
            | ExternalProbeRule -> externalProbeRules

        evaluateRules ruleSet refs manifest

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

    /// The first-party `IConfigValidator` that runs the **structural**
    /// well-formedness rules over the composed manifest at preflight.
    ///
    /// Structural-class (Phase 585): it carries the
    /// `IStructuralClassValidator` marker, so `ServerConfig.SkipPreflight`
    /// does not bypass it. The rules are a pure sweep over component
    /// entries already in memory — no socket, no dependency, microseconds —
    /// so an emergency boot loses nothing by running them, while skipping
    /// them would mean booting a composition whose identities collide. This
    /// is the same posture as the authorization classifier's unconditional
    /// boot refusal, and it is not a new config knob: `SkipPreflight`
    /// remains the only switch, it simply no longer reaches this class.
    type CompositionWellFormednessValidator(manifest: CompositionManifest, refs: CompositionReferences) =
        interface IConfigValidator with
            member _.Name = ValidatorName
            member _.Timeout = IConfigValidator.defaultTimeout

            member _.Validate() = async { return toValidationResult (checkClassWith StructuralRule refs manifest) }

        interface IStructuralClassValidator

    /// Phase 585 — the `IConfigValidator` for the external-probe class of
    /// composition rules. Deliberately unmarked, so `SkipPreflight` skips
    /// it like any other companion probe. Registered only when
    /// `externalProbeRules` is non-empty (it is empty today, so nothing is
    /// registered and a deployment pays nothing — GP 13).
    type CompositionExternalProbeValidator(manifest: CompositionManifest, refs: CompositionReferences) =
        interface IConfigValidator with
            member _.Name = ExternalProbeValidatorName
            member _.Timeout = IConfigValidator.defaultTimeout

            member _.Validate() = async { return toValidationResult (checkClassWith ExternalProbeRule refs manifest) }

    /// A `services` registration that adds the well-formedness validator(s)
    /// so the Phase 9m aggregator runs them alongside every companion
    /// `IConfigValidator`. Returned as an `IServiceCollection ->
    /// IServiceCollection` closure so the composition root folds it into
    /// the extension `ServiceConfig` hook the same way it registers other
    /// on-demand companions. Built from the live manifest + tool-reference
    /// edges, so it checks exactly what was composed.
    ///
    /// One registration per non-empty rule class (Phase 585) — the classes
    /// differ in whether `SkipPreflight` reaches them, and a validator is
    /// classified as a whole, so they cannot share one registration. With
    /// `externalProbeRules` empty the second registration is elided
    /// entirely, leaving the composed `services` byte-for-byte what it was
    /// before the split.
    let serviceRegistration
        (manifest: CompositionManifest)
        (refs: CompositionReferences)
        : IServiceCollection -> IServiceCollection =
        fun services ->
            services.AddSingleton<IConfigValidator>(
                CompositionWellFormednessValidator(manifest, refs) :> IConfigValidator
            )
            |> ignore

            if not externalProbeRules.IsEmpty then
                services.AddSingleton<IConfigValidator>(
                    CompositionExternalProbeValidator(manifest, refs) :> IConfigValidator
                )
                |> ignore

            services