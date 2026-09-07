// Ambient context for `src/AuthProviders/OidcClient/README.md`.
//
// The page is a companion README, and its refresh-policy block is an
// excerpt from the consuming client's composition root: the issuer
// details the deployment already holds (`tenant`, `clientId`,
// `redirectUri`, `issuer`) are read, never declared, because declaring
// them would turn a three-line "which knob" example into a config
// tutorial. Declared here so the block compiles exactly as a reader
// would copy it, with no `open`-ceremony added to the markdown.
//
// `OidcPresets` lives in the `ToolUp.AuthProviders.Oidc` namespace; the
// README refers to it qualified, as a consumer would after the one
// `open` a composition root already carries.
open ToolUp.AuthProviders.Oidc

[<AutoOpen>]
module PageAmbient =

    /// The Entra External ID tenant subdomain the deployment signs in against.
    let tenant: string = failwith "ambient"

    /// The OIDC client id registered with the issuer.
    let clientId: string = failwith "ambient"

    /// The redirect URI registered with the issuer.
    let redirectUri: string = failwith "ambient"

    /// The issuer URL of a provider the SDK has no first-class preset for.
    let issuer: string = failwith "ambient"