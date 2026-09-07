# Browser smoke harness

The repo's real-browser verification tier. Two scenarios: two contexts co-editing one
document (Phase 535), and an edit made with the link down that queues and then applies
(Phases 24 / 760).

```powershell
dotnet run --project Build.fsproj -- VerifyBrowserSmoke
```

That one command owns every step — `npm ci`, the Fable compile, the Vite bundle, the
harness build, Playwright's Chromium install, and the run — so CI and a developer cannot
drift. It takes about **two minutes**, of which the scenarios themselves are ~25 seconds
and the Fable compile is most of the rest.

## Scope — smoke, not an e2e suite

**A scenario belongs here only if it cannot be asserted anywhere else.** In practice that
means it needs one of exactly three things:

- **two isolated origins** — separate storage and separate network state, which is what a
  Playwright browser *context* is;
- **a browser storage engine** — IndexedDB, which the offline queue is built on;
- **the browser's own connectivity signal** — `navigator.onLine` and the `online` event,
  which the sync coordinator wakes on.

Everything else is cheaper and better tested elsewhere, and putting it here makes this
gate slower without making it stronger:

| What you want to assert | Where it goes |
|---|---|
| a pure function, a reducer, a validator | an Expecto pack under `src/*.Tests` (`VerifyAll`) |
| a client-tier MVU `update`, a rendered view, an a11y floor | the Fable-tier `node:test` harness (`VerifyFable`) |
| that the client tier still transpiles | `samples/MinimalClient` + `VerifyFable` |
| a seam's contract across implementations | the contract packs in `ToolUp.Platform.Tests/Contracts` |

Two scenarios is not a target to grow past. If the count starts climbing, the question to
ask is which of them could have been one of the rows above.

## How it fits together

```
tests/BrowserSmoke/
├── fixture/                     the page the browser loads (a Fable app, bundled by Vite)
│   ├── Fixture.fs               the deployment-owned half: the HTTP API, the page shell, the fault switches
│   └── package.json             react/ag-* mirror samples/MinimalClient; `yjs` is the CRDT library
└── ToolUp.BrowserSmoke.Tests/   the harness (an Expecto console runner)
    ├── FixtureHost.fs           HttpListener: the bundle, the co-edit API over a real ICrdtDocumentStore, and the SSE relay endpoint
    ├── Harness.fs               browser lifetime, the waits, the retry, the failure artefacts
    ├── CoEditSmokeTests.fs      scenario 1
    └── OfflineSmokeTests.fs     scenario 2
```

The code **under test** is the shipped source: `ToolUp.Platform.CrdtSyncClient`,
`ToolUp.Platform.NotificationClient`, `NotifyingCrdtDocumentStore`,
`ToolUp.Offline.Client.{OfflineQueue,SyncCoordinator}`, and — compiled straight from
`samples/MinimalClient` — the Phase 535 / 760 reference wiring a consumer is told to copy.
The fixture adds only what a deployment owns: the module-owned route over the CRDT store
(Phase 535 is seam-first and mounts none), and the notification ENDPOINT the relay's events
reach a page over.

**The client half of the fan-out is no longer here (Phase 764).** Until then `Fixture.fs`
polled `session.Resync` on a timer, which meant this gate certified a wiring the sample did
not ship: `CrdtCoEditSample` caught up once at join and then went quiet, and only the
fixture stayed live. The subscription now lives in the sample
(`CrdtCoEditSample.startLive`, over the reserved `_platform.crdt` topic and the tab's one
`EventSource`), and the page merely starts it — so a regression in the wiring a consumer
copies is a regression this gate sees.

One consequence is worth knowing before adding a scenario: with the poll gone, **a page
declares itself `data-ready` only once its notification stream is open**, proven by a hello
envelope `FixtureHost` writes to each stream as it subscribes it. A page that mounted while
its `EventSource` was still handshaking can miss the first edit a co-editor makes and, with
nothing left polling, never ask for it again — so the readiness gate is load-bearing, not
tidiness.

Neither project is in `ToolUp.Forge.sln` or in `BuildConfig.TestPacks`. That is deliberate:
`VerifyAll`'s wall-clock is a shared budget and this launches browsers, so the default gate
is untouched **by construction** rather than by convention.

## Adding a scenario

1. **Check it against the scope table above.** If it does not need a browser, it does not
   go here.
2. Add the page behaviour it needs to `fixture/Fixture.fs`, selected by query string, and
   expose what the scenario will assert on as a `data-` attribute or a textarea value —
   never as rendered prose, which is a translation away from being a locale bug.
3. Write the scenario with `Harness.scenario`, using `openPage`, `wait` and `stayFalse`.
   **Wait on facts, never on durations**: `wait` polls a predicate and names what it was
   waiting for when it gives up, which is the whole diagnosis in a CI log.
4. **Give it a go-red.** A scenario that has never been observed failing is a scenario that
   might be asserting nothing. Both existing scenarios ship with theirs as a permanent
   case — see below.
5. **Bump `browserScenarioFloor` in `Build.fs`** in the same commit. The target fails below
   it, which is what stops a runner with no browser reporting green while every scenario
   silently skips.
6. Rebuild the bundle: the target does it for you, but while iterating,
   `dotnet fable -o output --noCache` then `npm run build` inside `fixture/`.

## The fault switches

Each scenario's go-red is a permanent case driven by a query-string switch, not a one-off
hand edit someone once observed:

| Switch | Cuts | The scenario it makes red |
|---|---|---|
| `?fault=relay` | the server's fan-out — the page asks the host not to relay its appends, and the host writes them through the undecorated store | co-edit: the second context never sees the first's edit — while the server still holds it, so the failure is provably the relay and not the transport |
| `?fault=queue` | the offline queue's `Enqueue` | offline: nothing is held while disconnected, so nothing replays on reconnect |

Both cut a seam the **deployment** supplies. Nothing inside the SDK is mocked or modified
to produce them, which is what makes the red meaningful.

**Cut the smallest thing that names the fault.** `?fault=relay` has been wrong twice, both
times by cutting too much. It first skipped registering the fixture's poll timer
altogether, and on a page with no timer registered nothing else the fixture started
asynchronously ran either — including the pump's own join-time catch-up; the "broken" page
was not a page with a cut relay, it was a page that did nothing, and a scenario cannot
prove the relay is what failed if everything failed. It then kept the timer and skipped
only its one `Resync` call — correct while the poll was the relay, and no longer available
once the relay moved into the sample, because cutting `subscribeRelay` would break the SDK
code under test rather than a seam around it.

So the cut moved to the other end of the wire, where the fan-out now is: the page passes
`relay: false` on its appends and `FixtureHost` writes them through the store with no relay
decorator. The append is still durable and still retrievable by diff — the publish path is
untouched — and no event ever announces it. Exactly one thing changes, and it is the thing
the switch is named for.

## Flake policy

**One retry, then red.** A browser scenario has real nondeterminism a unit test does not,
and a harness with no retry produces a gate people learn to re-run rather than read. A
harness with unbounded retries turns a regression into a slow scenario. The retry is
**reported** — a scenario that passed on the second attempt says so — so a scenario that
starts needing it is visible long before it starts failing.

**Failure artefacts** land under `artifacts/` beside the test binary, on the failing
attempt only: a Playwright trace per browser context (DOM snapshots, network log, console)
and a full-page screenshot per page, named for the scenario and the attempt. `npx playwright
show-trace <file>` opens one.

## Chromium

The scenarios are gated on a locally-installed Playwright Chromium and report **Pending,
not Failed**, without one — the convention Phase 126 established for the HTML→PDF pack, and
the right behaviour for a fresh checkout. It is the wrong behaviour for CI, so
`VerifyBrowserSmoke` installs the browser and then **fails below the scenario floor**: a
runner with no browser produces a named failure, never a green job full of skips.

To install by hand:

```powershell
pwsh tests/BrowserSmoke/ToolUp.BrowserSmoke.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
```

## Ports

The host binds an **OS-assigned free loopback port**, so the harness claims nothing in the
workspace port bands: it is a test process, not an app, and a smoke run must not collide
with a second run or with a developer's own dev server.
