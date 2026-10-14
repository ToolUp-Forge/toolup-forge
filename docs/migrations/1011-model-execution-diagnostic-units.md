# The model-execution face carries diagnostic units

**Ships in:** ToolUp.Platform.Core (`ModelExecutionGate.Unit`, `ModelExecutionGateVerdict.Unit`,
`ModelExecutionOutcome.DiagnosticUnits`, `ModelExecutionRefusal.GateUnitMismatch`, `ModelExecutionUnits`),
ToolUp.Platform.Server (`GateSpec.Unit`, `GateVerdict.Unit`, `FitOutcome.DiagnosticUnits`,
`ModelArtifact.DiagnosticUnits`, `GateUnitMismatch`, `ModelFitError.GateUnitMismatch`, the new `Gate.evaluate`
signature, the external fit provider's `ModelFitWireGate.Unit`, `ModelFitArtifactDescriptor.DiagnosticUnits` and
`ExternalFitFailure.GateUnitMismatch`). Rides the 0.27.0 draft, whose class is already MINOR.

**Affected:** code that builds any of those records as a full literal (it gains a member: FS0764), code that matches
exhaustively over `ModelExecutionRefusal`, `ModelFitError` or `ExternalFitFailure` (each gains a case: FS0025, an
error in this repository's build), code that calls `Gate.evaluate` / `Gate.evaluateAll`, and an `IModelFitProvider`
implementation (its `FitOutcome` gains `DiagnosticUnits`). A deployment that states no units behaves exactly as
before.

## What changes

The public model-execution wire specification lets a fit outcome state the unit each diagnostic is in, and a gate
and its verdict state the unit the threshold is in (§5.3, §5.4). Units are compared by text and never converted
(§3.5). This face now carries them:

1. **Gates and verdicts state a unit.** `Unit: string option` on the wire gate and verdict, and on `GateSpec` /
   `GateVerdict`. `None` is a gate that states no unit, which is every gate written before this change.
2. **An outcome states its diagnostics' units** in `DiagnosticUnits: Map<string, string>` beside `Diagnostics`, not
   inside it, so the values keep their type. The map is empty where the provider states none. The registry stores
   it. A record stored before this change reads back with an empty map, and its verdicts read back with no unit.
3. **The gate rule.** A gate is evaluated only where its unit and the unit the provider reports for its diagnostic
   are the same text, or both absent (`ModelExecutionUnits.comparable`). Otherwise it is not evaluated: the fit
   envelope returns `ModelFitError.GateUnitMismatch`, the job fails permanently, and **no outcome is registered**.
   This covers a gate stating no unit against a diagnostic that has one, and a gate stating a unit against a
   diagnostic the provider did not report. `Gate.evaluate` and `Gate.evaluateAll` take the unit map and return a
   `Result`.
4. **`ModelExecutionRefusal.GateUnitMismatch(name, unit, reported)`** is the specification's `gateUnitMismatch`
   class on this face. It is declared last, so the earlier cases keep their positions on the positional wire.
5. **The external worker contract.** The `modelfit/v1` payload's gate carries `"unit"` only when the gate states one,
   and the artifact descriptor may carry `"diagnosticUnits"`. Both are omitted when they carry nothing, so a
   unit-free payload and descriptor keep their bytes. A unit for a diagnostic the descriptor does not report is a
   malformed descriptor. `FitExternally` refuses a mismatch as `ExternalFitFailure.GateUnitMismatch`. Through
   `IModelFitProvider.Fit`, the envelope evaluates the gates (it owns the verdicts), so a mismatch is terminal and
   is not retried.

## What to do

- **Record literals.** Add the member and state no unit:

  ```diff
   { Name = "rmse"
     Threshold = 0.5
  +  Unit = None
     Direction = GateDirection.AtMost }
  ```

  ```diff
   { CompositeKey = key
     ArtifactRef = artifactRef
     Diagnostics = diagnostics
  +  DiagnosticUnits = Map.empty
     GateVerdicts = [] ... }
  ```

  The same applies to `ModelExecutionGate`, `ModelExecutionGateVerdict` (`Unit = None`), `ModelExecutionOutcome`,
  `ModelArtifact` and `ModelFitArtifactDescriptor` (`DiagnosticUnits = Map.empty`).
- **A provider that knows its units** states them in `FitOutcome.DiagnosticUnits` as canonical unit text (§3.5, for
  example `kg`, `m/s`, `1` for a dimensionless ratio). Stating a unit changes which gates evaluate: from then on a
  gate on that diagnostic must state the same unit. Coordinate with your submitters before you start stating one.
- **Exhaustive matches** add an arm: `| ModelExecutionRefusal.GateUnitMismatch(name, unit, reported) -> …`,
  `| ModelFitError.GateUnitMismatch mismatch -> …`, `| ExternalFitFailure.GateUnitMismatch mismatch -> …`.
- **`Gate.evaluateAll diagnostics specs`** becomes
  `Gate.evaluateAll diagnostics units specs : Result<GateVerdict list, GateUnitMismatch>`.

## Design

- **Beside, not instead of.** A unit map next to `Diagnostics` keeps `Map<string, float>` as it is. A value type
  replacing `float` would have broken every reader of a diagnostic in order to carry something most of them do not
  read.
- **Text, not conversion.** Units are compared by byte equality. Nothing here relates `g` to `kg`, which is the point
  of the specification's rule: a threshold of 2 in grams against an error in kilograms is a factor of a thousand out
  while looking like a pass, and the submitter, not the executor, knows which was meant.
- **Refused where the units are learned.** Providers declare no units ahead of a fit, so the envelope learns them
  from the outcome and refuses there, before anything is registered. The specification requires exactly this when
  the units are only learned after a fit.
- **Version consequence.** Widening shipped records breaks full-literal construction, and a closed union gaining a
  case breaks exhaustive matches. Both are breaking, so the change is MINOR on the 0.x policy and rides the 0.27.0
  draft, which is already MINOR.

## Verify

`ModelExecutionApi` (Phase 1011 list): the gate rule in every combination of stated and absent units; the envelope
refusing a mismatch by name; a unit-bearing outcome keeping its units through registration, storage and the wire.
`ExternalModelFit`: the payload and descriptor carry units and omit them when empty, an orphaned unit is refused,
and a worker's units are held to the gate. `model-execution-spec-conformance`: every corpus vector round-trips
through the widened records byte for byte, with no unit in any residue, and `Bridge.unmappedRefusalClasses` is empty.

## Rollback

Revert the commit. Records stored with units still read under the previous version, because the reader ignores
members it does not know, but their units are dropped.
