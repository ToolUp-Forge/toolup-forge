// Ambient context for `src/Feliz.AgGrid/README.md`.
//
// The README's opening block is a binding example, so the only thing it
// reads from outside itself is the consumer's own row collection — what
// `AgGrid.rowData` is handed, and the record whose field the block's
// `ColumnDef.field` selector projects. Declared here so the block
// compiles exactly as a reader would copy it, with no data-shape
// ceremony added to the markdown.

[<AutoOpen>]
module PageAmbient =

    /// One row of the consumer's own grid — the shape `ColumnDef.field`
    /// selects against.
    type GridRow = { Name: string }

    /// The rows the grid is bound to.
    let rows: GridRow list = failwith "ambient"