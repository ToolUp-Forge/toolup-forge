// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Wire.Conformance.BlockPartFixtures

open ToolUp.Platform.AI

// ─── The block-part fixture family (Phase 1128) ──────────────────────
//
// One declaration of the block-part values every host asserts against:
//
//   • this pack, on .NET and under Fable — the audit summary, the export
//     rendering and every provider's request body for a message holding a
//     block (byte-identical on both hosts, and never carrying the node);
//   • `ToolUp.Platform.Tests` — the server's conversation-blob writer
//     (System.Text.Json with the remoting converters) writes `partsJson`
//     for `parts`, and reads it back to `parts`;
//   • `ToolUp.AI.Client.Tests`, transpiled — the browser's decoder reads
//     `partsJson` to `parts`.
//
// Neither of the last two hosts can run the other's codec, so the pinned
// literal is the meeting point: the .NET pack holds the writer to it and
// the Fable pack holds the reader to it, which together are "the client
// decodes what the server wrote". This file names only `ToolUp.AI.Wire`
// types so every one of those hosts compiles it unchanged.

/// The token that marks the node payload. A request body, a summary or
/// an export that contains it carried the node.
[<Literal>]
let NodeMarker = "node-payload-7f3c"

/// A Fuaran-UI node as wire JSON. This tier never reads it: the shape is
/// illustrative, and the marker is what the absence checks look for.
let tableNode =
    """{"kind":"table","title":"node-payload-7f3c","rows":[["north",120],["south",80]]}"""

/// A chart node, carrying the same marker.
let trendNode =
    """{"kind":"lineChart","title":"node-payload-7f3c","series":[{"name":"north","points":[[1,40],[2,80]]}]}"""

/// A table block's provenance: three facts cited, two points withheld.
let tableProvenance: BlockProvenance = {
    Kind = "table"
    Request = """{"kind":"table","tableId":"daily-trade","metric":"revenue","period":{"day":"2026-10-26"}}"""
    FactIds = [ "fact-0001"; "fact-0002"; "fact-0003" ]
    Withheld = [
        {
            PolicyRef = "trade-figures"
            Count = 2
        }
    ]
    TableId = "daily-trade"
    Metric = "revenue"
    Period = "2026-10-26"
}

/// A trend block's provenance: nothing withheld.
let trendProvenance: BlockProvenance = {
    Kind = "trend"
    Request =
        """{"kind":"trend","tableId":"daily-trade","metric":"revenue","period":{"day":"2026-10-26","rangeDays":28}}"""
    FactIds = [ "fact-0101"; "fact-0102" ]
    Withheld = []
    TableId = "daily-trade"
    Metric = "revenue"
    Period = "2026-09-29..2026-10-26"
}

let tableBlock = BlockPart(tableNode, tableProvenance)

let trendBlock = BlockPart(trendNode, trendProvenance)

/// The text a message carries beside its blocks.
[<Literal>]
let Prose = "Revenue by site for the last delivery."

/// An assistant message's parts: prose, then a table block and a trend block.
let parts: AIContentPart list = [ TextPart Prose; tableBlock; trendBlock ]

/// `parts` as the server's conversation-blob writer writes it.
let partsJson =
    """[{"TextPart":"Revenue by site for the last delivery."},{"BlockPart":["{\"kind\":\"table\",\"title\":\"node-payload-7f3c\",\"rows\":[[\"north\",120],[\"south\",80]]}",{"Kind":"table","Request":"{\"kind\":\"table\",\"tableId\":\"daily-trade\",\"metric\":\"revenue\",\"period\":{\"day\":\"2026-10-26\"}}","FactIds":["fact-0001","fact-0002","fact-0003"],"Withheld":[{"PolicyRef":"trade-figures","Count":2}],"TableId":"daily-trade","Metric":"revenue","Period":"2026-10-26"}]},{"BlockPart":["{\"kind\":\"lineChart\",\"title\":\"node-payload-7f3c\",\"series\":[{\"name\":\"north\",\"points\":[[1,40],[2,80]]}]}",{"Kind":"trend","Request":"{\"kind\":\"trend\",\"tableId\":\"daily-trade\",\"metric\":\"revenue\",\"period\":{\"day\":\"2026-10-26\",\"rangeDays\":28}}","FactIds":["fact-0101","fact-0102"],"Withheld":[],"TableId":"daily-trade","Metric":"revenue","Period":"2026-09-29..2026-10-26"}]}]"""

/// `AIContentPart.redactedSummary` of each block.
let tableSummary =
    "[block: table, table=daily-trade, metric=revenue, period=2026-10-26, facts=3, withheld=2]"

let trendSummary =
    "[block: trend, table=daily-trade, metric=revenue, period=2026-09-29..2026-10-26, facts=2, withheld=0]"

/// `AIContentPart.exportPlaceholder 2 tableBlock`: a markdown table of the
/// provenance — what the read named and the facts it cited, no rows.
let tableExport =
    String.concat "\n" [
        "**Block #2: table**"
        ""
        "| Provenance | Value |"
        "|---|---|"
        "| Table | daily-trade |"
        "| Metric | revenue |"
        "| Period | 2026-10-26 |"
        "| Facts | 3 |"
        "| Fact | fact-0001 |"
        "| Fact | fact-0002 |"
        "| Fact | fact-0003 |"
        "| Withheld under trade-figures | 2 |"
    ]

/// `AIContentPart.exportPlaceholder 3 trendBlock`: a placeholder naming
/// the request.
let trendExport =
    """[block #3: trend, request `{"kind":"trend","tableId":"daily-trade","metric":"revenue","period":{"day":"2026-10-26","rangeDays":28}}`]"""

/// The provider history a message holding `parts` becomes: the user's
/// question, then the assistant turn built by `AIProviderMessage.multipart`.
let providerHistory: AIProviderMessage list = [
    AIProviderMessage.text "user" "Show me revenue by site."
    AIProviderMessage.multipart "assistant" parts
]