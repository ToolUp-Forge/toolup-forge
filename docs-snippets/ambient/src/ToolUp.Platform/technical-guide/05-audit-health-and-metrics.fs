// Ambient context for `src/ToolUp.Platform/technical-guide/05-audit-health-and-metrics.md`.
//
// The wiring excerpts on this page are all lifted out of one composition
// root, and each reads values that root already built: the deployment's
// `ServerConfig`, the `IBlobStorage` the audit archive writes through, the
// Redis connection string, and the logger the channel is handed. A block
// that declares any of these for itself shadows this one.
open ToolUp.Platform.BlobStorage

[<AutoOpen>]
module PageAmbient =

    let config: ServerConfig = failwith "ambient"

    let blobStorage: IBlobStorage = failwith "ambient"

    let connectionString: string = failwith "ambient"

    let logger: ILogger = failwith "ambient"