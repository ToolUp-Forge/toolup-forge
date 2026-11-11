# A Compute grammar profile for the assistant

**Ships in:** ToolUp.Facts.Core, ToolUp.Facts.Server, ToolUp.Facts.Presentation.Server and ToolUp.AI.Server 0.42.0.
Opt-in: a deployment that registers no pipeline profile evaluates pipelines exactly as before. Two changes are
breaking for a consumer that reached them:

- `PipelineRefusal` (ToolUp.Facts.Core) gains `OutsideProfile` and `JoinRefused`. An exhaustive `match` adds them, or
  calls `PipelineRefusal.describe`, which covers them.
- `SystemPromptBuilder.languageProfile` (ToolUp.AI.Server) is replaced by `SystemPromptBuilder.promptFragments`, which
  takes a list of `PromptFragment`s, so a language profile's fragment and a pipeline profile's are served by one seam.

**Affected:** a deployment composing `evaluate_pipeline` (`PresentationCompose.withPipelineTool`) whose assistant
should write from a deliberate subset of the `Fuaran.Compute` algebra, with that subset in its prompt; and every
deployment serving a language profile's fragment.

## What changes

1. **`PipelineProfile`** (ToolUp.Facts.Server): the verbs (`Transform` cases), expressions (`ColExpr` cases), binary
   operators and functions the assistant may write, by wire spelling, and per verb the fields a step of it may write.
   Start from `PipelineProfile.defaultProfile` or `PipelineProfile.named`, and widen or narrow it with `withSteps` /
   `withoutSteps`, `withExpressions` / `withoutExpressions`, `withOperators` / `withoutOperators`, `withFunctions` /
   `withoutFunctions` and `withFields`.
2. **Registration against the codec.** `PipelineProfile.register` checks every name against the spellings
   `DataFrameCodec` encodes, and refuses by name (`PipelineProfileRefusal`) an unknown verb, expression, operator,
   function or field, a verb reading another source (`union`, `intersect`, `except`, `predict`), `param` and `now`, and
   `join` without a relation check. It renders the prompt fragment once (`RegisteredPipelineProfile.Fragment`), every
   example in which round-trips through the codec.
3. **The grammar.** `FactsCompose.withPipelineProfile registered` registers it beside the fact store. The pipeline tool,
   its asserting form and a reader's re-run then refuse a pipeline outside the profile before any fact is read,
   naming the step, the case and the admitted set (`PipelineRefusal.OutsideProfile`).
4. **A join through a relation check.** `PipelineProfile.withRelationCheck check` admits `join` steps the check
   admits for the caller (`PipelineCaller`, `PipelineJoin`), each refused otherwise in the check's words
   (`PipelineRefusal.JoinRefused`). The joined table is named by id and read through the same gated slice read;
   withheld cells on either side withhold the columns that read them. A pipeline with a join is never asserted.
5. **One fragment seam.** `SystemPromptBuilder.promptFragments logger fragments` serves every registered fragment as
   one cached layer and logs each one's size at construction. `PromptFragment.ofLanguageProfile` makes the language
   profile's; `PromptFragment.create registered.Description registered.Fragment` makes the pipeline profile's.

## What to do

A language profile's layer moves to the new seam:

```diff
- SystemPromptBuilder.languageProfile (Some logger) (Some languageProfile)
+ SystemPromptBuilder.promptFragments (Some logger) [ PromptFragment.ofLanguageProfile languageProfile ]
```

To give the assistant a pipeline profile, register it at boot, compose it beside the fact store, and add its fragment
to the same layer. No pilot has yet recorded the verbs its assistant wrote, so the set below is a pilot deployment's
plausible one, read off the default and the questions a daily-trade analyst asks (totals and rankings per site,
day-on-day change, banded days), with `project`, `coalesce` and the string functions left out:

```fsharp skip=fragment
let pipelineProfile =
    PipelineProfile.named "pilot"
    |> PipelineProfile.withSteps [ "filter"; "derive"; "groupBy"; "sort"; "limit"; "window" ]
    |> PipelineProfile.withExpressions [ "col"; "lit"; "binary"; "case"; "isNull"; "apply" ]
    |> PipelineProfile.withOperators [ "add"; "sub"; "mul"; "div"; "eq"; "ne"; "lt"; "le"; "gt"; "ge"; "and"; "or" ]
    |> PipelineProfile.withFunctions [ "abs"; "round" ]
    |> PipelineProfile.register
    |> Result.defaultWith (fun refusals ->
        failwithf "pipeline profile: %s" (refusals |> List.map PipelineProfileRefusal.describe |> String.concat "; "))

let app, ai =
    (app |> FactsCompose.withPipelineProfile pipelineProfile, AIOptions.create factory providerProfile)
    |> PresentationCompose.withPipelineTool

SystemPrompt = Some(SystemPromptBuilder.compose [
    SystemPromptBuilder.fromStatic "You are the analysts' assistant."
    SystemPromptBuilder.activeModuleContext
    SystemPromptBuilder.promptFragments (Some logger) [
        PromptFragment.ofLanguageProfile languageProfile
        PromptFragment.create pipelineProfile.Description pipelineProfile.Fragment
    ]
])
```

To admit joins, supply the check that knows the deployment's relations:

```fsharp skip=fragment
let declared: PipelineRelationCheck =
    fun caller join ->
        if relations.Contains(join.Table, join.Joined) then Ok()
        else Error(sprintf "no declared relation joins '%s' to '%s'" join.Table join.Joined)

PipelineProfile.defaultProfile
|> PipelineProfile.withSteps [ "join" ]
|> PipelineProfile.withRelationCheck declared
|> PipelineProfile.register
```

## Verification

The boot log carries `Pipeline profile 'pilot': 6 verb(s), prompt fragment <n> bytes.` beside the language profile's
line. In the Platform pack, `Pipeline profile` checks registration, the fragment's round trip, the grammar's refusals,
joins and the measurement; `Language profile` checks that both fragments are served and that none leaves the prompt
unchanged.

## Rollback

Drop `FactsCompose.withPipelineProfile` and the fragment: the pipeline tool returns to the full algebra. The
`promptFragments` seam stays; with no fragment it contributes nothing.
