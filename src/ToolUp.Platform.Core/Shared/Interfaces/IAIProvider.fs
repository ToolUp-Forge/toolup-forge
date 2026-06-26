// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.AI

// Phase 11.C.5 Tier 3 — the unified `RetryPolicy` lives at the root
// `ToolUp.Platform` namespace; opening it here so this file's
// signatures can reference the type unqualified (the pre-11.C.5
// AI-specific `RetryPolicy` was declared in this same namespace, so
// no `open` was needed; the relocation introduces this one-liner).
open ToolUp.Platform

// ─── Connector contract value types — relocated (Phase 250) ───────
//
// The message / tool / response / error value types this interface
// exchanges (`AIProviderMessage`, `AIProviderToolCall`,
// `AIProviderToolResult`, `AIProviderToolDef`, `AIProviderResponse`,
// `TokenUsage`, `AIProviderCapabilities`, `AIProviderError`,
// `AIContentPart` / `ImageSource` / `ImagePayload`, + their companion
// modules) moved to the Fable-safe `ToolUp.AI.Wire` tier in
// `Contract.fs`, **keeping the `ToolUp.Platform.AI` namespace** — so a
// browser host can reference the contract directly while every existing
// `open ToolUp.Platform.AI` consumer (including union-case constructors
// and companion modules, which a type abbreviation could not republish)
// compiles byte-for-byte unchanged (GP 11 / GP 12). `ToolUp.Platform.Core`
// takes a project reference on `ToolUp.AI.Wire`; the types resolve here in
// the same namespace, merely from a lower assembly. The `IAIProvider`
// interface itself stays in Core because it references the server-tier
// `RetryPolicy`; `RetryPolicy` is the unified type from
// `Shared/Types/RetryPolicy.fs` (compiled before this file). AI providers
// consume it via `policy.MaxAttempts`, `policy.InitialBackoff`,
// `policy.MaxBackoff`, and `policy.Timeout`.

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

    /// Phase 67b — schema-respecting structured-output. The provider
    /// guarantees (best-effort) that `AIProviderResponse.Content` is
    /// a JSON document conforming to the supplied `schema`. `schema`
    /// is a JSON Schema as a string (same convention as
    /// `AIProviderToolDef.InputSchema`); providers parse it internally
    /// and translate to their native structured-output wire format
    /// (Gemini `generationConfig.responseSchema`, OpenAI
    /// `response_format: { type: "json_schema" }`, Claude tool-based
    /// workaround).
    ///
    /// Non-streaming only — streaming structured-output is deferred to
    /// a follow-on phase. Tool use is permitted; the schema applies to
    /// the final assistant turn's text content.
    ///
    /// Backward-compat contract for implementers: providers without
    /// native structured-output may delegate to
    /// `IAIProviderDefaults.sendStructuredViaFallback` — a one-line
    /// implementation that prepends a schema-as-instruction to the
    /// system prompt, calls `SendMessage`, and post-validates as
    /// parseable JSON. The shipped providers (Gemini, OpenAI, Claude)
    /// override natively; external implementers may opt into the
    /// fallback. Deployments using only `SendMessage` are unaffected.
    ///
    /// Returns `Ok response` with schema-conformant `Content` on
    /// success. Returns `Error SchemaUnsupported` when the provider
    /// (or the fallback) cannot honour the schema; returns other
    /// `AIProviderError` cases for transport / model / retry
    /// failures, identically to `SendMessage`.
    abstract SendStructuredMessage:
        messages: AIProviderMessage list *
        tools: AIProviderToolDef list *
        systemPrompt: string option *
        schema: string *
        retryPolicy: RetryPolicy ->
            Async<Result<AIProviderResponse, AIProviderError>>

/// Phase 67b — fallback implementations external `IAIProvider`
/// implementers may compose into their own `SendStructuredMessage`
/// methods. The shipped providers (Gemini, OpenAI, Claude) provide
/// native implementations; this helper is the path for non-native
/// providers (and the contract test surface for the default fallback).
///
/// Portable (Phase 250). The JSON post-validation routes through the
/// Fable-safe `ToolUp.AI.Wire.JsonHost.parse` (browser `JSON.parse` under
/// Fable, `System.Text.Json` on .NET) instead of an unguarded
/// `System.Text.Json.JsonDocument.Parse`, so the whole module — and with
/// it the connector contract — compiles to both hosts with no
/// `#if !FABLE_COMPILER` guard.
module IAIProviderDefaults =
    /// Default-impl fallback for `IAIProvider.SendStructuredMessage`.
    /// Prepends the schema as a system-prompt instruction, calls
    /// `provider.SendMessage`, and post-validates that the response
    /// is parseable JSON. Returns `Error SchemaUnsupported` when the
    /// content is empty or non-JSON; transport / model / retry
    /// errors propagate from `SendMessage` unchanged.
    ///
    /// One-line implementer adoption:
    ///   member this.SendStructuredMessage(m, t, s, sch, r) =
    ///       IAIProviderDefaults.sendStructuredViaFallback this m t s sch r
    let sendStructuredViaFallback
        (provider: IAIProvider)
        (messages: AIProviderMessage list)
        (tools: AIProviderToolDef list)
        (systemPrompt: string option)
        (schema: string)
        (retryPolicy: RetryPolicy)
        : Async<Result<AIProviderResponse, AIProviderError>> =
        async {
            let schemaInstruction =
                sprintf
                    "You MUST respond with a single JSON document and nothing else (no prose, no Markdown code fences). The JSON document MUST conform to this JSON Schema:\n\n%s"
                    schema

            let combinedPrompt =
                match systemPrompt with
                | Some p -> Some(sprintf "%s\n\n%s" p schemaInstruction)
                | None -> Some schemaInstruction

            let! result = provider.SendMessage(messages, tools, combinedPrompt, None, retryPolicy)

            return
                result
                |> Result.bind (fun r ->
                    let content = if isNull r.Content then "" else r.Content.Trim()

                    if content = "" then
                        Error(
                            SchemaUnsupported(
                                "structured-output",
                                "Provider returned empty content; this provider may lack native structured-output support."
                            )
                        )
                    else
                        // `JsonHost.parse` is total (returns `option`, never
                        // throws) and host-bridged, so the validation needs no
                        // try/with and carries no host-specific parse exception
                        // to report — `None` is the single "not parseable JSON"
                        // signal on both .NET and Fable.
                        match ToolUp.AI.Wire.JsonHost.parse content with
                        | Some _ -> Ok r
                        | None ->
                            Error(
                                SchemaUnsupported(
                                    "structured-output",
                                    "Provider returned non-JSON content; this provider may lack native structured-output support."
                                )
                            ))
        }