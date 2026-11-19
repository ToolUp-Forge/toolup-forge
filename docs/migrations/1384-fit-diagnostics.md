# Phase 1384: fit diagnostics on the governed-diagnostics contract

**Who this is for:** a deployment that constructs a `CleanRoomTemplate` record in full, a data host that registers
the model-execution governed-diagnostics contract (`ModelExecutionPeerContract.governedDiagnostics`), and a
modeller that calls it. **Breaking surface** (a record gains a field; one function changes shape).

## What changes

1. **`CleanRoomTemplate` gains `Release: ReleaseTerms`**: per-method release declarations, optional calibrated
   noise, and whether a released answer names its withheld cells. All three are template data, so the template's
   canonical encoding covers them, its version moves with them and a bilateral approval binds to them. A template
   whose release terms are `ReleaseTerms.none` encodes and hashes exactly as before, so **every existing approval
   still binds**. A gate composed for a template that switches noise on, without the same `NoisedReleasePolicy`
   and a mechanism, withholds every answer.
2. **Six fit diagnostics join the profile's vocabulary**: `ResidualHistogram`, `ResidualSummaryByBin`,
   `QuantileComparison`, `Calibration`, `ThresholdCurve`, `InfluenceSummary` (`FitDiagnostics.all`). Each takes
   the composite key of an outcome the peer's own `SubmitFit` produced (`ModelExecutionPeerFitDiagnosticRequest`)
   and nothing else; a body carrying any other member is refused as `DiagnosticParameterRefused`, naming it. The
   data host reads the outcome's observations back (`ModelExecutionDiagnosticsDeps.FitObservations`) and forge
   computes the projection (`FitDiagnostics.project`) with the binning the template declares
   (`FitDiagnosticDeclaration`, set with `ModelExecutionProfile.withFitDiagnostic`). A fit-diagnostic answer is a
   `ReleasedCohort`: the released cells and the labels of the cells the floor withheld.
3. **`governedDiagnostics` takes a `ModelExecutionDiagnosticsGate` and returns a `Result`.** The gate value adds
   the approval check, the privacy-budget meter every release is charged to, and the noise mechanism; the
   composition is refused, by name, when the template offers a fit diagnostic without a usable binning or
   switches noise on with no mechanism. An un-noised release is counted at zero ε; a noised one spends the
   template's ε. `ModelExecutionDiagnosticsDeps` gains `FitObservations`.
4. **The profile is at version 2, and a request declares the version that defined its operation.** Every request
   built by `ModelExecutionPeerContract.request` for an operation version 1 defined still declares `1`, so a data
   host that has not upgraded reads it exactly as before; a fit-diagnostic request declares `2`, and such a host
   refuses it as `ProfileVersionUnsupported(2, 1)`.
5. **A one-dimensional governed answer may not carry U+241F in a label.** It is the separator of the
   two-dimensional cell grammar (`CellLabel.grid` / `CellLabel.tryGrid`), which no diagnostic uses yet.

## Do this

Add the field to every full `CleanRoomTemplate` literal:

```fsharp skip=fragment
let template: CleanRoomTemplate = {
    TemplateId = "reach"
    AllowedMethods = Set.ofList [ "EstimateReach" ]
    Floor = floor
    Release = ReleaseTerms.none
}
```

Compose the governed-diagnostics contract through the gate value, and handle the refusal at composition:

```fsharp skip=fragment
let template =
    ModelExecutionProfile.template "diagnostics" floor (Set.ofList [ "Collinearity" ])
    |> ModelExecutionProfile.withFitDiagnostic FitDiagnostics.ResidualHistogram {
        Bins = Some(FitDiagnosticBins.Edges [ -3m; -2m; -1m; 0m; 1m; 2m; 3m ])
        Points = []
        Precision = 0.01m
        ResidualThreshold = None
        LeverageThreshold = None
    }

let gate: ModelExecutionDiagnosticsGate = {
    Broker = CleanRoomBroker.create ()
    Template = template
    Approval = approvalCheck            // or None
    Budget = PrivacyBudgetMeter.create ledger policy
    Noise = None                        // Some mechanism when the template switches noise on
    Audit = sink
}

match ModelExecutionPeerContract.governedDiagnostics gate deps with
| Ok gated -> peer.RegisterContract gated.Registration
| Error refusals -> failwithf "%A" (refusals |> List.map ModelExecutionTemplateRefusal.describe)
```

`deps` gains `FitObservations`; a data host that serves no fit diagnostic answers `None`. A template edited to add
a fit diagnostic is a new template version, so it needs approving again before a gate with an approval check
answers under it.

## What is not done here

- **The differencing check.** Two outcomes over row sets that differ by one record would give two answers whose
  difference is that record. The check that refuses a diagnostic over such an outcome is forge#1278's and has not
  landed; until it does, nothing here prevents it beyond the floor, the template's fixed binning and the budget.
- **Two-dimensional diagnostics and interval bands** (forge#1386), and the modeller-side conversion of an answer
  into a chart (forge#1385).

## Verify

`dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "fit diagnostics"`
runs the fit-diagnostic cases; the federation corpus's `model-execution/fit-diagnostics` vector pins the answers.

## Rollback

Revert the commit. Templates built with `ReleaseTerms.none` hash as they did before it, so no approval needs
re-issuing on either side of the change.
