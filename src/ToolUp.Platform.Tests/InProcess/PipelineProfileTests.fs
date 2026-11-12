// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.PipelineProfileTests

// ─── The assistant's pipeline profile ────────────────────────────────
//
// A deployment registers the `Fuaran.Compute` subset its assistant may write
// in an `evaluate_pipeline` call. What this pack pins:
//
//   1. **Registration reads the algebra.** An unknown verb, expression,
//      operator, function or field refuses the registration by name, and so
//      does a verb that reads another source, an expression nothing can bind,
//      and a join with no relation check.
//   2. **The vocabulary is the codec's.** Every case of `Transform` and
//      `ColExpr` has an example, and every example the fragment shows decodes
//      through `DataFrameCodec` and encodes back to the same bytes.
//   3. **The grammar.** A pipeline outside the profile is refused before any
//      fact is read, naming the step, the case and the admitted set; the text
//      the model reads is pinned. With no profile nothing changes.
//   4. **A join only through the relation check.** A join the check refuses is
//      refused before evaluation; an admitted one reads the joined table
//      through the gate, and a withheld cell on either side withholds the
//      columns that read it, and only those.
//   5. **The measurement.** The default fragment's size, and the refusal count
//      over a scripted set of analysis pipelines with and without the profile.

open System
open Expecto
open Microsoft.FSharp.Reflection
open Fuaran.Core
open Fuaran.Compute
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Platform.Scope.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.FactRenderToolTests.RenderFixtures

// ─── Fixtures ───────────────────────────────────────────────────────

/// Daily margin per site, surfaceable: a table a join can read whole.
let private openMargins: FactTableDefinition = {
    margins with
        Id = "daily-margin-open"
        Columns = [ FactTableDefinition.column "margin" FactTableValueShape.Scalar ]
}

let private joinTables: IFactTableRegistry =
    FactTableRegistry.build [
        for t in [ trade; margins; openMargins ] do
            {
                FactTableRegistration.Module = "trade"
                Definition = t
            }
    ] [ BindAllFactTables DefaultFactTableWriter.Destination ]

/// A world with the revenue table, the restricted margin table and the open
/// one, each written for three days and two sites.
let private joinWorld () : World =
    let storage = InMemoryBlobStorage()
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let ticks = ref 0

    let clock () =
        ticks.Value <- ticks.Value + 1
        day1.AddDays(10.0).AddSeconds(float ticks.Value)

    let store =
        BlobFactStore.createWithRegistryAndClock storage events (Some registry) clock

    let writer =
        DefaultFactTableWriter.createWithClock store storage events joinTables (Some registry) clock

    let gate = OverlayGate(FactDisclosureGate(store, events))
    let scopeId = "team-" + Guid.NewGuid().ToString "N"

    let w = {
        Deps = {
            Tables = joinTables
            Writer = Some writer
            Store = store
            Gate = gate
            Registry = Some registry
            Clock = clock
            Views = None
        }
        Gate = gate
        Scope =
            ScopeResolution.ofStorageScope {
                ScopeId = scopeId
                Container = "container-" + scopeId
                Persist = true
            }
    }

    seed writer w.Scope trade "revenue"
    seed writer w.Scope margins "margin"
    seed writer w.Scope openMargins "margin"
    w

/// The fact id of `metric` of `table` at `site` on the day `offset` days after
/// the first.
let private factIdOf (w: World) (table: string) (metric: string) (site: string) (offset: int) : string =
    let series =
        match
            FactBrowseHandler.querySeries w.Deps w.Scope "u" {
                TableId = table
                Metric = metric
                SubjectPrefix = []
                SubjectLevel = None
                PeriodFrom = day1
                PeriodTo = day1.AddDays 3.0
                MaxPoints = FactBrowseApi.MaxSeriesPoints
                View = None
            }
            |> Async.RunSynchronously
        with
        | Ok s -> s
        | Error e -> failtestf "series: %A" e

    series.Points
    |> List.find (fun p -> List.last p.SubjectPath = site && p.PeriodFrom = day1.AddDays(float offset))
    |> _.FactId

let private registered (profile: PipelineProfile) : RegisteredPipelineProfile =
    match PipelineProfile.register profile with
    | Ok r -> r
    | Error refusals ->
        failtestf
            "the profile did not register: %s"
            (refusals |> List.map PipelineProfileRefusal.describe |> String.concat "; ")

let private refusalsOf (profile: PipelineProfile) : PipelineProfileRefusal list =
    match PipelineProfile.register profile with
    | Ok _ -> failtest "the profile registered, and should have been refused"
    | Error refusals -> refusals

let private answerUnder (profile: RegisteredPipelineProfile option) (w: World) (args: string) =
    PipelineTool.answerUnder profile w.Deps w.Scope "u" args
    |> Async.RunSynchronously

let private revenueArgs (pipeline: Transform list) =
    pipelineArgs trade.Id "revenue" 3 pipeline

let private refusalOf (answer: Result<PipelineToolResult, PipelineRefusal>) : PipelineRefusal =
    match answer with
    | Error refusal -> refusal
    | Ok result -> failtestf "expected a refusal, got a result of %d row(s)" result.Rows

let private resultOf (answer: Result<PipelineToolResult, PipelineRefusal>) : PipelineToolResult =
    match answer with
    | Ok result -> result
    | Error refusal -> failtestf "expected a result, got %s" (PipelineRefusal.describe refusal)

let private defaultVerbs = [ "filter"; "project"; "derive"; "groupBy"; "sort"; "limit"; "window" ]

let private perSubject: Transform list = [
    GroupBy(
        [ FactBrowseSlice.SubjectColumn ],
        [
            {
                Name = "total"
                Fn = Sum
                Of = "revenue"
            }
        ]
    )
    Transform.sortBy [ FactBrowseSlice.SubjectColumn, Asc ]
]

// ─── 1. Registration ────────────────────────────────────────────────

let registrationTests =
    testList "Pipeline profile registration" [

        test "the default profile registers with the cases the profile names" {
            let profile = (registered PipelineProfile.defaultProfile).Profile
            Expect.equal profile.Steps (set defaultVerbs) "seven verbs"

            Expect.equal
                profile.Expressions
                (set [ "col"; "lit"; "binary"; "coalesce"; "case"; "cast"; "isNull"; "in"; "apply" ])
                "nine expressions"

            Expect.equal profile.Operators (set PipelineProfile.knownOperators) "every binary operator"

            for fn in [ "exp"; "ln"; "log10"; "datePart"; "dateDiffDays" ] do
                Expect.isFalse
                    (profile.Functions.Contains fn)
                    (sprintf "'%s' is not an arithmetic or string function" fn)

            Expect.isTrue
                (profile.Functions.Contains "round" && profile.Functions.Contains "concat")
                "arithmetic and string"
        }

        test "an unknown verb, expression, operator, function or field is refused by name" {
            let refusals =
                PipelineProfile.defaultProfile
                |> PipelineProfile.withSteps [ "rollup" ]
                |> PipelineProfile.withExpressions [ "regex" ]
                |> PipelineProfile.withOperators [ "xor" ]
                |> PipelineProfile.withFunctions [ "sin" ]
                |> PipelineProfile.withFields "window" [ "of"; "frame" ]
                |> refusalsOf

            Expect.containsAll
                refusals
                [
                    PipelineProfileRefusal.UnknownStep "rollup"
                    PipelineProfileRefusal.UnknownExpression "regex"
                    PipelineProfileRefusal.UnknownOperator "xor"
                    PipelineProfileRefusal.UnknownFunction "sin"
                    PipelineProfileRefusal.UnknownField("window", "frame")
                ]
                "every unknown name, each by name"

            Expect.equal refusals.Length 5 "and nothing else"

            Expect.equal
                (PipelineProfileRefusal.describe (PipelineProfileRefusal.UnknownField("window", "frame")))
                "verb 'window' has no field 'frame'"
                "the sentence names both"
        }

        test "a verb reading another source, an unbindable expression and an unadmitted verb's fields are refused" {
            let refusals =
                PipelineProfile.defaultProfile
                |> PipelineProfile.withSteps [ "union"; "predict" ]
                |> PipelineProfile.withExpressions [ "param"; "now" ]
                |> PipelineProfile.withFields "pivot" [ "on" ]
                |> refusalsOf

            Expect.containsAll
                refusals
                [
                    PipelineProfileRefusal.ReadsAnotherSource "union"
                    PipelineProfileRefusal.ReadsAnotherSource "predict"
                    PipelineProfileRefusal.Unbindable "param"
                    PipelineProfileRefusal.Unbindable "now"
                    PipelineProfileRefusal.FieldsOfUnadmittedStep "pivot"
                ]
                "each by name"
        }

        test "a profile admitting join with no relation check is refused by name" {
            let refusals =
                PipelineProfile.defaultProfile
                |> PipelineProfile.withSteps [ "join" ]
                |> refusalsOf

            Expect.equal refusals [ PipelineProfileRefusal.JoinWithoutRelationCheck ] "the one refusal"

            Expect.stringContains
                (PipelineProfileRefusal.describe PipelineProfileRefusal.JoinWithoutRelationCheck)
                "relation check"
                "names the remedy"

            let withCheck =
                PipelineProfile.defaultProfile
                |> PipelineProfile.withSteps [ "join" ]
                |> PipelineProfile.withRelationCheck (fun _ _ -> Ok())

            Expect.isOk (PipelineProfile.register withCheck) "with a check it registers"
        }

        test "a profile with no name or no verb is refused" {
            Expect.containsAll
                (refusalsOf (PipelineProfile.named " "))
                [ PipelineProfileRefusal.NoName; PipelineProfileRefusal.NoSteps ]
                "both"
        }
    ]

// ─── 2. The vocabulary and the fragment ─────────────────────────────

let private caseNames<'T> () =
    FSharpType.GetUnionCases typeof<'T> |> Array.map _.Name |> Set.ofArray

let private exampleCases<'T> (examples: 'T list) =
    examples
    |> List.map (fun x -> (fst (FSharpValue.GetUnionFields(x, typeof<'T>))).Name)
    |> Set.ofList

/// The widest profile registration admits.
let private widest: PipelineProfile =
    PipelineProfile.named "widest"
    |> PipelineProfile.withSteps (
        PipelineProfile.knownSteps
        |> List.filter (fun s -> not (List.contains s [ "union"; "intersect"; "except"; "predict" ]))
    )
    |> PipelineProfile.withExpressions (
        PipelineProfile.knownExpressions
        |> List.filter (fun e -> e <> "param" && e <> "now")
    )
    |> PipelineProfile.withOperators PipelineProfile.knownOperators
    |> PipelineProfile.withFunctions PipelineProfile.knownFunctions
    |> PipelineProfile.withRelationCheck (fun _ _ -> Ok())

/// The example JSON of each `- `name`: json` line under `heading`.
let private examplesUnder (heading: string) (fragment: string) : (string * string) list =
    fragment.Split '\n'
    |> Array.skipWhile (fun l -> not (l.StartsWith heading))
    |> Array.skip 1
    |> Array.takeWhile (fun l -> l.StartsWith "- `")
    |> Array.map (fun l ->
        let close = l.IndexOf("`", 3)
        l.Substring(3, close - 3), l.Substring(close + 3))
    |> List.ofArray

let private wire (v: JVal) = Canonical.write Rendering.CrossHost v

let fragmentTests =
    testList "Pipeline profile fragment" [

        test "every Transform and ColExpr case has an example, and every operator and function a name" {
            Expect.equal (exampleCases PipelineProfile.stepExamples) (caseNames<Transform> ()) "every verb"

            Expect.equal (exampleCases PipelineProfile.expressionExamples) (caseNames<ColExpr> ()) "every expression"

            Expect.equal PipelineProfile.knownOperators.Length (caseNames<BinOp> ()).Count "every operator"
            Expect.equal PipelineProfile.knownFunctions.Length (caseNames<ScalarFn> ()).Count "every function"
        }

        test "every example the fragment shows round-trips through the codec" {
            let fragment = (registered widest).Fragment
            let verbs = examplesUnder "Verbs" fragment
            let expressions = examplesUnder "Expressions" fragment

            Expect.equal
                (verbs |> List.map fst)
                (widest.Steps |> fun s -> PipelineProfile.knownSteps |> List.filter s.Contains)
                "a line per verb"

            Expect.isGreaterThanOrEqual expressions.Length 15 "a line per expression"

            for (name, json) in verbs do
                match
                    Canonical.read json
                    |> Result.mapError string
                    |> Result.bind (DataFrameCodec.decodeTransform >> Result.mapError string)
                with
                | Ok step ->
                    Expect.equal
                        (wire (DataFrameCodec.encodeTransform step))
                        json
                        (sprintf "'%s' re-encodes to the same bytes" name)

                    Expect.equal (PipelineProfile.stepName step) name (sprintf "'%s' decodes to its own verb" name)
                | Error e -> failtestf "the '%s' example does not decode: %s" name e

            for (name, json) in expressions do
                match
                    Canonical.read json
                    |> Result.mapError string
                    |> Result.bind (DataFrameCodec.decodeExpr >> Result.mapError string)
                with
                | Ok e ->
                    Expect.equal
                        (wire (DataFrameCodec.encodeExpr e))
                        json
                        (sprintf "'%s' re-encodes to the same bytes" name)

                    Expect.equal
                        (PipelineProfile.expressionName e)
                        name
                        (sprintf "'%s' decodes to its own expression" name)
                | Error e -> failtestf "the '%s' example does not decode: %s" name e
        }

        test "the default fragment names its cases and nothing outside them" {
            let fragment = (registered PipelineProfile.defaultProfile).Fragment

            Expect.stringStarts fragment "## Computing over facts (pipeline profile 'default')" "headed by the profile"
            Expect.equal (examplesUnder "Verbs" fragment |> List.map fst) defaultVerbs "the seven verbs, in order"
            Expect.stringContains fragment "Operators (a `binary` expression's `op`): `add`" "the operators"
            Expect.stringContains fragment "Functions (an `apply` expression's `fn`): `abs`" "the functions"
            Expect.isFalse (fragment.Contains "`pivot`") "a verb outside the profile is not shown"
            Expect.isFalse (fragment.Contains "Joins:") "no join rule without a join"
        }

        test "a profile admitting a join states the join rule and the Ref spelling" {
            let fragment =
                (registered (
                    PipelineProfile.defaultProfile
                    |> PipelineProfile.withSteps [ "join" ]
                    |> PipelineProfile.withRelationCheck (fun _ _ -> Ok())
                ))
                    .Fragment

            Expect.stringContains fragment "Joins: a `join` step names another fact table by its id" "the rule"
            Expect.stringContains fragment "{\"ref\":\"<fact table id>\"" "the Ref spelling"
        }

        test "measurement: the default fragment's size" {
            let profile = registered PipelineProfile.defaultProfile
            // Recorded in the phase outcome; the bounds hold it near that
            // figure so a growth of the fragment is a decision, not a drift.
            Expect.isLessThan profile.FragmentTokens 1000 "under a thousand estimated tokens"
            Expect.isGreaterThan profile.FragmentTokens 300 "the examples are there"

            Expect.equal
                profile.FragmentTokens
                (int (ceil (float profile.Fragment.Length / 4.0)))
                "four characters a token"
        }
    ]

// ─── 3. The grammar ─────────────────────────────────────────────────

let private pivot =
    Pivot {
        Index = [ FactBrowseSlice.SubjectColumn ]
        On = FactBrowseSlice.LabelColumn
        Values = "revenue"
        Agg = Sum
    }

let grammarTests =
    testList "Pipeline profile grammar" [

        test "a verb outside the profile is refused before any fact is read, naming the admitted verbs" {
            let w = joinWorld ()
            let profile = registered PipelineProfile.defaultProfile

            let refusal =
                refusalOf (answerUnder (Some profile) w (revenueArgs (perSubject @ [ pivot ])))

            Expect.equal
                refusal
                (PipelineRefusal.OutsideProfile(3, "verb", "pivot", defaultVerbs))
                "the step, the case and the set"

            Expect.equal
                (PipelineTool.refusalText refusal)
                "evaluate_pipeline refused the request: step 3 uses the verb 'pivot', which this deployment's pipeline profile does not admit; the verbs it admits are 'filter', 'project', 'derive', 'groupBy', 'sort', 'limit', 'window', so rewrite the step with those."
                "the text the model reads"

            Expect.isEmpty w.Gate.Surfaces "refused before the slice was read"
        }

        test "an expression, operator, function or field outside the profile is refused by name" {
            let w = joinWorld ()

            let narrowed =
                registered (
                    PipelineProfile.defaultProfile
                    |> PipelineProfile.withoutOperators [ "mod" ]
                    |> PipelineProfile.withFields "window" [ "fn"; "of"; "as"; "orderBy" ]
                )

            let refused (pipeline: Transform list) =
                refusalOf (answerUnder (Some narrowed) w (revenueArgs pipeline))

            match refused [ Filter(Not(IsNull(Col "revenue"))) ] with
            | PipelineRefusal.OutsideProfile(1, "expression", "not", admitted) ->
                Expect.contains admitted "isNull" "the admitted expressions"
            | other -> failtestf "expected the expression refusal, got %A" other

            match refused [ Derive("odd", Binary(Mod, Col "row", Lit(Int 2))) ] with
            | PipelineRefusal.OutsideProfile(1, "operator", "mod", admitted) ->
                Expect.isFalse (List.contains "mod" admitted) "mod is withdrawn"
            | other -> failtestf "expected the operator refusal, got %A" other

            match refused [ Derive("growth", ApplyFn(Ln, [ Col "revenue" ])) ] with
            | PipelineRefusal.OutsideProfile(1, "function", "ln", _) -> ()
            | other -> failtestf "expected the function refusal, got %A" other

            let partitioned =
                Window {
                    PartitionBy = [ FactBrowseSlice.SubjectColumn ]
                    OrderBy = [ FactBrowseSlice.FromColumn, Asc ]
                    Fn = CumulSum
                    Of = "revenue"
                    As = "running"
                }

            Expect.equal
                (refused [ partitioned ])
                (PipelineRefusal.OutsideProfile(1, "window field", "partitionBy", [ "as"; "fn"; "of"; "orderBy" ]))
                "a field the profile narrows away"

            let unpartitioned =
                Window {
                    PartitionBy = []
                    OrderBy = [ FactBrowseSlice.FromColumn, Asc ]
                    Fn = CumulSum
                    Of = "revenue"
                    As = "running"
                }

            Expect.isOk (answerUnder (Some narrowed) w (revenueArgs [ unpartitioned ])) "an empty field is not written"
        }

        test "a pipeline inside the profile answers exactly as with no profile; with no profile nothing changes" {
            let w = joinWorld ()
            let profile = registered PipelineProfile.defaultProfile
            let under = resultOf (answerUnder (Some profile) w (revenueArgs perSubject))
            let without = resultOf (answerUnder None w (revenueArgs perSubject))
            Expect.equal under.Table without.Table "the same table"
            Expect.equal under.PipelineHash without.PipelineHash "the same pipeline"

            let pivoted = revenueArgs [ pivot ]

            Expect.equal
                (answerUnder None w pivoted |> refusalOf)
                (PipelineTool.answerWith w.Deps w.Scope "u" pivoted
                 |> Async.RunSynchronously
                 |> refusalOf)
                "no profile is answerWith, refusal for refusal"

            match answerUnder None w pivoted |> refusalOf with
            | PipelineRefusal.OpenSchema(1, _) -> ()
            | other -> failtestf "with no profile a pivot is refused by the walk as before, got %A" other
        }
    ]

// ─── 4. Joins ───────────────────────────────────────────────────────

let private joinOn (table: string) (how: JoinKind) =
    Join(
        Ref table,
        [
            FactBrowseSlice.SubjectColumn, FactBrowseSlice.SubjectColumn
            FactBrowseSlice.FromColumn, FactBrowseSlice.FromColumn
        ],
        how
    )

/// The joined view: subject, day, revenue, margin, and revenue less margin.
let private joined (table: string) : Transform list = [
    joinOn table Left
    Project [
        FactBrowseSlice.SubjectColumn, FactBrowseSlice.SubjectColumn
        FactBrowseSlice.FromColumn, FactBrowseSlice.FromColumn
        "revenue", "revenue"
        "margin", "margin"
    ]
    Derive("net", Binary(Sub, Col "revenue", Col "margin"))
]

/// A profile admitting a join along the declared relations, recording each
/// join it was asked about.
let private relationProfile () =
    let asked = ResizeArray<PipelineJoin>()

    let check: PipelineRelationCheck =
        fun _ join ->
            asked.Add join

            if join.Joined = openMargins.Id || join.Joined = margins.Id then
                Ok()
            else
                Error(sprintf "no declared relation joins '%s' to '%s'" join.Table join.Joined)

    let profile =
        PipelineProfile.defaultProfile
        |> PipelineProfile.withSteps [ "join" ]
        |> PipelineProfile.withRelationCheck check
        |> registered

    profile, asked

let private withheldColumns (result: PipelineToolResult) =
    result.Withheld |> List.map _.Column |> Set.ofList

let joinTests =
    testList "Pipeline profile joins" [

        test "a join the relation check refuses is refused by name before evaluation" {
            let w = joinWorld ()
            let profile, asked = relationProfile ()

            let refusal =
                refusalOf (answerUnder (Some profile) w (revenueArgs (joined "daily-cost")))

            Expect.equal
                refusal
                (PipelineRefusal.JoinRefused(1, "no declared relation joins 'daily-trade' to 'daily-cost'"))
                "the check's own words"

            Expect.equal
                (PipelineRefusal.describe refusal)
                "step 1 is a join this deployment refuses: no declared relation joins 'daily-trade' to 'daily-cost'"
                "the text the model reads"

            Expect.equal
                (List.ofSeq asked)
                [
                    {
                        Step = 1
                        Table = trade.Id
                        Joined = "daily-cost"
                        On = [ "subject", "subject"; "period_from", "period_from" ]
                        Kind = "left"
                    }
                ]
                "the check read the step"

            Expect.isEmpty w.Gate.Surfaces "nothing was read"
        }

        test "a join with no profile is refused as another source, as before" {
            let w = joinWorld ()

            Expect.equal
                (refusalOf (answerUnder None w (revenueArgs (joined openMargins.Id))))
                (PipelineRefusal.NamedSource(1, "join"))
                "the slice is the only source"
        }

        test "a join of a table written into the pipeline is refused" {
            let w = joinWorld ()
            let profile, _ = relationProfile ()

            let embeddedJoin = Join(Embedded Table.empty, [ "subject", "subject" ], Inner)

            match refusalOf (answerUnder (Some profile) w (revenueArgs [ embeddedJoin ])) with
            | PipelineRefusal.JoinRefused(1, reason) -> Expect.stringContains reason "by its id" "names the remedy"
            | other -> failtestf "expected the join refusal, got %A" other
        }

        test "an admitted join reads the joined table through the gate, and nothing withheld withholds nothing" {
            let w = joinWorld ()
            let profile, _ = relationProfile ()

            let result =
                resultOf (answerUnder (Some profile) w (revenueArgs (joined openMargins.Id)))

            Expect.equal result.Columns [ "subject"; "period_from"; "revenue"; "margin"; "net" ] "the joined view"
            Expect.equal result.Rows 6 "one row per subject and day"
            Expect.isEmpty result.Withheld "nothing withheld"

            Expect.isTrue
                (w.Gate.Surfaces |> List.forall ((=) FactToolResult))
                "both slices judged at the tool-result door"

            Expect.isGreaterThan result.FactIdCount 6 "the fact ids of both tables"
        }

        test "a withheld cell on the left withholds the left column and what reads it, and not the joined column" {
            let w = joinWorld ()
            let profile, _ = relationProfile ()
            w.Gate.Deny [ factIdOf w trade.Id "revenue" "s1" 0 ]

            let result =
                resultOf (answerUnder (Some profile) w (revenueArgs (joined openMargins.Id)))

            Expect.equal (withheldColumns result) (set [ "revenue"; "net" ]) "revenue and what reads it"
        }

        test "a withheld cell on the right withholds the joined column and what reads it, and not the left" {
            let w = joinWorld ()
            let profile, _ = relationProfile ()
            w.Gate.Deny [ factIdOf w openMargins.Id "margin" "s2" 1 ]

            let result =
                resultOf (answerUnder (Some profile) w (revenueArgs (joined openMargins.Id)))

            Expect.equal (withheldColumns result) (set [ "margin"; "net" ]) "margin and what reads it"

            let margin = result.Withheld |> List.find (fun c -> c.Column = "margin")
            Expect.equal margin.Metrics [ "daily-margin-open:margin" ] "named by its table"
        }

        test "a joined table withheld whole is read, and withholds every column that reads it" {
            let w = joinWorld ()
            let profile, _ = relationProfile ()

            let result =
                resultOf (answerUnder (Some profile) w (revenueArgs (joined margins.Id)))

            Expect.equal (withheldColumns result) (set [ "margin"; "net" ]) "the restricted table's columns"

            let margin = result.Withheld |> List.find (fun c -> c.Column = "margin")
            Expect.equal margin.Metrics [ "daily-margin:margin" ] "named by its table"
            Expect.equal (margin.Withheld |> List.sumBy _.Count) 6 "every cell, counted"
        }

        test "an assertion over a joined pipeline is refused" {
            let w = joinWorld ()
            let profile, _ = relationProfile ()

            let args =
                (revenueArgs (joined openMargins.Id)).TrimEnd('}')
                + ""","assert":{"column":"net","as_metric":"margin","level":"site"}}"""

            match
                PipelineAssertion.answerUnder (Some profile) w.Deps w.Scope "u" args
                |> Async.RunSynchronously
            with
            | Error(PipelineRefusal.JoinRefused(1, reason)) -> Expect.stringContains reason "not asserted" "says why"
            | other -> failtestf "expected the join refusal, got %A" other
        }
    ]

// ─── 5. Measurement ─────────────────────────────────────────────────

/// Scripted analysis turns: the pipelines an analyst's questions over daily
/// revenue by site come to, as a model writes them with the full algebra in
/// view. A scripted turn does not read the prompt, so the profile cannot
/// improve what it writes: this measures what the default profile REFUSES of
/// an unchanged script, which is the cost side of the trade.
let private scripted: (string * Transform list) list = [
    "total per site", perSubject
    "best site", perSubject @ [ Transform.sortBy [ "total", Desc ]; Transform.limit 1 0 ]
    "days over a threshold",
    [
        Filter(Binary(Gt, Col "revenue", Lit(Decimal "1100")))
        GroupBy(
            [ FactBrowseSlice.SubjectColumn ],
            [
                {
                    Name = "days"
                    Fn = Count
                    Of = FactBrowseSlice.RowColumn
                }
            ]
        )
    ]
    "running total",
    [
        Window {
            PartitionBy = [ FactBrowseSlice.SubjectColumn ]
            OrderBy = [ FactBrowseSlice.FromColumn, Asc ]
            Fn = CumulSum
            Of = "revenue"
            As = "running"
        }
    ]
    "day-on-day change",
    [
        Window {
            PartitionBy = [ FactBrowseSlice.SubjectColumn ]
            OrderBy = [ FactBrowseSlice.FromColumn, Asc ]
            Fn = Lag
            Of = "revenue"
            As = "previous"
        }
        Derive("change", Binary(Sub, Col "revenue", Col "previous"))
    ]
    "banded days",
    [
        Derive("band", Case([ Binary(Gt, Col "revenue", Lit(Decimal "1100")), Lit(Str "high") ], Lit(Str "low")))
        GroupBy(
            [ "band" ],
            [
                {
                    Name = "days"
                    Fn = Count
                    Of = FactBrowseSlice.RowColumn
                }
            ]
        )
    ]
    "rounded revenue", [ Derive("whole", ApplyFn(Round, [ Col "revenue" ])) ]
    "site names", [ Derive("site", ApplyFn(Upper, [ Col FactBrowseSlice.SubjectColumn ])) ]
    "revenue by day as columns", [ pivot ]
    "the sites",
    [
        Project [ FactBrowseSlice.SubjectColumn, FactBrowseSlice.SubjectColumn ]
        Distinct
    ]
    "log revenue", [ Derive("log", ApplyFn(Ln, [ Cast(FloatType, Col "revenue") ])) ]
    "days with revenue", [ Filter(Not(IsNull(Col "revenue"))) ]
    "over a named threshold", [ Filter(Binary(Gt, Col "revenue", Param "threshold")) ]
    "long form", [ Unpivot([ FactBrowseSlice.SubjectColumn ], [ "revenue" ]) ]
]

let measurementTests =
    testList "Pipeline profile measurement" [

        test "measurement: refusals over the scripted analysis turns, with and without the default profile" {
            let w = joinWorld ()
            let profile = registered PipelineProfile.defaultProfile

            let refusedUnder (p: RegisteredPipelineProfile option) =
                scripted
                |> List.filter (fun (_, pipeline) -> Result.isError (answerUnder p w (revenueArgs pipeline)))
                |> List.map fst

            Expect.equal
                (refusedUnder None)
                [ "revenue by day as columns"; "over a named threshold" ]
                "with no profile: 2 of 14 (an open schema, an unbound parameter)"

            Expect.equal
                (refusedUnder (Some profile))
                [
                    "revenue by day as columns"
                    "the sites"
                    "log revenue"
                    "days with revenue"
                    "over a named threshold"
                    "long form"
                ]
                "with the default profile: 6 of 14 (pivot, distinct, ln, not, param, unpivot)"
        }
    ]

let tests =
    testList "Pipeline profile" [ registrationTests; fragmentTests; grammarTests; joinTests; measurementTests ]