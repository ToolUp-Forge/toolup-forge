// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Scope

// ─── One tool per method card ────────────────────────────────────────
//
// A method card becomes one spec tool (`SpecTool`). What is generated from
// the card, so nothing is written twice:
//
//   - the tool's name is the card's tool name, its description the card's
//     meaning paragraph, and its parameters the door's slice arguments
//     followed by the card's own, each declared from the schema the card
//     gives it (with its default and admissible range in its description);
//   - the validator refuses a call that omits a required argument, writes one
//     of the wrong type or outside its closed set, or fails a precondition the
//     evaluator checks before any read, all together and each by name, the
//     precondition by the card's own name with the card's remedy;
//   - the result is the evaluator's table, which the door bounds and, where
//     it declares rows, asserts as facts whose method is the card's identity:
//     `Computed(card id, card version, parameter hash)`, the parameter hash
//     taken over the card's definition hash and the call's arguments;
//   - the text the model reads names the method, the evaluator's summary, the
//     card's diagnostics with their healthy ranges and its interpretation
//     rules, and never a figure: the figures are the result's.
//
// A card is bound to what runs it in one of two ways: an evaluator a
// component supplies (`MethodTool.bind`), which is how a card read from a
// package or built in code is registered, or an existing spec tool
// (`MethodTool.ofSpecTool`), which keeps its own decoding, validation and
// evaluation and takes its declaration from the card. Either way the binding
// is checked when it is registered (`MethodRegistry.register`).

/// A call of a method, as its evaluator reads it.
type MethodCall = {
    /// The method called.
    Method: MethodIdentity
    /// The slice the method is evaluated over.
    Slice: FactSliceQuery
    /// The method's own arguments, by name: those the call wrote and the
    /// defaults of those it omitted.
    Arguments: Map<string, JsonElement>
    /// The hash over the card's definition hash and the arguments: the
    /// parameter hash a fact computed by the call names.
    ParameterHash: string
}

/// What a method's evaluation answers.
type MethodEvaluation = {
    /// The result as the door reads it.
    Result: SpecResult
    /// The text the model reads of the result: its shape and what was
    /// withheld, never a figure.
    Summary: string
}

/// What runs a card, supplied by the component that registers it.
type MethodEvaluator = {
    /// The definition hash the evaluator implements. A card whose
    /// definition hash is not this one is refused at registration.
    DefinitionHash: string
    /// The preconditions checked before any fact is read, by the card's
    /// precondition name: whether the call meets it. A precondition on the
    /// data is the evaluation's to refuse.
    Checks: Map<string, MethodCall -> bool>
    /// The evaluation over the gated slice: the result, or the name of the
    /// card condition (a precondition or a declared refusal) it refused under.
    Evaluate: SpecRead -> MethodCall -> Async<Result<MethodEvaluation, string>>
}

/// What a registered method tool answers a call with, over explicit
/// dependencies: the fact browse's, the scope, the principal and the
/// arguments.
type MethodAnswer = FactBrowseHandler.FactBrowseDeps -> ResolvedScope -> string -> string -> Async<string>

/// A method tool as registration built it: its declaration, its answer over
/// explicit dependencies and its executor.
type MethodToolBuild = {
    /// The tool's declaration.
    Definition: AIToolDefinition
    /// The answer over explicit dependencies.
    Answer: MethodAnswer
    /// The registered executor.
    Executor: HttpContext -> string -> Async<string>
}

/// A card bound to what runs it. Built by `MethodTool.bind` or
/// `MethodTool.ofSpecTool`, and checked by `MethodRegistry.register`.
[<Sealed>]
type MethodBinding
    internal
    (
        card: MethodCard,
        definitionHash: string,
        checks: string list,
        operation: (string * string) option,
        grammar: string option,
        build: (MethodParameter * ToolSchemaNode) list -> MethodToolBuild
    ) =
    /// The card.
    member _.Card = card
    /// The definition hash the binding implements.
    member _.DefinitionHash = definitionHash
    /// The preconditions the binding checks before any read.
    member _.Checks = checks
    /// The operation id and version a bound spec tool asserts under, when
    /// the binding is over one.
    member _.Operation = operation
    /// What the binding adds to the card's profile entry: the grammar an
    /// existing tool reads its spec under.
    member _.Grammar = grammar
    member internal _.Build = build

/// Building a card's tool.
[<RequireQualifiedAccess>]
module MethodTool =

    // ── The signature's schemas ───────────────────────────────────

    let private descriptionOf (e: JsonElement) : string =
        match e.TryGetProperty "description" with
        | true, d when d.ValueKind = JsonValueKind.String -> d.GetString()
        | _ -> ""

    let rec private nodeAt (path: string) (e: JsonElement) : Result<ToolSchemaNode, string> =
        if e.ValueKind <> JsonValueKind.Object then
            Error(sprintf "%s is not a schema object" path)
        else
            match e.TryGetProperty "type" with
            | true, t when t.ValueKind = JsonValueKind.String ->
                match t.GetString() with
                | "string" ->
                    match e.TryGetProperty "enum" with
                    | false, _ -> Ok(SchemaString [])
                    | true, values when
                        values.ValueKind = JsonValueKind.Array
                        && values.GetArrayLength() > 0
                        && values.EnumerateArray()
                           |> Seq.forall (fun v -> v.ValueKind = JsonValueKind.String)
                        ->
                        Ok(SchemaString [ for v in values.EnumerateArray() -> v.GetString() ])
                    | true, _ -> Error(sprintf "%s.enum is not a non-empty array of strings" path)
                | "number" -> Ok SchemaNumber
                | "integer" -> Ok SchemaInteger
                | "boolean" -> Ok SchemaBoolean
                | "array" ->
                    match e.TryGetProperty "items" with
                    | true, items -> nodeAt (path + ".items") items |> Result.map SchemaArray
                    | false, _ -> Error(sprintf "%s is an array with no items schema" path)
                | "object" ->
                    let required =
                        match e.TryGetProperty "required" with
                        | true, r when r.ValueKind = JsonValueKind.Array -> [
                            for v in r.EnumerateArray() ->
                                if v.ValueKind = JsonValueKind.String then
                                    v.GetString()
                                else
                                    ""
                          ]
                        | _ -> []

                    let members =
                        match e.TryGetProperty "properties" with
                        | true, p when p.ValueKind = JsonValueKind.Object -> [ for m in p.EnumerateObject() -> m ]
                        | _ -> []

                    let names = members |> List.map _.Name |> Set.ofList

                    match required |> List.tryFind (fun r -> not (names.Contains r)) with
                    | Some missing -> Error(sprintf "%s requires '%s', which it does not declare" path missing)
                    | None ->
                        let rec fields acc (ms: JsonProperty list) =
                            match ms with
                            | [] -> Ok(SchemaObject(List.rev acc))
                            | m :: rest ->
                                match nodeAt (path + "." + m.Name) m.Value with
                                | Error e -> Error e
                                | Ok node ->
                                    fields
                                        (ToolSchema.field
                                            m.Name
                                            (descriptionOf m.Value)
                                            (List.contains m.Name required)
                                            node
                                         :: acc)
                                        rest

                        fields [] members
                | other -> Error(sprintf "%s has type '%s', which a tool schema does not carry" path other)
            | _ -> Error(sprintf "%s names no type" path)

    /// A parameter's schema as a tool schema, or why it does not decode.
    let schemaOf (parameter: MethodParameter) : Result<ToolSchemaNode, string> =
        try
            use document = JsonDocument.Parse parameter.Schema
            nodeAt parameter.Name document.RootElement
        with :? JsonException as e ->
            Error(sprintf "the schema is not JSON: %s" e.Message)

    // ── The declaration ───────────────────────────────────────────

    /// The description a parameter is declared with: its meaning, then its
    /// admissible range and its default where the card gives them.
    let parameterText (parameter: MethodParameter) : string =
        String.concat "" [
            parameter.Meaning
            match parameter.Range with
            | Some range -> sprintf " Admissible: %s." range
            | None -> ()
            match parameter.Default with
            | Some d -> sprintf " Default: %s." d
            | None -> ()
        ]

    /// The effects a card's tool declares: the read, and the write when its
    /// outputs assert facts.
    let effectsOf (card: MethodCard) : ToolEffectDeclaration =
        if card.Outputs.Facts.IsEmpty then
            ToolEffectDeclaration.readFacts
        else
            ToolEffectDeclaration.declare [ ReadFacts; ComputeFacts; WriteState FactEvents.SourceModule ]

    /// The declaration generated from a card and its decoded parameters:
    /// server-resident, on both surfaces, under the fact store's source.
    let definitionOf
        (card: MethodCard)
        (parameters: (MethodParameter * ToolSchemaNode) list)
        (effects: ToolEffectDeclaration)
        : AIToolDefinition =
        {
            Name = card.Signature.ToolName
            Description = card.Meaning.Summary
            Parameters =
                SpecTool.sliceParameters
                @ (parameters
                   |> List.map (fun (p, node) -> {
                       ToolSchema.parameter p.Name (parameterText p) p.Required node with
                           Default = p.Default
                   }))
            SourceModule = FactEvents.SourceModule
            EmitsActions = None
            Location = ServerResident
            Surface = Both
            IsLiveInterface = false
            ResultBudget = DefaultResultBudget
            Effects = effects
        }

    // ── The arguments ─────────────────────────────────────────────

    let rec private fits (node: ToolSchemaNode) (v: JsonElement) : bool =
        match node with
        | SchemaString [] -> v.ValueKind = JsonValueKind.String
        | SchemaString values -> v.ValueKind = JsonValueKind.String && List.contains (v.GetString()) values
        | SchemaNumber -> v.ValueKind = JsonValueKind.Number
        | SchemaInteger -> v.ValueKind = JsonValueKind.Number && (let ok, _ = v.TryGetInt64() in ok)
        | SchemaBoolean -> v.ValueKind = JsonValueKind.True || v.ValueKind = JsonValueKind.False
        | SchemaArray items ->
            v.ValueKind = JsonValueKind.Array
            && v.EnumerateArray() |> Seq.forall (fits items)
        | SchemaObject members ->
            v.ValueKind = JsonValueKind.Object
            && members
               |> List.forall (fun m ->
                   match v.TryGetProperty m.MemberName with
                   | true, x -> fits m.MemberSchema x
                   | false, _ -> not m.MemberRequired)

    let private expected (node: ToolSchemaNode) : string =
        match node with
        | SchemaString _ -> "a string"
        | SchemaNumber -> "a number"
        | SchemaInteger -> "an integer"
        | SchemaBoolean -> "true or false"
        | SchemaArray _ -> "an array, each element of its declared shape"
        | SchemaObject _ -> "an object of its declared members"

    /// The refusal an argument earns against its schema, if any.
    let internal argumentRefusal
        (methodId: string)
        (name: string)
        (node: ToolSchemaNode)
        (v: JsonElement)
        : MethodRefusal option =
        match node with
        | SchemaString values when
            not values.IsEmpty
            && v.ValueKind = JsonValueKind.String
            && not (List.contains (v.GetString()) values)
            ->
            Some(MethodRefusal.ArgumentNotAdmitted(methodId, name, values))
        | _ when not (fits node v) -> Some(MethodRefusal.ArgumentMistyped(methodId, name, expected node))
        | _ -> None

    let rec private canonical (sb: StringBuilder) (v: JsonElement) : unit =
        match v.ValueKind with
        | JsonValueKind.Object ->
            sb.Append '{' |> ignore

            v.EnumerateObject()
            |> Seq.sortWith (fun a b -> String.CompareOrdinal(a.Name, b.Name))
            |> Seq.iteri (fun i m ->
                if i > 0 then
                    sb.Append ',' |> ignore

                sb.Append(JsonSerializer.Serialize m.Name).Append(':') |> ignore
                canonical sb m.Value)

            sb.Append '}' |> ignore
        | JsonValueKind.Array ->
            sb.Append '[' |> ignore

            v.EnumerateArray()
            |> Seq.iteri (fun i x ->
                if i > 0 then
                    sb.Append ',' |> ignore

                canonical sb x)

            sb.Append ']' |> ignore
        | JsonValueKind.String -> sb.Append(JsonSerializer.Serialize(v.GetString())) |> ignore
        | _ -> sb.Append(v.GetRawText()) |> ignore

    /// The parameter hash of a call: SHA-256 over the definition hash and the
    /// arguments as canonical JSON (members ordered by name), lower-case hex.
    let parameterHash (definitionHash: string) (arguments: Map<string, JsonElement>) : string =
        let sb = StringBuilder()
        sb.Append(definitionHash).Append('\n').Append('{') |> ignore

        arguments
        |> Map.toList
        |> List.iteri (fun i (name, v) ->
            if i > 0 then
                sb.Append ',' |> ignore

            sb.Append(JsonSerializer.Serialize name).Append(':') |> ignore
            canonical sb v)

        sb.Append '}' |> ignore

        SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private defaultOf (text: string) : JsonElement =
        use document = JsonDocument.Parse text
        document.RootElement.Clone()

    // ── The tool over an evaluator ────────────────────────────────

    /// The text the model reads for a method's result: the method, the
    /// evaluator's summary, the card's diagnostics with their healthy ranges
    /// and its reading rules. Never a figure.
    let summaryOf (card: MethodCard) (evaluation: MethodEvaluation) : string =
        FactTool.serialize {|
            ``method`` = {|
                id = card.Identity.Id
                version = card.Identity.Version
                definitionHash = card.Identity.DefinitionHash
            |}
            summary = evaluation.Summary
            outputs = card.Outputs.Tables |> List.map _.Name
            diagnostics =
                card.Outputs.Diagnostics
                |> List.map (fun d -> {| name = d.Name; healthy = d.Healthy |})
            interpretation = {|
                rules = card.Interpretation.Rules
                say = card.Interpretation.Say
                neverSay = card.Interpretation.NeverSay
            |}
            note =
                "The result's figures are the server's evaluation, shown to the user; present them and state no number that is not an asserted fact."
        |}

    let private conditionNamed (card: MethodCard) (name: string) : MethodCondition option =
        card.Applicability.Preconditions @ card.Signature.Refusals
        |> List.tryFind (fun c -> c.Name = name)

    /// The spec tool a card becomes over an evaluator and its decoded
    /// parameters.
    let internal specToolOf
        (card: MethodCard)
        (evaluator: MethodEvaluator)
        (parameters: (MethodParameter * ToolSchemaNode) list)
        : SpecTool<MethodCall, MethodRefusal, MethodEvaluation> =
        let methodId = card.Identity.Id

        let decode (root: JsonElement) : Result<SpecRequest<MethodCall>, MethodRefusal> =
            match SpecTool.sliceOf root with
            | Error door -> Error(MethodRefusal.Door door)
            | Ok slice ->
                let arguments =
                    parameters
                    |> List.choose (fun (p, _) ->
                        match root.TryGetProperty p.Name with
                        | true, v when v.ValueKind <> JsonValueKind.Null -> Some(p.Name, v.Clone())
                        | _ -> p.Default |> Option.map (fun d -> p.Name, defaultOf d))
                    |> Map.ofList

                Ok {
                    Slice = slice
                    Spec = {
                        Method = card.Identity
                        Slice = slice
                        Arguments = arguments
                        ParameterHash = parameterHash card.Identity.DefinitionHash arguments
                    }
                }

        let validate (_: ResolvedScope) (_: string) (request: SpecRequest<MethodCall>) =
            let call = request.Spec

            let signature = [
                for p, node in parameters do
                    match call.Arguments.TryFind p.Name with
                    | None when p.Required -> MethodRefusal.MissingArgument(methodId, p.Name)
                    | None -> ()
                    | Some v ->
                        match argumentRefusal methodId p.Name node v with
                        | Some refusal -> refusal
                        | None -> ()
            ]

            let unmet =
                if not signature.IsEmpty then
                    []
                else
                    card.Applicability.Preconditions
                    |> List.filter (fun c ->
                        match evaluator.Checks.TryFind c.Name with
                        | Some holds -> not (holds call)
                        | None -> false)
                    |> List.map (fun c -> MethodRefusal.Unmet(methodId, c))

            match signature @ unmet with
            | [] -> Ok call
            | refusals -> Error refusals

        let evaluate (read: SpecRead) (request: SpecRequest<MethodCall>) = async {
            match! evaluator.Evaluate read request.Spec with
            | Ok evaluation -> return Ok evaluation
            | Error name ->
                match conditionNamed card name with
                | Some condition -> return Error(MethodRefusal.Unmet(methodId, condition))
                | None -> return Error(MethodRefusal.Undeclared(methodId, name))
        }

        {
            Definition = definitionOf card parameters (effectsOf card)
            OperationId = methodId
            Version = card.Identity.Version
            Decode = decode
            Validate = validate
            Evaluate = evaluate
            Hash = _.ParameterHash
            Project = _.Result
            Summary = summaryOf card
            OfDoor = MethodRefusal.Door
        }

    let private built (tool: SpecTool<'Spec, 'Refusal, 'Result>) : MethodToolBuild = {
        Definition = tool.Definition
        Answer = SpecTool.answerText tool
        Executor = SpecTool.executor tool
    }

    /// A card bound to an evaluator its registering component supplies: a
    /// card read from a package, or one built in code. Checked when it is
    /// registered.
    let bind (card: MethodCard) (evaluator: MethodEvaluator) : MethodBinding =
        MethodBinding(
            card,
            evaluator.DefinitionHash,
            evaluator.Checks |> Map.toList |> List.map fst,
            None,
            None,
            fun parameters -> built (specToolOf card evaluator parameters)
        )

    /// A card bound to an existing spec tool, which keeps its own decoding,
    /// validation, evaluation and effects and takes its name, description and
    /// parameters from the card. The card's id and version must be the tool's
    /// operation id and version, and `definitionHash` is the definition the
    /// tool implements; `grammar` is the text the tool's spec is read under,
    /// which the card's profile entry carries.
    let ofSpecTool
        (card: MethodCard)
        (definitionHash: string)
        (grammar: string option)
        (tool: SpecTool<'Spec, 'Refusal, 'Result>)
        : MethodBinding =
        MethodBinding(
            card,
            definitionHash,
            [],
            Some(tool.OperationId, tool.Version),
            grammar,
            fun parameters ->
                built {
                    tool with
                        Definition = definitionOf card parameters tool.Definition.Effects
                }
        )