# Phase 803 — Fable reader parity: `DateOnly`, `TimeOnly`, and a Fable leg in the gate

**Applies to:** any deployment whose browser client receives a `DateOnly` or
`TimeOnly` (directly or inside a record) over `ToolUp.Remoting`, and any
repository that runs `VerifyAll` through `ToolUp.Platform.Build`.
**Breaking:** no. Additive in the reader, additive on the `ToolUp.Platform.Build`
surface (one new module, `VerifyLeg`).
**Action required:** none to keep working. One optional build-script line.

## What changes

The MsgPack reader the browser client runs (`Platform.Core`'s `Read.fs` under
`FABLE_COMPILER`) **refused `DateOnly` and `TimeOnly` outright** — its arm of
`interpretIntegerAsFrom` carried an empty `#if NET6_0_OR_GREATER` block where
the .NET arm decodes both. A server returning `{ Since: DateOnly }` therefore
worked from a .NET client and threw
`Cannot interpret integer 673049 as DateOnly` in the browser. Both types now
decode under Fable with the same day-number / tick encoding and the same domain
bounds the .NET arm applies (`DateOnly.MaxValue.DayNumber`,
`TimeOnly.MaxValue.Ticks`); a value past them is a named refusal on both hosts.
The Fable **writer** already carried both types; nothing changed there.

The `bin16` byte-array frame, recorded on 2026-09-13 as a second Fable
divergence, was re-measured and found never to have been a reader fault: the
harness was reading the fixture through three different pooled `Buffer`s (fixed
by Phase 69c.D two days later) and the claim was not re-run. The `bin32` frame
and Phase 786's length-vs-remaining guard on both wide frames are now asserted
under Fable as well.

## What a consumer sees change

| Before | After |
|---|---|
| A `DateOnly` / `TimeOnly` in a response threw in the browser client | decodes, to the same value the .NET client sees |
| A record holding either type was unusable from the browser | usable; no server-side workaround (a `string` shadow field) needed |
| `dotnet run -- VerifyAll` / `verify.ps1` never transpiled the client tier | runs the Fable-tier harness as its last leg, `PASS — Fable` in the summary |

**One host limit, stated rather than hidden:** Fable's `TimeOnly` is a
millisecond count, so ticks below one millisecond are truncated on decode in
the browser. The .NET side keeps full tick precision. A contract that needs
sub-millisecond time on the client should carry ticks as an `int64`.

## The build-script line (optional — `ToolUp.Platform.Build` consumers)

`VerifyAll` now composes extra legs registered through `VerifyLeg.register`,
after its Expecto packs. A repository with a non-Expecto harness — a Fable
`node:test` pack, a browser smoke — adds it to the canonical gate with one
line in its `Build.fs`, before or after `registerTargets`:

```fsharp skip=fragment
VerifyLeg.register "Fable" (fun () ->
    // run your harness; return its exit code (0 = pass)
    runMyFableHarness ())
```

The leg appears in the `VerifyAll summary:` block as one more `PASS` / `FAIL`
line. **If your CI counts those lines against a floor** (forge's
`EXPECTED_PACKS` does), bump the floor in the same commit. Registering the same
name twice is refused. `BuildConfig` is unchanged — no record field was added,
so no consumer's `Build.fs` moves.

## Adoption

Flip your `sdk-adoption.json` record for this refactor:

```json
{ "refactor": "803", "status": "adopted", "sha": "<your commit>" }
```

Adopting is just taking the SDK version. Use `"status": "n-a"` with a reason if
no client of yours receives either type and you do not run `VerifyAll`.

## Rollback

Pin the previous `ToolUp.Platform.Core` / `ToolUp.Platform.Build` versions. No
wire bytes changed, so a server on this version and a client on the previous
one interoperate exactly as before — the previous client simply refuses the
two types it always refused.
