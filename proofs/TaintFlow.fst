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

/// Phase 795 — the taint-label lattice, the labelled transform algebra
/// and the derivation walk, modelled in F* and proved noninterfering.
///
/// # The sentence
///
/// **One party's data does not reach another party absent a
/// declassification the first party accepted.**
///
/// # What this module is
///
/// A hand-written model of what Phase 794 landed, clause for clause. Every
/// definition names its F# counterpart in the comment above it, as
/// `RemotingDecode.fst` and `DisclosureFold.fst` do:
///
///   * `TaintLabel` — `bottom` / `ofPolicyRef` / `join` / `below` /
///     `narrowsTo` (`src/ToolUp.Facts.Core/Shared/DisclosureTypes.fs`).
///   * `AssemblyLabelling.contributedBy` / `label` — the label-generic fold
///     over the transform algebra
///     (`src/ToolUp.Platform.Server/Server/DatasetAssemblyTypes.fs`).
///   * `DisclosureTaintConfig.routineClears` — the conjunction's single
///     decision point (`DisclosureTypes.fs`), and the derivation walk
///     `DisclosureTaint.analyzeWithLineage` builds on it
///     (`src/ToolUp.Facts.Server/Server/DisclosureTaint.fs`).
///
/// The differential host
/// (`src/ToolUp.Platform.Tests/InProcess/TaintFlowProofOracleTests.fs`)
/// runs the EXTRACTION of this module beside production and requires them
/// to agree on labels and on verdicts — that host is the only thing that
/// says this model is about the code that ships.
///
/// # The label, and what its equality is
///
/// Production's `TaintLabel` wraps a `Set<string>`. F\* has no set here, so
/// a label is carried as a LIST and its equality is mutual membership
/// (`eq_label`). That is not a weakening: it IS production's equality,
/// because a set forgets order and duplicates and the differential's bridge
/// normalises every model label through `TaintLabel.ofPolicyRefs` before
/// comparing. Where a law holds ON THE NOSE it is stated that way
/// (associativity, and `bottom` as a two-sided identity); where it holds
/// only up to label equality it is stated that way too (commutativity,
/// idempotence), because a list does not pick a canonical order and this
/// model does not pretend to.
///
/// # What is opaque, and why
///
/// Four things are type parameters or host-supplied functions rather than
/// modelled, each because the code under proof never inspects them:
///
///   * A SOURCE (`AssemblySource`). The labelling hands it to
///     `LabelOfSource` and reads nothing else from it.
///   * A FRAME — the data a transform computes over. The algebra never
///     looks inside one, which is what lets the flow theorem say "whatever
///     the other party's rows" and mean it.
///   * The SEMANTICS of each transform case (`step_fold` / `step_join`).
///     This is the load-bearing one. Production's `contributedBy` states in
///     its own `match` exactly which inputs each case reads: every case
///     folds or rearranges the working frame alone, and `Join`
///     additionally reads a second source. The model turns that statement
///     into an evaluator whose shape enforces it — `step_fold` cannot
///     reach a second source because it is not given one — and then
///     quantifies over EVERY such semantics. So the flow theorem holds
///     however the transforms actually compute, which is both the
///     strongest form available and the reason the model needs nothing
///     from production's executor.
///   * A POLICY'S CONTRIBUTOR SCOPE (`scope_of`). Resolved from the
///     registered policy vocabulary, which is compose-time data; the model
///     takes it as a total arrow for the same reason production looks it
///     up rather than accepting it from a caller.
///
/// # The theorems
///
///   * `join_laws` — the join is associative and has `bottom` as a
///     two-sided identity on the nose; it is commutative, idempotent and an
///     upper bound of both its parts up to label equality. `join_is_least`
///     adds the least-upper-bound half, so the order is the lattice order
///     and not merely some order.
///   * `label_monotone` — raise any source's declared label and the
///     pipeline's output label can only rise. Adding a party's data to an
///     input never lowers what the output carries.
///   * `label_never_falls` — along the pipeline the label is
///     non-decreasing at every node. The fold only ever joins; nothing in
///     the algebra can lower a label.
///   * `flow_noninterference` — THE FLOW HALF. For any pipeline, any two
///     input assignments differing only in party `p`'s data, and any
///     output whose computed label does not carry `p`: the outputs are
///     identical.
///   * `declassify_only_lowers` — the exception stated as the exception. A
///     label lost `p` only through `narrowsTo` under a routine whose
///     entitlement predicate cleared `p` — and where `p`'s policy names a
///     party, only through a routine THAT PARTY accepted.
///   * `conjunction_sound` — an empty inherited-policy set means every
///     taint-propagating source that reached the target was dropped
///     somewhere on its derivation, and every drop anywhere in that
///     derivation was made by a routine entitled to make it.
///   * `trace_noninterference` — the flow half over a SEQUENCE. A room
///     emits an ordered trace of releases; `slice_tr q` is party `q`'s view
///     of it. Two runs differing only in `p`'s data give `q` the same
///     slice, so ordering and count are covered, not one output at a time.
///   * `refinement_leaks_no_more` — an amended room whose party-`q` view is
///     a function of the baseline's party-`q` view and public data inherits
///     the baseline's noninterference.
///
/// # The honest limits, stated rather than hidden
///
/// **A release `q` observes whose COMPUTED label carries `p` is outside the
/// trace lemma, by construction.** That release is a declassification:
/// `p`'s data did influence it, and that is what a declassification means.
/// `declassify_only_lowers` is the statement that covers it — the drop was
/// licensed by `p`'s own party — and the delimited-release boundary between
/// the two is exactly where budgets live. Nothing here meters a budget.
///
/// **The derivation is modelled as a TREE.** Production walks a graph with
/// a memo table and a cycle guard. A memo cannot change a value; the
/// content-addressed store is acyclic, and a tree is the acyclic case
/// unfolded. Where a graph shares an upstream, the unfolding visits it
/// twice and joins the same refs into the same label — immaterial under set
/// equality. The differential checks the unfolding against the real walk on
/// every fixture rather than leaving it asserted.
///
/// **The model is about the LABEL, not the ordered ref list.** Production
/// keeps `InheritedPolicyRefs` in nearest-declared-first order so
/// `InheritedPolicyRef` — the Phase 562 deny ref — is unchanged. Order is
/// a presentation fact about a refusal message; `InheritedLabel` is the set,
/// and the set is what this module reasons about.
///
/// **The routines themselves are not modelled and not claimed.** Whether an
/// aggregation-over-k or a noise addition actually loses attribution is the
/// routine's business. This is a proof about walls.

module TaintFlow

(* ───────────────────────────────────────────────────────────────────
   Small closed types the model owns.

   Declared here rather than taken from `FStar.Pervasives.Native` so the
   EXTRACTION references `Prims` and nothing else — the same reason
   `DisclosureFold.fst` gives, and the same shim it lets the oracle host
   compile against.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `'a option`.
type opt (a: Type0) =
  | ONone : opt a
  | OSome : item: a -> opt a

/// F#: `'a * 'b`.
type pair (a: Type0) (b: Type0) =
  | Pair : first: a -> second: b -> pair a b

/// F#: `List.length`.
let rec length (#a: Type0) (xs: list a) : Tot nat (decreases xs) =
  match xs with
  | [] -> 0
  | _ :: rest -> 1 + length rest

/// F#: `List.contains`.
let rec mem_of (#a: eqtype) (x: a) (xs: list a) : Tot bool (decreases xs) =
  match xs with
  | [] -> false
  | y :: rest -> y = x || mem_of x rest

(* ───────────────────────────────────────────────────────────────────
   1. The label lattice — `Shared/DisclosureTypes.fs`.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `TaintLabel = private TaintRefs of Set<string>` — the set of
/// taint-propagating policy refs a value's lineage carries. Carried as a
/// list here; see the header for why its equality is mutual membership.
type label = list string

/// F#: `TaintLabel.contains`.
let mem (r: string) (l: label) : bool = mem_of r l

/// F#: `TaintLabel.bottom` — nothing reaches the value. The join's identity
/// and the bottom of the order.
let bottom: label = []

/// F#: `TaintLabel.isBottom`.
let is_bottom (l: label) : bool = Nil? l

/// F#: `TaintLabel.ofPolicyRef`.
let of_policy_ref (policy_ref: string) : label = [policy_ref]

/// List concatenation, defined here so the extraction references `Prims`
/// alone.
let rec app (a b: label) : Tot label (decreases a) =
  match a with
  | [] -> b
  | r :: rest -> r :: app rest b

/// F#: `TaintLabel.join` (`Set.union`) — the least upper bound, what a
/// value carries when two lineages meet.
let join (a b: label) : label = app a b

/// F#: `TaintLabel.joinAll` — `Seq.fold join bottom`.
let rec join_all (ls: list label) : Tot label (decreases ls) =
  match ls with
  | [] -> bottom
  | l :: rest -> join l (join_all rest)

/// Every ref of `a` is a ref of `b` — the lattice order, as a membership
/// statement. `below` below is production's own definition of the same
/// thing, and `below_is_sub` proves the two agree.
let rec sub (a b: label) : Tot bool (decreases a) =
  match a with
  | [] -> true
  | r :: rest -> mem r b && sub rest b

/// Label equality: mutual inclusion. This is `Set<string>` equality, which
/// is what production's `=` on a `TaintLabel` computes.
let eq_label (a b: label) : bool = sub a b && sub b a

/// F#: `TaintLabel.below` — "joining `a` into `b` adds nothing".
let below (a b: label) : bool = eq_label (join a b) b

// ─── Membership, and the small inductions everything else rests on ───

let rec mem_app (r: string) (a b: label)
  : Lemma (ensures mem r (app a b) == (mem r a || mem r b)) (decreases a)
          [SMTPat (mem r (app a b))] =
  match a with
  | [] -> ()
  | _ :: rest -> mem_app r rest b

let rec app_nil (a: label) : Lemma (ensures app a [] == a) (decreases a) =
  match a with
  | [] -> ()
  | _ :: rest -> app_nil rest

let rec app_assoc (a b c: label) : Lemma (ensures app (app a b) c == app a (app b c)) (decreases a) =
  match a with
  | [] -> ()
  | _ :: rest -> app_assoc rest b c

/// `sub` from pointwise membership — the introduction rule, so a
/// containment can be proved by giving the one-ref argument.
let rec sub_intro (a b: label) (h: (r: string) -> Lemma (requires mem r a) (ensures mem r b))
  : Lemma (ensures sub a b) (decreases a) =
  match a with
  | [] -> ()
  | _ :: rest ->
    let h_head (r: string) : Lemma (requires mem r a) (ensures mem r b) = h r in
    h_head (Cons?.hd a);
    let h_rest (r: string) : Lemma (requires mem r rest) (ensures mem r b) = h r in
    sub_intro rest b h_rest

/// `sub` to pointwise membership — the elimination rule.
let rec sub_elim (a b: label) (r: string)
  : Lemma (requires sub a b /\ mem r a) (ensures mem r b) (decreases a) =
  match a with
  | [] -> ()
  | x :: rest -> if x = r then () else sub_elim rest b r

let sub_refl (a: label) : Lemma (ensures sub a a) =
  sub_intro a a (fun _ -> ())

let sub_trans (a b c: label) : Lemma (requires sub a b /\ sub b c) (ensures sub a c) =
  sub_intro a c (fun r -> sub_elim a b r; sub_elim b c r)

let sub_join_left (a b: label) : Lemma (ensures sub a (join a b)) =
  sub_intro a (join a b) (fun _ -> ())

let sub_join_right (a b: label) : Lemma (ensures sub b (join a b)) =
  sub_intro b (join a b) (fun _ -> ())

/// A ref absent from a label is absent from everything below it — the step
/// the flow theorem takes at every node.
let not_mem_of_sub (a b: label) (r: string)
  : Lemma (requires sub a b /\ ~(mem r b)) (ensures ~(mem r a)) =
  if mem r a then sub_elim a b r else ()

// ─── The lattice laws ────────────────────────────────────────────────

/// **`join_laws`.** The six laws `TaintLabel.joinLawFailuresOf` checks,
/// each in the strongest form the representation supports. Associativity
/// and the two identity laws hold ON THE NOSE — the shipped `Set.union`
/// satisfies them on the nose too. Commutativity, idempotence and the
/// upper-bound law hold up to label equality, which is set equality: a list
/// does not choose a canonical order, and neither law is about order.
let join_laws (a b c: label)
  : Lemma
      (ensures
        join (join a b) c == join a (join b c) /\
        join bottom a == a /\
        join a bottom == a /\
        eq_label (join a b) (join b a) /\
        eq_label (join a a) a /\
        sub a (join a b) /\
        sub b (join a b)) =
  app_assoc a b c;
  app_nil a;
  sub_intro (join a b) (join b a) (fun _ -> ());
  sub_intro (join b a) (join a b) (fun _ -> ());
  sub_intro (join a a) a (fun _ -> ());
  sub_intro a (join a a) (fun _ -> ());
  sub_join_left a b;
  sub_join_right a b

/// The least-upper-bound half: any label above both parts is above the
/// join. With the upper-bound law in `join_laws`, this is what makes the
/// join the LEAST upper bound and the order a lattice order.
let join_is_least (a b u: label)
  : Lemma (requires sub a u /\ sub b u) (ensures sub (join a b) u) =
  sub_intro (join a b) u (fun r -> if mem r a then sub_elim a u r else sub_elim b u r)

/// Production defines its order as `join a b = b`; the model reasons with
/// `sub`. They are the same relation, so nothing below is about a different
/// order from the shipped one.
let below_is_sub (a b: label) : Lemma (ensures below a b == sub a b) =
  sub_join_right a b;
  sub_refl b;
  if sub a b then join_is_least a b b else ();
  if below a b then (sub_join_left a b; sub_trans a (join a b) b) else ()

(* ───────────────────────────────────────────────────────────────────
   2. Declassification — `DisclosureTypes.fs`, unchanged by Phase 794.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `DeclassificationRoutine`, as much of it as the decision reads. The
/// `OperationId` and the `Rationale` are audit payload; `AcceptingScopes`
/// is what decides.
type routine = { accepting_scopes: list string }

/// F#: `DisclosureTaintConfig.routineClears`. **Fail-closed.** An unscoped
/// policy is cleared by any declared routine (Phase 562 — the policy makes
/// no party claim). A party-scoped policy is cleared ONLY by a routine
/// whose accepting scopes name that party, so one party's consent can never
/// lower another party's label.
///
/// `scope_of` is the contributing party the REGISTERED policy declares —
/// compose-time data, never a caller-supplied claim.
let routine_clears (scope_of: string -> opt string) (rt: routine) (policy_ref: string) : bool =
  match scope_of policy_ref with
  | ONone -> true
  | OSome party -> mem_of party rt.accepting_scopes

/// F#: `TaintLabel.narrowsTo` — the ONE operator that lowers a label.
let rec narrows_to (clears: string -> bool) (l: label) : Tot label (decreases l) =
  match l with
  | [] -> []
  | r :: rest -> if clears r then narrows_to clears rest else r :: narrows_to clears rest

/// F#: `TaintLabel.clearedBy` — the refs a crossing actually cleared. Empty
/// ⇒ the routine cleared nothing, so the crossing declassified nothing and
/// is not recorded as one.
let rec cleared_by (clears: string -> bool) (l: label) : Tot label (decreases l) =
  match l with
  | [] -> []
  | r :: rest -> if clears r then r :: cleared_by clears rest else cleared_by clears rest

/// A ref survives a narrowing exactly when it was there and the routine was
/// not entitled to clear it.
let rec narrows_to_characterised (clears: string -> bool) (l: label) (r: string)
  : Lemma (ensures mem r (narrows_to clears l) == (mem r l && not (clears r))) (decreases l) =
  match l with
  | [] -> ()
  | _ :: rest -> narrows_to_characterised clears rest r

let narrows_to_only_lowers (clears: string -> bool) (l: label)
  : Lemma (ensures sub (narrows_to clears l) l) =
  sub_intro (narrows_to clears l) l (fun r -> narrows_to_characterised clears l r)

/// **`declassify_only_lowers`.** The exception, stated as the exception. If
/// a label carried `p` and the narrowed label does not, then the routine's
/// entitlement predicate cleared `p` — and where `p`'s policy names a
/// party, that party is one the routine's `AcceptingScopes` names. Nothing
/// else in this module lowers a label: `label_never_falls` below proves the
/// pipeline fold only ever joins, and `narrows_to` is the only other
/// operator on labels here at all.
let declassify_only_lowers (scope_of: string -> opt string) (rt: routine) (l: label) (p: string)
  : Lemma
      (requires mem p l /\ ~(mem p (narrows_to (routine_clears scope_of rt) l)))
      (ensures
        routine_clears scope_of rt p /\
        (forall (party: string). scope_of p == OSome party ==> mem_of party rt.accepting_scopes)) =
  narrows_to_characterised (routine_clears scope_of rt) l p

(* ───────────────────────────────────────────────────────────────────
   3. The labelled transform algebra — `Server/DatasetAssemblyTypes.fs`.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `AssemblyTransform` — the closed DU, case for case. The payload
/// fields of the four non-`Join` cases (columns, buckets, aggregations,
/// filters) are NOT modelled: `contributedBy` reads none of them, and a
/// second implementation of them here would be free to disagree with the
/// host's. `Join`'s right source IS modelled, because that is the one input
/// the labelling reads.
type transform (src: Type0) =
  | TJoin : right: src -> transform src
  | TResample : transform src
  | TLag : transform src
  | TWindow : transform src
  | TFilter : transform src

/// F#: `TransformLabelling<'Label>.LabelOfSource` — the declared part.
/// Which party a source belongs to is compose-time data, never something
/// inferred from a frame. `Bottom` and `Join` are not parameters here
/// because Phase 794's whole point is that there is exactly ONE join in the
/// estate; the model binds them to the lattice above for the same reason.
type labelling (src: Type0) = src -> label

/// F#: `AssemblyLabelling.contributedBy` — the label a transform
/// CONTRIBUTES, i.e. the labels of its inputs other than the working frame.
/// Only `Join` has one. An information-losing fold is not a declassifier,
/// so `Resample` / `Lag` / `Window` / `Filter` contribute the identity and
/// carry whatever reached them.
let contributed_by (#src: Type0) (label_of_source: labelling src) (t: transform src) : label =
  match t with
  | TJoin right -> label_of_source right
  | TResample -> bottom
  | TLag -> bottom
  | TWindow -> bottom
  | TFilter -> bottom

/// F#: `LabelledTransform<'Label>`.
type labelled_transform (src: Type0) = {
  lt_transform: transform src;
  lt_label: label;
  lt_contributed: label;
}

/// F#: `LabelledAssembly<'Label>`.
type labelled_assembly (src: Type0) = {
  la_base_label: label;
  la_nodes: list (labelled_transform src);
  la_output_label: label;
}

/// F#: the `List.mapFold` inside `AssemblyLabelling.label`, with `incoming`
/// the accumulator — each node's label is the join of the incoming frame's
/// label and what the node contributed.
let rec label_nodes (#src: Type0) (los: labelling src) (incoming: label) (ts: list (transform src))
  : Tot (pair (list (labelled_transform src)) label) (decreases ts) =
  match ts with
  | [] -> Pair [] incoming
  | t :: rest ->
    let contributed = contributed_by los t in
    let node_label = join incoming contributed in
    (match label_nodes los node_label rest with
     | Pair nodes out ->
       Pair
         ({ lt_transform = t; lt_label = node_label; lt_contributed = contributed } :: nodes)
         out)

/// The accumulator alone. `label_nodes` computes it beside the per-node
/// records; `label_nodes_out` proves they are the same value, so the
/// theorems can reason over this one and the extraction can stay faithful
/// to production's `mapFold`.
let rec fold_label (#src: Type0) (los: labelling src) (incoming: label) (ts: list (transform src))
  : Tot label (decreases ts) =
  match ts with
  | [] -> incoming
  | t :: rest -> fold_label los (join incoming (contributed_by los t)) rest

let rec label_nodes_out (#src: Type0) (los: labelling src) (incoming: label) (ts: list (transform src))
  : Lemma (ensures Pair?.second (label_nodes los incoming ts) == fold_label los incoming ts)
          (decreases ts) =
  match ts with
  | [] -> ()
  | t :: rest -> label_nodes_out los (join incoming (contributed_by los t)) rest

/// F#: `AssemblyLabelling.label`.
let label_assembly (#src: Type0) (los: labelling src) (base_source: src) (ts: list (transform src))
  : labelled_assembly src =
  let base_label = los base_source in
  match label_nodes los base_label ts with
  | Pair nodes out -> { la_base_label = base_label; la_nodes = nodes; la_output_label = out }

/// F#: `AssemblyTaint.labelOf` — the label a labelled node's output frame
/// carries.
let label_of (#src: Type0) (node: labelled_transform src) : label = node.lt_label

/// F#: `AssemblyLabelling.outputLabelOf` — what a consumer of the assembled
/// dataset inherits.
let output_label (#src: Type0) (los: labelling src) (base_source: src) (ts: list (transform src)) : label =
  (label_assembly los base_source ts).la_output_label

let output_label_is_fold (#src: Type0) (los: labelling src) (base_source: src) (ts: list (transform src))
  : Lemma (ensures output_label los base_source ts == fold_label los (los base_source) ts) =
  label_nodes_out los (los base_source) ts

// ─── Monotonicity ────────────────────────────────────────────────────

/// **`label_never_falls`.** Along the pipeline the label is non-decreasing:
/// whatever reached a node is still carried by everything downstream of it.
/// The fold only ever joins, so nothing in the algebra can lower a label —
/// the other half of `declassify_only_lowers`, proved over the algebra
/// rather than over one operator.
let rec label_never_falls (#src: Type0) (los: labelling src) (incoming: label) (ts: list (transform src))
  : Lemma (ensures sub incoming (fold_label los incoming ts)) (decreases ts) =
  match ts with
  | [] -> sub_refl incoming
  | t :: rest ->
    let node_label = join incoming (contributed_by los t) in
    label_never_falls los node_label rest;
    sub_join_left incoming (contributed_by los t);
    sub_trans incoming node_label (fold_label los node_label rest)

/// Raising the incoming label can only raise the output label.
let rec fold_label_monotone_in_incoming
  (#src: Type0) (los: labelling src) (i1 i2: label) (ts: list (transform src))
  : Lemma (requires sub i1 i2) (ensures sub (fold_label los i1 ts) (fold_label los i2 ts)) (decreases ts) =
  match ts with
  | [] -> ()
  | t :: rest ->
    let c = contributed_by los t in
    sub_join_left i2 c;
    sub_join_right i2 c;
    sub_trans i1 i2 (join i2 c);
    join_is_least i1 c (join i2 c);
    fold_label_monotone_in_incoming los (join i1 c) (join i2 c) rest

let rec fold_label_monotone_in_labelling
  (#src: Type0) (los1 los2: labelling src) (i1 i2: label) (ts: list (transform src))
  : Lemma
      (requires sub i1 i2 /\ (forall (s: src). sub (los1 s) (los2 s)))
      (ensures sub (fold_label los1 i1 ts) (fold_label los2 i2 ts))
      (decreases ts) =
  match ts with
  | [] -> ()
  | t :: rest ->
    let c1 = contributed_by los1 t in
    let c2 = contributed_by los2 t in
    sub_join_right i2 c2;
    sub_join_left i2 c2;
    sub_trans i1 i2 (join i2 c2);
    (match t with
     | TJoin _ -> sub_trans c1 c2 (join i2 c2)
     | _ -> ());
    join_is_least i1 c1 (join i2 c2);
    fold_label_monotone_in_labelling los1 los2 (join i1 c1) (join i2 c2) rest

/// **`label_monotone`.** Adding a party's data to any input can only raise
/// the output label. Stated over the labelling, which is where "adding a
/// party's data to an input" lives: raise any source's declared label — one
/// of them, all of them — and the label the pipeline's output carries can
/// only rise.
let label_monotone
  (#src: Type0) (los1 los2: labelling src) (base_source: src) (ts: list (transform src))
  : Lemma
      (requires forall (s: src). sub (los1 s) (los2 s))
      (ensures sub (output_label los1 base_source ts) (output_label los2 base_source ts)) =
  output_label_is_fold los1 base_source ts;
  output_label_is_fold los2 base_source ts;
  fold_label_monotone_in_labelling los1 los2 (los1 base_source) (los2 base_source) ts

(* ───────────────────────────────────────────────────────────────────
   4. The semantics, and the flow half.
   ─────────────────────────────────────────────────────────────────── *)

/// The per-case semantics, supplied by the host. See the header: this is
/// `contributedBy`'s own `match` turned into an evaluator whose SHAPE
/// enforces what each case may read. `step_fold` is given the working frame
/// and nothing else; `step_join` is given the working frame and exactly one
/// other source's frame. Every theorem below quantifies over all of them.
noeq type semantics (src: Type0) (frame: Type0) = {
  step_fold: transform src -> frame -> frame;
  step_join: src -> frame -> frame -> frame;
}

/// The caller's data: one frame per declared source.
type assignment (src: Type0) (frame: Type0) = src -> frame

let rec eval
  (#src #frame: Type0)
  (sem: semantics src frame)
  (assign: assignment src frame)
  (working: frame)
  (ts: list (transform src))
  : Tot frame (decreases ts) =
  match ts with
  | [] -> working
  | t :: rest ->
    let next =
      match t with
      | TJoin right -> sem.step_join right working (assign right)
      | TResample -> sem.step_fold t working
      | TLag -> sem.step_fold t working
      | TWindow -> sem.step_fold t working
      | TFilter -> sem.step_fold t working
    in
    eval sem assign next rest

/// A run of one pipeline: start from the base source's frame and apply the
/// transforms in order.
let run
  (#src #frame: Type0)
  (sem: semantics src frame)
  (assign: assignment src frame)
  (base_source: src)
  (ts: list (transform src))
  : frame =
  eval sem assign (assign base_source) ts

/// Two input assignments that differ ONLY in party `p`'s data: every source
/// whose DECLARED label does not carry `p` holds the same frame in both. A
/// source that does carry `p` is unconstrained — different rows, different
/// size, anything at all.
[@@ noextract_to "FSharp"]
let agree_off (#src #frame: Type0) (los: labelling src) (p: string) (a1 a2: assignment src frame) : prop =
  forall (s: src). ~(mem p (los s)) ==> a1 s == a2 s

let rec eval_noninterferes
  (#src #frame: Type0)
  (sem: semantics src frame)
  (los: labelling src)
  (p: string)
  (a1 a2: assignment src frame)
  (incoming: label)
  (working: frame)
  (ts: list (transform src))
  : Lemma
      (requires agree_off los p a1 a2 /\ ~(mem p (fold_label los incoming ts)))
      (ensures eval sem a1 working ts == eval sem a2 working ts)
      (decreases ts) =
  match ts with
  | [] -> ()
  | t :: rest ->
    let c = contributed_by los t in
    let node_label = join incoming c in
    label_never_falls los node_label rest;
    not_mem_of_sub node_label (fold_label los node_label rest) p;
    sub_join_right incoming c;
    not_mem_of_sub c node_label p;
    let next =
      match t with
      | TJoin right -> sem.step_join right working (a1 right)
      | TResample -> sem.step_fold t working
      | TLag -> sem.step_fold t working
      | TWindow -> sem.step_fold t working
      | TFilter -> sem.step_fold t working
    in
    eval_noninterferes sem los p a1 a2 node_label next rest

/// **`flow_noninterference`.** THE FLOW HALF. For any pipeline, any two
/// input assignments differing only in party `p`'s data, and any output
/// whose computed label does not carry `p` — the outputs are IDENTICAL.
///
/// It holds for every semantics, so it is not a statement about what the
/// transforms happen to compute. It is a statement about what they are able
/// to read, which is the thing the label tracks.
let flow_noninterference
  (#src #frame: Type0)
  (sem: semantics src frame)
  (los: labelling src)
  (p: string)
  (a1 a2: assignment src frame)
  (base_source: src)
  (ts: list (transform src))
  : Lemma
      (requires agree_off los p a1 a2 /\ ~(mem p (output_label los base_source ts)))
      (ensures run sem a1 base_source ts == run sem a2 base_source ts) =
  output_label_is_fold los base_source ts;
  label_never_falls los (los base_source) ts;
  not_mem_of_sub (los base_source) (fold_label los (los base_source) ts) p;
  eval_noninterferes sem los p a1 a2 (los base_source) (a1 base_source) ts

(* ───────────────────────────────────────────────────────────────────
   5. The derivation walk and the conjunction —
      `Server/DisclosureTaint.fs`.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: a `Fact` as much as `analyzeWithLineage` reads it. `own` is the
/// registered TAINT-PROPAGATING policy the fact mints
/// (`taintSourcePolicy` — a `Plain` policy and an unregistered ref are not
/// taint sources at all, so neither is one here); `routine_of` is the
/// declassification routine its method declares (`declassifierOf`); and
/// `upstream` is the derivation beneath it.
///
/// A fact is EITHER a declassifier OR a source, never both — production's
/// `match` makes the two branches exclusive, and so does `taint_of` below.
type derivation =
  | DNode : own: opt string -> routine_of: opt routine -> upstream: list derivation -> derivation

/// F#: `outputTaint` — the taint a fact's output carries. A declassifier
/// clears only what its accepting scopes entitle it to and everything else
/// flows on; a non-declassifier adds its own source policy to what it
/// inherited.
///
/// Production's `List.distinct` is not modelled: it cannot change which
/// refs a label contains, and containment is what a label is.
let rec taint_of (scope_of: string -> opt string) (d: derivation) : Tot label (decreases d) =
  match d with
  | DNode own rt ups ->
    let inputs = inputs_taint scope_of ups in
    (match rt with
     | OSome r -> narrows_to (routine_clears scope_of r) inputs
     | ONone ->
       (match own with
        | OSome s -> s :: inputs
        | ONone -> inputs))

/// F#: `graph.UpstreamOf factId |> List.collect (outputTaint visiting')` —
/// the union of the inputs' output taint.
and inputs_taint (scope_of: string -> opt string) (ds: list derivation) : Tot label (decreases ds) =
  match ds with
  | [] -> bottom
  | d :: rest -> join (taint_of scope_of d) (inputs_taint scope_of rest)

/// A node MINTS `r` when it is not a declassifier and its own registered
/// policy is `r`.
let mints (r: string) (d: derivation) : bool =
  match d with
  | DNode own rt _ ->
    (match rt, own with
     | ONone, OSome s -> s = r
     | _, _ -> false)

/// `r` is minted somewhere in this derivation — the non-vacuity condition
/// the conjunction is about. A party that contributed nothing has nothing
/// to have consented to.
let rec reaches (r: string) (d: derivation) : Tot bool (decreases d) =
  match d with
  | DNode _ _ ups -> mints r d || reaches_list r ups

and reaches_list (r: string) (ds: list derivation) : Tot bool (decreases ds) =
  match ds with
  | [] -> false
  | d :: rest -> reaches r d || reaches_list r rest

/// Wherever `r` entered a node and did not leave it, that node carried a
/// declared routine and the routine was ENTITLED to clear `r`. Stated at
/// every node of the derivation, not only at the root.
let rec drops_are_entitled (scope_of: string -> opt string) (r: string) (d: derivation)
  : Tot bool (decreases d) =
  match d with
  | DNode _ rt ups ->
    (if mem r (inputs_taint scope_of ups) && not (mem r (taint_of scope_of d)) then
       (match rt with
        | OSome rt' -> routine_clears scope_of rt' r
        | ONone -> false)
     else true)
    && drops_are_entitled_list scope_of r ups

and drops_are_entitled_list (scope_of: string -> opt string) (r: string) (ds: list derivation)
  : Tot bool (decreases ds) =
  match ds with
  | [] -> true
  | d :: rest -> drops_are_entitled scope_of r d && drops_are_entitled_list scope_of r rest

/// `r` was dropped somewhere in this derivation.
let rec drop_occurred (scope_of: string -> opt string) (r: string) (d: derivation)
  : Tot bool (decreases d) =
  match d with
  | DNode _ _ ups ->
    (mem r (inputs_taint scope_of ups) && not (mem r (taint_of scope_of d)))
    || drop_occurred_list scope_of r ups

and drop_occurred_list (scope_of: string -> opt string) (r: string) (ds: list derivation)
  : Tot bool (decreases ds) =
  match ds with
  | [] -> false
  | d :: rest -> drop_occurred scope_of r d || drop_occurred_list scope_of r rest

/// Unconditional, and the heart of the conjunction: the ONLY way a ref
/// leaves a derivation is `narrows_to` under a routine `routine_clears`
/// entitled. A non-declassifier can only add.
let rec every_drop_is_entitled (scope_of: string -> opt string) (r: string) (d: derivation)
  : Lemma (ensures drops_are_entitled scope_of r d) (decreases d) =
  match d with
  | DNode own rt ups ->
    (match rt with
     | OSome rt' -> narrows_to_characterised (routine_clears scope_of rt') (inputs_taint scope_of ups) r
     | ONone -> ());
    every_drop_is_entitled_list scope_of r ups

and every_drop_is_entitled_list (scope_of: string -> opt string) (r: string) (ds: list derivation)
  : Lemma (ensures drops_are_entitled_list scope_of r ds) (decreases ds) =
  match ds with
  | [] -> ()
  | d :: rest ->
    every_drop_is_entitled scope_of r d;
    every_drop_is_entitled_list scope_of r rest

let rec reached_but_absent_was_dropped (scope_of: string -> opt string) (r: string) (d: derivation)
  : Lemma
      (requires reaches r d /\ ~(mem r (taint_of scope_of d)))
      (ensures drop_occurred scope_of r d)
      (decreases d) =
  match d with
  | DNode own rt ups ->
    if mem r (inputs_taint scope_of ups) then ()
    else reached_but_absent_was_dropped_list scope_of r ups

and reached_but_absent_was_dropped_list (scope_of: string -> opt string) (r: string) (ds: list derivation)
  : Lemma
      (requires reaches_list r ds /\ ~(mem r (inputs_taint scope_of ds)))
      (ensures drop_occurred_list scope_of r ds)
      (decreases ds) =
  match ds with
  | [] -> ()
  | d :: rest ->
    if reaches r d then reached_but_absent_was_dropped scope_of r d
    else reached_but_absent_was_dropped_list scope_of r rest

/// **`conjunction_sound`.** An empty inherited-policy set means every
/// taint-propagating source that reached the target was DROPPED somewhere
/// on its derivation, and every drop anywhere in that derivation was made
/// by a routine entitled to make it — which, for a party-scoped policy, is
/// a routine that party accepted.
///
/// So the gate's single condition — "this list is empty" — is exactly "a
/// path satisfies every contributing party's policy", and one party's
/// consent never stands in for another's.
let conjunction_sound (scope_of: string -> opt string) (r: string) (d: derivation)
  : Lemma
      (requires is_bottom (taint_of scope_of d) /\ reaches r d)
      (ensures drop_occurred scope_of r d /\ drops_are_entitled scope_of r d) =
  reached_but_absent_was_dropped scope_of r d;
  every_drop_is_entitled scope_of r d

(* ───────────────────────────────────────────────────────────────────
   6. The trace form.

   The flow half as first filed quantified over a single output. Phase
   675's budgets meter a SEQUENCE of declassifications, so ordering and
   count are observables and a single-output statement does not reach
   them. The shape here is the one Rastogi, Swamy and Hicks proved in
   "Wys*: A DSL for Verified Secure Multi-party Computations" (2019):
   security as a delimited-release lemma over an observable TRACE, with
   each party's view a SLICE of it, and an amended computation shown to
   leak nothing new by exhibiting its trace as a function of the baseline
   trace plus public data.

   Production has no room type: the budget ledger meters crossings one at
   a time. This is the sequence those crossings form, named so the theorem
   can quantify over it. The ladder's assumed rung says so.
   ─────────────────────────────────────────────────────────────────── *)

/// One release a room makes: a pipeline, the declassification routine
/// applied to its output if any, and the parties that observe the result.
type release (src: Type0) = {
  rel_base: src;
  rel_transforms: list (transform src);
  rel_routine: opt routine;
  rel_observers: list string;
}

/// A room is an ordered sequence of releases. The order is an observable.
type room (src: Type0) = list (release src)

/// The label a release's output carries after its own declassification.
let release_label (#src: Type0) (scope_of: string -> opt string) (los: labelling src) (rl: release src)
  : label =
  let computed = output_label los rl.rel_base rl.rel_transforms in
  match rl.rel_routine with
  | OSome rt -> narrows_to (routine_clears scope_of rt) computed
  | ONone -> computed

/// One emission: what was released, the label it carries, and who sees it.
type emission (frame: Type0) = {
  ev_label: label;
  ev_value: frame;
  ev_observers: list string;
}

/// The observable trace of a run: one emission per release, in room order.
let rec run_room
  (#src #frame: Type0)
  (sem: semantics src frame)
  (scope_of: string -> opt string)
  (los: labelling src)
  (assign: assignment src frame)
  (rm: room src)
  : Tot (list (emission frame)) (decreases rm) =
  match rm with
  | [] -> []
  | rl :: rest ->
    {
      ev_label = release_label scope_of los rl;
      ev_value = run sem assign rl.rel_base rl.rel_transforms;
      ev_observers = rl.rel_observers;
    }
    :: run_room sem scope_of los assign rest

/// Party `q`'s VIEW of a trace: the ordered sub-sequence of emissions `q`
/// observes. A scoped segment `q` is outside drops out entirely — the Wys*
/// slice rule, and the conservative reading of it: `q` learns neither the
/// value nor that the release happened. Because the result is a LIST, its
/// ordering and its length are part of what the lemma below equates.
let rec slice_tr (#frame: Type0) (q: string) (tr: list (emission frame))
  : Tot (list frame) (decreases tr) =
  match tr with
  | [] -> []
  | e :: rest -> if mem_of q e.ev_observers then e.ev_value :: slice_tr q rest else slice_tr q rest

/// Nothing `q` observes was COMPUTED from `p`'s data: every release `q`
/// sees has an output label — the label before its own declassification —
/// that does not carry `p`.
///
/// **It is a condition on the room and the labelling ALONE.** Labels are
/// computed from declared source labels and the transform structure, never
/// from a frame, so whether it holds cannot depend on anyone's data.
///
/// **Taken before declassification, deliberately.** A release `q` observes
/// whose computed label carries `p` IS a declassification of `p`'s data,
/// and is outside this lemma by construction — `declassify_only_lowers` is
/// what covers it, and the boundary between the two is where budgets live.
let rec q_free_of (#src: Type0) (los: labelling src) (p q: string) (rm: room src)
  : Tot bool (decreases rm) =
  match rm with
  | [] -> true
  | rl :: rest ->
    (not (mem_of q rl.rel_observers) || not (mem p (output_label los rl.rel_base rl.rel_transforms)))
    && q_free_of los p q rest

/// **`trace_noninterference`.** The flow half over a SEQUENCE. Two runs of
/// one room whose assignments differ only in `p`'s data give `q` the same
/// slice — the same values, in the same order, and the same number of them.
/// Ordering and count are covered because the slice is a list and the
/// conclusion is list equality.
let rec trace_noninterference
  (#src #frame: Type0)
  (sem: semantics src frame)
  (scope_of: string -> opt string)
  (los: labelling src)
  (p q: string)
  (a1 a2: assignment src frame)
  (rm: room src)
  : Lemma
      (requires agree_off los p a1 a2 /\ q_free_of los p q rm)
      (ensures
        slice_tr q (run_room sem scope_of los a1 rm) == slice_tr q (run_room sem scope_of los a2 rm))
      (decreases rm) =
  match rm with
  | [] -> ()
  | rl :: rest ->
    if mem_of q rl.rel_observers then
      flow_noninterference sem los p a1 a2 rl.rel_base rl.rel_transforms
    else ();
    trace_noninterference sem scope_of los p q a1 a2 rest

/// **`refinement_leaks_no_more`.** An amended room leaks nothing new when
/// its party-`q` view can be exhibited as a FUNCTION of the baseline's
/// party-`q` view and public data: the amendment then inherits the
/// baseline's noninterference rather than needing a proof of its own.
///
/// The obligation is on whoever supplies `rebuild`; this lemma is the
/// statement that supplying it SUFFICES. That is the whole content of the
/// refinement pattern — the work is exhibiting the function, and this is
/// what the work buys.
let refinement_leaks_no_more
  (#src #frame #pub: Type0)
  (sem: semantics src frame)
  (scope_of: string -> opt string)
  (los: labelling src)
  (p q: string)
  (a1 a2: assignment src frame)
  (baseline amended: room src)
  (rebuild: list frame -> pub -> list frame)
  (public_data: pub)
  : Lemma
      (requires
        agree_off los p a1 a2 /\
        q_free_of los p q baseline /\
        (forall (a: assignment src frame).
          slice_tr q (run_room sem scope_of los a amended)
            == rebuild (slice_tr q (run_room sem scope_of los a baseline)) public_data))
      (ensures
        slice_tr q (run_room sem scope_of los a1 amended)
          == slice_tr q (run_room sem scope_of los a2 amended)) =
  trace_noninterference sem scope_of los p q a1 a2 baseline
