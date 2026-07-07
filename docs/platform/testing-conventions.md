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

```fsharp
testCaseAsync "exercises sweepExpired against a real blob"
<| async {
    do! CacheReset.invalidateAll ()
    let storage = InMemoryBlobStorage() :> IBlobStorage
    // … rest of the test
}
```

The reset function lives in the same source file as the mutable, gated by `internal` accessibility — `ToolUp.Platform.Server` already declares `<InternalsVisibleTo Include="ToolUp.Platform.Tests" />`, so the helper is invisible to external consumers.

```fsharp
// In PendingInviteStore.fs
let mutable private cache: CacheEntry option = None

/// Test-only: drop the in-memory cache so a subsequent test starts
/// from a clean slate. Registered via `CacheReset.invalidateAll`.
/// Never called from production code paths.
let internal __internal_resetForTests () = cache <- None
```

### Strategy B — `testSequencedGroup`

When a whole `testList` exercises the same shared cache and per-test isolation is cheaper to express by serialising, wrap the list:

```fsharp
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
- Fable-tier client-side state (`ToolUp.Platform.Client/`, `ToolUp.AI.Client/`, `AuthProviders/OidcClient/`, `AgGridEnterprise/`) — the .NET test runner doesn't compile these modules' browser-only branches and doesn't poison the cache through the test entry points.

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

Filter to server-tier paths under `src/ToolUp.Platform.Server/`, `src/AuthProviders/Oidc/`, `src/AuthProviders/EntraExternalId/` — client-tier paths fall under the Fable-tier (a)-class by construction.

## Testing client-tier MVU update functions

Client-tier modules (`ToolUp.Platform.Client`, `ToolUp.AI.Client`, etc.) cannot be exercised by the `.NET` Expecto runners. The blocker is module-level construction of ToolUp.Remoting proxies:

```fsharp
// PlatformAIKeysAdminUI.fs — typical AI.Client module shape
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

   ```fsharp
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
