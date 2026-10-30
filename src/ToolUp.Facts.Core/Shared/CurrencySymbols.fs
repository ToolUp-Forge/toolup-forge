// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts


// ─── Currency symbols ────────────────────────────────────────────────
//
// The money a fact carries renders in the currency its metric declares. The
// rendering is invariant-culture by design (a wire and prompt value, never
// localised), and invariant culture's currency symbol is the generic `¤`, so
// the declared ISO 4217 code is what names the symbol.
//
// **Data, not culture.** The table below is the whole mapping. It reads no
// ICU culture data, which a container running with invariant globalization
// does not have, so a rendering is the same on every host. It is pure F# over
// strings and a `Map`, so it compiles under Fable.
//
// **The symbols are the English (CLDR `en`) prefix forms**, chosen so a
// figure reads unambiguously in an English prose answer: the US dollar alone
// takes the bare `$`, every other dollar names its country (`A$`, `CA$`).
// A code the table does not hold renders with the code itself as its prefix
// (`CHF 1,299`), never `¤`.

/// ISO 4217 currency code → the symbol a figure in that currency is rendered
/// with. Consulted by `FactRendering.renderFor` when a metric declares a
/// `currency` dimensionality and a `C`-class display format.
[<RequireQualifiedAccess>]
module CurrencySymbols =

    /// The known codes and their symbols — the whole table.
    let table: Map<string, string> =
        Map.ofList [
            "AUD", "A$"
            "BRL", "R$"
            "CAD", "CA$"
            "CNY", "CN¥"
            "EUR", "€"
            "GBP", "£"
            "HKD", "HK$"
            "ILS", "₪"
            "INR", "₹"
            "JPY", "¥"
            "KRW", "₩"
            "MXN", "MX$"
            "NZD", "NZ$"
            "PHP", "₱"
            "TWD", "NT$"
            "USD", "$"
            "VND", "₫"
        ]

    /// Whether `code` has the shape of an ISO 4217 code: exactly three ASCII
    /// letters (either case). A symbol (`"£"`), a unit word (`"count"`) or a
    /// blank is not a code.
    let private isCode (code: string) : bool =
        not (isNull code)
        && code.Length = 3
        && code |> Seq.forall (fun c -> (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))

    /// The symbol the table holds for `code` (matched case-insensitively), or
    /// `None` for a code it does not hold.
    let tryFind (code: string) : string option =
        if isCode code then
            table |> Map.tryFind (code.ToUpperInvariant())
        else
            None

    /// The prefix a figure in currency `code` is rendered with: the table's
    /// symbol for a known code, the code itself and a space for any other
    /// ISO-shaped code (`"CHF "`), and `None` when `code` is not an ISO code
    /// at all — the caller then renders exactly as it would without a unit.
    let prefix (code: string) : string option =
        if isCode code then
            match tryFind code with
            | Some symbol -> Some symbol
            | None -> Some(code.ToUpperInvariant() + " ")
        else
            None