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

/// Phase 788 — the Elmish dispatch ring buffer, modelled in F* and
/// proved to be a FIFO queue.
///
/// # What this module is
///
/// A hand-written model of `RingBuffer<'item>`
/// (`src/ToolUp.Platform.Client/Client/Elmish/Ring.fs`), clause for
/// clause: the two-case state, `Push`, `Pop` and the grow step
/// `doubleSize`. The ring is the structure every message dispatched
/// from inside `update` waits in — `Program.runWith` pushes each
/// reentrant `dispatch` onto it and drains it in order — so what it
/// promises is the ordering of every follow-up message in every client
/// on the platform. Until this phase nothing in the tree exercised it.
///
/// Every definition names its F# counterpart in the comment above it.
/// The differential hosts (`ElmishProofOracleTests.fs` on .NET and in the
/// Fable test project, because the runtime ships to both) run the
/// EXTRACTION of this module beside the production ring over generated
/// push / pop sequences and require the two to pop the same values in
/// the same order — that host is the only thing that says this model is
/// about the code that ships.
///
/// # The representation
///
/// F#'s backing array is a `list (slot a)`. `Placeholder` is the
/// `Unchecked.defaultof` the grow step writes into the new tail and the
/// constructor's `Array.zeroCreate` fills every slot with; `Written v` is
/// a slot the write head has filled. Modelling the placeholder as a
/// constructor rather than as an opaque default value is what lets
/// `placeholder_unobserved` be a theorem rather than a comment.
///
/// The index step `(i + 1) % items.Length` is modelled by `succ`, which
/// is the same function on every index the code holds (`succ_is_mod`).
/// Modelling it as a case split rather than as `%` keeps every lemma in
/// linear arithmetic, which is what makes the proofs cheap and stable
/// under `--quake`.
///
/// # The theorems
///
/// The whole claim is `ring_is_queue`: for any well-formed ring and any
/// sequence of pushes and pops, the ring produces exactly the outputs a
/// reference FIFO queue produces from the ring's unread contents. That
/// is `fifo`, `no_lost_slot` and `no_double_pop` in one statement, and
/// it holds through every grow. The named corollaries below state each
/// of the phase's lemma families on its own, `order_across_grow`
/// isolates the grow step, and `placeholder_unobserved` says the
/// placeholder slots the grow step manufactures are never popped.
///
/// # The precondition
///
/// Every theorem assumes the backing array holds at least two slots,
/// and `create` guarantees it: the constructor floors its capacity at
/// `minimum_capacity`. The floor is not a convenience. At capacity one
/// the `ReadWritable` state cannot tell "one unread slot" from "none"
/// — `wix = rix` in both — and the second push overwrites an unread
/// item before the grow step runs; `capacity_one_loses_an_item` is that
/// execution, computed. Before this phase the constructor floored at 10
/// while `Program.withRingBufferCapacity` floored at 1 and documented
/// per-program configurability; the phase reconciles both to this one
/// number.
module ElmishRing

(* ───────────────────────────────────────────────────────────────────
   Small closed types the model owns.

   Declared here rather than taken from `FStar.Pervasives.Native` so the
   EXTRACTION references `Prims` and nothing else — the same reason
   `RemotingDecode.fst` gives, and the same shim it lets the oracle host
   compile against.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `'a option`.
type opt (a: Type0) =
  | ONone : opt a
  | OSome : item: a -> opt a

/// F#: `'a * 'b`.
type pair (a: Type0) (b: Type0) =
  | Pair : first: a -> second: b -> pair a b

/// A backing-array slot. `Placeholder` is `Unchecked.defaultof<'item>`;
/// `Written v` is a slot the write head has filled.
type slot (a: Type0) =
  | Placeholder : slot a
  | Written : value: a -> slot a

(* ───────────────────────────────────────────────────────────────────
   The array, as a list — the four operations the ring performs on it.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `items.Length`.
let rec length (#a: Type0) (xs: list a) : Tot nat (decreases xs) =
  match xs with
  | [] -> 0
  | _ :: rest -> 1 + length rest

/// F#: `items.[i]`. Total: an out-of-range read yields a placeholder,
/// which `wf` proves is never what the ring reads.
let rec nth (#a: Type0) (xs: list (slot a)) (i: nat) : Tot (slot a) (decreases xs) =
  match xs with
  | [] -> Placeholder
  | x :: rest -> if i = 0 then x else nth rest (i - 1)

/// F#: `items.[i] <- v`. Total: an out-of-range write is dropped.
let rec set (#a: Type0) (xs: list (slot a)) (i: nat) (v: slot a)
  : Tot (list (slot a)) (decreases xs) =
  match xs with
  | [] -> []
  | x :: rest -> if i = 0 then v :: rest else x :: set rest (i - 1) v

/// F#: `Seq.append` / `yield!`.
let rec append (#a: Type0) (xs ys: list a) : Tot (list a) (decreases xs) =
  match xs with
  | [] -> ys
  | x :: rest -> x :: append rest ys

/// F#: `Seq.skip n`.
let rec skip (#a: Type0) (n: nat) (xs: list a) : Tot (list a) (decreases xs) =
  if n = 0 then xs
  else
    match xs with
    | [] -> []
    | _ :: rest -> skip (n - 1) rest

/// F#: `Seq.take n`.
let rec take (#a: Type0) (n: nat) (xs: list a) : Tot (list a) (decreases xs) =
  if n = 0 then []
  else
    match xs with
    | [] -> []
    | x :: rest -> x :: take (n - 1) rest

/// F#: `Array.zeroCreate n` (every slot a placeholder) and the grow
/// step's `for _ in 0 .. items.Length do yield Unchecked.defaultof`.
let rec replicate (#a: Type0) (n: nat) (v: a) : Tot (list a) (decreases n) =
  if n = 0 then [] else v :: replicate (n - 1) v

(* ───────────────────────────────────────────────────────────────────
   The index step.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `(i + 1) % n`, on the indices the ring holds (`i < n`) — see
/// `succ_is_mod`. A case split rather than a modulus so every lemma
/// below is linear arithmetic.
let succ (n: nat) (i: nat) : nat = if i + 1 >= n then 0 else i + 1

/// The modelling step is faithful: on every index the code holds, `succ`
/// IS `(i + 1) % n`.
let succ_is_mod (n: pos) (i: nat)
  : Lemma (requires i < n) (ensures succ n i = (i + 1) % n) =
  if i + 1 < n then FStar.Math.Lemmas.small_mod (i + 1) n
  else FStar.Math.Lemmas.multiple_modulo_lemma 1 n

(* ───────────────────────────────────────────────────────────────────
   The ring — `Ring.fs`, clause for clause.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `RingState<'item>` — `Writable of wx * ix` (nothing unread; `ix`
/// is where the next write lands) and `ReadWritable of rw * wix * rix`
/// (unread slots from `rix` up to but excluding `wix`, cyclically).
type ring (a: Type0) =
  | Writable : items: list (slot a) -> ix: nat -> ring a
  | ReadWritable : items: list (slot a) -> wix: nat -> rix: nat -> ring a

/// The constructor's capacity floor — the ONE number the proof's
/// precondition needs, stated once here and mirrored by
/// `RingBuffer.MinimumCapacity`, which both the constructor and
/// `Program.withRingBufferCapacity` read.
let minimum_capacity : nat = 2

/// F#: `max size MinimumCapacity`.
let capacity_of (size: int) : (n: nat{n >= minimum_capacity}) =
  if size > minimum_capacity then size else minimum_capacity

/// F#: `RingBuffer(size)` — `Writable(Array.zeroCreate (max size MinimumCapacity), 0)`.
let create (#a: Type0) (size: int) : ring a =
  Writable (replicate (capacity_of size) Placeholder) 0

/// F#: `doubleSize ix items` — the slots from `ix` to the end, then the
/// slots before `ix`, then `items.Length + 1` placeholders (the
/// inclusive `0 .. items.Length` range), so the new array holds
/// `2n + 1` slots with the unread window rotated to start at 0.
let double_size (#a: Type0) (ix: nat) (items: list (slot a)) : list (slot a) =
  append (skip ix items) (append (take ix items) (replicate (length items + 1) Placeholder))

/// F#: `RingBuffer.Pop`.
let pop (#a: Type0) (r: ring a) : pair (ring a) (opt (slot a)) =
  match r with
  | ReadWritable items wix rix ->
      let rix' = succ (length items) rix in
      if rix' = wix then Pair (Writable items wix) (OSome (nth items rix))
      else Pair (ReadWritable items wix rix') (OSome (nth items rix))
  | Writable _ _ -> Pair r ONone

/// F#: `RingBuffer.Push`.
let push (#a: Type0) (item: a) (r: ring a) : ring a =
  match r with
  | Writable items ix ->
      let items' = set items ix (Written item) in
      ReadWritable items' (succ (length items) ix) ix
  | ReadWritable items wix rix ->
      let items' = set items wix (Written item) in
      let wix' = succ (length items) wix in
      if wix' = rix then ReadWritable (double_size rix items') (length items') 0
      else ReadWritable items' wix' rix

(* ───────────────────────────────────────────────────────────────────
   The differential host's driver — one op at a time, outputs collected.
   Extracted, so both hosts run THIS over a generated sequence.
   ─────────────────────────────────────────────────────────────────── *)

/// One operation on the ring.
type op (a: Type0) =
  | Push : item: a -> op a
  | Pop : op a

/// Run a sequence of operations; every `Pop` contributes its result to
/// the output list, in order.
let rec run (#a: Type0) (r: ring a) (ops: list (op a))
  : Tot (pair (ring a) (list (opt (slot a)))) (decreases ops) =
  match ops with
  | [] -> Pair r []
  | Push item :: rest -> run (push item r) rest
  | Pop :: rest ->
      let Pair r' out = pop r in
      let Pair r'' outs = run r' rest in
      Pair r'' (out :: outs)

(* ───────────────────────────────────────────────────────────────────
   The specification — ghost. What the ring MEANS.
   ─────────────────────────────────────────────────────────────────── *)

/// The number of steps from `from` forward to `to_`, cyclically.
[@@ noextract_to "FSharp"]
let dist (n: nat) (from: nat{from < n}) (to_: nat) : nat =
  if to_ >= from then to_ - from else to_ + n - from

/// `count` steps of `succ` from `start`.
[@@ noextract_to "FSharp"]
let rec iter (n: nat) (start: nat) (count: nat) : Tot nat (decreases count) =
  if count = 0 then start else iter n (succ n start) (count - 1)

/// The `count` slots read cyclically from `start`.
[@@ noextract_to "FSharp"]
let rec window (#a: Type0) (items: list (slot a)) (start: nat) (count: nat)
  : Tot (list (slot a)) (decreases count) =
  if count = 0 then [] else nth items start :: window items (succ (length items) start) (count - 1)

/// The unread contents of a ring, oldest first — the queue it stands for.
[@@ noextract_to "FSharp"]
let unread (#a: Type0) (r: ring a) : list (slot a) =
  match r with
  | Writable _ _ -> []
  | ReadWritable items wix rix ->
      if rix < length items then window items rix (dist (length items) rix wix) else []

/// Every slot is a `Written` one.
[@@ noextract_to "FSharp"]
let rec all_written (#a: Type0) (xs: list (slot a)) : Tot bool (decreases xs) =
  match xs with
  | [] -> true
  | Placeholder :: _ -> false
  | Written _ :: rest -> all_written rest

/// Well-formed: at least two slots, indices in range, the two heads
/// apart in the readable state, and nothing unread is a placeholder.
[@@ noextract_to "FSharp"]
let wf (#a: Type0) (r: ring a) : bool =
  match r with
  | Writable items ix -> length items >= 2 && ix < length items
  | ReadWritable items wix rix ->
      let n = length items in
      n >= 2 && wix < n && rix < n && wix <> rix && all_written (unread r)

(* ───────────────────────────────────────────────────────────────────
   List lemmas.
   ─────────────────────────────────────────────────────────────────── *)

let rec length_append (#a: Type0) (xs ys: list a)
  : Lemma (ensures length (append xs ys) = length xs + length ys) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> length_append rest ys

let rec append_nil (#a: Type0) (xs: list a)
  : Lemma (ensures append xs [] == xs) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> append_nil rest

let rec append_assoc (#a: Type0) (xs ys zs: list a)
  : Lemma (ensures append (append xs ys) zs == append xs (append ys zs)) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> append_assoc rest ys zs

let rec length_replicate (#a: Type0) (n: nat) (v: a)
  : Lemma (ensures length (replicate n v) = n) (decreases n) =
  if n = 0 then () else length_replicate (n - 1) v

let rec length_set (#a: Type0) (xs: list (slot a)) (i: nat) (v: slot a)
  : Lemma (ensures length (set xs i v) = length xs) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> if i = 0 then () else length_set rest (i - 1) v

let rec nth_set_same (#a: Type0) (xs: list (slot a)) (i: nat) (v: slot a)
  : Lemma (requires i < length xs) (ensures nth (set xs i v) i == v) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> if i = 0 then () else nth_set_same rest (i - 1) v

let rec nth_set_other (#a: Type0) (xs: list (slot a)) (i j: nat) (v: slot a)
  : Lemma (requires i <> j) (ensures nth (set xs i v) j == nth xs j) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> if i = 0 || j = 0 then () else nth_set_other rest (i - 1) (j - 1) v

let rec length_skip (#a: Type0) (n: nat) (xs: list a)
  : Lemma (requires n <= length xs) (ensures length (skip n xs) = length xs - n) (decreases xs) =
  if n = 0 then ()
  else
    match xs with
    | [] -> ()
    | _ :: rest -> length_skip (n - 1) rest

let rec skip_step (#a: Type0) (xs: list (slot a)) (i: nat)
  : Lemma (requires i < length xs) (ensures skip i xs == nth xs i :: skip (i + 1) xs) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> if i = 0 then () else skip_step rest (i - 1)

let rec length_take (#a: Type0) (n: nat) (xs: list a)
  : Lemma (ensures length (take n xs) = (if n <= length xs then n else length xs)) (decreases xs) =
  if n = 0 then ()
  else
    match xs with
    | [] -> ()
    | _ :: rest -> length_take (n - 1) rest

let rec take_all (#a: Type0) (n: nat) (xs: list a)
  : Lemma (requires length xs <= n) (ensures take n xs == xs) (decreases xs) =
  if n = 0 then ()
  else
    match xs with
    | [] -> ()
    | _ :: rest -> take_all (n - 1) rest

let rec take_append (#a: Type0) (xs ys: list a)
  : Lemma (ensures take (length xs) (append xs ys) == xs) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> take_append rest ys

let rec all_written_append (#a: Type0) (xs ys: list (slot a))
  : Lemma (ensures all_written (append xs ys) = (all_written xs && all_written ys)) (decreases xs) =
  match xs with
  | [] -> ()
  | Placeholder :: _ -> ()
  | Written _ :: rest -> all_written_append rest ys

(* ───────────────────────────────────────────────────────────────────
   Index lemmas — all linear.
   ─────────────────────────────────────────────────────────────────── *)

let succ_in_range (n: nat) (i: nat) : Lemma (requires i < n) (ensures succ n i < n) = ()

/// Stepping the origin forward shortens the distance by one.
let dist_step (n: nat) (from: nat{from < n}) (to_: nat)
  : Lemma (requires to_ < n /\ from <> to_)
          (ensures succ n from < n /\ dist n from to_ = 1 + dist n (succ n from) to_) = ()

/// Stepping the target forward lengthens the distance by one, until it
/// would land on the origin.
let dist_succ (n: nat) (from: nat{from < n}) (to_: nat)
  : Lemma (requires to_ < n /\ succ n to_ <> from)
          (ensures dist n from (succ n to_) = dist n from to_ + 1) = ()

/// When the next write would land on the read head, the window is full:
/// `n - 1` slots.
let dist_full (n: nat) (from: nat{from < n}) (to_: nat)
  : Lemma (requires n >= 2 /\ to_ < n /\ succ n to_ = from) (ensures dist n from to_ = n - 1) = ()

let rec iter_in_range (n: nat) (start: nat) (count: nat)
  : Lemma (requires start < n) (ensures iter n start count < n) (decreases count) =
  if count = 0 then () else iter_in_range n (succ n start) (count - 1)

/// Walking `dist from to` steps from `from` lands on `to`.
let rec iter_dist (n: nat) (from: nat{from < n}) (to_: nat)
  : Lemma (requires to_ < n) (ensures iter n from (dist n from to_) = to_) (decreases (dist n from to_)) =
  if from = to_ then ()
  else begin
    dist_step n from to_;
    iter_dist n (succ n from) to_
  end

/// Whether `i` is one of the `count` indices read cyclically from `start`.
[@@ noextract_to "FSharp"]
let rec in_window (n: nat) (start: nat) (count: nat) (i: nat) : Tot bool (decreases count) =
  if count = 0 then false else start = i || in_window n (succ n start) (count - 1) i

/// The target of a distance is not inside the window it measures.
let rec target_outside_window (n: nat) (from: nat{from < n}) (to_: nat)
  : Lemma (requires to_ < n) (ensures not (in_window n from (dist n from to_) to_)) (decreases (dist n from to_)) =
  if from = to_ then ()
  else begin
    dist_step n from to_;
    target_outside_window n (succ n from) to_
  end

(* ───────────────────────────────────────────────────────────────────
   Window lemmas.
   ─────────────────────────────────────────────────────────────────── *)

/// A write outside the window leaves the window unchanged.
let rec window_set_outside (#a: Type0) (items: list (slot a)) (start: nat) (count: nat) (i: nat) (v: slot a)
  : Lemma (requires not (in_window (length items) start count i))
          (ensures window (set items i v) start count == window items start count) (decreases count) =
  length_set items i v;
  if count = 0 then ()
  else begin
    nth_set_other items i start v;
    window_set_outside items (succ (length items) start) (count - 1) i v
  end

/// One more slot on the window is the slot at the window's end.
let rec window_snoc (#a: Type0) (items: list (slot a)) (start: nat) (count: nat)
  : Lemma (ensures window items start (count + 1) == append (window items start count) [nth items (iter (length items) start count)])
          (decreases count) =
  if count = 0 then () else window_snoc items (succ (length items) start) (count - 1)

/// A window is the concatenation of its two halves.
let rec window_split (#a: Type0) (items: list (slot a)) (start: nat) (c1 c2: nat)
  : Lemma (ensures window items start (c1 + c2) == append (window items start c1) (window items (iter (length items) start c1) c2))
          (decreases c1) =
  if c1 = 0 then () else window_split items (succ (length items) start) (c1 - 1) c2

/// A window that never wraps is a plain slice.
let rec window_no_wrap (#a: Type0) (items: list (slot a)) (start: nat) (count: nat)
  : Lemma (requires start + count <= length items)
          (ensures window items start count == take count (skip start items)) (decreases count) =
  if count = 0 then ()
  else begin
    skip_step items start;
    window_no_wrap items (start + 1) (count - 1)
  end

/// Reading a whole array cyclically from `ix` is the rotation the grow
/// step builds.
let rotate_is_window (#a: Type0) (items: list (slot a)) (ix: nat)
  : Lemma (requires ix < length items)
          (ensures append (skip ix items) (take ix items) == window items ix (length items)) =
  let n = length items in
  length_skip ix items;
  window_no_wrap items ix (n - ix);
  take_all (n - ix) (skip ix items);
  window_no_wrap items 0 ix;
  window_split items ix (n - ix) ix;
  if ix = 0 then append_nil items
  else iter_dist n ix 0

(* ───────────────────────────────────────────────────────────────────
   The theorems.
   ─────────────────────────────────────────────────────────────────── *)

/// **`push_spec`.** A push on a well-formed ring appends its item to the
/// unread contents and leaves the ring well-formed — through the grow
/// step included.
let push_spec (#a: Type0) (item: a) (r: ring a)
  : Lemma (requires wf r) (ensures wf (push item r) /\ unread (push item r) == append (unread r) [Written item]) =
  match r with
  | Writable items ix ->
      let n = length items in
      length_set items ix (Written item);
      nth_set_same items ix (Written item);
      dist_succ n ix ix
  | ReadWritable items wix rix ->
      let n = length items in
      let d = dist n rix wix in
      let items' = set items wix (Written item) in
      length_set items wix (Written item);
      nth_set_same items wix (Written item);
      target_outside_window n rix wix;
      window_set_outside items rix d wix (Written item);
      iter_dist n rix wix;
      window_snoc items' rix d;
      all_written_append (unread r) [Written item];
      let wix' = succ n wix in
      if wix' = rix then begin
        dist_full n rix wix;
        rotate_is_window items' rix;
        let rot = append (skip rix items') (take rix items') in
        let tail = replicate (n + 1) (Placeholder #a) in
        length_skip rix items';
        length_take rix items';
        length_append (skip rix items') (take rix items');
        length_replicate (n + 1) (Placeholder #a);
        append_assoc (skip rix items') (take rix items') tail;
        length_append rot tail;
        take_append rot tail;
        window_no_wrap (append rot tail) 0 n
      end
      else dist_succ n rix wix

/// **`pop_spec`.** A pop on a well-formed ring yields the oldest unread
/// slot and leaves the rest, well-formed; on an empty ring it yields
/// nothing and changes nothing.
let pop_spec (#a: Type0) (r: ring a)
  : Lemma (requires wf r)
          (ensures (match unread r with
                    | [] -> pop r == Pair r ONone
                    | s :: rest -> (let Pair r' out = pop r in wf r' /\ out == OSome s /\ unread r' == rest))) =
  match r with
  | Writable _ _ -> ()
  | ReadWritable items wix rix ->
      let n = length items in
      dist_step n rix wix

/// **`create_wf`.** Every ring the constructor builds is well-formed and
/// empty, whatever capacity was asked for.
let create_wf (#a: Type0) (size: int)
  : Lemma (wf (create #a size) /\ unread (create #a size) == []) =
  length_replicate (capacity_of size) (Placeholder #a)

/// The reference: a FIFO queue over the same operations.
[@@ noextract_to "FSharp"]
let rec queue_run (#a: Type0) (q: list (slot a)) (ops: list (op a))
  : Tot (pair (list (slot a)) (list (opt (slot a)))) (decreases ops) =
  match ops with
  | [] -> Pair q []
  | Push item :: rest -> queue_run (append q [Written item]) rest
  | Pop :: rest ->
      (match q with
       | [] -> (let Pair q' outs = queue_run q rest in Pair q' (ONone :: outs))
       | s :: q' -> (let Pair q'' outs = queue_run q' rest in Pair q'' (OSome s :: outs)))

/// **`ring_is_queue`** — the headline. Over ANY operation sequence a
/// well-formed ring produces exactly the outputs the reference queue
/// produces from its unread contents, and ends well-formed holding
/// exactly what the queue holds. That is `fifo`, `no_lost_slot` and
/// `no_double_pop` at once: every pushed item is popped exactly once,
/// in push order, however many times the ring grows on the way.
let rec ring_is_queue (#a: Type0) (r: ring a) (ops: list (op a))
  : Lemma (requires wf r)
          (ensures (let Pair r' outs = run r ops in
                    let Pair q' qouts = queue_run (unread r) ops in
                    wf r' /\ unread r' == q' /\ outs == qouts))
          (decreases ops) =
  match ops with
  | [] -> ()
  | Push item :: rest ->
      push_spec item r;
      ring_is_queue (push item r) rest
  | Pop :: rest ->
      pop_spec r;
      let Pair r' _ = pop r in
      ring_is_queue r' rest

(* ───────────────────────────────────────────────────────────────────
   The named corollaries — each of the phase's lemma families on its
   own, stated over the ring the constructor builds.
   ─────────────────────────────────────────────────────────────────── *)

/// **`fifo`.** Two items pushed in order are popped in that order.
let fifo (#a: Type0) (size: int) (first second: a)
  : Lemma (ensures (let Pair _ outs = run (create #a size) [Push first; Push second; Pop; Pop] in
                    outs == [OSome (Written first); OSome (Written second)])) =
  create_wf #a size;
  ring_is_queue (create #a size) [Push first; Push second; Pop; Pop];
  assert_norm (queue_run #a [] [Push first; Push second; Pop; Pop]
               == Pair [] [OSome (Written first); OSome (Written second)])

/// **`no_lost_slot`, `no_double_pop`.** After any sequence, the outputs
/// are the reference queue's — no push is dropped and no slot is
/// returned twice, because the queue does neither.
let no_lost_slot_no_double_pop (#a: Type0) (size: int) (ops: list (op a))
  : Lemma (ensures (let Pair _ outs = run (create #a size) ops in
                    let Pair _ qouts = queue_run [] ops in
                    outs == qouts)) =
  create_wf #a size;
  ring_is_queue (create #a size) ops

/// **`placeholder_unobserved`.** A pop on a well-formed ring never
/// returns one of the placeholder slots the constructor and the grow
/// step manufacture.
let placeholder_unobserved (#a: Type0) (r: ring a)
  : Lemma (requires wf r) (ensures (let Pair _ out = pop r in out =!= OSome (Placeholder #a))) =
  pop_spec r

/// **`order_across_grow`.** The grow step in isolation: a push that
/// triggers `doubleSize` leaves the unread contents in order with the
/// pushed item last, in a ring of `2n + 1` slots whose read head is 0.
let order_across_grow (#a: Type0) (item: a) (r: ring a)
  : Lemma (requires wf r /\ ReadWritable? r /\ succ (length (ReadWritable?.items r)) (ReadWritable?.wix r) = ReadWritable?.rix r)
          (ensures (let r' = push item r in
                    unread r' == append (unread r) [Written item]
                    /\ ReadWritable? r'
                    /\ length (ReadWritable?.items r') = length (ReadWritable?.items r) + length (ReadWritable?.items r) + 1
                    /\ ReadWritable?.rix r' = 0)) =
  push_spec item r;
  let ReadWritable items wix rix = r in
  let items' = set items wix (Written item) in
  length_set items wix (Written item);
  length_skip rix items';
  length_append (skip rix items') (append (take rix items') (replicate (length items' + 1) Placeholder));
  length_append (take rix items') (replicate (length items' + 1) Placeholder);
  length_replicate (length items' + 1) (Placeholder #a);
  length_take rix items'

/// **`capacity_one_loses_an_item`.** The precondition is necessary: at
/// one slot the second push overwrites the first item before the grow
/// step runs, and the first pop returns the SECOND item. This is the
/// execution, computed — and the reason `create` floors the capacity.
let capacity_one_loses_an_item ()
  : Lemma (let r = push 2 (push 1 (Writable #nat [Placeholder] 0)) in
           let Pair _ out = pop r in
           out == OSome (Written 2)) =
  assert_norm (let r = push 2 (push 1 (Writable #nat [Placeholder] 0)) in
               let Pair _ out = pop r in
               out == OSome (Written 2))
