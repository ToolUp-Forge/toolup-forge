// Ambient context for `src/Hosts/EdgeCache/README.md`.
//
// The purge example composes two things the page never shows being
// built, because neither is this companion's to build: the deployment's
// own `HttpClient` (whose lifetime, timeout and handler chain belong to
// the deployment) and its composed `ISecretStore`. Declared here so the
// block compiles exactly as a reader would copy it.
open System.Net.Http
open ToolUp.Platform.Secrets

[<AutoOpen>]
module PageAmbient =

    /// The deployment's own `HttpClient`, supplied to
    /// `HttpEdgeCache.create` rather than constructed by it.
    let httpClient: HttpClient = failwith "ambient"

    /// The composed secret store the purge credential is read from on
    /// every call.
    let secretStore: ISecretStore = failwith "ambient"