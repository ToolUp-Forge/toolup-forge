# Phase 1136 — a grounded run accepts requested projections and marks conjecture

**Class:** breaking on the 0.37.0 draft (already breaking), additive for anyone who builds a
`GroundedNarrativeDefinition` with `GroundedNarrativeDefinition.create`. A run that uses neither new form
renders exactly as before, and its instruction is the same, byte for byte (GP 11).

## What changes

- **The projection context.** `GroundedNarrativeDefinition.Project` now takes a `GroundedProjectionContext`
  (the run's resolved scope, its principal, the narrative's surface, and the run's request context, which is
  the gated fact reader) instead of the scope id. `withProjection` keeps its scope-id signature and adapts it,
  so a deployment that registers projections through it changes nothing. `withProjectionContext` registers a
  hook that reads facts. A full record construction of `GroundedNarrativeDefinition` gains
  `RequestBuilders = Map.empty` and `AllowsConjecture = false`, and its `Project` takes the context.
- **Requested projections.** `withRequestBuilder kind builder` lets the model ask for a table of that kind
  under a section's `"projections"`. Each request is built under the projection context and checked by the
  grounding gate on its own. A refused request is named on the audit row, and the rest of the run publishes.
- **Conjecture.** `withConjecture` tells the model it may mark a hypothesis, as `{"conjecture":"..."}` or
  `{{...}}`. It renders as a callout labelled *Conjecture — not a recorded fact*, and any digit in it is
  refused by the gate.
- **The run's decoder and assembler** (`GroundedNarrativeRun.parseOutput`, `assemble`) now read and take
  `GroundedSectionDraft` values in place of `(section id, paragraphs)` pairs, and `assemble` takes the
  requested projections after the deterministic ones. Neither was called outside the run.
- **The audit row** gains `Conjectures`, `Projections` and `ProjectionRefusals`. A row written before 1136
  reads as none.

## Adopting it

The one builder line, for each kind the deployment allows (`ToolUp.Facts.Presentation` draws a table, a
breakdown or a comparison as a table whose every cell cites its Fact):

```fsharp skip=fragment
|> GroundedNarrativeDefinition.withRequestBuilder "breakdown" (fun context request ->
    PresentationNarrative.project (FactBrowseHandler.factBrowseApi context.Request).QuerySeries request.Json)
```

The prompt sentence: the run tells the model the request form and the registered kinds, but only the
deployment knows its tables. Add one sentence to the definition's `Prompt` naming them, for example:

> You may request a breakdown of table `daily-sales` (metrics `revenue`, `orders`; levels `site`,
> `category`) for yesterday when a finding sits in one part of the business.

For conjecture, add `|> GroundedNarrativeDefinition.withConjecture`.

A deployment whose deterministic projection needs facts moves from `withProjection (fun scopeId -> …)` to
`withProjectionContext (fun context -> …)` and reads through `context.Request`. That replaces any static
holder of services it kept for the purpose.

## What a projection can and cannot draw

The gate reads a chart's props as prose, so a narrative projects tables. Requests of kind `trend`, and
requests that carry annotations, are refused by name before any read: a date is a figure to the gate. A
subject or metric whose name contains a digit draws a table that the gate refuses, and the request is refused
on the audit row. See `docs/platform/grounded-narratives.md`, "Projections" and "Conjecture".

## Verification

`dotnet run --project src/ToolUp.Platform.Tests -- --filter-test-list "Phase 1136 H"` runs five cases: a
deterministic projection read under the run's principal with a withheld point named and not drawn, a
requested breakdown whose every number is a gated fact beside a refused unregistered kind, a digit in a
conjecture refused (with and without a citation token), a conjecture rendered as a labelled callout and
counted, and a run with neither that is byte-identical to before.

## Rollback

Pin the previous SDK version. Nothing is persisted in a new shape. The audit row's new fields are ignored by
an older reader.
