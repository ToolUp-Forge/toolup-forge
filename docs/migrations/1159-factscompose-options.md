# Phase 1159: `FactsCompose` takes one options record

**What changes.** A composition root states its facts configuration as one `FactsComposeOptions` value, composed by
`FactsCompose.withOptions`. The twenty-six `FactsCompose.with*` functions each set one field of it; they still compile
and still work, marked `[<Obsolete>]` with the field they set named, and go at a future major (the window of
`docs/platform/deprecation-policy.md`). The codemod rewrites a chain into the record:

```powershell
dotnet run --project Build.fsproj -- Codemod <your-repo> --phase 1159
```

## The rewrite

```diff
 ServerApp.empty
 |> ServerApp.withStorage blob
-|> FactsCompose.withFactStore
-|> FactsCompose.withFactTableWriter
-|> FactsCompose.withDelegateFacts [ "sku-sales" ]
-|> FactsCompose.withTeamOutputVisibility TeamVisible [ TeamVisible; TeamAdmins ]
+|> FactsCompose.withOptions {
+    FactsComposeOptions.defaults with
+        FactTableWriter = true
+        DelegateFacts = [ "sku-sales" ]
+        TeamOutputVisibility = Some(TeamOutputVisibilityDeclaration.create TeamVisible [ TeamVisible; TeamAdmins ])
+}
 |> ServerApp.run
```

`FactsComposeOptions.defaults` composes the store alone, so `|> FactsCompose.withFactStore` on its own becomes
`|> FactsCompose.withOptions FactsComposeOptions.defaults`. A knob used as a value becomes the applied record:

```diff
-|> RagOptions.withFacts FactsCompose.withFactTier
+|> RagOptions.withFacts (
+    FactsCompose.withOptions {
+        FactsComposeOptions.defaults with
+            EnableStore = true
+            FactTableWriter = true
+    }
+)
```

## The knob map

| Obsolete knob | Field |
|---|---|
| `withFactStore` | none: `FactsComposeOptions.defaults` |
| `withFactTier` | `EnableStore = true; FactTableWriter = true` |
| `withFactTableWriter` | `FactTableWriter = true` |
| `withFactStoreImplementation backend factory` | `StoreImplementation = Some(FactStoreReplacement.create backend factory)` |
| `withQuestionCompiler c` | `QuestionCompiler = Some c` |
| `withDisclosurePurposes c` | `DisclosurePurposes = Some c` |
| `withDeclassificationBudgetConfig c` | `DeclassificationBudgets = Some c` |
| `withDeclassificationBudgets ledger budgets` | `DeclassificationBudgets = Some(DeclassificationBudgetConfig.create ledger budgets)` |
| `withDisclosureFanOut n` | `DisclosureFanOut = Some n` |
| `withDelegateFacts tables` | `DelegateFacts = tables` |
| `withCoverageNarratives o` | `CoverageNarratives = Some o` |
| `withDeliveryTrigger runsFor` | `DeliveryTrigger = Some runsFor` |
| `withToleranceTriggers triggers` | `ToleranceTriggers = triggers` |
| `withTeamOutputVisibility default allowed` | `TeamOutputVisibility = Some(TeamOutputVisibilityDeclaration.create default allowed)` |
| `withFactPublication c` | `FactPublication = Some c` |
| `withFactImport anchors` | `FactImport = Some anchors` |
| `withCoherenceChecks cadence principal` | `CoherenceChecks = Some(CoherenceCheckSchedule.forPrincipal cadence principal)` |
| `withCoherenceChecksConfig c cadence principals` | `CoherenceChecks = Some(CoherenceCheckSchedule.create c cadence principals)` |
| `withNarrativeCalculations c` | `NarrativeCalculations = Some c` |
| `withPipelineProfile p` | `PipelineProfile = Some p` |
| `withSpecTool tool` | `SpecTools = [ FactsComposeOptions.specTool tool ]` |
| `withMethods p` | `Methods = Some p` |
| `withDatasetModels resolver registry` | `DatasetModels = Some(DatasetModelsDeclaration.create resolver registry)` |
| `withFactTablesOptions o` | `FactTables = Some o` |
| `withWithholdingSurface name surface` | renamed `addWithholdingSurface name surface` (a builder's seam: it adds, and composes no store); a root may state `WithholdingSurfaces = [ name, surface ]` instead |
| `withGroundingEnvelopeSeal profile observe` | `GroundingEnvelopeSeal = Some(GroundingEnvelopeSealDeclaration.create profile observe)` |

A list field takes every value a chain gave it: two `withSpecTool` calls are one `SpecTools` list.

## What behaves differently

- **The order is the record's.** The fields apply in one fixed order (`docs/platform/facts.md`, "A root states its
  facts configuration once"), whatever order a chain wrote the knobs in. The rules that said where a knob had to go
  (`withFactStoreImplementation` straight after `withFactStore`, `withGroundingEnvelopeSeal` last, the triggers after
  `withFactTableWriter`) are gone: the record cannot be written in the wrong order.
- **A contradiction is refused once, before anything composes, by its field.** A delivery or tolerance trigger with no
  table writer was refused when the container was built, naming `FactsCompose.withDeliveryTrigger`; it is now refused
  by `withOptions`, naming `FactsComposeOptions.DeliveryTrigger`. A malformed tolerance trigger or visibility
  declaration, and a tool declared twice, are refused the same way. A host that matched the builder name in a
  `CompositionRefusal` matches the field name now. `FactsComposeOptions.refusals` answers the list without composing.
- **The store composes once.** A second `withOptions`, or an obsolete knob after one, composes only its own fields
  over the store the first composed; nothing is declared twice. A knob that used to refuse because the store was not
  composed yet (`withFactTablesOptions` before `withFactStore`) composes the store itself now.
- **An obsolete knob on an enabled app composes the store.** `withDelegateFacts []` used to return the app unchanged;
  it now composes the store, like `withOptions FactsComposeOptions.defaults`. A deployment without the fact store is
  still returned unchanged by every one of them.

## What the codemod does not rewrite

A knob whose arguments span lines, one applied to an argument mid-line (`FactsCompose.withMethods profile app`), and a
chain that sets one option field twice are reported as `1159-knob` and left alone: move the knob into the record by
the map above.
