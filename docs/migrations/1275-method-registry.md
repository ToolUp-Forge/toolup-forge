# A method tool registry and the method profile

**Ships in:** ToolUp.Facts.Core, ToolUp.Facts.Server and ToolUp.AI.Server 0.42.0. Additive: no existing call
changes, `evaluate_pipeline` answers exactly as before, and a deployment that registers no method sends the prompt
it sent before.

**Affected:** a deployment that wants to expose analytical methods to the assistant as typed tools, each with its
meaning, when it applies and how its result is read; and a forge component that registers methods of its own
(views over a dataset model, authorised calculations).

## What changes

1. **`MethodCard`** (ToolUp.Facts.Core): a card's eight parts (`MethodIdentity`, `MethodMeaning`,
   `MethodApplicability` with `MethodCondition` / `MethodAlternative` / `MethodChoice`, `MethodSignature` with
   `MethodParameter`, `MethodOutputs`, `MethodInterpretation`, `MethodPresentation`, `MethodExample`), forge's mirror
   of the published card specification until that package ships. **`MethodRefusal`** is what a method tool refuses
   with: an `ISpecRefusal` named by the card's condition.
2. **`MethodTool`** (ToolUp.Facts.Server): `bind card evaluator` binds a card to a `MethodEvaluator` (the definition
   hash it implements, the preconditions it checks before any read, the evaluation); `ofSpecTool` binds a card to an
   existing `SpecTool`. Beside them `MethodCall`, `MethodEvaluation`, `MethodBinding`, `MethodToolBuild`,
   `MethodAnswer`, and `parameterHash`, `schemaOf`, `definitionOf`, `summaryOf`.
3. **`MethodRegistry.register bindings`**: the `MethodProfile` (one tool per card, the fragment rendered once,
   `describe_method`), or every `MethodRegistrationRefusal` by name. `MethodProfile` carries `Fragment`,
   `FragmentBytes`, `FragmentTokens`, `Description`, `Methods` and `Tools`; the `MethodProfile` module renders an
   `entry`, `entryTokens` and `describe`.
4. **`PipelineCard`**: `evaluate_pipeline`'s card (`cardOf`, `DefinitionHash`) and `binding profile asserting`.
5. **`FactsCompose.withMethods profile`**: declares every method tool and `describe_method` beside the fact tools.
6. **`SystemPromptBuilder.promptFragments`** (ToolUp.AI.Server) serves an empty fragment as nothing and does not
   report it, so an empty method profile changes no prompt.

## A registration

A card built in code, with the evaluator its component supplies:

```fsharp skip=fragment
let meanCard: MethodCard = {
    Identity = { Id = "acme.subject_mean"; Version = "1"; DefinitionHash = "sha256:…"; Package = "Acme.Analytics"
                 Determinism = MethodDeterminism.Pinned }
    Meaning = { Summary = "Average one metric per subject over a slice."; Answers = "What is each site's typical day?"
                Assumes = [ "the days are comparable" ] }
    Applicability = { Preconditions = [ { Name = "MetricInSlice"; Statement = "the metric is one of the slice's metrics"
                                          Remedy = "add it to 'metrics'" } ]
                      DoesNotApply = []; Alternatives = []; Choice = MethodChoice.Mechanical }
    Signature = { ToolName = "subject_mean"
                  Parameters = [ { Name = "metric"; Meaning = "The metric to average."; Schema = """{"type":"string"}"""
                                   Required = true; Default = None; Range = None; Role = None } ]
                  Refusals = [] }
    Outputs = { Tables = []; Diagnostics = []; Warnings = []; Facts = [ "subject_mean" ] }
    Interpretation = { Rules = []; Say = []; NeverSay = [ "a trend" ] }
    Presentation = { Blocks = [ "table" ]; BaseCard = None }
    Examples = []
}

let meanEvaluator: MethodEvaluator = {
    DefinitionHash = "sha256:…"
    Checks = Map.ofList [ "MetricInSlice", fun call -> List.contains (call.Arguments["metric"].GetString()) call.Slice.Metrics ]
    Evaluate = fun read call -> evaluateMeans read call   // Ok MethodEvaluation, or Error "<a card condition's name>"
}

let methods =
    match MethodRegistry.register [ PipelineCard.binding None false; MethodTool.bind meanCard meanEvaluator ] with
    | Ok profile -> profile
    | Error refusals -> failwith (refusals |> List.map MethodRegistrationRefusal.describe |> String.concat "; ")

let app = app |> FactsCompose.withMethods methods
let fragments = SystemPromptBuilder.promptFragments (Some logger) [ PromptFragment.create methods.Description methods.Fragment ]
```

An asserted row's method is `Computed("acme.subject_mean", "1", <parameter hash>)`, and the derived metric it asserts
is registered with `ProducingOperation = Some "acme.subject_mean"`.

## Notes

- **No package pin yet.** The card specification's package has not published; when it does, the registry reads its
  card type and the mirror goes in a later version.
- **The pipeline card binds the text-answering form.** A deployment composing the presentation tier's
  block-answering `evaluate_pipeline` keeps that registration and does not register the pipeline card: `withMethods`
  refuses a tool name already declared. Under a pipeline profile, serve the method profile's fragment in place of the
  pipeline profile's, which the pipeline card's entry carries.

## Verification

`dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Method registry"`.

## Rollback

Remove the `withMethods` call and the fragment; nothing else reads the registry.
