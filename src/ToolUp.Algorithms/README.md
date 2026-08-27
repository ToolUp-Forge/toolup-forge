# ToolUp.Algorithms

A curated catalog of analytical primitives — regression, descriptive statistics, distribution
fitting, time-series smoothing — with a provider seam so the numerics come from a companion package
rather than from the SDK.

Companion package: a deployment that never calls `withAlgorithms` registers no service, mounts no
route, adds no AI tool, and pays nothing (GP 13).

## Package naming

This companion ships as three **tier-suffixed** packages:

| Package | Contents |
|---|---|
| `ToolUp.Algorithms.Core` | catalog types, the four operation contracts, request validation, the remoting contract |
| `ToolUp.Algorithms.Server` | the provider seam, catalog, dispatcher, AI tool family, compose pipeline |
| `ToolUp.Algorithms.Client` | the read-only catalog proxy |

There is **no** unsuffixed `ToolUp.Algorithms` package published from this repository. If you have
encountered a package by that exact id, it is a different, separately-distributed library and is not
this companion — check the package's repository URL before referencing it. The tier suffixes are
load-bearing here, not decoration.

## Why a catalog rather than "just use a numerics library"

The interface set was chosen by measurement, not by intuition. A pre-build eval
(`evals/algorithms-primitives-eval/`) put a code assistant through five representative tasks against
a raw numerics library, compiled and ran everything it produced, and scored two things separately:
how often the code **failed to compile**, and how often it compiled, ran, and returned **a plausible
number that was wrong for the question asked**.

The two are close to anti-correlated. The tasks that compiled first time produced the two most
dangerous results; the task that failed to compile twice was the one recommended for exclusion.

The measured value of the wrapper turned out to be almost never the arithmetic — the raw library
computed correct numbers wherever it had a surface at all. What it does not do is **state which
convention it used**. Four fields carry essentially the whole delta:

| Field | Closes |
|---|---|
| `DescriptiveSummary.Convention` | R-7 vs R-8 quantiles — a ~4% disagreement with every spreadsheet on a small sample, with nothing recording which ran |
| `SmoothingResult.Alignment` | trailing vs centred windows — the same numbers offset by half a window, an error that survives every visual check |
| `DistributionFitResult.Method` | maximum-likelihood vs method-of-moments — an unexplained discrepancy against any other tool |
| `RegressionFitResult.ReferenceLevels` | which categorical level became the contrast base, without which a coefficient is uninterpretable |

Each was implicit on the raw path and is explicit, echoed, and assertable here.

## Shipped surface

| Concern | File | Contents |
|---|---|---|
| Catalog types | [`Shared/AlgorithmTypes.fs`](../ToolUp.Algorithms.Core/Shared/AlgorithmTypes.fs) | `AlgorithmInfo`, `AlgorithmKind`, `AlgorithmParameterSpec`, `AlgorithmError` |
| Operation contracts | [`Shared/AlgorithmOperations.fs`](../ToolUp.Algorithms.Core/Shared/AlgorithmOperations.fs) | the four request/result pairs, `AlgorithmInvocation` / `AlgorithmOutcome`, `AlgorithmValidation` |
| Canonical parameter specs | [`Shared/AlgorithmParameters.fs`](../ToolUp.Algorithms.Core/Shared/AlgorithmParameters.fs) | `forKind` — the JSON projection of each request record |
| Wire contract | [`Shared/AlgorithmCatalogApi.fs`](../ToolUp.Algorithms.Core/Shared/AlgorithmCatalogApi.fs) | `IAlgorithmCatalogApi` (read-only) |
| Fitter interfaces | [`Server/IAlgorithmFitters.fs`](../ToolUp.Algorithms.Server/Server/IAlgorithmFitters.fs) | `IRegressionFitter`, `IDescriptiveStats`, `IDistributionFitter`, `ITimeSeriesFilter` |
| Provider seam | [`Server/IAlgorithmProvider.fs`](../ToolUp.Algorithms.Server/Server/IAlgorithmProvider.fs) | `IAlgorithmProvider`, `AlgorithmProviderParts`, `AlgorithmProvider.create` |
| Query + execution surfaces | [`Server/IAlgorithmCatalog.fs`](../ToolUp.Algorithms.Server/Server/IAlgorithmCatalog.fs) | `IAlgorithmCatalog`, `IAlgorithmDispatcher` |
| Registry + dispatch | [`Server/AlgorithmDispatcher.fs`](../ToolUp.Algorithms.Server/Server/AlgorithmDispatcher.fs) | duplicate-id rejection at compose, catalog projection, execution path |
| AI tool family | [`Server/AlgorithmAITools.fs`](../ToolUp.Algorithms.Server/Server/AlgorithmAITools.fs) | `_algorithms.list` + one tool per algorithm |
| `/dev/inspect` panel | [`Server/AlgorithmCatalogContributor.fs`](../ToolUp.Algorithms.Server/Server/AlgorithmCatalogContributor.fs) | the "Algorithms" panel |
| Compose pipeline | [`Server/AlgorithmsCompose.fs`](../ToolUp.Algorithms.Server/Server/AlgorithmsCompose.fs) | `withAlgorithms`, `AlgorithmsServerApp` |
| Client proxy | [`Client/AlgorithmsClient.fs`](../ToolUp.Algorithms.Client/Client/AlgorithmsClient.fs) | catalog listing |

## What this package does not contain

**Any numerics.** There is no vendor dependency in any of the three tiers and no arithmetic beyond
argument validation and the AIC/BIC identities. Every fit is executed by a provider companion. That
is GP 1 applied to a family where the temptation to "just add a reference" is strongest, and it is
what makes swapping providers — or comparing two of them on the same catalog — a composition
change rather than a fork.

**Nonlinear curve fitting.** `ICurveFitter` was measured as a control and deliberately excluded; see
`evals/algorithms-primitives-eval/findings.md`. The raw path fitted a two-parameter
diminishing-returns curve correctly from a rough starting guess, first fit. The one gap worth a
future interface is the absence of a convergence signal, and the right trigger for that is a caller
asking for it.

**An execution endpoint on the wire.** The remoting contract lists the catalog only. An analytical
call carries its data by value, so a public execute endpoint is an unmetered compute surface and
needs its own quota design. The two shipped execution paths — the AI tool family (budgeted by the
agent loop) and direct `IAlgorithmDispatcher` resolution inside a module's own handler (where the
module owns the input size) — are both bounded by substrate that already exists.

## Enabling it

```fsharp skip=fragment
open ToolUp.Algorithms
open ToolUp.Algorithms.AlgorithmsCompose

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withStorage storage
|> withAlgorithms (fun a -> a |> AlgorithmsServerApp.withProvider myProvider)
|> ServerApp.run
```

Register every provider in one `withAlgorithms` call — a second call builds a second registry and
the last DI registration wins.

Composing without providers is legal and useful during bring-up: you get an empty catalog, the
`_algorithms.list` tool reporting nothing available, and no execution path.

## Writing a provider

A provider implements whichever fitter interfaces it can serve and assembles them:

```fsharp skip=fragment
let provider =
    AlgorithmProviderParts.create "acme" "1.0.0"
    |> AlgorithmProviderParts.withDescriptive (AcmeDescriptiveStats())
    |> AlgorithmProviderParts.withAlgorithms [
        AlgorithmInfo.declare
            "stats.describe"
            "Descriptive statistics"
            DescriptiveStatistics
            "Summarise a numeric sample."
            (AlgorithmParameters.forKind DescriptiveStatistics)
            (AlgorithmParameters.returnsFor DescriptiveStatistics)
            "Deterministic. Sample (n-1) dispersion. Quantiles honour the requested convention exactly."
       ]
    |> AlgorithmProvider.create
```

Nothing in this package needs editing to add one. Obligations:

- **Honour the echoed conventions.** Reporting `ExcelCompatible` while computing the
  median-unbiased estimate is the precise defect the interface exists to prevent — and one a
  numerics library invites, since several default to R-8 under method names that read as
  spreadsheet-compatible.
- **Return `AlgorithmError`, never raise.** A declared family or kind you cannot serve is
  `Unsupported` naming yourself, never a silent substitution. (The dispatcher wraps escaped
  exceptions into `ExecutionFailed`, but do not rely on it.)
- **Stay stateless between calls** (GP 12 rule 4) — memoise only on the request value.
- **Declare a real `PrecisionContract`.** It is the one place two implementations' numerical
  differences are stated, and `/dev/inspect` surfaces it verbatim so two deployments can be diffed.
- One provider per algorithm id. A clash fails compose naming both providers and the id, by
  design: resolving it by registration order would hide exactly the choice the catalog exists to
  surface.

## Six portability rules (GP 12)

Audited in the header of [`Server/IAlgorithmFitters.fs`](../ToolUp.Algorithms.Server/Server/IAlgorithmFitters.fs)
and pinned by the contract packs in `src/ToolUp.Algorithms.Tests/`. The one documented shape worth
calling out: `IAlgorithmProvider.DeclareAlgorithms` is **synchronous**, following the existing
`IModelFitProvider.DeclareGates` precedent. Compose-time declaration metadata is read once while
building the container, and making it awaitable would force an `Async.RunSynchronously` at compose
— the shape rule 2 exists to avoid, not to create. Every *execution* boundary is async.

## Relationship to the model-fit envelope

`IModelFitProvider` (`ToolUp.Platform.Server`) and `IAlgorithmProvider` are different seams and both
are needed:

| | model-fit envelope | algorithm catalog |
|---|---|---|
| Spec | opaque payload forge never parses | typed request records |
| Shape | long-running, job-scheduled, artifact-producing | request/response, in-process |
| Identity | content-addressed composite key over dataset vintage + spec + seed | a stable algorithm id |
| Forge's role | store, compare gates, audit — never interpret | route, validate, and make conventions legible |

A fitted media-mix model is the first; "what is the 75th percentile of this column" is the second.
