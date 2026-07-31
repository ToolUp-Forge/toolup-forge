# Phase 621 — Declarative module-needs registration (consumer migration)

**What changes.** Three registration shapes that carried only a *function* gained an enumerable half,
declared beside the function rather than instead of it:

| Registration | The function (unchanged, still authoritative) | The new declaration |
|---|---|---|
| `ErasedModule.NeedsData` | `(DataTypeId -> bool) -> bool` — the shell's activation gate | `NeedsDataKeys: DataTypeId list option` |
| `ErasedModule.ActionDecoder` | `(actionKey, payloadJson) -> Msg option` — the action router's dispatch | `ActionKeys: string list option` |
| *(nothing)* — outbound `IModuleQueryBus.Ask` calls | still ordinary calls | `QueryTargets: ModuleQueryTarget list option` |

`ModuleSurface.describe` now reports each declaration as ordinary entries — `datatype-need` and
`query-target` on the needs side, `action-key` on the provides side — where before it had only an
`Opaque` note to offer.

**Nothing to do to stay put.** All three fields default to `None`, and a module that declares none of
them produces a **byte-identical** descriptor and byte-identical runtime behaviour (GP 11). Pinned
both ways by test, including the `Opaque` reason strings verbatim — an undeclared module does not
even gain a "declares nothing" clause.

## The one thing that can break your build

Adding fields to `ErasedModule` and `ClientModule<'Model,'Msg>` **retypes their record
constructors**. Two shapes are affected, and only one of them is realistic:

- **Positional / full-record construction of `ErasedModule` or `ClientModule`** — e.g. a test fixture
  or a hand-rolled erasure that writes out every field. Add `NeedsDataKeys = None`, `ActionKeys =
  None`, `QueryTargets = None`. Inside the SDK this forced exactly **five** sites, all test fixtures.
- **`{ existing with … }` record-update and the `ClientModule.create |> with… |> register` chain** —
  the supported and overwhelmingly common form — need **no source change**. Every SDK built-in and
  every documented module goes through the chain.

**Version.** A minor bump under the SemVer-on-`0.x` policy, taken at the next release cut with the
rest of the batch (this repo cuts versions in `chore(release)` commits rolling several phases up,
rather than per phase).

## Declaring

```fsharp
ClientModule.create spec
|> ClientModule.withView view
// The gate declared ONCE — derives the NeedsData conjunction AND the key
// list, so the two cannot drift apart.
|> ClientModule.withRequiredDataTypes [ "SalesData" ]
|> ClientModule.withActionDecoder decode
|> ClientModule.withActionKeys [ "apply-budget" ]
|> ClientModule.withQueryTargets [ ModuleQueryTarget.ofContract Catalogue.skuLookup ]
|> ClientModule.register
```

| Builder | Sets | When |
|---|---|---|
| `withNeedsDataKeys keys` | `NeedsDataKeys` only — **does not touch the predicate** | the gate is not a plain conjunction (any-of, or a predicate over a computed property), so you keep `withNeedsData` and declare the ids you know about |
| `withRequiredDataTypes keys` | both `NeedsData` (`every declared id is available`) and `NeedsDataKeys` | the gate *is* "all of these" — prefer this, it is the shape that cannot drift |
| `withActionKeys keys` | `ActionKeys` | alongside `withActionDecoder`; the keys the decoder matches on **are** the declaration |
| `withQueryTargets targets` | `QueryTargets` | build entries with `ModuleQueryTarget.ofContract` where you ask through a `ModuleQueryContract` — the contract then carries both strings, so the declaration cannot typo either |

## How to read the fields (the part that will mislead you if skipped)

**A declaration is "at least these", never a closed set.** The function stays authoritative: the
predicate may accept an id the list omits, the decoder a key it omits. Consequences:

- The `Opaque` note **survives** the declaration wherever the function is still registered. It does
  not disappear on declaring — it gains a clause saying how many keys were declared beside it.
  Dropping it would read as "this surface is now fully enumerated", which the subset claim does not
  support.
- Do not build a check that fails on something *not* in the list. Build checks in the direction that
  is provable: a declared data type no composed module registers, or a declared query target no
  composed module answers.

**`None` and `Some []` are different, and `Option.defaultValue []` erases the difference.** `None` is
"this module makes no claim" (the pre-621 state). `Some []` is the claim "this set is empty". The
descriptor reports them differently, deliberately, so a module is never recorded as having declared
something it did not.

## `QueryTargets`: why an undeclared `Ask` is neither rejected nor reported

This is the one of the three that is a genuinely new surface, and the design question it raises is
what happens when a module `Ask`s something it did not declare. The answer is **nothing** — it works,
exactly as before — and that is not an omission:

1. **Compose cannot see it.** `IModuleQueryBus.Ask` is an ordinary function call from arbitrary module
   code. No compose-time pass enumerates a module's call sites, so a compose-time rejection would be
   enforcing a claim it has no way to verify.
2. **The bus cannot attribute it.** `Ask(context, request)` carries no caller identity.
   (`ModuleQueryContext.CallerModule` is on the *handler* side and is `None` whenever the caller did
   not name itself.) So even a dev-time warning has nothing reliable to warn about.
3. **Rejecting a working `Ask` would break GP 11 outright.**

The resolution is therefore in the *quantifier*, not in enforcement: the field means "at least
these", and read that way an undeclared ask does not falsify it. This is the same posture
`ActionDeclaration` has carried on the emitting side since it shipped — an inspection surface, not an
enforcement contract.

## What did **not** change: `unsatisfied-needs-data` is still a warning

Phase 583's `unsatisfied-needs-data` rule was expected to become an error once the need was
declarative. It has not, for two reasons worth knowing so the question is not reopened from the same
wrong start:

1. **The new declaration does not reach it.** `NeedsDataKeys` is a **client-tier** field and
   `CompositionValidator` runs server-side; the Server tier does not reference the Client tier. The
   rule's input (`ModuleGraphReferences.DataNeeds`) still carries exactly what it carried before —
   the `ServerModule.VectorisationHandlers` ids, which were already name-declared. Promoting on the
   strength of a declaration the rule cannot see would raise the severity of a population 621 did not
   change.
2. **The claim is a subset even where visible**, so a rule still cannot enumerate every need — which
   is the original reason the severity was a warning.

The GP 11 consequence of promoting anyway is concrete: every deployment booting today with a
vectorisation handler naming a not-yet-registered data type — the staged-producer shape the rule's own
text calls legitimate — would **stop starting on upgrade**. Error severity needs a need the rule can
see in full: either a server-side declaration, or the composition root unioning the client's
declarations into `ServerConfig` the way `FeatureFlags` and `DataTypes` already cross that boundary.
Both are a later phase, not a severity edit.

## Verification

`dotnet build ToolUp.Forge.sln`; `dotnet run --project Build.fsproj -- VerifyAll`. The
`ModuleSurface` pack carries the pins: undeclared-is-byte-identical, `Some []` distinct from `None`,
the opaque note surviving a declaration, and the coverage diff (which **fails loudly** on a
registration field the descriptor has not learned — that is the guard working, not an obstacle).

## Rollback

Revert the commit. The fields are additive-with-a-constructor-retype and nothing reads them at
runtime, so a consumer that never declared is unaffected in either direction.

## See also

- `docs/platform/modules.md` — "Declaring the enumerable half"
- `docs/migrations/582-module-contract-pack.md` — the conformance laws that probe these functions
- `docs/migrations/583-module-graph-rules.md` — `unsatisfied-needs-data` and its scope
