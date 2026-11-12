# ToolUp.Elmish.Kernel

The proved core of the ToolUp.Elmish MVU runtime, in its own package:

| File | What it is | Its model |
|---|---|---|
| `Ring.fs` | `RingBuffer<'item>` — the queue the dispatch loop defers re-entrant dispatches into; grows on overflow | `proofs/ElmishRing.fst` — a FIFO queue through every grow |
| `Sub.fs` | `Subscriptions` — the subscription diff (`diff`, `NewSubs.calculate`) and the step that applies it (`Fx.change`) | `proofs/ElmishSub.fst` — starts, stops and keeps exactly what the diff says |
| `Loop.fs` | `Loop<'msg, 'model>` — the dispatch loop's step machine: the ring, the re-entrancy latch, the termination flag, the drain, one paint per drain, the one teardown and the boot | `proofs/ElmishLoop.fst` — no message lost under re-entrancy, the painted model is the model when idle, termination is absorbing |

Each model abstracts every impure callee as an oracle, so nothing here is Fable, React or SDK: the package
depends on FSharp.Core alone. The host supplies the callees through `LoopHost<'msg, 'model>` — the update with
the effects its reply asks for, the render hook, the teardown's callees and the reporter. The differential
hosts in the repository's test packs run the extracted models beside this code on .NET and under Fable.

Most applications never reference this package directly: `ToolUp.Platform.Client` depends on it, and its
`ToolUp.Elmish` runtime (`Program`, `Cmd`, `Sub`, the React and HMR adapters) wraps it, with every public
name under `ToolUp.Elmish` unchanged. The source ships under `fable/` in the package for Fable consumers.

`ToolUp.Elmish.Kernel` is a working name; the published name is a later naming decision.

Licensed under Apache-2.0. Derived from Fable.Elmish (Apache-2.0); see the repository's `NOTICE.md`.
