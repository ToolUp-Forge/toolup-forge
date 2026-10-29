module ToolUp.AI.SystemPromptBuilder

open System
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
    /// Prior conversation turns for this request, oldest first, rendered as
    /// plain text and already bounded by the handler. Empty on the first
    /// turn, and empty for every deployment that has not composed an
    /// `IConversationStore` — the substrate this is read from.
    ///
    /// Threaded onto `RetrievalRequest.History` by `RAGPromptBuilder`, where
    /// the query-rewrite stage consumes it to resolve follow-up
    /// queries ("what about it?") against what was said before. Builders
    /// that do not perform retrieval ignore it; an empty list leaves every
    /// downstream stage byte-identical (GP 11).
    ConversationHistory: string list
    /// Per-request metadata-equality filter scoping this
    /// turn's retrieval, as reported by `AIMessageRequest.RetrievalFilters`.
    /// `None` on every turn that did not ask to be scoped.
    ///
    /// Merged with the deployment-level `RetrievalDefaults.Filters` by
    /// `RAGPromptBuilder` (deployment wins on a shared key — see
    /// `RetrievalDefaults.mergeFilters`) and threaded onto
    /// `RetrievalRequest.Filters`. This is the seam that makes a filter
    /// reachable from the AI panel at all: `withRetrievalToolAware` builds
    /// its own `RetrievalRequest`, so without it a caller on the prompt
    /// path has no way to scope one. Builders that do not retrieve ignore
    /// it; `None` leaves every downstream stage byte-identical (GP 11).
    RetrievalFilters: Map<string, string> option
    /// Mutable cell collecting `RetrievedSource`s populated by RAG-style
    /// builders (`RAGPromptBuilder.withRetrieval`) so the assistant
    /// turn can attach them to the outbound `ConversationMessage`
    /// without coupling the prompt-builder return type to the wire-format
    /// shape. Keeps `SystemPromptBuilder` returning `Async<string>` while
    /// still letting builders surface structured side-data to the handler.
    /// Builders that don't perform retrieval ignore this;
    /// `PromptContribution.collect` reads it once the builder resolves,
    /// and every turn allocates a fresh cell.
    RetrievedSources: RetrievedSource list ref
    /// Per-request server-side short-circuit channel. A builder that
    /// must refuse the turn WITHOUT invoking the provider (e.g.
    /// `RAGPromptBuilder` under `StrictlyGrounded` when retrieval
    /// found nothing) sets `Value <- Some <reply text>`. The turn reads
    /// it through `PromptContribution.collect` and, if set, replies with
    /// that text and skips the model call entirely — the
    /// "server-side guard" the grounding contract promises. Builders
    /// that never short-circuit leave it `None`. Fresh cell per
    /// request (same lifecycle as `RetrievedSources`).
    ShortCircuit: string option ref
    /// The answer plan compiled for this turn, named by id.
    /// `RAGPromptBuilder`'s planned-retrieval path sets it when a
    /// composed `IFactClausePlanner` produced clauses; it stays `None`
    /// on every turn that planned nothing and in every composition
    /// without the fact tier (GP 11 / GP 13).
    ///
    /// The channel exists so the plan is **reused, not recomputed**
    /// compiling a question can cost a provider round-trip, so
    /// the provenance recording that follows the answer names the
    /// already-computed plan by this id rather than re-compiling the
    /// same question. Only the id crosses — the plan itself is a
    /// fact-companion type, retained by the planner that made it.
    /// Same mutable-cell lifecycle as `RetrievedSources` / `ShortCircuit`
    /// (fresh cell per request).
    PlannedAnswerId: string option ref
    /// The storage scope the platform's scope resolution
    /// minted for this request (`ScopeResolution.forRequest`), or the
    /// explicit anonymous scope when it minted none. Builders that read
    /// the fact tier (the clause planner, the retrieval pipeline) key on
    /// this value rather than deriving a scope of their own from
    /// `Access`, so a turn reads the one shard its request resolved to.
    /// A context built without a request (tests, startup-time prompt
    /// construction) carries `ResolvedScope.anonymous`, never a derived
    /// scope.
    Scope: ResolvedScope
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

/// What a prompt builder contributed to one turn: the prompt text and the
/// side data a RAG-style builder reports beside it. The turn reads this
/// record and nothing else of the builder's run.
type PromptContribution = {
    /// The composed system prompt, `None` when the builders produced none.
    SystemPrompt: string option
    /// The sources a retrieving builder grounded the prompt on, carried to
    /// the assistant message's Sources panel and the citation normaliser.
    RetrievedSources: RetrievedSource list
    /// A reply the builder demanded instead of a provider call (a
    /// strictly grounded retrieval that found nothing).
    ShortCircuit: string option
    /// The id of the answer plan compiled for this turn, when one was.
    PlannedAnswerId: string option
}

module PromptContribution =
    /// Run `builder` over `context` and read what it contributed. The
    /// context's three side-data cells are the builder contract; this is
    /// the one place they are read, so the turn holds a value, not cells.
    /// No builder contributes nothing.
    let collect (builder: SystemPromptBuilder option) (context: PromptContext) : Async<PromptContribution> = async {
        let! text =
            match builder with
            | Some build -> build context
            | None -> async.Return ""

        return {
            SystemPrompt = if text = "" then None else Some text
            RetrievedSources = context.RetrievedSources.Value
            ShortCircuit = context.ShortCircuit.Value
            PlannedAnswerId = context.PlannedAnswerId.Value
        }
    }

/// The agent loop's limits. Every field defaults to the value the loop has
/// always used (`AgentLoopLimits.defaults`); a deployment that needs other
/// numbers sets `AIAssistantServerConfig.Limits`.
type AgentLoopLimits = {
    /// Turns one message may take before the loop stops it as a runaway.
    MaxTurns: int
    /// Consecutive turns of only failing tool calls before the loop stops
    /// early. One errored turn is normal recovery.
    MaxConsecutiveErrored: int
    /// Extra attempts after the provider reports its own retry budget
    /// exhausted (`RetriesExhausted`). Other provider errors are final.
    OuterRetries: int
    /// The per-call cap on an interactive provider call. Shorter than the
    /// client's 60 s watchdog, so a hung call surfaces as its own error.
    ProviderSendTimeout: TimeSpan
    /// How long the loop waits for the browser to post a client-resident
    /// tool's result. The consent and approval prompts wait the platform
    /// default (`AgentLoopLimits.DefaultClientToolTimeoutMs`).
    ClientToolTimeout: TimeSpan
    /// Prior provider-history messages replayed to the model each turn;
    /// older ones are dropped with a warning. A non-positive value means
    /// the default.
    HistoryWindow: int
}

module AgentLoopLimits =
    /// The platform default for a wait on the browser: the client-resident
    /// tool result, the consent prompt and the approval hold. 90 s, longer
    /// than the client's own 30 s watchdog, so the client-side failure wins
    /// the race and the user sees its specific cause.
    [<Literal>]
    let DefaultClientToolTimeoutMs = 90_000

    /// The default history window: generous enough that ordinary
    /// conversations are never truncated, bounded so a long-running one
    /// cannot grow per-turn token spend without limit.
    [<Literal>]
    let DefaultHistoryWindow = 60

    /// The limits the loop has always run under.
    let defaults: AgentLoopLimits = {
        MaxTurns = 15
        MaxConsecutiveErrored = 2
        OuterRetries = 1
        ProviderSendTimeout = TimeSpan.FromSeconds 50.0
        ClientToolTimeout = TimeSpan.FromMilliseconds(float DefaultClientToolTimeoutMs)
        HistoryWindow = DefaultHistoryWindow
    }

    /// The history window to apply: `HistoryWindow`, or the default when it
    /// is not positive.
    let historyWindow (limits: AgentLoopLimits) : int =
        if limits.HistoryWindow > 0 then
            limits.HistoryWindow
        else
            DefaultHistoryWindow

/// Server-side AI assistant configuration. Contains both the client-visible
/// `Branding` (which will be forwarded to the client via the AI module's
/// props) and the server-only `SystemPrompt` builder. Passed to the
/// `composeWithAI` wrapper.
type AIAssistantServerConfig = {
    Branding: AIAssistantBranding
    /// Optional system-prompt builder. When `None`, no system prompt is
    /// sent to the provider.
    SystemPrompt: SystemPromptBuilder option
    /// The agent loop's limits, including the history window replayed to
    /// the model each turn. `AgentLoopLimits.defaults` is what the loop has
    /// always used.
    Limits: AgentLoopLimits
    /// How the server decides each turn's authoritative `AISurface` (which
    /// drives `FullPageOnly` tool gating). Default `TrustClient` takes the
    /// client-supplied `AIMessageRequest.Surface` at face value.
    /// `DeriveFromCookie key` derives the surface from a signed
    /// server-issued capability cookie and demotes a client that lies
    /// about its surface (defence in depth). See `AISurfaceCapability`
    /// and TECHNICAL_GUIDE §"Surface determination & trust model".
    AISurfaceDerivation: AISurfaceDerivationMode
}

/// Companion helpers for `AIAssistantServerConfig`.
module AIAssistantServerConfig =
    /// The loop's limits under `cfg`: its `Limits`, or the defaults when no
    /// configuration was composed.
    let limits (cfg: AIAssistantServerConfig option) : AgentLoopLimits =
        cfg |> Option.map _.Limits |> Option.defaultValue AgentLoopLimits.defaults