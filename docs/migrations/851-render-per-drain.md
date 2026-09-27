# Phase 851 — the loop renders once per drain, dispatch is stable, and no async command waits on a timer

**What changes for you: probably nothing, and three things to know.** No public API moved. Every
`Program.withReact*` entry point, every `Cmd.OfAsync` / `Cmd.OfRemoting` combinator and every
`IDispatcher` member keeps its name and type. What changed is *when* the runtime does three things
it always did, and each is a behaviour a consumer could have — accidentally — depended on. Read the
three "if you…" paragraphs; if none applies, upgrade and move on.

---

## What it is

Three costs sat on every interaction in every client, all in the runtime rather than in any view
(measured by [Phase 849](849-browser-runtime-benchmark.md), whose numbers are the *before* below).

1. **The view was built once per message, not once per interaction.** `Program.runWithDispatch`
   called the render hook after every `update`, so a drain of N messages — one dispatched message
   whose command fans out into N−1 `Cmd.ofMsg` — built the whole F# view N times, of which the user
   saw one. Now the hook runs **once, when the ring is empty, with the model the drain ended on**,
   and the React adapter constructs the view **once per task** (two drains in one browser task paint
   once, via the microtask queue).
2. **Every async command waited on a timer before it began.** Under Fable, `Cmd.OfAsync*`,
   `Cmd.OfRemoting*` and `IDispatcher.DispatchAsync` started through `setTimeout 1` — a macrotask
   every remote call paid before its request went out. Now they start with `Async.StartImmediate`.
3. **The module view's `dispatch` was a fresh closure on every render.** The shell built
   `ModuleMsg >> dispatch` inline in `view`, so the one prop every module view receives changed
   identity every time, and no memo boundary around a module view could hold. Now it is **the same
   function for the life of the loop**.

## Before and after

Same harness (`ClientBench`, `src/ToolUp.AI.Client.Tests`), same seed (849001), same machine class
(node v25.9.0, win32/x64, React production build, other builds running concurrently in both runs).
Before is 849.D's first-run record; after is 2026-09-27 on this phase's tree.

| Lever | Before (849) | After (851) | What it says |
|---|---|---|---|
| render-hook calls per drain (1 dispatched + 16 `Cmd.ofMsg`) | **17** | **1** | the acceptance: a drain of N messages paints once |
| async command hop, `update` issues `Cmd.OfAsync` → body starts | min **1.12 ms**, median **14.6 ms**, max 20.7 ms (n=20) | min **0.003 ms**, median **0.008 ms**, max 0.382 ms (n=20) | the timer hop is gone; what remains is the call itself |
| view per dispatch (200-row table, 200 external dispatches) | 906 us, 200 hook calls per round | 786 us, 200 hook calls per round | unchanged by design — each external dispatch is its own drain and paints once; the difference is run-to-run noise on a loaded machine |
| boot, `samples/MinimalClient` import → first render (min, n=6) | 1265 ms | 1023 ms | not claimed — the boot path was not this phase's target; noise band |

**Falsifiers, so these are measurements.** The hook count is a counter inside the render hook the
harness installs (`Program.withSetState`), incremented per call; a loop that still painted per
message would read 17, and the pre-851 loop is committed as a go-red skeleton in
`ElmishLoopProofOracleTests` and asserted *caught* on the render count (see below). The async hop
is `now()` at `update` minus `now()` at the first line of the async body; a start that still went
through a timer reads its granularity (~1 ms floor, ~15 ms median on Windows), and the 849 run did.
The per-dispatch view cost is asserted by the harness to have made exactly one hook call per external
dispatch in every round (`viewObserved`); the budget `client.viewPerDispatchUs` is unchanged and the
gate `ToolUp.Platform.Build.Tests` is green on this tree.

The two things the harness does not measure and this phase pins elsewhere: the adapter's
once-per-task coalescing is `RenderCoalescingTests` (Fable pack, on Node's real microtask queue — two
requests in one task run the render once, a request after that runs it again, a request made from
inside the render is not swallowed); the module dispatch identity is `ModuleDispatchStabilityTests`
(Fable pack, `Object.is` over the transpiled code — the comparison a memo boundary makes).

## The loop model, re-proved

The render hook moved inside the step machine, so [`proofs/ElmishLoop.fst`](../../proofs/ElmishLoop.fst)
was amended and re-proved on the pinned prover rather than assumed to survive — see
[`proofs/README.md`](../../proofs/README.md#the-claims-ladder--the-dispatch-loop-phase-789). In one
paragraph: the machine gains a `dirty` cell (set by every `update`, cleared by the paint), a `paint`
transition, a second oracle `render : model -> ev list` (what `setState` re-dispatches
synchronously) and two observables (`painted`, `renders`); the drain exits only when the ring is
empty *and* nothing is dirty, and a paint is followed by one more pop because the hook may have
dispatched. The six Phase 789 theorems hold unchanged in statement (`exactly_once`, `in_order`,
`reentrant_no_loss`, `terminated_absorbing`, `boot_drain_equiv`, `active_iff_not_terminated`), and
two are added: **`painted_is_model`** — whenever the loop is idle, the model on screen *is* the model
(unconditional); **`render_once_per_drain`** — a dispatch from idle paints exactly once when it
finishes and not at all when it terminates, *conditional* on the render hook dispatching nothing
synchronously (which the React adapter's does; a consumer hook that dispatches re-opens the drain
and paints again — still exactly-once, still the final model, just more than one paint).

The differential host scripts the case the move creates — a `setState` that dispatches, observed
*after* the drain — and compares render count and painted model beside trace, log, model and
`IsActive`; three go-red skeletons (the latch released early, the pre-851 paint-per-message loop, a
paint with no pop after it) are asserted caught. Per [Phase 850](../../proofs/README.md#the-verified-implementation-spike-phase-850)'s
verdict, the shipped loop is hand-written F# held to the model by that differential; the extraction
is the oracle, never production.

## If you…

- **…relied on `setState` (or your `view`) running after every `update`.** It runs once per drain
  now. A `Program.withSetState` you wrote yourself is handed the *final* model of each drain, and
  the intermediate models are never handed to it. If your hook dispatched, its message now arrives
  after the drain, in a fresh round of the same drain, rather than mid-drain; the loop still
  processes it exactly once and in order. `Program.withTrace` is unaffected — it wraps `update`
  and still sees every message.
- **…read the DOM synchronously right after a dispatch.** The React adapter now constructs the view
  on the microtask queue, so a `dispatch` followed by a synchronous DOM read in the same task sees
  the previous view. Await a microtask (`Promise.resolve().then`) or, in a test, a `setTimeout 0`.
  This was already true of React 18's commit; it is now also true of the F# view construction.
- **…used the async-command timer hop as a scheduling boundary.** A `Cmd.OfAsync.perform` over an
  already-resolved value, or a remoting call answered from Phase 854's in-flight table, now
  completes *inside the drain that issued it*: its message is queued behind whatever is waiting and
  processed in the same drain (`reentrant_no_loss` is the theorem). A test that waited "one timer
  hop" for such a message can assert synchronously; a test that waited longer still passes.
- **…captured the module `dispatch` and compared it by reference.** It is now stable across renders
  for one loop and changes only when the program is re-run (HMR). Nothing should have depended on it
  changing.

## Amendment — the boot sets its latch first, and the reporter cannot wedge the loop

Two defects in the loop this phase re-proved, both older than the phase, fixed on top of it.

**An effect or a sink that dispatches at start is now queued, not drained.** `runWithDispatch` used
to call the dispatcher-handle sinks, the effect-controller sinks and every effect's start function
*before* it set the re-entrancy latch. One that dispatched synchronously therefore ran a whole drain
— `update`, the subscription diff, the command, a paint — ahead of the boot paint. Three things
followed: the render hook's first model was not the one `init` returned (a hydration mismatch under
`withReactHydrate`); the boot painted twice; and the boot's subscription diff, computed from the
init model before anything ran, stopped the subscriptions that drain had started. The latch is now
set before anything the program supplied is called, so such a message is queued and handed to
`update` by the boot drain, after the boot paint, ahead of `init`'s command's messages and in the
order it was raised.

*Who has to do anything:* nobody whose effects only register callbacks, which is every effect the
SDK ships. An effect of your own that dispatches from its start function, and code that read state
it expected that message to have produced *before the first render*, now sees it one drain later —
in the second paint rather than the first.

**An error reporter that throws no longer wedges the loop.** The loop reports a failing `update`,
command, render hook, sink, effect or subscription through the program's reporter and carries on. If
the reporter itself raised, the exception escaped the drain with the latch still set, and from then
on every dispatch queued its message and processed nothing. Every call to the reporter now goes
through a guard that catches what it raises and writes both failures to the console. A reporter
that throws is still a defect in the reporter; it is no longer fatal to the program.

`proofs/ElmishLoop.fst` gains the events raised before the boot paint as a parameter and one
theorem, `boot_paints_init_model`; the other eight were re-proved over the restated boot.

## What did NOT change

- `withReactSynchronous` keeps its name and is still the default every client calls. Its meaning
  moved from "not `requestAnimationFrame`" to "one construction per task"; `withReactBatched` (rAF,
  one per frame) stays as the explicit opt-in for animation-heavy apps; `withReactHydrate` mounts by
  hydrating the *init* model — the boot paint still runs before `init`'s command, so a hydrating
  renderer is handed the model the server rendered — and then behaves as the default.
- Subscriptions are diffed per message, as before; commands run per message, as before. Only the
  paint moved.
- On .NET (tests, SSR prerender) the render hook is still synchronous — once per drain, at the end
  of the drain; there is no microtask queue there and none is simulated.
- A terminating drain paints nothing: a torn-down program's last view stays as it was. Until 851 the
  messages before the terminating one were each painted. Nothing observable depends on a dead
  program's final paint (HMR replaces the root; `beforeunload` navigates away), and
  `terminated_absorbing` now says so of `painted` and `renders` too.

## Rollback

Revert the phase's commits on `src/ToolUp.Platform.Client/Client/Elmish/{Program,Prelude,React,Dispatcher}.fs`
and `src/ToolUp.Platform.Client/Client/SDK.Client.fs`, and with them `proofs/ElmishLoop.fst`,
`proofs/oracle/ElmishLoop.fs` and `src/ToolUp.Platform.Tests/InProcess/ElmishLoopProofOracleTests.fs`
— the model and the code move together, and `proofs/check.ps1` refuses a tree where they do not.
