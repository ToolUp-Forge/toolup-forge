// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.McpHost.McpProtocol

open System
open System.Buffers
open System.Text
open System.Text.Json
open ToolUp.Platform

// ─── Phase 489 — the MCP wire, implemented directly ──────────────────
//
// Model Context Protocol is JSON-RPC 2.0 over an HTTP transport. This
// module is the whole of the wire: parsing a request, rendering a
// response, and the error-code mapping. Everything in it is PURE —
// strings and typed values in, strings out, no `HttpContext`, no DI, no
// I/O — so the protocol conformance the phase's acceptance turns on is
// testable without standing up a server.
//
// **No third-party MCP SDK, deliberately.** GP 1 keeps vendor SDKs out
// of the core and GP 2 keeps the default composition free of heavyweight
// dependencies; an open, published protocol with a JSON-RPC envelope is
// exactly the case where implementing the wire directly costs less than
// carrying a dependency. Precedent in this repo: the peer substrate
// speaks JSON-RPC 2.0 the same way, and for the same reason — a wire
// format committed to third parties must not be able to change under
// them because a package upgraded.
//
// **Batching is not supported, and that is the specification's own
// position.** MCP revision 2025-06-18 removed JSON-RPC batching. A
// top-level array is therefore refused as an invalid request rather than
// silently handled, so a client that would have sent one learns it here
// instead of discovering that only the first element ran.
//
// **The request id is echoed back VERBATIM as raw JSON.** JSON-RPC
// permits a string, a number or null, and a server that re-renders a
// number through its own formatter can return `1.0` where the client
// sent `1` — a correlation failure that looks like a hung request. So
// the id is carried as the exact bytes it arrived as and written back
// unparsed.

/// Cap on any authored or tool-sourced string this module renders onto
/// the wire or into an audit payload.
///
/// Tool error text is attacker-influenced under prompt injection in
/// exactly the way the Phase 47 rollup's refusal reasons are: a tool's
/// message can quote arguments a model chose. So it is control-stripped
/// as well as truncated, for the same three reasons that phase records —
/// an escape sequence smuggled into a console view, an unbounded blob
/// pushed through a JSON response, and an empty string rendering as a
/// blank the reader mistakes for a bug.
[<Literal>]
let DetailMaxChars = 500

/// Control-strip, whitespace-collapse and truncate a string for the wire.
let sanitise (raw: string) : string =
    if String.IsNullOrWhiteSpace raw then
        "(no detail)"
    else
        let despecialised = raw |> String.map (fun c -> if Char.IsControl c then ' ' else c)

        let collapsed =
            despecialised.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
            |> String.concat " "

        if collapsed.Length <= DetailMaxChars then
            collapsed
        else
            collapsed.Substring(0, DetailMaxChars) + "…"

// ─── JSON-RPC error codes ────────────────────────────────────────────

/// Standard JSON-RPC 2.0 reserved codes plus the implementation-defined
/// server codes (-32000 … -32099) this host uses.
[<RequireQualifiedAccess>]
module ErrorCode =
    let parseError = -32700
    let invalidRequest = -32600
    let methodNotFound = -32601
    let invalidParams = -32602
    let internalError = -32603

    /// No agent credential, or one that did not validate. Paired with
    /// HTTP 401 + `WWW-Authenticate`, per the MCP authorization spec.
    let unauthorized = -32001

    /// The named tool is not available to this caller.
    ///
    /// **One code and one message for BOTH "no such tool" and "not
    /// yours".** Discovery is authorisation-filtered, so a caller able to
    /// distinguish the two could enumerate the deployment's entire tool
    /// surface one `tools/call` at a time — which would give back exactly
    /// what filtering `tools/list` took away. The audit trail keeps the
    /// distinction (`McpError.ToolNotFound` vs `McpError.ToolDenied`);
    /// the wire does not.
    let toolUnavailable = -32002

    /// A budget ceiling refused the call. The typed `BudgetDenial` rides
    /// the error's `data` member, so a client branches on the dimension
    /// rather than string-matching a message.
    let budgetExhausted = -32003

    /// The agent's rate limit refused the call. The typed
    /// `RateLimitedError` rides `data`; `Retry-After` rides the HTTP
    /// response.
    let rateLimited = -32004

    /// The tool ran and raised. Present for completeness — a tool
    /// EXECUTION failure is normally reported as a successful JSON-RPC
    /// result carrying `isError: true`, which is the MCP convention and
    /// what this host does (see `toolCallResult`). This code is what a
    /// failure OUTSIDE a tool call maps to.
    let toolFailed = -32005

/// The JSON-RPC error code for one typed refusal.
let errorCode (error: McpError) : int =
    match error with
    | McpError.Unauthorized -> ErrorCode.unauthorized
    | McpError.ToolNotFound _
    | McpError.ToolDenied _ -> ErrorCode.toolUnavailable
    | McpError.BudgetExhausted _ -> ErrorCode.budgetExhausted
    | McpError.RateLimited _ -> ErrorCode.rateLimited
    | McpError.InvalidParams _ -> ErrorCode.invalidParams
    | McpError.ToolFailed _ -> ErrorCode.toolFailed
    | McpError.Protocol _ -> ErrorCode.invalidRequest

/// The one-line human-readable message for a typed refusal.
///
/// `ToolNotFound` and `ToolDenied` render the SAME sentence — see
/// `ErrorCode.toolUnavailable`. `Unauthorized` says nothing at all about
/// why, so the endpoint is not an oracle for which credentials exist.
let errorMessage (error: McpError) : string =
    match error with
    | McpError.Unauthorized -> "Unauthorized: a valid agent credential is required."
    | McpError.ToolNotFound toolName
    | McpError.ToolDenied(toolName, _) -> sprintf "Tool '%s' is not available to this agent." (sanitise toolName)
    | McpError.BudgetExhausted denial -> BudgetDenial.describe denial
    | McpError.RateLimited limited ->
        sprintf
            "Rate limit reached: at most %d calls are admitted per window; retry after %d seconds."
            limited.Limit
            limited.RetryAfterSeconds
    | McpError.InvalidParams detail -> sprintf "Invalid params: %s" (sanitise detail)
    | McpError.ToolFailed(toolName, detail) -> sprintf "Tool '%s' failed: %s" (sanitise toolName) (sanitise detail)
    | McpError.Protocol detail -> sprintf "Invalid request: %s" (sanitise detail)

/// The stable DU case name, payload-free. The safe outcome label for an
/// audit row and for metrics, where the message text could carry a
/// tool's own words.
let errorCaseName (error: McpError) : string =
    match error with
    | McpError.Unauthorized -> "Unauthorized"
    | McpError.ToolNotFound _ -> "ToolNotFound"
    | McpError.ToolDenied _ -> "ToolDenied"
    | McpError.BudgetExhausted _ -> "BudgetExhausted"
    | McpError.RateLimited _ -> "RateLimited"
    | McpError.InvalidParams _ -> "InvalidParams"
    | McpError.ToolFailed _ -> "ToolFailed"
    | McpError.Protocol _ -> "Protocol"

/// The HTTP status this refusal is carried on.
///
/// Almost everything is 200: a JSON-RPC error IS the response, and a
/// transport status that hides the envelope leaves a client unable to
/// correlate the failure with the request it made. The two exceptions
/// are the ones where HTTP semantics carry information a JSON-RPC error
/// cannot — 401 so a client knows to present a credential (the MCP
/// authorization spec requires it, with `WWW-Authenticate`), and 429 so
/// ordinary HTTP retry machinery sees the back-off.
let httpStatus (error: McpError) : int =
    match error with
    | McpError.Unauthorized -> 401
    | McpError.RateLimited _ -> 429
    | _ -> 200

// ─── Requests ────────────────────────────────────────────────────────

/// One parsed JSON-RPC request or notification.
type McpRequest = {
    /// The request id EXACTLY as it arrived, as raw JSON (`"abc"`, `7`,
    /// `null`). `None` for a notification, which by JSON-RPC carries no
    /// id and receives no response.
    RawId: string option
    /// The JSON-RPC method name.
    Method: string
    /// The `params` member, if present.
    Params: JsonElement option
}

[<RequireQualifiedAccess>]
module Method =
    [<Literal>]
    let Initialize = "initialize"

    [<Literal>]
    let Initialized = "notifications/initialized"

    [<Literal>]
    let Ping = "ping"

    [<Literal>]
    let ToolsList = "tools/list"

    [<Literal>]
    let ToolsCall = "tools/call"

/// Parse a request body into one `McpRequest`.
///
/// A top-level array is refused rather than partially handled — see the
/// file header on batching.
let parseRequest (body: string) : Result<McpRequest, McpError> =
    if String.IsNullOrWhiteSpace body then
        Error(McpError.Protocol "the request body was empty")
    else
        let parsed =
            try
                Ok(JsonDocument.Parse body)
            with ex ->
                Error(McpError.Protocol(sprintf "the request body is not JSON (%s)" ex.Message))

        match parsed with
        | Error e -> Error e
        | Ok doc ->
            use doc = doc
            let root = doc.RootElement

            if root.ValueKind = JsonValueKind.Array then
                Error(
                    McpError.Protocol
                        "JSON-RPC batching was removed in MCP revision 2025-06-18; send one request per body"
                )
            elif root.ValueKind <> JsonValueKind.Object then
                Error(McpError.Protocol "a JSON-RPC request must be a JSON object")
            else
                let versionOk =
                    match root.TryGetProperty "jsonrpc" with
                    | true, v -> v.ValueKind = JsonValueKind.String && v.GetString() = "2.0"
                    | _ -> false

                if not versionOk then
                    Error(McpError.Protocol "every request must carry \"jsonrpc\": \"2.0\"")
                else
                    match root.TryGetProperty "method" with
                    | true, m when m.ValueKind = JsonValueKind.String ->
                        let rawId =
                            match root.TryGetProperty "id" with
                            | true, id when id.ValueKind <> JsonValueKind.Undefined ->
                                // `GetRawText` preserves the client's exact
                                // lexical form, which is what makes the echo
                                // faithful for a numeric id.
                                Some(id.GetRawText())
                            | _ -> None

                        let parameters =
                            match root.TryGetProperty "params" with
                            | true, p when p.ValueKind <> JsonValueKind.Undefined ->
                                // The document is disposed when this function
                                // returns, so the element is cloned out of it.
                                Some(p.Clone())
                            | _ -> None

                        Ok {
                            RawId = rawId
                            Method = m.GetString()
                            Params = parameters
                        }
                    | _ -> Error(McpError.Protocol "a JSON-RPC request must carry a string \"method\"")

/// The `name` and `arguments` of a `tools/call` request.
///
/// `arguments` is optional in the specification (a tool taking no
/// parameters may be called without it) and defaults to an empty object,
/// so a conforming client that omits it is not refused. Anything present
/// but not an object IS refused: silently coercing would hand the tool a
/// shape it did not declare.
let parseToolCall (parameters: JsonElement option) : Result<string * string, McpError> =
    match parameters with
    | None -> Error(McpError.InvalidParams "tools/call requires a params object carrying a tool name")
    | Some p when p.ValueKind <> JsonValueKind.Object ->
        Error(McpError.InvalidParams "tools/call params must be a JSON object")
    | Some p ->
        match p.TryGetProperty "name" with
        | true, n when
            n.ValueKind = JsonValueKind.String
            && not (String.IsNullOrWhiteSpace(n.GetString()))
            ->
            let name = n.GetString()

            match p.TryGetProperty "arguments" with
            | true, args when args.ValueKind = JsonValueKind.Object -> Ok(name, args.GetRawText())
            | true, args when args.ValueKind = JsonValueKind.Null -> Ok(name, "{}")
            | true, _ -> Error(McpError.InvalidParams "tools/call arguments must be a JSON object")
            | _ -> Ok(name, "{}")
        | _ -> Error(McpError.InvalidParams "tools/call params must carry a non-empty string \"name\"")

// ─── Responses ───────────────────────────────────────────────────────

/// Write a JSON-RPC envelope whose body is produced by `writeBody`,
/// echoing `rawId` verbatim.
let private envelope (rawId: string option) (writeBody: Utf8JsonWriter -> unit) : string =
    let buffer = new ArrayBufferWriter<byte>()
    use writer = new Utf8JsonWriter(buffer)
    writer.WriteStartObject()
    writer.WriteString("jsonrpc", "2.0")
    writer.WritePropertyName "id"

    match rawId with
    | Some raw -> writer.WriteRawValue(raw, skipInputValidation = false)
    | None -> writer.WriteNullValue()

    writeBody writer
    writer.WriteEndObject()
    writer.Flush()
    Encoding.UTF8.GetString(buffer.WrittenSpan)

/// A JSON-RPC error response.
///
/// The typed refusal rides the error object's `data` member wherever
/// there is one to carry — a budget denial's dimension / quota / spend /
/// period, a rate limit's retry window — so a client branches on data
/// rather than parsing the message. `ToolDenied`'s reason is deliberately
/// NOT carried: it names the module or grant the caller lacks, which is
/// the enumeration `ErrorCode.toolUnavailable` exists to prevent.
let errorResponse (rawId: string option) (error: McpError) : string =
    envelope rawId (fun writer ->
        writer.WritePropertyName "error"
        writer.WriteStartObject()
        writer.WriteNumber("code", errorCode error)
        writer.WriteString("message", errorMessage error)

        match error with
        | McpError.BudgetExhausted denial ->
            writer.WritePropertyName "data"
            writer.WriteStartObject()
            writer.WriteString("kind", "budgetExhausted")
            writer.WriteString("domain", denial.Domain)
            writer.WriteString("dimension", denial.Dimension)
            writer.WriteString("periodKey", denial.PeriodKey)
            writer.WriteNumber("quota", denial.Quota)
            writer.WriteNumber("spent", denial.Spent)
            writer.WriteNumber("requested", denial.Requested)
            writer.WriteEndObject()
        | McpError.RateLimited limited ->
            writer.WritePropertyName "data"
            writer.WriteStartObject()
            writer.WriteString("kind", "rateLimited")
            writer.WriteNumber("limit", limited.Limit)
            writer.WriteNumber("retryAfterSeconds", limited.RetryAfterSeconds)
            writer.WriteEndObject()
        | _ -> ()

        writer.WriteEndObject())

/// The `initialize` result: the protocol revision this host speaks, the
/// capabilities it advertises, and its name.
///
/// `tools.listChanged` is `false` and honest: the registry is populated
/// at compose and immutable afterwards, so there is no change to notify.
/// Advertising `true` and never sending the notification would leave a
/// client waiting for something that cannot happen.
let initializeResult (rawId: string option) (serverVersion: string) : string =
    envelope rawId (fun writer ->
        writer.WritePropertyName "result"
        writer.WriteStartObject()
        writer.WriteString("protocolVersion", McpHostConstants.ProtocolVersion)
        writer.WritePropertyName "capabilities"
        writer.WriteStartObject()
        writer.WritePropertyName "tools"
        writer.WriteStartObject()
        writer.WriteBoolean("listChanged", false)
        writer.WriteEndObject()
        writer.WriteEndObject()
        writer.WritePropertyName "serverInfo"
        writer.WriteStartObject()
        writer.WriteString("name", McpHostConstants.ServerName)
        writer.WriteString("version", serverVersion)
        writer.WriteEndObject()
        writer.WriteEndObject())

/// An empty result — the response to `ping`.
let emptyResult (rawId: string option) : string =
    envelope rawId (fun writer ->
        writer.WritePropertyName "result"
        writer.WriteStartObject()
        writer.WriteEndObject())

/// One tool as MCP renders it: the authored name, the description, and
/// the JSON-Schema input schema.
///
/// `inputSchema` arrives as a JSON string the AI tool registry already
/// built for the provider surface, so it is written RAW rather than
/// re-encoded — re-parsing and re-emitting a schema is a second place
/// for it to change shape.
///
/// The name is the AUTHORED `AIToolDefinition.Name`, not the
/// provider-sanitised one: MCP's own name grammar admits dots, the
/// sanitisation exists for one provider family's constraint, and a grant
/// an operator wrote against the authored name must match what the agent
/// sees.
type McpToolView = {
    Name: string
    Description: string
    InputSchemaJson: string
}

/// The `tools/list` result.
let toolsListResult (rawId: string option) (tools: McpToolView list) : string =
    envelope rawId (fun writer ->
        writer.WritePropertyName "result"
        writer.WriteStartObject()
        writer.WritePropertyName "tools"
        writer.WriteStartArray()

        for tool in tools do
            writer.WriteStartObject()
            writer.WriteString("name", tool.Name)
            writer.WriteString("description", tool.Description)
            writer.WritePropertyName "inputSchema"
            writer.WriteRawValue(tool.InputSchemaJson, skipInputValidation = false)
            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteEndObject())

/// The `tools/call` result: one text content block, plus the `isError`
/// flag.
///
/// **A tool that RAN and failed is a successful JSON-RPC response with
/// `isError: true`, not a JSON-RPC error.** That is the MCP convention
/// and it matters behaviourally: a model is supposed to SEE a tool's
/// failure and adapt, whereas a JSON-RPC error is a protocol fault the
/// client handles out of band. A tool that was never reachable is the
/// other thing, and gets a JSON-RPC error (`ErrorCode.toolUnavailable`).
let toolCallResult (rawId: string option) (text: string) (isError: bool) : string =
    envelope rawId (fun writer ->
        writer.WritePropertyName "result"
        writer.WriteStartObject()
        writer.WritePropertyName "content"
        writer.WriteStartArray()
        writer.WriteStartObject()
        writer.WriteString("type", "text")
        writer.WriteString("text", text)
        writer.WriteEndObject()
        writer.WriteEndArray()
        writer.WriteBoolean("isError", isError)
        writer.WriteEndObject())