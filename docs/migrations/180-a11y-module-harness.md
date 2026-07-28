# Migration — Phase 180: accessibility assertions in the module testing harness

**Status:** additive, test-package-only. New surface in `ToolUp.Platform.Testing`; no shipped runtime
code path, no change to any existing API. A deployment is byte-for-byte unchanged (GP 11 / GP 13).
**No consumer action is required** — the assertions do nothing until a test calls them.

## Why

Phase 11a shipped `ModuleHarness` / `ServerHarness` / `Fakes` so a module could unit-test its MVU and
server logic. There was no **accessibility floor** in that harness: a module could pass its entire
test pack while shipping unlabelled buttons, alt-less images, unlabelled form controls, or ARIA that
the browser silently ignores. Nothing caught any of it until a user with assistive tech did.

This phase adds an axe-style rule set that runs **in-process under Expecto** — no browser, no npm, no
new CI job.

## What shipped

### `Accessibility` — the rule surface

New file
[`src/ToolUp.Platform.Testing/Testing/AccessibilityAssertions.fs`](../../src/ToolUp.Platform.Testing/Testing/AccessibilityAssertions.fs),
module `ToolUp.Platform.Testing.Accessibility` (`RequireQualifiedAccess`, as `HostedTreeA11y` is).

**Node model.** A small portable tree the rules run over:

```fsharp
type A11yNode =
    | Element of tag: string * attrs: Map<string, string> * children: A11yNode list
    | Text of string
```

built with `Accessibility.el` / `elem` / `text` / `fragment`, or parsed from a rendered HTML fragment
with `Accessibility.ofHtml`.

**Findings.** Every rule returns `A11yFinding list` — empty means pass:

```fsharp
type A11ySeverity = A11yError | A11yWarning
type A11yProfile  = Minimal   | Strict

type A11yFinding = {
    Rule: string      // e.g. "everyImageHasAlt"
    Path: string      // e.g. "section#panel > nav > button[1]"
    Message: string
    Severity: A11ySeverity
}
```

The severity cases carry the `A11y` prefix deliberately — a bare `Error` would shadow `Result`'s case
at every call site.

**Rules.**

| Rule | Severity | Profile | Catches |
|---|---|---|---|
| `everyInteractiveHasAccessibleName` | `A11yError` | `Minimal` | `button` / `a[href]` / `summary` / any interactive `role` with no text, `aria-label`, `aria-labelledby` or `title` |
| `everyImageHasAlt` | `A11yError` | `Minimal` | `img` / `area` / `input[type=image]` with no `alt` **attribute** (`alt=""` passes — it declares the image decorative) |
| `everyControlHasLabel` | `A11yError` | `Minimal` | `input` / `select` / `textarea` with no `aria-label`, `aria-labelledby`, `title`, wrapping `<label>`, or `<label for="…">` targeting its `id` |
| `ariaRolesAndPropsValid` | `A11yError` | `Minimal` | a `role` outside the WAI-ARIA role set, or an `aria-*` name outside the ARIA 1.2 state/property allowlist |
| `headingOrderIsMonotonic` | `A11yWarning` | `Strict` | a heading level skipped going deeper (`h1` → `h3`) |
| `noColourOnlyState` | `A11yWarning` | `Strict` | best-effort WCAG 1.4.1 heuristic — a state-coloured element with no text, no state-bearing `aria-*`, no `title`, no `role=alert/status` |

`aria-hidden="true"` subtrees are excluded from name computation, as assistive tech excludes them.

**Profiles.** `Minimal` (the default) runs the four `A11yError`-class table-stakes rules. `Strict`
runs those **plus** the two `A11yWarning`-class heuristics **and treats every finding as fatal** —
opting into `Strict` is what makes a warning fail a test. `Accessibility.isFatal` is the predicate.

**Entry points.**

```fsharp
Accessibility.check            : A11yProfile -> A11yNode -> A11yFinding list
Accessibility.assertAccessible : A11yProfile -> A11yNode -> A11yFinding list   // throws on fatal
Accessibility.``assert``       : A11yProfile -> A11yNode -> A11yFinding list   // phase-named alias
Accessibility.ofHtml           : string -> A11yNode
Accessibility.checkHtml        : A11yProfile -> string -> A11yFinding list
Accessibility.assertHtml       : A11yProfile -> string -> A11yFinding list
```

`assert` is an F# keyword, so the phase-named entry needs backticks at the call site;
`assertAccessible` is the ceremony-free spelling of the same function. Both **return** the tolerated
(non-fatal) findings rather than swallowing them.

Shipped fixtures — `Accessibility.cleanFixture` and `Accessibility.violationFixtures` — give an
external implementation something to validate against.

### `ModuleHarness.AssertAccessible` — the fluent member

Two additive members on
[`ModuleHarness`](../../src/ToolUp.Platform.Testing/Testing/ModuleHarness.fs); every existing member
(`Dispatch` / `DispatchAll` / `AssertModel` / `AssertModelWith` / `AssertCmd` / `AssertNoCmd`) is
unchanged:

```fsharp
member AssertAccessible     : render: ('Model -> ('Msg -> unit) -> Accessibility.A11yNode)
                            * ?profile: Accessibility.A11yProfile -> ModuleHarness<'Model,'Msg>

member AssertAccessibleHtml : render: ('Model -> ('Msg -> unit) -> string)
                            * ?profile: Accessibility.A11yProfile -> ModuleHarness<'Model,'Msg>
```

Both render against the harness's **current** model with `ignore` as the dispatch, run the profile's
rules, throw with the consolidated finding list on any fatal finding, and return the same harness so
they chain.

## Adopting it — copy-pasteable

Add an accessibility step to an existing module test chain:

```fsharp
module A11y = ToolUp.Platform.Testing.Accessibility

// A render your test can actually run (see "The rendering constraint" below).
let renderPanel (model: Model) (_: Msg -> unit) : A11y.A11yNode =
    A11y.el "section" [ "id", "panel" ] [
        A11y.el "h1" [] [ A11y.text "Panel" ]
        A11y.el "button" [ "type", "button" ] [ A11y.text model.Label ]
    ]

testCase "the panel keeps its accessibility floor across a rename"
<| fun _ ->
    ModuleHarness.fromUnitInit MyModule.init MyModule.update
    |> fun h ->
        h
            .AssertAccessible(renderPanel)              // Minimal — the default
            .Dispatch(MyModule.Rename "Download")
            .AssertAccessible(renderPanel, A11y.Strict) // opt into the higher bar
    |> ignore
```

Assert an SSR view or a standalone component directly, without the harness:

```fsharp
// Giraffe.ViewEngine / Feliz.ViewEngine — render, then assert.
Layouts.publicPage page
|> RenderView.AsString.htmlNode
|> A11y.assertHtml A11y.Minimal
|> ignore
```

A failure prints the rule, the element path and the fix:

```
Accessibility check failed under the Minimal profile — 2 finding(s):
  [error] everyInteractiveHasAccessibleName at section#panel > button: <button> is interactive but
          has no accessible name (no text content, aria-label, aria-labelledby or title)
  [error] everyImageHasAlt at section#panel > img: <img> has no alt attribute (use alt="" to declare
          it decorative, or descriptive text)
```

## The rendering constraint (read before wiring this up)

`AssertAccessible` takes a **render function**, not the module's `view`, and that is a real
limitation rather than a stylistic choice.

A consumer module's `view` produces a Fable-tier Feliz `ReactElement`. The .NET Expecto runner
**cannot evaluate it** — the same constraint `AriaPropTests` records for the whole Fable-only client
tier, which it therefore checks textually rather than by rendering. There is no existing machinery in
this repo that renders a Fable `Feliz` view server-side (`Feliz.ViewEngine`, used by
`ToolUp.PublicRendering`, is a *different* SSR DSL, not an evaluator for the client-tier one).

So the caller supplies whichever render it can genuinely run:

- **.NET / SSR** — render a `Giraffe.ViewEngine` / `Feliz.ViewEngine` view to its HTML fragment and
  use `AssertAccessibleHtml` / `Accessibility.ofHtml`. This is the fragment-string seam
  `HydrationParity` (Phase 203) and `HostedTreeA11y` (Phase 277) already use.
- **Fable / browser** — under a Fable test runner, hand a mounted node's `outerHTML` to `ofHtml`.
- **Hand-built** — construct the `A11yNode` from the view's shape with `el` / `elem` / `text`. Cheap,
  and it keeps the assertion honest about the markup the module intends to emit.

The practical consequence: **this phase gives you the rule engine and the harness seam; it does not
automatically a11y-check an unmodified Fable view.** A module gets the floor by supplying a render
the runner can execute. Closing that gap properly means either a Fable-side test pack (the shape
`ToolUp.AI.Client.Tests` uses) or an SSR-able view — both out of scope here.

The same constraint bounds the SDK's own regression guard: the stock components run through `Minimal`
in the new pack are the `ToolUp.BrandKit` SSR primitives (page header in both lockups, page footer,
card / text / pill primitives, icon shell) — the forge-shipped components a .NET runner can actually
render. The Fable-tier shell is not covered in-process.

## Verification

New pack
[`src/ToolUp.Platform.Tests/InProcess/AccessibilityAssertionsTests.fs`](../../src/ToolUp.Platform.Tests/InProcess/AccessibilityAssertionsTests.fs),
registered in `Program.fs`, so it runs under `dotnet run --project Build.fsproj -- VerifyAll` with no
new CI job. 40 tests across seven groups: shipped fixtures; per-rule proofs (each rule fires on its
own defect and stays silent on a clean tree); the `Minimal`-vs-`Strict` split; the standalone
`assert` entry and the `ofHtml` seam (including cross-harness coherence with Phase 277's conformant
fixture); `ModuleHarness` chaining plus a proof the Phase 11a API is unchanged; the BrandKit stock
components through `Minimal`; and an allowlist-⇄-`AriaProp.fs` coherence check so the ARIA vocabulary
and the SDK's own blessed helpers cannot drift apart.

## Adoption tracking

`ToolUp/SDK-ADOPTION.md` is **generated** — do not hand-edit it. A consumer records its own stance by
flipping the matching record in its repo-root `sdk-adoption.json` (`adopted` with the SHA once it
calls `AssertAccessible`; `n-a` while it does not). The matrix regenerates from those manifests via
`roadmapctl adoption`.

Consumers are `n-a` by default here: nothing changes until a test calls into the new surface.

## Rollback

Delete `Testing/AccessibilityAssertions.fs` and its `<Compile>` entry, drop the two `ModuleHarness`
members, and remove the test pack + its `Program.fs` registration. Nothing else references the
surface; no shipped code path is involved.
