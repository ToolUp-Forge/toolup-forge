// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Phase 791 — one closed value for everything a provider sees ──────
//
// The cage's input side had no type. The system prompt was a `string`
// produced by a parallel fold over IO-capable builders, the facts block
// was `sprintf`, and the two met the message list only as arguments at
// the provider call — so the only way to ask "what did the model see?"
// was to search the strings after the fact, which is what Phase 703's
// non-entry assertion does.
//
// `ModelInput` is that value, and `ModelInput.render` is the one pure
// function that turns it into what `IAIProvider` accepts. The split is
// the point: assembly stays with the builders (which keep their IO and
// now yield typed blocks), rendering is total and effect-free, and the
// value is available at the provider boundary for Phase 792 to quantify
// over.
//
// `render` JOINS; it does not reformat. Every byte it emits came from a
// field of the value, so the pre-phase strings survive byte-for-byte
// (GP 11) and a rendering cannot introduce a fact the value does not
// carry — the property `leakedFactIds` measures.

/// A fact as the model may see it: the fact, the disclosure verdict that
/// admitted it, and the scope that verdict was resolved for.
type DisclosedFact = {
    /// The fact's stable identity.
    FactId: string
    /// The fact's value as rendered for the model.
    Value: string
    /// The verdict from the disclosure gate. `ModelInput.tryAddFact`
    /// refuses anything other than `FactDisclosable`.
    Verdict: FactDisclosureVerdict
    /// The scope (principal / tenant) the verdict was resolved for.
    Scope: string
}

/// Whether a retrieved chunk passed the Phase 775 disclosure gate.
/// `NotGated` is the honest reading before that phase lands — it says
/// the gate did not run, never that it passed.
type ChunkGateResult =
    | GatePassed
    | GateFailed of reason: string
    | NotGated

/// A retrieved chunk as the model may see it, carrying its gate result.
type Chunk = {
    /// The chunk's stable identity.
    ChunkId: string
    /// The chunk text.
    Text: string
    /// The Phase 775 gate result.
    GateResult: ChunkGateResult
    /// Source attribution, where the retrieval carried one.
    Source: string option
}

/// One builder's contribution to the system prompt, tagged with the
/// builder that produced it. Blocks are joined in list order.
type PromptBlock = {
    /// The contributing builder, e.g. `"SystemPromptBuilder"`.
    BuilderId: string
    /// The assembled text, exactly as the builder produced it.
    Text: string
}

/// A tool result after the Phase 709 budget has been applied — what
/// remains, not what the tool returned.
type ToolResult = {
    /// The tool that produced it.
    ToolName: string
    /// The content after the budget elided whatever it elided.
    Content: string
}

/// Everything a provider is shown, as one closed value.
///
/// `PromptBlocks` and `Messages` are what `render` emits. `Facts`,
/// `Chunks` and `ToolResults` are the typed record of what those blocks
/// and messages were built from — the half a string cannot carry, and
/// the half the admissibility theorem needs.
type ModelInput = {
    /// Disclosed facts, each carrying its verdict and resolved scope.
    Facts: DisclosedFact list
    /// Retrieved chunks, each carrying its gate result.
    Chunks: Chunk list
    /// System-prompt contributions, in the order they are joined.
    PromptBlocks: PromptBlock list
    /// Tool results after budget enforcement.
    ToolResults: ToolResult list
    /// The conversation turns handed to the provider.
    Messages: AIProviderMessage list
}

/// What `render` produces: exactly the two arguments `IAIProvider`
/// takes. The phase specification calls this shape `ProviderMessages`;
/// it is a record rather than a tuple so the two halves cannot be
/// transposed at a call site.
type ProviderMessages = {
    /// The joined system prompt. `None` when the value carries no
    /// blocks, which is how an entry that sent no system prompt stays
    /// byte-for-byte unchanged.
    SystemPrompt: string option
    /// The conversation turns, unchanged from the value.
    Messages: AIProviderMessage list
}

module ModelInput =

    /// A value carrying nothing.
    let empty: ModelInput = {
        Facts = []
        Chunks = []
        PromptBlocks = []
        ToolResults = []
        Messages = []
    }

    /// Refuse a fact that the disclosure gate did not admit. This is the
    /// constructor Phase 792 relies on: the type cannot be populated by a
    /// leak, so "every fact in the value was disclosable" holds by
    /// construction rather than by review.
    let tryAddFact (fact: DisclosedFact) : Result<DisclosedFact, string> =
        match fact.Verdict with
        | FactDisclosable -> Ok fact
        | FactNotDisclosable policyRef ->
            Error $"Fact {fact.FactId} refused: {FactDisclosureVerdict.refusalText policyRef}"

    /// Append a fact, or refuse it. See `tryAddFact`.
    let addFact (fact: DisclosedFact) (input: ModelInput) : Result<ModelInput, string> =
        tryAddFact fact
        |> Result.map (fun f -> {
            input with
                Facts = input.Facts @ [ f ]
        })

    /// Append a chunk and its gate result.
    let addChunk (chunk: Chunk) (input: ModelInput) : ModelInput = {
        input with
            Chunks = input.Chunks @ [ chunk ]
    }

    /// Append a builder's contribution to the system prompt.
    let addBlock (block: PromptBlock) (input: ModelInput) : ModelInput = {
        input with
            PromptBlocks = input.PromptBlocks @ [ block ]
    }

    /// Append a post-budget tool result.
    let addToolResult (result: ToolResult) (input: ModelInput) : ModelInput = {
        input with
            ToolResults = input.ToolResults @ [ result ]
    }

    /// Set the conversation turns, mirroring any tool results they carry
    /// into `ToolResults` so the typed record cannot drift from what the
    /// messages actually hold.
    let withMessages (messages: AIProviderMessage list) (input: ModelInput) : ModelInput = {
        input with
            Messages = messages
            ToolResults = [
                for m in messages do
                    for tr in m.ToolResults do
                        {
                            ToolName = tr.ToolCallId
                            Content = tr.Content
                        }
            ]
    }

    /// Lift a pre-phase `string option` system prompt into the value.
    ///
    /// The migration seam, and the reason the string-shaped entry points
    /// can delegate rather than duplicate: a `Some text` becomes the one
    /// block `builderId` contributed, a `None` becomes no blocks, and
    /// `render` sends both back out unchanged (GP 11).
    let ofSystemPrompt
        (builderId: string)
        (systemPrompt: string option)
        (messages: AIProviderMessage list)
        : ModelInput =
        let withBlocks =
            match systemPrompt with
            | None -> empty
            | Some text -> empty |> addBlock { BuilderId = builderId; Text = text }

        withMessages messages withBlocks

    /// Render the value to what the provider accepts.
    ///
    /// Pure, total, and additive-free: the system prompt is the blocks'
    /// text joined in order with the same double-newline separator
    /// `SystemPromptBuilder.compose` uses, and the messages pass through
    /// untouched. Nothing is reformatted and nothing is introduced.
    let render (input: ModelInput) : ProviderMessages = {
        SystemPrompt =
            match input.PromptBlocks with
            | [] -> None
            | blocks -> Some(blocks |> List.map _.Text |> String.concat "\n\n")
        Messages = input.Messages
    }

    /// The rendering as one string — every byte the provider is handed,
    /// in the order it is handed them.
    ///
    /// This is the surface the Phase 703 oracle searches, expressed over
    /// the value rather than captured from a spy, so the two probes are
    /// looking at the same thing when they are asked to agree.
    let renderedText (input: ModelInput) : string =
        let rendered = render input

        String.concat "\n" [
            defaultArg rendered.SystemPrompt ""

            for m in rendered.Messages do
                m.Content

                for tr in m.ToolResults do
                    tr.Content
        ]

    /// The fact ids the value declares.
    let disclosedFactIds (input: ModelInput) : string list = input.Facts |> List.map _.FactId

    /// The differential: ids from `candidates` that appear in the
    /// rendering but are absent from the value.
    ///
    /// A non-empty result is a rendering that showed the model a fact the
    /// value does not account for — the failure Phase 791.D exists to
    /// catch. `candidates` is required rather than inferred because a
    /// leak can only be detected for an id the caller can name; that is
    /// the same honest limit the Phase 703 probe works under.
    let leakedFactIds (candidates: string list) (input: ModelInput) : string list =
        let text = renderedText input
        let declared = input.Facts |> List.map _.FactId |> Set.ofList

        candidates
        |> List.filter (fun id -> text.Contains id && not (declared.Contains id))

    /// Whether any chunk failed the Phase 775 gate.
    let hasFailedGates (input: ModelInput) : bool =
        input.Chunks
        |> List.exists (fun c ->
            match c.GateResult with
            | GateFailed _ -> true
            | _ -> false)

// ─── The typed provider entry (Phase 791.C) ──────────────────────────
//
// An optional type extension rather than a member on `IAIProvider`.
// Widening the interface would retype the abstract member every shipped
// connector, every decorator and every test double implements, which is
// precisely the break GP 11 forbids — and the notice would land on
// implementers, who have no choice but to keep implementing it. As an
// extension the typed call is available at every call site, resolves by
// argument type against the string-shaped member, and costs existing
// implementations nothing.

[<AutoOpen>]
module ModelInputProviderExtensions =

    type IAIProvider with

        /// Send a `ModelInput`. Renders once and delegates to the
        /// string-shaped member, which remains the transport.
        member this.SendMessage
            (
                input: ModelInput,
                tools: AIProviderToolDef list,
                onStream: (string -> unit) option,
                retryPolicy: RetryPolicy
            ) : Async<Result<AIProviderResponse, AIProviderError>> =
            let rendered = ModelInput.render input
            this.SendMessage(rendered.Messages, tools, rendered.SystemPrompt, onStream, retryPolicy)

        /// Send a `ModelInput` under a response schema. Renders once and
        /// delegates to the string-shaped member.
        member this.SendStructuredMessage
            (input: ModelInput, tools: AIProviderToolDef list, schema: string, retryPolicy: RetryPolicy)
            : Async<Result<AIProviderResponse, AIProviderError>> =
            let rendered = ModelInput.render input
            this.SendStructuredMessage(rendered.Messages, tools, rendered.SystemPrompt, schema, retryPolicy)