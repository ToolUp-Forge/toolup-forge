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

/// Phase 788 — the Elmish subscription diff, modelled in F* and proved
/// to start, stop and keep exactly what its documentation says.
///
/// # What this module is
///
/// A hand-written model of `Sub.Internal.diff` and its two helpers
/// `NewSubs.calculate` and `Fx.change`
/// (`src/ToolUp.Platform.Client/Client/Elmish/Sub.fs`), clause for
/// clause. `Program.runWith` calls `diff` after every `update` to
/// reconcile the running subscriptions against the ones the new model
/// asks for; `Sub.fs`'s own comment states the contract — "subs whose
/// `SubId` disappears are stopped, new ones are started" — and this
/// module proves it, including on the `keys = newKeys` shortcut at the
/// top of `diff`, which is precisely where a "started twice / never
/// stopped" leak would live if the shortcut disagreed with the general
/// path.
///
/// # What is opaque
///
/// Two things are type parameters rather than modelled, because the
/// diff never inspects them:
///
///   * The SUBSCRIPTION KEY (`SubId`, a `string list`). The diff only ever
///     asks whether two keys are equal, so the model takes any type with
///     decidable equality and the host instantiates it with the real
///     one.
///   * The START FUNCTION (`Subscribe<'msg>`) and the running
///     subscription's handle (`IDisposable`). `diff` carries them through
///     untouched; `change` calls them, and its lemma is about which keys
///     survive it, not what they do.
///
/// F#'s `Set<SubId>` is modelled as a duplicate-free key list; set
/// equality is mutual membership, which is what `Set.(=)` decides.
///
/// # The theorems
///
///   * `start_exactly_new` — `toStart` is exactly the deduplicated new
///     subs whose key was not active.
///   * `stop_exactly_removed` — `toStop` is exactly the active subs whose
///     key is not asked for any more.
///   * `keep_exactly_common` — `toKeep` is exactly the active subs whose
///     key is still asked for, and `toKeep` + `toStop` is a partition of
///     the active list.
///   * `never_both` — no key is both started and stopped, and no key is
///     both kept and stopped.
///   * `dupes_exact` — a key is reported duplicate iff it occurs more
///     than once in the requested subs; the survivor is the LAST
///     occurrence.
///   * `fast_path_agrees` — when the shortcut fires, the general path
///     would have produced the same four lists.
///   * `change_keys` — after `change`, the active keys are exactly the
///     requested keys, provided every start succeeds.
module ElmishSub

(* ───────────────────────────────────────────────────────────────────
   Small closed types the model owns — see `ElmishRing.fst`.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `'a option`.
type opt (a: Type0) =
  | ONone : opt a
  | OSome : item: a -> opt a

/// F#: `'a * 'b`.
type pair (a: Type0) (b: Type0) =
  | Pair : first: a -> second: b -> pair a b

/// F#: the four-tuple `diff` returns — `dupes, toStop, toKeep, toStart`.
type diff_result (k: Type0) (h: Type0) (s: Type0) =
  | Diff : dupes: list k -> to_stop: list (pair k h) -> to_keep: list (pair k h) -> to_start: list (pair k s) -> diff_result k h s

/// F#: the accumulator `NewSubs.calculate` folds — `dupes, newKeys, newSubs`.
type accumulator (k: Type0) (s: Type0) =
  | Calc : dupes: list k -> new_keys: list k -> new_subs: list (pair k s) -> accumulator k s

(* ───────────────────────────────────────────────────────────────────
   Lists as sets.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `Set.contains` / `List.contains`.
let rec mem (#k: eqtype) (key: k) (keys: list k) : Tot bool (decreases keys) =
  match keys with
  | [] -> false
  | x :: rest -> x = key || mem key rest

/// F#: `Set.(=)` — mutual membership.
let rec subset (#k: eqtype) (xs ys: list k) : Tot bool (decreases xs) =
  match xs with
  | [] -> true
  | x :: rest -> mem x ys && subset rest ys

let set_equal (#k: eqtype) (xs ys: list k) : bool = subset xs ys && subset ys xs

/// F#: `List.map fst`.
let rec keys_of (#k: Type0) (#v: Type0) (xs: list (pair k v)) : Tot (list k) (decreases xs) =
  match xs with
  | [] -> []
  | Pair key _ :: rest -> key :: keys_of rest

/// F#: `List.filter (fun (k, _) -> mem k keys)`.
let rec with_key_in (#k: eqtype) (#v: Type0) (keys: list k) (xs: list (pair k v))
  : Tot (list (pair k v)) (decreases xs) =
  match xs with
  | [] -> []
  | Pair key value :: rest ->
      if mem key keys then Pair key value :: with_key_in keys rest else with_key_in keys rest

/// F#: `List.filter (fun (k, _) -> not (mem k keys))`.
let rec with_key_not_in (#k: eqtype) (#v: Type0) (keys: list k) (xs: list (pair k v))
  : Tot (list (pair k v)) (decreases xs) =
  match xs with
  | [] -> []
  | Pair key value :: rest ->
      if mem key keys then with_key_not_in keys rest else Pair key value :: with_key_not_in keys rest

/// F#: `List.append`.
let rec append (#a: Type0) (xs ys: list a) : Tot (list a) (decreases xs) =
  match xs with
  | [] -> ys
  | x :: rest -> x :: append rest ys

(* ───────────────────────────────────────────────────────────────────
   `Sub.fs`, clause for clause.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `NewSubs.update (subId, start) (dupes, newKeys, newSubs)`.
let update (#k: eqtype) (#s: Type0) (entry: pair k s) (acc: accumulator k s) : accumulator k s =
  let Pair sub_id start = entry in
  let Calc dupes new_keys new_subs = acc in
  if mem sub_id new_keys then Calc (sub_id :: dupes) new_keys new_subs
  else Calc dupes (sub_id :: new_keys) (Pair sub_id start :: new_subs)

/// F#: `NewSubs.calculate subs = List.foldBack update subs init` — the
/// LAST entry is folded first, so a key's last occurrence is the one
/// kept and every earlier one is reported as a dupe.
let rec calculate (#k: eqtype) (#s: Type0) (subs: list (pair k s)) : Tot (accumulator k s) (decreases subs) =
  match subs with
  | [] -> Calc [] [] []
  | entry :: rest -> update entry (calculate rest)

/// F#: `Subs.diff activeSubs sub`.
let diff (#k: eqtype) (#h: Type0) (#s: Type0) (active: list (pair k h)) (sub: list (pair k s)) : diff_result k h s =
  let keys = keys_of active in
  let Calc dupes new_keys new_subs = calculate sub in
  if set_equal keys new_keys then Diff dupes [] active []
  else
    let to_keep = with_key_in new_keys active in
    let to_stop = with_key_not_in new_keys active in
    let to_start = with_key_not_in keys new_subs in
    Diff dupes to_stop to_keep to_start

/// F#: `Fx.change onError dispatch (dupes, toStop, toKeep, toStart)` —
/// the part that decides the next active list: `List.append toKeep
/// started`, where `started` is every `toStart` entry whose start
/// succeeded. Warnings, stops and the start calls themselves are the
/// host's effects; `start` here is the host's start-or-fail, returning
/// the handle when the subscription came up.
let rec started (#k: Type0) (#h: Type0) (#s: Type0) (start: k -> s -> opt h) (xs: list (pair k s))
  : Tot (list (pair k h)) (decreases xs) =
  match xs with
  | [] -> []
  | Pair key subscribe :: rest ->
      (match start key subscribe with
       | OSome handle -> Pair key handle :: started start rest
       | ONone -> started start rest)

let change (#k: eqtype) (#h: Type0) (#s: Type0) (start: k -> s -> opt h) (d: diff_result k h s) : list (pair k h) =
  let Diff _ _ to_keep to_start = d in
  append to_keep (started start to_start)

(* ───────────────────────────────────────────────────────────────────
   Specification vocabulary — ghost.
   ─────────────────────────────────────────────────────────────────── *)

/// Whether a pair is in a list.
[@@ noextract_to "FSharp"]
let rec mem_pair (#k: eqtype) (#v: Type0) (p: pair k v) (xs: list (pair k v)) : Tot prop (decreases xs) =
  match xs with
  | [] -> False
  | x :: rest -> x == p \/ mem_pair p rest

/// How many times a key occurs in a sub list.
[@@ noextract_to "FSharp"]
let rec count (#k: eqtype) (#v: Type0) (key: k) (xs: list (pair k v)) : Tot nat (decreases xs) =
  match xs with
  | [] -> 0
  | Pair x _ :: rest -> (if x = key then 1 else 0) + count key rest

/// Duplicate-free.
[@@ noextract_to "FSharp"]
let rec distinct (#k: eqtype) (keys: list k) : Tot bool (decreases keys) =
  match keys with
  | [] -> true
  | x :: rest -> not (mem x rest) && distinct rest

/// F#: `List.last`-occurrence dedup — the sub list with every key's
/// earlier occurrences removed, in original order.
[@@ noextract_to "FSharp"]
let rec keep_last (#k: eqtype) (#v: Type0) (xs: list (pair k v)) : Tot (list (pair k v)) (decreases xs) =
  match xs with
  | [] -> []
  | Pair key value :: rest ->
      if mem key (keys_of rest) then keep_last rest else Pair key value :: keep_last rest

(* ───────────────────────────────────────────────────────────────────
   Lemmas about `calculate`.
   ─────────────────────────────────────────────────────────────────── *)

/// The three components of `calculate`, characterised: `new_keys` is
/// exactly the keys of `subs` (as a set), `new_subs` is `keep_last subs`,
/// and `new_keys` is distinct.
let rec calculate_characterised (#k: eqtype) (#s: Type0) (subs: list (pair k s))
  : Lemma (ensures (let Calc _ new_keys new_subs = calculate subs in
                    new_subs == keep_last subs
                    /\ distinct new_keys
                    /\ (forall (key: k). mem key new_keys = mem key (keys_of subs))))
          (decreases subs) =
  match subs with
  | [] -> ()
  | _ :: rest -> calculate_characterised rest

/// `count key xs > 0` iff `mem key (keys_of xs)`.
let rec count_is_mem (#k: eqtype) (#s: Type0) (xs: list (pair k s))
  : Lemma (ensures (forall (key: k). (count key xs > 0) = mem key (keys_of xs))) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> count_is_mem rest

/// The keys of `keep_last xs` are the keys of `xs`, as a set.
let rec keep_last_keys (#k: eqtype) (#v: Type0) (xs: list (pair k v))
  : Lemma (ensures (forall (key: k). mem key (keys_of (keep_last xs)) = mem key (keys_of xs))) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> keep_last_keys rest

/// A key is reported duplicate iff it occurs more than once — the
/// earlier occurrences are the dupes, and each occurrence past the
/// first-from-the-end contributes one report.
let rec dupes_characterised (#k: eqtype) (#s: Type0) (subs: list (pair k s))
  : Lemma (ensures (let Calc dupes _ _ = calculate subs in
                    forall (key: k). mem key dupes = (count key subs > 1)))
          (decreases subs) =
  match subs with
  | [] -> ()
  | Pair key _ :: rest ->
      dupes_characterised rest;
      calculate_characterised rest;
      count_is_mem rest

(* ───────────────────────────────────────────────────────────────────
   Lemmas about the two filters.
   ─────────────────────────────────────────────────────────────────── *)

let rec with_key_in_spec (#k: eqtype) (#v: Type0) (keys: list k) (xs: list (pair k v))
  : Lemma (ensures (forall (p: pair k v). mem_pair p (with_key_in keys xs) <==> (mem_pair p xs /\ mem (Pair?.first p) keys)))
          (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> with_key_in_spec keys rest

let rec with_key_not_in_spec (#k: eqtype) (#v: Type0) (keys: list k) (xs: list (pair k v))
  : Lemma (ensures (forall (p: pair k v). mem_pair p (with_key_not_in keys xs) <==> (mem_pair p xs /\ not (mem (Pair?.first p) keys))))
          (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> with_key_not_in_spec keys rest

/// The two filters partition their input: every element lands in
/// exactly one, and both are sublists in order.
let rec filters_partition (#k: eqtype) (#v: Type0) (keys: list k) (xs: list (pair k v))
  : Lemma (ensures (forall (p: pair k v). mem_pair p xs <==> (mem_pair p (with_key_in keys xs) \/ mem_pair p (with_key_not_in keys xs))))
          (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> filters_partition keys rest

/// When every key of `xs` is in `keys`, the keep filter is the identity
/// and the stop filter is empty.
let rec filters_when_all_in (#k: eqtype) (#v: Type0) (keys: list k) (xs: list (pair k v))
  : Lemma (requires subset (keys_of xs) keys)
          (ensures with_key_in keys xs == xs /\ with_key_not_in keys xs == [])
          (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> filters_when_all_in keys rest

/// `subset` is membership, pointwise.
let rec subset_spec (#k: eqtype) (xs ys: list k)
  : Lemma (ensures subset xs ys = true <==> (forall (key: k). mem key xs ==> mem key ys)) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> subset_spec rest ys

let rec mem_keys_of (#k: eqtype) (#v: Type0) (xs: list (pair k v))
  : Lemma (ensures (forall (key: k). mem key (keys_of xs) <==> (exists (value: v). mem_pair (Pair key value) xs))) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> mem_keys_of rest

/// `keys_of` distributes over `append`.
let rec keys_of_append (#k: eqtype) (#v: Type0) (xs ys: list (pair k v))
  : Lemma (ensures (forall (key: k). mem key (keys_of (append xs ys)) = (mem key (keys_of xs) || mem key (keys_of ys)))) (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> keys_of_append rest ys

/// The keys that came up are among the keys asked to start; all of them,
/// when every start succeeds.
let rec started_keys (#k: eqtype) (#h: Type0) (#s: Type0) (start: k -> s -> opt h) (xs: list (pair k s))
  : Lemma (ensures (forall (key: k). mem key (keys_of (started start xs)) ==> mem key (keys_of xs))
                   /\ ((forall (key: k) (subscribe: s). OSome? (start key subscribe)) ==>
                       (forall (key: k). mem key (keys_of xs) ==> mem key (keys_of (started start xs)))))
          (decreases xs) =
  match xs with
  | [] -> ()
  | _ :: rest -> started_keys start rest

(* ───────────────────────────────────────────────────────────────────
   The theorems.
   ─────────────────────────────────────────────────────────────────── *)

/// **`start_exactly_new`.** `toStart` holds exactly the deduplicated
/// requested subs whose key is not active — as pairs, so the start
/// function that survives dedup is the one the caller will run.
let start_exactly_new (#k: eqtype) (#h: Type0) (#s: Type0) (active: list (pair k h)) (sub: list (pair k s))
  : Lemma (ensures (let Diff _ _ _ to_start = diff active sub in
                    forall (p: pair k s).
                      mem_pair p to_start <==> (mem_pair p (keep_last sub) /\ not (mem (Pair?.first p) (keys_of active))))) =
  let keys = keys_of active in
  let Calc _ new_keys new_subs = calculate sub in
  calculate_characterised sub;
  with_key_not_in_spec keys new_subs;
  if set_equal keys new_keys then begin
    // The shortcut returns `[]` for `toStart`; every deduplicated sub's
    // key is in `new_keys`, hence in `keys`, so the right-hand side is
    // false for every pair too.
    subset_spec new_keys keys;
    subset_spec keys new_keys;
    mem_keys_of new_subs;
    mem_keys_of sub;
    keep_last_keys sub
  end
  else ()

/// **`stop_exactly_removed`.** `toStop` holds exactly the active subs
/// whose key is no longer requested.
let stop_exactly_removed (#k: eqtype) (#h: Type0) (#s: Type0) (active: list (pair k h)) (sub: list (pair k s))
  : Lemma (ensures (let Diff _ to_stop _ _ = diff active sub in
                    forall (p: pair k h).
                      mem_pair p to_stop <==> (mem_pair p active /\ not (mem (Pair?.first p) (keys_of sub))))) =
  let keys = keys_of active in
  let Calc _ new_keys _ = calculate sub in
  calculate_characterised sub;
  with_key_not_in_spec new_keys active;
  if set_equal keys new_keys then begin
    subset_spec keys new_keys;
    mem_keys_of active
  end
  else ()

/// **`keep_exactly_common`.** `toKeep` holds exactly the active subs
/// whose key is still requested — and with `toStop` partitions `active`.
let keep_exactly_common (#k: eqtype) (#h: Type0) (#s: Type0) (active: list (pair k h)) (sub: list (pair k s))
  : Lemma (ensures (let Diff _ to_stop to_keep _ = diff active sub in
                    (forall (p: pair k h). mem_pair p to_keep <==> (mem_pair p active /\ mem (Pair?.first p) (keys_of sub)))
                    /\ (forall (p: pair k h). mem_pair p active <==> (mem_pair p to_keep \/ mem_pair p to_stop)))) =
  let keys = keys_of active in
  let Calc _ new_keys _ = calculate sub in
  calculate_characterised sub;
  with_key_in_spec new_keys active;
  filters_partition new_keys active;
  if set_equal keys new_keys then begin
    subset_spec keys new_keys;
    mem_keys_of active
  end
  else ()

/// **`never_both`.** No key is both started and stopped, and no key is
/// both kept and stopped.
let never_both (#k: eqtype) (#h: Type0) (#s: Type0) (active: list (pair k h)) (sub: list (pair k s))
  : Lemma (ensures (let Diff _ to_stop to_keep to_start = diff active sub in
                    (forall (key: k). not (mem key (keys_of to_start) && mem key (keys_of to_stop)))
                    /\ (forall (key: k). not (mem key (keys_of to_keep) && mem key (keys_of to_stop))))) =
  let Diff _ to_stop to_keep to_start = diff active sub in
  start_exactly_new active sub;
  stop_exactly_removed active sub;
  keep_exactly_common active sub;
  mem_keys_of to_start;
  mem_keys_of to_stop;
  mem_keys_of to_keep;
  mem_keys_of active;
  mem_keys_of sub;
  keep_last_keys sub;
  mem_keys_of (keep_last sub)

/// **`dupes_exact`.** A key is reported as a duplicate iff it occurs
/// more than once in the requested subs.
let dupes_exact (#k: eqtype) (#h: Type0) (#s: Type0) (active: list (pair k h)) (sub: list (pair k s))
  : Lemma (ensures (let Diff dupes _ _ _ = diff active sub in
                    forall (key: k). mem key dupes = (count key sub > 1))) =
  dupes_characterised sub

/// **`fast_path_agrees`.** When `keys = newKeys` the shortcut returns
/// `dupes, [], active, []` — and the general path, run on the same
/// input, returns the same four lists. The shortcut is an optimisation,
/// not a different answer.
let fast_path_agrees (#k: eqtype) (#h: Type0) (#s: Type0) (active: list (pair k h)) (sub: list (pair k s))
  : Lemma (requires (let Calc _ new_keys _ = calculate sub in set_equal (keys_of active) new_keys))
          (ensures (let Calc dupes new_keys new_subs = calculate sub in
                    diff active sub == Diff dupes
                                           (with_key_not_in new_keys active)
                                           (with_key_in new_keys active)
                                           (with_key_not_in (keys_of active) new_subs))) =
  let keys = keys_of active in
  let Calc _ new_keys new_subs = calculate sub in
  calculate_characterised sub;
  filters_when_all_in new_keys active;
  keep_last_keys sub;
  subset_spec new_keys keys;
  subset_spec (keys_of new_subs) keys;
  filters_when_all_in keys new_subs

/// **`change_keys`.** After `change`, every active key is a requested
/// key and — when every start succeeds — every requested key is active.
let change_keys (#k: eqtype) (#h: Type0) (#s: Type0) (start: k -> s -> opt h) (active: list (pair k h)) (sub: list (pair k s))
  : Lemma (ensures (let next = change start (diff active sub) in
                    (forall (key: k). mem key (keys_of next) ==> mem key (keys_of sub))
                    /\ ((forall (key: k) (subscribe: s). OSome? (start key subscribe)) ==>
                        (forall (key: k). mem key (keys_of sub) ==> mem key (keys_of next))))) =
  let Diff _ _ to_keep to_start = diff active sub in
  keep_exactly_common active sub;
  start_exactly_new active sub;
  keys_of_append to_keep (started start to_start);
  started_keys start to_start;
  mem_keys_of to_keep;
  mem_keys_of to_start;
  mem_keys_of active;
  mem_keys_of sub;
  keep_last_keys sub;
  mem_keys_of (keep_last sub)
