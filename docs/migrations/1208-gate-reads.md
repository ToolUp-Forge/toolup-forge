# The gate declares what it reads: `gate-reads.json`

**Ships in:** the repository's own gate (`GateManifest.fs`, `Build.fs`'s `Gate` target and `GateReconcile` row, the
`ToolUp.Platform.Build.Tests` pack). No package surface moves and no consumer action is needed.

**Affected:** contributors and automated workers who land commits on `main`, and anyone editing the gate manifest.

## What changed

`gate-reads.json` at the repository root now records the non-code files the gate (`verify.ps1`) reads beyond the
.NET default. The landing tooling reads a repository's `gate-reads.json` to decide whether a commit that moved `main`
under a held landing owes a re-gate. Without the record it kept the conservative default and re-gated every crossing.
With it, the gate's inputs are the .NET default (sources, tests, proofs, every build and project file, the solution's
project directories, the formatter configuration, `verify.ps1`) plus the record's `reads`, and a commit touching
nothing in that union is gate-inert.

The record is **rendered, never hand-written**:

- `GateManifest.fs` declares it. `stepReads` holds what `verify.ps1`'s own steps read: the root `dotnet-tools.json`,
  and the files the build packs or embeds. `rowReads` holds what each row's sources open, keyed by row name. `notRead`
  holds the files the census names that the gate does not open, each with its reason.
- `dotnet run --project Build.fsproj -- Gate --render` writes it, beside `EXPECTED_PACKS` and the CI table.
- `Gate --check` and the `GateReconcile` row of `VerifyAll` are red when the committed file is not the render, when a
  `rowReads` key names no row or a row `VerifyAll` does not run in the full lane, and when a row declares file reads
  without `Input.Tree` in its `Reads`.

A row's `Reads` (`Input.Tree`, `Input.Git`, …) says which kind of input a leg depends on, not which files. That is why
the file lists sit beside the rows rather than inside `GateLeg`. `GateLeg` is a type of the published
`ToolUp.Platform.Build` package, and a new case or field there would be a breaking change to every consumer's manifest.

## The census, and its red run

`GateReadsCensusTests` (registered under the `Gate.reconcile` list of the Build pack) is a static census over the
tracked tree. A tracked file outside the .NET default is NAMED when:

- a source that reaches the real repository carries a literal equal to the file's name or path, or a
  `Path.Combine(<locator>, ...)` prefix of it. Reaching the repository means it mentions `repoRoot`,
  `ToolUp.Forge.sln`, `__SOURCE_DIRECTORY__` or `CorpusAnchor`, or it is a root driver file or a
  `ToolUp.Platform.Build` source;
- a project, props or targets file outside `templates/` carries an `Include` / `Update` item resolving to it. This is
  how the Build pack reaches `perf-budgets.json`: its project file copies it into the test output.

Every named file must be covered by `reads` or `notRead`. A `notRead` entry that settles nothing, and a `reads`
entry covering no tracked file, are red too. The census over-reports by design and cannot see a path assembled with no
literal and no locator in its file.

**Shown red once.** With `"perf-budgets.json"` removed from the Build row's `rowReads` and the record re-rendered, the
census failed:

```
1 file(s) the gate may read are in neither `reads` nor `notRead` of gate-reads.json. ...
  perf-budgets.json  (named by src/ToolUp.Platform.Build.Tests/ToolUp.Platform.Build.Tests.fsproj)
```

The entry was restored and the test kept. Its go-red cases over fabricated sources run on every gate.

## What is now gate-inert, and what is not

Inert (a commit touching only these crosses a held landing): the root prose files other than `README.md`,
`CONTRIBUTING.md`, `SECURITY.md`, `CHANGELOG.md` and `CLAUDE.md` (`ACCEPTANCE_TESTS.md`, `CODE_OF_CONDUCT.md`,
`DEPLOYMENT.md`, `LICENSE`, `MAINTAINERS.md`, `NOTICE.md`, `TRADEMARK.md`, `THIRD_PARTY_NOTICES.md`), `Brand Assets/**`,
`icons/**`, `evals/**`, `probes/**`, `client-styling/**`, `rule-errata/**`, the workflows other than `checks.yml` and
`publish-nuget.yml`, the dev scripts no leg runs, and the templates other than the two `ToolUp.Cli` embeds.

**Not inert, by measurement rather than by choice:**

- **`docs/**`.** The publication-boundary sweep scans every markdown file under `docs/`, and several Platform lists
  read specific docs. A docs-only commit still re-gates. No row sweeps the whole tracked set, so nothing records `**`:
  the sweep's surface is `docs/**`, `samples/**`, the root README, CONTRIBUTING and SECURITY, and `src/**`.
- **`perf-budgets.json`.** The Build pack's shipped-budget arm parses the repository's own copy.
- **Any `.fs` file**, formatting-only changes included. Fantomas `--check` and the compiler read them.

## Premise correction

The phase was filed on the reading that two of Phase 1197's three landings re-gated for content no test reads: a
formatting-only commit, then one line in `perf-budgets.json`. Measured against this record, **both crossings touched
files the gate reads.** The first touched `.fs` files, a default input. The second touched `perf-budgets.json`, which
the Build pack opens. The record would have admitted neither, and a re-gate was owed both times. What the record buys
is the crossings that genuinely are inert (the list above). The minutes it saves are therefore not the 1197 figures
(two re-gates, about 30 minutes each).

`builds` / `notBuilt` are deliberately not declared. Undeclared, the landing tooling assumes the root solution and
reports a project the gate does not reach as a Warning. Declared, an unreached project becomes an Error that refuses a
landing, so it needs a `notBuilt` entry with a reason for every project outside `ToolUp.Forge.sln` (the template
scaffolds, the browser-smoke and fuzz harnesses, the probes and tools outside the solution, the snippet projects).
That is a separate decision.

## Measured on the first landing

_Pending: the first gate-inert crossing the record admits on `main` under a held landing window. Record here the
landing tooling's own admission line, naming the record, and the minutes it saved against a full lane._

## Rollback

Delete `gate-reads.json`. The landing tooling then keeps its conservative default and re-gates every crossing. The
census and the render checks go red until the declarations in `GateManifest.fs` are removed with it.
