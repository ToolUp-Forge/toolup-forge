// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open Fuaran.Core
open Fuaran.DB
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

// ─── describe_dataset_model (Phase 1348) ─────────────────────────────
//
// The assistant reads the dataset models through `Fuaran.DB`'s AI-tools
// surface (`AiTools.readTools`: the outline, the tables and the relations),
// over the caller's projection of each model (`DatasetModelRegistry.projectFor`)
// and never the model itself, so a table the gate would withhold entirely is
// not named. Beside them forge serves what the read tools do not carry: each
// table's own description (its `FactTableDescription`, never restated in
// the model) and the measures with the metrics they publish. The model's op
// catalogue and write policy are not offered: the assistant reads a module's
// model and never amends it, and a pipeline it writes is refused by the
// relation check with `Fuaran.DB`'s own guidance when it leaves the model.

/// The `describe_dataset_model` AI tool.
[<RequireQualifiedAccess>]
module DatasetModelTools =

    /// The tool's name.
    [<Literal>]
    let Name = "describe_dataset_model"

    /// The parts a call may ask for.
    let parts: string list = [ "outline"; "tables"; "relations"; "measures" ]

    /// The tool's declaration.
    let definition: AIToolDefinition = {
        Name = Name
        Description =
            "Describe THE DATASET MODELS over this deployment's fact tables: which tables a module models, their columns (name, type and what each holds), the relations a pipeline may join them along, and the measures with the metrics they publish. Call it before you write a pipeline that joins two tables: a join is admitted only along a relation listed here. Pass `part` (outline, tables, relations or measures) to read one part, and `table` to read only the model holding that table. It reports declarations only, never a value, and lists only what you may be shown."
        Parameters = [
            {
                Name = "part"
                Type = "string"
                Description =
                    "Optional — one of outline, tables, relations, measures. Omit it for tables, relations and measures."
                Required = false
                Default = None
            }
            {
                Name = "table"
                Type = "string"
                Description = "Optional — read only the model holding this fact table, by id."
                Required = false
                Default = None
            }
        ]
        SourceModule = FactEvents.SourceModule
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
        Effects = ToolEffectDeclaration.readFacts
    }

    let private readTool (name: string) (state: ModelState) : JVal =
        AiTools.readTools
        |> List.tryFind (fun t -> t.Name = name)
        |> Option.map (fun t -> t.Run state)
        |> Option.defaultValue (JArr [])

    let private withDescriptions (registry: DatasetModelRegistry) (tables: JVal) : JVal =
        match tables with
        | JArr rows ->
            rows
            |> List.map (fun row ->
                match row with
                | JObj members ->
                    let name =
                        members
                        |> List.tryPick (fun (k, v) ->
                            match k, v with
                            | "name", JStr n -> Some n
                            | _ -> None)

                    match name |> Option.bind registry.TryDescription with
                    | Some d -> JObj(members @ [ "description", JStr d.Description ])
                    | None -> JObj members
                | other -> other)
            |> JArr
        | other -> other

    let private measuresOf (model: RegisteredDatasetModel) (projected: ProjectedModel) : JVal =
        let visible =
            Model.tables projected.Model |> List.map (fun (_, t) -> t.Name) |> Set.ofList

        let shown =
            Model.tables projected.Model
            |> List.collect (fun (node, t) -> Model.columns node |> List.map (fun (_, c) -> t.Name, c.Name))
            |> Set.ofList

        model.Declaration.Measures
        |> List.filter (fun m -> visible.Contains m.Table && shown.Contains(m.Table, m.Column))
        |> List.map (fun m ->
            JObj [
                "name", JStr m.Name
                "table", JStr m.Table
                "column", JStr m.Column
                "aggregate", JStr m.Aggregate
                "publishes", JStr m.Publishes
            ])
        |> JArr

    /// What one model answers for `part`, over its projection.
    let describe
        (registry: DatasetModelRegistry)
        (resolver: DisclosurePolicyResolver)
        (part: string option)
        (model: RegisteredDatasetModel)
        : JVal option =
        let projected = DatasetModelRegistry.projectFor resolver model

        if List.isEmpty (Model.tables projected.Model) then
            None
        else
            let state = ModelOps.ofRoot projected.Model

            let section (p: string) =
                match p with
                | "outline" -> p, readTool "outline" state
                | "tables" -> p, withDescriptions registry (readTool "tables" state)
                | "relations" -> p, readTool "relations" state
                | _ -> "measures", measuresOf model projected

            let wanted =
                match part with
                | Some p -> [ p ]
                | None -> [ "tables"; "relations"; "measures" ]

            Some(
                JObj(
                    [
                        "module", JStr model.Module
                        "modelHash", JStr model.Hash
                        "projectionHash", JStr(ModelProjection.hash projected)
                    ]
                    @ (wanted |> List.map section)
                )
            )

    let private textArg (root: JsonElement) (name: string) : string option =
        match root.TryGetProperty name with
        | true, v when
            v.ValueKind = JsonValueKind.String
            && not (String.IsNullOrWhiteSpace(v.GetString()))
            ->
            Some(v.GetString().Trim())
        | _ -> None

    /// The answer to a call, over explicit dependencies.
    let answer (registry: DatasetModelRegistry) (resolver: DisclosurePolicyResolver) (argsJson: string) : string =
        let error (message: string) =
            Json.render (JObj [ "error", JStr message ])

        let args =
            if String.IsNullOrWhiteSpace argsJson then
                Ok None
            else
                try
                    Ok(Some(JsonDocument.Parse(argsJson).RootElement.Clone()))
                with e ->
                    Error e.Message

        match args with
        | Error reason -> error (sprintf "the arguments are not JSON: %s" reason)
        | Ok root ->
            let part = root |> Option.bind (fun r -> textArg r "part")
            let table = root |> Option.bind (fun r -> textArg r "table")

            match part with
            | Some p when not (List.contains p parts) ->
                error (sprintf "part '%s' is not one of %s" p (String.concat ", " parts))
            | _ ->
                let models =
                    match table with
                    | None -> registry.Models
                    | Some t -> registry.TryModelOf t |> Option.toList

                let described = models |> List.choose (describe registry resolver part)

                match table, described with
                | Some t, [] ->
                    error (
                        sprintf "no dataset model you may read holds fact table '%s'; call %s with no arguments" t Name
                    )
                | _ -> Json.render (JObj [ "modelCount", JInt described.Length; "models", JArr described ])

    /// The executor over the registry and the deployment's policy resolver.
    let executor
        (registry: DatasetModelRegistry)
        (resolver: DisclosurePolicyResolver)
        : HttpContext -> string -> Async<string> =
        fun _ argsJson -> async { return answer registry resolver argsJson }