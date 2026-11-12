// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text.Json
open Microsoft.FSharp.Reflection
open ToolUp.Platform

// ─── The method registry ─────────────────────────────────────────────
//
// A deployment registers method cards, each bound to what runs it
// (`MethodTool.bind` over an evaluator, `MethodTool.ofSpecTool` over an
// existing spec tool), and gets the method profile: one tool per card, the
// prompt fragment and `describe_method` (`MethodProfile`). A card built in
// code by another component registers exactly as a card read from a package
// does, under the same refusals. Compose the profile with
// `FactsCompose.withMethods`.
//
// Registration refuses, each by name and all together, and registers
// nothing when it refuses:
//
//   - a card with no id or no meaning, or whose id or tool name repeats
//     another card's (or `describe_method`);
//   - a tool name that is not lower-case letters, digits and `_`;
//   - a parameter whose schema does not decode, whose default does not fit
//     it, whose name repeats or is one of the slice arguments every method
//     takes;
//   - a card whose definition hash is not the one its binding implements, or
//     (over an existing spec tool) whose id and version are not the tool's
//     operation and version;
//   - a condition whose name is not an identifier, repeats, or is a name the
//     door or the method tool already refuse with, or whose statement or
//     remedy is empty or more than one line; and a check naming no declared
//     precondition.

/// Why a registration was refused. Nothing was registered.
[<RequireQualifiedAccess>]
type MethodRegistrationRefusal =
    /// A card has no id.
    | NoId
    /// Two cards share an id.
    | DuplicateId of methodId: string
    /// A card has no meaning paragraph, which is its tool's description.
    | NoMeaning of methodId: string
    /// A card's tool name is not lower-case letters, digits and `_`.
    | ToolNameInvalid of methodId: string * toolName: string
    /// A card's tool name is another card's, or `describe_method`.
    | DuplicateToolName of methodId: string * toolName: string
    /// A parameter's name is empty, or is a slice argument every method takes.
    | ParameterNameReserved of methodId: string * parameter: string
    /// A card declares a parameter twice.
    | DuplicateParameter of methodId: string * parameter: string
    /// A parameter's schema does not decode as a tool schema.
    | SchemaDoesNotDecode of methodId: string * parameter: string * reason: string
    /// A parameter's default is not JSON, or does not fit its schema.
    | DefaultDoesNotFit of methodId: string * parameter: string * reason: string
    /// The card's definition hash is not the one its binding implements.
    | DefinitionHashUnbound of methodId: string * cardHash: string * boundHash: string
    /// The card's id and version are not the bound spec tool's operation and
    /// version.
    | IdentityUnbound of methodId: string * version: string * operation: string * operationVersion: string
    /// A condition's name is not an identifier, or is one the door or the
    /// method tool already refuse with.
    | ConditionNameInvalid of methodId: string * name: string
    /// A card declares a condition name twice.
    | DuplicateCondition of methodId: string * name: string
    /// A condition's statement or remedy is empty or more than one line.
    | ConditionTextInvalid of methodId: string * name: string
    /// The binding checks a precondition the card does not declare.
    | CheckUndeclared of methodId: string * name: string

/// Reading a `MethodRegistrationRefusal`.
[<RequireQualifiedAccess>]
module MethodRegistrationRefusal =

    /// One sentence naming what to repair.
    let describe (refusal: MethodRegistrationRefusal) : string =
        match refusal with
        | MethodRegistrationRefusal.NoId -> "a method card has no id"
        | MethodRegistrationRefusal.DuplicateId id -> sprintf "two method cards have the id '%s'" id
        | MethodRegistrationRefusal.NoMeaning id ->
            sprintf "method '%s' has no meaning paragraph, which is its tool's description" id
        | MethodRegistrationRefusal.ToolNameInvalid(id, tool) ->
            sprintf "method '%s' names the tool '%s'; a tool name is lower-case letters, digits and '_'" id tool
        | MethodRegistrationRefusal.DuplicateToolName(id, tool) ->
            sprintf "method '%s' names the tool '%s', which another registered tool already has" id tool
        | MethodRegistrationRefusal.ParameterNameReserved(id, parameter) ->
            sprintf
                "method '%s' declares the parameter '%s', which is empty or a slice argument every method takes"
                id
                parameter
        | MethodRegistrationRefusal.DuplicateParameter(id, parameter) ->
            sprintf "method '%s' declares the parameter '%s' twice" id parameter
        | MethodRegistrationRefusal.SchemaDoesNotDecode(id, parameter, reason) ->
            sprintf "method '%s': the schema of parameter '%s' does not decode: %s" id parameter reason
        | MethodRegistrationRefusal.DefaultDoesNotFit(id, parameter, reason) ->
            sprintf "method '%s': the default of parameter '%s' does not fit: %s" id parameter reason
        | MethodRegistrationRefusal.DefinitionHashUnbound(id, cardHash, boundHash) ->
            sprintf
                "method '%s' binds the definition '%s', but its evaluator implements '%s'"
                id
                (if String.IsNullOrWhiteSpace cardHash then
                     "(none)"
                 else
                     cardHash)
                boundHash
        | MethodRegistrationRefusal.IdentityUnbound(id, version, operation, operationVersion) ->
            sprintf
                "method '%s' v%s is bound to a tool asserting as '%s' v%s; a card over a spec tool is that tool's operation and version"
                id
                version
                operation
                operationVersion
        | MethodRegistrationRefusal.ConditionNameInvalid(id, name) ->
            sprintf
                "method '%s' names a condition '%s'; a condition name is an identifier (an upper-case letter, then letters and digits) the method tool does not already refuse with"
                id
                name
        | MethodRegistrationRefusal.DuplicateCondition(id, name) ->
            sprintf "method '%s' declares the condition '%s' twice" id name
        | MethodRegistrationRefusal.ConditionTextInvalid(id, name) ->
            sprintf "method '%s': condition '%s' needs a one-line statement and a one-line remedy" id name
        | MethodRegistrationRefusal.CheckUndeclared(id, name) ->
            sprintf
                "method '%s' is bound with a check for '%s', which its card does not declare as a precondition"
                id
                name

/// Registering method cards.
[<RequireQualifiedAccess>]
module MethodRegistry =

    /// The names a method tool already refuses with: the door's and the
    /// method tool's own. No card condition may take one.
    let reservedNames: Set<string> =
        set [
            yield! FSharpType.GetUnionCases typeof<SpecDoorRefusal> |> Array.map _.Name
            "MissingArgument"
            "ArgumentMistyped"
            "ArgumentNotAdmitted"
            "UndeclaredRefusal"
        ]

    let private sliceArguments: Set<string> =
        SpecTool.sliceParameters |> List.map _.Name |> Set.ofList

    let private isIdentifier (name: string) : bool =
        not (String.IsNullOrEmpty name)
        && Char.IsAsciiLetterUpper name[0]
        && name |> Seq.forall Char.IsAsciiLetterOrDigit

    let private isToolName (name: string) : bool =
        not (String.IsNullOrEmpty name)
        && name.Length <= 64
        && Char.IsAsciiLetterLower name[0]
        && name
           |> Seq.forall (fun c -> Char.IsAsciiLetterLower c || Char.IsAsciiDigit c || c = '_')

    let private oneLine (s: string) : bool =
        not (String.IsNullOrWhiteSpace s) && not (s.Contains '\n' || s.Contains '\r')

    let private defaultFits (id: string) (p: MethodParameter) (node: ToolSchemaNode) =
        match p.Default with
        | None -> None
        | Some text ->
            try
                use document = JsonDocument.Parse text

                MethodTool.argumentRefusal id p.Name node document.RootElement
                |> Option.map (fun r ->
                    MethodRegistrationRefusal.DefaultDoesNotFit(id, p.Name, (r :> ISpecRefusal).Remedy))
            with :? JsonException ->
                Some(MethodRegistrationRefusal.DefaultDoesNotFit(id, p.Name, "it is not JSON"))

    /// One binding's refusals, beside the parameters it decoded.
    let private check
        (binding: MethodBinding)
        : MethodRegistrationRefusal list * (MethodParameter * ToolSchemaNode) list =
        let card = binding.Card
        let id = card.Identity.Id

        let decoded =
            card.Signature.Parameters |> List.map (fun p -> p, MethodTool.schemaOf p)

        let conditions = card.Applicability.Preconditions @ card.Signature.Refusals

        let preconditions =
            card.Applicability.Preconditions |> List.map _.Name |> Set.ofList

        let refusals = [
            if String.IsNullOrWhiteSpace card.Meaning.Summary then
                MethodRegistrationRefusal.NoMeaning id
            if not (isToolName card.Signature.ToolName) then
                MethodRegistrationRefusal.ToolNameInvalid(id, card.Signature.ToolName)
            for name, count in card.Signature.Parameters |> List.countBy _.Name do
                if String.IsNullOrWhiteSpace name || sliceArguments.Contains name then
                    MethodRegistrationRefusal.ParameterNameReserved(id, name)
                elif count > 1 then
                    MethodRegistrationRefusal.DuplicateParameter(id, name)
            for p, node in decoded do
                match node with
                | Error reason -> MethodRegistrationRefusal.SchemaDoesNotDecode(id, p.Name, reason)
                | Ok node ->
                    match defaultFits id p node with
                    | Some refusal -> refusal
                    | None -> ()
            if
                card.Identity.DefinitionHash <> binding.DefinitionHash
                || String.IsNullOrWhiteSpace card.Identity.DefinitionHash
            then
                MethodRegistrationRefusal.DefinitionHashUnbound(
                    id,
                    card.Identity.DefinitionHash,
                    binding.DefinitionHash
                )
            match binding.Operation with
            | Some(operation, version) when operation <> id || version <> card.Identity.Version ->
                MethodRegistrationRefusal.IdentityUnbound(id, card.Identity.Version, operation, version)
            | _ -> ()
            for name, count in conditions |> List.countBy _.Name do
                if not (isIdentifier name) || reservedNames.Contains name then
                    MethodRegistrationRefusal.ConditionNameInvalid(id, name)
                elif count > 1 then
                    MethodRegistrationRefusal.DuplicateCondition(id, name)
            for c in conditions do
                if not (oneLine c.Statement && oneLine c.Remedy) then
                    MethodRegistrationRefusal.ConditionTextInvalid(id, c.Name)
            for name in binding.Checks do
                if not (preconditions.Contains name) then
                    MethodRegistrationRefusal.CheckUndeclared(id, name)
        ]

        refusals,
        decoded
        |> List.choose (fun (p, node) -> node |> Result.toOption |> Option.map (fun n -> p, n))

    /// Register `bindings`: the method profile, with one tool per card, the
    /// fragment rendered once and `describe_method`; or every refusal, by
    /// name, with nothing registered. No binding registers an empty profile,
    /// whose fragment is empty and which registers no tool.
    let register (bindings: MethodBinding list) : Result<MethodProfile, MethodRegistrationRefusal list> =
        let checkedBindings = bindings |> List.map (fun b -> b, check b)

        let across = [
            for b in bindings do
                if String.IsNullOrWhiteSpace b.Card.Identity.Id then
                    MethodRegistrationRefusal.NoId
            for id, count in bindings |> List.countBy _.Card.Identity.Id do
                if count > 1 && not (String.IsNullOrWhiteSpace id) then
                    MethodRegistrationRefusal.DuplicateId id
            let byTool =
                (MethodProfile.DescribeName, "")
                :: (bindings |> List.map (fun b -> b.Card.Signature.ToolName, b.Card.Identity.Id))

            for tool, owners in byTool |> List.groupBy fst do
                let ids = owners |> List.map snd |> List.filter (fun id -> id <> "")

                // Against describe_method every card naming it is refused;
                // between cards, every one after the first.
                let refused =
                    match ids with
                    | _ when tool = MethodProfile.DescribeName -> ids
                    | [] -> []
                    | _ :: later -> later

                for id in refused do
                    MethodRegistrationRefusal.DuplicateToolName(id, tool)
        ]

        match across @ (checkedBindings |> List.collect (fun (_, (refusals, _)) -> refusals)) with
        | [] ->
            checkedBindings
            |> List.map (fun (b, (_, parameters)) -> {
                Card = b.Card
                Grammar = b.Grammar
                Tool = b.Build parameters
            })
            |> MethodProfile.create
            |> Ok
        | refusals -> Error refusals