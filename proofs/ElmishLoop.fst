(*
   Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*)

/// Phase 789 — the Elmish dispatch loop, modelled in F* as a step
/// machine and proved to process every message exactly once, in
/// dispatch order, with termination absorbing. Phase 851 moved the
/// render hook to the END of the drain and re-proved the machine. The
/// boot was then restated so the latch is set BEFORE the sinks and the
/// effects' start functions run, which is what `boot_paints_init_model`
/// is about.
///
/// # What this module is
///
/// A hand-written model of `Program.runWithDispatch`
/// (`src/ToolUp.Platform.Client/Client/Elmish/Program.fs`), clause for
/// clause: `dispatch`, `processMsgs`, the terminate callback, and the
/// boot drain after `init`. The loop is a scheduling skeleton over five
/// mutable cells — the ring, the `reentered` latch, the `terminated`
/// latch, the model and (since Phase 851) the `dirty` flag — and its
/// transitions depend on the impure callees (`update`, `setState`,
/// `Subs.Fx.change`, `Cmd.exec`) only through WHICH MESSAGES THEY
/// SYNCHRONOUSLY RE-DISPATCH and whether they call `Terminate`. The
/// per-message callees are therefore abstracted as ONE oracle,
/// `update : msg -> model -> pair model (list ev)`: the new model and, in
/// order, the events `update`, `subscribe`, `Subs.Fx.change` and
/// `Cmd.exec` raise before control returns to the loop. The render hook
/// is a SECOND oracle, `render : model -> list ev` — the events
/// `setState` raises — because since Phase 851 it runs at a different
/// point: once, when the ring is empty, with the model the drain ended
/// on, and not after every `update`. Under those abstractions the loop
/// is a total, deterministic step function over the cells, and this
/// module is that function together with the laws the runtime relies on
/// and nothing tests.
///
/// Phase 788 proved the ring a FIFO queue (`ElmishRing.fst`); this
/// module IMPORTS it — `ring`, `push`, `pop`, `wf`, `unread`,
/// `push_spec`, `pop_spec`, `create_wf` — and adds nothing about the
/// ring. The proof here is about the two latches and the paint.
///
/// Every definition names its F# counterpart in the comment above it.
/// The differential host (`ElmishLoopProofOracleTests.fs` on .NET) runs
/// the EXTRACTION of this module beside the production loop with a
/// scripted `update` and a scripted `render` that re-dispatch chosen
/// messages at chosen points — from an effect's start function before
/// the boot paint, from `update`'s command, from the boot drain, from
/// the post-drain `setState`, after `Terminate` — and
/// requires the two to hand `update` the same messages in the same
/// order, to end on the same model, to have painted the same model, and
/// to have painted the same number of times. That host is the only thing
/// that says this model is about the code that ships.
///
/// # The representation
///
/// The state is the five cells plus the dispatcher's flag and six
/// observables the differential compares: `trace`, the messages `update`
/// has been handed, in order; `log`, the messages `dispatch` ACCEPTED
/// (pushed onto the ring), in order — external and reentrant alike;
/// `painted`, the model most recently handed to the render hook;
/// `renders`, how many times it was handed one; and since Phase 871
/// `started`, how many of the boot's gated starts were made, and `held`,
/// how many handles the loop holds for them. `active` is
/// `DispatcherCore.active`, carried so the two-flag encoding is a
/// theorem over the model rather than a comment.
///
/// `processMsgs` is a `while` loop whose exit depends on the oracles
/// eventually re-dispatching nothing — which nothing guarantees, and
/// which production does not guarantee either (an `update` that always
/// re-dispatches never returns; so does a `setState` that does). The
/// model bounds it with `fuel`, one unit per processed message and one
/// per paint, and REPORTS exhaustion rather than hiding it: the latch is
/// left set when the drain did not finish, so a stalled machine looks
/// exactly like production mid-drain, and `run` stops feeding it
/// external events, because a single-threaded host cannot deliver any
/// while a synchronous drain has not returned. Every theorem below is
/// partial correctness: what holds of every state the machine reaches,
/// whether or not the drain finished. Liveness of the drain is not
/// claimed.
///
/// # The theorems
///
/// `exactly_once`: in every idle, non-terminated state the machine can
/// reach, `log == trace` — every accepted message was handed to `update`
/// once and only once, and in the order it was accepted. `in_order`:
/// in EVERY reachable state, `trace` is a prefix of `log` — termination
/// can truncate, never permute. `reentrant_no_loss`: a `dispatch` made
/// while the latch is set queues its message at the back and processes
/// nothing — and paints nothing — so the message is neither lost nor
/// moved ahead of what was already waiting. `terminated_absorbing`: once
/// `terminated`, no operation changes `trace`, `log`, the model or what
/// was painted, and nothing clears the flag. `boot_drain_equiv`: the
/// boot drain, which duplicates the steady-state critical section by
/// hand, IS that critical section over the initial paint and the events
/// `init`'s effects raise — and for a single message is the initial
/// paint followed by `dispatch`. `active_iff_not_terminated`: in every
/// reachable state `active = not terminated`; `fallback_breaks_encoding`
/// computes the one production arm that drives them apart.
///
/// Phase 851 adds two about the paint. `painted_is_model`: in every
/// idle, non-terminated state a program reaches, the model on screen IS
/// the model — nothing is left unpainted when a drain returns.
/// `render_once_per_drain`: a `dispatch` from an idle state paints
/// exactly once when it finishes, and not at all when it terminates —
/// CONDITIONAL on the render oracle being quiet (a `setState` that
/// dispatches nothing synchronously, which is what the React adapter
/// is; a `setState` that dispatches re-opens the drain and paints again,
/// and the differential scripts exactly that case).
///
/// And one about the boot. `boot_paints_init_model`: whatever the
/// dispatcher-handle sinks, the effect-controller sinks and the effects'
/// start functions dispatch, the boot paint hands the render hook the
/// model `init` returned, and nothing was handed to `update` before it.
///
/// Phase 871 makes termination TOTAL and adds what it releases and what
/// it starts. Until 871 the model proved a terminated machine PROCESSES
/// nothing, with the teardown's calls abstracted, and said nothing about
/// what the boot STARTS once a sink or an effect has called `Terminate`
/// — which production went on doing: the remaining effects, the init
/// model's subscriptions and `init`'s command all started after the
/// teardown that should have disposed them had already run. The machine
/// now carries two more observables: `started`, the boot's gated starts
/// that were made (an effect's start function, the subscription start,
/// `init`'s command), and `held`, the handles the loop holds for them.
/// Teardown releases every handle (`terminate` sets `held` to zero), and
/// the boot's starts are `gated` — made only while the program runs, and
/// a start that terminated the program itself holds nothing afterwards.
/// `terminated_holds_nothing`: in every state a program reaches, a
/// terminated program holds nothing. `terminated_starts_nothing`: a
/// sink that calls `Terminate` means the boot makes no start at all;
/// `gated_terminated` is the per-start guard it rests on, and
/// `start_terminating_releases` the start that terminates by itself.
///
/// Phase 900 splits the step at the diff. Until 900 the `update` oracle
/// was one reply — the new model and every event `update`, `subscribe`,
/// `Subs.Fx.change` and `Cmd.exec` raised, flattened — so a `Terminate`
/// raised by one subscription's start function inside a MESSAGE's diff
/// was, to the model, an event among events: the machine terminated,
/// every later event was absorbed, and nothing said whether production
/// went on STARTING. It did: `Subs.Fx.change` started the rest of the
/// diff, the loop assigned the lot to the set the teardown had already
/// stopped, and the message's command ran after. The oracle now returns
/// a `reply` — the model, the diff's STARTS (one `start` per subscription
/// `Subs.Fx.change` starts, in order) and the command — and `step`
/// applies them as the boot does: `start_all` makes each start only
/// while the program runs, holds what it returned only if it is still
/// running when it returns, and `start_opt` runs the command only if it
/// is still running after the diff. `diff_terminating_starts_nothing_after`
/// is the theorem: a diff whose i-th start terminates the program makes
/// exactly i starts, no command, and holds nothing. `started` and `held`
/// now count the message arm's starts too, so `terminated_holds_nothing`
/// covers them, and the go-red (the step before 900 — `apply_evs` over the
/// flattened events) fails `diff_terminating_starts_nothing_after` on
/// the start count.
///
/// # What is abstracted, and stated as such
///
/// Every parameter of the model is an assumption. `update` is the
/// composite of `update`, `subscribe`, `Subs.Fx.change` and `Cmd.exec`:
/// a pure function of the message and the model to a `reply` — the new
/// model, the starts the diff makes (each the events its start function
/// raises synchronously, in order, and whether it returned a handle) and
/// the events the command raises. An exception from `update` or
/// `subscribe` is an oracle reply whose model is the old one (production
/// leaves `state` unassigned) with no starts and no command; a start that
/// throws is reported and returns nothing (`holds = false`); a command
/// that throws is a command carrying whatever it dispatched before the
/// throw. `render` is `setState`: a pure function of the model
/// handed to it, to the events it raises synchronously; the React
/// adapter raises none. `to_terminate` is `Program.withTermination`'s
/// predicate, a pure function of the message. `fuel` is a bound the
/// model imposes on itself, never a claim about production. `capacity`
/// is any integer — `create_wf` says the ring is well-formed whatever was
/// asked for. `sinks` are the events the dispatcher-handle sinks and the
/// effect-controller sinks raise at boot, BEFORE the boot paint and
/// under the latch; the sinks are handed handles and start nothing, so
/// they are not gated. `fx` are the effects, one `start` each: the
/// events its start function raises and whether it returned a handle
/// (one that throws is reported and returns none). `subs` is the start
/// of the init model's subscriptions — ONE start, because
/// `Subs.Fx.change` starts them together and the loop can gate only
/// before the call, not between the subscriptions it starts (the gate
/// between them would live in `Sub.fs`) — or none, for `Sub.none`.
/// `cmd` is what `init`'s command raises — one start, for the same
/// reason over `Cmd.exec` — or none, for `Cmd.none`; a command holds
/// nothing. The boot paint's own events come from `render`.
///
/// Four things are not parameters because production makes them true by
/// construction, and the host pins each. The ERROR REPORTER: an
/// exception from a callee is reported and the loop goes on, which is
/// only true of a reporter that returns — every call to the program's
/// reporter goes through a guard that catches what the reporter itself
/// raises. The TERMINATE HANDLER's outcome: teardown is the same
/// transition whether the handler returned or raised, because the
/// handler runs under its own guard and `MarkTerminated` sits in a
/// `finally` (until Phase 871 a raising handler on the message route
/// left both flags unset). What the teardown's other callees RAISE:
/// production sets `terminated` before any of them runs, so a dispatch
/// they make is refused by `dispatch`'s own guard and a `Terminate` they
/// call finds the teardown already under way — `terminated_stays_terminated`
/// is what happens to both. And a teardown RE-ENTERED from its own
/// handler: the same flag makes the inner call a no-op.
///
/// What the model abstracts about teardown and the starts, and states:
/// WHAT the released handles do when disposed (an effect's `Dispose`, a
/// subscription's stop) is the effects' contract, not the loop's; the
/// model counts handles, it does not look inside them. Since Phase 900
/// `held` counts what the boot's starts AND every message diff's starts
/// acquired, so `terminated_holds_nothing` covers a subscription a
/// message's diff started. What a diff STOPS is not counted: `held`
/// never moves down except at teardown, so the model is exact for a
/// program whose subscription keys only ever accumulate (which is what
/// the differential scripts), and which keys a diff stops is
/// `ElmishSub.fst`'s theorem (`stop_exactly_removed`), not this one's. A
/// stop function that calls `Terminate` is not modelled either: the
/// diff's stops run before its starts and are the effects' contract.
/// The `DispatchAsync` post-await recheck is an interleaving and is NOT
/// modelled — this is the synchronous machine.

module ElmishLoop

open ElmishRing

(* ───────────────────────────────────────────────────────────────────
   Events — what the callees can do to the loop, and what the outside
   world can.
   ─────────────────────────────────────────────────────────────────── *)

/// A synchronous event raised from INSIDE the loop, while one message is
/// being processed or the model is being painted: `dispatch' msg` from
/// `setState`, a subscription's start, or a command; or
/// `IDispatcher.Terminate ()` from any of them.
type ev (m: Type0) =
  | Msg : msg: m -> ev m
  | Term : ev m

/// An event from OUTSIDE the loop, between drains: a dispatch from a
/// timer, a socket, React, or HMR; or `IDispatcher.Terminate ()`.
type ext (m: Type0) =
  | XDispatch : msg: m -> ext m
  | XTerminate : ext m

/// Phase 871 — one of the boot's GATED starts: an effect's start
/// function, the start of the init model's subscriptions, or `init`'s
/// command. `raised` are the events it raises synchronously while it
/// runs; `holds` is whether it returned something the loop must dispose
/// — an effect's `IDisposable`, the subscriptions `Subs.Fx.change`
/// started. A command holds nothing; a start that throws is reported
/// and returns nothing.
noeq type start (m: Type0) = {
  raised: list (ev m);
  holds: bool;
}

/// Phase 900 — what the `update` oracle returns for one message: the
/// new model; the STARTS the subscription diff makes, one per
/// subscription `Subs.Fx.change` starts, in the order it starts them
/// (`toStart`'s order: the requested order, deduplicated); and the
/// message's command — the events `Cmd.exec` raises, or none for
/// `Cmd.none`. Until 900 the three were one flattened event list.
noeq type reply (m: Type0) (md: Type0) = {
  next: md;
  starts: list (start m);
  cmd: opt (list (ev m));
}

/// A command as a start: it runs, raises its events, and holds nothing —
/// a command returns no handle. Used by the boot for `init`'s command and
/// by `step` for the message's.
let command (#m: Type0) (cmd: opt (list (ev m))) : opt (start m) =
  match cmd with
  | ONone -> ONone
  | OSome evs -> OSome ({ raised = evs; holds = false })

(* ───────────────────────────────────────────────────────────────────
   The state — the five cells, the dispatcher's flag, four observables.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `rb`, `reentered`, `terminated`, `dispatcherCore.active`, `state`,
/// `dirty`; `trace`, `log`, `painted` and `renders` are the differential's
/// observables (see the header). `dirty` is production's cell of the same
/// name: the model has moved since the render hook last saw it. `started`
/// and `held` (Phase 871) are observables too: production has no counter,
/// and the differential counts the start functions it scripts and the
/// handles they returned that were not yet disposed.
noeq type st (m: Type0) (md: Type0) = {
  ring: ring m;
  reentered: bool;
  terminated: bool;
  active: bool;
  model: md;
  dirty: bool;
  trace: list m;
  log: list m;
  painted: opt md;
  renders: nat;
  started: nat;
  held: nat;
}

/// The state after `init` returned and `dispatcherCore.Wire dispatch'`
/// ran — the first moment anything can dispatch, and BEFORE the boot
/// paint: the init model is unpainted, so `dirty` is set. `Wire` is what
/// sets `active`; before it nothing holds a reference to `dispatch`.
/// Nothing the program supplied runs between `Wire` and the latch being
/// set (`boot`), so no dispatch ever finds this state's latch clear.
let initial (#m #md: Type0) (capacity: int) (model: md) : st m md = {
  ring = create capacity;
  reentered = false;
  terminated = false;
  active = true;
  model = model;
  dirty = true;
  trace = [];
  log = [];
  painted = ONone;
  renders = 0;
  started = 0;
  held = 0;
}

(* ───────────────────────────────────────────────────────────────────
   The primitive transitions.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `dispatch msg` while `reentered` — `if not terminated then rb.Push msg`
/// and nothing else, because the latch is set. The push is logged.
let enqueue (#m #md: Type0) (s: st m md) (msg: m) : st m md =
  if s.terminated then s
  else { s with ring = push msg s.ring; log = append s.log [msg] }

/// F#: `teardown ()` — since Phase 871 the ONE teardown, run by the
/// `toTerminate msg` branch and installed as the terminate callback:
/// `if not terminated then terminated <- true`, then the subscriptions
/// stopped and the effects disposed — every handle the loop holds is
/// released, `held = 0` — then the handler, each under its own guard,
/// and `dispatcherCore.MarkTerminated ()` in a `finally`. The transition
/// is the same whether or not any callee raised, and whatever they
/// dispatch meets the flag already set (see the header).
let terminate (#m #md: Type0) (s: st m md) : st m md =
  if s.terminated then s
  else { s with terminated = true; active = false; held = 0 }

/// One event raised from inside the loop, applied under the latch.
let apply_ev (#m #md: Type0) (s: st m md) (e: ev m) : st m md =
  match e with
  | Msg msg -> enqueue s msg
  | Term -> terminate s

/// The events one callee sequence raises, in order.
let rec apply_evs (#m #md: Type0) (s: st m md) (evs: list (ev m)) : Tot (st m md) (decreases evs) =
  match evs with
  | [] -> s
  | e :: rest -> apply_evs (apply_ev s e) rest

/// F#: `dirty <- false; program.setState state dispatch'` — the paint.
/// The hook is handed the CURRENT model, the flag is cleared before it
/// runs (so a dispatch it makes re-dirties the model rather than being
/// painted twice), and the events it raises are applied under the latch,
/// exactly as a command's are. Phase 851: this is the one place the hook
/// is called from inside the drain.
let paint (#m #md: Type0) (render: md -> list (ev m)) (s: st m md) : st m md =
  let s1 = { s with dirty = false; painted = OSome s.model; renders = s.renders + 1 } in
  apply_evs s1 (render s.model)

/// F#: one subscription `Subs.Fx.change` starts inside a message's diff
/// (Phase 900) — `if terminated () then None else tryStart …`, and the
/// stop of everything the call started once the flag is found set. Made
/// only while the program runs; its events are applied under the latch
/// (a `dispatch'` enqueues, a `Terminate` goes through the callback);
/// what it returned is held only if the program is still running when
/// it returns — otherwise `change` stops it as soon as the diff returns.
/// `gated` below is the same transition for the boot's starts, whose
/// events go through `boot_ev`; under the latch the two agree
/// (`boot_evs_are_apply_evs`).
let start_one (#m #md: Type0) (s: st m md) (x: start m) : st m md =
  if s.terminated then s
  else
    let s1 = apply_evs { s with started = s.started + 1 } x.raised in
    if x.holds && not s1.terminated then { s1 with held = s1.held + 1 } else s1

/// F#: `toStart |> List.choose (fun sub -> if terminated () then None
/// else tryStart …)` — the diff's starts, one at a time, in order.
let rec start_all (#m #md: Type0) (s: st m md) (xs: list (start m)) : Tot (st m md) (decreases xs) =
  match xs with
  | [] -> s
  | x :: rest -> start_all (start_one s x) rest

/// F#: `if not terminated then cmd' |> Cmd.exec …` — the message's
/// command, run only while the program is still running after its diff
/// (Phase 900); `Cmd.none` runs nothing.
let start_opt (#m #md: Type0) (s: st m md) (x: opt (start m)) : st m md =
  match x with
  | ONone -> s
  | OSome x -> start_one s x

/// F#: the `Some msg` arm of `processMsgs`'s `while` — one popped
/// message. `toTerminate msg` takes the teardown branch; otherwise
/// `update` and `subscribe` run (the oracle: the message is handed to
/// `update` — that is the `trace` append), then the diff's starts one at
/// a time (`Subs.Fx.change`, gated between them since Phase 900), then
/// the command if the program is still running (`Cmd.exec`, gated since
/// 900), and only THEN is `state <- model'; dirty <- true` assigned,
/// which is why a `Terminate` raised from a start or a command still
/// leaves the new model in place. Since Phase 851 `setState` is NOT
/// among the callees here.
let step (#m #md: Type0)
         (update: m -> md -> reply m md)
         (to_terminate: m -> bool)
         (msg: m) (s: st m md) : st m md =
  if to_terminate msg then terminate s
  else
    let r = update msg s.model in
    let s1 = { s with trace = append s.trace [msg] } in
    let s2 = start_all s1 r.starts in
    let s3 = start_opt s2 (command r.cmd) in
    { s3 with model = r.next; dirty = true }

(* ───────────────────────────────────────────────────────────────────
   The drain.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `while not terminated && (Option.isSome nextMsg || dirty) do …`.
/// `next` is `nextMsg`, popped BEFORE the flag is examined — so a
/// message popped in the same iteration that terminates is dropped,
/// exactly as production drops it. An empty ring with the model dirty is
/// the `None` arm: the paint, then one more pop, because the hook may
/// have dispatched. An empty ring with nothing dirty is the exit. The
/// second component is whether the loop EXITED (on the flag or on a
/// clean empty ring) rather than ran out of fuel; the fuel is spent
/// after a message is processed or a paint is made, and before the next
/// pop, so a stalled machine has processed everything it popped.
/// `OSome Placeholder` is `nextMsg.Value` being `Unchecked.defaultof`,
/// which `placeholder_unobserved` proves a well-formed ring never pops;
/// the arm exists because the function is total.
let rec loop (#m #md: Type0)
             (fuel: nat)
             (update: m -> md -> reply m md)
             (to_terminate: m -> bool)
             (render: md -> list (ev m))
             (s: st m md) (next: opt (slot m))
  : Tot (pair (st m md) bool) (decreases fuel) =
  if s.terminated then Pair s true
  else
    match next with
    | ONone ->
        if not s.dirty then Pair s true
        else if fuel = 0 then Pair s false
        else
          let s' = paint render s in
          let Pair r' next' = pop s'.ring in
          loop (fuel - 1) update to_terminate render { s' with ring = r' } next'
    | OSome Placeholder -> Pair s true
    | OSome (Written msg) ->
        let s' = step update to_terminate msg s in
        if fuel = 0 then Pair s' false
        else
          let Pair r' next' = pop s'.ring in
          loop (fuel - 1) update to_terminate render { s' with ring = r' } next'

/// F#: `processMsgs ()` — `let mutable nextMsg = rb.Pop()` then the loop.
let process_msgs (#m #md: Type0)
                 (fuel: nat)
                 (update: m -> md -> reply m md)
                 (to_terminate: m -> bool)
                 (render: md -> list (ev m))
                 (s: st m md) : pair (st m md) bool =
  let Pair r next = pop s.ring in
  loop fuel update to_terminate render { s with ring = r } next

/// F#: `reentered <- true; processMsgs (); reentered <- false` — the
/// critical section `dispatch` runs when the latch was clear. The latch
/// is cleared only when the drain exited; a stalled drain leaves it set,
/// which is where production would be too.
let critical (#m #md: Type0)
             (fuel: nat)
             (update: m -> md -> reply m md)
             (to_terminate: m -> bool)
             (render: md -> list (ev m))
             (s: st m md) : st m md =
  let Pair s' finished = process_msgs fuel update to_terminate render { s with reentered = true } in
  if finished then { s' with reentered = false } else s'

/// F#: `dispatch msg`.
let dispatch (#m #md: Type0)
             (fuel: nat)
             (update: m -> md -> reply m md)
             (to_terminate: m -> bool)
             (render: md -> list (ev m))
             (s: st m md) (msg: m) : st m md =
  if s.terminated then s
  else
    let s1 = { s with ring = push msg s.ring; log = append s.log [msg] } in
    if s1.reentered then s1
    else critical fuel update to_terminate render s1

(* ───────────────────────────────────────────────────────────────────
   The boot drain — transcribed, not derived. The equivalence is the
   theorem `boot_drain_equiv` below.
   ─────────────────────────────────────────────────────────────────── *)

/// One event `init`'s effects raise during the boot drain — a dispatch
/// goes through `dispatch'` itself (the latch is set, so it enqueues),
/// a `Terminate` through the callback.
let boot_ev (#m #md: Type0)
            (fuel: nat)
            (update: m -> md -> reply m md)
            (to_terminate: m -> bool)
            (render: md -> list (ev m))
            (s: st m md) (e: ev m) : st m md =
  match e with
  | Msg msg -> dispatch fuel update to_terminate render s msg
  | Term -> terminate s

let rec boot_evs (#m #md: Type0)
                 (fuel: nat)
                 (update: m -> md -> reply m md)
                 (to_terminate: m -> bool)
                 (render: md -> list (ev m))
                 (s: st m md) (evs: list (ev m)) : Tot (st m md) (decreases evs) =
  match evs with
  | [] -> s
  | e :: rest -> boot_evs fuel update to_terminate render (boot_ev fuel update to_terminate render s e) rest

/// F#: `if not terminated then …` — Phase 871's check, made before every
/// effect's registration, before the subscription start and before
/// `init`'s command. A start that is made counts, raises its events
/// (each through `dispatch'`, under the latch, so a dispatch only
/// enqueues; a `Terminate` through the callback), and hands the loop what
/// it returned to hold — UNLESS one of its own events terminated the
/// program: the teardown has already run and will not run again, so what
/// it returned is released as soon as it returns (`EffectRegistry.Register`
/// disposes the handle instead of storing it; the boot stops the
/// subscriptions `Subs.Fx.change` returned). A start the check finds the
/// program terminated at is not made at all.
let gated (#m #md: Type0)
          (fuel: nat)
          (update: m -> md -> reply m md)
          (to_terminate: m -> bool)
          (render: md -> list (ev m))
          (s: st m md) (x: start m) : st m md =
  if s.terminated then s
  else
    let s1 = boot_evs fuel update to_terminate render { s with started = s.started + 1 } x.raised in
    if x.holds && not s1.terminated then { s1 with held = s1.held + 1 } else s1

/// F#: `for effect in program.effects do if not terminated then Register …`.
let rec gated_all (#m #md: Type0)
                  (fuel: nat)
                  (update: m -> md -> reply m md)
                  (to_terminate: m -> bool)
                  (render: md -> list (ev m))
                  (s: st m md) (xs: list (start m)) : Tot (st m md) (decreases xs) =
  match xs with
  | [] -> s
  | x :: rest -> gated_all fuel update to_terminate render (gated fuel update to_terminate render s x) rest

/// A start the boot may have nothing to make: `Sub.none` starts no
/// subscription, `Cmd.none` runs no command.
let gated_opt (#m #md: Type0)
              (fuel: nat)
              (update: m -> md -> reply m md)
              (to_terminate: m -> bool)
              (render: md -> list (ev m))
              (s: st m md) (x: opt (start m)) : st m md =
  match x with
  | ONone -> s
  | OSome x -> gated fuel update to_terminate render s x

/// F#: the head of the boot — `reentered <- true` FIRST, before anything
/// the program supplied is called; then the dispatcher-handle sinks and
/// the effect-controller sinks run UNDER the latch (`sinks`: the events
/// they raise, each through `dispatch'`, so a dispatch only enqueues; a
/// `Terminate` through the callback), and then the effects, each a
/// `gated` start (Phase 871 — until then every effect was started even
/// after a sink or an earlier effect had called `Terminate`).
///
/// Until this was restated the latch was set AFTER the sinks and the
/// effects ran, so an effect that dispatched from its start function
/// found the latch clear and ran a whole drain — `update`, the
/// subscription diff, the command, and a paint — before the boot paint,
/// on a model the boot paint was then handed second.
/// `boot_paints_init_model` is what the order buys.
let preboot (#m #md: Type0)
            (fuel: nat)
            (update: m -> md -> reply m md)
            (to_terminate: m -> bool)
            (render: md -> list (ev m))
            (s: st m md) (sinks: list (ev m)) (fx: list (start m)) : st m md =
  gated_all fuel update to_terminate render
            (boot_evs fuel update to_terminate render { s with reentered = true } sinks) fx

/// F#: the boot paint — `dirty <- false; setState model dispatch'`,
/// unconditional, after the sinks and the effects and BEFORE `init`'s
/// command runs: a hydrating renderer must see the model the server
/// rendered. Not gated: a program a sink terminated still paints its
/// init model once, as production does.
let boot_paint (#m #md: Type0)
               (fuel: nat)
               (update: m -> md -> reply m md)
               (to_terminate: m -> bool)
               (render: md -> list (ev m))
               (s: st m md) (sinks: list (ev m)) (fx: list (start m)) : st m md =
  paint render (preboot fuel update to_terminate render s sinks fx)

/// F#: the tail of `runWithDispatch` — the latch, the sinks and the
/// effects (`preboot`), the boot paint (`boot_paint`), then the gated
/// subscription start (`Subs.Fx.change … dispatch'`, and `Subs.Fx.stop`
/// over what it returned if one of them terminated the program) and the
/// gated `Cmd.exec … dispatch' cmd`, then `processMsgs ()`, then
/// `reentered <- false`.
let boot (#m #md: Type0)
         (fuel: nat)
         (update: m -> md -> reply m md)
         (to_terminate: m -> bool)
         (render: md -> list (ev m))
         (s: st m md) (sinks: list (ev m)) (fx: list (start m))
         (subs: opt (start m)) (cmd: opt (list (ev m))) : st m md =
  let s1 = boot_paint fuel update to_terminate render s sinks fx in
  let s2 = gated_opt fuel update to_terminate render (gated_opt fuel update to_terminate render s1 subs) (command cmd) in
  let Pair s3 finished = process_msgs fuel update to_terminate render s2 in
  if finished then { s3 with reentered = false } else s3

(* ───────────────────────────────────────────────────────────────────
   The differential host's driver — the outside world, one event at a
   time. Extracted, so the host runs THIS beside production.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: an `IDispatcher.Dispatch` / `.Terminate` from outside the loop.
/// A machine whose last drain stalled is never fed: production would
/// not have returned to the host.
let rec run (#m #md: Type0)
            (fuel: nat)
            (update: m -> md -> reply m md)
            (to_terminate: m -> bool)
            (render: md -> list (ev m))
            (s: st m md) (exts: list (ext m)) : Tot (st m md) (decreases exts) =
  if s.reentered then s
  else
    match exts with
    | [] -> s
    | XDispatch msg :: rest -> run fuel update to_terminate render (dispatch fuel update to_terminate render s msg) rest
    | XTerminate :: rest -> run fuel update to_terminate render (terminate s) rest

/// A whole program: `init` (its model), the events the sinks raise and
/// the effects' starts before the boot paint, the subscription start and
/// `init`'s command after it, the boot drain, then the outside world.
let program (#m #md: Type0)
            (fuel: nat)
            (update: m -> md -> reply m md)
            (to_terminate: m -> bool)
            (render: md -> list (ev m))
            (capacity: int) (model: md)
            (sinks: list (ev m)) (fx: list (start m)) (subs: opt (start m)) (cmd: opt (list (ev m)))
            (exts: list (ext m)) : st m md =
  run fuel update to_terminate render
      (boot fuel update to_terminate render (initial capacity model) sinks fx subs cmd) exts

/// F#: `Dispatcher.fs`'s fallback arm — `IDispatcher.Terminate` with no
/// callback wired: `active <- false` and nothing else. Not a transition
/// the runtime ever takes (`SetTerminateCallback` always precedes
/// `AsInterface`); modelled so the exception to the two-flag encoding is
/// a computed fact rather than a footnote.
let fallback_terminate (#m #md: Type0) (s: st m md) : st m md = { s with active = false }

(* ───────────────────────────────────────────────────────────────────
   The specification — ghost. What the loop MEANS.
   ─────────────────────────────────────────────────────────────────── *)

/// The messages in a list of slots — the ring's unread contents as
/// messages. Placeholders are skipped; under `wf` there are none.
[@@ noextract_to "FSharp"]
let rec msgs (#m: Type0) (xs: list (slot m)) : Tot (list m) (decreases xs) =
  match xs with
  | [] -> []
  | Placeholder :: rest -> msgs rest
  | Written v :: rest -> v :: msgs rest

/// The messages accepted but not yet handed to `update`.
[@@ noextract_to "FSharp"]
let pending (#m #md: Type0) (s: st m md) : list m = msgs (unread s.ring)

/// `xs` is a prefix of `ys`.
[@@ noextract_to "FSharp"]
let is_prefix (#m: Type0) (xs ys: list m) : prop = take (length xs) ys == xs

/// The messages a popped `nextMsg` stands for.
[@@ noextract_to "FSharp"]
let opt_msgs (#m: Type0) (next: opt (slot m)) : list m =
  match next with
  | OSome (Written v) -> [v]
  | _ -> []

/// A render oracle that dispatches nothing synchronously — the React
/// adapter's `setState`, which hands the view to React and returns.
[@@ noextract_to "FSharp"]
let quiet (#m #md: Type0) (render: md -> list (ev m)) : prop = forall (x: md). render x == []

/// The popped `nextMsg` is a message `update` will be handed — written,
/// and not one the termination predicate takes.
[@@ noextract_to "FSharp"]
let processes (#m: Type0) (to_terminate: m -> bool) (next: opt (slot m)) : bool =
  match next with
  | OSome (Written msg) -> not (to_terminate msg)
  | _ -> false

/// The popped `nextMsg` is a message the termination predicate takes.
[@@ noextract_to "FSharp"]
let terminates (#m: Type0) (to_terminate: m -> bool) (next: opt (slot m)) : bool =
  match next with
  | OSome (Written msg) -> to_terminate msg
  | _ -> false

/// The core invariant — true of every state the machine passes through,
/// latched or idle. The ring is well-formed; the dispatcher's flag is
/// the loop's flag negated; while not terminated the log is exactly the
/// trace followed by what is pending, and once terminated the trace is
/// a prefix of the log and both are frozen; a model that is not dirty
/// is the model on screen; and (Phase 871) a terminated program holds
/// nothing.
[@@ noextract_to "FSharp"]
let inv_core (#m #md: Type0) (s: st m md) : prop =
  wf s.ring
  /\ s.active = not s.terminated
  /\ (not s.terminated ==> s.log == append s.trace (pending s))
  /\ (s.terminated ==> is_prefix s.trace s.log)
  /\ (not s.dirty ==> s.painted == OSome s.model)
  /\ (s.terminated ==> s.held == 0)

/// The invariant at the boundary of every operation: the core, and an
/// idle non-terminated machine has drained its ring and painted its
/// model.
[@@ noextract_to "FSharp"]
let inv (#m #md: Type0) (s: st m md) : prop =
  inv_core s /\ (not s.reentered /\ not s.terminated ==> unread s.ring == [] /\ not s.dirty)

/// The events contain a `Terminate`.
[@@ noextract_to "FSharp"]
let rec has_term (#m: Type0) (evs: list (ev m)) : Tot bool (decreases evs) =
  match evs with
  | [] -> false
  | Term :: _ -> true
  | Msg _ :: rest -> has_term rest

(* ───────────────────────────────────────────────────────────────────
   List lemmas.
   ─────────────────────────────────────────────────────────────────── *)

let rec msgs_append (#m: Type0) (xs ys: list (slot m))
  : Lemma (ensures msgs (append xs ys) == append (msgs xs) (msgs ys)) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> msgs_append rest ys

let prefix_append (#m: Type0) (xs zs: list m)
  : Lemma (ensures is_prefix xs (append xs zs)) =
  take_append xs zs

let prefix_refl (#m: Type0) (xs: list m)
  : Lemma (ensures is_prefix xs xs) =
  append_nil xs;
  take_append xs []

(* ───────────────────────────────────────────────────────────────────
   The transitions preserve the invariant.
   ─────────────────────────────────────────────────────────────────── *)

/// **`enqueue_spec`.** A reentrant push queues the message at the back
/// of what is pending, logs it, and touches nothing else — not the
/// model, not the paint.
let enqueue_spec (#m #md: Type0) (s: st m md) (msg: m)
  : Lemma (requires inv_core s)
          (ensures (let s' = enqueue s msg in
                    inv_core s'
                    /\ s'.reentered == s.reentered /\ s'.terminated == s.terminated
                    /\ s'.active == s.active /\ s'.trace == s.trace /\ s'.model == s.model
                    /\ s'.dirty == s.dirty /\ s'.painted == s.painted /\ s'.renders == s.renders
                    /\ s'.started == s.started /\ s'.held == s.held
                    /\ (not s.terminated ==>
                          s'.log == append s.log [msg] /\ pending s' == append (pending s) [msg]))) =
  if s.terminated then ()
  else begin
    push_spec msg s.ring;
    msgs_append (unread s.ring) [Written msg];
    append_assoc s.trace (pending s) [msg]
  end

/// **`terminate_spec`.** Termination sets both flags, releases every
/// handle, freezes trace, log and the paint, starts nothing, and is
/// idempotent.
let terminate_spec (#m #md: Type0) (s: st m md)
  : Lemma (requires inv_core s)
          (ensures (let s' = terminate s in
                    inv_core s'
                    /\ s'.terminated /\ not s'.active /\ s'.held == 0
                    /\ s'.reentered == s.reentered /\ s'.trace == s.trace
                    /\ s'.log == s.log /\ s'.model == s.model /\ s'.ring == s.ring
                    /\ s'.dirty == s.dirty /\ s'.painted == s.painted /\ s'.renders == s.renders
                    /\ s'.started == s.started)) =
  if s.terminated then ()
  else prefix_append s.trace (pending s)

let rec apply_evs_spec (#m #md: Type0) (s: st m md) (evs: list (ev m))
  : Lemma (requires inv_core s)
          (ensures (let s' = apply_evs s evs in
                    inv_core s'
                    /\ s'.reentered == s.reentered /\ s'.trace == s.trace /\ s'.model == s.model
                    /\ s'.dirty == s.dirty /\ s'.painted == s.painted /\ s'.renders == s.renders
                    /\ s'.started == s.started /\ (not s'.terminated ==> s'.held == s.held)
                    /\ (s.terminated ==> s'.terminated)))
          (decreases evs) =
  match evs with
  | [] -> ()
  | Msg msg :: rest ->
      enqueue_spec s msg;
      apply_evs_spec (enqueue s msg) rest
  | Term :: rest ->
      terminate_spec s;
      apply_evs_spec (terminate s) rest

/// **`start_one_spec`** (Phase 900). A start inside a message's diff
/// keeps the core invariant: its events extend log and pending in
/// lockstep (`apply_evs_spec`), its handle is held only while the
/// program runs, and a terminated machine is untouched — the gate.
let start_one_spec (#m #md: Type0) (s: st m md) (x: start m)
  : Lemma (requires inv_core s)
          (ensures (let s' = start_one s x in
                    inv_core s'
                    /\ s'.reentered == s.reentered /\ s'.trace == s.trace /\ s'.model == s.model
                    /\ s'.dirty == s.dirty /\ s'.painted == s.painted /\ s'.renders == s.renders
                    /\ (s.terminated ==> s' == s))) =
  if s.terminated then ()
  else apply_evs_spec { s with started = s.started + 1 } x.raised

let rec start_all_spec (#m #md: Type0) (s: st m md) (xs: list (start m))
  : Lemma (requires inv_core s)
          (ensures (let s' = start_all s xs in
                    inv_core s'
                    /\ s'.reentered == s.reentered /\ s'.trace == s.trace /\ s'.model == s.model
                    /\ s'.dirty == s.dirty /\ s'.painted == s.painted /\ s'.renders == s.renders
                    /\ (s.terminated ==> s' == s)))
          (decreases xs) =
  match xs with
  | [] -> ()
  | x :: rest ->
      start_one_spec s x;
      start_all_spec (start_one s x) rest

let start_opt_spec (#m #md: Type0) (s: st m md) (x: opt (start m))
  : Lemma (requires inv_core s)
          (ensures (let s' = start_opt s x in
                    inv_core s'
                    /\ s'.reentered == s.reentered /\ s'.trace == s.trace /\ s'.model == s.model
                    /\ s'.dirty == s.dirty /\ s'.painted == s.painted /\ s'.renders == s.renders
                    /\ (s.terminated ==> s' == s))) =
  match x with
  | ONone -> ()
  | OSome x -> start_one_spec s x

/// **`step_spec`.** Processing a popped message — one the log holds at
/// the head of what is pending — restores the core invariant: the
/// message moves from pending to trace, every event the diff's starts
/// and the command raised extends both log and pending in lockstep, and
/// the model is dirty (or the machine terminated). The paint is
/// untouched.
let step_spec (#m #md: Type0)
              (update: m -> md -> reply m md)
              (to_terminate: m -> bool)
              (msg: m) (s: st m md)
  : Lemma (requires wf s.ring /\ s.active = not s.terminated /\ not s.terminated
                    /\ s.log == append s.trace (msg :: pending s)
                    /\ (not s.dirty ==> s.painted == OSome s.model))
          (ensures (let s' = step update to_terminate msg s in
                    inv_core s' /\ s'.reentered == s.reentered
                    /\ s'.renders == s.renders /\ s'.painted == s.painted
                    /\ (to_terminate msg ==> s'.terminated)
                    /\ (not (to_terminate msg) ==> s'.dirty))) =
  if to_terminate msg then prefix_append s.trace (msg :: pending s)
  else begin
    let r = update msg s.model in
    let s1 = { s with trace = append s.trace [msg] } in
    append_assoc s.trace [msg] (pending s);
    start_all_spec s1 r.starts;
    start_opt_spec (start_all s1 r.starts) (command r.cmd)
  end

/// **`paint_spec`.** The paint hands the hook the current model, counts
/// it, clears the flag, and applies the hook's events under the latch:
/// the trace and the model are untouched, and the painted model IS the
/// model.
let paint_spec (#m #md: Type0) (render: md -> list (ev m)) (s: st m md)
  : Lemma (requires inv_core s)
          (ensures (let s' = paint render s in
                    inv_core s'
                    /\ s'.reentered == s.reentered /\ s'.trace == s.trace /\ s'.model == s.model
                    /\ not s'.dirty /\ s'.painted == OSome s.model /\ s'.renders == s.renders + 1
                    /\ s'.started == s.started
                    /\ (s.terminated ==> s'.terminated))) =
  let s1 = { s with dirty = false; painted = OSome s.model; renders = s.renders + 1 } in
  apply_evs_spec s1 (render s.model)

/// The paint never touches the latch — with or without the invariant.
let rec apply_evs_reentered (#m #md: Type0) (s: st m md) (evs: list (ev m))
  : Lemma (ensures (apply_evs s evs).reentered == s.reentered) (decreases evs) =
  match evs with
  | [] -> ()
  | e :: rest -> apply_evs_reentered (apply_ev s e) rest

let paint_reentered (#m #md: Type0) (render: md -> list (ev m)) (s: st m md)
  : Lemma (ensures (paint render s).reentered == s.reentered) =
  apply_evs_reentered { s with dirty = false; painted = OSome s.model; renders = s.renders + 1 } (render s.model)

/// The loop's invariant, with the popped `nextMsg` accounted for.
[@@ noextract_to "FSharp"]
let loop_inv (#m #md: Type0) (s: st m md) (next: opt (slot m)) : prop =
  wf s.ring
  /\ s.active = not s.terminated
  /\ s.reentered
  /\ next =!= OSome (Placeholder #m)
  /\ (ONone? next ==> unread s.ring == [])
  /\ (not s.terminated ==> s.log == append s.trace (append (opt_msgs next) (pending s)))
  /\ (s.terminated ==> is_prefix s.trace s.log)
  /\ (not s.dirty ==> s.painted == OSome s.model)
  /\ (s.terminated ==> s.held == 0)

/// A pop on a well-formed ring re-establishes the loop invariant.
let pop_loop_inv (#m #md: Type0) (s: st m md)
  : Lemma (requires inv_core s /\ s.reentered)
          (ensures (let Pair r next = pop s.ring in loop_inv { s with ring = r } next)) =
  pop_spec s.ring;
  match unread s.ring with
  | [] -> ()
  | Written _ :: _ -> ()
  | Placeholder :: _ -> ()

let rec loop_spec (#m #md: Type0)
                  (fuel: nat)
                  (update: m -> md -> reply m md)
                  (to_terminate: m -> bool)
                  (render: md -> list (ev m))
                  (s: st m md) (next: opt (slot m))
  : Lemma (requires loop_inv s next)
          (ensures (let Pair s' finished = loop fuel update to_terminate render s next in
                    inv_core s' /\ s'.reentered
                    /\ (finished /\ not s'.terminated ==> unread s'.ring == [] /\ not s'.dirty)))
          (decreases fuel) =
  if s.terminated then ()
  else
    match next with
    | ONone ->
        if not s.dirty then ()
        else if fuel = 0 then ()
        else begin
          paint_spec render s;
          let s' = paint render s in
          pop_loop_inv s';
          let Pair r' next' = pop s'.ring in
          loop_spec (fuel - 1) update to_terminate render { s' with ring = r' } next'
        end
    | OSome Placeholder -> ()
    | OSome (Written msg) ->
        step_spec update to_terminate msg s;
        let s' = step update to_terminate msg s in
        if fuel = 0 then ()
        else begin
          pop_loop_inv s';
          let Pair r' next' = pop s'.ring in
          loop_spec (fuel - 1) update to_terminate render { s' with ring = r' } next'
        end

let process_msgs_spec (#m #md: Type0)
                      (fuel: nat)
                      (update: m -> md -> reply m md)
                      (to_terminate: m -> bool)
                      (render: md -> list (ev m))
                      (s: st m md)
  : Lemma (requires inv_core s /\ s.reentered)
          (ensures (let Pair s' finished = process_msgs fuel update to_terminate render s in
                    inv_core s' /\ s'.reentered
                    /\ (finished /\ not s'.terminated ==> unread s'.ring == [] /\ not s'.dirty))) =
  pop_loop_inv s;
  let Pair r next = pop s.ring in
  loop_spec fuel update to_terminate render { s with ring = r } next

let critical_spec (#m #md: Type0)
                  (fuel: nat)
                  (update: m -> md -> reply m md)
                  (to_terminate: m -> bool)
                  (render: md -> list (ev m))
                  (s: st m md)
  : Lemma (requires inv_core s)
          (ensures inv (critical fuel update to_terminate render s)) =
  process_msgs_spec fuel update to_terminate render { s with reentered = true }

/// **`dispatch_inv`.** `dispatch` preserves the invariant from any
/// state that satisfies it — idle or latched, terminated or not.
let dispatch_inv (#m #md: Type0)
                 (fuel: nat)
                 (update: m -> md -> reply m md)
                 (to_terminate: m -> bool)
                 (render: md -> list (ev m))
                 (s: st m md) (msg: m)
  : Lemma (requires inv s)
          (ensures inv (dispatch fuel update to_terminate render s msg)) =
  if s.terminated then ()
  else begin
    enqueue_spec s msg;
    let s1 = enqueue s msg in
    if s1.reentered then ()
    else critical_spec fuel update to_terminate render s1
  end

let terminate_inv (#m #md: Type0) (s: st m md)
  : Lemma (requires inv s) (ensures inv (terminate s)) =
  terminate_spec s

/// **`dispatch_latched`.** Under the latch, `dispatch` IS `enqueue` —
/// the clause-for-clause transcription and the primitive agree.
let dispatch_latched (#m #md: Type0)
                     (fuel: nat)
                     (update: m -> md -> reply m md)
                     (to_terminate: m -> bool)
                     (render: md -> list (ev m))
                     (s: st m md) (msg: m)
  : Lemma (requires s.reentered)
          (ensures dispatch fuel update to_terminate render s msg == enqueue s msg) = ()

let rec boot_evs_are_apply_evs (#m #md: Type0)
                               (fuel: nat)
                               (update: m -> md -> reply m md)
                               (to_terminate: m -> bool)
                               (render: md -> list (ev m))
                               (s: st m md) (evs: list (ev m))
  : Lemma (requires s.reentered)
          (ensures boot_evs fuel update to_terminate render s evs == apply_evs s evs)
          (decreases evs) =
  match evs with
  | [] -> ()
  | Msg msg :: rest ->
      dispatch_latched fuel update to_terminate render s msg;
      boot_evs_are_apply_evs fuel update to_terminate render (enqueue s msg) rest
  | Term :: rest ->
      boot_evs_are_apply_evs fuel update to_terminate render (terminate s) rest

(* ───────────────────────────────────────────────────────────────────
   The gated starts (Phase 871).
   ─────────────────────────────────────────────────────────────────── *)

/// Under the latch a gated start leaves the latch set — with or without
/// the invariant.
let gated_reentered (#m #md: Type0)
                    (fuel: nat)
                    (update: m -> md -> reply m md)
                    (to_terminate: m -> bool)
                    (render: md -> list (ev m))
                    (s: st m md) (x: start m)
  : Lemma (requires s.reentered)
          (ensures (gated fuel update to_terminate render s x).reentered) =
  if s.terminated then ()
  else begin
    let s0 = { s with started = s.started + 1 } in
    boot_evs_are_apply_evs fuel update to_terminate render s0 x.raised;
    apply_evs_reentered s0 x.raised
  end

let rec gated_all_reentered (#m #md: Type0)
                            (fuel: nat)
                            (update: m -> md -> reply m md)
                            (to_terminate: m -> bool)
                            (render: md -> list (ev m))
                            (s: st m md) (xs: list (start m))
  : Lemma (requires s.reentered)
          (ensures (gated_all fuel update to_terminate render s xs).reentered)
          (decreases xs) =
  match xs with
  | [] -> ()
  | x :: rest ->
      gated_reentered fuel update to_terminate render s x;
      gated_all_reentered fuel update to_terminate render (gated fuel update to_terminate render s x) rest

let gated_opt_reentered (#m #md: Type0)
                        (fuel: nat)
                        (update: m -> md -> reply m md)
                        (to_terminate: m -> bool)
                        (render: md -> list (ev m))
                        (s: st m md) (x: opt (start m))
  : Lemma (requires s.reentered)
          (ensures (gated_opt fuel update to_terminate render s x).reentered) =
  match x with
  | ONone -> ()
  | OSome x -> gated_reentered fuel update to_terminate render s x

/// **`gated_spec`.** Under the latch a gated start preserves the core
/// invariant — a start that terminated the program by itself holds
/// nothing afterwards — and touches nothing the loop processes or
/// paints: not the trace, the model, the dirty flag or the paint. Made,
/// it is counted once; on a terminated program it is not made.
let gated_spec (#m #md: Type0)
               (fuel: nat)
               (update: m -> md -> reply m md)
               (to_terminate: m -> bool)
               (render: md -> list (ev m))
               (s: st m md) (x: start m)
  : Lemma (requires inv_core s /\ s.reentered)
          (ensures (let s' = gated fuel update to_terminate render s x in
                    inv_core s' /\ s'.reentered
                    /\ s'.trace == s.trace /\ s'.model == s.model /\ s'.dirty == s.dirty
                    /\ s'.painted == s.painted /\ s'.renders == s.renders
                    /\ (s.terminated ==> s'.terminated /\ s'.started == s.started)
                    /\ (not s.terminated ==> s'.started == s.started + 1))) =
  if s.terminated then ()
  else begin
    let s0 = { s with started = s.started + 1 } in
    boot_evs_are_apply_evs fuel update to_terminate render s0 x.raised;
    apply_evs_spec s0 x.raised
  end

let rec gated_all_spec (#m #md: Type0)
                       (fuel: nat)
                       (update: m -> md -> reply m md)
                       (to_terminate: m -> bool)
                       (render: md -> list (ev m))
                       (s: st m md) (xs: list (start m))
  : Lemma (requires inv_core s /\ s.reentered)
          (ensures (let s' = gated_all fuel update to_terminate render s xs in
                    inv_core s' /\ s'.reentered
                    /\ s'.trace == s.trace /\ s'.model == s.model /\ s'.dirty == s.dirty
                    /\ s'.painted == s.painted /\ s'.renders == s.renders
                    /\ (s.terminated ==> s'.terminated)))
          (decreases xs) =
  match xs with
  | [] -> ()
  | x :: rest ->
      gated_spec fuel update to_terminate render s x;
      gated_all_spec fuel update to_terminate render (gated fuel update to_terminate render s x) rest

let gated_opt_spec (#m #md: Type0)
                   (fuel: nat)
                   (update: m -> md -> reply m md)
                   (to_terminate: m -> bool)
                   (render: md -> list (ev m))
                   (s: st m md) (x: opt (start m))
  : Lemma (requires inv_core s /\ s.reentered)
          (ensures (let s' = gated_opt fuel update to_terminate render s x in
                    inv_core s' /\ s'.reentered
                    /\ s'.trace == s.trace /\ s'.model == s.model /\ s'.dirty == s.dirty
                    /\ s'.painted == s.painted /\ s'.renders == s.renders
                    /\ (s.terminated ==> s'.terminated))) =
  match x with
  | ONone -> ()
  | OSome x -> gated_spec fuel update to_terminate render s x

/// **`gated_terminated`** (Phase 871). The guard itself: a start the boot
/// reaches once the program is terminated is not made — nothing is
/// counted, raised or held, and the state is unchanged. The F# is the
/// `if not terminated then` before every effect's registration, the
/// subscription start and `init`'s command.
let gated_terminated (#m #md: Type0)
                     (fuel: nat)
                     (update: m -> md -> reply m md)
                     (to_terminate: m -> bool)
                     (render: md -> list (ev m))
                     (s: st m md) (x: start m)
  : Lemma (requires s.terminated)
          (ensures gated fuel update to_terminate render s x == s) = ()

/// …and over every effect still to start.
let rec gated_all_terminated (#m #md: Type0)
                             (fuel: nat)
                             (update: m -> md -> reply m md)
                             (to_terminate: m -> bool)
                             (render: md -> list (ev m))
                             (s: st m md) (xs: list (start m))
  : Lemma (requires s.terminated)
          (ensures gated_all fuel update to_terminate render s xs == s)
          (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> gated_all_terminated fuel update to_terminate render s rest

/// **`boot_inv`.** The boot drain establishes the invariant from a state
/// with the core — the unpainted state `initial` builds is one —
/// whatever the sinks raised, whatever the effects' starts raised and
/// returned, and whatever the subscription start and `init`'s command
/// raised after the boot paint.
let boot_inv (#m #md: Type0)
             (fuel: nat)
             (update: m -> md -> reply m md)
             (to_terminate: m -> bool)
             (render: md -> list (ev m))
             (s: st m md) (sinks: list (ev m)) (fx: list (start m))
             (subs: opt (start m)) (cmd: opt (list (ev m)))
  : Lemma (requires inv_core s)
          (ensures inv (boot fuel update to_terminate render s sinks fx subs cmd)) =
  let s0 = { s with reentered = true } in
  boot_evs_are_apply_evs fuel update to_terminate render s0 sinks;
  apply_evs_spec s0 sinks;
  let sk = apply_evs s0 sinks in
  gated_all_spec fuel update to_terminate render sk fx;
  let sp = gated_all fuel update to_terminate render sk fx in
  paint_spec render sp;
  let s1 = paint render sp in
  gated_opt_spec fuel update to_terminate render s1 subs;
  let s2 = gated_opt fuel update to_terminate render s1 subs in
  gated_opt_spec fuel update to_terminate render s2 (command cmd);
  process_msgs_spec fuel update to_terminate render (gated_opt fuel update to_terminate render s2 (command cmd))

/// **`boot_paints_init_model`.** Whatever the dispatcher-handle sinks, the
/// effect-controller sinks and the effects' start functions dispatch
/// before the boot paint — any number of messages, a `Terminate`, both —
/// none of it is handed to `update`, none of it moves the model, none of
/// it paints; and the boot paint then hands the render hook THE MODEL
/// `init` RETURNED, once. This is the property a hydrating renderer
/// depends on and the reason the latch is set before the sinks run: with
/// the latch clear, the first such dispatch would have run a drain and
/// painted a model the server never rendered.
let boot_paints_init_model (#m #md: Type0)
                           (fuel: nat)
                           (update: m -> md -> reply m md)
                           (to_terminate: m -> bool)
                           (render: md -> list (ev m))
                           (s: st m md) (sinks: list (ev m)) (fx: list (start m))
  : Lemma (requires inv_core s)
          (ensures (let sp = preboot fuel update to_terminate render s sinks fx in
                    let s1 = boot_paint fuel update to_terminate render s sinks fx in
                    sp.model == s.model /\ sp.trace == s.trace
                    /\ sp.painted == s.painted /\ sp.renders == s.renders
                    /\ s1.model == s.model /\ s1.trace == s.trace
                    /\ s1.painted == OSome s.model /\ s1.renders == s.renders + 1)) =
  let s0 = { s with reentered = true } in
  boot_evs_are_apply_evs fuel update to_terminate render s0 sinks;
  apply_evs_spec s0 sinks;
  let sk = apply_evs s0 sinks in
  gated_all_spec fuel update to_terminate render sk fx;
  paint_spec render (gated_all fuel update to_terminate render sk fx)

/// **`run_inv`.** The outside world preserves the invariant.
let rec run_inv (#m #md: Type0)
                (fuel: nat)
                (update: m -> md -> reply m md)
                (to_terminate: m -> bool)
                (render: md -> list (ev m))
                (s: st m md) (exts: list (ext m))
  : Lemma (requires inv s)
          (ensures inv (run fuel update to_terminate render s exts))
          (decreases exts) =
  if s.reentered then ()
  else
    match exts with
    | [] -> ()
    | XDispatch msg :: rest ->
        dispatch_inv fuel update to_terminate render s msg;
        run_inv fuel update to_terminate render (dispatch fuel update to_terminate render s msg) rest
    | XTerminate :: rest ->
        terminate_inv s;
        run_inv fuel update to_terminate render (terminate s) rest

/// **`initial_inv`.** The state after `Wire` satisfies the core
/// invariant, whatever capacity was asked for. Not `inv`: the init model
/// is unpainted until the boot paints it, and `boot_inv` is what turns
/// the core into the full invariant.
let initial_inv (#m #md: Type0) (capacity: int) (model: md)
  : Lemma (ensures inv_core (initial #m capacity model)) =
  create_wf #m capacity

/// **`program_inv`.** Every state a program reaches satisfies the
/// invariant.
let program_inv (#m #md: Type0)
                (fuel: nat)
                (update: m -> md -> reply m md)
                (to_terminate: m -> bool)
                (render: md -> list (ev m))
                (capacity: int) (model: md)
                (sinks: list (ev m)) (fx: list (start m))
                (subs: opt (start m)) (cmd: opt (list (ev m))) (exts: list (ext m))
  : Lemma (ensures inv (program fuel update to_terminate render capacity model sinks fx subs cmd exts)) =
  initial_inv #m capacity model;
  boot_inv fuel update to_terminate render (initial capacity model) sinks fx subs cmd;
  run_inv fuel update to_terminate render
          (boot fuel update to_terminate render (initial capacity model) sinks fx subs cmd) exts

(* ───────────────────────────────────────────────────────────────────
   The theorems — the phase's six, as named corollaries, and Phase
   851's two about the paint.
   ─────────────────────────────────────────────────────────────────── *)

/// **`exactly_once`.** In every idle, non-terminated state a program
/// reaches, the log IS the trace: every message `dispatch` accepted —
/// from outside or re-dispatched from inside, including from the
/// post-drain paint — was handed to `update` exactly once, and in the
/// order it was accepted.
let exactly_once (#m #md: Type0)
                 (fuel: nat)
                 (update: m -> md -> reply m md)
                 (to_terminate: m -> bool)
                 (render: md -> list (ev m))
                 (capacity: int) (model: md)
                 (sinks: list (ev m)) (fx: list (start m))
                (subs: opt (start m)) (cmd: opt (list (ev m))) (exts: list (ext m))
  : Lemma (ensures (let s = program fuel update to_terminate render capacity model sinks fx subs cmd exts in
                    not s.reentered /\ not s.terminated ==> s.log == s.trace)) =
  program_inv fuel update to_terminate render capacity model sinks fx subs cmd exts;
  let s = program fuel update to_terminate render capacity model sinks fx subs cmd exts in
  if not s.reentered && not s.terminated then append_nil s.trace

/// **`in_order`.** In EVERY state a program reaches — mid-drain,
/// stalled, or terminated — the trace is a prefix of the log. Nothing
/// is ever handed to `update` out of the order in which it was accepted;
/// termination can only truncate.
let in_order (#m #md: Type0)
             (fuel: nat)
             (update: m -> md -> reply m md)
             (to_terminate: m -> bool)
             (render: md -> list (ev m))
             (capacity: int) (model: md)
             (sinks: list (ev m)) (fx: list (start m))
                (subs: opt (start m)) (cmd: opt (list (ev m))) (exts: list (ext m))
  : Lemma (ensures (let s = program fuel update to_terminate render capacity model sinks fx subs cmd exts in
                    is_prefix s.trace s.log)) =
  program_inv fuel update to_terminate render capacity model sinks fx subs cmd exts;
  let s = program fuel update to_terminate render capacity model sinks fx subs cmd exts in
  if s.terminated then () else prefix_append s.trace (pending s)

/// **`reentrant_no_loss`.** A `dispatch` made while the latch is set —
/// from `update`'s command, from the post-drain `setState`, from a
/// subscription's start, from an async command that completed
/// synchronously — queues its message at the BACK of what is pending,
/// logs it, and processes nothing: the trace, the model, the paint and
/// everything already waiting are untouched. With `exactly_once`, the
/// message is handed to `update` once the drain reaches it; with
/// `in_order`, after everything accepted before it. It can be neither
/// lost nor moved ahead. This is the lemma that makes
/// `Async.StartImmediate` safe as the async-command start (Phase 851.C):
/// a command whose body runs to a dispatch before yielding dispatches
/// under the latch, and this is what the latch does with it.
let reentrant_no_loss (#m #md: Type0)
                      (fuel: nat)
                      (update: m -> md -> reply m md)
                      (to_terminate: m -> bool)
                      (render: md -> list (ev m))
                      (s: st m md) (msg: m)
  : Lemma (requires inv_core s /\ s.reentered /\ not s.terminated)
          (ensures (let s' = dispatch fuel update to_terminate render s msg in
                    inv_core s'
                    /\ s'.log == append s.log [msg]
                    /\ pending s' == append (pending s) [msg]
                    /\ s'.trace == s.trace /\ s'.model == s.model
                    /\ s'.dirty == s.dirty /\ s'.painted == s.painted /\ s'.renders == s.renders
                    /\ s'.reentered /\ not s'.terminated)) =
  dispatch_latched fuel update to_terminate render s msg;
  enqueue_spec s msg

/// Termination freezes the callee events: nothing they raise changes a
/// terminated state.
let rec terminated_stays_terminated (#m #md: Type0) (s: st m md) (evs: list (ev m))
  : Lemma (requires s.terminated)
          (ensures apply_evs s evs == s)
          (decreases evs) =
  match evs with
  | [] -> ()
  | _ :: rest -> terminated_stays_terminated s rest

/// **`terminated_absorbing`.** Once `terminated`, no event from inside
/// or outside changes what `update` saw, what was accepted, the model,
/// or what was painted — nor, since Phase 871, what was started or is
/// held — and nothing clears the flag. The two guards at the head of
/// `dispatch` and `processMsgs`'s `while` are this lemma.
let rec terminated_absorbing (#m #md: Type0)
                             (fuel: nat)
                             (update: m -> md -> reply m md)
                             (to_terminate: m -> bool)
                             (render: md -> list (ev m))
                             (s: st m md) (exts: list (ext m))
  : Lemma (requires s.terminated)
          (ensures (let s' = run fuel update to_terminate render s exts in
                    s'.terminated /\ s'.trace == s.trace /\ s'.log == s.log
                    /\ s'.model == s.model /\ s'.active == s.active
                    /\ s'.painted == s.painted /\ s'.renders == s.renders
                    /\ s'.started == s.started /\ s'.held == s.held))
          (decreases exts) =
  if s.reentered then ()
  else
    match exts with
    | [] -> ()
    | XDispatch msg :: rest -> terminated_absorbing fuel update to_terminate render s rest
    | XTerminate :: rest -> terminated_absorbing fuel update to_terminate render s rest

/// …and the boot on a machine terminated before it ran: the boot paint
/// still hands the hook the init model — production paints
/// unconditionally at boot — but no effect, no subscription and no
/// command is started (Phase 871: until then all three were), nothing
/// more is held, and the drain pops at most one slot and processes
/// nothing. The lemma is stated over ANY terminated entry state; a sink
/// that calls `Terminate` reaches one inside the boot, which is
/// `terminated_starts_nothing`.
let terminated_absorbing_boot (#m #md: Type0)
                              (fuel: nat)
                              (update: m -> md -> reply m md)
                              (to_terminate: m -> bool)
                              (render: md -> list (ev m))
                              (s: st m md) (sinks: list (ev m)) (fx: list (start m))
                              (subs: opt (start m)) (cmd: opt (list (ev m)))
  : Lemma (requires s.terminated)
          (ensures (let s' = boot fuel update to_terminate render s sinks fx subs cmd in
                    s'.terminated /\ s'.trace == s.trace /\ s'.log == s.log
                    /\ s'.model == s.model /\ s'.active == s.active
                    /\ s'.painted == OSome s.model /\ s'.renders == s.renders + 1
                    /\ s'.started == s.started /\ s'.held == s.held)) =
  let s0 = { s with reentered = true } in
  boot_evs_are_apply_evs fuel update to_terminate render s0 sinks;
  terminated_stays_terminated s0 sinks;
  gated_all_terminated fuel update to_terminate render s0 fx;
  let p = { s0 with dirty = false; painted = OSome s0.model; renders = s0.renders + 1 } in
  terminated_stays_terminated p (render s0.model)

/// **`terminate_then_nothing`.** From ANY idle state, a `Terminate` from
/// outside means nothing after it is ever processed or painted.
let terminate_then_nothing (#m #md: Type0)
                           (fuel: nat)
                           (update: m -> md -> reply m md)
                           (to_terminate: m -> bool)
                           (render: md -> list (ev m))
                           (s: st m md) (exts: list (ext m))
  : Lemma (requires not s.reentered)
          (ensures (let s' = run fuel update to_terminate render s (XTerminate :: exts) in
                    s'.terminated /\ s'.trace == s.trace /\ s'.log == s.log /\ s'.model == s.model
                    /\ s'.painted == s.painted /\ s'.renders == s.renders)) =
  terminated_absorbing fuel update to_terminate render (terminate s) exts

/// A `Terminate` among the events terminates the machine — with or
/// without the invariant.
let rec apply_evs_has_term (#m #md: Type0) (s: st m md) (evs: list (ev m))
  : Lemma (requires has_term evs)
          (ensures (apply_evs s evs).terminated)
          (decreases evs) =
  match evs with
  | Term :: rest -> terminated_stays_terminated (terminate s) rest
  | Msg msg :: rest -> apply_evs_has_term (enqueue s msg) rest

/// **`terminated_starts_nothing`** (Phase 871). A dispatcher-handle sink
/// or an effect-controller sink that calls `Terminate` at boot means the
/// boot makes NO start: no effect's start function runs, no subscription
/// starts and `init`'s command does not run — `started` does not move —
/// and the program holds nothing. The boot paint is still made, once,
/// with the init model: it is unconditional, as a hydrating renderer
/// needs. Until 871 production started all three after the teardown had
/// run, and nothing ever disposed them. `gated_terminated` is the guard
/// this rests on; an effect that terminates from its own start function
/// stops the effects after it by the same guard.
let terminated_starts_nothing (#m #md: Type0)
                              (fuel: nat)
                              (update: m -> md -> reply m md)
                              (to_terminate: m -> bool)
                              (render: md -> list (ev m))
                              (s: st m md) (sinks: list (ev m)) (fx: list (start m))
                              (subs: opt (start m)) (cmd: opt (list (ev m)))
  : Lemma (requires inv_core s /\ has_term sinks)
          (ensures (let s' = boot fuel update to_terminate render s sinks fx subs cmd in
                    s'.terminated /\ s'.started == s.started /\ s'.held == 0
                    /\ s'.trace == s.trace /\ s'.model == s.model
                    /\ s'.painted == OSome s.model /\ s'.renders == s.renders + 1)) =
  let s0 = { s with reentered = true } in
  boot_evs_are_apply_evs fuel update to_terminate render s0 sinks;
  apply_evs_spec s0 sinks;
  apply_evs_has_term s0 sinks;
  let sk = apply_evs s0 sinks in
  gated_all_terminated fuel update to_terminate render sk fx;
  paint_spec render sk

/// **`start_terminating_releases`** (Phase 871). A start whose own events
/// terminate the program — an effect or a subscription that calls
/// `Terminate` from its start function — was made and is counted, and
/// afterwards the program holds nothing: the teardown ran while the start
/// was still running, so what the start then returned is released at
/// once rather than held past the teardown. Until 871 production stored
/// the effect's handle in the registry `DisposeAll` had just emptied, and
/// assigned the subscriptions `Subs.Fx.change` returned to the set the
/// teardown had already stopped.
let start_terminating_releases (#m #md: Type0)
                               (fuel: nat)
                               (update: m -> md -> reply m md)
                               (to_terminate: m -> bool)
                               (render: md -> list (ev m))
                               (s: st m md) (x: start m)
  : Lemma (requires inv_core s /\ s.reentered /\ not s.terminated /\ has_term x.raised)
          (ensures (let s' = gated fuel update to_terminate render s x in
                    s'.terminated /\ s'.held == 0 /\ s'.started == s.started + 1)) =
  let s0 = { s with started = s.started + 1 } in
  boot_evs_are_apply_evs fuel update to_terminate render s0 x.raised;
  apply_evs_spec s0 x.raised;
  apply_evs_has_term s0 x.raised

/// **`terminated_holds_nothing`** (Phase 871). In every state a program
/// reaches, a terminated program holds nothing: every handle the boot's
/// starts acquired has been released — by the teardown, or, for a start
/// that terminated the program itself, as soon as it returned — and no
/// start is made after it. It is the invariant's last clause.
let terminated_holds_nothing (#m #md: Type0)
                             (fuel: nat)
                             (update: m -> md -> reply m md)
                             (to_terminate: m -> bool)
                             (render: md -> list (ev m))
                             (capacity: int) (model: md)
                             (sinks: list (ev m)) (fx: list (start m))
                             (subs: opt (start m)) (cmd: opt (list (ev m))) (exts: list (ext m))
  : Lemma (ensures (let s = program fuel update to_terminate render capacity model sinks fx subs cmd exts in
                    s.terminated ==> s.held == 0)) =
  program_inv fuel update to_terminate render capacity model sinks fx subs cmd exts

/// **`boot_drain_equiv`.** The boot drain IS the steady-state critical
/// section, run over everything the boot does under the latch before its
/// drain: the sinks' events, the effects' gated starts, the boot paint,
/// the gated subscription start and the gated command. The
/// hand-duplicated
/// `reentered <- true; …; paint; …; processMsgs (); reentered <- false`
/// and `dispatch`'s are one function.
let boot_drain_equiv (#m #md: Type0)
                     (fuel: nat)
                     (update: m -> md -> reply m md)
                     (to_terminate: m -> bool)
                     (render: md -> list (ev m))
                     (s: st m md) (sinks: list (ev m)) (fx: list (start m))
                     (subs: opt (start m)) (cmd: opt (list (ev m)))
  : Lemma (ensures boot fuel update to_terminate render s sinks fx subs cmd
                   == critical fuel update to_terminate render
                        (gated_opt fuel update to_terminate render
                           (gated_opt fuel update to_terminate render
                              (paint render
                                 (gated_all fuel update to_terminate render
                                    (apply_evs { s with reentered = true } sinks) fx))
                              subs)
                           (command cmd))) =
  let s0 = { s with reentered = true } in
  boot_evs_are_apply_evs fuel update to_terminate render s0 sinks;
  apply_evs_reentered s0 sinks;
  let sk = apply_evs s0 sinks in
  gated_all_reentered fuel update to_terminate render sk fx;
  let sp = gated_all fuel update to_terminate render sk fx in
  paint_reentered render sp;
  let s1 = paint render sp in
  gated_opt_reentered fuel update to_terminate render s1 subs;
  let s2 = gated_opt fuel update to_terminate render s1 subs in
  gated_opt_reentered fuel update to_terminate render s2 (command cmd)

/// The state after the boot paint alone, with the latch released — what
/// a program that dispatched nothing from `init` looks like the moment
/// `runWithDispatch` returns.
[@@ noextract_to "FSharp"]
let booted (#m #md: Type0) (render: md -> list (ev m)) (s: st m md) : st m md =
  { paint render { s with reentered = true } with reentered = false }

/// **`boot_single_is_dispatch`.** For one message raised by `init`'s
/// command, and no sink, effect or subscription, the boot drain is the
/// boot paint, the command's start counted, and then `dispatch` itself,
/// from any idle non-terminated state the paint does not terminate.
let boot_single_is_dispatch (#m #md: Type0)
                            (fuel: nat)
                            (update: m -> md -> reply m md)
                            (to_terminate: m -> bool)
                            (render: md -> list (ev m))
                            (s: st m md) (msg: m)
  : Lemma (requires not s.reentered /\ not s.terminated /\ not (booted render s).terminated)
          (ensures boot fuel update to_terminate render s [] [] ONone (OSome [Msg msg])
                   == dispatch fuel update to_terminate render
                        ({ booted render s with started = (booted render s).started + 1 }) msg) =
  let s0 = { s with reentered = true } in
  paint_reentered render s0;
  let s1 = paint render s0 in
  boot_evs_are_apply_evs fuel update to_terminate render { s1 with started = s1.started + 1 } [Msg msg]

/// **`active_iff_not_terminated`.** In every state a program reaches,
/// `DispatcherCore.active` is the loop's `terminated` negated. Both
/// sites that set `terminated` call `MarkTerminated`, and `Wire` set
/// `active` before anything could dispatch; the model carries the two
/// cells in lockstep and this says the lockstep is an invariant, not a
/// coincidence of the paths tried.
let active_iff_not_terminated (#m #md: Type0)
                              (fuel: nat)
                              (update: m -> md -> reply m md)
                              (to_terminate: m -> bool)
                              (render: md -> list (ev m))
                              (capacity: int) (model: md)
                              (sinks: list (ev m)) (fx: list (start m))
                (subs: opt (start m)) (cmd: opt (list (ev m))) (exts: list (ext m))
  : Lemma (ensures (let s = program fuel update to_terminate render capacity model sinks fx subs cmd exts in
                    s.active = not s.terminated)) =
  program_inv fuel update to_terminate render capacity model sinks fx subs cmd exts

/// **`fallback_breaks_encoding`.** The one arm that drives the two
/// flags apart, computed: `Dispatcher.fs`'s fallback for an unwired
/// callback clears `active` and leaves `terminated` — a state in which
/// `IDispatcher.Dispatch` refuses while the loop would still process.
/// The runtime never wires the interface without the callback; this
/// names the state that arm reaches so the encoding's exception is a
/// theorem too.
let fallback_breaks_encoding (#m #md: Type0) (s: st m md)
  : Lemma (requires not s.terminated)
          (ensures (let s' = fallback_terminate s in not s'.active /\ not s'.terminated)) = ()

/// **`painted_is_model`.** In every idle, non-terminated state a program
/// reaches, nothing is left unpainted and the model on screen IS the
/// model. This is what rendering once at the END of the drain has to
/// establish that rendering after every `update` got for free: the last
/// paint saw the last model.
let painted_is_model (#m #md: Type0)
                     (fuel: nat)
                     (update: m -> md -> reply m md)
                     (to_terminate: m -> bool)
                     (render: md -> list (ev m))
                     (capacity: int) (model: md)
                     (sinks: list (ev m)) (fx: list (start m))
                (subs: opt (start m)) (cmd: opt (list (ev m))) (exts: list (ext m))
  : Lemma (ensures (let s = program fuel update to_terminate render capacity model sinks fx subs cmd exts in
                    not s.reentered /\ not s.terminated ==> not s.dirty /\ s.painted == OSome s.model)) =
  program_inv fuel update to_terminate render capacity model sinks fx subs cmd exts

/// Under a quiet render the loop paints at most once, as its last act:
/// a paint is made only on an empty ring, a quiet hook leaves it empty,
/// and the next pop exits. So a loop that exits non-terminated painted
/// exactly once if it had anything to paint — the model was dirty on
/// entry, or its first message was processed — and a loop that
/// terminated painted nothing.
let rec loop_quiet (#m #md: Type0)
                   (fuel: nat)
                   (update: m -> md -> reply m md)
                   (to_terminate: m -> bool)
                   (render: md -> list (ev m))
                   (s: st m md) (next: opt (slot m))
  : Lemma (requires loop_inv s next /\ quiet render)
          (ensures (let Pair s' finished = loop fuel update to_terminate render s next in
                    (s.terminated ==> s'.terminated)
                    /\ (terminates to_terminate next ==> s'.terminated)
                    /\ (s'.terminated ==> s'.renders == s.renders)
                    /\ (finished /\ not s'.terminated ==>
                          s'.renders == s.renders + (if s.dirty || processes to_terminate next then 1 else 0))))
          (decreases fuel) =
  if s.terminated then ()
  else
    match next with
    | ONone ->
        if not s.dirty then ()
        else if fuel = 0 then ()
        else begin
          paint_spec render s;
          let s' = paint render s in
          pop_loop_inv s';
          let Pair r' next' = pop s'.ring in
          loop_quiet (fuel - 1) update to_terminate render { s' with ring = r' } next'
        end
    | OSome Placeholder -> ()
    | OSome (Written msg) ->
        step_spec update to_terminate msg s;
        let s' = step update to_terminate msg s in
        if fuel = 0 then ()
        else begin
          pop_loop_inv s';
          let Pair r' next' = pop s'.ring in
          loop_quiet (fuel - 1) update to_terminate render { s' with ring = r' } next'
        end

/// **`render_once_per_drain`.** From an idle, non-terminated state, a
/// `dispatch` that finishes without terminating hands the render hook
/// the model EXACTLY ONCE — however many messages the drain processed,
/// however many the commands re-dispatched — and a `dispatch` that
/// terminates hands it nothing. CONDITIONAL on `quiet render`: the
/// React adapter's `setState` dispatches nothing synchronously, and
/// that is the hook this theorem is about. A hook that dispatches
/// re-dirties the model, and the drain paints again once the ring is
/// empty; `painted_is_model` and `exactly_once` hold of that machine
/// too, and the differential scripts it.
let render_once_per_drain (#m #md: Type0)
                          (fuel: nat)
                          (update: m -> md -> reply m md)
                          (to_terminate: m -> bool)
                          (render: md -> list (ev m))
                          (s: st m md) (msg: m)
  : Lemma (requires inv s /\ not s.reentered /\ not s.terminated /\ quiet render)
          (ensures (let s' = dispatch fuel update to_terminate render s msg in
                    (not s'.reentered /\ not s'.terminated ==> s'.renders == s.renders + 1)
                    /\ (s'.terminated ==> s'.renders == s.renders))) =
  enqueue_spec s msg;
  push_spec msg s.ring;
  let s1 = { enqueue s msg with reentered = true } in
  // Idle and not terminated: the ring was empty, so the push made it
  // exactly [msg] and the pop hands the loop that message.
  assert (unread s.ring == []);
  assert (unread s1.ring == [Written msg]);
  pop_loop_inv s1;
  pop_spec s1.ring;
  let Pair r next = pop s1.ring in
  assert (next == OSome (Written msg));
  assert (not s.dirty);
  // `loop_spec` for the latch (a drain that ran out of fuel leaves it
  // set, so the result is not idle); `loop_quiet` for the count.
  loop_spec fuel update to_terminate render { s1 with ring = r } next;
  loop_quiet fuel update to_terminate render { s1 with ring = r } next

(* ───────────────────────────────────────────────────────────────────
   A message's diff that terminates mid-way (Phase 900).
   ─────────────────────────────────────────────────────────────────── *)

/// No start in the list raises a `Terminate`.
[@@ noextract_to "FSharp"]
let rec no_term (#m: Type0) (xs: list (start m)) : Tot bool (decreases xs) =
  match xs with
  | [] -> true
  | x :: rest -> not (has_term x.raised) && no_term rest

/// Events with no `Terminate` among them leave a running machine running
/// and count no start.
let rec apply_evs_no_term (#m #md: Type0) (s: st m md) (evs: list (ev m))
  : Lemma (requires not s.terminated /\ not (has_term evs))
          (ensures (let s' = apply_evs s evs in not s'.terminated /\ s'.started == s.started))
          (decreases evs) =
  match evs with
  | [] -> ()
  | Msg msg :: rest -> apply_evs_no_term (enqueue s msg) rest
  | Term :: _ -> ()

/// Events with a `Terminate` among them terminate a running machine and
/// leave it holding nothing: the teardown released everything, and every
/// event after it is absorbed.
let rec apply_evs_term_held (#m #md: Type0) (s: st m md) (evs: list (ev m))
  : Lemma (requires not s.terminated /\ has_term evs)
          (ensures (let s' = apply_evs s evs in s'.terminated /\ s'.held == 0 /\ s'.started == s.started))
          (decreases evs) =
  match evs with
  | [] -> ()
  | Msg msg :: rest -> apply_evs_term_held (enqueue s msg) rest
  | Term :: rest -> terminated_stays_terminated (terminate s) rest

/// **`start_one_terminated`** (Phase 900). The gate in `Subs.Fx.change`:
/// a subscription the diff reaches once the program is terminated is not
/// started, and the state is unchanged. `start_all_terminated` is the
/// rest of the diff after a start that terminated it.
let start_one_terminated (#m #md: Type0) (s: st m md) (x: start m)
  : Lemma (requires s.terminated) (ensures start_one s x == s) = ()

let rec start_all_terminated (#m #md: Type0) (s: st m md) (xs: list (start m))
  : Lemma (requires s.terminated) (ensures start_all s xs == s) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> start_all_terminated s rest

/// **`start_one_terminating`** (Phase 900). A start in a message's diff
/// whose own events terminate the program is made and counted, and the
/// machine afterwards holds nothing: what the start returned is stopped
/// as soon as the diff returns rather than held past the teardown.
let start_one_terminating (#m #md: Type0) (s: st m md) (x: start m)
  : Lemma (requires not s.terminated /\ has_term x.raised)
          (ensures (let s' = start_one s x in
                    s'.terminated /\ s'.held == 0 /\ s'.started == s.started + 1)) =
  apply_evs_term_held { s with started = s.started + 1 } x.raised

/// Starts none of which terminates the program are all made, in order.
let rec start_all_no_term (#m #md: Type0) (s: st m md) (xs: list (start m))
  : Lemma (requires not s.terminated /\ no_term xs)
          (ensures (let s' = start_all s xs in not s'.terminated /\ s'.started == s.started + length xs))
          (decreases xs) =
  match xs with
  | [] -> ()
  | x :: rest ->
      apply_evs_no_term { s with started = s.started + 1 } x.raised;
      start_all_no_term (start_one s x) rest

let rec start_all_append (#m #md: Type0) (s: st m md) (xs ys: list (start m))
  : Lemma (ensures start_all s (append xs ys) == start_all (start_all s xs) ys) (decreases xs) =
  match xs with
  | [] -> ()
  | x :: rest -> start_all_append (start_one s x) rest ys

/// **`diff_terminating_starts_nothing_after`** (Phase 900) — the
/// headline. A message whose diff starts `before`, then a subscription
/// `x` that calls `Terminate` from its start function, then `after`:
/// exactly the starts up to and including `x` are made (`before` in
/// full, because none of them terminates; `x`; and none of `after`), the
/// message's command does not run (it would have counted), the machine
/// is terminated and holds nothing — what `before` and `x` returned was
/// stopped as soon as the diff returned — and the new model is still
/// assigned, as a `Terminate` from a command leaves it. Until 900
/// production started `after` too, held all of it past the teardown, and
/// ran the command; the go-red is the step before 900, which applies the
/// flattened events and fails this lemma on the start count.
let diff_terminating_starts_nothing_after (#m #md: Type0)
                                          (update: m -> md -> reply m md)
                                          (to_terminate: m -> bool)
                                          (msg: m) (s: st m md)
                                          (before: list (start m)) (x: start m) (after: list (start m))
  : Lemma (requires not s.terminated /\ not (to_terminate msg)
                    /\ (update msg s.model).starts == append before (x :: after)
                    /\ no_term before /\ has_term x.raised)
          (ensures (let s' = step update to_terminate msg s in
                    s'.terminated /\ s'.held == 0
                    /\ s'.started == s.started + length before + 1
                    /\ s'.model == (update msg s.model).next)) =
  let r = update msg s.model in
  let s1 = { s with trace = append s.trace [msg] } in
  start_all_append s1 before (x :: after);
  start_all_no_term s1 before;
  let s2 = start_all s1 before in
  start_one_terminating s2 x;
  let s3 = start_one s2 x in
  start_all_terminated s3 after;
  match command r.cmd with
  | ONone -> ()
  | OSome c -> start_one_terminated s3 c
