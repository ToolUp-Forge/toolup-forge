// Ambient context for `src/ToolUp.Platform/technical-guide/06-jobs-ingestion-and-diagnostics.md`.
//
// Two illustrative consumer-side names the page uses to make its wiring
// concrete: the module's own job handler, and a connector companion's
// credential form. Both belong to the reader's program — the SDK ships
// neither — so they are declared here rather than in the blocks.
open Feliz

[<AutoOpen>]
module PageAmbient =

    /// Stands in for the reader's own `IJobHandler` implementation.
    type SalesAnalysisRollupHandler() =
        interface IJobHandler with
            member _.Execute(ctx: JobContext) : Async<JobResult> = failwith "ambient"

    /// Stands in for a connector companion's Feliz credential form.
    module GoogleAnalyticsCredentialUI =

        let render (ctx: DataSourceCredentialUIContext) : ReactElement = failwith "ambient"