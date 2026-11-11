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

/// Phase 793 — the AI tool gate, modelled in F* and proved sound
/// against its policy.
///
/// # What this module is
///
/// A hand-written model of the one decision behind the AI tool surface,
/// clause for clause:
///
///   * `ToolGate.decideDeclared`
///     (`src/ToolUp.AI.Server/Server/AIToolRegistry.fs`) — RBAC, grant
///     liveness and the effect ceiling, as ONE pure function over the
///     caller, the deployment's `ToolPolicy` and a tool's declared
///     `ToolEffectDeclaration`.
///   * `AIToolRegistry.ListAccessible` — the per-turn list the model is
///     offered, which is that decision applied as a filter.
///   * `AIToolRegistry.FindByName` and the agent loop's dispatch-site
///     re-check (`AIAgentEngine.fs`) — the boundary that would refuse a
///     name the model produced anyway, which is the same decision
///     applied to the tool the name resolves to.
///
/// Every definition names its F# counterpart in the comment above it,
/// as `RemotingDecode.fst` and `DisclosureFold.fst` do. The differential
/// host (`src/ToolUp.Platform.Tests/InProcess/ToolGateProofOracleTests.fs`)
/// runs the EXTRACTION of this module beside production over every
/// in-tree tool definition under generated policies, callers and grant
/// predicates, and requires the verdicts and the listed sets to agree —
/// that host is the only thing that says this model is about the code
/// that ships.
///
/// # What is opaque, and why
///
/// Two inputs are host-supplied predicates rather than modelled:
///
///   * RBAC — `source_permitted: string -> bool`, which is
///     `isToolSourcePermittedFor access` over the caller's permission
///     map, its Read/Write/Admin hierarchy and the SDK-reserved-namespace
///     exemption. The gate reads one bit of it per tool.
///   * Grant liveness — `grant_live: string -> bool`, which is
///     `moduleGrantGate ctx`: a per-request verdict over consent stamps
///     the middleware left, resolved per call so a revocation bites at
///     the next one.
///
/// Both are the earlier phases' decisions (36.A and 730). The theorem
/// takes them as total arrows and holds for EVERY pair, which is the
/// honest statement: nothing proved here depends on what those two
/// answer, only on the gate composing them with the ceiling in one
/// place. Modelling either would introduce a second implementation free
/// to disagree with the host's — the reason `DisclosureFold.fst` takes
/// its string ordering from the host.
///
/// # The theorem
///
/// For any RBAC predicate, any grant predicate, any policy and any
/// registry:
///
///   * `list_sound_against_policy` — no tool whose declared effects
///     exceed the policy's ceiling is listed. Under a bounded ceiling
///     every listed tool's every declared effect has a class the ceiling
///     names, and an undeclared tool is listed only when the policy
///     admits undeclared tools.
///   * `listed_iff_admitted` — a tool is in the list exactly when it is
///     in the registry and the decision admits it: the list IS the
///     decision, with nothing added and nothing dropped.
///   * `dispatch_never_wider_than_list` — if the dispatch re-check admits
///     a tool name, the list the model was offered carries a tool of that
///     name. The "two gates that must agree share their whole decision"
///     comment on `ListAccessible`, as a lemma.
///   * `admit_all_partition` / `admit_all_first_refusal` — the gate
///     sequence (`Gates.admitAll` over `Gates.sequence`'s five slots)
///     admits exactly when every gate passes every call, and a refusal
///     names the first refusing gate; `admitted_reach_holds_authority` — a
///     read the sequence admits holds Read and a live grant on every module
///     it reaches, so every door (the loop, the four reach tools) runs one
///     decision with no gate missing.
///   * `egress_never_escapes` — a tool declaring `Egress` to any
///     destination is refused under every bounded ceiling that does not
///     name the egress class. This is the go-red property: a gate that
///     dropped `Egress` before deciding would violate it, and the
///     differential host commits exactly that gate to show the comparison
///     catches it.

module ToolGate

(* ───────────────────────────────────────────────────────────────────
   Small closed types the model owns — declared here rather than taken
   from `FStar.Pervasives.Native` so the extraction references `Prims`
   and nothing else, as the two modules beside this one do.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `'a option`.
type opt (a: Type0) =
  | ONone : opt a
  | OSome : item: a -> opt a

/// F#: `List.contains` over an `eqtype`.
let rec mem (#a: eqtype) (x: a) (xs: list a) : Tot bool (decreases xs) =
  match xs with
  | [] -> false
  | y :: rest -> x = y || mem x rest

(* ───────────────────────────────────────────────────────────────────
   The effect vocabulary — `Shared/Types/ModuleAITypes.fs`.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `ToolEffect`. The payloads are opaque strings the gate compares
/// only through `class_of`; the envelope, not the gate, reads them.
type tool_effect =
  | ReadFacts : tool_effect
  | ComputeFacts : tool_effect
  | ReadContent : tool_effect
  | WriteState : scope: string -> tool_effect
  | Egress : destination: string -> tool_effect
  | Spend : budget_class: string -> tool_effect
  | External : capability_id: string -> tool_effect
  | EmitsActions : tool_effect

/// F#: `ToolEffectClass`.
type effect_class =
  | CReadFacts : effect_class
  | CComputeFacts : effect_class
  | CReadContent : effect_class
  | CWriteState : effect_class
  | CEgress : effect_class
  | CSpend : effect_class
  | CExternal : effect_class
  | CEmitsActions : effect_class

/// F#: `ToolEffect.classOf`.
let class_of (e: tool_effect) : effect_class =
  match e with
  | ReadFacts -> CReadFacts
  | ComputeFacts -> CComputeFacts
  | ReadContent -> CReadContent
  | WriteState _ -> CWriteState
  | Egress _ -> CEgress
  | Spend _ -> CSpend
  | External _ -> CExternal
  | EmitsActions -> CEmitsActions

/// F#: `ToolEffectDeclaration`. A declared set is carried as a list —
/// the gate reads it by membership of classes, so order and repetition
/// are invisible to every definition below.
type declaration =
  | Undeclared : declaration
  | Declared : effects: list tool_effect -> declaration

(* ───────────────────────────────────────────────────────────────────
   The policy and the decision — `AIToolRegistry.fs`.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `ToolEffectCeiling`.
type ceiling =
  | Unbounded : ceiling
  | Bounded : classes: list effect_class -> ceiling

/// F#: `ToolPolicy` — the two fields the decision reads. The approval
/// set and the external-principal ceiling are data the call sites
/// select a policy WITH (`ToolPolicy.forExternalPrincipal` substitutes
/// the latter for `ceiling`), so every lemma over `policy` covers them.
type policy = {
  ceiling: ceiling;
  permit_undeclared: bool;
}

/// F#: `ToolGateVerdict`.
type verdict =
  | Admitted : verdict
  | RefusedSource : source_module: string -> verdict
  | RefusedGrant : source_module: string -> verdict
  | RefusedUndeclared : verdict
  | RefusedEffects : exceeding: list tool_effect -> verdict

/// F#: the `List.filter` inside `ToolGate.exceeding` — the declared
/// effects whose class the bounded ceiling does not name, in order.
let rec exceeding_in (classes: list effect_class) (es: list tool_effect) : Tot (list tool_effect) (decreases es) =
  match es with
  | [] -> []
  | e :: rest ->
    if mem (class_of e) classes then exceeding_in classes rest else e :: exceeding_in classes rest

/// F#: `ToolGate.exceeding`.
let exceeding (c: ceiling) (es: list tool_effect) : list tool_effect =
  match c with
  | Unbounded -> []
  | Bounded classes -> exceeding_in classes es

/// F#: `ToolGate.decideDeclared` — THE decision. RBAC first, then grant
/// liveness, then the ceiling, so each refusal names the gate that
/// produced it and the audit lands on that gate's stream.
let decide
  (source_permitted: string -> bool)
  (grant_live: string -> bool)
  (p: policy)
  (source_module: string)
  (d: declaration)
  : verdict =
  if not (source_permitted source_module) then RefusedSource source_module
  else if not (grant_live source_module) then RefusedGrant source_module
  else
    match d with
    | Undeclared -> if p.permit_undeclared then Admitted else RefusedUndeclared
    | Declared es ->
      match exceeding p.ceiling es with
      | [] -> Admitted
      | over -> RefusedEffects over

/// F#: `ToolGate.admits`.
let admits (v: verdict) : bool = Admitted? v

(* ───────────────────────────────────────────────────────────────────
   The registry — list time and dispatch time.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `RegisteredTool`, as the gate reads it — the authored name, the
/// provider-sanitised alias `FindByName` also matches, the source module
/// and the declaration (`AIToolEffects.declaredOf`, EmitsActions folded
/// in).
type tool = {
  name: string;
  alias: string;
  source_module: string;
  effects: declaration;
}

/// F#: `AIToolRegistry.ListAccessible` — the decision as a filter, in
/// registry order.
let rec list_accessible
  (source_permitted: string -> bool)
  (grant_live: string -> bool)
  (p: policy)
  (ts: list tool)
  : Tot (list tool) (decreases ts) =
  match ts with
  | [] -> []
  | t :: rest ->
    if admits (decide source_permitted grant_live p t.source_module t.effects)
    then t :: list_accessible source_permitted grant_live p rest
    else list_accessible source_permitted grant_live p rest

/// F#: `AIToolRegistry.FindByName` — the first tool whose authored name
/// OR provider alias is the name the model produced.
let rec find_by_name (ts: list tool) (n: string) : Tot (opt tool) (decreases ts) =
  match ts with
  | [] -> ONone
  | t :: rest -> if t.name = n || t.alias = n then OSome t else find_by_name rest n

/// F#: the agent loop's dispatch-site re-check — look the name up, then
/// run the same decision on what it resolved to. An unknown name is not
/// admitted (the loop's `UnknownTool` arm).
let dispatch_admits
  (source_permitted: string -> bool)
  (grant_live: string -> bool)
  (p: policy)
  (ts: list tool)
  (n: string)
  : bool =
  match find_by_name ts n with
  | ONone -> false
  | OSome t -> admits (decide source_permitted grant_live p t.source_module t.effects)

(* ───────────────────────────────────────────────────────────────────
   The gate sequence — every door, in one order.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `ToolRefusal`, by the slot of `GateSet` that refused — which gate
/// answered, which is what the order decides.
type slot =
  | SToolGate : slot
  | SAllowlist : slot
  | SApproval : slot
  | SQueryability : slot
  | SConsent : slot

/// F#: `GateCall`, as the gates read it — the module the call reaches
/// (the tool's own source module at the loop, the TARGET module at a
/// reach tool) and the tool's declaration.
type reach = {
  reach_module: string;
  reach_effects: declaration;
}

/// F#: `Gates.sequence` — THE order, named by slot: the tool gate, the
/// allowlist, the approval, the module's opt-in, the user's consent.
let gate_order : list slot = [ SToolGate; SAllowlist; SApproval; SQueryability; SConsent ]

/// F#: one gate asking every call (`Gates.admitAll`'s inner loop) — the
/// first call it refuses answers, naming the gate.
let rec across (passes: slot -> reach -> bool) (s: slot) (calls: list reach) : Tot (opt slot) (decreases calls) =
  match calls with
  | [] -> ONone
  | x :: rest -> if passes s x then across passes s rest else OSome s

/// F#: `Gates.admitAll` — each gate, in `order`, asks every call before the
/// next gate asks any; the first refusal answers.
let rec admit_all (passes: slot -> reach -> bool) (order: list slot) (calls: list reach)
  : Tot (opt slot) (decreases order) =
  match order with
  | [] -> ONone
  | s :: rest ->
    match across passes s calls with
    | OSome refused -> OSome refused
    | ONone -> admit_all passes rest calls

/// F#: the `GateSet` a door fills: the tool gate is `decide` over the
/// module the call reaches; the other four are host-supplied predicates,
/// opaque here for the reason RBAC and grant liveness are.
let gate_passes
  (source_permitted: string -> bool)
  (grant_live: string -> bool)
  (p: policy)
  (allowlist: reach -> bool)
  (approval: reach -> bool)
  (queryable: reach -> bool)
  (consent: reach -> bool)
  (s: slot)
  (c: reach)
  : bool =
  match s with
  | SToolGate -> admits (decide source_permitted grant_live p c.reach_module c.reach_effects)
  | SAllowlist -> allowlist c
  | SApproval -> approval c
  | SQueryability -> queryable c
  | SConsent -> consent c

/// F#: `Gates.admitAll set calls` at any door — the sequence over the
/// calls one read makes (one at the loop and at three reach tools; one per
/// producer at `query_entity`).
let admit_reach
  (source_permitted: string -> bool)
  (grant_live: string -> bool)
  (p: policy)
  (allowlist: reach -> bool)
  (approval: reach -> bool)
  (queryable: reach -> bool)
  (consent: reach -> bool)
  (calls: list reach)
  : opt slot =
  admit_all (gate_passes source_permitted grant_live p allowlist approval queryable consent) gate_order calls

(* ───────────────────────────────────────────────────────────────────
   THE THEOREMS

   Everything from here on is ghost: the predicates carry
   `noextract_to "FSharp"` and the lemmas are erased by the extractor,
   so the oracle the host runs is exactly the definitions above.
   ─────────────────────────────────────────────────────────────────── *)

// ─── 1. The list is sound against the policy ─────────────────────────

/// Every declared effect has a class the ceiling names.
[@@ noextract_to "FSharp"]
let rec each_class_admitted (classes: list effect_class) (es: list tool_effect) : Tot bool (decreases es) =
  match es with
  | [] -> true
  | e :: rest -> mem (class_of e) classes && each_class_admitted classes rest

let rec exceeding_nil_iff_each_admitted (classes: list effect_class) (es: list tool_effect)
  : Lemma (ensures (Nil? (exceeding_in classes es) <==> each_class_admitted classes es)) (decreases es) =
  match es with
  | [] -> ()
  | _ :: rest -> exceeding_nil_iff_each_admitted classes rest

/// A declaration sits WITHIN a policy: an undeclared tool only where the
/// policy admits undeclared tools; a declared one under an unbounded
/// ceiling always, and under a bounded ceiling exactly when every
/// declared effect's class is named. This is what "does not exceed the
/// policy's ceiling" means, spelled out.
[@@ noextract_to "FSharp"]
let within_ceiling (p: policy) (d: declaration) : bool =
  match d with
  | Undeclared -> p.permit_undeclared
  | Declared es ->
    match p.ceiling with
    | Unbounded -> true
    | Bounded classes -> each_class_admitted classes es

/// An admitting verdict implies the declaration is within the ceiling
/// — and the two host predicates both answered yes. The converse holds
/// too; the equality is what makes the decision EXACTLY the ceiling
/// check once authority is settled.
let admitted_iff_within
  (source_permitted: string -> bool)
  (grant_live: string -> bool)
  (p: policy)
  (source_module: string)
  (d: declaration)
  : Lemma
      (admits (decide source_permitted grant_live p source_module d)
       = (source_permitted source_module && grant_live source_module && within_ceiling p d)) =
  match d with
  | Undeclared -> ()
  | Declared es ->
    match p.ceiling with
    | Unbounded -> ()
    | Bounded classes -> exceeding_nil_iff_each_admitted classes es

/// Every listed tool is within the policy.
[@@ noextract_to "FSharp"]
let rec each_within (p: policy) (ts: list tool) : Tot bool (decreases ts) =
  match ts with
  | [] -> true
  | t :: rest -> within_ceiling p t.effects && each_within p rest

/// **`list_sound_against_policy`.** No tool whose declared effects
/// exceed the policy's ceiling is listed — for any RBAC predicate, any
/// grant predicate, any policy, any registry.
let rec list_sound_against_policy
  (source_permitted: string -> bool)
  (grant_live: string -> bool)
  (p: policy)
  (ts: list tool)
  : Lemma (ensures each_within p (list_accessible source_permitted grant_live p ts)) (decreases ts) =
  match ts with
  | [] -> ()
  | t :: rest ->
    admitted_iff_within source_permitted grant_live p t.source_module t.effects;
    list_sound_against_policy source_permitted grant_live p rest

// ─── 2. The list IS the decision ─────────────────────────────────────

/// **`listed_iff_admitted`.** A tool is listed exactly when it is
/// registered and the decision admits it. Nothing reaches the list the
/// decision refused, and nothing the decision admits is dropped — the
/// list filter and the decision are one function, not two that agree.
let rec listed_iff_admitted
  (source_permitted: string -> bool)
  (grant_live: string -> bool)
  (p: policy)
  (ts: list tool)
  (t: tool)
  : Lemma
      (ensures
        (mem t (list_accessible source_permitted grant_live p ts)
         <==> (mem t ts /\ admits (decide source_permitted grant_live p t.source_module t.effects))))
      (decreases ts) =
  match ts with
  | [] -> ()
  | _ :: rest -> listed_iff_admitted source_permitted grant_live p rest t

// ─── 3. Dispatch is never wider than the list ────────────────────────

/// Some tool in `ts` carries `n` as its name or its alias.
[@@ noextract_to "FSharp"]
let rec has_named (ts: list tool) (n: string) : Tot bool (decreases ts) =
  match ts with
  | [] -> false
  | t :: rest -> t.name = n || t.alias = n || has_named rest n

let rec find_by_name_mem (ts: list tool) (n: string)
  : Lemma
      (ensures
        (match find_by_name ts n with
         | ONone -> True
         | OSome t -> mem t ts /\ (t.name = n \/ t.alias = n)))
      (decreases ts) =
  match ts with
  | [] -> ()
  | _ :: rest -> find_by_name_mem rest n

let rec mem_named (ts: list tool) (t: tool) (n: string)
  : Lemma
      (requires mem t ts /\ (t.name = n \/ t.alias = n))
      (ensures has_named ts n)
      (decreases ts) =
  match ts with
  | [] -> ()
  | head :: rest -> if head = t then () else mem_named rest t n

/// **`dispatch_never_wider_than_list`.** If the dispatch re-check admits
/// the name the model produced, the list the model was offered carries a
/// tool of that name. A forged, hallucinated or replayed name that the
/// list would have refused is refused at dispatch too — the two gates
/// share their whole decision.
let dispatch_never_wider_than_list
  (source_permitted: string -> bool)
  (grant_live: string -> bool)
  (p: policy)
  (ts: list tool)
  (n: string)
  : Lemma
      (requires dispatch_admits source_permitted grant_live p ts n)
      (ensures has_named (list_accessible source_permitted grant_live p ts) n) =
  find_by_name_mem ts n;
  match find_by_name ts n with
  | ONone -> ()
  | OSome t ->
    listed_iff_admitted source_permitted grant_live p ts t;
    mem_named (list_accessible source_permitted grant_live p ts) t n

// ─── 4. Egress never escapes a ceiling that does not name it ─────────

let rec egress_exceeds (classes: list effect_class) (es: list tool_effect) (destination: string)
  : Lemma
      (requires not (mem CEgress classes) /\ mem (Egress destination) es)
      (ensures not (Nil? (exceeding_in classes es)))
      (decreases es) =
  match es with
  | [] -> ()
  | e :: rest -> if e = Egress destination then () else egress_exceeds classes rest destination

/// **`egress_never_escapes`.** A tool declaring `Egress` to any
/// destination is refused under every bounded ceiling that does not name
/// the egress class, whatever else it declares and whatever the two host
/// predicates answer. This is the property the differential host's
/// go-red gate — one that drops `Egress` before deciding — violates, and
/// which the comparison is asserted to catch.
let egress_never_escapes
  (source_permitted: string -> bool)
  (grant_live: string -> bool)
  (classes: list effect_class)
  (permit_undeclared: bool)
  (source_module: string)
  (es: list tool_effect)
  (destination: string)
  : Lemma
      (requires not (mem CEgress classes) /\ mem (Egress destination) es)
      (ensures
        not (admits (decide source_permitted grant_live
                       { ceiling = Bounded classes; permit_undeclared = permit_undeclared }
                       source_module (Declared es)))) =
  egress_exceeds classes es destination

// ─── 5. The gate sequence: its partition, and authority at every door ─

/// Gate `s` passes every call.
[@@ noextract_to "FSharp"]
let rec passes_all (passes: slot -> reach -> bool) (s: slot) (calls: list reach) : Tot bool (decreases calls) =
  match calls with
  | [] -> true
  | x :: rest -> passes s x && passes_all passes s rest

/// Every gate of `order` passes every call.
[@@ noextract_to "FSharp"]
let rec every_gate_passes (passes: slot -> reach -> bool) (order: list slot) (calls: list reach)
  : Tot bool (decreases order) =
  match order with
  | [] -> true
  | s :: rest -> passes_all passes s calls && every_gate_passes passes rest calls

let rec across_none_iff (passes: slot -> reach -> bool) (s: slot) (calls: list reach)
  : Lemma (ensures (ONone? (across passes s calls) <==> passes_all passes s calls)) (decreases calls) =
  match calls with
  | [] -> ()
  | _ :: rest -> across_none_iff passes s rest

/// **`admit_all_partition`.** The sequence admits exactly when every gate
/// passes every call, and refuses exactly when some gate refuses some call
/// — for any gates, any order, any number of calls.
let rec admit_all_partition (passes: slot -> reach -> bool) (order: list slot) (calls: list reach)
  : Lemma (ensures (ONone? (admit_all passes order calls) <==> every_gate_passes passes order calls))
      (decreases order) =
  match order with
  | [] -> ()
  | s :: rest ->
    across_none_iff passes s calls;
    admit_all_partition passes rest calls

let rec across_refuses_with (passes: slot -> reach -> bool) (s: slot) (calls: list reach)
  : Lemma (ensures (match across passes s calls with | ONone -> True | OSome t -> t == s)) (decreases calls) =
  match calls with
  | [] -> ()
  | _ :: rest -> across_refuses_with passes s rest

/// **`admit_all_first_refusal`.** A refusal names the FIRST gate in the
/// order that refuses some call, and every gate before it passed every
/// call: the order is observable, and a later gate (a dialog) is never
/// asked while an earlier one refuses.
let admit_all_first_refusal (passes: slot -> reach -> bool) (s: slot) (rest: list slot) (calls: list reach)
  : Lemma
      (ensures
        (if passes_all passes s calls
         then admit_all passes (s :: rest) calls == admit_all passes rest calls
         else admit_all passes (s :: rest) calls == OSome s)) =
  across_none_iff passes s calls;
  across_refuses_with passes s calls

let rec passes_all_mem (passes: slot -> reach -> bool) (s: slot) (calls: list reach) (c: reach)
  : Lemma (requires passes_all passes s calls /\ mem c calls) (ensures passes s c) (decreases calls) =
  match calls with
  | [] -> ()
  | x :: rest -> if x = c then () else passes_all_mem passes s rest c

/// **`admitted_reach_holds_authority`.** If the sequence admits a read, the
/// caller holds Read on, and a live grant over, EVERY module the read
/// reaches, its declaration sits within the ceiling, and every host gate
/// passed for it. This is what the `query_entity` flip (read permission and
/// grant liveness over each producer) and `query_module`'s grant check
/// are: a reach tool is a door like the loop, with no gate missing.
let admitted_reach_holds_authority
  (source_permitted: string -> bool)
  (grant_live: string -> bool)
  (p: policy)
  (allowlist: reach -> bool)
  (approval: reach -> bool)
  (queryable: reach -> bool)
  (consent: reach -> bool)
  (calls: list reach)
  (c: reach)
  : Lemma
      (requires
        ONone? (admit_reach source_permitted grant_live p allowlist approval queryable consent calls) /\ mem c calls)
      (ensures
        source_permitted c.reach_module /\ grant_live c.reach_module /\ within_ceiling p c.reach_effects /\
        allowlist c /\ approval c /\ queryable c /\ consent c) =
  let passes = gate_passes source_permitted grant_live p allowlist approval queryable consent in
  admit_all_partition passes gate_order calls;
  passes_all_mem passes SToolGate calls c;
  passes_all_mem passes SAllowlist calls c;
  passes_all_mem passes SApproval calls c;
  passes_all_mem passes SQueryability calls c;
  passes_all_mem passes SConsent calls c;
  admitted_iff_within source_permitted grant_live p c.reach_module c.reach_effects

/// **`loop_door_never_wider_than_list`.** At the loop the call reaches the
/// tool's own source module, so a call the sequence admits is one the
/// decision admits — the tool the list would have offered.
let loop_door_never_wider_than_list
  (source_permitted: string -> bool)
  (grant_live: string -> bool)
  (p: policy)
  (allowlist: reach -> bool)
  (approval: reach -> bool)
  (queryable: reach -> bool)
  (consent: reach -> bool)
  (t: tool)
  : Lemma
      (requires
        ONone? (admit_reach source_permitted grant_live p allowlist approval queryable consent
                  [ { reach_module = t.source_module; reach_effects = t.effects } ]))
      (ensures admits (decide source_permitted grant_live p t.source_module t.effects)) =
  let c = { reach_module = t.source_module; reach_effects = t.effects } in
  admitted_reach_holds_authority source_permitted grant_live p allowlist approval queryable consent [ c ] c
