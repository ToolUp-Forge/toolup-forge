// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.AI

// ─── Portable connector contract (Wave 32, Phase 250) ────────────
//
// The connector *value types* — the message / tool / response / error
// records + DUs an `IAIProvider` exchanges with the platform — live here
// in the Fable-safe `ToolUp.AI.Wire` tier so a browser host can reference
// the contract directly. They are pure F# records + DUs (FSharp.Core only;
// `System.Uri` in `AIContentPart.redactedSummary` is the one BCL type, and
// it is Fable-supported), so the same source compiles to both the .NET
// server host and a Fable browser host.
//
// **Namespace is deliberately `ToolUp.Platform.AI`, not `ToolUp.AI.Wire`.**
// The types relocated here from `ToolUp.Platform.Core`'s `IAIProvider.fs`
// (which keeps the `IAIProvider` interface itself — it references the
// server-tier `RetryPolicy`). Keeping the *namespace* unchanged is what
// makes the move byte-stable for every existing consumer: `open
// ToolUp.Platform.AI` still brings the types, their union-case constructors
// (`TextPart`, `TransientNetwork`, …), AND their companion modules
// (`AIProviderError.toMessage`, …) into scope exactly as before — a type
// abbreviation cannot republish union cases or companion modules, so the
// namespace-preserving relocation (not aliasing) is the mechanism that
// holds the public surface stable (GP 11). `ToolUp.Platform.Core` takes a
// project reference on this tier; the types remain in the same namespace,
// merely in a lower assembly.

// ─── Provider-level message types ────────────────────────────────
// These map to AI API wire format, distinct from ConversationMessage
// which represents persisted conversation history.

/// A tool call requested by the AI provider
type AIProviderToolCall = {
    Id: string
    Name: string
    Arguments: string // JSON
}

/// A result returned to the AI provider after executing a tool call
type AIProviderToolResult = { ToolCallId: string; Content: string }

// ─── Multimodal content parts (Phase 6o) ─────────────────────────
//
// `AIContentPart` is the wire format for messages whose content is
// richer than a single string — typically text + one or more
// images. Plain-text messages set `AIProviderMessage.Parts = []` and
// continue to use the `Content: string` field; messages carrying
// images populate `Parts` and providers select the multipart shape
// in their request body builder. The `Content` field stays in place
// so existing handlers compile unchanged and the agent loop can
// still log a textual preview.

/// Wire shape for an image attached to an AI message. `MediaType`
/// is the IANA MIME type ("image/jpeg", "image/png", "image/webp"
/// — see vendor docs for the per-provider supported list).
type ImageSource =
    /// In-memory image bytes. Providers base64-encode at the wire
    /// boundary; bytes never enter audit blobs or latency events
    /// (see `AIContentPart.redactedSummary`).
    | Base64Bytes of byte[]
    /// External URL the provider fetches server-side. Providers that
    /// don't support URL sources (or don't trust them in the
    /// deployment's threat model) may reject with
    /// `AIProviderError.UnsupportedCapability`.
    | Url of string

type ImagePayload = {
    MediaType: string
    Source: ImageSource
}

/// A single chunk of a multipart message. Providers iterate the
/// list and emit one wire block per part in the vendor's native
/// content-block shape (Anthropic `content: [...]`, OpenAI
/// `content: [{type: "image_url", ...}]`).
type AIContentPart =
    | TextPart of string
    | ImagePart of ImagePayload

module AIContentPart =
    /// PII / payload-size-safe summary suitable for audit logs +
    /// latency events. Image bytes NEVER enter persisted blobs —
    /// they're large (typical mobile receipt: 1–5 MB base64) and
    /// frequently PII (faces, locations, sensitive documents).
    let redactedSummary (part: AIContentPart) : string =
        match part with
        | TextPart s ->
            // Keep first 200 chars of text content; long prompts can
            // still leak PII but the caller can intercept further
            // upstream via the existing AIAgentEngine redaction.
            if s.Length > 200 then s.Substring(0, 200) + "…" else s
        | ImagePart payload ->
            match payload.Source with
            | Base64Bytes bytes -> sprintf "[image: %d bytes, type=%s]" bytes.Length payload.MediaType
            | Url u -> sprintf "[image: url, type=%s, host=%s]" payload.MediaType (System.Uri(u).Host)

    /// Markdown placeholder for the conversation export. Exports
    /// must be paste-able into Slack / email / issue trackers, so
    /// embedded multi-MB base64 strings are unsafe / unkind /
    /// unhelpful.
    let exportPlaceholder (index: int) (part: AIContentPart) : string =
        match part with
        | TextPart s -> s
        | ImagePart _ -> sprintf "![image #%d]" index

/// A single message in the AI provider's conversation format. The
/// optional multimodal payload travels in `Parts`; the `Content`
/// field carries the plain-text body (and stays populated even for
/// multipart messages so audit / latency events can fall back to
/// the text portion when bytes need to be redacted).
type AIProviderMessage = {
    Role: string // "user" | "assistant" | "system"
    Content: string
    ToolCalls: AIProviderToolCall list
    ToolResults: AIProviderToolResult list
    /// Phase 6o multimodal content blocks. Empty for plain-text
    /// messages — providers continue to emit the legacy string
    /// shape. Populated for multipart messages — providers emit
    /// the vendor-native content-block array.
    Parts: AIContentPart list
}

module AIProviderMessage =
    /// Construct a plain-text message — convenience for the common
    /// case where `Parts` should be empty.
    let text (role: string) (content: string) : AIProviderMessage = {
        Role = role
        Content = content
        ToolCalls = []
        ToolResults = []
        Parts = []
    }

    /// Construct a multipart message. The `Content` field is set
    /// to the concatenated text parts so audit / latency events
    /// have a fallback when image bytes are redacted.
    let multipart (role: string) (parts: AIContentPart list) : AIProviderMessage =
        let textConcat =
            parts
            |> List.choose (function
                | TextPart s -> Some s
                | ImagePart _ -> None)
            |> String.concat " "

        {
            Role = role
            Content = textConcat
            ToolCalls = []
            ToolResults = []
            Parts = parts
        }

    /// True if this message carries any image part.
    let isMultimodal (msg: AIProviderMessage) : bool =
        msg.Parts
        |> List.exists (function
            | ImagePart _ -> true
            | TextPart _ -> false)

    /// Redacted summary suitable for `IAuditLog` / latency events.
    /// Image bytes never enter the returned string — only metadata.
    let redactedSummary (msg: AIProviderMessage) : string =
        if msg.Parts.IsEmpty then
            if msg.Content.Length > 200 then
                msg.Content.Substring(0, 200) + "…"
            else
                msg.Content
        else
            msg.Parts |> List.map AIContentPart.redactedSummary |> String.concat " "

/// Tool definition in the format the AI provider expects
type AIProviderToolDef = {
    Name: string
    Description: string
    InputSchema: string // JSON Schema as string
}

/// Provider-reported token accounting for a single turn.
///
/// Vocabulary is normalised across providers so the latency rollup stays
/// provider-agnostic:
/// - Anthropic: `PromptTokens` = `input_tokens + cache_creation_input_tokens
///   + cache_read_input_tokens`; `CachedPromptTokens` =
///   `cache_read_input_tokens`; `CacheCreationTokens` =
///   `Some cache_creation_input_tokens`.
/// - OpenAI: `PromptTokens` = `usage.prompt_tokens`; `CachedPromptTokens` =
///   `usage.prompt_tokens_details.cached_tokens` (0 if absent);
///   `CacheCreationTokens` = `None` (OpenAI does not expose a separate
///   cache-write count — caching is automatic).
type TokenUsage = {
    /// Total input tokens for this turn (cached + new).
    PromptTokens: int
    /// Portion of `PromptTokens` served from the provider's prompt cache.
    /// 0 when the prefix was uncached or the provider does not cache.
    CachedPromptTokens: int
    /// Generated output tokens.
    OutputTokens: int
    /// Provider-specific: tokens written to the prompt cache this turn.
    /// Anthropic populates; OpenAI returns `None`. Operationally useful
    /// for cost analysis (cache writes are billed differently from reads).
    CacheCreationTokens: int option
}

/// Result from a single AI provider call (one turn, not the full agent loop)
type AIProviderResponse = {
    Content: string
    ToolCalls: AIProviderToolCall list
    StopReason: string // "end_turn" | "tool_use" | "max_tokens"
    /// Provider-reported token usage for this turn. `None` when the
    /// provider could not extract usage (transient parse failure, or a
    /// streaming early-exit that missed the final usage chunk). Healthy
    /// responses from caching-capable providers always populate.
    Usage: TokenUsage option
}

/// Provider capability flags. Clients and the agent loop read these to
/// decide whether a feature is supported before invoking it (streaming,
/// tool use, vision, caching). Prevents silent mid-conversation failures
/// when a deployment swaps providers.
type AIProviderCapabilities = {
    /// Provider can stream partial responses (onStream callback honoured).
    Streaming: bool
    /// Provider supports tool-use loops (tool calls in the response).
    ToolUse: bool
    /// Provider accepts image inputs in messages.
    Vision: bool
    /// Provider populates `AIProviderResponse.Usage.CachedPromptTokens`
    /// when its prompt cache hits. Anthropic and OpenAI both `true`;
    /// providers without prompt caching set `false` so the latency
    /// rollup hides the cache-hit-rate column for that provider/model
    /// bucket.
    SupportsPromptCaching: bool
    /// Phase 6j.B — this provider can serve a Tier-3 *triage* turn: a
    /// single, tool-free, schema-constrained call whose only job is to
    /// decide whether a trivial UI instruction ("set country to UK")
    /// maps onto one declared field, or needs the full agent loop.
    ///
    /// `false` (the value every pre-6j.B provider carries, and the
    /// value `unknown` carries) means the fast-path triage resolver
    /// never fires for this provider and the request goes straight to
    /// the full agent loop — byte-for-byte the pre-6j.B path (GP 11).
    /// Declare `true` only when the provider can honour a small
    /// structured-output request cheaply; a provider whose only model
    /// is a frontier model gains nothing from triage and should leave
    /// this `false`.
    SupportsTriage: bool
    /// Phase 6j.B — the model id this provider family would use to
    /// serve triage, when it has a cheaper tier than `Model` (an
    /// Anthropic connector names its Haiku-grade id here; an OpenAI
    /// connector its mini-grade id).
    ///
    /// Since Phase 661 this is a **dispatch instruction the triage
    /// resolver honours by itself**: with no explicit
    /// `FastPathTriageConfig.TriageProvider` wired, the resolver names
    /// this id on the triage call through the per-call model override
    /// (`AIProviderCallOptions.Model` → `IAIProviderModelOverride`,
    /// which every shipped connector implements), so the cheap turn
    /// runs on this model and the agent loop keeps `Model` — with no
    /// second provider instance for a composition root to build. A
    /// connector that cannot serve the id, or does not implement the
    /// override, serves triage on `Model` and the triage telemetry row
    /// says so (`Route = override-fallback`). `TriageProvider` remains
    /// the explicit escape hatch and wins when set.
    /// `None` ⇒ the provider declares no cheaper tier; triage (when
    /// enabled and `SupportsTriage`) runs on `Model` itself.
    TriageModelId: string option
    /// Identifier for diagnostics and logging.
    ProviderName: string
    /// Model identifier currently configured.
    Model: string
}

module AIProviderCapabilities =
    /// Unknown provider — defaults to no capabilities. Prefer an explicit
    /// declaration over this fallback.
    let unknown = {
        Streaming = false
        ToolUse = false
        Vision = false
        SupportsPromptCaching = false
        SupportsTriage = false
        TriageModelId = None
        ProviderName = "unknown"
        Model = "unknown"
    }

// ─── Provider errors ─────────────────────────────────────────────

/// Classification of AI provider failures. Providers return these through
/// SendMessage's Result channel so the agent loop and SubmitMessage boundary
/// can reason about errors without string-matching. Parallels the pattern
/// used by ToolInvocationError for tool dispatch failures — but provider
/// errors are NEVER fed back to the model as tool results; they surface to
/// the user as a failed AITask.
type AIProviderError =
    /// Transport-level transient failure: connection refused, TCP reset,
    /// DNS resolution failure, or a timeout from the HTTP client. Retry-
    /// worthy within the provider's RetryPolicy budget.
    | TransientNetwork of message: string
    /// HTTP 429 (rate-limited) or 5xx server error. Retry-worthy with
    /// exponential backoff derived from `RetryPolicy.InitialBackoff`.
    | TransientServer of statusCode: int * message: string
    /// HTTP 4xx other than 429 — authentication failure, bad request,
    /// model not found, content policy violation. Same request would
    /// fail identically; NOT retry-worthy.
    | PermanentClient of statusCode: int * message: string
    /// Provider returned an unexpected response shape (JSON parse failure,
    /// missing required field, schema mismatch). Treated as catastrophic;
    /// not retryable because another attempt will parse the same way.
    | MalformedResponse of detail: string
    /// Streaming delivery failed after partial content was delivered to
    /// the onStream callback. The accumulator already emitted partial
    /// output so the caller cannot retry safely — partial text is
    /// preserved for diagnostics and the error must surface to the user.
    | StreamingAborted of partialText: string * detail: string
    /// The provider's internal retry loop exhausted its RetryPolicy budget
    /// without a successful response. Wraps the last inner error so
    /// callers can still distinguish root cause.
    | RetriesExhausted of attempts: int * lastError: AIProviderError
    /// Phase 6o — the caller asked for a feature the active model
    /// doesn't support (currently: vision input against a
    /// non-vision-capable model). Fail synchronously rather than
    /// shipping the image to the vendor and waiting for HTTP 400;
    /// callers get the same diagnostic at substantially lower cost.
    | UnsupportedCapability of feature: string * detail: string
    /// Phase 67b — `SendStructuredMessage` was called but the response
    /// did not conform to the supplied JSON Schema. Distinct from
    /// `UnsupportedCapability` because the provider attempted the
    /// request (and may even support structured-output natively) — the
    /// failure is at the schema-conformance boundary, not at the
    /// feature-availability boundary.
    ///
    /// Sub-causes encoded in `feature`:
    /// - `"structured-output"` — the default-impl fallback path; the
    ///   provider lacks native structured-output and the post-hoc
    ///   JSON-parse check failed.
    /// - `"oneOf"` / `"anyOf"` / `"$ref"` / ... — the schema uses a
    ///   feature this provider's native structured-output mode cannot
    ///   honour. `detail` cites the offending JSON path.
    /// NOT retryable — the same request would fail identically.
    | SchemaUnsupported of feature: string * detail: string

module AIProviderError =
    /// Human-readable rendering for logs, UI, SSE error events, and
    /// AITaskFailed messages.
    let rec toMessage (err: AIProviderError) =
        match err with
        | TransientNetwork m -> $"Transient network error: {m}"
        | TransientServer(code, m) -> $"Server error (HTTP {code}): {m}"
        | PermanentClient(code, m) -> $"Permanent client error (HTTP {code}): {m}"
        | MalformedResponse d -> $"Malformed provider response: {d}"
        | StreamingAborted(_, d) -> $"Streaming aborted mid-response: {d}"
        | RetriesExhausted(n, inner) -> $"Retries exhausted after {n} attempts. Last error: {toMessage inner}"
        | UnsupportedCapability(feature, detail) -> $"Unsupported capability '{feature}': {detail}"
        | SchemaUnsupported(feature, detail) -> $"Schema feature '{feature}' not honoured by provider: {detail}"

    /// Whether a single attempt's error justifies another retry inside
    /// the provider's loop. Callers (e.g. the agent loop) use different
    /// rules — StreamingAborted is always terminal at the agent level.
    let isRetryable (err: AIProviderError) =
        match err with
        | TransientNetwork _
        | TransientServer _ -> true
        | PermanentClient _
        | MalformedResponse _
        | StreamingAborted _
        | RetriesExhausted _
        | UnsupportedCapability _
        | SchemaUnsupported _ -> false


// ─── Per-call options (Phase 661) ────────────────────────────────
//
// `IAIProvider.SendMessage` / `SendStructuredMessage` take positional
// arguments, not a request record, so there was no request field to
// widen. The per-call model rides a small options record instead: a
// future per-call knob (a temperature, a reasoning budget) is an
// additive field here, not another interface change. The record is
// Fable-safe like the rest of this file — a browser host that drives a
// connector through the wire tier can name a model per call too.

/// Options that apply to ONE provider call, carried beside the
/// messages rather than baked into the provider instance.
///
/// `Model = None` is today's behaviour, byte-identical: the provider
/// serves the call on its configured `Capabilities.Model`, and the
/// request bytes it emits are unchanged from a plain `SendMessage`.
type AIProviderCallOptions = {
    /// The model id to serve THIS call on, in the provider's own id
    /// vocabulary (an Anthropic connector reads `claude-…`, a Gemini
    /// connector `models/gemini-…`). A provider that cannot serve the
    /// named id — a foreign vendor's id, a blank — serves the call on
    /// its configured model and reports `OverrideFellBack` rather than
    /// failing the call; see `ModelOverrideOutcome`.
    Model: string option
}

module AIProviderCallOptions =
    /// No per-call options — the configured model, unchanged bytes.
    let none: AIProviderCallOptions = { Model = None }

    /// Ask for one call on `model`.
    let forModel (model: string) : AIProviderCallOptions = { Model = Some model }

/// What the provider did about `AIProviderCallOptions.Model` on one
/// call — the response metadata a caller reads to tell a triage turn
/// that ran on the cheap model from one quietly served by the frontier
/// model. Every case names the model that actually served.
type ModelOverrideOutcome =
    /// No override was requested; the configured model served.
    | ConfiguredModel of model: string
    /// The override was requested and the provider served it.
    | OverrideHonoured of model: string
    /// The override was requested and the provider could not serve it:
    /// it fell back to its configured model (`served`) and says why.
    /// The call itself succeeded — a fallback is metadata, not an error.
    | OverrideFellBack of requested: string * served: string * reason: string

module ModelOverrideOutcome =
    /// The model that actually served the call.
    let served (outcome: ModelOverrideOutcome) : string =
        match outcome with
        | ConfiguredModel m
        | OverrideHonoured m -> m
        | OverrideFellBack(_, s, _) -> s

    /// True when an override was asked for and NOT honoured.
    let fellBack (outcome: ModelOverrideOutcome) : bool =
        match outcome with
        | OverrideFellBack _ -> true
        | ConfiguredModel _
        | OverrideHonoured _ -> false

    /// Short route tag for telemetry rows and log lines:
    /// `configured` | `override` | `override-fallback`.
    let route (outcome: ModelOverrideOutcome) : string =
        match outcome with
        | ConfiguredModel _ -> "configured"
        | OverrideHonoured _ -> "override"
        | OverrideFellBack _ -> "override-fallback"

    /// Human-readable rendering for logs.
    let describe (outcome: ModelOverrideOutcome) : string =
        match outcome with
        | ConfiguredModel m -> $"served on configured model '{m}'"
        | OverrideHonoured m -> $"served on requested model '{m}'"
        | OverrideFellBack(requested, served, reason) ->
            $"requested model '{requested}' not served ({reason}); fell back to '{served}'"

    /// The one resolution rule every connector applies: decide which
    /// model serves the call and record the outcome.
    ///
    /// - `Model = None` ⇒ `configured`, `ConfiguredModel`.
    /// - a blank id ⇒ `configured`, `OverrideFellBack` ("blank model id").
    /// - `canServe id` ⇒ `id`, `OverrideHonoured` (the configured id
    ///   itself is trivially honoured).
    /// - otherwise ⇒ `configured`, `OverrideFellBack` with `unservedReason`.
    ///
    /// `canServe` is the connector's static family check (an Anthropic
    /// connector serves `claude-…` ids and nothing else); it is a
    /// vocabulary test, not a probe — an id in the right family that
    /// the vendor has retired still fails at HTTP time, as it always did.
    let resolve
        (canServe: string -> bool)
        (unservedReason: string)
        (configured: string)
        (options: AIProviderCallOptions)
        : string * ModelOverrideOutcome =
        match options.Model with
        | None -> configured, ConfiguredModel configured
        | Some requested when System.String.IsNullOrWhiteSpace requested ->
            configured, OverrideFellBack(requested, configured, "blank model id")
        | Some requested when canServe requested -> requested, OverrideHonoured requested
        | Some requested -> configured, OverrideFellBack(requested, configured, unservedReason)

/// A provider call that carried `AIProviderCallOptions`: the ordinary
/// response plus what the provider did about the options.
type AIProviderCallResponse = {
    /// The turn, exactly as `SendMessage` / `SendStructuredMessage`
    /// would have returned it.
    Response: AIProviderResponse
    /// Which model served, and whether that was the one asked for.
    Model: ModelOverrideOutcome
}

module AIProviderCallResponse =
    /// Attach an outcome to a plain send's result — the shape every
    /// connector's override path returns after delegating the transport
    /// to its ordinary send.
    let attach
        (outcome: ModelOverrideOutcome)
        (call: Async<Result<AIProviderResponse, AIProviderError>>)
        : Async<Result<AIProviderCallResponse, AIProviderError>> =
        async {
            let! result = call
            return result |> Result.map (fun response -> { Response = response; Model = outcome })
        }

/// Vendor-family vocabulary tests over model ids, shared by the
/// shipped connectors' `canServe` checks so four connectors agree on
/// what "a foreign vendor's id" means. Prefix tests only — the
/// vendor's own catalogue is the authority on whether an id exists.
module ModelIdFamily =
    let private normalise (id: string) =
        let trimmed = if isNull id then "" else id.Trim().ToLowerInvariant()

        if trimmed.StartsWith "models/" then
            trimmed.Substring 7
        else
            trimmed

    /// `claude-…` — Anthropic's id vocabulary.
    let isAnthropic (id: string) : bool = (normalise id).StartsWith "claude"

    /// `gemini-…` / `gemma-…`, with or without the `models/` path
    /// prefix the Gemini REST API uses.
    let isGoogle (id: string) : bool =
        let n = normalise id
        n.StartsWith "gemini" || n.StartsWith "gemma"

    /// An id that carries neither of the above families' prefixes —
    /// what an OpenAI-compatible connector (OpenAI itself, an Azure
    /// OpenAI deployment) is prepared to forward.
    let isOpenAICompatible (id: string) : bool =
        not (System.String.IsNullOrWhiteSpace id)
        && not (isAnthropic id)
        && not (isGoogle id)