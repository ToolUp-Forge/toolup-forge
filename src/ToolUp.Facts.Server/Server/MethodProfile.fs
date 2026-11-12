// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text
open System.Text.Json
open ToolUp.Platform

// ─── The method profile ──────────────────────────────────────────────
//
// The registered methods as the assistant meets them: one prompt fragment,
// rendered once at registration, with one entry per registered card (what it
// does, the question it answers, when it applies and when it does not, how to
// read its result, and the grammar its spec is read under where its tool has
// one), and one tool per card beside `describe_method`, which answers a
// card's full text on demand: its signature, outputs with their healthy
// ranges, and the worked examples the fragment omits.
//
// The profile is the tool list. A card that is not registered is neither in
// the fragment nor callable, and a registration with no card has an empty
// fragment and no tool, so a deployment that registers none sends the prompt
// it sent before. Serve the fragment through the AI tier's prompt-fragment
// seam (`PromptFragment.create profile.Description profile.Fragment`), whose
// builder reports its size at boot.

/// The token estimate a fragment's size is reported in.
module internal MethodTokens =

    /// Tokens at four characters a token (`RequestTokenEstimator`): advisory,
    /// not a tokeniser's count.
    let estimate (text: string) : int =
        int (
            Math.Ceiling(ToolUp.Platform.Jobs.TeamQuotaPolicy.RequestTokenEstimator.estimateChars text.Length: decimal)
        )

/// One registered method: its card, the grammar its tool's spec is read
/// under, and its tool.
type RegisteredMethod = {
    /// The card.
    Card: MethodCard
    /// The grammar the card's entry carries, when its tool has one.
    Grammar: string option
    /// The tool registration built.
    Tool: MethodToolBuild
}

/// The methods a deployment registered, with the profile fragment rendered
/// once. Only `MethodRegistry.register` makes one.
[<Sealed>]
type MethodProfile internal (methods: RegisteredMethod list, fragment: string, describe: MethodToolBuild option) =
    /// The registered methods, in registration order.
    member _.Methods = methods
    /// The prompt fragment, rendered once at registration. Empty when no
    /// method is registered.
    member _.Fragment = fragment
    /// The fragment's size in UTF-8 bytes.
    member _.FragmentBytes = Encoding.UTF8.GetByteCount fragment

    /// The fragment's size in tokens, estimated at four characters a token:
    /// advisory, not a tokeniser's count.
    member _.FragmentTokens = MethodTokens.estimate fragment

    /// The line the boot log names the fragment by.
    member _.Description = sprintf "Method profile: %d method(s)" methods.Length

    /// The tools the profile registers: one per method, then
    /// `describe_method`. None when no method is registered.
    member _.Tools: MethodToolBuild list =
        (methods |> List.map _.Tool) @ Option.toList describe

/// The fragment and `describe_method`.
[<RequireQualifiedAccess>]
module MethodProfile =

    /// `describe_method`'s name.
    [<Literal>]
    let DescribeName = "describe_method"

    let private code (s: string) = "`" + s + "`"

    let private determinism (d: MethodDeterminism) : string =
        match d with
        | MethodDeterminism.Pinned -> "pinned: the same inputs give the same result on every host"
        | MethodDeterminism.Seeded -> "stochastic, seeded and replayable"
        | MethodDeterminism.HostRounded -> "deterministic up to the host's rounding"

    /// One card's entry in the fragment: never its examples.
    let entry (method: RegisteredMethod) : string =
        let card = method.Card
        let sb = StringBuilder()
        let line (s: string) = sb.Append(s).Append('\n') |> ignore

        let listed (label: string) (items: string list) =
            if not items.IsEmpty then
                line (sprintf "- %s: %s" label (String.concat "; " items))

        line (
            sprintf
                "### %s — method %s v%s (%s)"
                (code card.Signature.ToolName)
                (code card.Identity.Id)
                card.Identity.Version
                (determinism card.Identity.Determinism)
        )

        if not (String.IsNullOrWhiteSpace card.Meaning.Answers) then
            line (sprintf "- Answers: %s" card.Meaning.Answers)

        listed "Assumes" card.Meaning.Assumes
        listed "Applies when" (card.Applicability.Preconditions |> List.map _.Statement)
        listed "Does not apply when" card.Applicability.DoesNotApply

        listed
            "Instead"
            (card.Applicability.Alternatives
             |> List.map (fun a -> sprintf "%s when %s" (code a.Method) a.PreferWhen))

        if card.Applicability.Choice = MethodChoice.Judgement then
            line "- Choosing it is a judgement: propose it, say why, and let the user decide."

        listed "Reading the result" card.Interpretation.Rules
        listed "Say" card.Interpretation.Say
        listed "Never say" card.Interpretation.NeverSay

        match method.Grammar with
        | Some grammar ->
            line ""
            line grammar
        | None -> ()

        sb.ToString().TrimEnd('\n')

    /// The fragment for `methods`: a heading, the rule that a method is called
    /// as its own tool, and one entry per method. Empty for no method. Pure;
    /// registration renders it once and keeps it.
    let fragment (methods: RegisteredMethod list) : string =
        match methods with
        | [] -> ""
        | _ ->
            String.concat "\n\n" [
                "## Methods (method profile)"
                "Each method below is its own tool, called over one slice of one fact table. A call outside a method's conditions is refused by name with what to repair. Call "
                + code DescribeName
                + " with a method's id for its arguments, its outputs and their healthy ranges, and worked examples."
                yield! methods |> List.map entry
            ]

    /// Each method's entry, by card id, with its estimated token size.
    let entryTokens (profile: MethodProfile) : (string * int) list =
        profile.Methods
        |> List.map (fun m -> m.Card.Identity.Id, MethodTokens.estimate (entry m))

    // ── describe_method ───────────────────────────────────────────

    let private condition (c: MethodCondition) = {|
        name = c.Name
        statement = c.Statement
        remedy = c.Remedy
    |}

    /// A card's full text, as `describe_method` answers it.
    let describe (card: MethodCard) : string =
        FactTool.serialize {|
            ``method`` = {|
                id = card.Identity.Id
                version = card.Identity.Version
                definitionHash = card.Identity.DefinitionHash
                package = card.Identity.Package
                determinism = determinism card.Identity.Determinism
            |}
            tool = card.Signature.ToolName
            meaning = {|
                summary = card.Meaning.Summary
                answers = card.Meaning.Answers
                assumes = card.Meaning.Assumes
            |}
            applicability = {|
                preconditions = card.Applicability.Preconditions |> List.map condition
                doesNotApply = card.Applicability.DoesNotApply
                alternatives =
                    card.Applicability.Alternatives
                    |> List.map (fun a -> {|
                        ``method`` = a.Method
                        preferWhen = a.PreferWhen
                    |})
                choice =
                    match card.Applicability.Choice with
                    | MethodChoice.Mechanical -> "mechanical"
                    | MethodChoice.Judgement -> "judgement"
            |}
            arguments =
                card.Signature.Parameters
                |> List.map (fun p -> {|
                    name = p.Name
                    meaning = p.Meaning
                    required = p.Required
                    ``default`` = p.Default |> Option.defaultValue ""
                    range = p.Range |> Option.defaultValue ""
                    role = p.Role |> Option.defaultValue ""
                |})
            refusals = card.Signature.Refusals |> List.map condition
            outputs = {|
                tables =
                    card.Outputs.Tables
                    |> List.map (fun t -> {|
                        name = t.Name
                        meaning = t.Meaning
                        columns = t.Columns |> List.map (fun c -> {| name = c.Name; meaning = c.Meaning |})
                    |})
                diagnostics =
                    card.Outputs.Diagnostics
                    |> List.map (fun d -> {|
                        name = d.Name
                        meaning = d.Meaning
                        healthy = d.Healthy
                    |})
                warnings = card.Outputs.Warnings
                facts = card.Outputs.Facts
            |}
            interpretation = {|
                rules = card.Interpretation.Rules
                say = card.Interpretation.Say
                neverSay = card.Interpretation.NeverSay
            |}
            presentation = {|
                blocks = card.Presentation.Blocks
                baseCard = card.Presentation.BaseCard |> Option.defaultValue ""
            |}
            examples =
                card.Examples
                |> List.map (fun e -> {|
                    title = e.Title
                    call = e.Call
                    result = e.Result
                    explanation = e.Explanation
                |})
        |}

    /// `describe_method`'s answer for `argsJson` over `methods`: the card
    /// named by id or tool name, or a refusal naming the registered ones.
    let describeAnswer (methods: RegisteredMethod list) (argsJson: string) : string =
        let named =
            try
                use document = JsonDocument.Parse argsJson

                match document.RootElement.TryGetProperty "method" with
                | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                | _ -> None
            with :? JsonException ->
                None

        let registered =
            methods
            |> List.map (fun m -> sprintf "'%s'" m.Card.Identity.Id)
            |> String.concat ", "

        match named with
        | None -> sprintf "%s refused the request: name the method as 'method', one of %s." DescribeName registered
        | Some name ->
            match
                methods
                |> List.tryFind (fun m -> m.Card.Identity.Id = name || m.Card.Signature.ToolName = name)
            with
            | Some m -> describe m.Card
            | None ->
                sprintf
                    "%s refused the request: no method '%s' is registered; the registered methods are %s."
                    DescribeName
                    name
                    registered

    /// `describe_method` over `methods`: it reads no fact.
    let describeTool (methods: RegisteredMethod list) : MethodToolBuild =
        let answer = describeAnswer methods

        {
            Definition = {
                Name = DescribeName
                Description =
                    "Read a registered method's full card: what it does, when it applies, its arguments with their defaults and ranges, its outputs and the ranges that are healthy, how to read them, and worked examples. Call it before a method you have not used in this conversation, or when a result needs reading."
                Parameters = [
                    ToolSchema.parameter
                        "method"
                        "The method's id."
                        true
                        (ToolSchema.enumOf (methods |> List.map _.Card.Identity.Id))
                ]
                SourceModule = FactEvents.SourceModule
                EmitsActions = None
                Location = ServerResident
                Surface = Both
                IsLiveInterface = false
                ResultBudget = DefaultResultBudget
                Effects = ToolEffectDeclaration.declare []
            }
            Answer = fun _ _ _ argsJson -> async.Return(answer argsJson)
            Executor = fun _ argsJson -> async.Return(answer argsJson)
        }

    /// The profile over registered methods: the fragment rendered once, and
    /// `describe_method` beside the methods' tools when there is one.
    let internal create (methods: RegisteredMethod list) : MethodProfile =
        MethodProfile(methods, fragment methods, (if methods.IsEmpty then None else Some(describeTool methods)))