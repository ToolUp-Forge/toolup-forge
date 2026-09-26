// Ambient context for `src/Observability/Datadog/README.md`.
//
// The page is a companion README, so its composition blocks are excerpts
// from the consuming server's composition root: the deployment's
// `ISecretStore`, the one long-lived `HttpClient`, the DI collection the
// client registers into, and the `DatadogReadbackConfig` the first block
// builds and the `ServerConfig` block reads back. Declared here so the
// blocks compile exactly as a reader would copy them. The first block's
// own `settings` shadows the ambient one, which is the point of the
// auto-opened module.
open System.Net.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Secrets

[<AutoOpen>]
module PageAmbient =

    /// The deployment's secret store, already composed; the client reads
    /// `DD-API-KEY` / `DD-APPLICATION-KEY` from it on every call.
    let secretStore: ISecretStore = failwith "ambient"

    /// The one `HttpClient` the deployment keeps for its lifetime.
    let httpClient: HttpClient = failwith "ambient"

    /// The DI collection the readback client is registered into.
    let services: IServiceCollection = failwith "ambient"

    /// The settings value the "Composing it" block builds; the
    /// `ServerConfig` block reads it back rather than restating it.
    let settings: DatadogReadbackConfig = failwith "ambient"