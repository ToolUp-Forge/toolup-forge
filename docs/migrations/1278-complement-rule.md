# Phase 1278: the complement rule and the near-duplicate filter on the clean-room gate

**Who this is for:** a deployment that answers a peer's cohort questions through the clean-room gate (a survey
holder, a model-execution data host) and wants the gate to guard against **differencing**: a released cell whose
complement within its split is a handful of people, and two answers whose filters differ by one member. Both checks
are counts the holder already has; neither is a differential-privacy claim. **Breaking for a full record literal
only** (`ReleaseTerms`, `PeerOptions`, `ModelExecutionDiagnosticsGate` each gain a field); otherwise additive.

**A composition that declares none of this behaves exactly as before:** a template whose release terms leave the
new fields at their defaults encodes, hashes and stays approved as it did, and a composition with no release window
releases the same bytes, records the same decision rows and charges the same budget.

## What to declare

1. **The complement check** is template data: `{ template with Release.Complement = Some(ComplementRule.Within
   "column") }`. Changing it changes the template's canonical hash, so the approval is re-sought. The handler then
   answers a `DeclaredCohort` (`DeclaredCohort.ofResult result |> DeclaredCohort.withSplit { Key = "column"; Base =
   60; Cells = [ "yes␟men"; "no␟men" ] }`): for each released cell of a split under the key, `Base` less the cell
   must clear the floor, or the cell is withheld beside the suppressed ones. An answer declaring no split under the
   key is withheld. The splits are read by the gate and never released.
2. **The near-duplicate check** is a composition option: `PeerOptions.withReleaseWindow
   (ReleaseWindowMeter.create (BlobReleaseWindow blobs) ReleaseWindowPolicy.defaults)` (365-day length and
   retention; `InMemoryReleaseWindow` for a single instance; `BlobReleaseWindow` needs conditional writes). With it
   composed, every gated answer declares its filter cohort, `DeclaredCohort.withFilter (FilterCohort.ofMembers
   memberIds)`, built from stable member identifiers and holding none of them; an answer declaring none is
   withheld. A cohort below the floor, or one within the floor of a cohort the template already released, is
   refused, recorded and charged to the budget whatever its withhold policy.
3. **The scope** is template data: `Release.NearDuplicate = NearDuplicateRule.Compared
   DifferencingScope.PerCounterparty` (the default: one counterparty's releases within the window's length) or
   `Compared DifferencingScope.PerVintage` (every release on the cohort's vintage, to any counterparty; name it with
   `FilterCohort.withVintage`). A template that should not be compared declares `NearDuplicateRule.NotCompared
   "<reason>"`; the reason is in the approved template.
4. **Model-execution fit diagnostics.** `ModelExecutionProfile.withFitDiagnostic` now sets `PerVintage` (a
   fit-diagnostic template's hash moves, so re-seek its approval). Compose the check with
   `ModelExecutionDiagnosticsGate.Differencing = Some { Window = …; RowSet = fun binding rowsOf -> … }`, the
   `RowSet` answering a fit outcome's rows (`ModelExecutionRowSetOf.Outcome`) or a whole vintage's (`Vintage`, for
   the vintage projections) as `FilterCohort.ofMembers recordKeys |> FilterCohort.withVintage v`. A literal adds
   `Differencing = None` to keep today's behaviour.

Both checks hold to `PrivacyGate.differencingFloor`, the larger of `MinCohortSize` and `SuppressionThreshold`. A
refusal reaches the caller as `PeerCleanRoomWithheld`, like every withhold; the verdict (`ComplementBelowFloor`,
`NearDuplicateFilter` naming the earlier cohort's hash, `FilterBelowFloor`) is on the receiver-side decision row.

## Verify

Run the Platform test pack: `Phase 1278 — the complement rule and the near-duplicate filter`, the release-window
cases in `Phase 18b ICleanRoomBroker`, and the two-fits case in the model-execution fit diagnostics.

## Roll back

Drop `withReleaseWindow` (or set `Differencing = None`) and reset `Complement` to `None`: the gate is the pre-phase
gate. A template reverted to the defaults returns to its earlier hash.
