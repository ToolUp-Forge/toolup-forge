# ToolUp.FeatureFlagProviders.OpenFeature

Opt-in companion that backs ToolUp feature-flag **evaluation** with an external flag system via
the **OpenFeature** standard — so an enterprise that already runs LaunchDarkly / Flagsmith /
Unleash can plug it in behind the same seam. The free in-process flag store (Phase 5c) stays the
default; this is purely additive (GP 2, GP 11).

## How it fits

`OpenFeatureFlagSource` implements the Phase 239 `IFlagSource` seam. Resolution precedence:

```
User → Team → Platform store override  →  IFlagSource(s)  →  declared default
```

A source is consulted only when no in-process scope set the key, and before the declared
default — so wiring it never changes a flag a deployment already manages in-process.

## Usage

```fsharp
open OpenFeature
open ToolUp.FeatureFlagProviders.OpenFeature
open ToolUp.Platform.FlagEvaluator

// 1. Register your OpenFeature provider however your vendor documents it:
do! Api.Instance.SetProviderAsync(myVendorProvider)

// 2. Build the evaluator with the OpenFeature source:
let evaluator =
    createWithFlagSources store declaredFlags [ OpenFeatureFlagSource() ] (Some logger)
```

The adapter maps the caller's `AccessContext` to an OpenFeature `EvaluationContext`
(targeting key = user / session id; `teamId` set for team members) and runs the
correctly-typed evaluation (`GetBooleanDetailsAsync` for `Bool` flags,
`GetStringDetailsAsync` for `Variant` flags). It defers to the next layer on any
`ErrorType` (e.g. the provider doesn't know the flag), so ToolUp's declared default still
applies.
