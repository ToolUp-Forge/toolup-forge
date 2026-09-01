// Ambient context for `src/Feliz.AgCharts/README.md`.
//
// The README's opening block is a binding example, so the only thing it
// reads from outside itself is the consumer's own data series — the rows
// `AgChart.data` is handed. Declared here so the block compiles exactly
// as a reader would copy it, with no data-shape ceremony added to the
// markdown.

[<AutoOpen>]
module PageAmbient =

    /// One point of the consumer's own series — the shape whose field
    /// names the block's `xKey` / `yKey` select on.
    type RevenuePoint = { month: string; revenue: float }

    /// The rows the chart is bound to.
    let points: RevenuePoint list = failwith "ambient"