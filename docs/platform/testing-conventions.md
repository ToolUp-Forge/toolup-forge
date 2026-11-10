# Testing conventions

Conventions for keeping the SDK's Expecto test suite reliable under default parallel-within-testList execution. The rules below exist because shared mutable state under parallel test execution is one of the few production-shape patterns that flakes deterministically when sibling tests collide on it.

## The "module-level mutable cache" pattern

A handful of SDK substrate files carry a `let mutable private <name>` (or `let mutable internal <name>`) at module scope — a process-local cache or registration list populated lazily on first use and read on every call. For example:

- [`PendingInviteStore.cache`](../../src/ToolUp.Platform.Server/Server/Teams/PendingInviteStore.fs) — 30-second TTL cache of the pending-invite blob, populated on first read and invalidated on every write.

In production this is the right shape: process-lifetime singletons at hot paths, no DI overhead per call. In tests the same shape is a flake source — sibling tests in the same `testList` mutate the shared cache concurrently and observe each other's writes.

**When module-level mutable is OK.**

- Process-lifetime singletons at hot paths where DI resolution per call is measurable.
- One-shot init guards (`registered = false`).
- Compose-time registration lists drained on application start.

**When it's not OK.**

- Anywhere DI resolution per call is cheap enough that the cache buys nothing.
- New code on the SDK boundary. Prefer a record-shaped runtime threaded through `compose` (see how `FileManagementRuntime` retired `FileManagement.fs`'s registration mutables — the last, the post-save hook list, went when hooks became DI registrations the runtime folds in).

**Client-tier consolidation rule (Phase 496).** A client-tier concern (`ToolUp.Platform.Client/`, `ToolUp.AI.Client/`) with **more than one** ambient module-level mutable consolidates them into a **single mutable state record** — one `type private <Concern>State = { … }` carrying the per-field justification as doc comments, plus one `let mutable private state = { … }` binding updated via copy-and-update (`state <- { state with … }`). One documented exception per concern instead of N, and the related fields cannot be updated inconsistently. Reference shapes: `NotificationClient.NotificationClientState`, `UserSession.SubjectAndBridgeState` / `TokenStorageState` (split by lifetime), `CsrfClient.CsrfClientState`. New **lone** flags (one-shot init guards, warn-once latches, single pluggable-reporter slots) stay as plain `let mutable private` bindings and keep the one-line justification comment — a record wrapper adds nothing to a lone boolean.

## Test-isolation strategies

Tests that touch a module-level cache MUST pick one of the following:

### Strategy A — per-module `__internal_resetForTests` + `CacheReset.invalidateAll`

The module exposes an `internal` reset function. Tests call [`CacheReset.invalidateAll`](../../src/ToolUp.Platform.Tests/Support/CacheReset.fs) at the top of any `testCaseAsync` that mutates the shared cache. The registry knows the small finite set of cache-bearing modules; adding a new (b)-class module means appending one line to `CacheReset.fs`.

```fsharp skip=fragment
testCaseAsync "exercises sweepExpired against a real blob"
<| async {
    do! CacheReset.invalidateAll ()
    let storage = InMemoryBlobStorage() :> IBlobStorage
    // … rest of the test
}
```

The reset function lives in the same source file as the mutable, gated by `internal` accessibility — `ToolUp.Platform.Server` already declares `<InternalsVisibleTo Include="ToolUp.Platform.Tests" />`, so the helper is invisible to external consumers.

```fsharp
// In InMemoryPendingInviteStore.fs, inside `module PendingInviteStore`
type private CacheEntry = {
    Map: Map<string, PendingInviteByEmail>
    LoadedAt: DateTime
}

let mutable private cache: CacheEntry option = None

/// Test-only: drop the in-memory cache so a subsequent test starts
/// from a clean slate. Registered via `CacheReset.invalidateAll`.
/// Never called from production code paths.
let internal __internal_resetForTests () = cache <- None
```

### Strategy B — `testSequencedGroup`

When a whole `testList` exercises the same shared cache and per-test isolation is cheaper to express by serialising, wrap the list:

```fsharp skip=fragment
testSequencedGroup "uses shared PendingInviteStore cache" (
    testList "TeamInvitation cache-sensitive tests" [
        // tests in here run sequentially with respect to each other
    ]
)
```

Strategy A is the default for new test work — it scales as test count grows. Strategy B is the right tool when a single `testList` is the only consumer of the cache and the parallel speedup isn't material.

### Strategy A caveat — mid-test cache freshness

Strategy A's reset gives the test a clean starting state, but it does NOT prevent sibling parallel tests from overwriting the cache MID-TEST. Concretely: a test that calls `CacheReset.invalidateAll` then issues `upsert` then reads via `listAll` (which goes through `cachedRead`) can still observe a sibling's cache write between the upsert and the listAll. The window is shorter than without the reset, but not zero.

Two ways to handle this:

- **Prefer cache-bypass verification.** If the substrate exposes a path that reads via `loadFromStore` (cache-bypass) rather than `cachedRead`, use that path for assertions. The restored `sweepExpired` test in [`TeamInvitationTests.fs`](../../src/ToolUp.Platform.Tests/InProcess/TeamInvitationTests.fs) verifies via `sweepExpired` return values — `sweepExpired` reads through `loadFromStore`, so its return value reflects MY storage's blob regardless of what's in the shared cache.
- **Escalate to Strategy B.** When the assertion genuinely requires a cache-going read (`listAll` is the canonical case for `PendingInviteStore`), wrap the test or testList in `testSequencedGroup` to remove the concurrency window entirely.

## The doubles the suite uses are the ones the Testing package ships

`ToolUp.Platform.Testing` ships the test doubles and helpers a consumer's tests need, and the SDK's own
packs use the same ones: **a double the Testing package ships is not re-declared in a test file.** A
pack that writes its own `InMemorySecretStore`, `RecordingAuditLog`, `SilentLogger`, `CapturingLogger`,
`RecordingLogger` or `RecordingMetricsSink` has forked the convention a consumer reads; the Build pack's
`TestDoubleCensusTests` holds the Platform pack to it.

| Need | Shipped | Where |
|---|---|---|
| a secret store | `TestSecretStore()` (`.Seed`; also reached as `Fakes.TestSecretStore`) | `Testing/CoreFakes.fs` |
| an audit log that records | `TestAuditLog()` (`.Records`, `.Events`, `.Recording`) | `Testing/Recording.fs` |
| a metrics sink that records | `Fakes.TestMetricsSink()` (`.Increments`, `.Observations`, `.Gauges`) | `Testing/Fakes.fs` |
| a logger | `Logger.silent`, `Logger.recording ()` (`.Lines`, `.MessagesAt`, `.Contains`) | `Testing/Loggers.fs` |
| any other double that records | `Recording<'T>` | `Testing/Recording.fs` |
| a fixture directory | `TempDir.create "<family>"`, `TempDir.withDir`, `TempDir.forRun` (owned by the run) | `Testing/TempDir.fs` |
| the time, or a wait | `Clock.system`, `Clock.fixedAt`, `Clock.waitUntil` | `Testing/Clock.fs` |
| the four server fakes together | `ServerHarness.createDefaultFakes ()` | `Testing/ServerHarness.fs` |

A test whose assertion reads a double differently adapts the snapshot at the call site; the shipped
double is not widened for one pack. A double that does something the shipped one does not (a store
that fails writes, a log that refuses) stays local under a name that says so (`FailingSecretStore`),
never under the shipped double's name. A part pack (`ToolUp.Platform.Tests.Infra` and the rest of the
family) may not reference the Testing package, which reaches the umbrella; it compiles the Core-only
helper files by source link (`Recording.fs`, `Clock.fs`, `TempDir.fs`, `Loggers.fs`, `CoreFakes.fs`, and
`Fakes.fs` in a pack that reaches the Scope part), never by copy; a companion pack links the ones it uses the same way.

**A double the code under test writes from another thread records into a `Recording<'T>`.** A bare
`ResizeArray` read while a background job, a timer or a callback appends to it throws "Collection was
modified" or answers a torn count, so every read takes a snapshot under the lock the writes take; a list
written and read only on the test's own thread may stay a plain list.

**A fixture that creates a directory owns its deletion: use `TempDir`.** `use dir = TempDir.create
"<family>"` makes `%TEMP%/toolup-<family>-<guid>` and deletes it on dispose; nothing in a test pack calls
`Path.GetTempPath()` itself. The rule, the families and the census are in
[`docs/testing/temp-directories.md`](../testing/temp-directories.md).

**A wait waits on the event, not on the wall clock.** A test that needs the code under test to have done
something waits on the double it writes (`Recording.WaitUntil`, which wakes on the write); a condition
no write signals is polled through a `Clock` (`Clock.waitUntil`). A timeout is a hang backstop, sized so
a loaded machine never reaches it, never the mechanism the assertion relies on: a longer deadline only
widens a race, it does not remove it.

## Audit checklist

Every `let mutable` at module scope in `toolup-forge/src/` MUST fall into one of two classes:

**Class (a) — documented exception.** Acceptable when the mutable is:
- A warn-once advisory flag (`OidcAuthProvider.unmappedRolesWarned`) — written-once, read-only-by-observers, no per-test reset hazard because the test surface doesn't observe it.
- Fable-tier client-side state (`ToolUp.Platform.Client/`, `ToolUp.AI.Client/`, `AuthProviders/OidcClient/`, `Feliz.AgGrid.Enterprise/`) — the .NET test runner doesn't compile these modules' browser-only branches and doesn't poison the cache through the test entry points.

Each (a) site MUST carry a comment naming its classification: `// (a) — process-lifetime warn-once flag, no Expecto reset hazard` (or similar).

**Class (b) — exposes `__internal_resetForTests`.** Required when the mutable is:
- A read-on-every-call cache populated lazily (`PendingInviteStore.cache`).

Each (b) site MUST be registered in `CacheReset.invalidateAll`.

## Running the audit

```powershell
Set-Location C:\repos\ToolUp\toolup-forge
& "C:\Program Files\Git\usr\bin\grep.exe" -rn "^let mutable " src/
```

Filter to server-tier paths under `src/ToolUp.Platform.Server/` and `src/AuthProviders/Oidc/` — client-tier paths fall under the Fable-tier (a)-class by construction.

## Every Expecto pack runs sequenced by default

Each pack's `Program.fs` passes `CLIArguments.Sequenced` as the default:

```fsharp skip=fragment
[<EntryPoint>]
let main argv = runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests
```

It is a *default*, not a hard `testSequenced` wrapper, so `--parallel` on the command line still overrides it. Prefer the default; reach for `--parallel` only to reproduce a concurrency-specific problem, and expect it to hang (see below).

### Why: Expecto deadlocks when parallel tests write to the console

Expecto replaces `Console.Out` / `Console.Error` with a synchronized `FuncTextWriter` so it can attribute a test's output to that test. A test thread writing through it takes that writer's monitor and descends into `ANSIOutputWriter.prettyPrintInner` → `flushInner`, which fires the `ProgressIndicator`, which writes to the **real** console stream and blocks in `ConsolePal.WriteFromConsoleStream` — while sibling threads sit on the writer's monitor it still holds. Two locks, acquired in two orders, with no timeout on either. The process hangs forever.

Almost every pack here drives a subject that logs (`ConsoleLogger`, compose-time warnings, the `toolup` CLI's own output), so this is not an exotic case.

Measured on a Linux host, `ToolUp.Cli.Tests` (36 cases, every one of which runs a real `toolup` command, and every command prints):

| configuration | completed |
|---|---|
| parallel (default before this change) | 3/6 |
| parallel, `--no-spinner` | 4/6 |
| parallel, `--colours 0` | 0/6 |
| parallel, `--parallel-workers 2` | 4/6 |
| **sequenced** | **6/6, and 10/10 on a repeat run** |

Two things that look like fixes and are not: **no CLI flag helps** — `--no-spinner` still hangs, `--colours 0` makes it worse — and **upgrading Expecto does not help** either; 11.1.0 completed 1/6 against 10.2.3's 3/6 on the same pack.

`--parallel-workers 2` still hanging is the important row: a 2-core CI runner is exposed too, not just a big dev box. `ToolUp.Platform.Tests` hangs reliably on a 16-core host and had merely been getting away with it on a 2-core runner.

**Sequencing is not the performance cost it looks like.** `ToolUp.Platform.Tests` (5,214 cases) runs in 4m28s sequenced — these packs are dominated by I/O and compose-time work, not by parallelisable CPU. It also makes the CI log readable, since output stops interleaving mid-line between tests.

**If you add a pack**, copy the entry point above. **If you are tempted to make a pack parallel again**, the bar is that its subject writes nothing to the console — not that it currently happens to pass.

## `VerifyAll` builds once, then runs each pack's dll — and the build is a precondition (Phase 731)

`dotnet run --project Build.fsproj -- VerifyAll` is still the canonical invocation, but what it does underneath changed in Phase 731, in two coupled ways worth knowing before you reach for either.

**It builds once, then invokes each pack's built test dll directly (`dotnet <dll>`), rather than shelling `dotnet run --project <pack>` per pack.** The reason is a hang, not a speed-up: the `dotnet run` launch path intermittently wedges *before the suite starts* — ~0% CPU, no output, indistinguishable from a slow pack until someone gives up waiting on it. That failure mode is worse than an ordinary red, because nothing about it says which it is. Invoking the built dll has no such step. Each pack's assembly is asked of MSBuild (`-getProperty:TargetPath`) rather than assembled from a `bin/<config>/<tfm>/<name>.dll` convention, so a project that sets `AssemblyName` or a custom output path stays correct instead of failing in a way that looks like a missing pack. The child's working directory is the caller's — measured, not assumed; `dotnet run` does *not* move the child into the project directory — so a pack resolving a relative path sees exactly what it saw before. **If you add a harness that shells a test pack, invoke the dll, not `dotnet run`.**

The per-pack `PASS` / `FAIL` summary block is unchanged, and deliberately so: CI parses it and fails the job unless at least `EXPECTED_PACKS` lines report `PASS` — a floor rendered from the gate manifest (below), so adding a row moves it through `Gate --render`, never by hand. A `PENDING` line (an arm whose named input is absent) is counted in neither. The up-front build sits **outside** that aggregate — a build failure means no pack can run, so it fails the target directly rather than producing a summary about a tree that does not compile.

**The Public-API approval gate's build precondition is now enforced, and it fails once.** `Phase 175 — Public-API approval baseline` renders each packable assembly's surface from its built DLL, so a pack run against a tree whose companion `bin/` directories are empty has nothing to compare. It used to answer that with one assertion failure per unbuilt assembly — 52 of them — which reads like a catastrophic surface break and has cost at least one session a wrong diagnosis. It now resolves each DLL **when the case runs** rather than snapshotting the answer at process start, and reports a missing build as a single `the solution is built` failure naming the count, a bounded sample, and the remedy; the per-assembly cases defer to it. The pack is still red — a precondition that let the run report green would be the vacuous shape the section below exists to end. **So: `dotnet build ToolUp.Forge.sln` before running the Platform pack on its own.** Running it through `VerifyAll` needs no such care, because the target now does that build itself.

## The gate manifest: a new gate is a row

`GateManifest.rows` (repo root, `GateManifest.fs`, source-linked into the Build test pack) is the one statement of what the forge gate runs. A row is a `GateLeg`: its `Name` (the token in the `VerifyAll summary:` block), its `Kind` (an `ExpectoPack` project, a `Body` returning a `Verdict`, or a `Script` path), its `Lanes` (`TestLane.Lane` — a pack whose own test sources read nothing is in `pure`; every pack is in the other three), its `Where` (`Local` under `VerifyAll`, `Ci <job>` for a `checks.yml` / `proofs.yml` job, `Release` for `publish-nuget.yml`), its `Reads` (`Tree`, `Git`, `Node`, `Browser`, `Docker`, `Network`, `Env <name>`), its measured `Budget` and its `Floor`.

Everything that used to restate the list is a render of the rows, and each render is held by a test or a `--check`:

- `BuildConfig.TestPacks` is `Gate.packs` over the `Local` pack rows; `GateReconcile` holds every test project (`*.Tests.fsproj`, or a `.Tests.` segment in the file name) to a pack row or a named exclusion (`GateReconcileTests`).
- The `pure` lane is the pack rows with `Reads = []`; `PureLaneTests` re-runs the recorded grep over each such row's sources and is red when one reads the tree.
- CI's `EXPECTED_PACKS` is `Gate.ciFloor`, the count of rows whose `Where` carries `Ci "verify-all"`; `dotnet run --project Build.fsproj -- Gate --check` refuses a drift and `Gate --render` writes it. The CI table in `CLAUDE.md` is rendered from `Where` the same way, and `GateManifestTests` holds every `checks.yml` job to a row or a named CI-only reason, and every row's job to a step that runs it.
- Each pack is launched under a scrubbed environment: every `Env` variable some other row declares and this row does not is cleared, so a pack cannot read a credential it did not name (`Gate.scrubbedFor`).
- `VerifyGate` serialises `VerifyAll` and every `Body` row that reads the tree for over a minute; its stale-holder bound is twice the full lane's summed `Budget`, never a literal. Each budget is read from `gate-timings.json` (below), or is the row's declared seed where the record has no median for it.
- `VerifyAll` prints the lane's measured duration once at the end of a run and a trailer naming the rows it did not run (`Where` without `Local`) with their reads; `VerifyGate` appends one admission record per run to `.git/fuaran-gate/verify-runs.jsonl`, and `dotnet run --project Build.fsproj -- GateTimings [--since <days>]` folds them into per-lane and per-row medians. With `--record` the fold is written to `gate-timings.json`.
- A `Floor` is applied by the fold, not the body: `AtLeast n` / `Exactly n` against the `Pass` count, `HighWater <file>` against the committed mark, `FromRegistry proofs.json` by `proofs/check.ps1`, which reads each differential host's case names from the claims registry and fails by name on a case that stops running.

`dotnet run --project Build.fsproj -- Gate --run <row>` runs one row on its own (building what it names), which is how the CI-only jobs invoke theirs. A consumer's `Build.fs` that sets `TestPacks` and calls `VerifyLeg.register` is unchanged: both are derived views onto `Gate.register`.

## What the gate costs: `gate-timings.json`

How long the gate takes is a committed record, not a figure to quote: read `gate-timings.json` at the repository root.
It is the fold of the admission journal, and it states its window (`since`, `until`), the runs it folds and the
machines that ran them. Per lane it gives the median and p90 wall clock of the green `VerifyAll` runs. Per row it gives
the median seconds over the green full-lane runs. A run is green when its exit code is 0 and no leg failed.

- `dotnet run --project Build.fsproj -- GateTimings --record` writes it. The window is seven days (`--since <days>`
  widens it) ending at the newest recorded run, so the same journal folds to the same record whenever it is folded. A
  re-fold that would change nothing but `foldedAt` leaves the file alone, which makes a second `--record` over the same
  journal a no-op diff. A journal with no runs writes nothing and says so.
- A row's `Budget` in `GateManifest.rows` is its seed. `GateManifest.withRecordedBudgets` replaces it with the record's
  median, and `budgetSource` says which one a row carries. `VerifyGate` reads the derived budgets, so the stale-holder
  bound sums the same figures the planning tooling reads.
- `GateReconcile` and `Gate --check` are red when the record measures a row `VerifyAll` does not run in the full lane,
  or when a row it runs has neither a median in the record nor a seed. They warn when the record's window ended more
  than 14 days ago. `--record` refuses to write a record that would fail that check.
- The record is in `gate-reads.json`'s `notRead`, so a commit carrying only it is gate-inert. The close-out of a series
  of landings runs `GateTimings --record` and commits `gate-timings.json` by name.
- Never edit it by hand. A row added to the manifest needs a seed until a recorded run measures it.

The journal lives under one clone's git directory, so a record folds one machine's runs. A fold on another machine
replaces the record with that machine's window, and `machines` says which machine was measured. Machines are hashed
labels (the first eight hex digits of the SHA-256 of the upper-cased host name), never host names: the journal is
machine-local, the record is published.

## What the gate reads: `gate-reads.json`, and what belongs in `reads` versus `notRead`

`gate-reads.json` at the repository root says which NON-CODE files the gate reads. The landing tooling treats the
.NET default (sources, tests, proofs, every build and project file, the solution's project directories, the
formatter's configuration and `verify.ps1`) plus the record's `reads` as the gate's inputs, and everything outside the
union as gate-inert: a commit on `main` touching only gate-inert files is crossed by a landing without a re-gate.
The record is **generated**. `GateManifest.fs` declares it (`stepReads` for `verify.ps1`'s own steps, `rowReads` per
row, `notRead`), `dotnet run --project Build.fsproj -- Gate --render` writes it, and `Gate --check` and the
`GateReconcile` row are red when the committed file is not the render. Never edit the JSON by hand.

**A path belongs in `reads` when the gate `verify.ps1` runs opens it.** That means one of its own steps (the root
`dotnet-tools.json` for the tool restore), a project item the build copies or embeds (`perf-budgets.json`, which the
Build pack copies into its output), or a file a row's sources open from the real repository (`api-baselines/**`,
`CHANGELOG.md`, the docs and samples the publication-boundary sweep reads). Declare it on the row whose sources open
it. That row must read the tree (`Input.Tree` in its `Reads`) and must be a row `VerifyAll` runs in the full lane.
Name a directory with `**` when the row walks it. Do not list files one by one under a directory the row walks.
Count an existence check as a read. Deleting the file changes the verdict.

**A path belongs in `notRead` only when the census names it and the gate does not open it**, each with its reason:
a fixture literal that shares the file's name (a test writing `README.md` into a temp directory), a file a target
writes but no leg reads (`THIRD_PARTY_NOTICES.md`), or a file only a CI-only row reads (`docs-snippets/corpus-floor.txt`,
the `doc-snippets` job). A CI-only row is not the gate a landing re-runs, so its reads go in `notRead`, never in
`reads`. A `notRead` entry never widens the inert set. The landing tooling does not consult it. It only settles a
census finding, and an entry that settles nothing is itself red.

**The census is static and over-reports on purpose** (`GateReadsCensusTests`, registered under the `Gate.reconcile`
list). It names a tracked file outside the default in two cases. A source that reaches the repository (it mentions a
root locator, or is a root driver file or a `ToolUp.Platform.Build` source) carries a literal equal to the file's name
or path, or a `Path.Combine(<locator>, ...)` prefix of it. Or a project, props or targets file outside `templates/`
carries an item that resolves to it. Every named file must be covered by `reads` or `notRead`. The census cannot see
a path assembled with no literal naming it, or a reference held in data. When a test opens such a file, add it to
`reads` by hand. That is what the record exists to say.

**No row sweeps the whole tracked set.** A row that did would record `**`, which leaves nothing inert. The
publication-boundary sweep (`OpenCoreVocabularyNeutralityTests`) reads a declared surface: `docs/**`, `samples/**`,
the root README, CONTRIBUTING and SECURITY, and the packable projects' sources and READMEs under `src/`. So `docs/**` is
a gate input, and a docs-only commit still re-gates. That is the honest answer while the sweep scans docs. The fitness
sweeps enumerate the checkout with `git ls-files --exclude-standard` and open only code-shaped files. They read every
`.gitignore`, which is why `.gitignore` and `**/.gitignore` are in `reads`. Background and the census's red run:
[`docs/migrations/1208-gate-reads.md`](../migrations/1208-gate-reads.md).

## The Platform family: one pack per server part

The Platform test pack is partitioned the way the server tier is: one pack per part, each a row of the gate manifest,
each referencing only its part's closure.

| Row | Project | Holds |
|---|---|---|
| `Platform.Remoting` | `src/ToolUp.Platform.Tests.Remoting/` | the lists that reach the Remoting part and no later one, and the lists that reach no server part |
| `Platform.Infra` | `src/ToolUp.Platform.Tests.Infra/` | the lists that reach the Infra part and no later one |
| `Platform.Config` | `src/ToolUp.Platform.Tests.Config/` | the lists that reach the Config part and no later one |
| `Platform.Scope` | `src/ToolUp.Platform.Tests.Scope/` | the lists that reach the Scope part and no later one |
| `Platform.Jobs` | `src/ToolUp.Platform.Tests.Jobs/` | the lists that reach the Jobs part and no later one |
| `Platform.Audit` | `src/ToolUp.Platform.Tests.Audit/` | the lists that reach the Audit part and no later one |
| `Platform` | `src/ToolUp.Platform.Tests/` | the residue: the lists that reach the umbrella (Compose, Lifecycle, Files) or a companion built on it |
| `Platform.PublicApi` | `src/ToolUp.Platform.Tests.PublicApi/` | the public-API approval gate, built over every packable project |

`src/ToolUp.Platform.Tests.Support/` is the harness every member references (the lane filter, the registration guard,
the corpus anchor, the shared entry point `PackEntry.run`). It holds no cases and is a named exclusion. The suite
consuming `ToolUp.Platform.Testing` (Phase 1100) absorbs it.

The rules for a new list or a new test file:

- **The test sources stay under `src/ToolUp.Platform.Tests/`.** Each member compiles its files from there, as each
  server part compiles its sources from the umbrella's folder. Which pack a file is in is read from the project files,
  not its folder. Companion packs and `Build.fsproj` source-link files from there, so the paths do not move.
- **A list goes to the latest part its test file reaches.** That is the latest part whose assembly it uses a symbol
  of, or names in a string literal (a runtime load, or a scan of that part's sources). A file that uses another test
  file's symbols reaches what that file reaches. A `[<Tests>]`-bearing file used by a file of a later pack moves to
  that pack. A file without bindings that two packs use is compiled by both. A list that reaches the umbrella or a
  companion built on it stays in the residue.
- **Register it in its pack's `Program.fs`.** The registration guard sweeps that pack's assembly, so a list
  registered in the wrong member is red in the right one.
- **An `open` of a part the file does not use does not compile** in a pack that does not reference the part. Remove
  it: the partition removed every such open it exposed.
- **Internals.** A member that reads a part's internals needs that part to grant `InternalsVisibleTo` to the member's
  assembly. Build the part clean once after adding the grant.

The map of every list to its pack, with the reach that placed it:
[`docs/migrations/1194-platform-pack-partition.md`](../migrations/1194-platform-pack-partition.md).

## `[<Tests>]` alone does not run a test list — register it (Phase 722)

These packs do **not** use Expecto's `[<Tests>]` auto-discovery. Each `Program.fs` calls `runTestsWithCLIArgs` over an explicitly-enumerated list, so the attribute is decoration: a new `[<Tests>]`-attributed binding that is not appended to that list **compiles, is attributed exactly like every other, and silently never runs** — and the pack reports its usual green with a total nobody reads as suspicious. Phase 634 hit this: its first full-pack run reported `7,045 passed / 0 failed` having executed none of its seven new cases, and it was caught only by probing `--list-tests`. **So: append every new list to the pack's own list in `Program.fs`, and confirm with `--list-tests` (or the run's case count) before trusting a green.** A filtered run is worse, not better — Expecto's `--filter` joins the test path with `.` and reports `Success!` when it matched nothing.

Since Phase 722 the omission is loud rather than silent. Every pack with this shape appends `TestRegistrationGuard` (source-linked from `src/ToolUp.Platform.Tests/Support/TestRegistrationGuard.fs`) to its own list:

```fsharp skip=fragment
let private registeredTests =
    testList "ToolUp.Example.Tests" [ FooTests.tests; BarTests.tests ]

let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 2 registeredTests
```

The guard adds three cases: the **subset check** (every `[<Tests>]`-attributed binding reflection finds in the assembly appears somewhere in the registered tree, failing by name), a **non-vacuity floor** on how many bindings the sweep found (so a sweep that has gone blind cannot satisfy the subset check over the empty set — the same reason `VerifyFable` asserts a TAP case floor rather than reading `node --test`'s exit code), and a **falsifier** that runs the comparison over a deliberately-omitted binding and asserts it goes red, paired with the control that it falls silent once that binding is registered. Comparison is by physical identity, not by label, so an unrelated nested test name cannot silence a real omission; wrapping a registered list (`testSequencedGroup`, `testList`) keeps its children's references and is fine.

A binding that genuinely must not run in the .NET pack — the standing case is a client-tier list whose module body touches Fable `importDefault` dummy code — is declared as a `TestRegistrationGuard.Exemption` with its reason and passed to `withGuardExempting`. The declaration is data the guard reads, not prose beside the list: an exemption naming a binding the assembly no longer carries, or one the pack has since registered, **fails the guard** rather than quietly excusing something.

Three packs (`ToolUp.Stripe.Tests`, `ToolUp.Voice.Tests`, `ToolUp.Cloud.Parity.Tests`) call `runTestsInAssemblyWithCLIArgs` instead — real auto-discovery, where `[<Tests>]` **is** sufficient and this whole class cannot arise. They carry no guard, deliberately. The client-tier Fable harness is a different runner again; its floor lives in `VerifyFable`.

## A companion's tests live in its own pack

A case belongs in a **companion pack** (`src/<Family>/ToolUp.<Family>.Tests/`, one per companion family of `version-groups.json`) when
the companion's own assembly is what it exercises. Examples are a sink's wire format, a backend's binding of the SDK
contract pack, a provider's configuration parsing, and a live arm against the real service. A case stays in
**`ToolUp.Platform.Tests`** when its subject is the core. That covers the SDK's contract packs themselves (the laws, under
`Contracts/`, with their self-tests against the in-tree defaults) and the cases over the core's own implementations. It
also covers integrations whose subject is a core tier and which use a companion as one backend among others, until that
use is factored behind a fake. A companion pack references its family, the substrate and whatever the contract packs it
hosts need, and no other SDK tier. A case that needs one is not a companion case.

Mechanics, all held by a gate:

- **Contracts are source-linked, not copied.** `<Compile Include="..\..\ToolUp.Platform.Tests\Contracts\IXContract.fs"
  Link="Contracts\IXContract.fs" />` compiles the one copy of the laws into the companion pack. A copy under any
  `Contracts/` directory is compared with its source up to the self-test boundary by the Build pack's `vendored contract
  copies` list. A copy with no registered boundary fails it, and the remedy named is the link.
- **A source-linked file that carries a `[<Tests>]` binding is exempted by name** (`withGuardExempting`). It is linked
  for its fixtures, and its cases are the Platform pack's.
- **Internals.** A case that reads an `internal` needs the owning assembly to grant `InternalsVisibleTo` to the pack. The
  server tier's parts grant each companion pack that needs it, beside `ToolUp.DataSources.Tests`. A grant does not move
  the granting assembly's reference assembly, so build that assembly clean once after adding one.
- **Live backends are env-gated**: Pending (a `skiptest` naming the variable) without the variable, never Failed. The pack's
  gate-manifest row declares the variable, because `VerifyAll` clears every other row's variables for each pack.
- **One row per pack** in `GateManifest.fs`. `Gate --render` moves `EXPECTED_PACKS` and the CI table, and `GateReconcile`
  is red for a test project with no row.
- **The pack's directory ends in `.Tests`** (`src/<Family>/ToolUp.<Family>.Tests/`). The tree scans that hold production
  code to a rule exclude test code by that path segment: the analyzer inventory's `segmentSuffix` and
  `VerifyScopeLiterals`. A pack under a directory named plain `Tests` reads to them as production code, and both legs
  go red on the moved fixtures.

Which files have moved, and which are still in the Platform pack and why:
[`docs/migrations/1197-companion-test-packs.md`](../migrations/1197-companion-test-packs.md).

## The `affected` lane: run the lists a change can reach

`TOOLUP_TEST_LANE=affected` with `TOOLUP_TEST_LISTS=<file>` runs the Platform family's lists named in the file and
every other pack whole. The file is written by `Select`. A name in it is `<row>/<list>` for a member of the family, or
an unqualified name for the residue (`Platform`), as before the partition; `<row>/*` keeps a member whole. Each member
reads its own share, and a member the file names nothing of runs its registration guard alone. The measurement the
lane was cut from is in [`docs/migrations/1121-affected-lane-v0.md`](../migrations/1121-affected-lane-v0.md).

```powershell
dotnet run --project Build.fsproj -- Select --base origin/main --head HEAD --declared tests.txt --out .affected/selection.json
$env:TOOLUP_TEST_LANE = 'affected'; $env:TOOLUP_TEST_LISTS = "$PWD/.affected/selection.lists"; pwsh ./verify.ps1
```

**When it may be used.** It is a worker's fail-fast lane: run it on your own change, before the merge, to learn in
minutes whether the lists that change reaches are green. It is **not citable**. A ship cites the full lane, run on the
folded tree. A selected run becomes citable only once the gate runner accepts a selected record under a declared
selection policy for this repository, and until then nothing reads its manifest but a person. **A release always runs
`full`**, whatever any policy says.

**What `Select` selects.** It unions three sources and records which source admitted each unit:

- **declared** — the lists the change is known to reach, from `--declared` (one per line: `<pack>/<list>`, or a bare
  Platform list by its label or by the binding a member's `Program.fs` registers; a bare binding is placed in the member
  that registers it, a bare label in the residue). A list in another pack runs anyway, because that pack runs whole.
- **diffed** — every Platform-family list whose defining test file is in the diff, read off the members'
  registrations and the module the file defines. A new test file's lists are in by construction. An edited test file
  re-runs whole.
- **always** — each member's registration guard, `Phase 175 — Public-API approval baseline` (in `Platform.PublicApi`),
  every other pack (the `pure` packs among them) and the Analyzers leg.

There is no coverage map yet, so there is no derived source, and every manifest says `map: none`.

**Forcing paths run the full lane, and the manifest names the rule that fired:**

| Rule | Paths |
|---|---|
| `msbuild-import` | any `*.props` or `*.targets` |
| `solution` | `*.sln`, `*.slnx` |
| `build-script` | `Build.fs`, `Build.fsproj`, the repo-root sources the build script compiles, `verify.ps1` |
| `toolchain` | `global.json`, `nuget.config` |
| `composition-root` | `ToolUp.Platform.Server`'s `Compose/`, `ServerApp.fs`, `SDK.Server.fs` |
| `server-client-config` | `ServerConfig` (`SDK.Shared.fs`, `Shared/Config/`) and `ClientConfig` (`SDK.ClientTypes.fs`) |
| `module-registry` | a module's `*Compose.fs`, the client's `RegisteredModules.fs` |
| `test-support` | the Platform pack's `Support/`, `ToolUp.Platform.Testing`, a non-source file in the Platform pack |
| `test-project` | a `*.Tests.fsproj` |
| `unpinned-test-source` | a Platform test file whose lists cannot be named. Either it is registered only through an applied, argument-taking entry (a contract pack), or it defines no registered list at all (a helper other lists share). |

Under a forcing rule the lists file holds `*`, so the Platform pack keeps every list and every leg runs. Each rule is
pinned in both directions in `ToolUp.Platform.Build.Tests/AffectedLaneTests.fs`. A rule added without a pinned row
fails that pack.

**The Platform pack's `Program.fs` is deliberately NOT a forcing path.** Every change that adds a list touches it,
and the registration guard runs in every lane. The cost: a registration added without its test file changing is not
diffed. Declare it.

**The legs follow the diff.** The Fable leg runs only when client-tier code moved. Samples builds every sample in every
lane, and transpiles the Fable ones only when client-tier code moved. Analyzers always runs. "Client-tier" is decided
conservatively: anything under `samples/`, and any `src/` project not named as server-only, build-only or
.NET-test-only. A guess can only add minutes, never skip a leg that mattered. A lists file written by hand, with no
manifest beside it, runs every leg.

**What the run says about itself.** `Select` writes the manifest (`--out`) and the lists file beside it (`.lists`),
which points back at the manifest. Under `affected`, `VerifyAll` prints the manifest summary before anything runs:
units by source, every skipped list, the forcing rules checked and any that fired, the legs, `map: none`. The Platform
pack then prints each list it kept (with the line that kept it) and each it skipped, with case counts. A log cannot be
mistaken for a full run.

**The keep is the one name-keyed selection, and it fails closed.** Every other lane selects by physical identity.
`affected` reads names from a file, so it resolves each line to the registered lists it names: by label, or by
binding through reflection. A line that names no registered list makes the pack exit non-zero before running
anything, and so does a missing or empty lists file. This is the `--filter` lesson: a mistyped name must never become
a green run of nothing. The manifest's skipped list is exact when every declared Platform name is a binding. A declared
label is resolved only by the pack, so the manifest marks its skipped list `skippedExact: false` and the pack's own
print is the exact account.

**The graph source: whole packs by the project graph.** `Select --source graph` selects packs instead of lists. A
changed path belongs to the projects whose items name it; failing that, the projects whose wildcard items cover it;
failing that, the project whose directory holds it most closely. So a server part's source, which sits in the umbrella's
folder, is the part's, and a test file is the pack's that compiles it. A pack is selected when its closure (its project
and every project it references) holds a changed path's owner, and runs whole. Every other pack runs nothing and is
named as skipped, unless a declared or always-on unit names it. The manifest gains `"source": "graph"` and a `graph`
block with the selected and skipped packs, each with the case count of its newest recorded run, and the changed paths
no project owns (they select nothing). Only the graph-blind forcing rules apply (`msbuild-import`, `solution`,
`build-script`, `toolchain`): the graph places a composition-root file, a test project or a support file itself.

```powershell
dotnet run --project Build.fsproj -- Select --base origin/main --head HEAD --source graph --out .affected/selection.json
```

A change to one server part selects that part's pack, the packs of the parts after it, the residue and every
companion pack over the umbrella, because each of those compiles against the part. It skips the packs of the parts
before it. A change confined to the Audit part, the last, selects the Audit pack and the residue among the family.

**When the graph source may be cited: the same rule as the declared source.** It is a fail-fast lane and not
citable. A ship cites the full lane, run on the folded tree, until the gate runner accepts a selected record under a
declared selection policy. A release always runs `full`. The graph is the build's own and always current, so it is the
structural fallback for the declared source and a cross-check on a coverage map. It cannot see what a test reads at
run time outside its closure: a document, a baseline file, a fixture no project item names. A changed path no project
owns is listed as unowned for that reason, and a list that reads one runs only when it is declared.

**What it does not protect against.** A selected run is unsound by construction. A regression in a list nobody
declared, no diff touched and no forcing rule covers will pass it. Two things keep that cost bounded. The full lane is
still the gate, and the lane says what it skipped.

### The build floor under a selected run

A selected lane still builds the solution first. What that build costs depends on what the change touched, because
reference assemblies are on (`ProduceReferenceAssembly`, declared in `Directory.Build.props` and held by
`CompilerLeversTests.fs` in the Build pack):

- **Internal edit to an implementation.** The edited project recompiles. Its dependants do not, because they compile
  against its reference assembly and that did not change. On a warm worktree an internal Server edit took about 1.5
  minutes (2 projects) and an internal Core edit under a minute.
- **Public change.** Every dependant recompiles: 129 projects for Server and 193 for Core, close to a clean build.
- **A changed or added `[<Literal>]`.** The reference assembly does not move, so dependants are not recompiled and an
  incremental build keeps the old value. Build them clean (`--no-incremental`) before you trust a green over a literal
  change.

The gate itself is unaffected. The Public-API approval list renders the implementation DLLs in `bin/`, never a `ref/`
copy, so an internal edit leaves every baseline unchanged and a public addition still fails it by name. A clean solution
build, the floor for a fresh checkout, is still the 4m18s quiet-machine figure. The measurements are in
[`docs/migrations/1195-compiler-levers.md`](../migrations/1195-compiler-levers.md).

## The build census: what each project costs to build

[`build-census.json`](../../build-census.json) at the repository root records, for every project in
`ToolUp.Forge.sln`, its build seconds, its evaluation seconds, the lines in its `Compile` items, its fan-in (solution
projects that reference it) and its fan-out (solution projects it references). It also records the tree SHA, the
machine, the UTC time of the measurement, the wall clock of the build and the command that produced it. Rows are
sorted by build seconds, most expensive first. Read it before you plan a project split or size a gate's build budget,
so the plan follows measured cost rather than folder size.

**The figures are one machine's, and they are dated.** They come from a single clean graph build on the machine the
file names, at the time it names, and that machine may have been busy. Compare projects against each other within
one file, and prefer their ranking to an exact ratio. Contention moves the totals most, but a busy run also stretches
the projects that build in the wide middle of the graph more than the ones that build early. Do not compare a total
from one census with a total from another machine. A per-project figure is that project's own build time under
`-graph`, so it never includes time spent waiting for a reference. Because projects build in parallel, the per-project
seconds add up to several times the wall clock.

**Regenerate it** whenever the project set changes, and whenever you need current figures:

```powershell
dotnet run --project Build.fsproj -- Census
```

That target rebuilds the whole solution once from scratch (`dotnet build ToolUp.Forge.sln -graph --no-incremental`).
It takes as long as a clean build and leaves every project rebuilt. Commit the regenerated file.

**The file cannot go silently stale.** `dotnet run --project Build.fsproj -- Census --check` builds nothing. It is red
when the file's project set differs from the solution's, and it names each project added or removed. The Build pack
runs the same check over the committed file (`BuildCensusTests.fs`), so adding a project to the solution without
regenerating the census reddens `VerifyAll`. Timings are never compared, because they differ from machine to machine.
Why the census reads a graph build's performance summary, not a binary log or a plain solution build, is in
[`docs/migrations/1199-build-census.md`](../migrations/1199-build-census.md).

## Testing client-tier MVU update functions

Client-tier modules (`ToolUp.Platform.Client`, `ToolUp.AI.Client`, etc.) cannot be exercised by the `.NET` Expecto runners. The blocker is module-level construction of ToolUp.Remoting proxies:

```fsharp
// PlatformAIKeysAdminUI.fs — typical AI.Client module shape
open ToolUp.AI

let private api =
    Api.makeProxy<PlatformAIKeysApi> (customOptions = UserSession.withRequestHeaders)
```

F# initialises module-level `let` bindings eagerly through the static constructor on first member access. ToolUp.Remoting's reflection-based proxy builder is shaped for Fable's runtime: `buildProxy` walks the API record and constructs a record of `FSharpFunc<_,Async<_>>` values via a closure (`normalize@…-1`) that the .NET reflection layer cannot bind back to the record's field types. The first call to `init ()` or `update msg model` triggers the static constructor, which throws `System.ArgumentException` before any test assertion runs.

The fix is to test through the same runtime the code actually deploys to: Fable transpile → Node test runner. The reference harness lives at [`src/ToolUp.AI.Client.Tests/`](../../src/ToolUp.AI.Client.Tests/).

### Runner choice: `node:test`, not Fable.Mocha

The harness uses Node's built-in test runner (`node:test`, stable in Node 20+) plus `node:assert/strict`, not `Fable.Mocha` + npm `mocha`. Both reach Fable-compiled F#; the differentiator is the supply-chain story.

`mocha` 11.7.x's transitive dep tree carries unaddressed audit findings on `serialize-javascript` (RCE via crafted `RegExp.flags` / `Date.toISOString`, CVSS 8.1) and `diff` (DoS in `parsePatch` / `applyPatch`). The [Mocha team's official position (#5690)](https://github.com/mochajs/mocha/issues/5690) is that neither is reachable through Mocha's actual surface — the test runner only processes developer-written test code, not untrusted input — but `npm audit` does not make that distinction. Every contributor running `npm audit` in the test project would see "3 vulnerabilities, 1 high," and no current `mocha` version clears them.

`node:test` sidesteps the noise entirely: it ships with Node itself, no transitive npm deps to audit. The thin `NodeTest.fs` shim ([source](../../src/ToolUp.AI.Client.Tests/NodeTest.fs)) gives the same Expecto-style API the rest of forge uses (`testCase` / `testList` / `Expect.equal` / `Expect.isTrue` / …) on top of `node:test` + `node:assert/strict`.

### How to add a new client-tier test pack

1. **Add a `.fs` file under [`src/ToolUp.AI.Client.Tests/`](../../src/ToolUp.AI.Client.Tests/)** alongside `PlatformAIKeysAdminUITests.fs`. The pack's top shape:

   ```fsharp skip=fragment
   module ToolUp.AI.Client.Tests.MyNewTests

   open ToolUp.AI.Client.Tests.NodeTest
   // open the modules under test

   let tests =
       testList "MyNewSuite" [
           testCase "describes what this verifies" <| fun _ ->
               let actual = subjectUnderTest input
               Expect.equal actual expected "what the assertion proves"
       ]
   ```

2. **Register the new file** in `ToolUp.AI.Client.Tests.fsproj` `<Compile>` list (after `NodeTest.fs`, before `Program.fs`).
3. **Register the suite** in `Program.fs`'s top-level `allTests` — append it to the `testList` argument list.
4. **Run the gate** from `src/ToolUp.AI.Client.Tests/`:

   ```powershell
   dotnet tool restore
   npm install --no-fund --no-audit
   dotnet fable -o output --noCache
   node --import ./register-loader.mjs --test output/Program.js
   ```

   The `--import ./register-loader.mjs` flag activates the asset-import loader hook (no-ops `.svg` / `.css` / `.png` etc. imports the Fable-emitted JS carries from Feliz components — see [`test-loader.mjs`](../../src/ToolUp.AI.Client.Tests/test-loader.mjs)). The `--test` flag puts Node into test-runner mode; exit code reflects pass/fail.

### What this harness does and does not exercise

- **Does exercise**: pure `update` functions, model transitions, Msg → Cmd plumbing, `Cmd.batch` / `Cmd.none` composition, any pure F# code reachable from the Fable-transpiled output.
- **Does not exercise**: Feliz `view` rendering (no DOM), React `useState` interaction, browser-only APIs (SSE event sources, IndexedDB, `window.*`), `Cmd` execution (Cmds are constructed but not run).

Adding view-level tests is a follow-on once a concrete view-level case lands. That work would add JSDOM as a devDependency, plus a small `Feliz` mount helper, and would compose on top of the same `NodeTest.fs` facade.

### When to use this harness vs `Platform.Tests`

| If you want to test… | Use |
|---|---|
| Server-tier infrastructure (storage, queue, validators, dispatch) | `ToolUp.Platform.Tests` (`.NET` Expecto) |
| Client-tier source via textual analysis (analyser, presence check, anti-pattern audit) | `ToolUp.Platform.Tests` — see `SvgPropTests` / `SubjectWildcardAnalyzerTests` |
| Client-tier MVU `update` runtime behaviour | `ToolUp.AI.Client.Tests` (this harness) |
| Live AI provider response shape | `ToolUp.AIProviders.Tests` (env-gated `.NET` Expecto) |

### Which job transpiles which client tier (Phase 345)

The names mislead, so this is written down rather than left to be re-derived from
[`.github/workflows/checks.yml`](../../.github/workflows/checks.yml).

| Client tier | Transpiled in CI by | Via |
|---|---|---|
| `ToolUp.AI.Client`, `ToolUp.KnowledgeBase.Client` | `fable-tier` | `VerifyFable` → `src/ToolUp.AI.Client.Tests`, which references both |
| `ToolUp.Platform.Client` (+ `Platform.Core`) | `fable-tier` **and** `browser-smoke` | transitively through `ToolUp.AI.Client`; and directly by the browser-smoke fixture |
| **`ToolUp.Offline.Client`** | **`browser-smoke` only** | `VerifyBrowserSmoke` → `tests/BrowserSmoke/fixture` |

The tier with one point of coverage is the **offline** one. Exactly two projects in the tree
reference `ToolUp.Offline.Client` — the browser-smoke fixture and `samples/MinimalClient` — and no
CI job compiles the sample: `cd samples/MinimalClient && dotnet fable -o output` is the developer's
phase-boundary check, never a gate. `ToolUp.Platform.Client` is the better-covered case and is
easily misread as the exposed one, because the fixture's own header talks about the client tiers it
references; `VerifyFable` has driven Platform.Client transitively through
`ToolUp.AI.Client` since long before the browser gate existed.

Two consequences worth knowing before you change either job:

- **A dropped `<ProjectReference>` in the fixture would not go red — it would narrow the gate and
  stay green**, with eight browser scenarios still passing. `VerifyBrowserSmoke` therefore asserts
  the fixture's compile set before it installs anything, and its failure names this table. Changing
  what the gate covers means editing that list and this section in the same commit.
- **Widening `VerifyFable` to the offline tier was considered and rejected.** It would compile that
  tier a second time, in a second job, for a `node:test` pack holding no cases over it — wall-clock
  with no added signal. The sanctioned gate for offline-client transpilation is `browser-smoke`; if
  that job is ever removed or made non-gating, the transpile is re-homed in the same commit.

## What this convention does NOT do

- It does not force `testSequenced` everywhere. Expecto's parallel-within-testList execution is a real productivity feature.
- It does not migrate genuinely-load-bearing `mutable private` sites to DI-resolved alternatives — that's a separate refactor concern. The auth-pipeline `metricsSink` migration is the canonical shape: register the sink as a substrate dependency at compose time and pass it explicitly through the pipeline rather than carrying it in module-level mutable state, then retire the `let mutable private metricsSink = NoOpMetricsSink` site.
- It does not eliminate test-flake root causes other than shared-cache pollution. Wall-clock timing assertions (`Async.Sleep` budgets, scheduler-jitter-sensitive thresholds) are a separate concern with separate fixes.

## Reading the convention from a new module

1. Run the audit grep on your new module's source path.
2. For each `let mutable` site, decide (a) / (b) per the audit checklist.
3. Add the `// (a) — …` comment OR the `__internal_resetForTests` helper.
4. If (b), append a line to `CacheReset.invalidateAll`.
5. Write the test using Strategy A (`do! CacheReset.invalidateAll ()`) or Strategy B (`testSequencedGroup`).

The convention's enforcement is by audit and by code review — there is no compile-time gate (the F# language doesn't carry "this mutable is process-lifetime safe" as a type). The audit grep is cheap enough to run on every refactor that touches an SDK substrate file.

## Documentation snippets are compiled (Phase 620)

Every `fsharp` block under `docs/**` and `src/ToolUp.Platform/technical-guide/**` is extracted and
compiled against the real SDK by the `doc-snippets` CI job. Run it exactly as CI does:

```
dotnet run --project Build.fsproj -- VerifyDocSnippets
```

This closes a class no test could see: a reader copies a fenced block verbatim, so a snippet naming
a renamed or removed API is a defect that ships to every reader while every suite stays green. It
is the mirror of the XML-doc coverage gate — that one asserts *docs exist on code*, this one asserts
*code in docs still resolves*; neither subsumes the other.

**Writing a block needs no ceremony.** Ambient `open`s are supplied by the harness, never by the
markdown, so what a reader copies stays exactly what an author meant to show. If a block genuinely
cannot compile, mark its fence with a reason from the closed set — `fragment`, `signature`, or
`anti-pattern`:

````markdown
```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withStorage (LocalFileStorage("./data") :> IBlobStorage)
|> ...
```
````

The marker is invisible when rendered (every renderer takes the first info-string word as the
language). "It does not compile" is **not** a reason — `skip` claims a block is not *checkable*, and
a block that is checkable and wrong belongs in the shrink-only `docs-snippets/known-drift.txt`
baseline, or gets fixed.

Full convention, the baseline's ratchet, and how context is supplied:
[`docs-snippets/README.md`](../../docs-snippets/README.md).

## Generated documentation pages, and the one command that moves them (Phases 214 / 697 / 765)

Four committed artefacts are **projections of the config-key registry** (`ConfigKeys.keys` in
`src/ToolUp.Platform.Core/Shared/Types/ConfigKeyDescriptor.fs`), not hand-maintained documents:

| Artefact | What is generated | Projected by |
|---|---|---|
| `docs/reference/config-reference.md` | the whole file | `ReferenceDoc.render` |
| `docs/reference/toolup.config.schema.json` | the whole file | `ConfigSchema.render` |
| `docs/operations/env-vars.md` | the **`Description` column** of every table headed `Variable` | `OperationsDoc.render` |
| `DEPLOYMENT.md` | the region between the `BEGIN GENERATED` / `END GENERATED` markers: each key's direction, and the boot seal's coverage | `DeploymentDoc.render` |

One command refreshes all four:

```
dotnet run --project Build.fsproj -- GenerateConfigReference
```

(`pwsh dev-scripts/generate-config-reference.ps1` is the same target under a shorter name.) It
builds the Platform pack and runs its `ConfigReference` test list with
`TOOLUP_REGEN_CONFIG_REFERENCE=1`, which flips those four arms from *comparing* to *writing*. Without
the variable they compare, and `VerifyAll` is red whenever a committed artefact disagrees with the
registry — so **a registry edit and its regenerated artefacts land in the same commit**, or the gate
says so.

**The operations page is the interesting one, because only a column of it is generated.** Whole-file
generation was rejected for it: it covers a curated subset of the registry and each row carries two
facts the registry does not hold — the `ServerConfig` field the value binds to, and the reader's
parse contract. So the projection rewrites one cell per registry-backed row and passes every other
byte through; the prose, the other columns, `SERVER_PORT` (no `TOOLUP_` prefix, so no descriptor)
and the client-side `define` tables all survive regeneration unchanged. **Never hand-edit a
`Description` cell** — the next regeneration overwrites it. If the page's wording is the better one,
move it into the `ConfigKeyDescriptor` and every projection gains it at once.

A byte-comparison quantifies over what the page HAS, so two things it cannot see are asserted
separately by `OperationsDoc.issues`: a table that dropped its `Description` column (the projection
would then have nothing to say about its keys, and the compare would pass), and a row naming a
`TOOLUP_*` variable the registry no longer describes (a rename leaves the page documenting a
variable nothing reads). Both fail by name and neither is regenerable — regenerating over them would
write a file that is still wrong.

## Certifying against an external conformance corpus (Phase 602)

Most fixture corpora in this repo are *emitted* from the code they certify — the federation-seam
corpus in the federation-seam specification home (a separate public repository — see
`docs/interplatform/FEDERATION_WIRE.md`) is regenerated by the live emitters and compared
byte-for-byte, so a shape change that did not regenerate it fails in the same commit. That catches
drift, but it cannot catch a *misreading of the specification*: whatever the emitter does becomes
"the format" by default.

A second kind of corpus exists for the wire surfaces that are governed by a specification this
repo does not own. There the corpus is **external**: neither authored nor vendored here, canonical
over this implementation rather than derived from it, and resolved at test time. The
model-execution wire face is the first surface certified this way
(`src/ToolUp.Platform.Tests/Conformance/ModelExecutionSpecConformance.fs`), against the
**model-execution wire specification** — `MODEL_EXECUTION_WIRE.md` and its conformance corpus,
published at <https://github.com/Fuaran-Core/fuaran-model-execution-spec> (Apache-2.0). <!-- OSS-BOUNDARY-EXEMPT: a citation of a public, Apache-2.0 specification by name and URL. --> This repo
conforms to that specification; it does not define the format, and where any in-repo description
of the wire disagrees with the specification, the specification wins.

Four rules apply to any family of this kind. They exist because a conformance suite is exactly the
kind of code that passes by doing nothing.

**1. An absent corpus fails loudly. It never skips.** A run that could not reach its corpus
certified nothing, and a skip makes that indistinguishable from a pass. The family resolves the
corpus from `TOOLUP_MODEL_EXECUTION_CORPUS`, or — with that unset — by a bounded search of the
enclosing directories for a corpus that identifies itself through its own manifest. Either way,
not finding one is a named test failure carrying what was tried.

**2. The corpus is pinned twice, because the two answer different questions.** The harness pins the
corpus *revision* (which CI checks out, so a corpus commit never breaks this build by surprise) and
a *digest over the corpus manifest* (which fails immediately when a local checkout has drifted from
that revision). The manifest is the corpus's own authoritative enumeration, so one digest covers
every vector in it. A specification's own version number cannot serve instead: a wire version
deliberately does not move for an additive change — a new fixture family, a new registry entry —
which is precisely the drift a pin has to catch.

**3. Bumping the pin is a deliberate commit with a diff review.** Read the corpus diff, then move
the revision in the workflow and both pin constants in the harness together, in one commit. Never
loosen a pin to make a red build green: a corpus that changed underneath a certification is telling
you something, and the something is usually that a shape moved.

**4. Both counters from the corpus's own guidance are asserted.** The number of vectors *executed*
must equal the number the manifest enumerates — not that they all passed, that the expected count
ran — and at least one mutation must be shown to make the harness go red. Both arms live in the
family's `non-vacuity` group.

A carry gap between the specification's shapes and this repo's wire records is normal and is
recorded rather than hidden: each family round-trips through the real record *plus* an explicit
residue naming exactly the members that record does not model, and the round-trip is byte-exact
overall, so anything left out of the residue fails. The `carry-gap-inventory` group additionally
pins each wire record's own field set by reflection — a record widened elsewhere fails there, and
its author decides deliberately whether the new field closes a gap.

**Running it.** Nothing extra: the family is part of `ToolUp.Platform.Tests`, so
`dotnet run --project Build.fsproj -- VerifyAll` covers it. CI fetches the corpus into the
`verify-all` job; a repository variable names the corpus repository and the job fails by name when
it is unset. The specification's home is public, so no read token is involved — the checkout uses
the job's default credentials, and fork PRs run the family like any other job.

## The template gates use a PRIVATE packages folder, never a guessed shared one (Phase 903)

`VerifyTemplates` and `VerifyPackagedModuleTemplate` both restore scratch-fed `dotnet new` scaffolds
against a throwaway SDK version (`0.0.0-templategate`), packed fresh into a scratch feed on every
run — see the "WHY A THROWAWAY VERSION" note beside `templateGateVersion` in `Build.fs`. A throwaway
version only works if NuGet's own package cache cannot serve a *previous* run's extracted copy of
it, which means the gate must evict that version's cache entries before it restores.

**The rule: evict a folder the gate itself owns privately, never the machine's shared
`globalPackagesFolder`.** Both targets used to wipe the throwaway version's entries out of a
*guessed* shared folder — `NUGET_PACKAGES` if set, else `~/.nuget/packages`. That guess is narrower
than NuGet's own resolution: a `globalPackagesFolder` entry in *any* `nuget.config` on NuGet's
search path overrides both the env var and the default, so a machine that sets it points the gate's
eviction at a folder NuGet is not actually reading from. The eviction step then no-ops silently,
restore resolves the *previous* run's package from wherever NuGet is really caching, and the gate
reports green having verified nothing about the tree it was told to check.

**The multi-worktree reason it is a private folder, not merely a better-resolved shared one.** The
narrower fix — ask NuGet directly (`dotnet nuget locals global-packages --list`), honouring
`NUGET_PACKAGES` first exactly as NuGet does — closes the wrong-folder class but still points every
run at ONE folder shared by every worktree on the machine. Two worktrees running a template gate at
once pack and evict the same throwaway version in the same shared cache, and each run's eviction can
race a sibling's restore — precisely the contention a several-worker campaign produces when multiple
sessions each hold their own `wt/<phase>` checkout of this repository on one machine. `TemplateGatePackages.privatePackagesFolder`
(`TemplateGatePackages.fs`, source-linked into `ToolUp.Platform.Build.Tests`) instead scopes the
folder under the calling repository's own `obj/` — already the home of `template-gate-feed` /
`packaged-module-template-feed`, and per-worktree by construction, since a worktree has its own
`obj/` — and the gate points `NUGET_PACKAGES` at it for every child process it spawns for the
remainder of the target. A private folder needs no resolution logic at all, because there is
nothing ambient left to get wrong, and it structurally cannot read — or evict — a sibling worktree's
cache. `TemplateGatePackages.evict` therefore wipes the whole private folder unconditionally rather
than targeting individual cache entries by id and version the way the retired shared-folder logic
did: nothing but this gate's own restores ever writes there, so a targeted eviction would only add
ceremony. Both targets print the folder they evicted and every package id (top-level subfolder name)
that was present before the wipe, so a reader of the gate's own output can tell a cold run from one
that actually needed it.
