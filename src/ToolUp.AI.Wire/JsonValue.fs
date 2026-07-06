// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI.Wire

/// A portable JSON value model. Depends on FSharp.Core only — no
/// `System.Net.Http`, no `System.Text.Json` — so it compiles unchanged to
/// both a .NET server host and a Fable browser host. It is the neutral
/// substrate a provider wire-mapping translates to/from, letting one
/// mapping serve both hosts (Wave 32).
///
/// **Object members preserve insertion order.** `JObject` carries an
/// ordered `(string * JsonValue) list`, not a `Map`, so serialization is
/// byte-stable: members emit in the order they were built, identically on
/// both hosts. That byte-stable key order is the load-bearing property the
/// later parity gate depends on.
///
/// Mirrors the hand-rolled `J` JSON DU idiom proven elsewhere in the codebase.
type JsonValue =
    | JNull
    | JBool of bool
    | JNumber of float
    | JString of string
    | JArray of JsonValue list
    | JObject of (string * JsonValue) list

/// Ergonomic builders. Auto-opened so a `open ToolUp.AI.Wire` lets a
/// wire-mapping write `jobj [ "model", jstr model; "stream", jbool true ]`
/// without qualification.
[<AutoOpen>]
module JsonBuilders =

    /// The JSON `null` literal.
    let jnull: JsonValue = JNull

    /// A JSON boolean.
    let jbool (b: bool) : JsonValue = JBool b

    /// A JSON number from a float. JSON has a single numeric type; integral
    /// values serialize without a decimal point (see `JsonHost.serialize`).
    let jnum (n: float) : JsonValue = JNumber n

    /// A JSON number from an int — the common case for token counts,
    /// indices, and small literals in a wire body.
    let jint (n: int) : JsonValue = JNumber(float n)

    /// A JSON string.
    let jstr (s: string) : JsonValue = JString s

    /// A JSON array.
    let jarr (items: JsonValue list) : JsonValue = JArray items

    /// A JSON object. Members serialize in the order given.
    let jobj (members: (string * JsonValue) list) : JsonValue = JObject members

/// Total accessors — every one returns `option` and never throws, so a
/// wire-mapping can navigate a parsed response without defensive guards or
/// try/with. The module shares the type's name (the FSharp.Core
/// `Option`/`List` companion-module convention).
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module JsonValue =

    /// The value of object member `name`, or `None` when the value is not an
    /// object or has no such member.
    let tryField (name: string) (v: JsonValue) : JsonValue option =
        match v with
        | JObject members ->
            members
            |> List.tryPick (fun (k, value) -> if k = name then Some value else None)
        | _ -> None

    /// The string payload, or `None` when the value is not a string.
    let asString (v: JsonValue) : string option =
        match v with
        | JString s -> Some s
        | _ -> None

    /// The boolean payload, or `None` when the value is not a boolean.
    let asBool (v: JsonValue) : bool option =
        match v with
        | JBool b -> Some b
        | _ -> None

    /// The numeric payload as a float, or `None` when the value is not a
    /// number.
    let asFloat (v: JsonValue) : float option =
        match v with
        | JNumber f -> Some f
        | _ -> None

    /// The numeric payload as an int, or `None` when the value is not a
    /// number, is non-integral, or falls outside `Int32` range. (The range
    /// guard also rejects NaN / ±∞, since they fail the bound comparison.)
    let asInt (v: JsonValue) : int option =
        match v with
        | JNumber f when f = floor f && f >= -2147483648.0 && f <= 2147483647.0 -> Some(int f)
        | _ -> None

    /// The array items, or `None` when the value is not an array.
    let asArray (v: JsonValue) : JsonValue list option =
        match v with
        | JArray items -> Some items
        | _ -> None

    /// The object members (ordered), or `None` when the value is not an
    /// object.
    let asObject (v: JsonValue) : (string * JsonValue) list option =
        match v with
        | JObject members -> Some members
        | _ -> None

    /// The array element at `index`, or `None` when the value is not an
    /// array or the index is out of range.
    let tryItem (index: int) (v: JsonValue) : JsonValue option =
        match v with
        | JArray items when index >= 0 && index < List.length items -> Some(List.item index items)
        | _ -> None