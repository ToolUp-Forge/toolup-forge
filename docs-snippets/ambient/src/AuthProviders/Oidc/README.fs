// Ambient context for `src/AuthProviders/Oidc/README.md`.
//
// The page's one block is an excerpt from a composition root it never
// shows in full: the `app` the sign-in page is composed onto, the
// `http` client the callback exchanges the code with, and the
// `config` built earlier in the page's prose. Declared here so the
// block compiles exactly as a reader would copy it.
open System.Net.Http
open ToolUp.Platform
open ToolUp.AuthProviders

[<AutoOpen>]
module PageAmbient =
    let app: ServerApp = failwith "ambient"
    let http: HttpClient = failwith "ambient"
    let config: OidcSsrSignIn.OidcSsrSignInConfig = failwith "ambient"