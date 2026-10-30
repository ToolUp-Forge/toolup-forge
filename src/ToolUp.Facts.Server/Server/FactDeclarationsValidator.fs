// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Grounding

// ─── What a deployment declared and left undescribed ─────────────────
//
// A description is optional (GP 11): a metric's `Context`, a fact table's
// description and a subject's description each default to nothing, and an
// optional field nobody is told about stays empty. The assistant's
// discovery tools then report names and units only. This audit names every
// declaration that carries no description, grouped by declaring module,
// with the one registration call each needs.
//
// It is information, never a refusal. The preflight result type has no
// informational severity (`Ok | Warning | Error`), and a `Warning` would
// move every deployment that has not described itself to `Warned` on its
// readiness report, which is a runtime-visible change for a field that is
// optional by design. So the finding is logged at Info and the validator
// answers `Ok`.

/// The declarations one module left undescribed, by kind.
type UndescribedDeclarations = {
    /// The declaring module; `FactDeclarationsAudit.Unattributed` when the
    /// composed registry does not record it.
    ModuleName: string
    /// Metric ids registered without a `Context`.
    Metrics: string list
    /// Fact table ids declared without a description.
    Tables: string list
    /// Subject ids declared without a description.
    Subjects: string list
}

[<RequireQualifiedAccess>]
module FactDeclarationsAudit =

    /// The group a declaration falls under when its registry does not
    /// record the declaring module (an externally supplied registry).
    [<Literal>]
    let Unattributed = "(module not recorded)"

    /// Stable `IConfigValidator.Name` of the audit.
    [<Literal>]
    let ValidatorName = "fact-declarations-described"

    type private Kind =
        | Metric
        | Table
        | Subject

    let private isBlank (text: string) : bool = String.IsNullOrWhiteSpace text

    let private metricGaps (registry: IMetricRegistry) : (string * Kind * string) list =
        let undescribed (definition: MetricDefinition) =
            match definition.Context with
            | Some context -> isBlank context
            | None -> true

        match MetricRegistry.tryRegistrations registry with
        | Some(registrations, _) -> [
            for r in registrations do
                if undescribed r.Definition then
                    r.Module, Metric, r.Definition.Id
          ]
        | None -> [
            for definition in registry.Metrics do
                if undescribed definition then
                    Unattributed, Metric, definition.Id
          ]

    let private subjectGaps (registry: IMetricRegistry) : (string * Kind * string) list =
        let undescribed (subjectId: string) =
            match MetricRegistry.trySubjectDescription registry subjectId with
            | Some description -> isBlank description.Description
            | None -> true

        match MetricRegistry.tryRegistrations registry with
        | Some(_, registrations) -> [
            for r in registrations do
                if undescribed r.Definition.Id then
                    r.Module, Subject, r.Definition.Id
          ]
        | None -> [
            for definition in registry.Subjects do
                if undescribed definition.Id then
                    Unattributed, Subject, definition.Id
          ]

    let private tableGaps (registry: IFactTableRegistry) : (string * Kind * string) list = [
        for table in registry.Tables do
            let undescribed =
                match FactTableRegistry.tryDescription registry table.Id with
                | Some description -> isBlank description.Description
                | None -> true

            if undescribed then
                registry.DeclaringModule table.Id |> Option.defaultValue Unattributed, Table, table.Id
    ]

    /// Every undescribed metric, table and subject the two registries hold,
    /// grouped by declaring module in the order the modules first appear.
    /// A deployment with everything described answers the empty list.
    let undescribed
        (metrics: IMetricRegistry option)
        (tables: IFactTableRegistry option)
        : UndescribedDeclarations list =
        let gaps = [
            yield! metrics |> Option.map metricGaps |> Option.defaultValue []
            yield! tables |> Option.map tableGaps |> Option.defaultValue []
            yield! metrics |> Option.map subjectGaps |> Option.defaultValue []
        ]

        gaps
        |> List.map (fun (moduleName, _, _) -> moduleName)
        |> List.distinct
        |> List.map (fun moduleName ->
            let ids kind =
                gaps
                |> List.choose (fun (m, k, id) -> if m = moduleName && k = kind then Some id else None)
                |> List.distinct

            {
                ModuleName = moduleName
                Metrics = ids Metric
                Tables = ids Table
                Subjects = ids Subject
            })

    let private quoted (ids: string list) : string =
        ids |> List.map (sprintf "'%s'") |> String.concat ", "

    /// The finding: one message naming each undescribed declaration under
    /// its module, with a count and the remedy for each kind.
    let message (gaps: UndescribedDeclarations list) : string =
        let count =
            gaps
            |> List.sumBy (fun g -> g.Metrics.Length + g.Tables.Length + g.Subjects.Length)

        let perModule =
            gaps
            |> List.map (fun g ->
                let parts = [
                    if not g.Metrics.IsEmpty then
                        sprintf "%d metric(s) without Context: %s" g.Metrics.Length (quoted g.Metrics)
                    if not g.Tables.IsEmpty then
                        sprintf "%d fact table(s) without a description: %s" g.Tables.Length (quoted g.Tables)
                    if not g.Subjects.IsEmpty then
                        sprintf "%d subject(s) without a description: %s" g.Subjects.Length (quoted g.Subjects)
                ]

                sprintf "module '%s' - %s" g.ModuleName (String.concat "; " parts))
            |> String.concat "\n  "

        sprintf
            "%d declaration(s) in %d module(s) carry no description, so the assistant's discovery tools report names and units only.\n  %s\nRemedy, one call each: set Context = Some \"...\" on the metric passed to ServerModule.declareMetrics; ServerModule.describeFactTables for a table; ServerModule.describeSubjects for a subject. Prose about what a thing is, never a figure. A description is optional; this is information only."
            count
            gaps.Length
            perModule

/// Preflight validator that reports what the deployment declared and left
/// undescribed (`FactDeclarationsAudit`). Its finding goes to `report`
/// (the startup logger at Info) and its result is always `Ok`: it can never
/// refuse a start, and a fully described deployment reports nothing.
type FactDeclarationsValidator
    (metrics: unit -> IMetricRegistry option, tables: unit -> IFactTableRegistry option, report: string -> unit) =

    interface ConfigValidation.IConfigValidator with
        member _.Name = FactDeclarationsAudit.ValidatorName
        member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout

        member _.Validate() = async {
            match FactDeclarationsAudit.undescribed (metrics ()) (tables ()) with
            | [] -> ()
            | gaps -> report (FactDeclarationsAudit.message gaps)

            return ConfigValidation.ValidationResult.Ok
        }

module internal FactDeclarationsValidatorRegistration =

    // The last instance registered for `'T`, as a built provider would
    // resolve it. Validators run on the instances registered at compose
    // end, so the audit reads the collection it was registered into.
    let private lastInstance<'T when 'T: not struct> (services: IServiceCollection) : 'T option =
        services
        |> Seq.filter (fun d -> d.ServiceType = typeof<'T> && not d.IsKeyedService)
        |> Seq.tryLast
        |> Option.bind (fun d ->
            match d.ImplementationInstance with
            | :? 'T as instance -> Some instance
            | _ -> None)

    /// Add the audit to `services` once.
    let register (services: IServiceCollection) : IServiceCollection =
        let present =
            services
            |> Seq.exists (fun d -> not d.IsKeyedService && (d.ImplementationInstance :? FactDeclarationsValidator))

        if present then
            services
        else
            services.AddSingleton<ConfigValidation.IConfigValidator>(
                FactDeclarationsValidator(
                    (fun () -> lastInstance<IMetricRegistry> services),
                    (fun () -> lastInstance<IFactTableRegistry> services),
                    (fun message ->
                        lastInstance<ILogger> services
                        |> Option.iter (fun logger -> logger.Info message))
                )
                :> ConfigValidation.IConfigValidator
            )