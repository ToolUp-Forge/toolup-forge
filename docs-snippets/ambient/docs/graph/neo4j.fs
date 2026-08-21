// Ambient context for `docs/graph/neo4j.md`.
//
// The composition blocks read the server URI, the credentials, and the DI
// collection from a composition root the page never shows in full — the
// page's own prose says to resolve them from `ISecretStore` rather than
// hard-code them. Declared here so the blocks compile as written.
open Microsoft.Extensions.DependencyInjection

[<AutoOpen>]
module PageAmbient =

    /// A bolt / neo4j / neo4j+s URI. `neo4j://` enables cluster routing.
    let uri: string = failwith "ambient"

    let username: string = failwith "ambient"

    let password: string = failwith "ambient"

    let services: IServiceCollection = failwith "ambient"