// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform.ConfigValidation

// ─── Phase 433 — data footprint: derivation, composition join, DSR gate ─
//
// The Server-tier half of the data-footprint manifest (the Core file
// carries the footprint TYPES + the pure join / query / diff algebra).
// Three surfaces, mirroring the shape Phase 432 settled on and Phase 431
// repeated:
//
//   1. **Derivation (433.B)** — a registered `DataType` already names its
//      producer: the compose flow accumulates `(moduleName, DataType)`
//      pairs, so the store knows which component registered which type.
//      `ofModules` / `ofApp` turn that into footprint entries with ZERO
//      per-module effort, and a module that registers one more `DataType`
//      surfaces with no change to this file. Composed audit sinks are the
//      second such seam: each one persists audit records under its own
//      `ComponentId.forCompanionImpl "IAuditSink" <name>` identity.
//
//      What derivation CANNOT see is the residual declared half: the
//      classes a companion keeps in its own backing store (no
//      registration names them), and the `ContainsPii` judgement, which
//      is a property of what the data MEANS rather than of how it was
//      registered. Those are declared alongside the companion's Phase 282
//      capability, keyed by the same `ComponentId`, and folded on top —
//      `DataFootprint.mergeSignature` for new classes,
//      `DataFootprint.reclassify` when the declaration supersedes a
//      derived class's PII classification.
//
//   2. **Composition join (433.C)** — `overManifest` restricts a
//      signature to the components the Phase 280 manifest actually
//      enumerates (so a stale declaration for an uncomposed component
//      cannot inflate the surface) and joins them to one
//      `CompositionDataFootprint`. That value answers "what does this
//      composition store, and where?" — queryable by store seam, data
//      class, PII flag, and (back through the signature) by component.
//      The id-keyed `DataFootprint.diff` projects it into the Phase 286
//      golden-file gate, so a component that STARTS persisting PII is a
//      reviewable change rather than a silent one.
//
//   3. **DSR completeness (433.D)** — an opt-in preflight rule: every
//      persisted PII-flagged class must be claimed by a DSR path this
//      deployment actually composes (an `IDataExporter` /
//      `IErasureHandler` name), or carry a declared exemption. A gap is a
//      NAMED finding carrying the class, the components that persist it,
//      and what is missing.
//
//      **A coverage claim, not a behavioural test.** Nothing here invokes
//      a handler or reads a store; the check is that the wiring exists
//      and that every claimed name resolves. Whether a handler erases
//      everything it should is a question for the handler's own tests.
//      This rules out the failure mode that has no test at all: a class
//      nobody remembered to wire up.
//
// **Class (Phase 585): structural.** The check is a pure in-memory sweep
// over sets already derived plus a set of composed handler NAMES captured
// at registration — no socket, no dependency, microseconds — so
// `ServerConfig.SkipPreflight` has nothing useful to skip. At the default
// `DefectWarning` severity the rule cannot block a boot at all; an
// operator who raises it to `DefectError` has deliberately made DSR
// completeness a boot gate, which is precisely the class of invariant an
// emergency boot must not silently switch off.
//
// **Opt-in and zero-cost (GP 11 / GP 13).** Nothing is derived until a
// caller asks, and `serviceRegistration` registers no validator for a
// composition with nothing persisted and nothing claimed — so
// `ServerApp.empty |> ServerApp.run` composes a byte-identical service
// collection.

/// Derivation of a `FootprintSignature` from the live registrations, and
/// the composition-level join over the Phase 280 manifest. Pure over
/// already-collected registrations — nothing is built until a caller asks.
module DataFootprintDerivation =

    /// A module's resolved identity: its explicit `ComponentId` when it
    /// declares one, else the `Name`-derived one (GP 11 — byte-for-byte
    /// the identity every other introspection surface already uses).
    let componentIdOf (serverModule: ServerModule) : ComponentId =
        serverModule.ComponentId
        |> Option.defaultValue (ComponentId.ofModule serverModule.Name)

    /// The class a registered `DataType` contributes: an entity class
    /// named by the type's own stable `Id`, behind `IDataObjectStore`.
    /// `ContainsPii` is `false` — derivation cannot tell whether a column
    /// holds an order quantity or a home address, and a guess in either
    /// direction would be worse than the honest default plus a
    /// declaration (see the `DataClass.ContainsPii` note).
    let dataTypeClass (dataTypeId: string) : DataClass =
        DataClass.create dataTypeId EntityClass DataObjectStoreSeam

    /// The class a composed audit sink contributes: audit records under
    /// the sink's own name, behind the audit seam.
    let auditSinkClass (sinkName: string) : DataClass =
        DataClass.create sinkName AuditRecordClass AuditSinkSeam

    /// The footprint a `(componentId, DataType.Id)` registration derives:
    /// the producing component WRITES the type and leaves it AT REST in
    /// the object store — which is what makes it a data-subject-request
    /// concern. `Reads` is deliberately not derived: whether a producer
    /// reads its own objects back is not visible in the registration, and
    /// claiming it would over-state the footprint.
    let private dataTypeFootprint (componentId: ComponentId) (dataTypeId: string) : DataFootprint =
        let cls = dataTypeClass dataTypeId

        DataFootprint.none componentId
        |> DataFootprint.withWrite cls
        |> DataFootprint.withPersist cls

    /// Derive the footprint of a set of composed modules from their own
    /// `DataTypes` registrations. `attribute` resolves a module to the
    /// identity its accesses are recorded under; the default
    /// `componentIdOf` reproduces the existing convention exactly, and it
    /// is a parameter only so a composition root that keys components
    /// differently (a multi-tenant shell, a test harness) can say so
    /// without this file learning about it.
    ///
    /// Nothing here is a hand-listed table: a module that registers one
    /// more `DataType` surfaces with no change to this function.
    let ofModulesWith (attribute: ServerModule -> ComponentId) (modules: ServerModule list) : FootprintSignature =
        modules
        |> List.collect (fun serverModule ->
            let owner = attribute serverModule

            serverModule.DataTypes
            |> List.map (fun dataType -> dataTypeFootprint owner dataType.Id))
        |> DataFootprint.signatureOf

    /// `ofModulesWith` under the default attribution — the call a
    /// composition root makes when it holds the module list.
    let ofModules (modules: ServerModule list) : FootprintSignature = ofModulesWith componentIdOf modules

    /// The footprints composed audit sinks derive: each sink persists
    /// audit records under its own multi-impl `ComponentId` (keyed by the
    /// sink's `Name`, never its position in the list — the Phase 279
    /// rule).
    let ofAuditSinks (sinkNames: string list) : FootprintSignature =
        sinkNames
        |> List.map (fun name ->
            let componentId = ComponentId.forCompanionImpl "IAuditSink" name
            let cls = auditSinkClass name

            DataFootprint.none componentId
            |> DataFootprint.withWrite cls
            |> DataFootprint.withPersist cls)
        |> DataFootprint.signatureOf

    /// The DERIVED half of a composed application's footprint: every
    /// registered `DataType` attributed to its producing module's resolved
    /// id, plus every composed audit sink. Reads only accumulators the
    /// compose flow already populates, so it cannot drift from what was
    /// actually composed.
    let ofApp (app: ServerApp) : FootprintSignature =
        let moduleIds = app.ModuleComponentIds |> Map.ofList

        let dataTypes =
            app.DataTypeRegistrations
            |> List.map (fun (moduleName, dataType) ->
                let owner =
                    moduleIds
                    |> Map.tryFind moduleName
                    |> Option.defaultValue (ComponentId.ofModule moduleName)

                dataTypeFootprint owner dataType.Id)
            |> DataFootprint.signatureOf

        let auditSinks = app.AuditSinks |> List.map _.Name |> ofAuditSinks

        DataFootprint.mergeSignature dataTypes auditSinks

    /// The 433.B fold: derive what the registrations know, then fold the
    /// residual DECLARED footprints (a companion's own backing store)
    /// on top, then apply any declared re-classification (the
    /// `ContainsPii` judgement derivation cannot make) across the whole
    /// result. Declaration is additive over derivation, except for the
    /// re-classification, which supersedes.
    let derive (app: ServerApp) (declared: FootprintSignature) (reclassified: DataClass list) : FootprintSignature =
        DataFootprint.mergeSignature (ofApp app) declared
        |> DataFootprint.reclassify reclassified

    // ── the composition join (433.C) ──────────────────────────────────

    /// Every `ComponentId` the Phase 280 manifest enumerates — modules,
    /// companion slots and impls, datatypes, tools, metrics, subjects.
    /// The id set the footprint is restricted to, so a declaration for a
    /// component this deployment does not compose cannot inflate the
    /// surface.
    let manifestComponents (manifest: CompositionManifest) : Set<ComponentId> =
        [
            manifest.Modules
            manifest.CompanionSlots
            manifest.DataTypes
            manifest.Tools
            manifest.Metrics
            manifest.Subjects
        ]
        |> List.collect id
        |> List.map _.Id
        |> Set.ofList

    /// The signature restricted to what the manifest actually composed.
    let restrictToManifest (manifest: CompositionManifest) (signature: FootprintSignature) : FootprintSignature =
        DataFootprint.restrictTo (manifestComponents manifest) signature

    /// **The 433.C join.** The whole composition's data-at-rest surface:
    /// the footprint signature restricted to the manifest's composed
    /// components and joined componentwise (the Phase 296 shape applied
    /// to data-at-rest). Query it with `DataFootprint.persistedClasses` /
    /// `classesBySeam` / `classesOfKind` / `persistedPiiClasses`, or go
    /// back through the signature with `DataFootprint.componentsPersisting`
    /// for the per-component attribution.
    let overManifest (manifest: CompositionManifest) (signature: FootprintSignature) : CompositionDataFootprint =
        restrictToManifest manifest signature |> DataFootprint.compose

    /// The composed DSR path names — every registered `IDataExporter.Name`
    /// and `IErasureHandler.Name`. The set a coverage claim's path names
    /// are resolved against; an app composing no DSR pipeline yields the
    /// empty set, which is the honest posture (every claim is then
    /// unresolved).
    let dsrPathNames (app: ServerApp) : Set<string> =
        (app.DataExporters |> List.map _.Name)
        @ (app.ErasureHandlers |> List.map _.Name)
        |> Set.ofList

/// The opt-in DSR/offboarding completeness preflight (433.D). Two rules,
/// exported in the Phase 294 `CompositionRuleDescriptor` vocabulary and
/// the Phase 585 classified form, run by a structural-class
/// `IConfigValidator` wired through the Phase 9m aggregator via the same
/// `serviceRegistration` closure `ComponentRequirementsPreflight` (Phase
/// 432) and `EventTopologyPreflight` (Phase 431) return.
///
/// Not a `CompositionValidator` rule, for the reason Phase 432 recorded
/// and Phase 431 repeated: a Phase 281 `CompositionRule` is
/// `manifest -> refs -> string list`, and neither the footprint signature
/// nor the composed DSR path set is reachable from either argument.
/// Growing that signature would break every rule that ships.
module DataFootprintPreflight =

    /// Stable `IConfigValidator.Name` for the DSR-coverage check.
    /// Structural-class (Phase 585) — `SkipPreflight` does NOT bypass it.
    [<Literal>]
    let ValidatorName = "data-footprint-dsr-coverage"

    /// Stable rule code for persisted personal data with no DSR path.
    [<Literal>]
    let PiiUncoveredRule = "data-footprint-pii-uncovered"

    /// Stable rule code for a coverage claim naming a DSR path this
    /// deployment does not compose.
    [<Literal>]
    let StaleClaimRule = "data-footprint-dsr-path-unresolved"

    /// The default severity of an uncovered PII class: a warning. An
    /// uncovered class is a real compliance defect, but it is also the
    /// shape a deployment legitimately takes while the DSR pipeline is
    /// staged behind the feature that persists the data — so the default
    /// reports and continues, and a deployment that wants it fatal passes
    /// `DefectError`.
    let defaultUncoveredSeverity: CompositionDefectSeverity = DefectWarning

    /// Phase 294 — the introspectable rule manifest, in the same
    /// `CompositionRuleDescriptor` shape `CompositionValidator.ruleManifest`
    /// exports, so an external pre-build checker reads one vocabulary
    /// across every rule family. The uncovered rule's severity is the
    /// default; a deployment that raises it says so at registration.
    let ruleManifest: CompositionRuleDescriptor list = [
        {
            Code = PiiUncoveredRule
            Severity = defaultUncoveredSeverity
            Description =
                "A data class flagged as containing personal data is persisted by a composed component, but no composed IDataExporter / IErasureHandler is claimed to cover it and no exemption is declared. A data-subject request cannot reach it."
        }
        {
            Code = StaleClaimRule
            Severity = DefectWarning
            Description =
                "A DSR coverage claim names an IDataExporter / IErasureHandler that this deployment does not compose. The claim is stale — the path was renamed or removed — so the coverage it asserts is not real."
        }
    ]

    /// Phase 585 — the same rules with their class. Both are structural:
    /// pure in-memory sweeps over the derived footprint and a set of
    /// composed handler names, with no external dependency to be down.
    let classifiedRuleManifest: ClassifiedCompositionRule list =
        ruleManifest
        |> List.map (fun rule -> {
            Code = rule.Code
            Severity = rule.Severity
            Description = rule.Description
            Class = StructuralRule
        })

    /// The coverage defects of a composition, at the configured
    /// uncovered-class severity.
    ///
    /// `available` is the composed DSR path name set
    /// (`DataFootprintDerivation.dsrPathNames`); `claims` are the
    /// deployment's declared coverage claims; `signature` is the joined
    /// footprint. Both rules are evaluated: an uncovered PII class is one
    /// finding, a stale claim another — a class can be covered by a live
    /// path AND carry a second, stale one, and both facts are worth
    /// saying.
    let defects
        (uncoveredSeverity: CompositionDefectSeverity)
        (available: Set<string>)
        (claims: DsrCoverage list)
        (signature: FootprintSignature)
        : CompositionDefect list =
        let uncovered =
            DsrCoverage.gaps available claims signature
            |> List.map (fun gap -> {
                RuleCode = PiiUncoveredRule
                Severity = uncoveredSeverity
                Message =
                    sprintf
                        "Data class %s is %s. Persisted by %s. Register an IDataExporter / IErasureHandler that covers it and declare the coverage, or declare an exemption saying why the class needs no data-subject path."
                        (DataClass.describe gap.GapClass)
                        gap.Detail
                        (gap.PersistedBy
                         |> List.map (ComponentId.value >> sprintf "'%s'")
                         |> String.concat ", ")
            })

        let stale =
            DsrCoverage.staleClaims available claims
            |> List.map (fun (claim, unresolved) -> {
                RuleCode = StaleClaimRule
                Severity = DefectWarning
                Message =
                    sprintf
                        "DSR coverage claim for data class %s names %s, which this deployment does not compose. Compose the named path, or correct the claim — an unresolved path asserts coverage that is not there."
                        (DataClass.describe claim.CoveredClass)
                        (unresolved |> List.map (sprintf "'%s'") |> String.concat ", ")
            })

        uncovered @ stale

    let private renderDefects (defects: CompositionDefect list) : string =
        defects
        |> List.map (fun d -> sprintf "[%s] %s" d.RuleCode d.Message)
        |> String.concat "\n"

    /// Translate coverage defects into a `ValidationResult`: any
    /// `DefectError` aborts, a clean sweep is `Ok`, warnings-only surface
    /// as `Warning`.
    let toValidationResult (defects: CompositionDefect list) : ValidationResult =
        let errors = defects |> List.filter (fun d -> d.Severity = DefectError)

        if not errors.IsEmpty then
            Error(renderDefects errors)
        else
            match defects with
            | [] -> Ok
            | warnings -> Warning(renderDefects warnings)

    /// The structural-class `IConfigValidator` that runs the coverage
    /// rules at preflight.
    type DataFootprintCoverageValidator
        (
            uncoveredSeverity: CompositionDefectSeverity,
            available: Set<string>,
            claims: DsrCoverage list,
            signature: FootprintSignature
        ) =
        interface IConfigValidator with
            member _.Name = ValidatorName
            member _.Timeout = IConfigValidator.defaultTimeout

            member _.Validate() = async {
                return toValidationResult (defects uncoveredSeverity available claims signature)
            }

        interface IStructuralClassValidator

    /// Whether this composition has anything for the rules to say
    /// something about: at least one persisted PII class, or at least one
    /// claim whose staleness could be reported. A composition with
    /// neither registers no validator (GP 13).
    let hasSomethingToCheck (claims: DsrCoverage list) (signature: FootprintSignature) : bool =
        not (List.isEmpty claims)
        || not (List.isEmpty (DataFootprint.persistedPiiClasses (DataFootprint.compose signature)))

    /// The opt-in registration (433.D): folds the coverage validator into
    /// the Phase 9m aggregator so it runs at the same startup gate as
    /// every other `IConfigValidator`. Returned as the same
    /// `IServiceCollection -> IServiceCollection` closure
    /// `CompositionValidator.serviceRegistration`,
    /// `ComponentRequirementsPreflight.serviceRegistration` and
    /// `EventTopologyPreflight.serviceRegistration` return, so a
    /// composition root folds it into the extension `ServiceConfig` hook
    /// identically.
    ///
    /// **Nothing is registered when there is nothing to check** (GP 13) —
    /// including the `ServerApp.empty |> ServerApp.run` base case — so a
    /// deployment that declares no footprint composes a byte-for-byte
    /// identical `services` (GP 11).
    let serviceRegistration
        (uncoveredSeverity: CompositionDefectSeverity)
        (available: Set<string>)
        (claims: DsrCoverage list)
        (signature: FootprintSignature)
        : IServiceCollection -> IServiceCollection =
        fun services ->
            if hasSomethingToCheck claims signature then
                services.AddSingleton<IConfigValidator>(
                    DataFootprintCoverageValidator(uncoveredSeverity, available, claims, signature) :> IConfigValidator
                )
                |> ignore

            services

    /// `serviceRegistration` over a composed `ServerApp` at the default
    /// severity — the production shape. The DSR path names come from the
    /// app's own registered exporters / erasure handlers, so a renamed
    /// handler surfaces as a stale claim rather than passing silently.
    let serviceRegistrationForApp
        (app: ServerApp)
        (claims: DsrCoverage list)
        (signature: FootprintSignature)
        : IServiceCollection -> IServiceCollection =
        serviceRegistration defaultUncoveredSeverity (DataFootprintDerivation.dsrPathNames app) claims signature