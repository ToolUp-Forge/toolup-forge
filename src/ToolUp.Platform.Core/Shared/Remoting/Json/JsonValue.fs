// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Json

open System
open System.Globalization

// ─── Phase 799 — the closed JSON value model ─────────────────────────
//
// The JSON twin of `ToolUp.Remoting.MsgPack.Value` (Phase 785), and the
// carrier the decoder algebra extends to the JSON wire over. The same
// three properties are load-bearing, for the same reasons:
//
//   * **Closed.** Six cases, no extension point. A decoder's match is
//     exhaustive by construction.
//   * **No null.** The absent value is `Null`, a case. A decoder never
//     needs a null guard.
//   * **Well-founded.** `JsonValue.size` is strictly smaller for every
//     subterm than for its container, so structural recursion terminates
//     by the model's own measure.
//
// **The numeric case carries the token's LEXICAL FORM, and that is the
// whole reason this type exists** rather than the decision going the
// other way. Phase 785.F assessed `ToolUp.AI.Wire.JsonValue` as the
// carrier — right shape, FSharp.Core-only, both hosts, already referenced
// — and disqualified it on one field: `JNumber of float`. A single IEEE
// double cannot hold what the algebra's width discipline exists to
// preserve: `int64` past 2^53 rounds silently, `decimal` becomes
// approximate, and the token's source is gone, so `asInt32` could not
// tell "was an integer" from "happens to be integral". Phase 784 pinned
// the same class of loss live on this wire (the STJ path loses a
// `TimeSpan` tick). So `Number` carries the digits as written — a JSON
// number token is a finite string of ASCII, and keeping it costs a
// string allocation the parse would have made anyway — and every numeric
// combinator reads the width it needs FROM THE TEXT, exactly.
//
// A sibling model rather than a widened `ToolUp.AI.Wire.JsonValue`: that
// type is matched exhaustively in forty-five files across the estate's
// provider mappings, and a seventh case is a break in every one of them
// (the record-field / DU-case widening class the surface guard names).
// The two coexist in different namespaces with different jobs — that one
// is a provider wire-mapping's substrate, this one is a decoder's.
//
// **Object members preserve insertion order**, as the AI.Wire model's
// do: an `(string * JsonValue) list`, never a `Map`, so a value
// round-trips to the text it came from and a duplicate key is VISIBLE
// (the decoder decides; the model does not silently keep one).

/// Phase 799 — one JSON value, as the wire can carry it.
///
/// `[<RequireQualifiedAccess>]` deliberately: `String`, `Array` and
/// `Bool` would otherwise shadow FSharp.Core names in every file that
/// opens this namespace.
[<RequireQualifiedAccess>]
type JsonValue =
    /// The JSON `null` literal. A CASE, never `null`.
    | Null
    /// A JSON boolean.
    | Bool of bool
    /// A JSON number, as WRITTEN: the token text, validated against the
    /// JSON number grammar by whoever produced this value and never
    /// parsed to a `float` on the way in. `JsonValue.tryInt64` and its
    /// siblings read the width they need from it.
    | Number of lexical: string
    /// A JSON string, unescaped.
    | String of string
    /// A JSON array's elements, in wire order.
    | Array of JsonValue list
    /// An object's members in wire order. Never a `Map`: order and
    /// duplicates are facts about the wire a decoder may need.
    | Object of members: (string * JsonValue) list

/// The measure, the description and the lexical-number readers over
/// `JsonValue`.
[<RequireQualifiedAccess>]
module JsonValue =

    /// The structural size: 1 for a leaf, 1 + the sizes of its children
    /// for a container. The well-founded measure — see the header, and
    /// `MsgPack.Value.size` for why it is shipped rather than implied.
    let rec size (value: JsonValue) : int =
        match value with
        | JsonValue.Null
        | JsonValue.Bool _
        | JsonValue.Number _
        | JsonValue.String _ -> 1
        | JsonValue.Array items -> items |> List.fold (fun total item -> total + size item) 1
        | JsonValue.Object members -> members |> List.fold (fun total (_, member') -> total + size member') 1

    /// What a refusal's `Found` field says about this value: the shape,
    /// and for a number its text — never a deep rendering of a container.
    let describe (value: JsonValue) : string =
        match value with
        | JsonValue.Null -> "null"
        | JsonValue.Bool true -> "bool true"
        | JsonValue.Bool false -> "bool false"
        | JsonValue.Number text -> sprintf "number %s" text
        | JsonValue.String text -> sprintf "string of %d character(s)" text.Length
        | JsonValue.Array items -> sprintf "array of %d element(s)" (List.length items)
        | JsonValue.Object members -> sprintf "object of %d member(s)" (List.length members)

    /// The FIRST member named `name`, or `None`. First rather than last
    /// because that is what a reader sees first; a decoder that must
    /// refuse a duplicate reads `members` itself.
    let tryMember (name: string) (value: JsonValue) : JsonValue option =
        match value with
        | JsonValue.Object members -> members |> List.tryPick (fun (k, v) -> if k = name then Some v else None)
        | _ -> None

    // ─── The number grammar, and the widths read from it ─────────────
    //
    // RFC 8259 §6: `-? int frac? exp?` with `int = 0 | [1-9][0-9]*`,
    // `frac = . [0-9]+`, `exp = [eE] [+-]? [0-9]+`. Checked by hand
    // rather than by a regex so both hosts run the same code and the
    // check allocates nothing.

    let private isDigit (c: char) = c >= '0' && c <= '9'

    /// Whether `text` is a JSON number token. The producer of a `Number`
    /// is expected to have checked this; the combinators re-check
    /// cheaply rather than trust it, because a `Number` built by hand
    /// in a test is a legitimate value.
    let isNumberToken (text: string) : bool =
        let n = text.Length

        // The index after a run of digits starting at `i` (possibly `i`).
        let digits (i: int) =
            let mutable j = i

            while j < n && isDigit text.[j] do
                j <- j + 1

            j

        let i = if n > 0 && text.[0] = '-' then 1 else 0

        let afterInt =
            if i >= n then -1
            elif text.[i] = '0' then i + 1
            elif isDigit text.[i] then digits i
            else -1

        if afterInt < 0 then
            false
        else
            let afterFrac =
                if afterInt < n && text.[afterInt] = '.' then
                    let j = digits (afterInt + 1)
                    if j > afterInt + 1 then j else -1
                else
                    afterInt

            if afterFrac < 0 then
                false
            else
                let afterExp =
                    if afterFrac < n && (text.[afterFrac] = 'e' || text.[afterFrac] = 'E') then
                        let signed =
                            afterFrac + 1 < n && (text.[afterFrac + 1] = '+' || text.[afterFrac + 1] = '-')

                        let start = if signed then afterFrac + 2 else afterFrac + 1
                        let j = digits start
                        if j > start then j else -1
                    else
                        afterFrac

                afterExp = n

    /// Whether the token is written as an INTEGER: no fraction, no
    /// exponent. `1.0` and `1e0` are integral in value and not in form,
    /// and the integer combinators refuse them: the writer never emits
    /// an integer that way, so a token that arrives so did not come from
    /// an integer.
    let isIntegralToken (text: string) : bool =
        isNumberToken text
        && not (text.Contains "." || text.Contains "e" || text.Contains "E")

    /// The token as an `int64`, or `None` when it is not an integral
    /// token or does not fit. Exact: no `float` on the way.
    let tryInt64 (text: string) : int64 option =
        if isIntegralToken text then
#if FABLE_COMPILER
            match Int64.TryParse text with
            | true, n -> Some n
            | _ -> None
#else
            match Int64.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
            | true, n -> Some n
            | _ -> None
#endif
        else
            None

    /// The token as a `uint64`, or `None`. A leading `-` is refused
    /// outright rather than parsed and range-checked.
    let tryUInt64 (text: string) : uint64 option =
        if isIntegralToken text && not (text.StartsWith "-") then
#if FABLE_COMPILER
            match UInt64.TryParse text with
            | true, n -> Some n
            | _ -> None
#else
            match UInt64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, n -> Some n
            | _ -> None
#endif
        else
            None

    /// The token as a `decimal`, exactly — the 28 significant digits
    /// `decimal` carries, read from the digits as written. `None` when
    /// the token is not a number or exceeds `decimal`'s range or scale.
    let tryDecimal (text: string) : decimal option =
        if isNumberToken text then
#if FABLE_COMPILER
            match Decimal.TryParse text with
            | true, d -> Some d
            | _ -> None
#else
            match Decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, d -> Some d
            | _ -> None
#endif
        else
            None

    /// The token as a `float`. The one convenience that IS a `float`,
    /// for a target that is one; correctly rounded, which is what
    /// "declared float" means on a text wire.
    let tryFloat (text: string) : float option =
        if isNumberToken text then
#if FABLE_COMPILER
            match Double.TryParse text with
            | true, f -> Some f
            | _ -> None
#else
            match Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, f -> Some f
            | _ -> None
#endif
        else
            None