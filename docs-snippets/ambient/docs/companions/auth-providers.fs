// Ambient context for `docs/companions/auth-providers.md`.
//
// The page is a cross-cutting tour of the shipped auth-provider
// companions, so nearly every block is an excerpt from a composition
// root it never shows in full: the deployment's `config` / `logger`,
// the compose-time DI seams (`services` for the preflight validators,
// `serviceProvider` for the resolved `IMetricsSink`), the `authConfig`
// built two sections earlier, the distributed `channel` the JWKS
// eviction signal publishes on, the `secretStore` a directory
// companion reads its credential from, and the `modules` list the
// client shell is run with. Declared here so the blocks compile
// exactly as a reader would copy them, with no `open`-ceremony added
// to the markdown.
//
// The page's own `open` lines stay in the markdown, because which
// package a companion lives in is part of what the page teaches; these
// are the ones a real composition root would already carry.
// `ToolUp.Platform.ConfigValidation` is deliberately NOT opened here.
// Its `ValidationResult` is `Ok | Warning of string | Error of string`,
// so opening it rebinds `Ok` and `Error` away from `Result` for every
// block on the page — silently, and only visibly in the one block that
// pattern-matches a `Result`. The two blocks that need the interface
// spell it `ConfigValidation.IConfigValidator`, which is exactly how
// `ServerApp.withConfigValidator` declares its own parameter.
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform.Metrics
open ToolUp.Platform.Secrets
open ToolUp.AuthProviders
open ToolUp.AuthProviders.GoogleDirectory
open ToolUp.AuthProviders.GoogleIdentity.GoogleIdentityConfig
open ToolUp.AuthProviders.Oidc.OidcAppConfig

[<AutoOpen>]
module PageAmbient =

    /// The deployment's own `ServerConfig`. The page spells it both
    /// ways — `config` in the composition-root pipelines, `serverConfig`
    /// in the CSP-validator block, which also takes the service
    /// collection.
    let config: ServerConfig = failwith "ambient"

    let serverConfig: ServerConfig = failwith "ambient"

    /// The SDK logger every provider factory takes as `ILogger option`.
    let logger: ILogger = failwith "ambient"

    /// The compose-time service COLLECTION, which the preflight
    /// validators inspect for a registered contributor...
    let services: IServiceCollection = failwith "ambient"

    /// ...and the built service PROVIDER, which is what resolves an
    /// already-registered `IMetricsSink`.
    let serviceProvider: IServiceProvider = failwith "ambient"

    /// The issuer / audience / client-registration values a deployment
    /// binds from its own configuration.
    ///
    /// `oidcIssuerUrl` rather than `issuerUrl` on purpose: a block's own
    /// `open` lines are spliced AFTER this preamble, so a companion
    /// module exporting an `issuerUrl` function would shadow anything
    /// declared here. The two that did — the Entra External ID config
    /// modules — were removed at Phase 749, but the naming stays: the
    /// hazard is structural, not specific to them.
    let oidcIssuerUrl: string = failwith "ambient"

    let audience: string = failwith "ambient"

    let issuer: string = failwith "ambient"

    let clientId: string = failwith "ambient"

    let redirectUri: string = failwith "ambient"

    /// The `AuthConfig` the page builds in "Claim mapping" and reuses
    /// in the metered / hardened construction blocks.
    let authConfig: AuthConfig = failwith "ambient"

    /// The distributed notification companion the JWKS eviction signal
    /// is published on and subscribed to.
    let channel: INotificationChannel = failwith "ambient"

    /// The deployment's secret backend — where the Google Workspace
    /// service-account key lives.
    let secretStore: ISecretStore = failwith "ambient"

    /// The consumer's own module list, passed to `Client.run`.
    let modules: ErasedModule list = failwith "ambient"

    /// The one Google `OidcAppConfig` declaration the worked example
    /// projects both sides from.
    let googleCfg: OidcAppConfig = failwith "ambient"

    /// The Google Identity Services UI config the One-Tap opt-in
    /// block builds on.
    let googleUi: GoogleIdentityUIConfig = failwith "ambient"

    /// The custom-provider example's own domain shapes — the config
    /// record its constructor takes, and the claims its bespoke
    /// `validateToken` returns. Page-local, not SDK types.
    type MyAuthConfig = { Endpoint: string; SigningKey: string }

    type MyClaims = {
        Subject: string
        Name: string
        Email: string option
    }

    let validateToken (token: string) : Result<MyClaims, string> = failwith "ambient"