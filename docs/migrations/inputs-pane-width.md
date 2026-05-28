# `ClientConfig.InputsPaneWidth` — two-pane Layout inputs-pane width constraint (consumer migration)

**What changes.** The SDK shell's two-pane (`PageContent.SplitPanel`) render now wraps the inputs (left) child in a fixed-width container resolved from a new `ClientConfig.InputsPaneWidth` field. The default `Narrow` applies `w-96 shrink-0` (24rem ≈ 384px), enforcing the "narrow inputs + wide results" design intent shell-side instead of relying on consumer modules to carry intrinsic-width form controls.

Calculator-shaped modules (form `<input>` / `<select>` children with intrinsic widths) settled naturally at ~400px under the prior unbounded layout and are unaffected by the default. Presentation-only modules (descriptive text + toggles, no form fields) had no intrinsic width and previously expanded across the row, squeezing the results pane to a narrow strip — those modules now sit in a constrained 24rem column, matching the visual the shell's `flex-1` results pane was already signalling.

**Scope.** Client-side, shell-only. No server change. No public-surface rename. Consumers that already carried a `w-96 shrink-0` workaround on their two-pane inputs root can drop it.

## Diff to apply

### Default behaviour (no change required)

Consumers on `ClientConfig.defaults` automatically inherit `InputsPaneWidth = Narrow`. If a consumer's modules are calculator-shaped (intrinsic-width form controls), the rendered output is byte-identical to today's natural settle width.

### Drop a hand-rolled `w-96 shrink-0` workaround

```fsharp
// Before — workaround on the inputs root inside the module's view:
let inputs =
    Html.div [
        prop.className "w-96 shrink-0"
        prop.children [
            // … form fields, descriptive text, etc.
        ]
    ]

SplitPanel(inputs, results)

// After — the shell wraps the inputs child; the module returns its
// natural inputs element:
let inputs =
    Html.div [
        prop.children [
            // … form fields, descriptive text, etc.
        ]
    ]

SplitPanel(inputs, results)
```

### Opt out for a balanced split

```fsharp
{ ClientConfig.defaults with
    InputsPaneWidth = Wide }   // 32rem fixed inputs column
```

### Opt out to preserve today's natural-content-width behaviour byte-for-byte

```fsharp
{ ClientConfig.defaults with
    InputsPaneWidth = Auto }   // no width class — pre-shell-constraint shape
```

## Verification

1. `dotnet build ToolUp.Forge.sln` clean.
2. Boot the consumer app; navigate to a two-pane module whose inputs pane was previously presentation-only — confirm the inputs column settles at ~24rem instead of expanding across the row, and the results pane fills the remainder.
3. Navigate to a calculator-shaped module — confirm visual output is byte-identical to the prior version (the intrinsic widths land inside a wider container; the container does not change the rendered width).
4. Toggle `InputsPaneWidth = Auto` in a dev build and confirm today's natural-content-width layout returns byte-for-byte.

## Rollback

Set `ClientConfig.InputsPaneWidth = Auto` on the consumer's composition root. The shell skips the width-wrap branch and renders the inputs child verbatim — equivalent to the pre-migration behaviour.

## Consumers

The migration is **N-A** for calculator-shaped modules, where the default `Narrow` is byte-identical to today's layout, and for any deployment with no two-pane `Layout` usage. A consumer that has previously hand-rolled a fixed-width workaround (e.g. a `w-96 shrink-0` class on its inputs root) drops the workaround on adoption.
