// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Security.Cryptography
open System.Text
open Fuaran.Core
open Fuaran.Compute
open Fuaran.DB
open ToolUp.Platform
open ToolUp.Platform.Compose
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes

// ─── The dataset model over a module's fact tables (Phase 1348) ──────
//
// A module's dataset model (`ServerModule.declareDatasetModel`) is lowered
// here to a `Fuaran.DB` model and checked:
//
//   - its tables are the module's fact tables, and each table's columns are
//     the columns a pipeline reads its slice under (`FactBrowseSlice.build`'s
//     layout), typed as the slice types them. Nothing is restated: a column's
//     description is its `FactTableDescription` column's, and a table's own
//     description stays the description's, served beside the model by its
//     tools;
//   - its relations are the model's joins and its measures the model's
//     measures; the declaration checks (`FactTablePreflight.datasetModelDefects`)
//     run first, then the model's own rules (`ModelRules.check`);
//   - a column's classification is its disclosure: `surfaceable`, `internal`
//     or `restricted:<policy>` for a metric's value and rendering, and
//     `existence:<table>` for the row, subject, period and per-metric
//     existence columns every slice read already serves.
//
// **Shape and cost, never disclosure.** The model declares no grant, row
// policy or group floor. What one caller may know of it is its projection
// (`ModelProjection.project`) under grants read off the classifications at
// the tool-result door, through the deployment's policy resolver, the same
// classification-level answer the gate starts from: a table whose every
// metric is withheld there does not appear, because its existence
// classification is granted only beside a granted metric. The gate still
// judges every cell a pipeline or a view reads.

/// A module's dataset model, lowered and checked.
type RegisteredDatasetModel = {
    /// The declaring module.
    Module: string
    /// The declaration as the module wrote it.
    Declaration: DatasetModelDeclaration
    /// The `Fuaran.DB` model it lowers to.
    Model: ModelNode
    /// `sha256:` over the model's canonical wire encoding.
    Hash: string
}

/// Why a composition's dataset models were refused. Nothing was registered.
[<RequireQualifiedAccess>]
type DatasetModelRefusal =
    /// A declaration check (`FactTablePreflight`'s rule code and message).
    | Declaration of rule: string * message: string
    /// A rule of the lowered model (`Fuaran.DB`'s code and message).
    | ModelRule of moduleName: string * code: string * message: string

/// Companion helpers for `DatasetModelRefusal`.
[<RequireQualifiedAccess>]
module DatasetModelRefusal =
    /// The refusal's stable code.
    let code (refusal: DatasetModelRefusal) : string =
        match refusal with
        | DatasetModelRefusal.Declaration(rule, _) -> rule
        | DatasetModelRefusal.ModelRule(_, code, _) -> code

    /// One sentence naming what to repair.
    let describe (refusal: DatasetModelRefusal) : string =
        match refusal with
        | DatasetModelRefusal.Declaration(rule, message) -> sprintf "[%s] %s" rule message
        | DatasetModelRefusal.ModelRule(moduleName, code, message) ->
            sprintf "[%s] module '%s': %s" code moduleName message

/// The composition's dataset models, checked. Only
/// `DatasetModelRegistry.register` makes one.
[<Sealed>]
type DatasetModelRegistry
    internal (models: RegisteredDatasetModel list, descriptions: Map<string, FactTableDescription>) =
    /// Every registered model, in module order.
    member _.Models = models

    /// The model a fact table is modelled in, if any.
    member _.TryModelOf(table: string) : RegisteredDatasetModel option =
        models |> List.tryFind (fun m -> List.contains table m.Declaration.Tables)

    /// A modelled or unmodelled table's description, as its declaring
    /// composition described it.
    member _.TryDescription(table: string) : FactTableDescription option = descriptions.TryFind table

/// Lowering, checking and projecting dataset models.
[<RequireQualifiedAccess>]
module DatasetModelRegistry =

    /// The classification the row, subject, period and per-metric existence
    /// columns of `table` carry.
    let existenceClass (table: string) : string = "existence:" + table

    /// The classification a metric's value and rendering carry.
    let disclosureClass (disclosure: FactTableDisclosure) : string =
        match disclosure with
        | FactTableDisclosure.Surfaceable -> "surfaceable"
        | FactTableDisclosure.Internal -> "internal"
        | FactTableDisclosure.Restricted policy -> "restricted:" + policy

    /// The aggregate a measure's wire spelling names.
    let aggregateOf (spelling: string) : AggFn option =
        [ Sum; Mean; Min; Max; Count; Median; StdDev; First; Last; CountDistinct ]
        |> List.tryFind (fun fn -> ModelWire.aggTag fn = spelling)

    let private tableNodeId (table: string) = "table:" + table
    let private columnNodeId (table: string) (column: string) = table + "." + column

    let private hashOf (model: ModelNode) : string =
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ModelWire.encodeNode model))
        "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant()

    /// The columns of `table` as a pipeline reads its slice, each with its
    /// classification and, for a metric's value, its description.
    let private columnsOf
        (table: FactTableDefinition)
        (description: FactTableDescription option)
        : Result<(Field * string) list, string> =
        let metrics = FactTableDefinition.metrics table

        let described =
            description
            |> Option.map (fun d -> Map.ofList d.Columns)
            |> Option.defaultValue Map.empty

        let valueClass =
            table.Columns
            |> List.map (fun c -> c.Metric, disclosureClass (FactTableDefinition.columnDisclosure table c))
            |> Map.ofList

        FactBrowseSlice.build metrics []
        |> Result.map (fun slice ->
            slice.Schema
            |> List.map (fun field ->
                match valueClass.TryFind field.Name with
                | Some cls ->
                    let field =
                        match described.TryFind field.Name with
                        | Some d -> Field.withDescription d field
                        | None -> field

                    field, cls
                | None ->
                    let rendering =
                        metrics
                        |> List.tryFind (fun m -> field.Name = FactBrowseSlice.metricColumn m "rendering")

                    match rendering with
                    | Some m -> field, valueClass[m]
                    | None -> field, existenceClass table.Id))

    /// Lower one declaration to a `Fuaran.DB` model. Assumes the declaration
    /// checks passed: every table it names is one its module declares.
    let internal lower
        (moduleName: string)
        (declaration: DatasetModelDeclaration)
        (tables: Map<string, FactTableDefinition>)
        (descriptions: Map<string, FactTableDescription>)
        : Result<ModelNode, string> =
        let tableNodes =
            declaration.Tables
            |> List.map (fun id ->
                columnsOf tables[id] (descriptions.TryFind id)
                |> Result.map (fun columns -> id, columns))

        match
            tableNodes
            |> List.tryPick (function
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some e -> Error e
        | None ->
            let tableNodes = tableNodes |> List.choose Result.toOption

            let classifications =
                tableNodes
                |> List.collect (fun (_, columns) -> columns |> List.map snd)
                |> List.distinct

            let table (id: string, columns: (Field * string) list) = {
                Id = tableNodeId id
                Kind = NodeKind.Table { Name = id; Physical = id }
                Children =
                    columns
                    |> List.map (fun (field, cls) -> {
                        Id = columnNodeId id field.Name
                        Kind =
                            NodeKind.Column {
                                Field = field
                                Physical = field.Name
                                Classification = cls
                            }
                        Children = []
                    })
            }

            let relations =
                declaration.Relations
                |> List.map (fun r -> {
                    Id = "relation:" + r.Name
                    Kind =
                        NodeKind.Join {
                            Name = r.Name
                            Left = tableNodeId r.Left
                            Right = tableNodeId r.Right
                            On = r.On |> List.map (fun (l, rr) -> columnNodeId r.Left l, columnNodeId r.Right rr)
                        }
                    Children = []
                })

            let measures =
                declaration.Measures
                |> List.choose (fun m ->
                    aggregateOf m.Aggregate
                    |> Option.map (fun fn -> {
                        Id = "measure:" + m.Name
                        Kind =
                            NodeKind.Measure {
                                Name = m.Name
                                Fn = fn
                                Column = columnNodeId m.Table m.Column
                            }
                        Children = []
                    }))

            Ok {
                Id = "model:" + moduleName
                Kind =
                    NodeKind.Model {
                        Name = moduleName
                        Classifications = classifications
                        Recordable = []
                    }
                Children =
                    [
                        {
                            Id = "source:" + moduleName
                            Kind =
                                NodeKind.Source {
                                    Name = moduleName
                                    Fingerprint = None
                                }
                            Children = tableNodes |> List.map table
                        }
                    ]
                    @ relations
                    @ measures
            }

    /// Check and lower every dataset model the composition declares: the
    /// declaration checks first, each refused by its rule, then each lowered
    /// model's own rules. `Ok` holds every model; `Error` every refusal found.
    let check (composition: FactTableComposition) : Result<DatasetModelRegistry, DatasetModelRefusal list> =
        let descriptions =
            composition.Descriptions
            |> List.map (fun r -> r.Description.TableId, r.Description)
            |> Map.ofList

        let declarationRefusals =
            FactTablePreflight.datasetModelDefects composition
            |> List.map (fun d -> DatasetModelRefusal.Declaration(d.RuleCode, d.Message))

        if not declarationRefusals.IsEmpty then
            Error declarationRefusals
        else
            let lowered =
                composition.DatasetModels
                |> List.map (fun r ->
                    let tables =
                        composition.Tables
                        |> List.filter (fun t -> t.Module = r.Module)
                        |> List.map (fun t -> t.Definition.Id, t.Definition)
                        |> Map.ofList

                    match lower r.Module r.Model tables descriptions with
                    | Error e -> Error [ DatasetModelRefusal.ModelRule(r.Module, "DB-LOWER", e) ]
                    | Ok model ->
                        match ModelRules.check model with
                        | [] ->
                            Ok {
                                Module = r.Module
                                Declaration = r.Model
                                Model = model
                                Hash = hashOf model
                            }
                        | defects ->
                            defects
                            |> List.map (fun d -> DatasetModelRefusal.ModelRule(r.Module, d.Code, d.Message))
                            |> Error)

            match
                lowered
                |> List.collect (function
                    | Error e -> e
                    | Ok _ -> [])
            with
            | [] -> Ok(DatasetModelRegistry(lowered |> List.choose Result.toOption, descriptions))
            | refusals -> Error refusals

    /// `check` over a composed app: its declared tables, metrics, descriptions
    /// and models (`ServerApp.factTableComposition`).
    let register (app: ServerApp) : Result<DatasetModelRegistry, DatasetModelRefusal list> =
        check (ServerApp.factTableComposition app)

    /// The grants one caller holds over `model` at the tool-result door: a
    /// `surfaceable` metric is granted, an `internal` one is not, a
    /// `restricted:<policy>` one is granted when the deployment's resolver
    /// permits the policy at that door, and a table's existence columns are
    /// granted when one of its metrics is.
    let grantsFor (resolver: DisclosurePolicyResolver) (model: ModelNode) : Grant list =
        let admitted (cls: string) =
            if cls = "surfaceable" then
                true
            elif cls.StartsWith("restricted:", StringComparison.Ordinal) then
                resolver (cls.Substring "restricted:".Length) FactEgressSurface.FactToolResult = Some true
            else
                false

        let tables = Model.tables model

        let tableAdmitted (table: string) =
            match tables |> List.tryFind (fun (_, t) -> t.Name = table) with
            | Some(node, _) ->
                Model.columns node
                |> List.exists (fun (_, c) ->
                    not (c.Classification.StartsWith("existence:", StringComparison.Ordinal))
                    && admitted c.Classification)
            | None -> false

        Model.spec model
        |> Option.map _.Classifications
        |> Option.defaultValue []
        |> List.filter (fun cls ->
            if cls.StartsWith("existence:", StringComparison.Ordinal) then
                tableAdmitted (cls.Substring "existence:".Length)
            else
                admitted cls)
        |> List.map (fun cls -> {
            Classification = cls
            Level = GrantLevel.RowLevel
        })

    /// What one caller may know of `model`: its projection under the caller's
    /// grants (`grantsFor`).
    let projectFor (resolver: DisclosurePolicyResolver) (model: RegisteredDatasetModel) : ProjectedModel =
        ModelProjection.project model.Model (grantsFor resolver model.Model)

    /// The relation check `evaluate_pipeline`'s profile asks about a join
    /// (`PipelineProfile.withRelationCheck`): the join step checked by
    /// `Fuaran.DB`'s pipeline check against the caller's projection of the
    /// model the pipeline's table is modelled in. A join along a declared
    /// relation is admitted; a table no model holds, or one the caller's
    /// projection does not show, joins nothing; a join along keys no relation
    /// declares is refused naming the relations that are. The join is checked
    /// over the table's own columns, so a key renamed by an earlier step is
    /// refused rather than guessed.
    let relationCheck (resolver: DisclosurePolicyResolver) (registry: DatasetModelRegistry) : PipelineRelationCheck =
        fun _ join ->
            match registry.TryModelOf join.Table with
            | None ->
                Error(
                    sprintf
                        "fact table '%s' is in no dataset model, so no relation is declared for it to join along"
                        join.Table
                )
            | Some model ->
                let projected = projectFor resolver model

                match
                    PipelineCheck.check projected.Model (Ref join.Table) [ Join(Ref join.Joined, join.On, Inner) ]
                with
                | Ok _ -> Ok()
                | Error refusal -> Error(PipelineCheck.describe refusal)