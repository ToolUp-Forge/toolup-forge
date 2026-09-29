# Phase 900 — effects start in attach order, and a terminating subscription ends its diff

**What changes for you: probably nothing, and two things to know.** No entry point moved. One
vendored-internal function gained a parameter. What changed is the *order* in which the runtime
starts lifetime-aware effects and *what it does* when a subscription's start function terminates the
program mid-diff. Read the two "if you…" paragraphs; if neither applies, upgrade and move on.

## What it is

1. **Effects start in the order they were attached.** `Program.withEffect` prepends to the
   program's effect list, exactly as `withDispatcherHandle` and `withEffectControllerHandle` prepend
   their sinks. The boot reversed the sinks on the way out and did not reverse the effects, so three
   effects attached `a`, `b`, `c` started `c`, `b`, `a` — the reverse of what `Program.effectIds`
   reported and of what a consumer reading its own composition expects. The boot now iterates
   `List.rev program.effects`, and a test pins the contract beside the accessor that already stated it
   (`effects start in the order they were attached, the order effectIds reports - Phase 900, run`).
2. **A subscription start that terminates the program ends its diff.** When one message's model asks
   for several subscriptions and one of them calls `IDispatcher.Terminate` from its start function,
   the runtime used to start the rest of the diff anyway, assign all of them to a set the teardown had
   already stopped (so nothing ever disposed them), and then run the message's command.
   `Sub.Internal.Fx.change` now reads the loop's `terminated` flag before each start; once it is set,
   nothing further starts, what the call had started is stopped, the next active set is empty, and the
   message arm skips the command. The same gate covers the boot's subscription start. The behaviour
   is proved on the loop model (`proofs/ElmishLoop.fst`, `diff_terminating_starts_nothing_after`) and
   measured on both hosts (`proofs/README.md`, the dispatch-loop ladder).

## If you attach more than one effect and one depends on another having started

Attach them in the order you need them started. Before this phase the last-attached effect started
first; if you relied on that — an effect attached second that assumed an effect attached first was
not yet running, or vice versa — reverse your `withEffect` calls. No shipped module in this
repository attaches order-dependent effects; the sample and template compositions attach at most one.

## If you call `Sub.Internal.Fx.change` yourself

It takes a fourth argument, `terminated: unit -> bool`, before the diff tuple:

```fsharp skip=fragment
// before
Sub.Internal.Fx.change onError dispatch (dupes, toStop, toKeep, toStart)
// after — a caller outside the loop, with no flag to read, passes a constant
Sub.Internal.Fx.change onError dispatch (fun () -> false) (dupes, toStop, toKeep, toStart)
```

With the flag clear the function is byte-for-byte what it was. The module is `Internal` and kept
public only for parity with upstream Elmish (see `256-public-surface-minimization.md`); the runtime
is its one caller in this repository. The `ToolUp.Platform.Client` API baseline moves by that one
signature.

## A program whose subscriptions never terminate it

Behaves byte-identically. The flag is read and never set; every start is made, every handle is
kept, the command runs. The differential campaign that holds the loop to its model on both hosts
(`src/ToolUp.Platform.Tests/Client/ElmishLoopDifferential.fs`) draws about a third of its scripts
with subscriptions that never terminate, and those scripts' verdicts did not move.

## Verification

```powershell
dotnet build src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter "ToolUp.Platform.Tests.Phase 789 - the proved dispatch loop as oracle"
pwsh ./proofs/check.ps1 -Runs 3
dotnet run --project Build.fsproj -- VerifyFable
```

## Rollback

Revert the phase's commits on `Sub.fs` and `Program.fs`; the proof leg then fails its byte-diff
against `proofs/oracle/ElmishLoop.fs` and the three `Phase 900, run` cases go red, which is the
state the phase started from.
