// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.AI

// Phase 11.C.5 Tier 3 — the unified `RetryPolicy` lives at the root
// `ToolUp.Platform` namespace; opening it here so this file's
// signatures can reference the type unqualified (the pre-11.C.5
// AI-specific `RetryPolicy` was declared in this same namespace, so
// no `open` was needed; the relocation introduces this one-liner).
open ToolUp.Platform

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

// Phase 11.C.5 Tier 3 — `RetryPolicy` is the unified type defined at
// `Shared/Types/RetryPolicy.fs` (compiled before this file). The old
// AI-specific shape (`{ MaxRetries; BackoffMs; TimeoutMs }`) was
// merged with the audit-side `AuditReplicatorRetryPolicy` shape. AI
// providers consume the unified record via `policy.MaxAttempts`,
// `policy.InitialBackoff`, `policy.MaxBackoff`, and `policy.Timeout`.

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
        | UnsupportedCapability _ -> false

// ─── Provider interface ──────────────────────────────────────────

/// Abstraction over any AI provider (Claude, GPT, etc.).
/// Handles a single request/response turn. The agent loop is owned
/// by the platform, not the provider.
type IAIProvider =
    /// What this provider supports. Shell and agent loop gate features
    /// on these flags; missing capabilities degrade gracefully.
    abstract Capabilities: AIProviderCapabilities

    /// Send messages with available tools, get a response.
    /// The provider handles API communication only — no tool execution.
    ///
    /// `retryPolicy` instructs the provider how to recover from transient
    /// failures. Providers MUST honour `MaxAttempts = 1` as fail-fast and
    /// SHOULD apply exponential backoff derived from `InitialBackoff`,
    /// capped at `MaxBackoff`. The optional per-call `Timeout` is the
    /// wall-clock bound on the entire send (including retries).
    ///
    /// Streaming calls (onStream = Some _) MUST NOT be retried after any
    /// partial content has been delivered to the callback — doing so
    /// would duplicate output. Providers MUST classify mid-stream
    /// failures as `StreamingAborted` with the accumulated partial text
    /// so callers can surface a diagnostic message to the user.
    ///
    /// The synchronous `onStream: (string -> unit) option` callback is a
    /// documented exemption to portability rule 2 — the method itself is
    /// `Async<_>`, and per-delta streaming callbacks stay synchronous by
    /// design.
    ///
    /// Returns `Ok response` on success, `Error AIProviderError` on failure.
    /// Transient errors are absorbed by the provider's retry loop;
    /// catastrophic errors (PermanentClient, MalformedResponse,
    /// StreamingAborted, RetriesExhausted) propagate to the caller.
    abstract SendMessage:
        messages: AIProviderMessage list *
        tools: AIProviderToolDef list *
        systemPrompt: string option *
        onStream: (string -> unit) option *
        retryPolicy: RetryPolicy ->
            Async<Result<AIProviderResponse, AIProviderError>>