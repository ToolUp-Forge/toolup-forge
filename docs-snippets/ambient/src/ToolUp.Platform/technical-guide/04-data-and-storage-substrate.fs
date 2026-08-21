// Ambient context for `src/ToolUp.Platform/technical-guide/04-data-and-storage-substrate.md`.
//
// The "When modules call `IResultStore`" excerpt runs inside a server-side
// module handler. Everything it reads — the request's `HttpContext`, the
// module's own analysis function, and the ids the surrounding request
// already resolved — belongs to that handler, not to the SDK, so it is
// declared here rather than padding the excerpt a reader copies.
open Microsoft.AspNetCore.Http
open System.Text.Json

[<AutoOpen>]
module PageAmbient =

    type AnalysisResult = { Rows: int }

    let ctx: HttpContext = failwith "ambient"

    let input: byte[] = failwith "ambient"

    let computeAnalysis (bytes: byte[]) : Async<AnalysisResult> = failwith "ambient"

    let inputFileObjectId: string = failwith "ambient"

    let scopeId: string = failwith "ambient"

    let userId: string = failwith "ambient"