# A dataset model over a module's fact tables, and views checked against it

**Ships in:** ToolUp.Platform.Core, ToolUp.Platform.Server and ToolUp.Facts.Server 0.42.0, and the composition profile
`server-app-host` 7.5. Breaking only for a full-literal construction of `ServerModule`, `ServerApp` or
`FactTableComposition`, each of which gains fields; everything else is additive, and a module that declares no model
composes byte for byte as before.

**Affected:** a module that wants its fact tables joined, aggregated or read through named views by a person, a chart
or the assistant; a consumer constructing one of the three records above in full.

## What changes

1. **Dependencies.** `ToolUp.Facts.Server` depends on `Fuaran.DB` 0.1.0 from nuget.org (core package only). The Fuaran
   floors rise to its line: `Fuaran.Core.*` 0.42.0, `Fuaran.Compute.DataFrame` 0.44.0, the Fuaran-UI packages 0.104.0.
   A consumer pinning an older Fuaran package raises it to these.
2. **Declarations** (ToolUp.Platform.Core): `DatasetModelDeclaration` with `FactTableRelation` and `FactTableMeasure`;
   `FactViewDefinition` with `FactViewParameter` and `FactViewColumn` (the pipeline as `Fuaran.Compute` wire JSON);
   `DatasetModelRegistration`, `FactViewRegistration`; the vocabularies `DatasetModelDeclaration.aggregates` and
   `.parameterTypes`.
3. **`ServerModule.declareDatasetModel` / `declareViews`** (fields `DatasetModel`, `Views`); `ServerApp` gains
   `RegisteredDatasetModels` / `RegisteredViews`; `FactTableComposition` gains `Descriptions`, `DatasetModels` and
   `Views`. `FactTablePreflight` gains four rules (`dataset-model-*`) and `datasetModelDefects`.
4. **ToolUp.Facts.Server:** `DatasetModelRegistry` (`register`, `check`, `grantsFor`, `projectFor`, `relationCheck`),
   `ViewRegistry` (`register`, `registerOrRefuse`, `check`, `evaluate`, `card`, `evaluator`, `bindings`),
   `DatasetModelTools` (`describe_dataset_model`) and `FactsCompose.withDatasetModels`.

## Adopting it

```fsharp skip=fragment
let app = ServerApp.create config |> ServerApp.addModule tradeModule   // declares a model and views

let models, views = ViewRegistry.registerOrRefuse app                    // refuses by name
let resolver = DisclosurePolicyResolver.denyUnknown                     // the resolver the gate is built with

let profile =
    PipelineProfile.defaultProfile
    |> PipelineProfile.withSteps [ "join" ]
    |> PipelineProfile.withRelationCheck (DatasetModelRegistry.relationCheck resolver models)

let methods = MethodRegistry.register (ViewRegistry.bindings views @ otherBindings)
```

then compose `FactsCompose.withDatasetModels resolver models`, `FactsCompose.withPipelineProfile` over the registered
profile and `FactsCompose.withMethods` over the method profile. A full-literal `ServerModule`, `ServerApp` or
`FactTableComposition` adds the new fields (`DatasetModel = None`, `Views = []`, …); one built from
`ServerModule.create` or `ServerApp.create` needs nothing.

## Verifying

`dotnet run --project src/ToolUp.Platform.Tests -- --filter "ToolUp.Platform.Tests.Dataset model and views"`: the
declaration refusals, the view refusals, a view as a method tool carrying its identity, the gate withholding through a
view, joins through the relation check, and the caller's projection.

## Rolling back

Drop the `declareDatasetModel` / `declareViews` calls and the compose lines above; nothing else depends on them.
