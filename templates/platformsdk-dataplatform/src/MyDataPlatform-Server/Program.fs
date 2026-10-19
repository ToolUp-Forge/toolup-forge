module MyDataPlatform.Server.Program

open System
open System.IO
open ToolUp.Platform
open ToolUp.AuthProviders

let private env (name: string) : string option =
    match Environment.GetEnvironmentVariable name with
    | null -> None
    | v when String.IsNullOrWhiteSpace v -> None
    | v -> Some(v.Trim())

/// Interactive sign-in for the gated pages, composed when the deployment
/// selects OIDC (`TOOLUP_AUTH_MODE=oidc`). The client id is the token
/// audience; the two endpoints and the redirect URI are your identity
/// provider's. With OIDC unset the server runs forge's dev-only header
/// identity (`X-User-Id`), which is for local runs only.
let private signIn () : OidcSsrSignIn.OidcSsrSignInConfig option =
    match env "TOOLUP_AUTH_MODE" with
    | Some mode when mode.Equals("oidc", StringComparison.OrdinalIgnoreCase) ->
        let required name =
            env name
            |> Option.defaultWith (fun () -> invalidOp (sprintf "%s is not set; OIDC sign-in needs it" name))

        Some {
            OidcSsrSignIn.defaults
                (required "OIDC_AUTHORIZATION_ENDPOINT")
                (required "OIDC_TOKEN_ENDPOINT")
                (required "TOOLUP_OIDC_AUDIENCE")
                (required "OIDC_REDIRECT_URI") with
                ClientSecret = env "OIDC_CLIENT_SECRET"
        }
    | _ -> None

/// `--local` runs on this machine with the local run's settings (LocalRun.fs).
[<EntryPoint>]
let main argv =
    // Verify the reference data before anything is served.
    ReferenceData.verify ()

    let dataRoot = env "APP_DATA_ROOT" |> Option.defaultValue (Path.GetFullPath "data")

    if Array.contains "--local" argv then
        let port = env "SERVER_PORT" |> Option.defaultValue "5000"
        LocalRun.apply (sprintf "http://localhost:%s" port) dataRoot

    let logger = ConsoleLogger.fromEnv ()

    let config =
        let fromEnv = ServerConfig.fromEnv logger ServerConfigOverrides.empty

        let fromEnv =
            match env "SERVER_PORT" with
            | Some _ -> fromEnv
            | None -> { fromEnv with Port = 5000 }

        Composition.configure fromEnv (Path.Combine(AppContext.BaseDirectory, "content"))

    let storage = Companions.blobStorage (Path.Combine(dataRoot, "blobs"))
    let secrets = Companions.secretStore (Path.Combine(dataRoot, "secrets"))
    let profile = Companions.providerProfile storage

    Composition.build {
        Config = config
        Storage = storage
        Secrets = secrets
        ProviderProfile = profile
        AIProviderFactory = Companions.aiProviderFactory profile secrets
        Embedder = LocalEmbeddingProvider.createPersistent storage
        Auth = AuthProvider.fromEnv logger OidcAuthProvider.fromConfig
        SignIn = signIn ()
    }
    |> ServerApp.run