// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ModelInputProofOracleTests

open System
open Expecto
open ToolUp.Platform.AI
open ToolUp.Platform.VectorKnowledgeTypes

// The extracted model, aliased BEFORE `ToolUp.AI` is opened. Production
// ships a `ModelInput` module of its own, and after that `open` the bare
// name resolves to it — so the two would be confusable at exactly the
// call sites where confusing them would make this pack compare production
// with production and pass.
module Proved = ModelInput

open ToolUp.AI

// ─── Phase 792 — the proved model input as oracle ────────────────────
//
// `proofs/ModelInput.fst` models `Shared/ModelInput.fs` clause for
// clause — the closed value, the smart constructor that refuses a fact
// the disclosure gate did not admit, and the pure `render` — and proves
// three lemmas over them: `admissible_by_construction`,
// `render_faithful` (with its relational companion
// `render_blind_to_facts`), and `input_noninterference`.
// `proofs/check.ps1` extracts that model to F# and byte-compares the
// result against the committed `proofs/oracle/ModelInput.fs`, which this
// pack references and runs.
//
// **A proof is about the MODEL, and this pack is the only thing that
// says the model is about the code.** So every arm below is
// differential, in the shape the two earlier proof packs established: a
// store, a scope and a turn set go through production and through the
// extracted model, and the two must agree on the ASSEMBLED VALUE, the
// REFUSALS it produced, the RENDERING, and the leak differential over
// the rendering — at once. Agreement on the value alone would miss a
// renderer that showed the model something the value never admitted,
// which is the whole failure this phase exists to exclude.
//
// **The subject is the non-entry probe, re-run over both halves.** The
// 250-subject population and its deliberately leaky store reproduce
// what the end-to-end probe in `PopulationQueryToolTests` asserts about
// a transcript, expressed over the value instead: 250 subjects offered,
// three admissible, and the other 247 nowhere — not in the value, not
// in the rendering, not in the bytes the provider would be handed.
//
// **The go-red case is committed, and it is a rendered withheld fact.**
// An oracle whose rendering carries one fact the constructor refused
// must be CAUGHT by the comparison. A differential that has never been
// shown to fail agrees with whatever it is shown.

// ─── The bridge ──────────────────────────────────────────────────────
//
// Case for case, and short on purpose: a defect here would make the
// comparison compare the wrong thing, and the way to keep that
// checkable is to keep it readable.

let private toModelVerdict (v: FactDisclosureVerdict) : Proved.verdict =
    match v with
    | FactDisclosable -> Proved.Disclosable
    | FactNotDisclosable policyRef -> Proved.NotDisclosable policyRef

let private ofModelVerdict (v: Proved.verdict) : FactDisclosureVerdict =
    match v with
    | Proved.Disclosable -> FactDisclosable
    | Proved.NotDisclosable policyRef -> FactNotDisclosable policyRef

let private toModelOpt (o: 'a option) : Proved.opt<'a> =
    match o with
    | Some x -> Proved.OSome x
    | None -> Proved.ONone

let private ofModelOpt (o: Proved.opt<'a>) : 'a option =
    match o with
    | Proved.OSome x -> Some x
    | Proved.ONone -> None

let private toModelGate (g: ChunkGateResult) : Proved.gate_result =
    match g with
    | GatePassed -> Proved.GatePassed
    | GateFailed reason -> Proved.GateFailed reason
    | NotGated -> Proved.NotGated

let private toModelFact (f: DisclosedFact) : Proved.disclosed_fact = {
    Proved.fact_id = f.FactId
    fact_value = f.Value
    fact_verdict = toModelVerdict f.Verdict
    fact_scope = f.Scope
}

/// The inverse, so the block renderer below has ONE implementation
/// rather than two. A second renderer over the model's own records
/// would be free to disagree with production's, which is the defect
/// class this differential exists to see rather than to introduce.
let private ofModelFact (f: Proved.disclosed_fact) : DisclosedFact = {
    FactId = f.fact_id
    Value = f.fact_value
    Verdict = ofModelVerdict f.fact_verdict
    Scope = f.fact_scope
}

let private toModelChunk (c: Chunk) : Proved.chunk = {
    Proved.chunk_id = c.ChunkId
    chunk_text = c.Text
    chunk_gate = toModelGate c.GateResult
    chunk_source = toModelOpt c.Source
}

let private toModelBlock (b: PromptBlock) : Proved.prompt_block = {
    Proved.block_builder_id = b.BuilderId
    block_text = b.Text
}

/// A turn, narrowed to the three fields rendering reads. The call-id
/// field becomes the model's tool NAME because that is the renaming
/// `withMessages` performs; doing it here keeps the model's
/// `with_messages` a faithful counterpart of production's.
let private toModelMessage (m: AIProviderMessage) : Proved.message = {
    Proved.message_role = m.Role
    message_content = m.Content
    message_tool_results =
        m.ToolResults
        |> List.map (fun tr -> {
            Proved.tool_name = tr.ToolCallId
            tool_content = tr.Content
        })
}

/// The host's substring test, supplied to the model as data. Production
/// asks `text.Contains id`, and the model's header explains why it does
/// not recompute that.
let private contains (text: string) (needle: string) : bool = text.Contains needle

// ─── The corpus ──────────────────────────────────────────────────────

/// One assembly a door would perform: the store it is offered (wider
/// than the scope permits, deliberately), the scope resolved for the
/// principal, the chunks retrieval produced, the turns, and the ids a
/// leak could be named for. `Name` is what a disagreement reports.
type private Case = {
    Name: string
    Scope: string
    BuilderId: string
    PreBlocks: PromptBlock list
    Store: DisclosedFact list
    Chunks: Chunk list
    Messages: AIProviderMessage list
    Candidates: string list
}

/// The block a door renders its admitted facts into. Shared by both
/// sides — see `ofModelFact`.
let private renderFacts (facts: DisclosedFact list) : string =
    facts |> List.map (fun f -> $"[{f.FactId}] {f.Value}") |> String.concat "\n"

let private factOf (factId: string) (value: string) (verdict: FactDisclosureVerdict) (scope: string) : DisclosedFact = {
    FactId = factId
    Value = value
    Verdict = verdict
    Scope = scope
}

let private turn (role: string) (content: string) = AIProviderMessage.text role content

let private turnWithResults (role: string) (content: string) (results: (string * string) list) = {
    AIProviderMessage.text role content with
        ToolResults = results |> List.map (fun (id, c) -> { ToolCallId = id; Content = c })
}

// ─── The 250-subject population and its leaky store ──────────────────
//
// The end-to-end probe in `PopulationQueryToolTests` seeds 250 subjects,
// returns the top three and asserts the other 247 are absent from the
// transcript. This is the same population expressed as a STORE: all 250
// facts are offered to the assembly, three carry an affirmative verdict
// for the resolved scope, and the rest are denied one of three ways —
// an internal classification, a named policy, or an affirmative verdict
// resolved for somebody ELSE'S scope, which is not permission and which
// only the scope half of the invariant excludes.

let private populationScope = "team-alpha"

let private populationSize = 250

let private populationTopK = 3

let private population: DisclosedFact list =
    let random = Random 792

    List.init populationSize (fun i ->
        let factId = sprintf "sku-%03d" i
        let value = sprintf "%d" (9999 - (i * 37))

        if i < populationTopK then
            factOf factId value FactDisclosable populationScope
        else
            match i % 3 with
            | 0 -> factOf factId value (FactNotDisclosable "Internal") populationScope
            | 1 -> factOf factId value (FactNotDisclosable "licence-x") populationScope
            // An affirmative verdict, for a scope that is not the
            // caller's. The verdict half of the invariant admits this;
            // only the scope half refuses it.
            | _ -> factOf factId value FactDisclosable (sprintf "team-%d" (random.Next(1, 9))))

let private populationCase: Case = {
    Name = "the 250-subject population over a leaky store"
    Scope = populationScope
    BuilderId = "PopulationQueryTool"
    PreBlocks = [
        {
            BuilderId = "SystemPromptBuilder"
            Text = "Answer from the ranking you were given."
        }
    ]
    Store = population
    Chunks = []
    Messages = [ turn "user" "Which subject has the highest revenue?" ]
    Candidates = population |> List.map _.FactId
}

// ─── The pinned corpus ───────────────────────────────────────────────

let private pinnedCases: Case list = [
    {
        Name = "empty everything"
        Scope = "team-a"
        BuilderId = "SystemPromptBuilder"
        PreBlocks = []
        Store = []
        Chunks = []
        Messages = []
        Candidates = []
    }
    {
        Name = "one admissible fact, one turn"
        Scope = "team-a"
        BuilderId = "PopulationQueryTool"
        PreBlocks = []
        Store = [ factOf "sku-open" "1,000" FactDisclosable "team-a" ]
        Chunks = []
        Messages = [ turn "user" "how much?" ]
        Candidates = [ "sku-open" ]
    }
    {
        Name = "a denied fact beside an admitted one - the refusal text is compared too"
        Scope = "team-a"
        BuilderId = "PopulationQueryTool"
        PreBlocks = []
        Store = [
            factOf "sku-open" "1,000" FactDisclosable "team-a"
            factOf "sku-internal" "9,999" (FactNotDisclosable "Internal") "team-a"
            factOf "sku-policy" "4,500" (FactNotDisclosable "policy/licence-x") "team-a"
        ]
        Chunks = []
        Messages = [ turn "user" "how much?" ]
        Candidates = [ "sku-open"; "sku-internal"; "sku-policy" ]
    }
    {
        Name = "an affirmative verdict for another scope is not permission"
        Scope = "team-a"
        BuilderId = "PopulationQueryTool"
        PreBlocks = []
        Store = [
            factOf "sku-open" "1,000" FactDisclosable "team-a"
            factOf "sku-elsewhere" "7,777" FactDisclosable "team-b"
        ]
        Chunks = []
        Messages = []
        Candidates = [ "sku-open"; "sku-elsewhere" ]
    }
    {
        Name = "several blocks join on the separator the composer uses"
        Scope = "team-a"
        BuilderId = "RAGPromptBuilder"
        PreBlocks = [
            {
                BuilderId = "platform"
                Text = "platform layer"
            }
            {
                BuilderId = "team"
                Text = "team layer"
            }
            {
                BuilderId = "module"
                Text = "module layer"
            }
        ]
        Store = [ factOf "sku-open" "1,000" FactDisclosable "team-a" ]
        Chunks = []
        Messages = []
        Candidates = [ "sku-open" ]
    }
    {
        Name = "chunks ride the value, gates and all"
        Scope = "team-a"
        BuilderId = "RAGPromptBuilder"
        PreBlocks = []
        Store = [ factOf "sku-open" "1,000" FactDisclosable "team-a" ]
        Chunks = [
            {
                ChunkId = "chunk-passed"
                Text = "the gate ran and passed"
                GateResult = GatePassed
                Source = Some "doc-1"
            }
            {
                ChunkId = "chunk-ungated"
                Text = "the gate did not run"
                GateResult = NotGated
                Source = None
            }
        ]
        Messages = [ turn "user" "summarise" ]
        Candidates = [ "sku-open" ]
    }
    {
        Name = "a failed gate is carried and detectable, not refused"
        Scope = "team-a"
        BuilderId = "RAGPromptBuilder"
        PreBlocks = []
        Store = []
        Chunks = [
            {
                ChunkId = "chunk-failed"
                Text = "the gate ran and refused"
                GateResult = GateFailed "policy/licence-x"
                Source = None
            }
        ]
        Messages = []
        Candidates = []
    }
    {
        Name = "turns carrying tool results, which the rendering and the mirror both read"
        Scope = "team-a"
        BuilderId = "PopulationQueryTool"
        PreBlocks = []
        Store = [ factOf "sku-open" "1,000" FactDisclosable "team-a" ]
        Chunks = []
        Messages = [
            turn "user" "ask the tool"
            turnWithResults "assistant" "here is what it said" [
                "call-1", "{\"rank\":1,\"subject\":\"sku-open\"}"
                "call-2", "{\"truncated\":true}"
            ]
        ]
        Candidates = [ "sku-open" ]
    }
    {
        Name = "a turn whose content is empty, and a block whose text is empty"
        Scope = "team-a"
        BuilderId = "SystemPromptBuilder"
        PreBlocks = [ { BuilderId = "empty"; Text = "" } ]
        Store = []
        Chunks = []
        Messages = [ turn "user" "" ]
        Candidates = []
    }
    populationCase
]

// ─── Generated cases ─────────────────────────────────────────────────
//
// Seeded, so a disagreement is reproducible by name. The store mixes
// verdict shapes no resolver in the pinned set happens to produce, and
// the scopes are sampled so the scope half of the invariant meets facts
// that are otherwise perfectly admissible.

let private verdictShapes = [
    FactDisclosable
    FactDisclosable
    FactNotDisclosable "Internal"
    FactNotDisclosable "licence-x"
    FactNotDisclosable "unknown-fact"
    FactNotDisclosable "multi-party-unsatisfied:party-a,party-b"
    FactNotDisclosable "purpose-marketing"
]

let private gateShapes = [ GatePassed; NotGated; GateFailed "policy/licence-x" ]

let private pick (random: Random) (xs: 'a list) : 'a = xs[random.Next(List.length xs)]

let private generatedCases: Case list =
    let random = Random 7920
    let scopes = [ "team-a"; "team-b"; "team-c" ]

    List.init 150 (fun n ->
        let scope = pick random scopes

        let store =
            List.init (random.Next(0, 9)) (fun i ->
                factOf
                    (sprintf "gen-%d-%d" n i)
                    (sprintf "%d" (random.Next(-1000, 100000)))
                    (pick random verdictShapes)
                    (pick random scopes))

        let chunks =
            List.init (random.Next(0, 4)) (fun i -> {
                ChunkId = sprintf "chunk-%d-%d" n i
                Text = sprintf "retrieved text %d" i
                GateResult = pick random gateShapes
                Source = if random.Next 2 = 0 then Some(sprintf "doc-%d" i) else None
            })

        let messages =
            List.init (random.Next(0, 4)) (fun i ->
                if random.Next 3 = 0 then
                    turnWithResults "assistant" (sprintf "turn %d" i) [ sprintf "call-%d" i, sprintf "result %d" i ]
                else
                    turn "user" (sprintf "turn %d" i))

        {
            Name = sprintf "generated #%d (scope %s)" n scope
            Scope = scope
            BuilderId = "SystemPromptBuilder"
            PreBlocks =
                if random.Next 3 = 0 then
                    [
                        {
                            BuilderId = "platform"
                            Text = sprintf "preamble %d" n
                        }
                    ]
                else
                    []
            Store = store
            Chunks = chunks
            Messages = messages
            // Every id in the store is a candidate, so the leak
            // differential is asked about the denied ones too — which is
            // the only way it can report anything.
            Candidates = store |> List.map _.FactId
        })

// ─── Running the two, and comparing every half at once ───────────────

/// Everything a provider boundary would observe, from either
/// implementation.
type private Outcome = {
    /// The assembled value's facts, field for field.
    Facts: (string * string * FactDisclosureVerdict * string) list
    /// The refusal the constructor produced for each rejected fact, in
    /// order. Compared verbatim, because wording is the cheapest place
    /// drift shows.
    Refusals: string list
    /// The mirrored tool-result record `withMessages` derives.
    MirroredToolResults: (string * string) list
    /// What `render` produced.
    SystemPrompt: string option
    Turns: (string * string * (string * string) list) list
    /// Every byte the provider would be handed.
    RenderedText: string
    DeclaredIds: string list
    /// The leak differential over the rendering.
    Leaked: string list
    FailedGates: bool
}

let private productionOutcome (case: Case) : Outcome =
    let admitted, refusals =
        case.Store
        |> List.fold
            (fun (input, refusals) f ->
                if f.Scope = case.Scope then
                    match ModelInput.addFact f input with
                    | Ok next -> next, refusals
                    | Error reason -> input, refusals @ [ reason ]
                else
                    input, refusals)
            (ModelInput.empty, [])

    let preBlocked =
        case.PreBlocks |> List.fold (fun acc b -> ModelInput.addBlock b acc) admitted

    let blocked =
        ModelInput.addBlock
            {
                BuilderId = case.BuilderId
                Text = renderFacts admitted.Facts
            }
            preBlocked

    let chunked =
        case.Chunks |> List.fold (fun acc c -> ModelInput.addChunk c acc) blocked

    let value = ModelInput.withMessages case.Messages chunked
    let rendered = ModelInput.render value

    {
        Facts = value.Facts |> List.map (fun f -> f.FactId, f.Value, f.Verdict, f.Scope)
        Refusals = refusals
        MirroredToolResults = value.ToolResults |> List.map (fun t -> t.ToolName, t.Content)
        SystemPrompt = rendered.SystemPrompt
        Turns =
            rendered.Messages
            |> List.map (fun m -> m.Role, m.Content, m.ToolResults |> List.map (fun tr -> tr.ToolCallId, tr.Content))
        RenderedText = ModelInput.renderedText value
        DeclaredIds = ModelInput.disclosedFactIds value
        Leaked = ModelInput.leakedFactIds case.Candidates value
        FailedGates = ModelInput.hasFailedGates value
    }

/// The model's assembly, with the block step pluggable so the go-red
/// oracle runs through the IDENTICAL comparison.
let private modelOutcomeWith (blockStep: Case -> Proved.model_input -> Proved.model_input) (case: Case) : Outcome =
    let admitted, refusals =
        case.Store
        |> List.fold
            (fun (input, refusals) f ->
                if f.Scope = case.Scope then
                    match Proved.add_fact (toModelFact f) input with
                    | Proved.Admitted next -> next, refusals
                    | Proved.Refused reason -> input, refusals @ [ reason ]
                else
                    input, refusals)
            (Proved.empty, [])

    let preBlocked =
        case.PreBlocks
        |> List.fold (fun acc b -> Proved.add_block (toModelBlock b) acc) admitted

    let blocked =
        Proved.add_block
            {
                Proved.block_builder_id = case.BuilderId
                block_text = renderFacts (admitted.input_facts |> List.map ofModelFact)
            }
            preBlocked

    let leakedIn = blockStep case blocked

    let chunked =
        case.Chunks
        |> List.fold (fun acc c -> Proved.add_chunk (toModelChunk c) acc) leakedIn

    let value = Proved.with_messages (case.Messages |> List.map toModelMessage) chunked
    let rendered = Proved.render value

    {
        Facts =
            value.input_facts
            |> List.map (fun f -> f.fact_id, f.fact_value, ofModelVerdict f.fact_verdict, f.fact_scope)
        Refusals = refusals
        MirroredToolResults = value.input_tool_results |> List.map (fun t -> t.tool_name, t.tool_content)
        SystemPrompt = ofModelOpt rendered.rendered_system_prompt
        Turns =
            rendered.rendered_messages
            |> List.map (fun m ->
                m.message_role,
                m.message_content,
                m.message_tool_results |> List.map (fun tr -> tr.tool_name, tr.tool_content))
        RenderedText = Proved.rendered_text value
        DeclaredIds = Proved.disclosed_fact_ids value
        Leaked = Proved.leaked_fact_ids contains case.Candidates value
        FailedGates = Proved.has_failed_gates value
    }

/// The honest oracle adds nothing beyond what production adds.
let private honestBlock (_: Case) (input: Proved.model_input) = input

/// Every fact the assembly must refuse — denied for the scope, or
/// affirmative for a different one.
let private withheldOf (case: Case) : DisclosedFact list =
    case.Store
    |> List.filter (fun f -> not (f.Scope = case.Scope && f.Verdict = FactDisclosable))

/// **The go-red oracle.** A renderer that shows the reader one fact the
/// constructor refused — the exact failure the value exists to exclude,
/// and the one a plausible reimplementation reaches by rendering from
/// the STORE rather than from what was admitted.
let private leakyBlock (case: Case) (input: Proved.model_input) =
    match withheldOf case with
    | [] -> input
    | withheld :: _ ->
        Proved.add_block
            {
                Proved.block_builder_id = "LeakyFactBuilder"
                block_text = $"[{withheld.FactId}] {withheld.Value}"
            }
            input

/// Compact enough to read in a failure message; the comparison itself is
/// structural over the whole outcome.
let private render (o: Outcome) : string =
    sprintf
        "facts=%A refusals=%A prompt=%A turns=%A declared=%A leaked=%A gatesFailed=%b mirrored=%A"
        (o.Facts |> List.map (fun (id, _, _, _) -> id))
        o.Refusals
        o.SystemPrompt
        (o.Turns |> List.map (fun (role, content, _) -> role, content))
        o.DeclaredIds
        o.Leaked
        o.FailedGates
        o.MirroredToolResults

/// Never throws. A throw from either side is a finding, not a crash.
let private disagreements (blockStep: Case -> Proved.model_input -> Proved.model_input) (cases: Case list) =
    cases
    |> List.choose (fun case ->
        let production =
            try
                Choice1Of2(productionOutcome case)
            with ex ->
                Choice2Of2(sprintf "THREW %s: %s" (ex.GetType().Name) ex.Message)

        let model =
            try
                Choice1Of2(modelOutcomeWith blockStep case)
            with ex ->
                Choice2Of2(sprintf "THREW %s: %s" (ex.GetType().Name) ex.Message)

        match production, model with
        | Choice1Of2 p, Choice1Of2 m when p = m -> None
        | _ ->
            let show =
                function
                | Choice1Of2 o -> render o
                | Choice2Of2 text -> text

            Some(sprintf "%s\n    production: %s\n    model:      %s" case.Name (show production) (show model)))

let private allCases () = pinnedCases @ generatedCases

// ─── The pack ────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 792 - the proved model input as oracle" [

        testCase "the covered set is not vacuous"
        <| fun () ->
            // Several arms below are `isEmpty` over a computed
            // population, which an empty population satisfies trivially
            // — so the population is asserted first.
            Expect.isGreaterThan (List.length pinnedCases) 9 "the pinned corpus should all be here"
            Expect.isGreaterThan (List.length generatedCases) 100 "and a generated population beside it"

            Expect.isNonEmpty
                (allCases () |> List.filter (fun c -> not (List.isEmpty (withheldOf c))))
                "at least one case must offer a fact the assembly has to refuse, or the go-red oracle has nothing to leak"

            Expect.isNonEmpty
                (allCases ()
                 |> List.filter (fun c ->
                     c.Store
                     |> List.exists (fun f -> f.Verdict = FactDisclosable && f.Scope <> c.Scope)))
                "and at least one must offer an affirmative verdict resolved for another scope, or the scope half is never \
                 exercised"

            Expect.equal (List.length population) populationSize "the seeded population is the size the probe uses"

        testCase "the extracted lift agrees with production over the entry corpus"
        <| fun () ->
            // `ofSystemPrompt` is the migration seam every provider
            // entry goes through, so it is compared on its own: a value
            // assembled some other way would not exercise it.
            let prompts = [
                None
                Some ""
                Some "Reply with a single word."
                Some "line one\n\nline two\nline three"
            ]

            let turnSets = [
                []
                [ turn "user" "the question" ]
                [
                    turn "user" "first"
                    turnWithResults "assistant" "second" [ "call-1", "the tool said" ]
                ]
            ]

            let found = [
                for prompt in prompts do
                    for turns in turnSets do
                        let fromProduction = ModelInput.ofSystemPrompt "builder" prompt turns
                        let renderedProduction = ModelInput.render fromProduction

                        let fromModel =
                            Proved.of_system_prompt "builder" (toModelOpt prompt) (turns |> List.map toModelMessage)

                        let renderedModel = Proved.render fromModel

                        if
                            ofModelOpt renderedModel.rendered_system_prompt
                            <> renderedProduction.SystemPrompt
                        then
                            yield sprintf "%A / %d turns: prompt differs" prompt (List.length turns)

                        if Proved.rendered_text fromModel <> ModelInput.renderedText fromProduction then
                            yield sprintf "%A / %d turns: rendered text differs" prompt (List.length turns)
            ]

            Expect.isEmpty found (sprintf "the lift must agree:\n  %s" (String.concat "\n  " found))

        testCase "the extracted constructor agrees with production on every verdict, refusal text included"
        <| fun () ->
            // The fold arms below compare refusals in bulk; this compares
            // them one verdict at a time, so a wording drift is reported
            // against the verdict that caused it.
            let found = [
                for verdict in verdictShapes do
                    let fact = factOf "sku-probe" "1,234" verdict "team-a"

                    let production =
                        match ModelInput.tryAddFact fact with
                        | Ok f -> Choice1Of2 f.FactId
                        | Error reason -> Choice2Of2 reason

                    let model =
                        match Proved.try_add_fact (toModelFact fact) with
                        | Proved.Admitted f -> Choice1Of2 f.fact_id
                        | Proved.Refused reason -> Choice2Of2 reason

                    if production <> model then
                        yield sprintf "%A: production %A, model %A" verdict production model
            ]

            Expect.isEmpty
                found
                (sprintf "the proved constructor and the shipped one must agree:\n  %s" (String.concat "\n  " found))

        testCase "the extracted value and rendering agree with production over the pinned corpus"
        <| fun () ->
            let found = disagreements honestBlock pinnedCases

            Expect.isEmpty
                found
                (sprintf
                    "the proved model and the shipped one must agree on the value, the refusals, the rendering and the leak \
                     differential:\n  %s"
                    (String.concat "\n  " found))

        testCase "the extracted value and rendering agree with production over the generated corpus"
        <| fun () ->
            let found = disagreements honestBlock generatedCases

            Expect.isEmpty
                found
                (sprintf
                    "the proved model and the shipped one must agree over the generated population:\n  %s"
                    (String.concat "\n  " found))

        testCase "the non-entry probe over both value and rendering - 250 seeded subjects, a leaky store"
        <| fun () ->
            // The end-to-end probe asserts this about a transcript. Here
            // it is asserted about the value AND the bytes the provider
            // would be handed, which is the half a string search over a
            // captured transcript cannot reach: a subject absent from
            // the transcript because the tool happened not to return it
            // is indistinguishable, there, from one the value refused.
            let outcome = productionOutcome populationCase

            Expect.equal
                (List.length outcome.Facts)
                populationTopK
                (sprintf
                    "exactly the %d admissible subjects entered the value; the other %d of %d were refused"
                    populationTopK
                    (populationSize - populationTopK)
                    populationSize)

            Expect.equal
                (outcome.DeclaredIds |> List.sort)
                ([ "sku-000"; "sku-001"; "sku-002" ])
                "and they are the three the scope permits, not an arbitrary three"

            Expect.isEmpty
                outcome.Leaked
                "no subject the rendering shows is undeclared in the value — the rendering introduced nothing"

            let withheldIds = withheldOf populationCase |> List.map _.FactId

            Expect.equal
                (List.length withheldIds)
                (populationSize - populationTopK)
                "the store really did offer the other 247"

            Expect.isEmpty
                (withheldIds |> List.filter (fun id -> outcome.RenderedText.Contains id))
                "and not one of them is anywhere in the bytes the provider would be handed"

            // Both halves again, through the extracted model, so the
            // claim above is one the theorem covers rather than one this
            // pack merely observed.
            Expect.isEmpty
                (disagreements honestBlock [ populationCase ])
                "the proved model reaches the same value and the same bytes"

        testCase "the differential CATCHES an oracle that renders one withheld fact - the go-red case"
        <| fun () ->
            let leakable =
                allCases () |> List.filter (fun c -> not (List.isEmpty (withheldOf c)))

            let found = disagreements leakyBlock leakable

            Expect.isNonEmpty
                found
                "an oracle that renders a fact the constructor refused must be caught: if this passes, the comparison is \
                 agreeing with whatever it is shown and every other arm in this pack is worthless"

            Expect.equal
                (List.length found)
                (List.length leakable)
                "and it must be caught on EVERY case that has a withheld fact to leak, not merely on one of them"

        testCase "and the rendered withheld fact is what it catches"
        <| fun () ->
            // The mechanism, pinned rather than left to the population.
            let case =
                pinnedCases
                |> List.find (fun c ->
                    c.Name = "a denied fact beside an admitted one - the refusal text is compared too")

            let honest = modelOutcomeWith honestBlock case
            let leaky = modelOutcomeWith leakyBlock case

            Expect.isEmpty honest.Leaked "the honest oracle leaks nothing — this is `render_faithful` running"

            Expect.equal
                leaky.Leaked
                [ "sku-internal" ]
                "the leaky oracle's rendering names a fact the value refused, and the differential names it back"

            Expect.equal
                honest.Facts
                leaky.Facts
                "the VALUE is identical in both — which is the point: a value-only comparison would not have seen this"

            Expect.isNonEmpty (disagreements leakyBlock [ case ]) "and the comparison reports exactly that"

        testCase
            "two stores that differ only in what was withheld are indistinguishable - input_noninterference running"
        <| fun () ->
            // The relational lemma's operational face. The second store
            // keeps every fact the scope permits and replaces the rest
            // wholesale — different ids, different values, different
            // policies, a different count — and nothing a provider
            // boundary can observe may move.
            let scrambled (case: Case) : Case =
                let visible =
                    case.Store
                    |> List.filter (fun f -> f.Scope = case.Scope && f.Verdict = FactDisclosable)

                let replacements =
                    List.init (List.length (withheldOf case) + 7) (fun i ->
                        factOf
                            (sprintf "scrambled-%d" i)
                            (sprintf "%d" (i * 13))
                            (FactNotDisclosable "purpose-unrelated")
                            case.Scope)

                {
                    case with
                        Name = case.Name + " (scrambled)"
                        Store = visible @ replacements
                }

            let cases = allCases () |> List.filter (fun c -> not (List.isEmpty (withheldOf c)))

            let found = [
                for case in cases do
                    let before = productionOutcome case
                    let after = productionOutcome (scrambled case)

                    // The refusals and the candidate set are properties
                    // of the STORE, so they legitimately differ; every
                    // other half is what the provider is shown.
                    let observable (o: Outcome) =
                        o.Facts, o.SystemPrompt, o.Turns, o.RenderedText, o.DeclaredIds, o.MirroredToolResults

                    if observable before <> observable after then
                        yield sprintf "%s: %s\n    became: %s" case.Name (render before) (render after)
            ]

            Expect.isEmpty
                found
                (sprintf
                    "scrambling the withheld facts must change nothing a provider can observe:\n  %s"
                    (String.concat "\n  " found))

            Expect.isGreaterThan (List.length cases) 50 "and the population it was checked over is not a handful"

        testCase "the model never throws, on any case, through any of its functions"
        <| fun () ->
            // The lemmas' operational face: every definition in the model
            // is total, but the extraction and the bridge are trusted
            // rather than proved, and this is the arm that would catch
            // either turning a total function into an exception.
            let threw =
                allCases ()
                |> List.choose (fun case ->
                    try
                        modelOutcomeWith honestBlock case |> ignore
                        modelOutcomeWith leakyBlock case |> ignore
                        None
                    with ex ->
                        Some(sprintf "%s: %s %s" case.Name (ex.GetType().Name) ex.Message))

            Expect.isEmpty threw (sprintf "the extracted model threw:\n  %s" (String.concat "\n  " threw))
    ]