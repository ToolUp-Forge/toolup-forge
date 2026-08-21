// Ambient context for `docs/graph/age.md`.
//
// The page's blocks are composition-root excerpts: they read the
// connection string, the DI collection, and (for the shared-transaction
// seam) the caller's own `NpgsqlDataSource` / scope / node from a program
// the page never shows in full. Declared here so the blocks compile as a
// reader would copy them.
open Microsoft.Extensions.DependencyInjection
open Npgsql
open ToolUp.Graph
open ToolUp.Graph.AGE

[<AutoOpen>]
module PageAmbient =

    /// Resolved at compose from `ISecretStore` / the `fromEnv` helpers —
    /// never a literal, per the companion-authoring guide.
    let connectionString: string = failwith "ambient"

    let services: IServiceCollection = failwith "ambient"

    /// The consumer's own pooled data source — typically the one backing
    /// its `IEntityStore`, which is what enables the shared-transaction seam.
    let dataSource: NpgsqlDataSource = failwith "ambient"

    let config: AgeGraphStoreConfig = AgeGraphStoreConfig.defaults

    let scopeId: string = failwith "ambient"

    let node: GraphNode = failwith "ambient"