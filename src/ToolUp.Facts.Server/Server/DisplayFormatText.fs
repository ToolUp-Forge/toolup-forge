// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open Fuaran.Facts

/// A metric's display format as the .NET format string the browse and
/// table contracts and the model-facing tool results carry: `N`/`F`
/// (grouped / ungrouped number), `P`, `C`, each with its fraction digits,
/// and `""` for verbatim.
module internal DisplayFormatText =

    let ofFormat (format: DisplayFormat) : string =
        let digits = format.FractionDigits |> Option.map string |> Option.defaultValue ""

        match format.Style with
        | DisplayStyle.Verbatim -> ""
        | DisplayStyle.Number -> (if format.Grouping then "N" else "F") + digits
        | DisplayStyle.Percent -> "P" + digits
        | DisplayStyle.Currency -> "C" + digits