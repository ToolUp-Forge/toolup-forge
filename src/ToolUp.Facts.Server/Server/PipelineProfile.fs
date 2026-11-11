// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Text
open Microsoft.FSharp.Reflection
open Fuaran.Core
open Fuaran.Compute
open ToolUp.Platform

// ─── The assistant's pipeline profile ─────────────────────────────────
//
// A deployment registers the `Fuaran.Compute` subset its assistant may write
// in an `evaluate_pipeline` call: the verbs (`Transform` cases), the
// expressions (`ColExpr` cases), the binary operators and the functions, and,
// per verb, the fields a step of it may write. It is the language profile's
// mechanism (`ToolUp.AI.LanguageProfile`) for computations: the trade between
// prompt size and what the model can write is the deployment's to make.
//
// **The vocabulary is the codec's.** Every name is a wire spelling read off
// `DataFrameCodec`'s own encoding of an example of each case, so a profile
// cannot name a spelling the decoder does not know, and the fragment shows
// the model only examples the decoder reads back.
//
// **The fragment is rendered once.** `register` renders the profile into a
// prompt fragment and keeps it; the AI tier serves that one string on every
// turn (`SystemPromptBuilder.promptFragments`). This assembly takes no AI
// server dependency for it: the fragment is a string.
//
// **The profile is the grammar.** `admit` reads a decoded pipeline before
// anything is read from the store and refuses, naming the step and what the
// profile admits, a verb, expression, operator, function or field outside it.
// A deployment that registers no profile is checked against the full algebra
// exactly as before.
//
// **A join only through a relation check.** The slice is the pipeline's only
// source, so a profile admits no step that reads another one, with one
// exception: `join`, admitted only when the profile carries a relation check
// (`PipelineRelationCheck`). The walk asks the check about every join step for
// the caller and refuses the one it refuses, by name, before evaluation. A
// joined table is named by its id (a `Ref` source), never written into the
// pipeline, and is read through the same gated slice read as the request's
// table.

/// A join step, as a relation check reads it.
type PipelineJoin = {
    /// The step's 1-based position in the pipeline as written.
    Step: int
    /// The fact table the pipeline reads: the request's `table`.
    Table: string
    /// The fact table the step joins, by id.
    Joined: string
    /// The `(left column, right column)` key pairs.
    On: (string * string) list
    /// The join kind's wire spelling (`inner`, `left`, `right`, `outer`,
    /// `semi`, `anti`).
    Kind: string
}

/// Who a pipeline is evaluated for: the scope its slices are read in and
/// the principal they are judged for.
type PipelineCaller = {
    /// The scope the slices are read in.
    Scope: ResolvedScope
    /// The principal the gate judges every cell for.
    Principal: string
}

/// A deployment's relation check: admits a join step for the caller, or
/// refuses it with the sentence the model reads (naming the relation it
/// lacks). Run in the static walk, before anything is evaluated.
type PipelineRelationCheck = PipelineCaller -> PipelineJoin -> Result<unit, string>

/// A deployment's pipeline profile, before registration: the verbs,
/// expressions, binary operators and functions the assistant may write, by
/// their wire spellings, the fields a step of a narrowed verb may write, and
/// the relation check a join needs. Built from `PipelineProfile.defaultProfile`
/// or `PipelineProfile.named`, then checked by `PipelineProfile.register`.
type PipelineProfile = {
    /// The profile's name, reported in the fragment and the boot log.
    Name: string
    /// The admitted verbs: a step's `$type` (`filter`, `groupBy`, …).
    Steps: Set<string>
    /// The admitted expressions: an expression's `$type` (`col`, `lit`, …).
    Expressions: Set<string>
    /// The admitted binary operators: a `binary` expression's `op`.
    Operators: Set<string>
    /// The admitted functions: an `apply` expression's `fn`.
    Functions: Set<string>
    /// Per verb, the fields a step of it may write. A verb absent here may
    /// write every field it has.
    Fields: Map<string, Set<string>>
    /// The check a join step must pass. A profile admits `join` only with one.
    RelationCheck: PipelineRelationCheck option
}

/// Why a profile was refused at registration. Nothing was registered.
[<RequireQualifiedAccess>]
type PipelineProfileRefusal =
    /// The profile has no name.
    | NoName
    /// The profile admits no verb.
    | NoSteps
    /// The algebra has no verb of this spelling.
    | UnknownStep of step: string
    /// The algebra has no expression of this spelling.
    | UnknownExpression of expression: string
    /// The algebra has no binary operator of this spelling.
    | UnknownOperator of operator: string
    /// The algebra has no function of this spelling.
    | UnknownFunction of fn: string
    /// The verb has no field of this name.
    | UnknownField of step: string * field: string
    /// Fields are narrowed for a verb the profile does not admit.
    | FieldsOfUnadmittedStep of step: string
    /// The verb reads another source, which only a join may, through a
    /// relation check.
    | ReadsAnotherSource of step: string
    /// The expression needs a binding no evaluation gives (`param`, `now`).
    | Unbindable of expression: string
    /// The profile admits `join` and supplies no relation check.
    | JoinWithoutRelationCheck

/// A profile that passed registration, with its prompt fragment rendered
/// once. Only `PipelineProfile.register` makes one.
[<Sealed>]
type RegisteredPipelineProfile internal (profile: PipelineProfile, fragment: string) =
    /// The profile as registered.
    member _.Profile = profile
    /// The prompt fragment, rendered once at registration.
    member _.Fragment = fragment
    /// The fragment's size in UTF-8 bytes, as the boot log reports it.
    member _.FragmentBytes = Encoding.UTF8.GetByteCount fragment

    /// The fragment's size in tokens, estimated at four characters a token
    /// (`RequestTokenEstimator`): advisory, not a tokeniser's count.
    member _.FragmentTokens =
        int (
            Math.Ceiling(
                ToolUp.Platform.Jobs.TeamQuotaPolicy.RequestTokenEstimator.estimateChars fragment.Length: decimal
            )
        )

    /// The line the boot log names the fragment by.
    member _.Description =
        sprintf "Pipeline profile '%s': %d verb(s)" profile.Name profile.Steps.Count

/// The registration builder, the fragment and the grammar.
[<RequireQualifiedAccess>]
module PipelineProfile =

    // ── The vocabulary, read off the codec ────────────────────────

    let private member' (name: string) (v: JVal) : JVal option =
        match v with
        | JObj fields -> fields |> List.tryPick (fun (k, x) -> if k = name then Some x else None)
        | _ -> None

    let private text (v: JVal option) : string =
        match v with
        | Some(JStr s) -> s
        | _ -> ""

    /// A step's wire spelling: its encoded `$type`.
    let stepName (step: Transform) : string =
        DataFrameCodec.encodeTransform step |> member' "$type" |> text

    /// An expression's wire spelling: its encoded `$type`.
    let expressionName (e: ColExpr) : string =
        DataFrameCodec.encodeExpr e |> member' "$type" |> text

    /// A binary operator's wire spelling: a `binary` expression's `op`.
    let operatorName (op: BinOp) : string =
        DataFrameCodec.encodeExpr (Binary(op, Col "a", Col "b")) |> member' "op" |> text

    /// A function's wire spelling: an `apply` expression's `fn`.
    let functionName (fn: ScalarFn) : string =
        DataFrameCodec.encodeExpr (ApplyFn(fn, [])) |> member' "fn" |> text

    let private rounding: Rounding = {
        Scale = Slot.Lit 2
        Mode = RoundingMode.HalfEven
    }

    /// One example of each verb, in the order the fragment lists them. A verb
    /// may have more than one when an example cannot show every field it has
    /// (a window's `n` belongs to `ntile` only); the fragment shows the first.
    let stepExamples: Transform list = [
        Filter(Binary(Gt, Col "revenue", Lit(Decimal "1000")))
        Project [ "subject", "subject"; "revenue", "value" ]
        Derive("net", Binary(Sub, Col "revenue", Lit(Decimal "100")))
        GroupBy(
            [ "subject" ],
            [
                {
                    Name = "total"
                    Fn = Sum
                    Of = "revenue"
                }
            ]
        )
        Transform.sortBy [ "total", Desc ]
        Transform.limit 10 0
        Window {
            PartitionBy = [ "subject" ]
            OrderBy = [ "period_from", Asc ]
            Fn = CumulSum
            Of = "revenue"
            As = "running_total"
        }
        Window {
            PartitionBy = []
            OrderBy = [ "total", Desc ]
            Fn = NTile 4
            Of = "total"
            As = "quartile"
        }
        Join(Ref "<fact table id>", [ "subject", "subject"; "period_from", "period_from" ], Left)
        Pivot {
            Index = [ "subject" ]
            On = "period_label"
            Values = "revenue"
            Agg = Sum
        }
        Unpivot([ "subject" ], [ "revenue"; "margin" ])
        Distinct
        Define {
            Name = "after_tax"
            Params = [ { Name = "x"; Default = None } ]
            Body = Binary(Mul, Arg "x", Lit(Decimal "0.8"))
        }
        Fit(FitSpec.ols "revenue" [ "margin", Col "margin" ])
        Union(Ref "<fact table id>")
        Intersect(Ref "<fact table id>")
        Except(Ref "<fact table id>")
        Predict(PredictSpec.ofTraining (FitSpec.ols "revenue" [ "margin", Col "margin" ]) (Ref "<fact table id>"))
    ]

    /// One example of each expression, in the order the fragment lists them.
    /// `in` covers both a literal list and a parameter list; the fragment
    /// shows the literal one.
    let expressionExamples: ColExpr list = [
        Col "revenue"
        Lit(Decimal "1000")
        Binary(Mul, Col "revenue", Lit(Decimal "1.2"))
        Coalesce [ Col "margin"; Lit(Decimal "0") ]
        Case([ Binary(Gt, Col "revenue", Lit(Decimal "1000")), Lit(Str "high") ], Lit(Str "low"))
        Cast(FloatType, Col "revenue")
        IsNull(Col "margin")
        InList(Col "subject", [ Lit(Str "s1"); Lit(Str "s2") ])
        ApplyFn(Round, [ Col "revenue" ])
        Not(IsNull(Col "margin"))
        Quotient(Col "revenue", Col "units", rounding)
        Rounded(Col "revenue", rounding)
        yield!
            match Unit.parse "km" with
            | Ok km -> [ Convert(Col "distance", km) ]
            | Error _ -> []
        Interpolate(Col "revenue", [ 0.0, 0.0; 1000.0, 1.0 ], InterpKind.Linear, InterpOutside.Clamp)
        Call("after_tax", [ Col "revenue" ])
        Arg "x"
        Param "threshold"
        InParam(Col "subject", "sites")
        Now NowGrain.Date
    ]

    let private nullaryCases<'T> () : 'T list =
        FSharpType.GetUnionCases typeof<'T>
        |> Array.filter (fun c -> c.GetFields().Length = 0)
        |> Array.map (fun c -> FSharpValue.MakeUnion(c, [||]) :?> 'T)
        |> List.ofArray

    let private distinctNames (names: string list) : string list =
        names |> List.filter (fun n -> n <> "") |> List.distinct

    /// Every verb of the algebra, by wire spelling, in example order.
    let knownSteps: string list = stepExamples |> List.map stepName |> distinctNames

    /// Every expression of the algebra, by wire spelling, in example order.
    let knownExpressions: string list =
        expressionExamples |> List.map expressionName |> distinctNames

    /// Every binary operator of the algebra, by wire spelling.
    let knownOperators: string list =
        nullaryCases<BinOp> () |> List.map operatorName |> distinctNames

    /// Every function of the algebra, by wire spelling.
    let knownFunctions: string list =
        nullaryCases<ScalarFn> () |> List.map functionName |> distinctNames

    /// A step's encoded fields, `$type` aside.
    let private fieldsOf (step: Transform) : (string * JVal) list =
        match DataFrameCodec.encodeTransform step with
        | JObj fields -> fields |> List.filter (fun (k, _) -> k <> "$type")
        | _ -> []

    /// Every field each verb has, read off its examples.
    let knownFields: Map<string, Set<string>> =
        stepExamples
        |> List.groupBy stepName
        |> List.map (fun (name, steps) -> name, steps |> List.collect (fieldsOf >> List.map fst) |> Set.ofList)
        |> Map.ofList

    /// The verbs that read another source with no check to ask, and the
    /// expressions that need a binding no evaluation gives.
    let private otherSourceSteps = set [ "union"; "intersect"; "except"; "predict" ]

    let private unbindable = set [ "param"; "now" ]

    [<Literal>]
    let private JoinStep = "join"

    // ── The builder ───────────────────────────────────────────────

    /// An empty profile of this name.
    let named (name: string) : PipelineProfile = {
        Name = name
        Steps = Set.empty
        Expressions = Set.empty
        Operators = Set.empty
        Functions = Set.empty
        Fields = Map.empty
        RelationCheck = None
    }

    /// The default profile: `filter`, `project`, `derive`, `groupBy`, `sort`,
    /// `limit` and `window`; `col`, `lit`, `binary`, `coalesce`, `case`,
    /// `cast`, `isNull`, `in` and `apply`; every binary operator; and the
    /// arithmetic and string functions (not `exp`, `ln` or `log10`, which are
    /// host-rounded, nor the date functions). No `join`, no set operation, no
    /// `param`, no `now`: the slice is the only source and the evaluation
    /// clock is the server's.
    /// Opt-in: a deployment that registers no profile is unchanged.
    let defaultProfile: PipelineProfile = {
        named "default" with
            Steps = set [ "filter"; "project"; "derive"; "groupBy"; "sort"; "limit"; "window" ]
            Expressions = set [ "col"; "lit"; "binary"; "coalesce"; "case"; "cast"; "isNull"; "in"; "apply" ]
            Operators = Set.ofList knownOperators
            Functions =
                set [
                    "abs"
                    "round"
                    "floor"
                    "ceil"
                    "sqrt"
                    "pow"
                    "least"
                    "greatest"
                    "length"
                    "lower"
                    "upper"
                    "substr"
                    "concat"
                    "trim"
                    "replace"
                    "indexOf"
                ]
    }

    /// Admit these verbs.
    let withSteps (steps: string list) (profile: PipelineProfile) : PipelineProfile = {
        profile with
            Steps = Set.union profile.Steps (Set.ofList steps)
    }

    /// Withdraw these verbs.
    let withoutSteps (steps: string list) (profile: PipelineProfile) : PipelineProfile = {
        profile with
            Steps = Set.difference profile.Steps (Set.ofList steps)
    }

    /// Admit these expressions.
    let withExpressions (expressions: string list) (profile: PipelineProfile) : PipelineProfile = {
        profile with
            Expressions = Set.union profile.Expressions (Set.ofList expressions)
    }

    /// Withdraw these expressions.
    let withoutExpressions (expressions: string list) (profile: PipelineProfile) : PipelineProfile = {
        profile with
            Expressions = Set.difference profile.Expressions (Set.ofList expressions)
    }

    /// Admit these binary operators.
    let withOperators (operators: string list) (profile: PipelineProfile) : PipelineProfile = {
        profile with
            Operators = Set.union profile.Operators (Set.ofList operators)
    }

    /// Withdraw these binary operators.
    let withoutOperators (operators: string list) (profile: PipelineProfile) : PipelineProfile = {
        profile with
            Operators = Set.difference profile.Operators (Set.ofList operators)
    }

    /// Admit these functions.
    let withFunctions (functions: string list) (profile: PipelineProfile) : PipelineProfile = {
        profile with
            Functions = Set.union profile.Functions (Set.ofList functions)
    }

    /// Withdraw these functions.
    let withoutFunctions (functions: string list) (profile: PipelineProfile) : PipelineProfile = {
        profile with
            Functions = Set.difference profile.Functions (Set.ofList functions)
    }

    /// Narrow the fields a step of `step` may write to `fields`.
    let withFields (step: string) (fields: string list) (profile: PipelineProfile) : PipelineProfile = {
        profile with
            Fields = profile.Fields.Add(step, Set.ofList fields)
    }

    /// The relation check every join step must pass. Admitting `join` needs
    /// one.
    let withRelationCheck (check: PipelineRelationCheck) (profile: PipelineProfile) : PipelineProfile = {
        profile with
            RelationCheck = Some check
    }

    // ── The fragment ──────────────────────────────────────────────

    let private code (s: string) = "`" + s + "`"

    let private codes (names: string seq) : string =
        names |> Seq.map code |> String.concat ", "

    let private wire (v: JVal) : string = Canonical.write Rendering.CrossHost v

    /// The prompt fragment for `profile`: one line per admitted verb and
    /// expression with its wire spelling and an example, the operators, the
    /// functions, any narrowed fields, and the join rule when it admits a
    /// join. Pure; `register` renders it once and keeps it.
    let fragment (profile: PipelineProfile) : string =
        let sb = StringBuilder()
        let line (s: string) = sb.Append(s).Append('\n') |> ignore

        line (sprintf "## Computing over facts (pipeline profile '%s')" profile.Name)
        line ""

        line (
            "`evaluate_pipeline` takes a pipeline written from the verbs, expressions, operators, functions and fields below, "
            + "and from nothing else: this list replaces the verb list in the tool's description. "
            + "Anything not listed is refused by name, with what this profile admits, before any fact is read."
        )

        line ""
        line "Verbs (a step's `$type`, with an example step):"

        for name in knownSteps |> List.filter profile.Steps.Contains do
            let example = stepExamples |> List.find (fun s -> stepName s = name)
            line (sprintf "- %s: %s" (code name) (wire (DataFrameCodec.encodeTransform example)))

        line ""
        line "Expressions (an expression's `$type`, with an example):"

        for name in knownExpressions |> List.filter profile.Expressions.Contains do
            let example = expressionExamples |> List.find (fun e -> expressionName e = name)
            line (sprintf "- %s: %s" (code name) (wire (DataFrameCodec.encodeExpr example)))

        if profile.Expressions.Contains "binary" then
            line ""

            line (
                sprintf
                    "Operators (a `binary` expression's `op`): %s."
                    (codes (knownOperators |> List.filter profile.Operators.Contains))
            )

        if profile.Expressions.Contains "apply" then
            line ""

            line (
                sprintf
                    "Functions (an `apply` expression's `fn`): %s."
                    (codes (knownFunctions |> List.filter profile.Functions.Contains))
            )

        let narrowed =
            profile.Fields
            |> Map.filter (fun step _ -> profile.Steps.Contains step)
            |> Map.toList

        if not narrowed.IsEmpty then
            line ""
            line "Fields (a step of these verbs writes only these fields):"

            for (step, fields) in narrowed do
                line (sprintf "- %s: %s" (code step) (codes fields))

        if profile.Steps.Contains JoinStep then
            line ""

            line (
                "Joins: a `join` step names another fact table by its id, "
                + wire (ColumnCodec.encodeJson (Ref "<fact table id>"))
                + ", never a table written into the pipeline. The joined table is read over the same period range and subject prefix as the request, "
                + "under the same disclosure rules, and a join is admitted only along a relation this deployment declares: "
                + "one it does not declare is refused by name. A column computed from a withheld cell on either side is withheld."
            )

        sb.ToString().TrimEnd('\n')

    // ── Registration ──────────────────────────────────────────────

    /// Check `profile` against the algebra and render its fragment once.
    /// Every refusal is reported, each by name.
    let register (profile: PipelineProfile) : Result<RegisteredPipelineProfile, PipelineProfileRefusal list> =
        let steps = Set.ofList knownSteps
        let expressions = Set.ofList knownExpressions
        let operators = Set.ofList knownOperators
        let functions = Set.ofList knownFunctions

        let refusals = [
            if String.IsNullOrWhiteSpace profile.Name then
                PipelineProfileRefusal.NoName
            if profile.Steps.IsEmpty then
                PipelineProfileRefusal.NoSteps
            for step in profile.Steps do
                if not (steps.Contains step) then
                    PipelineProfileRefusal.UnknownStep step
                elif otherSourceSteps.Contains step then
                    PipelineProfileRefusal.ReadsAnotherSource step
            if profile.Steps.Contains JoinStep && profile.RelationCheck.IsNone then
                PipelineProfileRefusal.JoinWithoutRelationCheck
            for expression in profile.Expressions do
                if not (expressions.Contains expression) then
                    PipelineProfileRefusal.UnknownExpression expression
                elif unbindable.Contains expression then
                    PipelineProfileRefusal.Unbindable expression
            for operator in profile.Operators do
                if not (operators.Contains operator) then
                    PipelineProfileRefusal.UnknownOperator operator
            for fn in profile.Functions do
                if not (functions.Contains fn) then
                    PipelineProfileRefusal.UnknownFunction fn
            for KeyValue(step, fields) in profile.Fields do
                match knownFields.TryFind step with
                | None -> PipelineProfileRefusal.UnknownStep step
                | Some _ when not (profile.Steps.Contains step) -> PipelineProfileRefusal.FieldsOfUnadmittedStep step
                | Some known ->
                    for field in fields do
                        if not (known.Contains field) then
                            PipelineProfileRefusal.UnknownField(step, field)
        ]

        if List.isEmpty refusals then
            Ok(RegisteredPipelineProfile(profile, fragment profile))
        else
            Error refusals

    // ── The grammar ───────────────────────────────────────────────

    /// The expressions a step writes, at the top.
    let private stepExpressions (step: Transform) : ColExpr list =
        match step with
        | Filter e
        | Derive(_, e) -> [ e ]
        | Define fd -> [ fd.Body ]
        | Fit spec -> FitSpec.exprs spec
        | Predict spec -> FitSpec.exprs spec.Fit
        | Project _
        | GroupBy _
        | Join _
        | Window _
        | Pivot _
        | Unpivot _
        | Sort _
        | Distinct
        | Limit _
        | Union _
        | Intersect _
        | Except _ -> []

    /// An expression's own operands. Total over the closed `ColExpr` union: a
    /// case the algebra gains is a compile error here, never an expression the
    /// grammar silently does not look inside.
    let private operands (e: ColExpr) : ColExpr list =
        match e with
        | Col _
        | Lit _
        | Param _
        | Now _
        | Arg _ -> []
        | Binary(_, a, b)
        | Quotient(a, b, _) -> [ a; b ]
        | Not a
        | IsNull a
        | InParam(a, _)
        | Cast(_, a)
        | Rounded(a, _)
        | Convert(a, _)
        | Interpolate(a, _, _, _) -> [ a ]
        | Coalesce xs
        | ApplyFn(_, xs)
        | Call(_, xs) -> xs
        | Case(cases, otherwise) -> (cases |> List.collect (fun (w, t) -> [ w; t ])) @ [ otherwise ]
        | InList(a, items) -> a :: items

    let rec private allExpressions (e: ColExpr) : ColExpr list =
        e :: List.collect allExpressions (operands e)

    let private sorted (names: Set<string>) (order: string list) : string list = order |> List.filter names.Contains

    /// The first thing in `step` the profile does not admit.
    let private outside (profile: PipelineProfile) (i: int) (step: Transform) : PipelineRefusal option =
        let name = stepName step

        let refuse (kind: string) (what: string) (admitted: string list) =
            Some(PipelineRefusal.OutsideProfile(i, kind, what, admitted))

        if not (profile.Steps.Contains name) then
            refuse "verb" name (sorted profile.Steps knownSteps)
        else
            let field =
                match profile.Fields.TryFind name with
                | None -> None
                | Some admitted ->
                    fieldsOf step
                    |> List.tryPick (fun (field, value) ->
                        match value with
                        | JArr [] -> None
                        | _ when admitted.Contains field -> None
                        | _ -> refuse (name + " field") field (Set.toList admitted))

            match field with
            | Some refusal -> Some refusal
            | None ->
                stepExpressions step
                |> List.collect allExpressions
                |> List.tryPick (fun e ->
                    let expression = expressionName e

                    if not (profile.Expressions.Contains expression) then
                        refuse "expression" expression (sorted profile.Expressions knownExpressions)
                    else
                        match e with
                        | Binary(op, _, _) when not (profile.Operators.Contains(operatorName op)) ->
                            refuse "operator" (operatorName op) (sorted profile.Operators knownOperators)
                        | ApplyFn(fn, _) when not (profile.Functions.Contains(functionName fn)) ->
                            refuse "function" (functionName fn) (sorted profile.Functions knownFunctions)
                        | _ -> None)

    /// Read `pipeline`, written for `caller` over fact table `table`, against
    /// the profile: every step's verb, fields, expressions, operators and
    /// functions, and every join through the relation check. Answers the
    /// joins it admitted, in step order, or the first refusal, naming the
    /// step and what the profile admits.
    let admit
        (registered: RegisteredPipelineProfile)
        (caller: PipelineCaller)
        (table: string)
        (pipeline: Transform list)
        : Result<PipelineJoin list, PipelineRefusal> =
        let profile = registered.Profile

        let rec go (acc: PipelineJoin list) (rest: (int * Transform) list) =
            match rest with
            | [] -> Ok(List.rev acc)
            | (i, step) :: rest ->
                match outside profile i step with
                | Some refusal -> Error refusal
                | None ->
                    match step, profile.RelationCheck with
                    | Join(Embedded _, _, _), _ ->
                        Error(
                            PipelineRefusal.JoinRefused(
                                i,
                                "it joins a table written into the pipeline; a join names a fact table by its id, as "
                                + wire (ColumnCodec.encodeJson (Ref "<fact table id>"))
                            )
                        )
                    | Join(Ref joined, on, _), Some check ->
                        let join = {
                            Step = i
                            Table = table
                            Joined = joined
                            On = on
                            Kind = fieldsOf step |> List.tryFind (fst >> (=) "how") |> Option.map snd |> text
                        }

                        match check caller join with
                        | Ok() -> go (join :: acc) rest
                        | Error reason -> Error(PipelineRefusal.JoinRefused(i, reason))
                    // Registration refuses `join` without a check; a profile
                    // built around `register` still never joins.
                    | Join _, None ->
                        Error(PipelineRefusal.JoinRefused(i, "this deployment supplies no relation check"))
                    | _ -> go acc rest

        go [] (List.indexed pipeline |> List.map (fun (i, step) -> i + 1, step))

/// Companion helpers for `PipelineProfileRefusal`.
[<RequireQualifiedAccess>]
module PipelineProfileRefusal =
    /// One sentence naming what to repair.
    let describe (refusal: PipelineProfileRefusal) : string =
        match refusal with
        | PipelineProfileRefusal.NoName -> "the profile has no name"
        | PipelineProfileRefusal.NoSteps -> "the profile admits no verb"
        | PipelineProfileRefusal.UnknownStep step -> sprintf "the pipeline algebra has no verb '%s'" step
        | PipelineProfileRefusal.UnknownExpression expression ->
            sprintf "the pipeline algebra has no expression '%s'" expression
        | PipelineProfileRefusal.UnknownOperator operator ->
            sprintf "the pipeline algebra has no binary operator '%s'" operator
        | PipelineProfileRefusal.UnknownFunction fn -> sprintf "the pipeline algebra has no function '%s'" fn
        | PipelineProfileRefusal.UnknownField(step, field) -> sprintf "verb '%s' has no field '%s'" step field
        | PipelineProfileRefusal.FieldsOfUnadmittedStep step ->
            sprintf "the profile narrows the fields of verb '%s', which it does not admit" step
        | PipelineProfileRefusal.ReadsAnotherSource step ->
            sprintf
                "verb '%s' reads another source, which no profile may admit: the slice is the only source, and only a join through a relation check reads another"
                step
        | PipelineProfileRefusal.Unbindable expression ->
            sprintf
                "expression '%s' needs a binding no evaluation gives (nothing binds a parameter, and the clock is the server's), so no profile may admit it"
                expression
        | PipelineProfileRefusal.JoinWithoutRelationCheck ->
            "the profile admits 'join' and supplies no relation check; a join is admitted only through one"