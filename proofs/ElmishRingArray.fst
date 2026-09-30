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

/// Phase 955 — the Elmish dispatch ring as a STATEFUL ARRAY, written in
/// Pulse and proved to refine the list model in `ElmishRing.fst`.
///
/// # What this module is
///
/// `ElmishRing.fst` models `RingBuffer<'item>` with its backing array as
/// a `list (slot a)`. That model is the specification: it is what the six
/// ring theorems are about, and its extraction is correct and slow (Phase
/// 850 measured it three orders of magnitude behind the shipped ring).
/// This module is the same ring over a mutable array — `Pulse.Lib.Vec`
/// for the slots, `Pulse.Lib.Box` for the two indices, the capacity and
/// the state flag — and every operation is proved to do to the abstract
/// ring exactly what the list model's operation does.
///
/// It is the first module under `proofs/` that is extracted to be RUN as
/// the implementation rather than beside one, and the first extracted
/// through Custard's F# backend (`--codegen Custard --custard_backend
/// FSharp`): a Pulse `vec` becomes a .NET array and a `box` an F# `ref`
/// cell. `proofs/check.ps1` holds the committed extraction under
/// `oracle/custard/` byte for byte to what the pinned prover emits.
///
/// # The refinement relation
///
/// `is_ring rb m` is the abstraction: the array ring `rb` REPRESENTS the
/// list-model ring `m`. It is a relation indexed by the model state, not
/// a function from the array to the model, and that is forced rather
/// than chosen: the array holds bare items, so it cannot tell a slot the
/// write head has filled from a placeholder the constructor or the grow
/// step manufactured — only the model records which is which (`Written`
/// against `Placeholder`). `repr` is the pure half of it: same length,
/// same indices, same state, and every slot the model calls `Written v`
/// holds `v` in the array. A `Placeholder` slot constrains nothing.
///
/// The function the relation does determine is the one that matters: the
/// ABSTRACT CONTENTS of the array ring are `ElmishRing.unread m`, the
/// queue the ring stands for.
///
/// One lemma per operation says the diagram commutes — `create_refines`,
/// `pop_refines` / `pop_empty_refines`, and `push_writable_refines` /
/// `push_readable_refines` / `push_grow_refines` for the three shapes a
/// push takes — and each Pulse function's postcondition is its model
/// operation applied to the model state: `create` yields
/// `ElmishRing.create`, `pop` yields `ElmishRing.pop`, `push` yields
/// `ElmishRing.push`.
///
/// # The theorems
///
/// The six theorems of `ElmishRing.fst` are statements about the model
/// state, so they hold of the array ring through `is_ring` and are not
/// proved again. `run` makes that a checked statement rather than a
/// remark: over ANY operation sequence in which no push is refused, the
/// array ring returns exactly the outputs the reference FIFO queue
/// returns from its unread contents, and ends holding exactly what that
/// queue holds — every pushed item popped once, in push order, nothing
/// lost and nothing invented, through every grow. Its proof is three
/// calls to `ElmishRing.ring_is_queue`.
///
/// # What is assumed, and the one place the array ring differs
///
/// Nothing is admitted or assumed. Three things are parameters of the
/// statement and worth reading as such:
///
///   * **The capacity ceiling.** The list model grows without bound. An
///     array of `2n + 1` slots needs `2n + 1` to be a size the platform
///     can index, and the only size bound F* states unconditionally is
///     that `SizeT` holds 16 bits. So the grow step is taken only while
///     the capacity is at most `max_growable` (32,767 slots, growing to
///     65,535); past it `push` changes nothing and returns `false`
///     (`at_ceiling`). Every theorem here is about pushes that returned
///     `true`. Lifting the ceiling is a stated platform assumption
///     (`SizeT.fits_u32`), not a proof.
///   * **The placeholder value.** `create` takes the value unfilled
///     slots hold, because Pulse allocates an array from an initial
///     element and has no uninitialised one. No theorem depends on what
///     it is: `placeholder_unobserved` says it is never popped.
///   * **`Pulse.Lib.Vec` and `Pulse.Lib.Box` mean what their interfaces
///     say.** The extraction realises them as a .NET array and a `ref`
///     cell; that realisation is the extractor's, and is trusted.
module ElmishRingArray
#lang-pulse

open Pulse.Lib.Pervasives
open Pulse.Lib.Vec
open Pulse.Lib.Box { box, (:=), (!) }

module Seq = FStar.Seq
module SZ = FStar.SizeT
module V = Pulse.Lib.Vec
module B = Pulse.Lib.Box
module ER = ElmishRing

(* ───────────────────────────────────────────────────────────────────
   The refinement relation — its pure half.
   ─────────────────────────────────────────────────────────────────── *)

/// What a model slot says about the array element in its position: a
/// `Written v` slot holds `v`; a `Placeholder` slot says nothing.
let slot_holds (#t: Type0) (s: ER.slot t) (x: t) : prop =
  match s with
  | ER.Placeholder -> True
  | ER.Written v -> x == v

/// The array's contents `buf` are the model's backing list `items`.
let models (#t: Type0) (buf: Seq.seq t) (items: list (ER.slot t)) : prop =
  Seq.length buf == ER.length items /\
  (forall (i: nat). i < Seq.length buf ==> slot_holds (ER.nth items i) (Seq.index buf i))

/// The array ring's concrete state — the array, its capacity, the two
/// indices and the state flag — represents the model ring `m`. In the
/// `Writable` state the read index is not part of the state, exactly as
/// in `Ring.fs`.
let repr (#t: Type0) (buf: Seq.seq t) (cap wix rix: nat) (readable: bool) (m: ER.ring t) : prop =
  match m with
  | ER.Writable items ix ->
      readable == false /\ models buf items /\ cap == ER.length items /\ wix == ix
  | ER.ReadWritable items w r ->
      readable == true /\ models buf items /\ cap == ER.length items /\ wix == w /\ rix == r

/// The model ring after `ElmishRing.pop`.
let pop_state (#t: Type0) (m: ER.ring t) : ER.ring t = ER.Pair?.first (ER.pop m)

/// What `ElmishRing.pop` returns.
let pop_out (#t: Type0) (m: ER.ring t) : ER.opt (ER.slot t) = ER.Pair?.second (ER.pop m)

/// The array ring's result in the model's vocabulary: an item it returns
/// is a `Written` slot, never a placeholder.
let out_of (#t: Type0) (o: option t) : ER.opt (ER.slot t) =
  match o with
  | None -> ER.ONone
  | Some v -> ER.OSome (ER.Written v)

/// The largest capacity the grow step is taken from: `2n + 1` must be a
/// size F* can show `SizeT` holds, and 16 bits is all it guarantees.
let max_growable : nat = 32767

/// The push the array ring refuses: one that must grow, from a capacity
/// past the ceiling.
let at_ceiling (#t: Type0) (m: ER.ring t) : prop =
  match m with
  | ER.Writable _ _ -> False
  | ER.ReadWritable items wix rix ->
      ER.succ (ER.length items) wix == rix /\ ER.length items > max_growable

/// What `push` promises: the model's push, or — at the ceiling only —
/// nothing changed.
let push_post (#t: Type0) (m m': ER.ring t) (x: t) (ok: bool) : prop =
  if ok then m' == ER.push x m else (m' == m /\ at_ceiling m)

/// Where the grow step reads slot `j` of the new array from: the old
/// array read cyclically from the read index.
let rot (cap rix j: nat) : nat = if rix + j >= cap then rix + j - cap else rix + j

/// What the copy loop establishes: `dst` holds `2n + 1` slots, the first
/// `n` of them `src` rotated to start at `rix`.
let grown (#t: Type0) (src dst: Seq.seq t) (cap rix: nat) : prop =
  Seq.length src == cap /\
  Seq.length dst == cap + cap + 1 /\
  rix < cap /\
  (forall (j: nat). j < cap ==> Seq.index dst j == Seq.index src (rot cap rix j))

(* ───────────────────────────────────────────────────────────────────
   List lemmas the model did not need: reading through the grow step.
   ─────────────────────────────────────────────────────────────────── *)

let rec nth_replicate_placeholder (#t: Type0) (n i: nat)
  : Lemma (ensures ER.nth (ER.replicate n (ER.Placeholder #t)) i == ER.Placeholder) (decreases n) =
  if n = 0 then ()
  else if i = 0 then ()
  else nth_replicate_placeholder #t (n - 1) (i - 1)

let rec nth_append (#t: Type0) (xs ys: list (ER.slot t)) (i: nat)
  : Lemma (ensures ER.nth (ER.append xs ys) i
                   == (if i < ER.length xs then ER.nth xs i else ER.nth ys (i - ER.length xs)))
          (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> if i = 0 then () else nth_append rest ys (i - 1)

let rec nth_skip (#t: Type0) (n: nat) (xs: list (ER.slot t)) (i: nat)
  : Lemma (ensures ER.nth (ER.skip n xs) i == ER.nth xs (n + i)) (decreases xs) =
  if n = 0 then ()
  else
    match xs with
    | [] -> ()
    | _ :: rest -> nth_skip (n - 1) rest i

let rec nth_take (#t: Type0) (n: nat) (xs: list (ER.slot t)) (i: nat)
  : Lemma (requires i < n) (ensures ER.nth (ER.take n xs) i == ER.nth xs i) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> if i = 0 then () else nth_take (n - 1) rest (i - 1)

/// `doubleSize` builds `2n + 1` slots.
let length_double_size (#t: Type0) (ix: nat) (items: list (ER.slot t))
  : Lemma (requires ix < ER.length items)
          (ensures ER.length (ER.double_size ix items) == ER.length items + ER.length items + 1) =
  let n = ER.length items in
  let tail = ER.replicate (n + 1) (ER.Placeholder #t) in
  ER.length_skip ix items;
  ER.length_take ix items;
  ER.length_replicate (n + 1) (ER.Placeholder #t);
  ER.length_append (ER.take ix items) tail;
  ER.length_append (ER.skip ix items) (ER.append (ER.take ix items) tail)

/// Slot `j` of `doubleSize ix items` is the old slot `rot n ix j`, and a
/// placeholder past the old length.
let nth_double_size (#t: Type0) (ix: nat) (items: list (ER.slot t)) (j: nat)
  : Lemma (requires ix < ER.length items)
          (ensures (let n = ER.length items in
                    ER.nth (ER.double_size ix items) j
                    == (if j < n then ER.nth items (rot n ix j) else ER.Placeholder))) =
  let n = ER.length items in
  let tail = ER.replicate (n + 1) (ER.Placeholder #t) in
  ER.length_skip ix items;
  ER.length_take ix items;
  nth_append (ER.skip ix items) (ER.append (ER.take ix items) tail) j;
  if j < n - ix then nth_skip ix items j
  else begin
    let k = j - (n - ix) in
    nth_append (ER.take ix items) tail k;
    if k < ix then nth_take ix items k
    else nth_replicate_placeholder #t (n + 1) (k - ix)
  end

let models_create (#t: Type0) (n: nat) (x: t)
  : Lemma (models (Seq.create n x) (ER.replicate n (ER.Placeholder #t))) =
  ER.length_replicate n (ER.Placeholder #t);
  introduce forall (i: nat). i < n ==> slot_holds (ER.nth (ER.replicate n (ER.Placeholder #t)) i) (Seq.index (Seq.create n x) i)
  with nth_replicate_placeholder #t n i

/// A write to the array is the model's `set … (Written x)`.
let models_upd (#t: Type0) (buf: Seq.seq t) (items: list (ER.slot t)) (i: nat) (x: t)
  : Lemma (requires models buf items /\ i < Seq.length buf)
          (ensures models (Seq.upd buf i x) (ER.set items i (ER.Written x))) =
  ER.length_set items i (ER.Written x);
  introduce forall (j: nat). j < Seq.length buf ==> slot_holds (ER.nth (ER.set items i (ER.Written x)) j) (Seq.index (Seq.upd buf i x) j)
  with begin
    if j = i then (if i < ER.length items then ER.nth_set_same items i (ER.Written x))
    else ER.nth_set_other items i j (ER.Written x)
  end

(* ───────────────────────────────────────────────────────────────────
   The diagram commutes — one lemma per operation.
   ─────────────────────────────────────────────────────────────────── *)

/// **`create_refines`.** A fresh array of `max size 2` slots represents
/// `ElmishRing.create size`.
let create_refines (#t: Type0) (size: nat) (n: nat) (x: t)
  : Lemma (requires n == (if size > 2 then size else 2))
          (ensures repr (Seq.create n x) n 0 0 false (ER.create #t size) /\ ER.wf (ER.create #t size)) =
  models_create #t n x;
  ER.length_replicate n (ER.Placeholder #t);
  ER.create_wf #t size

/// **`pop_empty_refines`.** In the `Writable` state the model's pop
/// changes nothing and returns nothing.
let pop_empty_refines (#t: Type0) (buf: Seq.seq t) (cap wix rix: nat) (m: ER.ring t)
  : Lemma (requires repr buf cap wix rix false m)
          (ensures pop_state m == m /\ pop_out m == ER.ONone) =
  ()

/// What well-formedness gives the code in the readable state: every
/// index it is about to use is in range.
let readable_facts (#t: Type0) (buf: Seq.seq t) (cap wix rix: nat) (m: ER.ring t)
  : Lemma (requires repr buf cap wix rix true m /\ ER.wf m)
          (ensures cap == Seq.length buf /\ cap >= 2 /\ wix < cap /\ rix < cap /\ wix <> rix) =
  ()

let writable_facts (#t: Type0) (buf: Seq.seq t) (cap wix rix: nat) (m: ER.ring t)
  : Lemma (requires repr buf cap wix rix false m /\ ER.wf m)
          (ensures cap == Seq.length buf /\ cap >= 2 /\ wix < cap) =
  ()

/// **`pop_refines`.** In the readable state the array element at the
/// read index IS what the model pops — a `Written` slot, never a
/// placeholder — and stepping the read index (or clearing the flag when
/// it meets the write index) represents the model's popped ring.
let pop_refines (#t: Type0) (buf: Seq.seq t) (cap wix rix: nat) (m: ER.ring t)
  : Lemma (requires repr buf cap wix rix true m /\ ER.wf m)
          (ensures rix < Seq.length buf /\
                   pop_out m == ER.OSome (ER.Written (Seq.index buf rix)) /\
                   ER.wf (pop_state m) /\
                   (let rix' = ER.succ cap rix in
                    if rix' = wix then repr buf cap wix rix false (pop_state m)
                    else repr buf cap wix rix' true (pop_state m))) =
  let ER.ReadWritable items w r = m in
  ER.dist_step (ER.length items) r w;
  ER.pop_spec m

/// **`push_writable_refines`.** A push in the `Writable` state.
let push_writable_refines (#t: Type0) (buf: Seq.seq t) (cap wix rix: nat) (m: ER.ring t) (x: t)
  : Lemma (requires repr buf cap wix rix false m /\ ER.wf m)
          (ensures wix < Seq.length buf /\
                   repr (Seq.upd buf wix x) cap (ER.succ cap wix) wix true (ER.push x m) /\
                   ER.wf (ER.push x m)) =
  let ER.Writable items ix = m in
  ER.push_spec x m;
  models_upd buf items ix x;
  ER.length_set items ix (ER.Written x)

/// **`push_readable_refines`.** A push in the readable state that does
/// not meet the read index.
let push_readable_refines (#t: Type0) (buf: Seq.seq t) (cap wix rix: nat) (m: ER.ring t) (x: t)
  : Lemma (requires repr buf cap wix rix true m /\ ER.wf m /\ ER.succ cap wix <> rix)
          (ensures wix < Seq.length buf /\
                   repr (Seq.upd buf wix x) cap (ER.succ cap wix) rix true (ER.push x m) /\
                   ER.wf (ER.push x m)) =
  let ER.ReadWritable items w r = m in
  ER.push_spec x m;
  models_upd buf items w x;
  ER.length_set items w (ER.Written x)

/// **`push_grow_refines`.** The push that grows: the new array the copy
/// loop built (`grown`) represents the model ring `doubleSize` built —
/// `2n + 1` slots, the unread window rotated to start at 0, the write
/// index at the old length.
let push_grow_refines (#t: Type0) (buf: Seq.seq t) (cap wix rix: nat) (m: ER.ring t) (x: t) (dst: Seq.seq t)
  : Lemma (requires repr buf cap wix rix true m /\ ER.wf m /\ ER.succ cap wix == rix /\
                    wix < Seq.length buf /\
                    grown (Seq.upd buf wix x) dst cap rix)
          (ensures repr dst (cap + cap + 1) cap 0 true (ER.push x m) /\ ER.wf (ER.push x m)) =
  let ER.ReadWritable items w r = m in
  let items' = ER.set items w (ER.Written x) in
  ER.push_spec x m;
  models_upd buf items w x;
  ER.length_set items w (ER.Written x);
  length_double_size r items';
  introduce forall (j: nat). j < Seq.length dst ==> slot_holds (ER.nth (ER.double_size r items') j) (Seq.index dst j)
  with nth_double_size r items' j

(* ───────────────────────────────────────────────────────────────────
   The array ring.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `RingBuffer<'item>`'s mutable state, a field at a time. `readable`
/// is the `RingState` case (`false` is `Writable`), `wix` is `ix` in that
/// case and `wix` in the other, and `items` is a box because the grow
/// step replaces the array.
noeq
type ring (t: Type0) = {
  items: box (V.vec t);
  cap: box SZ.t;
  wix: box SZ.t;
  rix: box SZ.t;
  readable: box bool;
  dflt: t;
}

/// **The abstraction.** The array ring `rb` represents the well-formed
/// model ring `m`.
let is_ring (#t: Type0) ([@@@mkey] rb: ring t) (m: ER.ring t) : slprop =
  exists* (v: V.vec t) (buf: Seq.seq t) (c w r: SZ.t) (rd: bool).
    B.pts_to rb.items v **
    V.pts_to v buf **
    B.pts_to rb.cap c **
    B.pts_to rb.wix w **
    B.pts_to rb.rix r **
    B.pts_to rb.readable rd **
    pure (V.is_full_vec v /\ ER.wf m /\ repr buf (SZ.v c) (SZ.v w) (SZ.v r) rd m)

/// F#: `(i + 1) % items.Length`, as the comparison `ElmishRing.succ` is.
let succ_sz (c: SZ.t) (i: SZ.t{SZ.v i < SZ.v c}) : (j: SZ.t{SZ.v j == ER.succ (SZ.v c) (SZ.v i)}) =
  SZ.fits_lte (SZ.v i + 1) (SZ.v c);
  let i1 = SZ.add i 1sz in
  if SZ.gte i1 c then 0sz else i1

/// F#: `RingBuffer(size)`. `dflt` is the value unfilled slots hold.
fn create (#t: Type0) (size: SZ.t) (dflt: t)
  returns rb: ring t
  ensures is_ring rb (ER.create #t (SZ.v size))
{
  let n : SZ.t = (if SZ.gt size 2sz then size else 2sz);
  let v = V.alloc dflt n;
  let items = B.alloc v;
  let cap = B.alloc n;
  let wix = B.alloc 0sz;
  let rix = B.alloc 0sz;
  let readable = B.alloc false;
  let rb : ring t = { items; cap; wix; rix; readable; dflt };
  rewrite (B.pts_to items v) as (B.pts_to rb.items v);
  rewrite (B.pts_to cap n) as (B.pts_to rb.cap n);
  rewrite (B.pts_to wix 0sz) as (B.pts_to rb.wix 0sz);
  rewrite (B.pts_to rix 0sz) as (B.pts_to rb.rix 0sz);
  rewrite (B.pts_to readable false) as (B.pts_to rb.readable false);
  create_refines #t (SZ.v size) (SZ.v n) dflt;
  fold (is_ring rb (ER.create #t (SZ.v size)));
  rb
}

/// F#: `RingBuffer.Pop`. The result is the model's, and the ring is left
/// representing the model's popped ring.
fn pop (#t: Type0) (rb: ring t) (#m: erased (ER.ring t))
  requires is_ring rb m
  returns o: option t
  ensures is_ring rb (pop_state m) ** pure (pop_out m == out_of o)
{
  unfold (is_ring rb m);
  with v buf c w r rd. _;
  let rdv = !rb.readable;
  if rdv {
    let vv = !rb.items;
    rewrite (V.pts_to v buf) as (V.pts_to vv buf);
    let cv = !rb.cap;
    let wv = !rb.wix;
    let rv = !rb.rix;
    readable_facts buf (SZ.v c) (SZ.v w) (SZ.v r) m;
    pop_refines buf (SZ.v c) (SZ.v w) (SZ.v r) m;
    let x = vv.(rv);
    let r1 = succ_sz cv rv;
    rewrite (V.pts_to vv buf) as (V.pts_to v buf);
    if (r1 = wv) {
      rb.readable := false;
      fold (is_ring rb (pop_state m));
      Some x
    } else {
      rb.rix := r1;
      fold (is_ring rb (pop_state m));
      Some x
    }
  } else {
    pop_empty_refines buf (SZ.v c) (SZ.v w) (SZ.v r) m;
    fold (is_ring rb (pop_state m));
    None #t
  }
}

let fits_doubled (c: nat)
  : Lemma (requires c <= max_growable) (ensures SZ.fits (c + c) /\ SZ.fits (c + c + 1)) =
  assert_norm (pow2 16 == 65536);
  SZ.fits_at_least_16 (c + c + 1);
  SZ.fits_lte (c + c) (c + c + 1)

/// F#: `doubleSize rix items` — a new array of `2n + 1` slots whose first
/// `n` are the old array read cyclically from the read index. The rest
/// hold `fill`, the placeholder.
fn grow (#t: Type0) (src: V.vec t) (cv rv: SZ.t) (fill: t) (#buf: erased (Seq.seq t))
  requires V.pts_to src buf **
           pure (Seq.length buf == SZ.v cv /\ SZ.v rv < SZ.v cv /\ SZ.v cv <= max_growable)
  returns dst: V.vec t
  ensures exists* (nbuf: Seq.seq t).
            V.pts_to src buf ** V.pts_to dst nbuf **
            pure (V.is_full_vec dst /\ grown buf nbuf (SZ.v cv) (SZ.v rv))
{
  fits_doubled (SZ.v cv);
  let nc = SZ.add (SZ.add cv cv) 1sz;
  let dst = V.alloc fill nc;
  let mut j = 0sz;
  while (SZ.lt !j cv)
  invariant exists* (vj: SZ.t) (nbuf: Seq.seq t). (
    pts_to j vj **
    V.pts_to src buf **
    V.pts_to dst nbuf **
    pure (SZ.v vj <= SZ.v cv /\
          Seq.length nbuf == SZ.v nc /\
          (forall (k: nat). k < SZ.v vj ==> Seq.index nbuf k == Seq.index buf (rot (SZ.v cv) (SZ.v rv) k))))
  decreases (SZ.v cv - SZ.v !j)
  {
    let vj = !j;
    SZ.fits_lte (SZ.v rv + SZ.v vj) (SZ.v cv + SZ.v cv);
    let s = SZ.add rv vj;
    let from = (if SZ.gte s cv then SZ.sub s cv else s);
    let y = src.(from);
    dst.(vj) <- y;
    SZ.fits_lte (SZ.v vj + 1) (SZ.v cv);
    j := SZ.add vj 1sz;
  };
  dst
}

/// F#: `RingBuffer.Push`. `true` and the model's push — or, at the
/// capacity ceiling only, `false` and nothing changed (`push_post`).
fn push (#t: Type0) (rb: ring t) (x: t) (#m: erased (ER.ring t))
  requires is_ring rb m
  returns ok: bool
  ensures exists* (m': ER.ring t). is_ring rb m' ** pure (push_post m m' x ok)
{
  unfold (is_ring rb m);
  with v buf c w r rd. _;
  let rdv = !rb.readable;
  let vv = !rb.items;
  rewrite (V.pts_to v buf) as (V.pts_to vv buf);
  let cv = !rb.cap;
  let wv = !rb.wix;
  if rdv {
    let rv = !rb.rix;
    readable_facts buf (SZ.v c) (SZ.v w) (SZ.v r) m;
    let w1 = succ_sz cv wv;
    if (w1 = rv) {
      if (SZ.gt cv 32767sz) {
        rewrite (V.pts_to vv buf) as (V.pts_to v buf);
        fold (is_ring rb m);
        false
      } else {
        vv.(wv) <- x;
        with buf1. assert (V.pts_to vv buf1);
        let nv = grow vv cv rv rb.dflt;
        with nbuf. assert (V.pts_to nv nbuf);
        V.free vv;
        fits_doubled (SZ.v cv);
        rb.items := nv;
        rb.cap := SZ.add (SZ.add cv cv) 1sz;
        rb.wix := cv;
        rb.rix := 0sz;
        push_grow_refines buf (SZ.v c) (SZ.v w) (SZ.v r) m x nbuf;
        fold (is_ring rb (ER.push x m));
        true
      }
    } else {
      vv.(wv) <- x;
      with buf1. assert (V.pts_to vv buf1);
      rb.wix := w1;
      push_readable_refines buf (SZ.v c) (SZ.v w) (SZ.v r) m x;
      rewrite (V.pts_to vv buf1) as (V.pts_to v buf1);
      fold (is_ring rb (ER.push x m));
      true
    }
  } else {
    writable_facts buf (SZ.v c) (SZ.v w) (SZ.v r) m;
    vv.(wv) <- x;
    with buf1. assert (V.pts_to vv buf1);
    rb.rix := wv;
    rb.wix := succ_sz cv wv;
    rb.readable := true;
    push_writable_refines buf (SZ.v c) (SZ.v w) (SZ.v r) m x;
    rewrite (V.pts_to vv buf1) as (V.pts_to v buf1);
    fold (is_ring rb (ER.push x m));
    true
  }
}

(* ───────────────────────────────────────────────────────────────────
   The whole guarantee, over any operation sequence.
   ─────────────────────────────────────────────────────────────────── *)

let rec outs_of (#t: Type0) (os: list (option t)) : Tot (list (ER.opt (ER.slot t))) (decreases os) =
  match os with
  | [] -> []
  | o :: rest -> out_of o :: outs_of rest

/// What `run` promises when no push was refused: the array ring did what
/// the model's `run` does, and therefore — `ring_is_queue` — what the
/// reference FIFO queue does from the ring's unread contents.
let run_post (#t: Type0) (m: ER.ring t) (ops: list (ER.op t)) (m': ER.ring t) (ok: bool) (outs: list (option t)) : prop =
  ok ==> (ER.run m ops == ER.Pair m' (outs_of outs) /\
          (let ER.Pair q' qouts = ER.queue_run (ER.unread m) ops in
           ER.unread m' == q' /\ outs_of outs == qouts))

let run_nil (#t: Type0) (m: ER.ring t)
  : Lemma (requires ER.wf m) (ensures run_post m [] m true []) =
  ER.ring_is_queue m []

let run_push (#t: Type0) (m m1 m2: ER.ring t) (x: t) (rest: list (ER.op t)) (ok1 ok2: bool) (outs: list (option t))
  : Lemma (requires ER.wf m /\ push_post m m1 x ok1 /\ (ok1 ==> run_post m1 rest m2 ok2 outs))
          (ensures run_post m (ER.Push x :: rest) m2 (ok1 && ok2) outs) =
  if ok1 && ok2 then ER.ring_is_queue m (ER.Push x :: rest)

let run_pop (#t: Type0) (m m2: ER.ring t) (o: option t) (rest: list (ER.op t)) (ok: bool) (outs: list (option t))
  : Lemma (requires ER.wf m /\ pop_out m == out_of o /\ run_post (pop_state m) rest m2 ok outs)
          (ensures run_post m (ER.Pop :: rest) m2 ok (o :: outs)) =
  if ok then ER.ring_is_queue m (ER.Pop :: rest)

ghost
fn ring_wf (#t: Type0) (rb: ring t) (#m: erased (ER.ring t))
  preserves is_ring rb m
  ensures pure (ER.wf m)
{
  unfold (is_ring rb m);
  fold (is_ring rb m);
}

/// **`run` — the array ring is a FIFO queue.** Drive the array ring's own
/// `push` and `pop` over any operation sequence. If no push was refused
/// at the capacity ceiling, the outputs are exactly the model's
/// (`ElmishRing.run`) and therefore exactly the reference queue's
/// (`ElmishRing.queue_run` from the unread contents), and the ring ends
/// holding exactly what that queue holds. Not extracted: the hosts drive
/// `push` and `pop` themselves.
fn rec run (#t: Type0) (rb: ring t) (ops: list (ER.op t)) (#m: erased (ER.ring t))
  requires is_ring rb m
  returns res: (bool & list (option t))
  ensures exists* (m': ER.ring t). is_ring rb m' ** pure (run_post m ops m' (fst res) (snd res))
  decreases ops
{
  ring_wf rb;
  match ops {
    [] -> {
      run_nil #t m;
      (true, [])
    }
    op :: rest -> {
      match op {
        ER.Push x -> {
          let ok1 = push rb x;
          with m1. assert (is_ring rb m1);
          if ok1 {
            let res = run rb rest;
            with m2. assert (is_ring rb m2);
            run_push #t m m1 m2 x rest ok1 (fst res) (snd res);
            res
          } else {
            run_push #t m m1 m1 x rest ok1 true [];
            (false, [])
          }
        }
        ER.Pop -> {
          let o = pop rb;
          let res = run rb rest;
          with m2. assert (is_ring rb m2);
          run_pop #t m m2 o rest (fst res) (snd res);
          (fst res, o :: snd res)
        }
      }
    }
  }
}
