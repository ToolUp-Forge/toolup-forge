module ToolUp.AI.ToolHelpers

open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Remoting.Json.SystemTextJson

// ─── AI tool helpers ─────────────────────────────────────────────
//
// Shared utilities for module-side `aiTools` executors. Each module's
// `Server.fs` exports an `aiTools` value of type
// `(AIToolDefinition * (HttpContext -> string -> Async<string>)) list`;
// the executor receives the JSON args string from the agent loop, parses
// it, calls the module's pure routines, and returns a serialised JSON
// result. These helpers handle the recurring boilerplate (argument
// extraction with clear error messages; F#-DU-aware serialisation via
// `ToolUp.Remoting.Json`) so the module's `aiTools` definition stays
// focused on the actual tool logic.
//
// **Two JSON entry points deliberately:**
//   - Raw `JsonElement` walks for the incoming args — fast and
//     allocation-light for primitive property access.
//   - `JsonSerializer` configured with
//     `ToolUp.Remoting.Json.SystemTextJson.FableConverters` for any
//     (de)serialisation that touches F# discriminated unions, option
//     types, or records containing them. The bare `System.Text.Json`
//     stack has no DU converter and silently produces "F# discriminated
//     union serialization is not supported" runtime failures the moment
//     a tool returns or consumes a DU-bearing record.

/// `ToolExecutor` type alias — the executor signature for an AI tool.
/// Receives the per-request `HttpContext` (so handlers can resolve
/// `IBlobStorage`, `IDataObjectStore`, etc. from DI and call into
/// `getFileContents` for the caller's storage scope) and the raw JSON
/// args string from the agent loop, and returns a serialised JSON
/// result that the agent appends to the conversation.
type ToolExecutor = HttpContext -> string -> Async<string>

/// Raised by argument-validating helpers when a tool received arguments
/// that do not match the parameter schema (missing required value, wrong
/// JSON type, or DU/record shape that fails to deserialise into the
/// expected F# type). Caught by the agent loop and classified as
/// `InvalidArguments` rather than `ToolThrew`, so the model's tool-
/// result message tells it to repair its arguments instead of treating
/// the failure as a transient tool fault worth retrying with the same
/// call.
exception ToolArgumentError of message: string

let private fableJsonOptions = FableConverters.create ()

/// Serialise a value to JSON using the FableConverters set so F#
/// discriminated unions, options, and records round-trip cleanly.
/// Use for every executor return value that contains module domain
/// types — vanilla `JsonSerializer` (without these converters) would
/// silently drop DU cases.
let fableSerialize (value: obj) : string =
    JsonSerializer.Serialize(value, fableJsonOptions)

/// Deserialise a JSON string into the given F# type using the
/// FableConverters set. Use for executor arguments that the AI agent
/// passes back from a prior tool call (the prior call's result was
/// serialised via `fableSerialize`, so deserialising on the way in
/// must use the same options).
let fableDeserialize<'T> (json: string) : 'T =
    JsonSerializer.Deserialize<'T>(json, fableJsonOptions)

/// Require a string argument. Throws with a clear message if absent or
/// wrong type. `hint` is appended to the error — use it to point the
/// caller at the next step ("call X.load_data first").
let requireString (args: JsonElement) (name: string) (hint: string) : string =
    match args.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
    | true, v -> failwith $"Argument '{name}' must be a string, got {v.ValueKind}. {hint}"
    | false, _ -> failwith $"Required argument '{name}' is missing. {hint}"

/// Require a decimal-valued argument. Throws with a clear message if
/// absent or wrong type.
let requireDecimal (args: JsonElement) (name: string) (hint: string) : decimal =
    match args.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.Number -> v.GetDecimal()
    | true, v -> failwith $"Argument '{name}' must be a number, got {v.ValueKind}. {hint}"
    | false, _ -> failwith $"Required argument '{name}' is missing. {hint}"

/// Require a numeric argument and return as `float`. Same contract as
/// `requireDecimal` but for callers that work in floating point.
let requireNumber (args: JsonElement) (name: string) (hint: string) : float =
    match args.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.Number -> v.GetDouble()
    | true, v -> failwith $"Argument '{name}' must be a number, got {v.ValueKind}. {hint}"
    | false, _ -> failwith $"Required argument '{name}' is missing. {hint}"

/// Require an argument of complex shape (array / object) and return
/// its raw JSON text for downstream type-specific deserialisation.
/// `expectedShape` is a short description used in the error message
/// when the caller gets the type wrong (e.g. "array of Foo").
let requireRawJson (args: JsonElement) (name: string) (expectedShape: string) (hint: string) : string =
    match args.TryGetProperty(name) with
    | true, v -> v.GetRawText()
    | false, _ -> failwith $"Required argument '{name}' is missing (expected {expectedShape}). {hint}"

/// Optional string argument. Returns `None` when the property is absent
/// or not a JSON string; returns `Some s` with the raw string value
/// otherwise.
let optionalString (args: JsonElement) (name: string) : string option =
    match args.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

/// Optional decimal argument with a default. Matches the
/// `ToolParameterSchema.Default` convention — if the tool's schema
/// declares `Default = Some "0"`, the executor calls
/// `optionalDecimal args "name" 0m` to honour it.
let optionalDecimal (args: JsonElement) (name: string) (defaultValue: decimal) : decimal =
    match args.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.Number -> v.GetDecimal()
    | _ -> defaultValue

/// Optional boolean argument with a default.
let optionalBool (args: JsonElement) (name: string) (defaultValue: bool) : bool =
    match args.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.True -> true
    | true, v when v.ValueKind = JsonValueKind.False -> false
    | _ -> defaultValue

/// Read a complex-shape argument (DU, record, array) and deserialise it
/// into the F# type `'T` using `FableConverters`. On failure, raises
/// `ToolArgumentError` carrying a message that names the parameter, the
/// expected shape, and the value the caller actually sent — so the
/// agent loop's tool-result message tells the model exactly what to
/// change, instead of leaking a raw STJ exception classified
/// (incorrectly) as a tool fault worth retrying with the same args.
///
/// `typeHint` is a short cleartext description of the F# type, ideally
/// enumerating valid cases for DU types
/// (e.g. "TimePeriod (one of: Last1Week, …, All)"). It is written to
/// the error the model reads — write it for the model.
///
/// `hint` is appended to point at the next step.
let requireTypedArg<'T> (args: JsonElement) (name: string) (typeHint: string) (hint: string) : 'T =
    match args.TryGetProperty(name) with
    | false, _ -> raise (ToolArgumentError $"Required argument '{name}' is missing (expected {typeHint}). {hint}")
    | true, v when v.ValueKind = JsonValueKind.Null ->
        raise (ToolArgumentError $"Required argument '{name}' was null (expected {typeHint}). {hint}")
    | true, v ->
        let raw = v.GetRawText()

        try
            JsonSerializer.Deserialize<'T>(raw, fableJsonOptions)
        with _ ->
            let truncated =
                if raw.Length > 200 then
                    raw.Substring(0, 200) + "…"
                else
                    raw

            raise (
                ToolArgumentError
                    $"Argument '{name}' did not match the expected shape ({typeHint}). Received: {truncated}. {hint}"
            )

/// Optional variant of `requireTypedArg`. Returns `None` when the
/// property is absent or JSON-null; raises `ToolArgumentError` if
/// present but mistyped — the agent loop classifies it as
/// `InvalidArguments` (it's a schema violation either way, not a tool
/// fault).
let optionalTypedArg<'T> (args: JsonElement) (name: string) (typeHint: string) : 'T option =
    match args.TryGetProperty(name) with
    | false, _ -> None
    | true, v when v.ValueKind = JsonValueKind.Null -> None
    | true, v ->
        let raw = v.GetRawText()

        try
            Some(JsonSerializer.Deserialize<'T>(raw, fableJsonOptions))
        with _ ->
            let truncated =
                if raw.Length > 200 then
                    raw.Substring(0, 200) + "…"
                else
                    raw

            raise (
                ToolArgumentError
                    $"Optional argument '{name}' did not match the expected shape ({typeHint}). Received: {truncated}."
            )

/// Pair each metadata entry with its executor, producing the
/// `(AIToolDefinition * ToolExecutor) list` shape that
/// `ServerModule.withAITools` consumes. Modules expose this directly
/// from their `Server.fs` as `let aiTools provider = wireTools defs executors`.
/// Missing executors fail loudly with a clear message naming the orphaned
/// tool — surfaces metadata/executor drift at startup, not in a stuck
/// agent turn.
let wireTools
    (definitions: AIToolDefinition list)
    (executors: Map<string, ToolExecutor>)
    : (AIToolDefinition * ToolExecutor) list =
    definitions
    |> List.map (fun def ->
        match executors |> Map.tryFind def.Name with
        | Some exec -> def, exec
        | None ->
            failwith
                $"No executor registered for tool '{def.Name}' — add it to the module's `executors` map in Server.fs.")