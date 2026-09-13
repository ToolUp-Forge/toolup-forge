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

// ─── Phase 508 — schema-validated argument decoding ──────────────
//
// The helpers above each answer one question about one property, which
// is why an executor taking a structured argument ends up walking the
// document by hand: pull a raw sub-object, check a discriminator string,
// branch, pull the next one. That hand-walk is the thing a declared
// schema makes unnecessary — the tool already says what it expects, so
// the executor should be able to hand the declaration back and get
// either its record or a refusal that names what was wrong.
//
// Two properties are deliberate:
//
//   * **The refusal is typed and carries a PATH**, not a sentence. A
//     model that sent `{"filter":{"unit":"furlongs"}}` against an enum
//     of two values needs to be told which member, what was allowed and
//     what it sent; a message saying the arguments did not match tells
//     it to guess. `ToolArgumentRefusal.toMessage` renders exactly that
//     sentence for the model, and `requireDecoded` raises it as the
//     `ToolArgumentError` the agent loop already classifies as
//     `InvalidArguments` rather than as a tool fault worth retrying.
//
//   * **Validation runs BEFORE deserialisation, and is the half that
//     reports.** `FableConverters` is a good deserialiser and a poor
//     validator — it reports a failure as an exception about a .NET type
//     the model has never heard of, and it accepts JSON the schema
//     forbids whenever the target type is more permissive than the
//     declaration (a string member against an enum is the common case).
//     Checking the declaration first means the enum, the required-member
//     set and the array element type are enforced by the thing that
//     declared them.
//
// No type erasure is introduced here: the decode is generic in `'T` and
// the deserialiser is called at that type, so nothing is boxed and the
// sanctioned erasure boundaries are untouched.

/// Why a tool's arguments did not match its declared schema.
type ToolArgumentRefusal = {
    /// Dotted path to the offending value, relative to the arguments
    /// object — e.g. "filter.unit" or "items[2].id". Empty for a fault
    /// in the arguments document itself.
    Path: string
    /// What the schema declared at that path, in the vocabulary the
    /// model was given (a type name, or the enum's values).
    Expected: string
    /// What arrived there, truncated. Empty when nothing did.
    Received: string
    /// One-sentence account, already model-readable.
    Message: string
}

[<RequireQualifiedAccess>]
module ToolArgumentRefusal =

    /// Render a refusal as the sentence the model should read. Names the
    /// path, what was expected and what arrived, so the repair is a
    /// mechanical edit rather than a guess.
    let toMessage (refusal: ToolArgumentRefusal) : string =
        let where =
            if System.String.IsNullOrEmpty refusal.Path then
                "the arguments"
            else
                $"argument '{refusal.Path}'"

        if System.String.IsNullOrEmpty refusal.Received then
            $"{refusal.Message} ({where} — expected {refusal.Expected})."
        else
            $"{refusal.Message} ({where} — expected {refusal.Expected}, received {refusal.Received})."

/// Render a `ToolSchemaNode` as the short expectation phrase a refusal
/// quotes. Deliberately the model's vocabulary (JSON Schema), not F#'s.
let private expectationOf (schema: ToolSchemaNode) : string =
    match schema with
    | SchemaString [] -> "a string"
    | SchemaString values -> "one of: " + String.concat ", " values
    | SchemaNumber -> "a number"
    | SchemaInteger -> "an integer"
    | SchemaBoolean -> "a boolean"
    | SchemaArray _ -> "an array"
    | SchemaObject _ -> "an object"

let private describe (value: JsonElement) : string =
    let raw = value.GetRawText()

    if raw.Length > 80 then raw.Substring(0, 80) + "…" else raw

let private refuse (path: string) (schema: ToolSchemaNode) (value: JsonElement) (message: string) = {
    Path = path
    Expected = expectationOf schema
    Received = describe value
    Message = message
}

let private joinPath (parent: string) (child: string) =
    if System.String.IsNullOrEmpty parent then
        child
    else
        parent + "." + child

/// Check a JSON value against a declared `ToolSchemaNode`, reporting the
/// FIRST violation with its path. First rather than all: the model
/// repairs one call at a time, and an executor's error string is read as
/// an instruction, so a list of faults is a worse instruction than the
/// one that must be fixed first.
let rec private validateAt (path: string) (schema: ToolSchemaNode) (value: JsonElement) : ToolArgumentRefusal option =
    match schema with
    | SchemaString allowed ->
        if value.ValueKind <> JsonValueKind.String then
            Some(refuse path schema value "value is not a string")
        elif List.isEmpty allowed then
            None
        elif allowed |> List.contains (value.GetString()) then
            None
        else
            Some(refuse path schema value "value is not one of the allowed values")
    | SchemaNumber ->
        if value.ValueKind = JsonValueKind.Number then
            None
        else
            Some(refuse path schema value "value is not a number")
    | SchemaInteger ->
        if value.ValueKind <> JsonValueKind.Number then
            Some(refuse path schema value "value is not a number")
        else
            match value.TryGetInt64() with
            | true, _ -> None
            | false, _ -> Some(refuse path schema value "value is not an integer")
    | SchemaBoolean ->
        if value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False then
            None
        else
            Some(refuse path schema value "value is not a boolean")
    | SchemaArray items ->
        if value.ValueKind <> JsonValueKind.Array then
            Some(refuse path schema value "value is not an array")
        else
            value.EnumerateArray()
            |> Seq.indexed
            |> Seq.tryPick (fun (i, element) -> validateAt $"{path}[{i}]" items element)
    | SchemaObject members ->
        if value.ValueKind <> JsonValueKind.Object then
            Some(refuse path schema value "value is not an object")
        else
            members
            |> List.tryPick (fun m ->
                let childPath = joinPath path m.MemberName

                // A JSON null on an optional member reads as absent —
                // the shape `optionalTypedArg` already established, and
                // the shape models actually emit for "no value".
                let present =
                    match value.TryGetProperty m.MemberName with
                    | true, child when child.ValueKind <> JsonValueKind.Null -> Some child
                    | _ -> None

                match present with
                | Some child -> validateAt childPath m.MemberSchema child
                | None when m.MemberRequired ->
                    Some {
                        Path = childPath
                        Expected = expectationOf m.MemberSchema
                        Received = ""
                        Message = "required member is missing"
                    }
                | None -> None)

/// Check a JSON value against a declared schema. `Ok ()` when it
/// conforms; `Error refusal` naming the first violation otherwise.
let validateSchema (schema: ToolSchemaNode) (value: JsonElement) : Result<unit, ToolArgumentRefusal> =
    match validateAt "" schema value with
    | None -> Ok()
    | Some refusal -> Error refusal

/// Validate one argument against its declared schema and deserialise it
/// into `'T` via `FableConverters`, so an executor stops hand-walking
/// the document. Returns a typed refusal rather than raising, so a tool
/// that would rather answer the model in its own domain shape can.
///
/// `schema` is the same value the declaration passed to
/// `ToolSchema.parameter`; declare it once, use it at both ends.
let decodeArg<'T> (args: JsonElement) (name: string) (schema: ToolSchemaNode) : Result<'T, ToolArgumentRefusal> =
    let missing = {
        Path = name
        Expected = expectationOf schema
        Received = ""
        Message = "required argument is missing"
    }

    match args.TryGetProperty name with
    | false, _ -> Error missing
    | true, value when value.ValueKind = JsonValueKind.Null -> Error missing
    | true, value ->
        match validateAt name schema value with
        | Some refusal -> Error refusal
        | None ->
            let raw = value.GetRawText()

            try
                Ok(JsonSerializer.Deserialize<'T>(raw, fableJsonOptions))
            with _ ->
                // The schema matched, so this is a mismatch between the
                // declaration and the F# type the executor asked for —
                // a tool defect, not an argument defect. Say so: telling
                // the model to repair arguments that already conform
                // sends it round a loop it cannot exit.
                Error {
                    Path = name
                    Expected = expectationOf schema
                    Received = describe value
                    Message =
                        $"arguments matched the declared schema but could not be read as {typeof<'T>.Name} — the tool's declaration and its executor disagree"
                }

/// Optional variant of `decodeArg`. `Ok None` when the argument is
/// absent or JSON-null; a refusal when it is present and does not match.
let decodeOptionalArg<'T>
    (args: JsonElement)
    (name: string)
    (schema: ToolSchemaNode)
    : Result<'T option, ToolArgumentRefusal> =
    match args.TryGetProperty name with
    | false, _ -> Ok None
    | true, value when value.ValueKind = JsonValueKind.Null -> Ok None
    | true, _ -> decodeArg<'T> args name schema |> Result.map Some

/// `decodeArg`, raising `ToolArgumentError` on a refusal. The shape an
/// executor uses when it wants the agent loop's existing
/// `InvalidArguments` classification and nothing more — the same
/// contract `requireTypedArg` has, with the schema doing the checking.
let requireDecoded<'T> (args: JsonElement) (name: string) (schema: ToolSchemaNode) : 'T =
    match decodeArg<'T> args name schema with
    | Ok value -> value
    | Error refusal -> raise (ToolArgumentError(ToolArgumentRefusal.toMessage refusal))