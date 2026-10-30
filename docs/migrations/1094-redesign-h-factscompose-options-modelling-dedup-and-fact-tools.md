# A model or compute implementation declares what it can do, and a composition that cannot be built refuses as data

**Ships in:** ToolUp.Platform.Core, ToolUp.Platform.Server, ToolUp.Facts.Server and ToolUp.Platform.Build 0.38.0,
the next minor after the tagged 0.37.0. The change is breaking-class: an abstract `Capabilities` member on three
seams, one interface removed, and one public helper removed.

**Affected:** every `IModelFitProvider`, `IModelRegistry` and `IExternalComputeDispatcher` implementation (a class
or an object expression); a backend that implements `IIsolatedComputeBackend`; code that type-probes an
implementation for `IModelEvaluationMetrics`, `IModelEvaluationPlanMetrics`, `IModelRegistrationNovelty`,
`IExternalCallbackCapableBackend` or `IIsolatedComputeBackend`; code that calls
`ModelFitWorkSpec.isArtifactDigest`; and code that matches the message text of a facts-composition failure. A
deployment that composes only the shipped providers, registries and backends changes nothing: each declares
exactly what its probes answered before.

## What changes

1. **`IModelFitProvider.Capabilities: ModelFitProviderCapabilities`** — `{ Metrics: IModelEvaluationMetrics option;
   PlanMetrics: IModelEvaluationPlanMetrics option }`, with `ModelFitProviderCapabilities.none`. `Evaluate` and
   `EvaluateFold` read the record; an undeclared evaluation is refused by name (`EvaluationUnsupported`,
   `PlanUnsupported`) exactly as before. The plan types and the two metric interfaces move from
   `ModelEvaluationTypes.fs` to `ModelFitTypes.fs` (same namespace, same names).
2. **`IModelRegistry.Capabilities: RegistryCapabilities`** — `{ Novelty: IModelRegistrationNovelty option }`, with
   `RegistryCapabilities.none`. The observer decorator reads it; the reference `BlobModelRegistry` declares it.
3. **`IExternalComputeDispatcher.Capabilities: BackendCapabilities`** — `{ Isolated: IsolationPosture option;
   Callback: IExternalCallbackCapableBackend option }`, with `BackendCapabilities.none`. **`IIsolatedComputeBackend`
   is removed**: its posture is `Isolated`. `ExecutionProfileGate.postureOf` reads the record.
4. **A decorator states what it carries.** `ExecutionProfileGate.enforce` re-declares the posture it enforces and
   carries the inner backend's `Callback`. Under the probe the gate hid the callback, so a callback-capable backend
   composed under the `Verified` profile was never handed its credential and resolved by polling only. The budget,
   memo and routing decorators declare `BackendCapabilities.none`, which is what their probes answered.
5. **`CompositionRefusal`** (`MissingDependency` / `InvalidDeclaration` / `UnsupportedImplementation`), raised as the
   exception `CompositionRefused` through `ServiceComposition.refuse`, replaces the bare `failwith` / `invalidOp` in
   `FactsCompose.withTeamOutputVisibility`, `FactsCompose.withDelegateFacts` and `ServerApp.withModelExecution`'s
   registry factory. `ServiceComposition.tryService` and `ServiceComposition.captureDescriptor` are the one
   spelling of an optional resolution and of capturing a registration before decorating it.
6. **`ModelFitWorkSpec.isArtifactDigest` is removed**; `(|Hex64|_|)` (Platform.Core) is the lowercase SHA-256
   pattern. Platform.Core also gains the `result { }` builder.
7. **Additive:** `DatasetStore.readAll` (every row of a vintage over any `IDatasetStore`), `FactTool.define` and
   `FactToolContext` (the shell the three fact tools share), and an `ExternalFitCompletionRegistry(retention, clock)`
   constructor.

## What to do

- Run the codemod: `dotnet run --project Build.fsproj -- Codemod <your source dir>`. Rules
  `1094-fit-provider-capabilities-member`, `1094-registry-capabilities-member` and `1094-backend-capabilities-member`
  add, after each `interface I… with` / `{ new I… with`, the member a type with no capability interface needs:

  ```fsharp skip=fragment
  member _.Capabilities = ModelFitProviderCapabilities.none
  ```

  or, where the same type or object expression implements a capability interface, the record naming it (an object
  expression reaches the interface by downcast, because its `this` is the seam):

  ```fsharp skip=fragment
  member this.Capabilities = { ModelFitProviderCapabilities.none with Metrics = Some(this :> IModelEvaluationMetrics) }
  ```

- **A decorator** forwards the record of what it wraps — `member _.Capabilities = inner.Capabilities` — or states
  what it deliberately does not carry. The codemod writes `.none` for a decorator, which is what the probe answered
  for it; replace it where the wrapped capability should survive.
- **An isolating backend** (rule `1094-isolated-backend`, reported): delete the `interface IIsolatedComputeBackend
  with` block and declare the posture, `{ BackendCapabilities.none with Isolated = Some posture }`. Until you do it
  reads as standard-only and refuses `Isolated` work — the safe direction.
- **A probe** (rule `1094-capability-probe`, reported): read the record instead —
  `provider.Capabilities.Metrics`, `registry.Capabilities.Novelty`, `dispatcher.Capabilities.Callback`.
- **A caller of `isArtifactDigest`:** `match value with Hex64 _ -> true | _ -> false`.
- **A host that caught a composition failure by message:** match `CompositionRefused refusal` and the
  `CompositionRefusal` case instead.

## Verification

Bind your implementation against the contract packs in `ToolUp.Platform.Tests`: `IModelFitProviderContract`,
`IModelRegistryContract` and `IExternalComputeDispatcherContract.contractFor`, each of which now carries the law "a
declared capability is honoured; an undeclared one is refused by name". A backend's pack run also checks that the
profile gate carries its declared callback and posture.

## Rollback

Pin 0.37.0. The change carries no stored data: no record, id or wire shape moved, so a rollback needs no data step.
