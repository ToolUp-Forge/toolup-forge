# Testing conventions

Conventions for keeping the SDK's Expecto test suite reliable under default parallel-within-testList execution. The rules below exist because shared mutable state under parallel test execution is one of the few production-shape patterns that flakes deterministically when sibling tests collide on it.

## The "module-level mutable cache" pattern

A handful of SDK substrate files carry a `let mutable private <name>` (or `let mutable internal <name>`) at module scope — a process-local cache or registration list populated lazily on first use and read on every call. Two examples:

- [`PendingInviteStore.cache`](../../src/ToolUp.Platform.Server/Server/Teams/PendingInviteStore.fs) — 30-second TTL cache of the pending-invite blob, populated on first read and invalidated on every write.
- [`FileManagement.pendingPostSaveHooks`](../../src/ToolUp.Platform.Server/Server/Files/FileManagement.fs) — companion-registered list of post-save hooks; populated before `compose`, drained when `compose` runs.

In production this is the right shape: process-lifetime singletons at hot paths, no DI overhead per call. In tests the same shape is a flake source — sibling tests in the same `testList` mutate the shared cache concurrently and observe each other's writes.

**When module-level mutable is OK.**

- Process-lifetime singletons at hot paths where DI resolution per call is measurable.
- One-shot init guards (`registered = false`).
- Compose-time registration lists drained on application start.

**When it's not OK.**

- Anywhere DI resolution per call is cheap enough that the cache buys nothing.
- New code on the SDK boundary. Prefer a record-shaped runtime threaded through `compose` (see how `FileManagementRuntime` retired three of `FileManagement.fs`'s prior module mutables — only `pendingPostSaveHooks` survives, and that survives precisely because it's populated by callers BEFORE `compose` runs).

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

## Audit checklist

Every `let mutable` at module scope in `toolup-forge/src/` MUST fall into one of two classes:

**Class (a) — documented exception.** Acceptable when the mutable is:
- A warn-once advisory flag (`OidcAuthProvider.unmappedRolesWarned`) — written-once, read-only-by-observers, no per-test reset hazard because the test surface doesn't observe it.
- A compose-time-write-once scalar (`FileManagement.storeEvictionMinutes`) — set by `configureEvictionMinutes` on startup; tests that need a non-default value set it via the configure path.
- Fable-tier client-side state (`ToolUp.Platform.Client/`, `ToolUp.AI.Client/`, `AuthProviders/OidcClient/`, `Feliz.AgGrid.Enterprise/`) — the .NET test runner doesn't compile these modules' browser-only branches and doesn't poison the cache through the test entry points.

Each (a) site MUST carry a comment naming its classification: `// (a) — process-lifetime warn-once flag, no Expecto reset hazard` (or similar).

**Class (b) — exposes `__internal_resetForTests`.** Required when the mutable is:
- A read-on-every-call cache populated lazily (`PendingInviteStore.cache`).
- A compose-time registration list drained by `compose` (`FileManagement.pendingPostSaveHooks`) — even though the drain pattern resets it, tests that bypass `compose` need an explicit reset to start clean.

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

The per-pack `PASS` / `FAIL` summary block is unchanged, and deliberately so: CI parses it and fails the job unless at least `EXPECTED_PACKS` lines report `PASS` (a floor, so adding a pack needs no workflow edit). The up-front build sits **outside** that aggregate — a build failure means no pack can run, so it fails the target directly rather than producing a summary about a tree that does not compile.

**The Public-API approval gate's build precondition is now enforced, and it fails once.** `Phase 175 — Public-API approval baseline` renders each packable assembly's surface from its built DLL, so a pack run against a tree whose companion `bin/` directories are empty has nothing to compare. It used to answer that with one assertion failure per unbuilt assembly — 52 of them — which reads like a catastrophic surface break and has cost at least one session a wrong diagnosis. It now resolves each DLL **when the case runs** rather than snapshotting the answer at process start, and reports a missing build as a single `the solution is built` failure naming the count, a bounded sample, and the remedy; the per-assembly cases defer to it. The pack is still red — a precondition that let the run report green would be the vacuous shape the section below exists to end. **So: `dotnet build ToolUp.Forge.sln` before running the Platform pack on its own.** Running it through `VerifyAll` needs no such care, because the target now does that build itself.

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
published at <https://github.com/Fuaran-Core/fuaran-model-execution-spec> (Apache-2.0). This repo
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
