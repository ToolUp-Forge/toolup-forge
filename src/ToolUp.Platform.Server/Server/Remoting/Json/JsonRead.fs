// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Json

open System.Text.Json
open ToolUp.Remoting

/// Phase 799 — the one `JsonElement`-to-`JsonValue` pass: the JSON twin
/// of `MsgPack.Read.Reader.TryReadValue`, and the only place a
/// System.Text.Json type meets the value model.
///
/// It reads a document System.Text.Json has ALREADY parsed — the
/// dispatcher's outer-array parse (Phase 69m) hands each argument over
/// as a cloned `JsonElement` — so well-formedness is STJ's job and this
/// pass never sees malformed text; what it adds is the bounds discipline
/// the MsgPack pass has (`Format.fs`'s header), stated for this wire:
///
///   1. **Depth.** Containers nest no deeper than `maxDepth`. STJ bounds
///      the PARSE at 64 by default, so a deeper document never reaches
///      here; the check is kept because the bound is this pass's own
///      contract, not a property borrowed from whoever parsed.
///   2. **Width.** No container carries more than `maxMembers` elements
///      or members. A JSON array is unbounded by its grammar, and a list
///      argument of a million entries is a value the decoder would then
///      walk; the bound makes that a named refusal at the point the
///      value is built rather than an allocation the handler pays for.
///
/// **Numbers never pass through `float`.** A `Number` token's text is
/// taken with `GetRawText()` and carried verbatim, which is the whole
/// reason `JsonValue.Number` is lexical. Everything else is a direct
/// case-for-case copy.
[<RequireQualifiedAccess>]
module JsonRead =

    /// How deeply containers may nest. Matches the MsgPack reader's
    /// `Format.DefaultMaxDepth` and STJ's own `JsonDocumentOptions`
    /// default, so the three bounds agree.
    [<Literal>]
    let DefaultMaxDepth = 64

    /// How many elements or members one container may carry.
    [<Literal>]
    let DefaultMaxMembers = 1_000_000

    let private refuse (expected: string) (found: string) : Result<'T, DecodeError> =
        Error(DecodeError.create expected found)

    /// Build the value model from a parsed element, under explicit bounds.
    let tryReadWith (maxDepth: int) (maxMembers: int) (element: JsonElement) : Result<JsonValue, DecodeError> =
        let rec go (depth: int) (element: JsonElement) : Result<JsonValue, DecodeError> =
            match element.ValueKind with
            | JsonValueKind.Null
            | JsonValueKind.Undefined -> Ok JsonValue.Null
            | JsonValueKind.True -> Ok(JsonValue.Bool true)
            | JsonValueKind.False -> Ok(JsonValue.Bool false)
            | JsonValueKind.String -> Ok(JsonValue.String(element.GetString()))
            | JsonValueKind.Number -> Ok(JsonValue.Number(element.GetRawText()))
            | JsonValueKind.Array ->
                if depth >= maxDepth then
                    refuse (sprintf "nesting at most %d container(s) deep" maxDepth) "an array nested deeper"
                else
                    let count = element.GetArrayLength()

                    if count > maxMembers then
                        refuse
                            (sprintf "an array of at most %d element(s)" maxMembers)
                            (sprintf "array of %d element(s)" count)
                    else
                        let mutable acc = []
                        let mutable failure = None
                        let mutable position = 0

                        for item in element.EnumerateArray() do
                            if failure.IsNone then
                                match go (depth + 1) item with
                                | Ok value -> acc <- value :: acc
                                | Error error -> failure <- Some(DecodeError.under (sprintf "[%d]" position) error)

                            position <- position + 1

                        match failure with
                        | Some error -> Error error
                        | None -> Ok(JsonValue.Array(List.rev acc))
            | JsonValueKind.Object ->
                if depth >= maxDepth then
                    refuse (sprintf "nesting at most %d container(s) deep" maxDepth) "an object nested deeper"
                else
                    let mutable acc = []
                    let mutable failure = None
                    let mutable count = 0

                    for property in element.EnumerateObject() do
                        if failure.IsNone then
                            count <- count + 1

                            if count > maxMembers then
                                failure <-
                                    Some(
                                        DecodeError.create
                                            (sprintf "an object of at most %d member(s)" maxMembers)
                                            "an object with more"
                                    )
                            else
                                match go (depth + 1) property.Value with
                                | Ok value -> acc <- (property.Name, value) :: acc
                                | Error error -> failure <- Some(DecodeError.under property.Name error)

                    match failure with
                    | Some error -> Error error
                    | None -> Ok(JsonValue.Object(List.rev acc))
            | other -> refuse "a JSON value" (sprintf "a %A token" other)

        go 0 element

    /// Build the value model under the default bounds.
    let tryRead (element: JsonElement) : Result<JsonValue, DecodeError> =
        tryReadWith DefaultMaxDepth DefaultMaxMembers element

    /// Parse text and build the value model. The parse is STJ's, with its
    /// own depth bound set to `DefaultMaxDepth`; a document that is not
    /// JSON is a refusal naming what the parser saw, never an exception.
    let tryParse (text: string) : Result<JsonValue, DecodeError> =
        try
            use document =
                JsonDocument.Parse(text, JsonDocumentOptions(MaxDepth = DefaultMaxDepth))

            tryRead document.RootElement
        with ex ->
            refuse "a JSON document" ex.Message