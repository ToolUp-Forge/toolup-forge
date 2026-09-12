# Conformance-pack coverage gate (contributor migration)

**What changes.** `ToolUp.Platform.Build.Tests` gained `ConformanceCoverage`, a gate that runs in
`dotnet run --project Build.fsproj -- VerifyAll` and fails the build when a *replaceable seam* has no
conformance pack, or when a pack is exercised by fewer implementations than the committed baseline
records. The rule it enforces was already policy — GP 12 and the R1 "ship the pack with the
interface" pattern — but nothing computed it, so nothing enforced it.

**Scope.** **Contributors only. Consumers: not applicable.** Zero shipped code changed. Nothing is
added to, removed from, or retyped in any published package; no `ToolUp.*` api-baseline moved. The
new module compiles into a test project that is `IsPackable=false`, and the new data file sits under
`src/ToolUp.Platform.Tests/`, which the `Pack` glob excludes. A consumer upgrading across this
release has nothing to do and will observe no difference.

## What the gate actually asserts

Nothing here is a hand-written list of interfaces, deliberately — a hand-written universe is exactly
how a new seam gets forgotten, which is the failure the gate exists to catch. Four derivations, all
read from the checkout:

| Derivation | Source |
|---|---|
| the public interface universe | every `X (interface)` line in `api-baselines/*.approved.txt` |
| the packs | `src/ToolUp.Platform.Tests/Contracts/*Contract.fs` |
| the bindings | `<Pack>.tests` call sites across `src/**/*.fs` |
| the production implementation count | `interface I… with` / `new I… with` **outside** the test projects |

A **replaceable seam** is a public interface with **two or more production implementations**. That is
GP 12's own definition read off the tree rather than asserted about it, and it is what makes the gate
self-maintaining: the moment a second implementation of anything lands, its interface joins the
must-pack set with no edit to the gate.

Three rules follow:

1. **Every replaceable seam carries a pack** at `Contracts/<Interface>Contract.fs`.
2. **Every pack is run by something.** A pack exposing a `tests` entry point that nothing calls is an
   outright failure with no baseline behind it — the tree has none today, and none may appear.
3. **A pack run by one implementation is not yet proof.** GP 12 treats a portable interface as
   unproven until a second implementation passes the same tests.

## The baseline, and why it is a ratchet rather than a threshold

Measured when the gate landed: **51 of 146** replaceable seams carried a pack, and **63 of 96**
bindable packs had exactly one implementation. Demanding either property outright would have made
the gate red on arrival, and a gate that is red on arrival is one people learn to step over.

So both shortfalls are pinned in
[`src/ToolUp.Platform.Tests/Contracts/conformance-coverage.approved.txt`](../../src/ToolUp.Platform.Tests/Contracts/conformance-coverage.approved.txt),
modelled on the api-baselines beside them — and, like those, **drift fails in either direction**:

- a seam or a pack that appears and is **not** in the baseline fails immediately (nothing is
  forgotten);
- a row whose debt has since been **paid** must be removed (the ratchet cannot slip back, and the
  file cannot rot into a list of things that used to be true).

The list may only shrink. There is no percentage to tune and no ratchet schedule to remember: the
plan *is* the shrinking file.

Counts are deliberately not recorded — only names. An implementation count churns on every unrelated
commit, and a baseline that churns is one whose failures stop being read. The full annotated census
(pack type, binding count, implementation count per seam) is derived on demand by
`ConformanceCoverage.census`, which cannot disagree with the tree the way a recorded one would.

## What you need to do

**Nothing, until you add a second implementation of something, or a new pack.** Then one of these:

**You added a second production implementation of an interface that has no pack.**

```text
IWidgetStore is a replaceable seam (2 production implementations) with NO contract pack, and the
baseline does not know about it. Author src/ToolUp.Platform.Tests/Contracts/IWidgetStoreContract.fs
and bind it, or — if it genuinely cannot be packed — add an [EXEMPT] row with the reason.
```

Write the pack. This is the intended moment: it is the point at which portability stopped being
hypothetical. The shape to copy is any existing `Contracts/I*Contract.fs` — a
`let tests (name: string) (factory: unit -> …)` returning a `testList`, called once per
implementation from that implementation's own test module.

**You added a pack, and only one implementation runs it.**

```text
IWidgetStoreContract is bound by exactly ONE implementation. GP 12 treats a portable interface as
unproven until a second implementation runs the same pack.
```

Bind a second one if there is a second one; otherwise add the pack to `[SINGLE-BOUND]`, which
acknowledges the coverage debt rather than hiding it.

**The seam genuinely cannot have a pack.** Add an `[EXEMPT]` row with the reason:

```text
[EXEMPT]
IWidgetStore: single-implementation by construction — the interface exists for the test double, and a
second implementation would have nothing to conform to
```

`[EXEMPT]` is the one hand-authored section; a regeneration carries it through verbatim, because a
rationale a machine can rewrite is a rationale nobody wrote. An exemption that stops excusing
anything is itself reported, so the section cannot silently accumulate.

**You want to accept the tree's current shape wholesale.** Regenerate with the same switch the
api-baselines use — this artefact is an approval baseline of exactly their family, and a reviewer
accepting one usually means both:

```powershell
$env:TOOLUP_APPROVE_API = "1"; dotnet run --project Build.fsproj -- VerifyAll; $env:TOOLUP_APPROVE_API = $null
```

Approve mode passes trivially, so **re-run without the variable before believing a green** — the same
caveat the public-API gate carries, for the same reason.

## Verification

```powershell
dotnet build src/ToolUp.Platform.Build.Tests/ToolUp.Platform.Build.Tests.fsproj
dotnet src/ToolUp.Platform.Build.Tests/bin/Debug/net10.0/ToolUp.Platform.Build.Tests.dll --filter-test-list ConformanceCoverage
```

22 cases. Four of them are vacuity pins asserting each derivation actually saw the tree: the gate's
content is a set difference, and a set difference over two empty sets is clean, so a derivation that
silently matched nothing would report perfect coverage forever. Eight more drive every finding class
and every parser from synthetic inputs, and the whole set was additionally demonstrated red against
the real tree by deleting a baseline row.

## Rollback

Remove the two `<Compile>` entries from `src/ToolUp.Platform.Build.Tests/ToolUp.Platform.Build.Tests.fsproj`,
the `ConformanceCoverageTests.tests` line from that project's `Program.fs`, and the three files the
phase added (the two modules and the baseline). Nothing else references them, and no shipped package
is involved.
