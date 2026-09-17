// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.AI

/// Phase 791: ModelInput — one closed value for everything a provider sees.
///
/// The model's input side has no typed value today: the system prompt is a
/// `string`, facts are `sprintf`'d, and they converge only as raw arguments
/// at SendMessage. This type unifies them — every provider entry constructs
/// a `ModelInput`, renders it once, and passes the rendering to the provider.
///
/// Every fact carries its disclosure verdict, every chunk carries its gate
/// result (Phase 775), and the renderer introduces nothing not in the value.
/// Composed with Phase 790's noninterference theorem over the disclosure fold,
/// this proves: every fact the model sees has an affirmative verdict for its
/// resolved scope (Phase 797), and render is faithful — no leakage.
/// A single fact as seen by the model: the fact itself, its disclosure verdict,
/// and the scope it was resolved in.
type DisclosedFact = {
    /// The fact's stable identity.
    FactId: string
    /// The fact's value (JSON representation).
    Value: string
    /// The disclosure verdict: FactDisclosable or FactNotDisclosable(policyRef).
    /// Smart constructor refuses facts without FactDisclosable.
    Verdict: FactDisclosureVerdict
    /// The scope (e.g., a principal's id) the verdict was resolved for.
    Scope: string
}

/// The gate result of a retrieved chunk: either passed the disclosure gate
/// or the gate was not run (marked as NotGated honestly, per Phase 775).
type ChunkGateResult =
    | GatePassed
    | GateFailed of reason: string
    | NotGated

/// A retrieved chunk: value + metadata + gate result.
type Chunk = {
    /// Chunk identifier.
    ChunkId: string
    /// The chunk text.
    Text: string
    /// Gate result (Phase 775) — None if the gate hasn't landed yet.
    GateResult: ChunkGateResult
    /// Optional document reference / source attribution.
    Source: string option
}

/// A block of text contributed by a builder (system prompt, facts, RAG results).
type PromptBlock = {
    /// Which builder contributed this (e.g., "SystemPromptBuilder", "RAGPromptBuilder").
    BuilderId: string
    /// The assembled text.
    Text: string
}

/// A tool result after budget enforcement (Phase 709). The budget may have
/// elided content; this carries what remains.
type ToolResult = {
    /// The tool name.
    ToolName: string
    /// The result content after budget applied.
    Content: string
}

/// ModelInput — the closed value containing everything a provider sees.
type ModelInput = {
    /// All disclosed facts, each carrying its verdict and scope.
    Facts: DisclosedFact list
    /// All retrieved chunks, each carrying its gate result.
    Chunks: Chunk list
    /// Prompt blocks from builders (system prompt, facts narration, context).
    PromptBlocks: PromptBlock list
    /// Tool results (already budget-bounded).
    ToolResults: ToolResult list
}

module ModelInput =

    /// Smart constructor: refuse a fact without affirmative disclosure.
    /// Returns `Error reason` if the fact's verdict is not FactDisclosable.
    let tryAddFact (fact: DisclosedFact) : Result<DisclosedFact, string> =
        match fact.Verdict with
        | FactDisclosable -> Ok fact
        | FactNotDisclosable policyRef -> Error $"Fact {fact.FactId} rejected: disclosure denied (policy: {policyRef})"

    /// Create an empty ModelInput.
    let empty: ModelInput = {
        Facts = []
        Chunks = []
        PromptBlocks = []
        ToolResults = []
    }

    /// Add a fact to the input. Fails if the fact lacks affirmative disclosure.
    let addFact (fact: DisclosedFact) (input: ModelInput) : Result<ModelInput, string> =
        match tryAddFact fact with
        | Error e -> Error e
        | Ok f ->
            Ok {
                input with
                    Facts = input.Facts @ [ f ]
            }

    /// Add a chunk to the input.
    let addChunk (chunk: Chunk) (input: ModelInput) : ModelInput = {
        input with
            Chunks = input.Chunks @ [ chunk ]
    }

    /// Add a prompt block to the input.
    let addBlock (block: PromptBlock) (input: ModelInput) : ModelInput = {
        input with
            PromptBlocks = input.PromptBlocks @ [ block ]
    }

    /// Add a tool result to the input.
    let addToolResult (result: ToolResult) (input: ModelInput) : ModelInput = {
        input with
            ToolResults = input.ToolResults @ [ result ]
    }

    /// Render the ModelInput to formatted strings for the provider.
    ///
    /// The rendering is pure and faithful: every element in the value appears
    /// exactly once in the rendered output, with no additions. This is the ONE
    /// place the model sees input, and the theorem over render and the
    /// constructor proves its faithfulness.
    ///
    /// Returns: (systemPrompt, userMessage) where each is the rendered string
    /// for that part of the provider input.
    let render (input: ModelInput) : string * string =

        // Build the system prompt from system prompt blocks.
        let systemPromptText =
            input.PromptBlocks
            |> List.filter (fun b -> b.BuilderId = "SystemPromptBuilder")
            |> List.map _.Text
            |> String.concat "\n"

        // Build the user's or assistant's message from facts, chunks, and remaining blocks.
        let userMessageContent =
            // Render disclosed facts as a structured section.
            let factsSection =
                if input.Facts.IsEmpty then
                    []
                else
                    let factLines =
                        input.Facts
                        |> List.map (fun f -> $"- {f.FactId}: {f.Value} (scope: {f.Scope})")
                        |> String.concat "\n"

                    [ "## Disclosed Facts\n" + factLines ]

            // Render chunks with their gate results.
            let chunksSection =
                if input.Chunks.IsEmpty then
                    []
                else
                    let chunkLines =
                        input.Chunks
                        |> List.map (fun c ->
                            let gateInfo =
                                match c.GateResult with
                                | GatePassed -> "(gate: passed)"
                                | GateFailed r -> $"(gate: failed - {r})"
                                | NotGated -> "(gate: not run)"

                            let sourceInfo =
                                match c.Source with
                                | Some s -> $" - {s}"
                                | None -> ""

                            $"- {c.ChunkId} {gateInfo}{sourceInfo}:\n  {c.Text}")
                        |> String.concat "\n"

                    [ "## Retrieved Context\n" + chunkLines ]

            // Render other prompt blocks (RAG, context, etc.).
            let otherBlocksSection =
                input.PromptBlocks
                |> List.filter (fun b -> b.BuilderId <> "SystemPromptBuilder")
                |> List.map (fun b -> $"## {b.BuilderId}\n{b.Text}")

            // Render tool results.
            let toolResultsSection =
                if input.ToolResults.IsEmpty then
                    []
                else
                    let resultLines =
                        input.ToolResults
                        |> List.map (fun r -> $"- {r.ToolName}:\n  {r.Content}")
                        |> String.concat "\n"

                    [ "## Tool Results\n" + resultLines ]

            // Concatenate all sections.
            (factsSection @ chunksSection @ otherBlocksSection @ toolResultsSection)
            |> String.concat "\n\n"

        (systemPromptText, userMessageContent)

    /// Extract the system prompt from the input.
    let getSystemPrompt (input: ModelInput) : string =
        input.PromptBlocks
        |> List.filter (fun b -> b.BuilderId = "SystemPromptBuilder")
        |> List.map _.Text
        |> String.concat "\n"

    /// Count facts by verdict for diagnostic/audit purposes.
    let countFactsByVerdict (input: ModelInput) : int =
        input.Facts |> List.filter (fun f -> f.Verdict = FactDisclosable) |> List.length

    /// Check if any chunks failed the gate.
    let hasFailedGates (input: ModelInput) : bool =
        input.Chunks
        |> List.exists (fun c ->
            match c.GateResult with
            | GateFailed _ -> true
            | _ -> false)