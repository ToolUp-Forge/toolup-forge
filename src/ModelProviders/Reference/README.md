# ToolUp.ModelProviders.Reference

A **trivial, deterministic reference `IModelFitProvider`** for the ToolUp model-fit
envelope (Phase 449). It binds the provider contract pack and proves the seam — it is
**explicitly not statistics**.

## What it does

`ReferenceModelFitProvider.create ()` returns an `IModelFitProvider` with:

- `Kind = "reference"`, `ProviderVersion = "1.0.0"`.
- `DeclareGates () = ["mean"; "abs_mean"]` — the diagnostics it always reports.
- `Fit` — computes a mean/identity-class value as a **pure, seeded function** of the
  request identity (`SpecHash` + dataset key + `Seed`), reports it as the `mean` /
  `abs_mean` diagnostics, and returns a deterministic `ArtifactRef`. No estimator, no
  optimisation, **no dataset read**. `DurationMs` / `CostUnits` are fixed zeros so an
  identical seeded request reproduces a byte-identical `FitOutcome`.

## What it is not

Not a real fitter. A production provider receives an `IDatasetStore` through its
`create` function, reads the vintage's rows, and does actual modelling math — which
**never lives in forge** (GP 1); forge only stores the diagnostics and compares them
against the requested gates (plan D10).

## Usage

```fsharp skip=fragment
open ToolUp.ModelProviders.Reference

// Register the provider, then enable the envelope:
services.AddSingleton<IModelFitProvider>(ReferenceModelFitProvider.create ()) |> ignore
// ServerConfig.ModelFitting = EnabledModelFitting  (requires a composed JobScheduler)
```

Licensed under Apache-2.0.
