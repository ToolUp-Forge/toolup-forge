module ToolUp.AI.SystemPromptBuilder

open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI

/// Everything the prompt builder needs, resolved per request. A richer shape
/// than a raw `AccessContext` so module-awareness and compose-time metadata
/// are first-class inputs.
type PromptContext = {
    /// Resolved access context (user, team, platform mode, permissions).
    Access: AccessContext
    /// The module the user was viewing when they submitted the prompt, as
    /// reported by `AIMessageRequest.ActiveModule`. `None` when the chat
    /// was opened from a non-module context.
    ActiveModule: string option
    /// Sidebar page route within the active module, as reported by
    /// `AIMessageRequest.ActivePage`. `None` for single-page modules or
    /// prompts submitted from a non-module context.
    ActivePage: string option
    /// Snapshot of the structured narrative currently shown on the active
    /// page, when the module exposes one. Feeds the built-in
    /// `currentNarrativeContext` builder — reflects the user's last
    /// render of the page, not a canonical store.
    ActivePageNarrative: NarrativeDocument option
    /// Module AI contexts registered at compose time, keyed by `ModuleName`.
    /// Look up via `ActiveModule` to find the domain contribution for the
    /// active module.
    ModuleContexts: Map<string, ModuleAIContext>
    /// The user's current message text. Available to builders that perform
    /// retrieval-augmented generation — use this as the query when calling
    /// `IRetrievalPipeline.Retrieve`. `None` in non-message contexts (e.g.
    /// system-level prompt construction at startup). Builders that do not
    /// need the message text can ignore this field.
    CurrentMessage: string option
    /// Mutable cell collecting `RetrievedSource`s populated by RAG-style
    /// builders (`RAGPromptBuilder.withRetrieval`) so the assistant
    /// handler can attach them to the outbound `ConversationMessage`
    /// without coupling the prompt-builder return type to the wire-format
    /// shape. Keeps `SystemPromptBuilder` returning `Async<string>` while
    /// still letting builders surface structured side-data to the handler.
    /// Builders that don't perform retrieval ignore this; the handler
    /// reads `Value` after `compose` resolves and clears it between
    /// turns by allocating a fresh cell per request.
    RetrievedSources: RetrievedSource list ref
    /// Per-request server-side short-circuit channel. A builder that
    /// must refuse the turn WITHOUT invoking the provider (e.g.
    /// `RAGPromptBuilder` under `StrictlyGrounded` when retrieval
    /// found nothing) sets `Value <- Some <reply text>`. The handler
    /// reads it after `compose` resolves and, if set, emits that text
    /// as the assistant turn and skips the model call entirely — the
    /// "server-side guard" the grounding contract promises. Builders
    /// that never short-circuit leave it `None`. Fresh cell per
    /// request (same lifecycle as `RetrievedSources`).
    ShortCircuit: string option ref
}

/// Builds the system prompt sent to the AI provider for a given request.
/// Async so builders can pull from cloud secret stores, team config stores,
/// blob storage, or third-party knowledge APIs without blocking the agent
/// loop.
type SystemPromptBuilder = PromptContext -> Async<string>

module SystemPromptBuilder =
    /// A builder that always returns the same static string. Back-compat
    /// shim for deployments that used the previous `SystemPromptPrefix`
    /// field — convert `Some prefix` to `SystemPromptBuilder.fromStatic prefix`.
    let fromStatic (prefix: string) : SystemPromptBuilder = fun _ -> async { return prefix }

    /// Built-in builder that injects the active module's `ModuleAIContext.SystemPrompt`.
    /// Returns an empty string when no module is active or the active module
    /// did not register a context. Use via `compose` alongside a platform
    /// prefix and any team-specific builders.
    let activeModuleContext: SystemPromptBuilder =
        fun ctx -> async {
            match ctx.ActiveModule with
            | Some name ->
                match ctx.ModuleContexts.TryFind name with
                | Some moduleCtx -> return moduleCtx.SystemPrompt
                | None -> return ""
            | None -> return ""
        }

    /// Built-in builder that renders `ActivePageNarrative` as markdown and
    /// wraps it in a header identifying the module and page. Returns an
    /// empty string when no narrative is present. Lets the agent answer
    /// "what does this page say?" / "summarise the narrative" without
    /// spending a tool call. The block is prefixed with a line noting that
    /// the snapshot reflects the user's last render, not canonical state.
    let currentNarrativeContext: SystemPromptBuilder =
        fun ctx -> async {
            match ctx.ActivePageNarrative with
            | None -> return ""
            | Some doc ->
                let markdown = NarrativeMarkdown.render doc

                let header =
                    match ctx.ActiveModule, ctx.ActivePage with
                    | Some m, Some p -> sprintf "## Current page narrative — %s %s" m p
                    | Some m, None -> sprintf "## Current page narrative — %s" m
                    | None, _ -> "## Current page narrative"

                return
                    sprintf
                        "%s\n\n_Snapshot of the narrative block currently rendered for the user. Reflects their last parameterisation, not canonical state._\n\n%s"
                        header
                        markdown
        }

    /// Layer multiple builders. Resolution is parallel; non-empty results
    /// are joined with double-newline separators in list order. Empty
    /// contributions are dropped so a deployment can opt out of any layer
    /// by returning `""`.
    let compose (builders: SystemPromptBuilder list) : SystemPromptBuilder =
        fun ctx -> async {
            let! parts = builders |> List.map (fun b -> b ctx) |> Async.Parallel
            return parts |> Array.filter (fun s -> s <> "") |> String.concat "\n\n"
        }

/// Server-side AI assistant configuration. Contains both the client-visible
/// `Branding` (which will be forwarded to the client via the AI module's
/// props) and the server-only `SystemPrompt` builder. Passed to the
/// `composeWithAI` wrapper.
type AIAssistantServerConfig = {
    Branding: AIAssistantBranding
    /// Optional system-prompt builder. When `None`, no system prompt is
    /// sent to the provider — equivalent to the previous
    /// `SystemPromptPrefix = None` case.
    SystemPrompt: SystemPromptBuilder option
    /// Cap on prior provider-history messages replayed to the LLM each
    /// turn. `None` ⇒ the SDK's safe default
    /// (`AIAssistantServerConfig.DefaultMaxHistoryMessages`). The old
    /// behaviour replayed the ENTIRE history verbatim, so a long-lived
    /// conversation grew per-turn cost without bound and eventually
    /// hard-failed with an opaque provider context-overflow error.
    /// `Some n` (n must be > 0; non-positive falls back to the default)
    /// keeps only the most recent `n` messages; older turns are dropped
    /// with a Warn log so the truncation is observable.
    MaxHistoryMessages: int option
}

/// Companion helpers for `AIAssistantServerConfig`.
module AIAssistantServerConfig =
    /// Safe default history window when `MaxHistoryMessages = None`.
    /// Generous enough that normal conversations are never truncated,
    /// bounded enough that a pathological long-runner can't grow
    /// per-turn token spend without limit.
    [<Literal>]
    let DefaultMaxHistoryMessages = 60

    /// Resolve the effective, validated history cap. Non-positive
    /// overrides are ignored in favour of the safe default.
    let effectiveMaxHistory (cfg: AIAssistantServerConfig option) : int =
        match cfg |> Option.bind _.MaxHistoryMessages with
        | Some n when n > 0 -> n
        | _ -> DefaultMaxHistoryMessages