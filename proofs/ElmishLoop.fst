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
/// dispatch order, with termination absorbing.
///
/// # What this module is
///
/// A hand-written model of `Program.runWithDispatch`
/// (`src/ToolUp.Platform.Client/Client/Elmish/Program.fs`), clause for
/// clause: `dispatch`, `processMsgs`, the terminate callback, and the
/// boot drain after `init`. The loop is a scheduling skeleton over four
/// mutable cells — the ring, the `reentered` latch, the `terminated`
/// latch and the model — and its transitions depend on the impure
/// callees (`update`, `setState`, `Subs.Fx.change`, `Cmd.exec`) only
/// through WHICH MESSAGES THEY SYNCHRONOUSLY RE-DISPATCH and whether
/// they call `Terminate`. Those callees are therefore abstracted as ONE
/// oracle, `update : msg -> model -> pair model (list ev)`: the new
/// model and, in order, the events the callees raise before control
/// returns to the loop. Under that abstraction the loop is a total,
/// deterministic step function over the four cells, and this module is
/// that function together with the laws the runtime relies on and
/// nothing tests.
///
/// Phase 788 proved the ring a FIFO queue (`ElmishRing.fst`); this
/// module IMPORTS it — `ring`, `push`, `pop`, `wf`, `unread`,
/// `push_spec`, `pop_spec`, `create_wf` — and adds nothing about the
/// ring. The proof here is about the two latches.
///
/// Every definition names its F# counterpart in the comment above it.
/// The differential host (`ElmishLoopProofOracleTests.fs` on .NET) runs
/// the EXTRACTION of this module beside the production loop with a
/// scripted `update` that re-dispatches chosen messages at chosen
/// points — from `update`'s command, from the boot drain, after
/// `Terminate` — and requires the two to hand `update` the same messages
/// in the same order, and to end on the same model. That host is the
/// only thing that says this model is about the code that ships.
///
/// # The representation
///
/// The state is the four cells plus two observables the differential
/// compares: `trace`, the messages `update` has been handed, in order;
/// and `log`, the messages `dispatch` ACCEPTED (pushed onto the ring),
/// in order — external and reentrant alike. `active` is
/// `DispatcherCore.active`, carried so the two-flag encoding is a
/// theorem over the model rather than a comment.
///
/// `processMsgs` is a `while` loop whose exit depends on the oracle
/// eventually re-dispatching nothing — which nothing guarantees, and
/// which production does not guarantee either (an `update` that always
/// re-dispatches never returns). The model bounds it with `fuel`, one
/// unit per processed message, and REPORTS exhaustion rather than
/// hiding it: the latch is left set when the drain did not finish, so a
/// stalled machine looks exactly like production mid-drain, and `run`
/// stops feeding it external events, because a single-threaded host
/// cannot deliver any while a synchronous drain has not returned. Every
/// theorem below is partial correctness: what holds of every state the
/// machine reaches, whether or not the drain finished. Liveness of the
/// drain is not claimed.
///
/// # The theorems
///
/// `exactly_once`: in every idle, non-terminated state the machine can
/// reach, `log == trace` — every accepted message was handed to `update`
/// once and only once, and in the order it was accepted. `in_order`:
/// in EVERY reachable state, `trace` is a prefix of `log` — termination
/// can truncate, never permute. `reentrant_no_loss`: a `dispatch` made
/// while the latch is set queues its message at the back and processes
/// nothing, so the message is neither lost nor moved ahead of what was
/// already waiting. `terminated_absorbing`: once `terminated`, no
/// operation changes `trace`, `log` or the model, and nothing clears
/// the flag. `boot_drain_equiv`: the boot drain, which duplicates the
/// steady-state critical section by hand, IS that critical section over
/// the events `init`'s effects raise — and for a single message is
/// literally `dispatch`. `active_iff_not_terminated`: in every reachable
/// state `active = not terminated`; `fallback_breaks_encoding` computes
/// the one production arm that drives them apart.
///
/// # What is abstracted, and stated as such
///
/// An exception from a callee is an oracle reply whose model is the old
/// one (production leaves `state` unassigned) carrying whatever was
/// dispatched before the throw. The teardown callbacks on the
/// terminating path (`Subs.Fx.stop`, `terminate`) are assumed not to
/// dispatch; a dispatch they made would land in the ring and never be
/// processed, which is what `terminated_absorbing` says of any message
/// after the flag. The `DispatchAsync` post-await recheck is an
/// interleaving and is NOT modelled — this is the synchronous machine.

module ElmishLoop

open ElmishRing

(* ───────────────────────────────────────────────────────────────────
   Events — what the callees can do to the loop, and what the outside
   world can.
   ─────────────────────────────────────────────────────────────────── *)

/// A synchronous event raised from INSIDE the loop, while one message is
/// being processed: `dispatch' msg` from `setState`, a subscription's
/// start, or a command; or `IDispatcher.Terminate ()` from any of them.
type ev (m: Type0) =
  | Msg : msg: m -> ev m
  | Term : ev m

/// An event from OUTSIDE the loop, between drains: a dispatch from a
/// timer, a socket, React, or HMR; or `IDispatcher.Terminate ()`.
type ext (m: Type0) =
  | XDispatch : msg: m -> ext m
  | XTerminate : ext m

(* ───────────────────────────────────────────────────────────────────
   The state — the four cells, the dispatcher's flag, two observables.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `rb`, `reentered`, `terminated`, `dispatcherCore.active`, `state`;
/// `trace` and `log` are the differential's observables (see the header).
noeq type st (m: Type0) (md: Type0) = {
  ring: ring m;
  reentered: bool;
  terminated: bool;
  active: bool;
  model: md;
  trace: list m;
  log: list m;
}

/// The state after `init` returned and `dispatcherCore.Wire dispatch'`
/// ran — the first moment anything can dispatch. `Wire` is what sets
/// `active`; before it nothing holds a reference to `dispatch`.
let initial (#m #md: Type0) (capacity: int) (model: md) : st m md = {
  ring = create capacity;
  reentered = false;
  terminated = false;
  active = true;
  model = model;
  trace = [];
  log = [];
}

(* ───────────────────────────────────────────────────────────────────
   The primitive transitions.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `dispatch msg` while `reentered` — `if not terminated then rb.Push msg`
/// and nothing else, because the latch is set. The push is logged.
let enqueue (#m #md: Type0) (s: st m md) (msg: m) : st m md =
  if s.terminated then s
  else { s with ring = push msg s.ring; log = append s.log [msg] }

/// F#: the terminate callback `SetTerminateCallback` installs —
/// `if not terminated then … terminated <- true; dispatcherCore.MarkTerminated ()`
/// — and the flag half of the `toTerminate msg` branch, which runs the
/// same two assignments. The teardown calls between them are abstracted
/// (see the header).
let terminate (#m #md: Type0) (s: st m md) : st m md =
  if s.terminated then s
  else { s with terminated = true; active = false }

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

/// F#: the body of `processMsgs`'s `while` — one popped message.
/// `toTerminate msg` takes the teardown branch; otherwise `update`,
/// `setState`, `Subs.Fx.change` and `Cmd.exec` run (the oracle: the
/// message is handed to `update` — that is the `trace` append — and the
/// events they raise are applied in order), and only THEN is
/// `state <- model'` assigned, which is why a `Terminate` raised from a
/// command still leaves the new model in place.
let step (#m #md: Type0)
         (update: m -> md -> pair md (list (ev m)))
         (to_terminate: m -> bool)
         (msg: m) (s: st m md) : st m md =
  if to_terminate msg then terminate s
  else
    let Pair model' evs = update msg s.model in
    let s1 = { s with trace = append s.trace [msg] } in
    let s2 = apply_evs s1 evs in
    { s2 with model = model' }

(* ───────────────────────────────────────────────────────────────────
   The drain.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `while not terminated && Option.isSome nextMsg do … nextMsg <- rb.Pop()`.
/// `next` is `nextMsg`, popped BEFORE the flag is examined — so a
/// message popped in the same iteration that terminates is dropped,
/// exactly as production drops it. The second component is whether the
/// loop EXITED (on the flag or on an empty ring) rather than ran out of
/// fuel; the fuel is spent after a message is processed and before the
/// next pop, so a stalled machine has processed everything it popped.
/// `OSome Placeholder` is `nextMsg.Value` being `Unchecked.defaultof`,
/// which `placeholder_unobserved` proves a well-formed ring never pops;
/// the arm exists because the function is total.
let rec loop (#m #md: Type0)
             (fuel: nat)
             (update: m -> md -> pair md (list (ev m)))
             (to_terminate: m -> bool)
             (s: st m md) (next: opt (slot m))
  : Tot (pair (st m md) bool) (decreases fuel) =
  if s.terminated then Pair s true
  else
    match next with
    | ONone -> Pair s true
    | OSome Placeholder -> Pair s true
    | OSome (Written msg) ->
        let s' = step update to_terminate msg s in
        if fuel = 0 then Pair s' false
        else
          let Pair r' next' = pop s'.ring in
          loop (fuel - 1) update to_terminate { s' with ring = r' } next'

/// F#: `processMsgs ()` — `let mutable nextMsg = rb.Pop()` then the loop.
let process_msgs (#m #md: Type0)
                 (fuel: nat)
                 (update: m -> md -> pair md (list (ev m)))
                 (to_terminate: m -> bool)
                 (s: st m md) : pair (st m md) bool =
  let Pair r next = pop s.ring in
  loop fuel update to_terminate { s with ring = r } next

/// F#: `reentered <- true; processMsgs (); reentered <- false` — the
/// critical section `dispatch` runs when the latch was clear. The latch
/// is cleared only when the drain exited; a stalled drain leaves it set,
/// which is where production would be too.
let critical (#m #md: Type0)
             (fuel: nat)
             (update: m -> md -> pair md (list (ev m)))
             (to_terminate: m -> bool)
             (s: st m md) : st m md =
  let Pair s' finished = process_msgs fuel update to_terminate { s with reentered = true } in
  if finished then { s' with reentered = false } else s'

/// F#: `dispatch msg`.
let dispatch (#m #md: Type0)
             (fuel: nat)
             (update: m -> md -> pair md (list (ev m)))
             (to_terminate: m -> bool)
             (s: st m md) (msg: m) : st m md =
  if s.terminated then s
  else
    let s1 = { s with ring = push msg s.ring; log = append s.log [msg] } in
    if s1.reentered then s1
    else critical fuel update to_terminate s1

(* ───────────────────────────────────────────────────────────────────
   The boot drain — transcribed, not derived. The equivalence is the
   theorem `boot_drain_equiv` below.
   ─────────────────────────────────────────────────────────────────── *)

/// One event `init`'s effects raise during the boot drain — a dispatch
/// goes through `dispatch'` itself (the latch is set, so it enqueues),
/// a `Terminate` through the callback.
let boot_ev (#m #md: Type0)
            (fuel: nat)
            (update: m -> md -> pair md (list (ev m)))
            (to_terminate: m -> bool)
            (s: st m md) (e: ev m) : st m md =
  match e with
  | Msg msg -> dispatch fuel update to_terminate s msg
  | Term -> terminate s

let rec boot_evs (#m #md: Type0)
                 (fuel: nat)
                 (update: m -> md -> pair md (list (ev m)))
                 (to_terminate: m -> bool)
                 (s: st m md) (evs: list (ev m)) : Tot (st m md) (decreases evs) =
  match evs with
  | [] -> s
  | e :: rest -> boot_evs fuel update to_terminate (boot_ev fuel update to_terminate s e) rest

/// F#: the tail of `runWithDispatch` — `reentered <- true`, then
/// `setState model dispatch'`, `Subs.Fx.change … dispatch'` and
/// `Cmd.exec … dispatch' cmd` (the events `init`'s effects raise, each
/// through `dispatch'`), then `processMsgs ()`, then `reentered <- false`.
let boot (#m #md: Type0)
         (fuel: nat)
         (update: m -> md -> pair md (list (ev m)))
         (to_terminate: m -> bool)
         (s: st m md) (evs: list (ev m)) : st m md =
  let s1 = { s with reentered = true } in
  let s2 = boot_evs fuel update to_terminate s1 evs in
  let Pair s3 finished = process_msgs fuel update to_terminate s2 in
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
            (update: m -> md -> pair md (list (ev m)))
            (to_terminate: m -> bool)
            (s: st m md) (exts: list (ext m)) : Tot (st m md) (decreases exts) =
  if s.reentered then s
  else
    match exts with
    | [] -> s
    | XDispatch msg :: rest -> run fuel update to_terminate (dispatch fuel update to_terminate s msg) rest
    | XTerminate :: rest -> run fuel update to_terminate (terminate s) rest

/// A whole program: `init` (its model and the events its effects raise),
/// the boot drain, then the outside world.
let program (#m #md: Type0)
            (fuel: nat)
            (update: m -> md -> pair md (list (ev m)))
            (to_terminate: m -> bool)
            (capacity: int) (model: md) (init_evs: list (ev m)) (exts: list (ext m)) : st m md =
  run fuel update to_terminate (boot fuel update to_terminate (initial capacity model) init_evs) exts

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

/// The core invariant — true of every state the machine passes through,
/// latched or idle. The ring is well-formed; the dispatcher's flag is
/// the loop's flag negated; while not terminated the log is exactly the
/// trace followed by what is pending, and once terminated the trace is
/// a prefix of the log and both are frozen.
[@@ noextract_to "FSharp"]
let inv_core (#m #md: Type0) (s: st m md) : prop =
  wf s.ring
  /\ s.active = not s.terminated
  /\ (not s.terminated ==> s.log == append s.trace (pending s))
  /\ (s.terminated ==> is_prefix s.trace s.log)

/// The invariant at the boundary of every operation: the core, and an
/// idle non-terminated machine has drained its ring.
[@@ noextract_to "FSharp"]
let inv (#m #md: Type0) (s: st m md) : prop =
  inv_core s /\ (not s.reentered /\ not s.terminated ==> unread s.ring == [])

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
/// of what is pending, logs it, and touches nothing else.
let enqueue_spec (#m #md: Type0) (s: st m md) (msg: m)
  : Lemma (requires inv_core s)
          (ensures (let s' = enqueue s msg in
                    inv_core s'
                    /\ s'.reentered == s.reentered /\ s'.terminated == s.terminated
                    /\ s'.active == s.active /\ s'.trace == s.trace /\ s'.model == s.model
                    /\ (not s.terminated ==>
                          s'.log == append s.log [msg] /\ pending s' == append (pending s) [msg]))) =
  if s.terminated then ()
  else begin
    push_spec msg s.ring;
    msgs_append (unread s.ring) [Written msg];
    append_assoc s.trace (pending s) [msg]
  end

/// **`terminate_spec`.** Termination sets both flags, freezes trace and
/// log, and is idempotent.
let terminate_spec (#m #md: Type0) (s: st m md)
  : Lemma (requires inv_core s)
          (ensures (let s' = terminate s in
                    inv_core s'
                    /\ s'.terminated /\ not s'.active
                    /\ s'.reentered == s.reentered /\ s'.trace == s.trace
                    /\ s'.log == s.log /\ s'.model == s.model /\ s'.ring == s.ring)) =
  if s.terminated then ()
  else prefix_append s.trace (pending s)

let rec apply_evs_spec (#m #md: Type0) (s: st m md) (evs: list (ev m))
  : Lemma (requires inv_core s)
          (ensures (let s' = apply_evs s evs in
                    inv_core s'
                    /\ s'.reentered == s.reentered /\ s'.trace == s.trace /\ s'.model == s.model
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

/// **`step_spec`.** Processing a popped message — one the log holds at
/// the head of what is pending — restores the core invariant: the
/// message moves from pending to trace, and every event the callees
/// raised extends both log and pending in lockstep.
let step_spec (#m #md: Type0)
              (update: m -> md -> pair md (list (ev m)))
              (to_terminate: m -> bool)
              (msg: m) (s: st m md)
  : Lemma (requires wf s.ring /\ s.active = not s.terminated /\ not s.terminated
                    /\ s.log == append s.trace (msg :: pending s))
          (ensures (let s' = step update to_terminate msg s in
                    inv_core s' /\ s'.reentered == s.reentered)) =
  if to_terminate msg then prefix_append s.trace (msg :: pending s)
  else begin
    let Pair _ evs = update msg s.model in
    let s1 = { s with trace = append s.trace [msg] } in
    append_assoc s.trace [msg] (pending s);
    apply_evs_spec s1 evs
  end

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
                  (update: m -> md -> pair md (list (ev m)))
                  (to_terminate: m -> bool)
                  (s: st m md) (next: opt (slot m))
  : Lemma (requires loop_inv s next)
          (ensures (let Pair s' finished = loop fuel update to_terminate s next in
                    inv_core s' /\ s'.reentered
                    /\ (finished /\ not s'.terminated ==> unread s'.ring == [])))
          (decreases fuel) =
  if s.terminated then ()
  else
    match next with
    | ONone -> ()
    | OSome Placeholder -> ()
    | OSome (Written msg) ->
        step_spec update to_terminate msg s;
        let s' = step update to_terminate msg s in
        if fuel = 0 then ()
        else begin
          pop_loop_inv s';
          let Pair r' next' = pop s'.ring in
          loop_spec (fuel - 1) update to_terminate { s' with ring = r' } next'
        end

let process_msgs_spec (#m #md: Type0)
                      (fuel: nat)
                      (update: m -> md -> pair md (list (ev m)))
                      (to_terminate: m -> bool)
                      (s: st m md)
  : Lemma (requires inv_core s /\ s.reentered)
          (ensures (let Pair s' finished = process_msgs fuel update to_terminate s in
                    inv_core s' /\ s'.reentered
                    /\ (finished /\ not s'.terminated ==> unread s'.ring == []))) =
  pop_loop_inv s;
  let Pair r next = pop s.ring in
  loop_spec fuel update to_terminate { s with ring = r } next

let critical_spec (#m #md: Type0)
                  (fuel: nat)
                  (update: m -> md -> pair md (list (ev m)))
                  (to_terminate: m -> bool)
                  (s: st m md)
  : Lemma (requires inv_core s)
          (ensures inv (critical fuel update to_terminate s)) =
  process_msgs_spec fuel update to_terminate { s with reentered = true }

/// **`dispatch_inv`.** `dispatch` preserves the invariant from any
/// state that satisfies it — idle or latched, terminated or not.
let dispatch_inv (#m #md: Type0)
                 (fuel: nat)
                 (update: m -> md -> pair md (list (ev m)))
                 (to_terminate: m -> bool)
                 (s: st m md) (msg: m)
  : Lemma (requires inv s)
          (ensures inv (dispatch fuel update to_terminate s msg)) =
  if s.terminated then ()
  else begin
    enqueue_spec s msg;
    let s1 = enqueue s msg in
    if s1.reentered then ()
    else critical_spec fuel update to_terminate s1
  end

let terminate_inv (#m #md: Type0) (s: st m md)
  : Lemma (requires inv s) (ensures inv (terminate s)) =
  terminate_spec s

/// **`dispatch_latched`.** Under the latch, `dispatch` IS `enqueue` —
/// the clause-for-clause transcription and the primitive agree.
let dispatch_latched (#m #md: Type0)
                     (fuel: nat)
                     (update: m -> md -> pair md (list (ev m)))
                     (to_terminate: m -> bool)
                     (s: st m md) (msg: m)
  : Lemma (requires s.reentered)
          (ensures dispatch fuel update to_terminate s msg == enqueue s msg) = ()

let rec boot_evs_are_apply_evs (#m #md: Type0)
                               (fuel: nat)
                               (update: m -> md -> pair md (list (ev m)))
                               (to_terminate: m -> bool)
                               (s: st m md) (evs: list (ev m))
  : Lemma (requires s.reentered)
          (ensures boot_evs fuel update to_terminate s evs == apply_evs s evs)
          (decreases evs) =
  match evs with
  | [] -> ()
  | Msg msg :: rest ->
      dispatch_latched fuel update to_terminate s msg;
      boot_evs_are_apply_evs fuel update to_terminate (enqueue s msg) rest
  | Term :: rest ->
      boot_evs_are_apply_evs fuel update to_terminate (terminate s) rest

/// **`boot_inv`.** The boot drain preserves the invariant.
let boot_inv (#m #md: Type0)
             (fuel: nat)
             (update: m -> md -> pair md (list (ev m)))
             (to_terminate: m -> bool)
             (s: st m md) (evs: list (ev m))
  : Lemma (requires inv_core s)
          (ensures inv (boot fuel update to_terminate s evs)) =
  let s1 = { s with reentered = true } in
  boot_evs_are_apply_evs fuel update to_terminate s1 evs;
  apply_evs_spec s1 evs;
  process_msgs_spec fuel update to_terminate (apply_evs s1 evs)

/// **`run_inv`.** The outside world preserves the invariant.
let rec run_inv (#m #md: Type0)
                (fuel: nat)
                (update: m -> md -> pair md (list (ev m)))
                (to_terminate: m -> bool)
                (s: st m md) (exts: list (ext m))
  : Lemma (requires inv s)
          (ensures inv (run fuel update to_terminate s exts))
          (decreases exts) =
  if s.reentered then ()
  else
    match exts with
    | [] -> ()
    | XDispatch msg :: rest ->
        dispatch_inv fuel update to_terminate s msg;
        run_inv fuel update to_terminate (dispatch fuel update to_terminate s msg) rest
    | XTerminate :: rest ->
        terminate_inv s;
        run_inv fuel update to_terminate (terminate s) rest

/// **`initial_inv`.** The state after `Wire` satisfies the invariant,
/// whatever capacity was asked for.
let initial_inv (#m #md: Type0) (capacity: int) (model: md)
  : Lemma (ensures inv (initial #m capacity model)) =
  create_wf #m capacity

/// **`program_inv`.** Every state a program reaches satisfies the
/// invariant.
let program_inv (#m #md: Type0)
                (fuel: nat)
                (update: m -> md -> pair md (list (ev m)))
                (to_terminate: m -> bool)
                (capacity: int) (model: md) (init_evs: list (ev m)) (exts: list (ext m))
  : Lemma (ensures inv (program fuel update to_terminate capacity model init_evs exts)) =
  initial_inv #m capacity model;
  boot_inv fuel update to_terminate (initial capacity model) init_evs;
  run_inv fuel update to_terminate (boot fuel update to_terminate (initial capacity model) init_evs) exts

(* ───────────────────────────────────────────────────────────────────
   The theorems — the phase's six, as named corollaries.
   ─────────────────────────────────────────────────────────────────── *)

/// **`exactly_once`.** In every idle, non-terminated state a program
/// reaches, the log IS the trace: every message `dispatch` accepted —
/// from outside or re-dispatched from inside — was handed to `update`
/// exactly once, and in the order it was accepted.
let exactly_once (#m #md: Type0)
                 (fuel: nat)
                 (update: m -> md -> pair md (list (ev m)))
                 (to_terminate: m -> bool)
                 (capacity: int) (model: md) (init_evs: list (ev m)) (exts: list (ext m))
  : Lemma (ensures (let s = program fuel update to_terminate capacity model init_evs exts in
                    not s.reentered /\ not s.terminated ==> s.log == s.trace)) =
  program_inv fuel update to_terminate capacity model init_evs exts;
  let s = program fuel update to_terminate capacity model init_evs exts in
  if not s.reentered && not s.terminated then append_nil s.trace

/// **`in_order`.** In EVERY state a program reaches — mid-drain,
/// stalled, or terminated — the trace is a prefix of the log. Nothing
/// is ever handed to `update` out of the order in which it was accepted;
/// termination can only truncate.
let in_order (#m #md: Type0)
             (fuel: nat)
             (update: m -> md -> pair md (list (ev m)))
             (to_terminate: m -> bool)
             (capacity: int) (model: md) (init_evs: list (ev m)) (exts: list (ext m))
  : Lemma (ensures (let s = program fuel update to_terminate capacity model init_evs exts in
                    is_prefix s.trace s.log)) =
  program_inv fuel update to_terminate capacity model init_evs exts;
  let s = program fuel update to_terminate capacity model init_evs exts in
  if s.terminated then () else prefix_append s.trace (pending s)

/// **`reentrant_no_loss`.** A `dispatch` made while the latch is set —
/// from `update`'s command, from `setState`, from a subscription's
/// start — queues its message at the BACK of what is pending, logs it,
/// and processes nothing: the trace, the model and everything already
/// waiting are untouched. With `exactly_once`, the message is handed to
/// `update` once the drain reaches it; with `in_order`, after everything
/// accepted before it. It can be neither lost nor moved ahead.
let reentrant_no_loss (#m #md: Type0)
                      (fuel: nat)
                      (update: m -> md -> pair md (list (ev m)))
                      (to_terminate: m -> bool)
                      (s: st m md) (msg: m)
  : Lemma (requires inv_core s /\ s.reentered /\ not s.terminated)
          (ensures (let s' = dispatch fuel update to_terminate s msg in
                    inv_core s'
                    /\ s'.log == append s.log [msg]
                    /\ pending s' == append (pending s) [msg]
                    /\ s'.trace == s.trace /\ s'.model == s.model
                    /\ s'.reentered /\ not s'.terminated)) =
  dispatch_latched fuel update to_terminate s msg;
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

/// The callee events never touch the latch.
let rec apply_evs_reentered (#m #md: Type0) (s: st m md) (evs: list (ev m))
  : Lemma (ensures (apply_evs s evs).reentered == s.reentered) (decreases evs) =
  match evs with
  | [] -> ()
  | e :: rest -> apply_evs_reentered (apply_ev s e) rest

/// **`terminated_absorbing`.** Once `terminated`, no event from inside
/// or outside changes what `update` saw, what was accepted, or the
/// model — and nothing clears the flag. The two guards at the head of
/// `dispatch` and `processMsgs`'s `while` are this lemma.
let rec terminated_absorbing (#m #md: Type0)
                             (fuel: nat)
                             (update: m -> md -> pair md (list (ev m)))
                             (to_terminate: m -> bool)
                             (s: st m md) (exts: list (ext m))
  : Lemma (requires s.terminated)
          (ensures (let s' = run fuel update to_terminate s exts in
                    s'.terminated /\ s'.trace == s.trace /\ s'.log == s.log
                    /\ s'.model == s.model /\ s'.active == s.active))
          (decreases exts) =
  if s.reentered then ()
  else
    match exts with
    | [] -> ()
    | XDispatch msg :: rest -> terminated_absorbing fuel update to_terminate s rest
    | XTerminate :: rest -> terminated_absorbing fuel update to_terminate s rest

/// …and the boot drain on a machine terminated before it ran (a
/// dispatcher-handle sink that called `Terminate`): the drain pops at
/// most one slot and processes nothing.
let terminated_absorbing_boot (#m #md: Type0)
                              (fuel: nat)
                              (update: m -> md -> pair md (list (ev m)))
                              (to_terminate: m -> bool)
                              (s: st m md) (evs: list (ev m))
  : Lemma (requires s.terminated)
          (ensures (let s' = boot fuel update to_terminate s evs in
                    s'.terminated /\ s'.trace == s.trace /\ s'.log == s.log
                    /\ s'.model == s.model /\ s'.active == s.active)) =
  let s1 = { s with reentered = true } in
  boot_evs_are_apply_evs fuel update to_terminate s1 evs;
  terminated_stays_terminated s1 evs

/// **`terminate_then_nothing`.** From ANY idle state, a `Terminate` from
/// outside means nothing after it is ever processed.
let terminate_then_nothing (#m #md: Type0)
                           (fuel: nat)
                           (update: m -> md -> pair md (list (ev m)))
                           (to_terminate: m -> bool)
                           (s: st m md) (exts: list (ext m))
  : Lemma (requires not s.reentered)
          (ensures (let s' = run fuel update to_terminate s (XTerminate :: exts) in
                    s'.terminated /\ s'.trace == s.trace /\ s'.log == s.log /\ s'.model == s.model)) =
  terminated_absorbing fuel update to_terminate (terminate s) exts

/// **`boot_drain_equiv`.** The boot drain IS the steady-state critical
/// section, run over the events `init`'s effects raised under the
/// latch — the hand-duplicated `reentered <- true; …; processMsgs ();
/// reentered <- false` and `dispatch`'s are one function.
let boot_drain_equiv (#m #md: Type0)
                     (fuel: nat)
                     (update: m -> md -> pair md (list (ev m)))
                     (to_terminate: m -> bool)
                     (s: st m md) (evs: list (ev m))
  : Lemma (ensures boot fuel update to_terminate s evs
                   == critical fuel update to_terminate (apply_evs { s with reentered = true } evs)) =
  let s1 = { s with reentered = true } in
  boot_evs_are_apply_evs fuel update to_terminate s1 evs;
  apply_evs_reentered s1 evs

/// **`boot_single_is_dispatch`.** For one message, the boot drain is
/// `dispatch` itself, from any idle non-terminated state.
let boot_single_is_dispatch (#m #md: Type0)
                            (fuel: nat)
                            (update: m -> md -> pair md (list (ev m)))
                            (to_terminate: m -> bool)
                            (s: st m md) (msg: m)
  : Lemma (requires not s.reentered /\ not s.terminated)
          (ensures boot fuel update to_terminate s [Msg msg] == dispatch fuel update to_terminate s msg) =
  boot_drain_equiv fuel update to_terminate s [Msg msg]

/// **`active_iff_not_terminated`.** In every state a program reaches,
/// `DispatcherCore.active` is the loop's `terminated` negated. Both
/// sites that set `terminated` call `MarkTerminated`, and `Wire` set
/// `active` before anything could dispatch; the model carries the two
/// cells in lockstep and this says the lockstep is an invariant, not a
/// coincidence of the paths tried.
let active_iff_not_terminated (#m #md: Type0)
                              (fuel: nat)
                              (update: m -> md -> pair md (list (ev m)))
                              (to_terminate: m -> bool)
                              (capacity: int) (model: md) (init_evs: list (ev m)) (exts: list (ext m))
  : Lemma (ensures (let s = program fuel update to_terminate capacity model init_evs exts in
                    s.active = not s.terminated)) =
  program_inv fuel update to_terminate capacity model init_evs exts

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
