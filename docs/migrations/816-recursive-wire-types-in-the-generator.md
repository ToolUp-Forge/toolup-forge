# Phase 816 — Recursive wire types in the generator

**Applies to:** any repository that runs `ToolUp.Remoting.Generator` over a wire type that
reaches itself (a recursive union or record), and any code that reads a `GenerationPlan`.
**Breaking:** no for the algebra and the wire — no combinator changed and no byte moved. One
**planning-model** field was added on `ToolUp.Remoting.Generator` (below).
**Action required:** none to keep working. One optional regeneration.

## What changes

Phase 800's census left two platform API records on the reflection path. One, `IConversionApi`,
was blocked by the *generator's* shape rather than the algebra's: `ColumnExpr` is a recursive
union (`Concat of parts: ColumnExpr list * …`, `SplitTake of source: ColumnExpr * …`), every case
of which the combinators express, and the planner refused the cycle because "a cycle cannot be
emitted as dependency-ordered `let` bindings". True, and beside the point — a `let rec` group can.

- **The planner admits a cycle.** A reference back to a type still being planned binds to that
  type's name. After planning, the strongly connected components of the binding graph are
  computed (Tarjan); each component that carries a cycle is a **recursive group**, named in the
  new `GenerationPlan.RecursiveGroups`, and `Bindings` is re-ordered so every group is
  contiguous — each component sits at the position of its last-completing member, which is sound
  because a non-member completed between two members cannot depend on a member (it would then be
  one) and no member depends on anything completed after the group.
- **The emitter renders a group as one `let rec … and …`,** with every member's body eta-expanded
  (`fun value -> (…) value`) so the recursive reference is read on the first decode rather than
  while the module initialises — no FS0040, no runtime initialisation check. Every binding
  outside a group is the plain `let` it always was, byte for byte, so a plan with no cycle emits
  exactly what it did before.
- **Termination is the algebra's own.** A recursive reference is only ever reached through
  `field` / `index` / `list` / `fields`, each of which descends into a strictly smaller subterm,
  and the one-pass reader's 64-container ceiling bounds the value's depth before any decoder runs.

The Phase 784 corpus gains a recursive union (`Tree`, class `Recursive`: recursion through a list
and through a several-field case, the two routes `ColumnExpr` takes) with pinned fixtures; the AOT
sample echoes it and decodes both fixtures through the generated `let rec`; the algebra pack and
the oracle host each carry a hand-written recursive decoder in the emitted shape.

## What moved off the reflection path

`IConversionApi`. The census now reports **37 of 38** platform API records expressible; the one
that remains is `FileManagementApi`, whose `ProcessedFileEntry.Info: obj option` is the
module-summary erasure boundary — a wire-shape decision, not a combinator, and pinned by name.

## The one surface change

```fsharp skip=fragment
// GenerationPlan gains one field; a plan with no cycle carries [].
RecursiveGroups: string list list   // e.g. [ [ "chain"; "ring" ] ]
```

Only code constructing or pattern-matching a `GenerationPlan` is affected. The CLI, the MSBuild
targets and every emitted file for a non-recursive plan are unchanged.

## Regenerating

A repository that commits generated decoders sees a recursive type gain a `let rec` binding (and
its cycle partners `and` bindings) on the next build; nothing else in the file moves.

## Verification

- `Phase 69k` in `ToolUp.Platform.Tests`: `ColumnExpr` plans as a group of one and emits
  eta-expanded; a two-member cycle with a bystander between its members re-orders and groups
  correctly; the census pins `IConversionApi` expressible and `FileManagementApi` alone.
- `Phase 785` / `Phase 787`: the recursive decoder agrees with the reflection reader and with the
  extracted model over the corpus, decodes a spine at the reader's ceiling exactly, and carries
  the path down to a refusal inside a nested branch.
- `dotnet run --project samples/HelloWorld-AOT/HelloWorld.AOT` — 66 of 73 fixtures through
  generated decoders, `recursive-leaf` and `recursive-tree` among them.

## Rollback

Revert the phase's commits. No wire bytes and no consumer baseline changed.
