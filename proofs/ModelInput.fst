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

/// Phase 792 — model-input admissibility, modelled in F* and proved.
///
/// # What this module is
///
/// A hand-written model of `src/ToolUp.AI.Core/Shared/ModelInput.fs`,
/// clause for clause: the closed value a provider is shown, the smart
/// constructor that refuses a fact the disclosure gate did not admit,
/// and the pure `render` that turns the value into what the provider
/// interface accepts. Every definition names its F# counterpart in the
/// comment above it, as `RemotingDecode.fst` and `DisclosureFold.fst`
/// do. The differential host
/// (`src/ToolUp.Platform.Tests/InProcess/ModelInputProofOracleTests.fs`)
/// runs the EXTRACTION of this module beside production over a seeded
/// population and a deliberately leaky store, and requires the value
/// AND the rendering to agree — that host is the only thing that says
/// this model is about the code that ships.
///
/// # The sentence being mechanised
///
/// *A model is isolated from knowledge except what is explicitly
/// permitted.* As a refinement on a type: every fact in the value
/// carries an affirmative disclosure verdict for the resolved scope,
/// every chunk carries a passing gate result or is marked ungated, and
/// rendering introduces nothing that is not in the value.
///
/// # What is opaque, and why
///
/// Three things are host-supplied rather than modelled, each because a
/// second implementation here would be free to disagree with the
/// host's:
///
///   * SUBSTRING CONTAINMENT. `leakedFactIds` asks `text.Contains id`.
///     F* has no substring test that extracts to `Prims` alone, so the
///     model takes a `contains: string -> string -> bool` from the
///     host — the shape `DisclosureFold.fst` gives the string ordering
///     and `RemotingDecode.fst` gives a string's length. Every lemma
///     below holds for ANY containment test, which is the honest
///     statement: nothing claimed here depends on how it is decided.
///   * SCOPE RESOLUTION. `fact_scope` is a string the caller resolved.
///     The model compares it and never derives it; that a principal's
///     scope was resolved correctly is an assumption, stated as one on
///     the README's assumed rung, and the subject of a later phase.
///   * THE FACT RENDERER. `build` takes the function that turns
///     admitted facts into the block a reader sees as an argument, so
///     `input_noninterference` quantifies over every such renderer
///     rather than pinning one.
///
/// # The theorem
///
///   * `admissible_by_construction` — for ANY candidate store, whatever
///     verdicts and scopes it carries, the value assembled from it
///     holds only facts with an affirmative verdict for the resolved
///     scope. The constructor's refusal is the invariant: a leak cannot
///     be placed in the value, so admissibility is not a property
///     anyone has to review for.
///   * `render_faithful` — the turns pass through untouched, the system
///     prompt is exactly the blocks' text joined, and an id the
///     rendering mentions but the value does not declare is REPORTED
///     rather than absorbed. Its companion `render_blind_to_facts` says
///     the stronger half relationally: rendering cannot read the facts,
///     the chunks or the tool results at all, so it cannot emit a fact
///     that some builder did not already put in a block.
///   * `input_noninterference` — two fact stores that agree on every
///     fact disclosable for the scope assemble to the SAME value and
///     the same rendering, whatever the non-disclosable facts contain:
///     different ids, different values, different counts.
///
/// # The honest limits, stated rather than hidden
///
/// **The chunk half is CHECKED, not enforced.** `ModelInput.addChunk`
/// accepts a chunk whatever its gate result, and the shipped module
/// offers `hasFailedGates` for a caller to ask. So the chunk clause is
/// proved as a PREMISE on `add_chunk` (`add_chunk_preserves_admissible`)
/// and as a decision procedure (`has_failed_gates_decides`), never as an
/// unconditional invariant the way the fact clause is. The README's
/// assumed rung carries the same sentence.
///
/// **The theorem is about the VALUE and its rendering.** What a builder
/// chose to put in a block, what the transport does with the rendering,
/// and what the model then says are all outside it.

module ModelInput

(* ───────────────────────────────────────────────────────────────────
   Small closed types the model owns.

   Declared here rather than taken from `FStar.Pervasives.Native` so the
   EXTRACTION references `Prims` and nothing else — the same reason the
   two earlier models give, and the same shim it lets the oracle host
   compile against.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `'a option`.
type opt (a: Type0) =
  | ONone : opt a
  | OSome : item: a -> opt a

/// F#: `Result<'a, string>` — what the smart constructor returns.
type outcome (a: Type0) =
  | Admitted : item: a -> outcome a
  | Refused : reason: string -> outcome a

/// F#: `List.append`. Hand-written so the extraction stays inside the
/// shim the two earlier models already need.
let rec append (#a: Type0) (xs: list a) (ys: list a) : Tot (list a) (decreases xs) =
  match xs with
  | [] -> ys
  | x :: rest -> x :: append rest ys

/// F#: `String.concat sep parts`. Empty for no parts, the part itself
/// for one, separator-joined beyond that — which is what makes a single
/// lifted system prompt render back byte for byte.
let rec join (sep: string) (parts: list string) : Tot string (decreases parts) =
  match parts with
  | [] -> ""
  | [p] -> p
  | p :: rest -> p ^ sep ^ join sep rest

/// F#: `Set.contains` over the declared ids, which is list membership
/// at this size.
let rec mem_id (id: string) (ids: list string) : Tot bool (decreases ids) =
  match ids with
  | [] -> false
  | x :: rest -> if x = id then true else mem_id id rest

(* ───────────────────────────────────────────────────────────────────
   The value — `Shared/ModelInput.fs`.

   Field names are distinct across every record on purpose: the F#
   backend emits all six into ONE module, where a repeated field name
   would make a record literal ambiguous at the host.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `FactDisclosureVerdict`. A refusal names WHY — the
/// classification or the policy ref — and never a value.
type verdict =
  | Disclosable : verdict
  | NotDisclosable : policy_ref: string -> verdict

/// F#: `FactDisclosureVerdict.refusalText` — the canonical wording,
/// which names the policy and never the value.
let refusal_text (policy_ref: string) : string =
  "computed, but not disclosable under policy " ^ policy_ref

/// F#: `DisclosedFact` — the fact, the verdict that admitted it, and
/// the scope that verdict was resolved for.
type disclosed_fact = {
  fact_id: string;
  fact_value: string;
  fact_verdict: verdict;
  fact_scope: string;
}

/// F#: `ChunkGateResult`. `NotGated` is the honest reading before the
/// gate lands — it says the gate did not run, never that it passed.
type gate_result =
  | GatePassed : gate_result
  | GateFailed : reason: string -> gate_result
  | NotGated : gate_result

/// F#: `Chunk` — a retrieved chunk carrying its gate result.
type chunk = {
  chunk_id: string;
  chunk_text: string;
  chunk_gate: gate_result;
  chunk_source: opt string;
}

/// F#: `PromptBlock` — one builder's contribution, joined in list
/// order.
type prompt_block = {
  block_builder_id: string;
  block_text: string;
}

/// F#: `ToolResult` — what remains after the budget elided whatever it
/// elided.
type tool_result = {
  tool_name: string;
  tool_content: string;
}

/// F#: `AIProviderMessage`, narrowed to the three fields rendering
/// reads. The call-id and multimodal-part fields ride outside the model
/// because `renderedText` does not consult them; the host's bridge
/// carries the production record through unchanged, so the narrowing is
/// stated rather than assumed.
type message = {
  message_role: string;
  message_content: string;
  message_tool_results: list tool_result;
}

/// F#: `ModelInput` — everything a provider is shown, as one closed
/// value.
type model_input = {
  input_facts: list disclosed_fact;
  input_chunks: list chunk;
  input_prompt_blocks: list prompt_block;
  input_tool_results: list tool_result;
  input_messages: list message;
}

/// F#: `ProviderMessages` — exactly the two arguments the provider
/// interface takes.
type provider_messages = {
  rendered_system_prompt: opt string;
  rendered_messages: list message;
}

(* ───────────────────────────────────────────────────────────────────
   The constructors — `module ModelInput`.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: `ModelInput.empty`.
let empty: model_input = {
  input_facts = [];
  input_chunks = [];
  input_prompt_blocks = [];
  input_tool_results = [];
  input_messages = [];
}

/// F#: `ModelInput.tryAddFact` — refuse a fact the disclosure gate did
/// not admit. THE refinement: the type cannot be populated by a leak,
/// so "every fact in the value was disclosable" holds by construction
/// rather than by review.
let try_add_fact (f: disclosed_fact) : outcome disclosed_fact =
  match f.fact_verdict with
  | Disclosable -> Admitted f
  | NotDisclosable policy_ref ->
    Refused ("Fact " ^ f.fact_id ^ " refused: " ^ refusal_text policy_ref)

/// F#: `ModelInput.addFact` — append a fact, or refuse it.
let add_fact (f: disclosed_fact) (input: model_input) : outcome model_input =
  match try_add_fact f with
  | Refused reason -> Refused reason
  | Admitted admitted -> Admitted ({ input with input_facts = append input.input_facts [admitted] })

/// F#: `ModelInput.addChunk`. Note what is NOT here: no refusal arm.
/// The shipped constructor takes a chunk whatever its gate result, and
/// `has_failed_gates` below is what a caller asks instead.
let add_chunk (c: chunk) (input: model_input) : model_input =
  { input with input_chunks = append input.input_chunks [c] }

/// F#: `ModelInput.addBlock`.
let add_block (b: prompt_block) (input: model_input) : model_input =
  { input with input_prompt_blocks = append input.input_prompt_blocks [b] }

/// F#: `ModelInput.addToolResult`.
let add_tool_result (t: tool_result) (input: model_input) : model_input =
  { input with input_tool_results = append input.input_tool_results [t] }

/// F#: the comprehension inside `ModelInput.withMessages` — every tool
/// result the turns carry, in order. Production reads the call id into
/// the tool-name field; the host's bridge performs that renaming, so
/// the model sees the field already named.
let rec tool_results_of (ms: list message) : Tot (list tool_result) (decreases ms) =
  match ms with
  | [] -> []
  | m :: rest -> append m.message_tool_results (tool_results_of rest)

/// F#: `ModelInput.withMessages` — set the turns, mirroring any tool
/// results they carry so the typed record cannot drift from what the
/// messages actually hold.
let with_messages (ms: list message) (input: model_input) : model_input =
  { input with input_messages = ms; input_tool_results = tool_results_of ms }

/// F#: `ModelInput.ofSystemPrompt` — the migration seam. A present
/// prompt becomes the one block that builder contributed, an absent one
/// becomes no blocks, and `render` sends both back out unchanged.
let of_system_prompt (builder_id: string) (system_prompt: opt string) (ms: list message) : model_input =
  let with_blocks =
    match system_prompt with
    | ONone -> empty
    | OSome text -> add_block ({ block_builder_id = builder_id; block_text = text }) empty
  in
  with_messages ms with_blocks

(* ───────────────────────────────────────────────────────────────────
   Rendering — the one pure function the provider boundary runs.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: the block-text projection `render` maps over.
let rec block_texts (blocks: list prompt_block) : Tot (list string) (decreases blocks) =
  match blocks with
  | [] -> []
  | b :: rest -> b.block_text :: block_texts rest

/// F#: each tool result's content on a turn.
let rec tool_contents (ts: list tool_result) : Tot (list string) (decreases ts) =
  match ts with
  | [] -> []
  | t :: rest -> t.tool_content :: tool_contents rest

/// F#: `ModelInput.render`. Pure, total and additive-free: the system
/// prompt is the blocks' text joined in order with the same
/// double-newline separator the prompt composer uses, and the turns
/// pass through untouched. Nothing is reformatted and nothing is
/// introduced — which is exactly what `render_blind_to_facts` proves,
/// since the two fields read here are the only two it can see.
let render (input: model_input) : provider_messages = {
  rendered_system_prompt =
    (match input.input_prompt_blocks with
     | [] -> ONone
     | blocks -> OSome (join "\n\n" (block_texts blocks)));
  rendered_messages = input.input_messages;
}

/// F#: the comprehension inside `ModelInput.renderedText` — each turn's
/// content, then each of its tool results' content, in order.
let rec message_texts (ms: list message) : Tot (list string) (decreases ms) =
  match ms with
  | [] -> []
  | m :: rest -> m.message_content :: append (tool_contents m.message_tool_results) (message_texts rest)

/// F#: `ModelInput.renderedText` — every byte the provider is handed,
/// in the order it is handed them.
let rendered_text (input: model_input) : string =
  let r = render input in
  let head =
    match r.rendered_system_prompt with
    | ONone -> ""
    | OSome s -> s
  in
  join "\n" (head :: message_texts r.rendered_messages)

/// F#: the id projection behind `ModelInput.disclosedFactIds`.
let rec fact_ids (fs: list disclosed_fact) : Tot (list string) (decreases fs) =
  match fs with
  | [] -> []
  | f :: rest -> f.fact_id :: fact_ids rest

/// F#: `ModelInput.disclosedFactIds`.
let disclosed_fact_ids (input: model_input) : list string = fact_ids input.input_facts

/// F#: the filter inside `ModelInput.leakedFactIds` — a candidate the
/// rendering mentions and the value does not declare.
let rec leaked_of
  (contains: string -> string -> bool)
  (text: string)
  (declared: list string)
  (candidates: list string)
  : Tot (list string) (decreases candidates) =
  match candidates with
  | [] -> []
  | id :: rest ->
    if contains text id
    then
      (if mem_id id declared
       then leaked_of contains text declared rest
       else id :: leaked_of contains text declared rest)
    else leaked_of contains text declared rest

/// F#: `ModelInput.leakedFactIds`. The candidate list is required
/// rather than inferred because a leak can only be detected for an id
/// the caller can name — the same honest limit the string probe works
/// under.
let leaked_fact_ids (contains: string -> string -> bool) (candidates: list string) (input: model_input)
  : list string =
  leaked_of contains (rendered_text input) (disclosed_fact_ids input) candidates

/// F#: the existential behind `ModelInput.hasFailedGates`.
let rec any_failed (cs: list chunk) : Tot bool (decreases cs) =
  match cs with
  | [] -> false
  | c :: rest -> if GateFailed? c.chunk_gate then true else any_failed rest

/// F#: `ModelInput.hasFailedGates`.
let has_failed_gates (input: model_input) : bool = any_failed input.input_chunks

(* ───────────────────────────────────────────────────────────────────
   The assembly a door performs.

   `ModelInput.fs` ships the constructors; the FOLD over a candidate set
   is what every caller writes around them — the population door's
   top-k, a retrieval builder's admitted chunks. It is modelled here
   because the admissibility and noninterference statements are about
   THE ASSEMBLY, not about one `addFact` call, and a model that stopped
   at the constructor could say nothing about a store.

   A refusal is DROPPED rather than propagated, which is the shape a
   door takes when the gate has already denied some members: the denied
   ones are a withheld count, not an abort.
   ─────────────────────────────────────────────────────────────────── *)

/// F#: the caller's admission test — this scope, and an affirmative
/// verdict.
let disclosable_for (scope: string) (f: disclosed_fact) : bool =
  if f.fact_scope = scope then Disclosable? f.fact_verdict else false

/// The sublist of a store that is disclosable for the scope — the ONLY
/// thing the assembly is allowed to depend on.
let rec visible (scope: string) (store: list disclosed_fact) : Tot (list disclosed_fact) (decreases store) =
  match store with
  | [] -> []
  | f :: rest -> if disclosable_for scope f then f :: visible scope rest else visible scope rest

/// F#: the fold a door writes over `ModelInput.addFact`.
let rec admit_all (scope: string) (store: list disclosed_fact) (input: model_input)
  : Tot model_input (decreases store) =
  match store with
  | [] -> input
  | f :: rest ->
    if f.fact_scope = scope
    then
      (match add_fact f input with
       | Admitted next -> admit_all scope rest next
       | Refused _ -> admit_all scope rest input)
    else admit_all scope rest input

/// F#: a door's whole input-side assembly — admit what the scope
/// permits, render the admitted facts into the block a reader sees, and
/// attach the turns. The renderer is taken as an argument so the
/// theorem quantifies over every one of them rather than pinning the
/// population door's.
let build
  (render_facts: list disclosed_fact -> string)
  (builder_id: string)
  (scope: string)
  (store: list disclosed_fact)
  (ms: list message)
  : model_input =
  let admitted = admit_all scope store empty in
  let blocked =
    add_block ({ block_builder_id = builder_id; block_text = render_facts admitted.input_facts }) admitted
  in
  with_messages ms blocked

(* ───────────────────────────────────────────────────────────────────
   THE THEOREMS

   Everything from here on is ghost: the predicates carry
   `noextract_to "FSharp"` and the lemmas are erased by the extractor,
   so the oracle the host runs is exactly the definitions above.
   ─────────────────────────────────────────────────────────────────── *)

// ─── 1. Admissible by construction ───────────────────────────────────

/// Every fact carries an affirmative verdict.
[@@ noextract_to "FSharp"]
let rec all_disclosable (fs: list disclosed_fact) : Tot bool (decreases fs) =
  match fs with
  | [] -> true
  | f :: rest -> Disclosable? f.fact_verdict && all_disclosable rest

/// Every fact was resolved for THIS scope — the half a verdict alone
/// does not carry, since an affirmative verdict for somebody else's
/// scope is not permission.
[@@ noextract_to "FSharp"]
let rec all_for_scope (scope: string) (fs: list disclosed_fact) : Tot bool (decreases fs) =
  match fs with
  | [] -> true
  | f :: rest -> f.fact_scope = scope && all_for_scope scope rest

/// Every chunk passed its gate or is marked ungated.
[@@ noextract_to "FSharp"]
let rec all_gates_ok (cs: list chunk) : Tot bool (decreases cs) =
  match cs with
  | [] -> true
  | c :: rest -> not (GateFailed? c.chunk_gate) && all_gates_ok rest

/// The refinement, as one predicate over the value.
[@@ noextract_to "FSharp"]
let admissible (scope: string) (input: model_input) : bool =
  all_disclosable input.input_facts
  && all_for_scope scope input.input_facts
  && all_gates_ok input.input_chunks

let rec all_disclosable_append (fs: list disclosed_fact) (gs: list disclosed_fact)
  : Lemma
      (ensures all_disclosable (append fs gs) == (all_disclosable fs && all_disclosable gs))
      (decreases fs) =
  match fs with
  | [] -> ()
  | _ :: rest -> all_disclosable_append rest gs

let rec all_for_scope_append (scope: string) (fs: list disclosed_fact) (gs: list disclosed_fact)
  : Lemma
      (ensures all_for_scope scope (append fs gs) == (all_for_scope scope fs && all_for_scope scope gs))
      (decreases fs) =
  match fs with
  | [] -> ()
  | _ :: rest -> all_for_scope_append scope rest gs

let rec all_gates_ok_append (cs: list chunk) (ds: list chunk)
  : Lemma
      (ensures all_gates_ok (append cs ds) == (all_gates_ok cs && all_gates_ok ds))
      (decreases cs) =
  match cs with
  | [] -> ()
  | _ :: rest -> all_gates_ok_append rest ds

/// `add_fact` cannot break the invariant, and needs no premise on the
/// fact's verdict to say so: the arm that would break it is the arm
/// that refuses.
let add_fact_preserves_admissible (scope: string) (f: disclosed_fact) (input: model_input)
  : Lemma
      (requires admissible scope input /\ f.fact_scope == scope)
      (ensures
        (match add_fact f input with
         | Refused _ -> True
         | Admitted next -> admissible scope next)) =
  all_disclosable_append input.input_facts [f];
  all_for_scope_append scope input.input_facts [f]

/// `add_chunk` DOES need one, and that is the honest shape of the
/// shipped constructor: it accepts a failed chunk, so the premise is
/// the caller's obligation rather than the type's guarantee.
let add_chunk_preserves_admissible (scope: string) (c: chunk) (input: model_input)
  : Lemma
      (requires admissible scope input /\ not (GateFailed? c.chunk_gate))
      (ensures admissible scope (add_chunk c input)) =
  all_gates_ok_append input.input_chunks [c]

/// …and `has_failed_gates` is the total decision procedure for the
/// clause the constructor does not enforce, so a caller can always ask.
let rec any_failed_decides (cs: list chunk)
  : Lemma (ensures any_failed cs == not (all_gates_ok cs)) (decreases cs) =
  match cs with
  | [] -> ()
  | _ :: rest -> any_failed_decides rest

let has_failed_gates_decides (input: model_input)
  : Lemma (has_failed_gates input == not (all_gates_ok input.input_chunks)) =
  any_failed_decides input.input_chunks

/// The three constructors that touch neither facts nor chunks cannot
/// affect admissibility at all.
let blocks_and_turns_preserve_admissible
  (scope: string)
  (b: prompt_block)
  (t: tool_result)
  (ms: list message)
  (input: model_input)
  : Lemma
      (requires admissible scope input)
      (ensures
        admissible scope (add_block b input) /\
        admissible scope (add_tool_result t input) /\
        admissible scope (with_messages ms input)) = ()

/// **`admissible_by_construction`.** For ANY candidate store, whatever
/// verdicts and scopes it carries, the assembled value holds only facts
/// with an affirmative verdict for the resolved scope — and only chunks
/// that passed or were never gated, since the assembly adds none. The
/// store is unconstrained: it may be entirely non-disclosable, entirely
/// another scope's, or both. Nothing about it has to be checked in
/// advance, which is what "by construction" means.
let rec admissible_by_construction (scope: string) (store: list disclosed_fact) (input: model_input)
  : Lemma
      (requires admissible scope input)
      (ensures admissible scope (admit_all scope store input))
      (decreases store) =
  match store with
  | [] -> ()
  | f :: rest ->
    if f.fact_scope = scope
    then
      (add_fact_preserves_admissible scope f input;
       match add_fact f input with
       | Admitted next -> admissible_by_construction scope rest next
       | Refused _ -> admissible_by_construction scope rest input)
    else admissible_by_construction scope rest input

/// The same, for the whole door-side assembly: `build` adds a block and
/// the turns on top, and neither can carry a fact into the value.
let build_admissible
  (render_facts: list disclosed_fact -> string)
  (builder_id: string)
  (scope: string)
  (store: list disclosed_fact)
  (ms: list message)
  : Lemma (admissible scope (build render_facts builder_id scope store ms)) =
  admissible_by_construction scope store empty

// ─── 2. Render faithful ──────────────────────────────────────────────

/// **`render_blind_to_facts`.** Two values that agree on their blocks
/// and their turns render IDENTICALLY, whatever their facts, chunks and
/// tool results are. This is "introduces nothing that is not in the
/// value" in its strongest checkable form: the renderer cannot read the
/// fact list at all, so no rendering can depend on one — and the only
/// way an id reaches the reader is a block or a turn some builder put
/// it in.
let render_blind_to_facts (i1: model_input) (i2: model_input)
  : Lemma
      (requires
        i1.input_prompt_blocks == i2.input_prompt_blocks /\
        i1.input_messages == i2.input_messages)
      (ensures render i1 == render i2 /\ rendered_text i1 == rendered_text i2) = ()

let rec leaked_is_mentioned_and_undeclared
  (contains: string -> string -> bool)
  (text: string)
  (declared: list string)
  (candidates: list string)
  (id: string)
  : Lemma
      (requires mem_id id (leaked_of contains text declared candidates))
      (ensures contains text id /\ not (mem_id id declared))
      (decreases candidates) =
  match candidates with
  | [] -> ()
  | c :: rest ->
    if contains text c
    then
      (if mem_id c declared
       then leaked_is_mentioned_and_undeclared contains text declared rest id
       else if c = id
            then ()
            else leaked_is_mentioned_and_undeclared contains text declared rest id)
    else leaked_is_mentioned_and_undeclared contains text declared rest id

let rec mentioned_undeclared_is_leaked
  (contains: string -> string -> bool)
  (text: string)
  (declared: list string)
  (candidates: list string)
  (id: string)
  : Lemma
      (requires mem_id id candidates /\ contains text id /\ not (mem_id id declared))
      (ensures mem_id id (leaked_of contains text declared candidates))
      (decreases candidates) =
  match candidates with
  | [] -> ()
  | c :: rest ->
    if c = id
    then ()
    else mentioned_undeclared_is_leaked contains text declared rest id

/// **`render_faithful`.** Four clauses, for any host containment test
/// and any candidate id:
///
///   1. the turns pass through untouched — not rebuilt, not reordered;
///   2. the system prompt is exactly the blocks' text joined, and
///      absent when there are no blocks, which is how an entry that
///      sent no system prompt stays byte for byte unchanged;
///   3. every id the differential REPORTS is genuinely mentioned in the
///      rendering and genuinely undeclared in the value;
///   4. and every candidate id that IS mentioned and undeclared is
///      reported — so an empty report means what a reader takes it to
///      mean, rather than merely that the filter found nothing.
///
/// Clauses 3 and 4 are what make the host's empty-list assertion a
/// statement; clause 2 is what makes the bytes checkable at all.
let render_faithful
  (contains: string -> string -> bool)
  (candidates: list string)
  (id: string)
  (input: model_input)
  : Lemma
      ((render input).rendered_messages == input.input_messages /\
       (match input.input_prompt_blocks with
        | [] -> (render input).rendered_system_prompt == ONone
        | blocks -> (render input).rendered_system_prompt == OSome (join "\n\n" (block_texts blocks))) /\
       (mem_id id (leaked_fact_ids contains candidates input) ==>
         (contains (rendered_text input) id /\ not (mem_id id (disclosed_fact_ids input)))) /\
       ((mem_id id candidates /\
         contains (rendered_text input) id /\
         not (mem_id id (disclosed_fact_ids input))) ==>
         mem_id id (leaked_fact_ids contains candidates input))) =
  let text = rendered_text input in
  let declared = disclosed_fact_ids input in
  introduce
    mem_id id (leaked_of contains text declared candidates) ==>
      (contains text id /\ not (mem_id id declared))
  with leaked_is_mentioned_and_undeclared contains text declared candidates id;
  introduce
    (mem_id id candidates /\ contains text id /\ not (mem_id id declared)) ==>
      mem_id id (leaked_of contains text declared candidates)
  with mentioned_undeclared_is_leaked contains text declared candidates id

// ─── 3. Input noninterference ────────────────────────────────────────

/// A withheld fact contributes nothing to the visible sublist, at any
/// position — the equation the noninterference premise is discharged by
/// in practice.
let visible_skips_undisclosable (scope: string) (f: disclosed_fact) (rest: list disclosed_fact)
  : Lemma
      (requires not (disclosable_for scope f))
      (ensures visible scope (f :: rest) == visible scope rest) = ()

/// The assembly consults the visible sublist and nothing else: running
/// it over the store and over the store's visible part give the same
/// value. Everything relational below is a corollary of this one
/// equation.
let rec admit_all_is_visible_only (scope: string) (store: list disclosed_fact) (input: model_input)
  : Lemma
      (ensures admit_all scope store input == admit_all scope (visible scope store) input)
      (decreases store) =
  match store with
  | [] -> ()
  | f :: rest ->
    if disclosable_for scope f
    then
      (match add_fact f input with
       | Admitted next -> admit_all_is_visible_only scope rest next
       | Refused _ -> admit_all_is_visible_only scope rest input)
    else admit_all_is_visible_only scope rest input

/// **`input_noninterference`.** Two fact stores that agree on every
/// fact disclosable for the resolved scope assemble to the SAME value —
/// and therefore, since rendering is a function of the value, to the
/// same rendering and the same bytes. The non-disclosable facts are
/// quantified away entirely: the two stores may differ in how many they
/// hold, in their ids, in their values and in their policies, and no
/// observation of what the provider is shown distinguishes one from the
/// other.
///
/// The renderer is universally quantified, so this holds for every one
/// a door might use — including one that renders each admitted fact's
/// value verbatim. What it could NOT survive is a renderer handed the
/// whole store, which is precisely why the shipped shape hands the
/// builder the admitted facts and why the theorem is stated over the
/// assembly rather than over `render` alone.
let input_noninterference
  (render_facts: list disclosed_fact -> string)
  (builder_id: string)
  (scope: string)
  (s1: list disclosed_fact)
  (s2: list disclosed_fact)
  (ms: list message)
  : Lemma
      (requires visible scope s1 == visible scope s2)
      (ensures
        build render_facts builder_id scope s1 ms == build render_facts builder_id scope s2 ms /\
        render (build render_facts builder_id scope s1 ms)
          == render (build render_facts builder_id scope s2 ms) /\
        rendered_text (build render_facts builder_id scope s1 ms)
          == rendered_text (build render_facts builder_id scope s2 ms)) =
  admit_all_is_visible_only scope s1 empty;
  admit_all_is_visible_only scope s2 empty
