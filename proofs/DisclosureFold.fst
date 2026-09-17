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

/// Phase 790 — the disclosure algebra, modelled in F* and proved
/// noninterfering.
///
/// # What this module is
///
/// A hand-written model of the two functions every population door
/// runs, clause for clause:
///
///   * `DisclosureEgress.evaluate`
///     (`src/ToolUp.Facts.Core/Shared/DisclosureTypes.fs`) — the one
///     disclosure predicate, `fact × egress-surface → allow/deny`.
///   * `PopulationDisclosure.fold` / `valuesWithheld` / `disclosedStats`
///     (`src/ToolUp.Facts.Core/Shared/PopulationQueryTypes.fs`) — the
///     population fold Phase 706 lifted to a single owner, so the
///     `query_metric_population` tool and the answer planner's
///     `UseAggregate` step cannot disclose differently.
///
/// Every definition names its F# counterpart in the comment above it,
/// as `RemotingDecode.fst` does. The differential host
/// (`src/ToolUp.Platform.Tests/InProcess/DisclosureProofOracleTests.fs`)
/// runs the EXTRACTION of this module beside production and requires
/// the disclosed list, the withheld projection and the gated summary to
/// agree at once — that host is the only thing that says this model is
/// about the code that ships.
///
/// # What is opaque, and why
///
/// Three things are type parameters or host-supplied functions rather
/// than modelled, each because the fold never inspects them and a
/// second implementation here would be free to disagree with the
/// host's:
///
///   * The egress SURFACE (`FactEgressSurface`). `evaluate` hands it to
///     the resolver and reads nothing from it.
///   * A fact's PAYLOAD — every `Fact` field but `FactId`. The fold reads
///     one field and carries the record through; that the rest is
///     opaque is what lets `verdict_noninterference` say "whatever the
///     withheld facts' values" and mean it, because the model cannot
///     even see a value.
///   * The STRING ORDERING `List.sortBy fst` sorts the withheld
///     projection by. F# compares strings ordinally; F* has no string
///     comparison that extracts to `Prims` alone, so the ordering is a
///     host-supplied `before: string -> string -> bool` — the shape
///     `RemotingDecode.fst` gives a string's length. Every lemma below
///     holds for any `before` at all, which is the honest statement:
///     nothing the theorem claims depends on how the projection is
///     ordered.
///
/// The existence-level half of `PopulationStats` — counts, period
/// coverage, freshness, method mix — is likewise one opaque field the
/// gate passes through untouched, and `magnitudes_absent_iff_withheld`
/// proves it does.
///
/// # The theorem
///
/// For any ranking, any total verdict function and any policy
/// assignment:
///
///   * `no_undisclosed_output` — no fact whose verdict is not-disclosable
///     appears in the disclosed list.
///   * `withheld_is_count_only` — the withheld count and the withheld
///     projection are functions of the verdict list alone. The
///     corollary `withheld_blind_to_values` states it relationally, over
///     two rankings whose facts may differ in every field and even in
///     TYPE: pointwise-equal verdicts give an identical projection.
///   * `ranks_preserved` — every disclosed `(rank, fact)` has `fact` at
///     position `rank` of the input ranking, one-based. A withheld member
///     leaves a gap; a contiguous renumbering is refuted, not restated.
///   * `magnitudes_absent_iff_withheld` — minimum, maximum and mean are
///     absent whenever anything was withheld, the summary is untouched
///     when nothing was, and the existence-level fields ride regardless.
///   * `verdict_noninterference` — two rankings that agree on every
///     disclosable fact, and on every verdict, yield IDENTICAL
///     `PopulationDisclosure` records: the same disclosed list, the same
///     withheld count, the same withheld projection — whatever the
///     withheld facts' identities or values.
///
/// # The honest limit, stated rather than hidden
///
/// The theorem is about the FOLD. `disclosed_stats` suppresses the
/// magnitude block of whatever summary it is handed, and that summary
/// was computed by the store over the WHOLE matched population: a
/// restricted member ranked below the ceiling still contributed to the
/// store's `Mean`, and under a highest-first ranking can BE its
/// `Minimum`. Nothing here claims otherwise — `PopulationQueryTypes.fs`
/// calls this "a floor, not a proof", and the README's assumed rung
/// carries the same sentence. What IS proved is that the gate acts on
/// the summary exactly when it acts on the members, never on one
/// without the other.

module DisclosureFold

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

/// F#: `List.length`.
let rec length (#a: Type0) (xs: list a) : Tot nat (decreases xs) =
  match xs with
  | [] -> 0
  | _ :: rest -> 1 + length rest

(* ───────────────────────────────────────────────────────────────────
   The egress predicate — `Shared/DisclosureTypes.fs`.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `Disclosure` (`Shared/FactTypes.fs`) — the classification every
/// fact carries from birth.
type disclosure =
  | Surfaceable : disclosure
  | Internal : disclosure
  | Restricted : policy_ref: string -> disclosure

/// F#: `FactDisclosureVerdict` (`ToolUp.Platform.VectorKnowledgeTypes`).
/// A refusal names WHY — the classification or the policy ref — and
/// never a value.
type verdict =
  | Disclosable : verdict
  | NotDisclosable : policy_ref: string -> verdict

/// F#: `DisclosurePolicyResolver = string -> FactEgressSurface -> bool option`.
/// The surface is opaque: `evaluate` passes it through and never reads it.
type resolver (surface: Type0) = string -> surface -> opt bool

/// F#: `DisclosurePolicyResolver.denyUnknown` — no policy vocabulary
/// registered, so every ref resolves as unknown.
let deny_unknown (#surface: Type0) : resolver surface = fun _ _ -> ONone

/// F#: `DisclosureEgress.evaluate`. `Surfaceable` is disclosable at every
/// surface; `Internal` never is, and the verdict names the classification
/// as its policy ref; `Restricted` is disclosable only when the resolver
/// AFFIRMATIVELY permits it — `Some false` and `None` both deny, so an
/// unknown policy ref can never fail open.
let evaluate
  (#surface: Type0)
  (resolve_policy: resolver surface)
  (at_surface: surface)
  (d: disclosure)
  : verdict =
  match d with
  | Surfaceable -> Disclosable
  | Internal -> NotDisclosable "Internal"
  | Restricted policy_ref ->
    match resolve_policy policy_ref at_surface with
    | OSome true -> Disclosable
    | OSome false -> NotDisclosable policy_ref
    | ONone -> NotDisclosable policy_ref

(* ───────────────────────────────────────────────────────────────────
   The population fold — `Shared/PopulationQueryTypes.fs`.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `Fact`. The fold reads ONE field, `FactId`; the other eleven ride
/// through as an opaque payload the model cannot inspect.
type fact (p: Type0) = {
  fact_id: string;
  payload: p;
}

/// F#: `PopulationDisclosure` — what the gate left of a ranking.
type population_disclosure (p: Type0) = {
  /// `Disclosable: (int * Fact) list` — paired with the TRUE rank.
  disclosable: list (pair nat (fact p));
  /// `WithheldCount: int`.
  withheld_count: nat;
  /// `WithheldByPolicy: (string * int) list` — a count grouped by policy
  /// ref, ordered by ref.
  withheld_by_policy: list (pair string nat);
}

/// F#: the `List.mapi` triple — `i + 1, fact, verdictFor fact.FactId`.
type judged (p: Type0) = {
  rank: nat;
  judged_fact: fact p;
  judged_verdict: verdict;
}

/// F#: `ranked |> List.mapi (fun i fact -> i + 1, fact, verdictFor fact.FactId)`,
/// with `position` the running `i`.
let rec judge
  (#p: Type0)
  (verdict_for: string -> verdict)
  (position: nat)
  (ranked: list (fact p))
  : Tot (list (judged p)) (decreases ranked) =
  match ranked with
  | [] -> []
  | f :: rest ->
    { rank = position + 1; judged_fact = f; judged_verdict = verdict_for f.fact_id }
    :: judge verdict_for (position + 1) rest

/// F#: `judged |> List.filter (fun (_, _, verdict) -> verdict = FactDisclosable)
///              |> List.map (fun (rank, fact, _) -> rank, fact)`.
let rec disclosable_of (#p: Type0) (js: list (judged p))
  : Tot (list (pair nat (fact p))) (decreases js) =
  match js with
  | [] -> []
  | j :: rest ->
    if Disclosable? j.judged_verdict
    then Pair j.rank j.judged_fact :: disclosable_of rest
    else disclosable_of rest

/// F#: `judged |> List.choose (fun (_, _, verdict) -> match verdict with
///        | FactDisclosable -> None | FactNotDisclosable policyRef -> Some policyRef)`.
let rec withheld_refs (#p: Type0) (js: list (judged p)) : Tot (list string) (decreases js) =
  match js with
  | [] -> []
  | j :: rest ->
    match j.judged_verdict with
    | Disclosable -> withheld_refs rest
    | NotDisclosable policy_ref -> policy_ref :: withheld_refs rest

/// F#: one step of `List.countBy id` — a seen key's count advances in
/// place, an unseen key is appended, so keys keep first-occurrence order.
let rec count_into (key: string) (counts: list (pair string nat))
  : Tot (list (pair string nat)) (decreases counts) =
  match counts with
  | [] -> [Pair key 1]
  | Pair k n :: rest ->
    if k = key then Pair k (n + 1) :: rest else Pair k n :: count_into key rest

/// F#: `List.countBy id`.
let rec count_by (keys: list string) (counts: list (pair string nat))
  : Tot (list (pair string nat)) (decreases keys) =
  match keys with
  | [] -> counts
  | key :: rest -> count_by rest (count_into key counts)

/// F#: the insertion step of `List.sortBy fst`. `before a b` is the
/// host's "a sorts strictly before b"; an entry goes ahead of the first
/// key that is not strictly before its own, which is what keeps the sort
/// STABLE, as `List.sortBy` is.
let rec insert_by
  (before: string -> string -> bool)
  (entry: pair string nat)
  (sorted: list (pair string nat))
  : Tot (list (pair string nat)) (decreases sorted) =
  match sorted with
  | [] -> [entry]
  | head :: rest ->
    match entry, head with
    | Pair key _, Pair head_key _ ->
      if before head_key key then head :: insert_by before entry rest else entry :: sorted

/// F#: `List.sortBy fst`.
let rec sort_by (before: string -> string -> bool) (entries: list (pair string nat))
  : Tot (list (pair string nat)) (decreases entries) =
  match entries with
  | [] -> []
  | entry :: rest -> insert_by before entry (sort_by before rest)

/// F#: `withheldPolicies |> List.countBy id |> List.sortBy fst` — the
/// withheld projection as a function of the policy refs ALONE. Named so
/// `withheld_is_count_only` can say exactly that.
let withheld_projection (before: string -> string -> bool) (refs: list string)
  : list (pair string nat) =
  sort_by before (count_by refs [])

/// F#: `PopulationDisclosure.fold`. `verdictFor` is total — the caller
/// supplies the conservative deny for an id the gate returned nothing
/// for, so the door never fails open; the model takes it as a total
/// arrow for the same reason.
let fold
  (#p: Type0)
  (before: string -> string -> bool)
  (verdict_for: string -> verdict)
  (ranked: list (fact p))
  : population_disclosure p =
  let judged = judge verdict_for 0 ranked in
  let withheld_policies = withheld_refs judged in
  {
    disclosable = disclosable_of judged;
    withheld_count = length withheld_policies;
    withheld_by_policy = withheld_projection before withheld_policies;
  }

/// F#: `PopulationDisclosure.valuesWithheld`.
let values_withheld (#p: Type0) (d: population_disclosure p) : bool = d.withheld_count > 0

/// F#: `PopulationStats` — the magnitude block modelled, and everything
/// existence-level (counts, period coverage, freshness, method mix) as
/// one opaque field the gate passes through.
type stats (m: Type0) (r: Type0) = {
  /// `Minimum: decimal option`.
  minimum: opt m;
  /// `Maximum: decimal option`.
  maximum: opt m;
  /// `Mean: decimal option`.
  mean: opt m;
  /// Every other field of the record.
  existence: r;
}

/// F#: `PopulationDisclosure.disclosedStats` — the magnitude block is
/// gated WITH the members.
let disclosed_stats (#p #m #r: Type0) (d: population_disclosure p) (s: stats m r) : stats m r =
  if values_withheld d then { s with minimum = ONone; maximum = ONone; mean = ONone } else s

(* ───────────────────────────────────────────────────────────────────
   THE THEOREMS

   Everything from here on is ghost: the predicates carry
   `noextract_to "FSharp"` and the lemmas are erased by the extractor,
   so the oracle the host runs is exactly the definitions above.
   ─────────────────────────────────────────────────────────────────── *)

// ─── The egress predicate, characterised ─────────────────────────────

/// WHICH verdict, for which classification: `Surfaceable` is always
/// disclosable, `Internal` never, and `Restricted` exactly when the
/// resolver answers `Some true` — so a resolver that answers `Some false`
/// or `None` denies, and the two are one clause because the door must
/// not distinguish "forbidden" from "unknown".
let evaluate_characterised
  (#surface: Type0)
  (resolve_policy: resolver surface)
  (at_surface: surface)
  (d: disclosure)
  : Lemma
      (match d with
       | Surfaceable -> evaluate resolve_policy at_surface d == Disclosable
       | Internal -> evaluate resolve_policy at_surface d == NotDisclosable "Internal"
       | Restricted policy_ref ->
         (evaluate resolve_policy at_surface d == Disclosable
          <==> resolve_policy policy_ref at_surface == OSome true) /\
         (not (resolve_policy policy_ref at_surface = OSome true)
          ==> evaluate resolve_policy at_surface d == NotDisclosable policy_ref)) = ()

/// The conservative default denies every `Restricted` fact at every
/// surface, naming the ref it could not resolve.
let unknown_policy_denies (#surface: Type0) (at_surface: surface) (policy_ref: string)
  : Lemma (evaluate deny_unknown at_surface (Restricted policy_ref) == NotDisclosable policy_ref) = ()

// ─── 1. No undisclosed output ────────────────────────────────────────

/// Every disclosed fact's verdict, under `verdict_for`, is `Disclosable`.
[@@ noextract_to "FSharp"]
let rec each_disclosed (#p: Type0) (verdict_for: string -> verdict) (xs: list (pair nat (fact p)))
  : Tot bool (decreases xs) =
  match xs with
  | [] -> true
  | Pair _ f :: rest -> Disclosable? (verdict_for f.fact_id) && each_disclosed verdict_for rest

/// Every judged entry carries the verdict `verdict_for` gives its fact.
[@@ noextract_to "FSharp"]
let rec judged_by (#p: Type0) (verdict_for: string -> verdict) (js: list (judged p))
  : Tot bool (decreases js) =
  match js with
  | [] -> true
  | j :: rest -> j.judged_verdict = verdict_for j.judged_fact.fact_id && judged_by verdict_for rest

let rec judge_is_judged_by
  (#p: Type0)
  (verdict_for: string -> verdict)
  (position: nat)
  (ranked: list (fact p))
  : Lemma (ensures judged_by verdict_for (judge verdict_for position ranked)) (decreases ranked) =
  match ranked with
  | [] -> ()
  | _ :: rest -> judge_is_judged_by verdict_for (position + 1) rest

let rec disclosable_of_judged_by
  (#p: Type0)
  (verdict_for: string -> verdict)
  (js: list (judged p))
  : Lemma
      (requires judged_by verdict_for js)
      (ensures each_disclosed verdict_for (disclosable_of js))
      (decreases js) =
  match js with
  | [] -> ()
  | _ :: rest -> disclosable_of_judged_by verdict_for rest

/// **`no_undisclosed_output`.** No fact whose verdict is not-disclosable
/// appears in the disclosed list — for any ranking, any total verdict
/// function, any ordering.
let no_undisclosed_output
  (#p: Type0)
  (before: string -> string -> bool)
  (verdict_for: string -> verdict)
  (ranked: list (fact p))
  : Lemma (each_disclosed verdict_for (fold before verdict_for ranked).disclosable) =
  judge_is_judged_by verdict_for 0 ranked;
  disclosable_of_judged_by verdict_for (judge verdict_for 0 ranked)

// ─── 2. Withheld is a count only ─────────────────────────────────────

/// The verdict list — the ONLY thing about the withheld members the
/// projection is allowed to depend on.
[@@ noextract_to "FSharp"]
let rec verdicts_of (#p: Type0) (verdict_for: string -> verdict) (ranked: list (fact p))
  : Tot (list verdict) (decreases ranked) =
  match ranked with
  | [] -> []
  | f :: rest -> verdict_for f.fact_id :: verdicts_of verdict_for rest

/// The policy refs of the not-disclosable verdicts, in ranking order.
[@@ noextract_to "FSharp"]
let rec refs_of (vs: list verdict) : Tot (list string) (decreases vs) =
  match vs with
  | [] -> []
  | Disclosable :: rest -> refs_of rest
  | NotDisclosable policy_ref :: rest -> policy_ref :: refs_of rest

let rec withheld_refs_are_refs_of
  (#p: Type0)
  (verdict_for: string -> verdict)
  (position: nat)
  (ranked: list (fact p))
  : Lemma
      (ensures withheld_refs (judge verdict_for position ranked) == refs_of (verdicts_of verdict_for ranked))
      (decreases ranked) =
  match ranked with
  | [] -> ()
  | _ :: rest -> withheld_refs_are_refs_of verdict_for (position + 1) rest

/// **`withheld_is_count_only`.** The withheld count and the withheld
/// projection are functions of the verdict list alone — the count is the
/// number of not-disclosable verdicts, and the projection is
/// `withheld_projection` over their policy refs. No fact reaches either:
/// not its identity, not its rank, not its value.
let withheld_is_count_only
  (#p: Type0)
  (before: string -> string -> bool)
  (verdict_for: string -> verdict)
  (ranked: list (fact p))
  : Lemma
      (let d = fold before verdict_for ranked in
       let refs = refs_of (verdicts_of verdict_for ranked) in
       d.withheld_count == length refs /\ d.withheld_by_policy == withheld_projection before refs) =
  withheld_refs_are_refs_of verdict_for 0 ranked

/// Two rankings — of facts of possibly DIFFERENT types — whose verdicts
/// agree position for position.
[@@ noextract_to "FSharp"]
let rec same_verdicts
  (#p #q: Type0)
  (v1: string -> verdict)
  (v2: string -> verdict)
  (r1: list (fact p))
  (r2: list (fact q))
  : Tot bool (decreases r1) =
  match r1, r2 with
  | [], [] -> true
  | a :: r1', b :: r2' -> v1 a.fact_id = v2 b.fact_id && same_verdicts v1 v2 r1' r2'
  | _, _ -> false

let rec same_verdicts_same_list
  (#p #q: Type0)
  (v1: string -> verdict)
  (v2: string -> verdict)
  (r1: list (fact p))
  (r2: list (fact q))
  : Lemma
      (requires same_verdicts v1 v2 r1 r2)
      (ensures verdicts_of v1 r1 == verdicts_of v2 r2)
      (decreases r1) =
  match r1, r2 with
  | _ :: r1', _ :: r2' -> same_verdicts_same_list v1 v2 r1' r2'
  | _, _ -> ()

/// The relational reading of lemma 2: two rankings with pointwise-equal
/// verdicts have the same withheld count and the same withheld
/// projection, whatever their facts are — and the facts need not even be
/// the same TYPE, which is the strongest way to say a value was not
/// consulted.
let withheld_blind_to_values
  (#p #q: Type0)
  (before: string -> string -> bool)
  (v1: string -> verdict)
  (v2: string -> verdict)
  (r1: list (fact p))
  (r2: list (fact q))
  : Lemma
      (requires same_verdicts v1 v2 r1 r2)
      (ensures
        (fold before v1 r1).withheld_count == (fold before v2 r2).withheld_count /\
        (fold before v1 r1).withheld_by_policy == (fold before v2 r2).withheld_by_policy) =
  same_verdicts_same_list v1 v2 r1 r2;
  withheld_is_count_only before v1 r1;
  withheld_is_count_only before v2 r2

// ─── 3. Ranks preserved ──────────────────────────────────────────────

/// The element at a ZERO-based position, `ONone` past either end.
[@@ noextract_to "FSharp"]
let rec at (#a: Type0) (xs: list a) (i: int) : Tot (opt a) (decreases xs) =
  match xs with
  | [] -> ONone
  | x :: rest -> if i = 0 then OSome x else if i < 0 then ONone else at rest (i - 1)

/// Every judged entry's rank, less `offset`, is its fact's one-based
/// position in `ranked`.
[@@ noextract_to "FSharp"]
let rec each_rank_true (#p: Type0) (ranked: list (fact p)) (offset: nat) (js: list (judged p))
  : Tot prop (decreases js) =
  match js with
  | [] -> True
  | j :: rest ->
    j.rank > offset /\
    at ranked (j.rank - offset - 1) == OSome j.judged_fact /\
    each_rank_true ranked offset rest

let rec shift_rank_true
  (#p: Type0)
  (f: fact p)
  (ranked: list (fact p))
  (offset: nat)
  (js: list (judged p))
  : Lemma
      (requires each_rank_true ranked (offset + 1) js)
      (ensures each_rank_true (f :: ranked) offset js)
      (decreases js) =
  match js with
  | [] -> ()
  | _ :: rest -> shift_rank_true f ranked offset rest

let rec judge_ranks_true
  (#p: Type0)
  (verdict_for: string -> verdict)
  (ranked: list (fact p))
  (offset: nat)
  : Lemma (ensures each_rank_true ranked offset (judge verdict_for offset ranked)) (decreases ranked) =
  match ranked with
  | [] -> ()
  | f :: rest ->
    judge_ranks_true verdict_for rest (offset + 1);
    shift_rank_true f rest offset (judge verdict_for (offset + 1) rest)

/// Every disclosed `(rank, fact)` has `fact` at one-based position `rank`
/// of `ranked`.
[@@ noextract_to "FSharp"]
let rec each_pair_true (#p: Type0) (ranked: list (fact p)) (xs: list (pair nat (fact p)))
  : Tot prop (decreases xs) =
  match xs with
  | [] -> True
  | Pair r f :: rest -> r >= 1 /\ at ranked (r - 1) == OSome f /\ each_pair_true ranked rest

let rec disclosable_of_ranks_true (#p: Type0) (ranked: list (fact p)) (js: list (judged p))
  : Lemma
      (requires each_rank_true ranked 0 js)
      (ensures each_pair_true ranked (disclosable_of js))
      (decreases js) =
  match js with
  | [] -> ()
  | _ :: rest -> disclosable_of_ranks_true ranked rest

/// **`ranks_preserved`.** A disclosed fact keeps its TRUE rank: for every
/// `(rank, fact)` in the disclosed list, `fact` is at position `rank` of
/// the input ranking. A withheld member therefore leaves a visible gap
/// rather than promoting the member below it — the contiguous
/// renumbering `PopulationQueryTypes.fs` calls "a correctness defect
/// dressed as tidiness" cannot satisfy this.
let ranks_preserved
  (#p: Type0)
  (before: string -> string -> bool)
  (verdict_for: string -> verdict)
  (ranked: list (fact p))
  : Lemma (each_pair_true ranked (fold before verdict_for ranked).disclosable) =
  judge_ranks_true verdict_for ranked 0;
  disclosable_of_ranks_true ranked (judge verdict_for 0 ranked)

// ─── 4. Magnitudes absent iff withheld ───────────────────────────────

/// Whether every verdict in the ranking is disclosable.
[@@ noextract_to "FSharp"]
let rec all_disclosable (#p: Type0) (verdict_for: string -> verdict) (ranked: list (fact p))
  : Tot bool (decreases ranked) =
  match ranked with
  | [] -> true
  | f :: rest -> Disclosable? (verdict_for f.fact_id) && all_disclosable verdict_for rest

let rec refs_empty_iff_all_disclosable (#p: Type0) (verdict_for: string -> verdict) (ranked: list (fact p))
  : Lemma
      (ensures (length (refs_of (verdicts_of verdict_for ranked)) = 0 <==> all_disclosable verdict_for ranked))
      (decreases ranked) =
  match ranked with
  | [] -> ()
  | _ :: rest -> refs_empty_iff_all_disclosable verdict_for rest

/// The magnitude gate fires exactly when some verdict denied — so
/// `valuesWithheld` is the one predicate both doors read, and it reads
/// the verdicts, never the summary.
let withheld_iff_any_denied
  (#p: Type0)
  (before: string -> string -> bool)
  (verdict_for: string -> verdict)
  (ranked: list (fact p))
  : Lemma (values_withheld (fold before verdict_for ranked) <==> not (all_disclosable verdict_for ranked)) =
  withheld_is_count_only before verdict_for ranked;
  refs_empty_iff_all_disclosable verdict_for ranked

/// **`magnitudes_absent_iff_withheld`.** When anything was withheld, the
/// disclosed summary's minimum, maximum and mean are absent; when nothing
/// was, the summary is returned untouched; and the existence-level fields
/// — counts, period coverage, freshness, method mix — ride through in
/// both cases. The biconditional is over the GATE'S ACTION: it suppresses
/// exactly when `withheld_count` is positive. It does not say an absent
/// magnitude implies a withheld member — a summary over nothing
/// comparable carries no magnitudes to begin with.
let magnitudes_absent_iff_withheld
  (#p #m #r: Type0)
  (d: population_disclosure p)
  (s: stats m r)
  : Lemma
      (let gated = disclosed_stats d s in
       (values_withheld d <==> d.withheld_count > 0) /\
       (values_withheld d ==> gated.minimum == ONone /\ gated.maximum == ONone /\ gated.mean == ONone) /\
       (not (values_withheld d) ==> gated == s) /\
       gated.existence == s.existence) = ()

// ─── 5. Noninterference ──────────────────────────────────────────────

/// Two rankings AGREE when they have the same length, the same verdict
/// at every position, and the same fact at every position whose verdict
/// is disclosable. At a withheld position the facts are unconstrained:
/// different ids, different values, anything.
[@@ noextract_to "FSharp"]
let rec agree
  (#p: Type0)
  (v1: string -> verdict)
  (v2: string -> verdict)
  (r1: list (fact p))
  (r2: list (fact p))
  : Tot prop (decreases r1) =
  match r1, r2 with
  | [], [] -> True
  | a :: r1', b :: r2' ->
    v1 a.fact_id == v2 b.fact_id /\
    (Disclosable? (v1 a.fact_id) ==> a == b) /\
    agree v1 v2 r1' r2'
  | _, _ -> False

let rec judge_agree
  (#p: Type0)
  (v1: string -> verdict)
  (v2: string -> verdict)
  (position: nat)
  (r1: list (fact p))
  (r2: list (fact p))
  : Lemma
      (requires agree v1 v2 r1 r2)
      (ensures
        disclosable_of (judge v1 position r1) == disclosable_of (judge v2 position r2) /\
        withheld_refs (judge v1 position r1) == withheld_refs (judge v2 position r2))
      (decreases r1) =
  match r1, r2 with
  | _ :: r1', _ :: r2' -> judge_agree v1 v2 (position + 1) r1' r2'
  | _, _ -> ()

/// **`verdict_noninterference`.** Two rankings that agree on every
/// disclosable fact — and on every verdict — yield IDENTICAL
/// `PopulationDisclosure` records: the same disclosed list, the same
/// withheld count, the same withheld projection. The withheld facts'
/// identities and values are quantified away entirely: no observation of
/// the fold's output distinguishes one withheld population from another.
let verdict_noninterference
  (#p: Type0)
  (before: string -> string -> bool)
  (v1: string -> verdict)
  (v2: string -> verdict)
  (r1: list (fact p))
  (r2: list (fact p))
  : Lemma (requires agree v1 v2 r1 r2) (ensures fold before v1 r1 == fold before v2 r2) =
  judge_agree v1 v2 0 r1 r2
