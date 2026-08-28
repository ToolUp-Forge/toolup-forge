// Ambient context for `src/ToolUp.OpenXml/README.md`.
//
// The Figures section shows a raster embed beside a native SVG embed, and reads
// two locals the page never produces: the PNG bytes a caller already holds, and
// the SVG text a chart renderer already emitted. Declaring them here keeps the
// block itself copy-clean and — unlike a `skip=fragment` marker — keeps every
// `Figures.*` / `Emit.*` / `DocModel.*` name in it under the gate.

[<AutoOpen>]
module OpenXmlReadmeAmbient =

    /// The bytes of a PNG the caller already holds (read from disk, a blob
    /// store, an asset store — the page deliberately does not say which).
    let logoBytes: byte[] = failwith "ambient"

    /// An SVG document a chart renderer already emitted.
    let chartSvg: string = failwith "ambient"