# An `IAIFieldRegistry` a composition root fills

**Ships in:** ToolUp.AI.Core (`AIDeclaredSurface`), ToolUp.AI.Server (`DeclaredAIFieldRegistry`). Rides the current
draft version slot; no version number moves.

**Stability class:** additive. Two new public types; no existing member changes. Nothing to do on upgrade.

**Affected:** only a deployment that wants Tier-3 triage and does not already have its own `IAIFieldRegistry`.

## What changes

`IAIFieldRegistry` shipped as a seam with no implementation a deployment could fill, so each one wrote a registry by
hand. `DeclaredAIFieldRegistry` is one that is filled from data:

- `AIDeclaredSurface` is a plain record, `{ ModuleId; Page; Fields }`, with `Fields` the existing `AIFieldDescriptor`
  list (id, description, value type, instruction patterns, aliases). A composition root that knows its views'
  controls builds one per module and page from them; nothing beyond the descriptor is needed from whatever produced
  the export.
- `DeclaredAIFieldRegistry(surfaces)` answers `Describe(moduleId, page)` from them. Module and page match
  case-insensitively and a page-less surface is its own key. An unfilled key answers an empty snapshot, which triage
  treats as a fall-through to the full agent loop: no exception, no model call. Several declarations of one
  module/page concatenate, and a repeated field id keeps its first declaration.

The descriptors are the same ones Tier 1 matches, and the triage prompt is built from them unchanged.

## Wiring

```fsharp skip=fragment
let registry = DeclaredAIFieldRegistry surfaces
let triage = FastPathTriageConfig.create (registry :> IAIFieldRegistry)
// then AIServerApp.withFastPathTriage triage, as before
```

## Verification

`Phase 6j.B - Tier-3 fast-path triage` -> `DeclaredAIFieldRegistry`: a registered descriptor is what the triage prompt
shows; an unfilled page runs the full loop without a triage call. Phase 175 approves the two additive baselines.

## Rollback

Remove the registry from the composition root. Nothing else depends on it.
