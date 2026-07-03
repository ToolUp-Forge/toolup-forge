# Migration 277 — hosted-tree a11y conformance harness

**Status:** test/build infrastructure — **no runtime surface; nothing to adopt in a deployment.**

## What changes

Phase 180 asserts accessibility for *modules* from source, but a hosted tree's structure is produced
at **runtime** (possibly AI-emitted), so role / aria coverage and focus order went unchecked. This
phase ships a hosted-tree a11y conformance harness — modelled on Phase 203's (`HydrationParity`)
render-fixture-and-gate shape — that renders a hosted-tree fixture, asserts a11y coverage, and fails
the build on a regression (it runs under `VerifyAll` via the `HostedTreeA11yTests` pack, not a screen
reader).

New surface in `src/ToolUp.Platform.Testing/Testing/HostedTreeA11y.fs` (namespace
`ToolUp.Platform.Testing`):

- `HostedTreeA11y.A11yViolation` — `UnlabelledControl | MissingRole | HeadingSkip | FocusOrderBreak`.
- `HostedTreeA11y.A11yResult` — `Conformant | Violations of A11yViolation list`.
- `HostedTreeA11y.check : string -> A11yResult` — checks a rendered hosted **fragment string** (so
  any tree language's lowering is checkable — the Phase 202 `ToyNode` witness included, GP 1).
- `HostedTreeA11y.describe` — a readable one-line diagnostic per violation.
- `HostedTreeA11y.conformantFixture` + `violationFixtures` — one fixture per failure class.

Checks: interactive controls (`button` / `a[href]` / `input`) have an accessible name (text /
`aria-label` / `aria-labelledby` / `title`); non-semantic interaction sites (`div`/`span` with
`onclick` / `data-action` / `data-toy-event`) carry a `role`; headings don't skip a level; positive
`tabindex` values are in ascending document order.

BCL-only (a hand-rolled scanner, no regex), zero runtime surface, byte-for-byte absent from any
consumer build (GP 13).

## How it runs

The `HostedTreeA11yTests` pack is registered in `Program.fs`, so `dotnet run --project Build.fsproj
-- VerifyAll` exercises it — a hosted-tree a11y regression fails the build.

```
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "HostedTreeA11y"
```

## Rollback

Delete `HostedTreeA11y.fs` + its `<Compile>` entry, `InProcess/HostedTreeA11yTests.fs` + its
`<Compile>` and `Program.fs` registration. No runtime impact — the harness ships no runtime code.

## SDK adoption

⛔ **N-A — test/build infrastructure.** Ships no runtime surface; no consumer adopts it. It is a
build-gate for any deployment that hosts a typed-tree UI.
