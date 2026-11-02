// Ambient context for `src/ToolUp.Facts.Presentation/README.md`.
//
// The page's block reads two locals from a handler it never shows in
// full: the request body as it arrived (`requestJson`) and the fact
// series the deployment's `IFactBrowseApi.QuerySeries` answered with
// (`series`). Declared here so the block compiles exactly as a reader
// would copy it.
open ToolUp.Facts

[<AutoOpen>]
module PageAmbient =
    let requestJson: string = failwith "ambient"
    let series: FactSeries = failwith "ambient"