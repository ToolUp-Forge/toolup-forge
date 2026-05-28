module ToolUp.AuthProviders.OidcAuthValidator

open System
open System.Net.Http
open System.Text.Json
open ToolUp.Platform.ConfigValidation

// ─── Phase 9m OIDC config preflight ──────────────────────────────────
//
// Probes the configured OIDC issuer's discovery endpoint
// (`{issuer}/.well-known/openid-configuration`) once at startup. If the
// endpoint is unreachable, returns a non-2xx, or omits the
// `authorization_endpoint` / `token_endpoint` keys, the validator
// returns `Error` and the SDK aggregator aborts startup with a
// single-line summary naming the failing issuer.
//
// Companion auto-registration: a deployment that imports
// `OidcAuthProvider.Server.props` and reads `TOOLUP_OIDC_ISSUER` calls
// `OidcAuthValidator.tryFromEnv` at composition time. When the env var
// is unset, `tryFromEnv` returns `None` and no validator is registered
// (GP 13 — lightweight default; deployments without OIDC pay nothing).
// When set, the deployment wires the validator in via
// `ServerApp.withConfigValidator`.

/// Shared HttpClient — module-level `lazy` is thread-safe and
/// `HttpClient` is designed for reuse. Creating one per call would
/// exhaust ephemeral sockets under repeat preflights (e.g. CI loops).
let private httpClient = lazy (new HttpClient())

let private discoveryPath = "/.well-known/openid-configuration"

let private discoveryUrl (issuer: string) : string = issuer.TrimEnd('/') + discoveryPath

let private hasField (json: string) (field: string) : bool =
    use doc = JsonDocument.Parse json
    doc.RootElement.TryGetProperty field |> fst

/// Validator implementation. Hidden behind `create` / `tryFromEnv`
/// because the outer module shares its name and exposing the type
/// directly produces an ambiguous `OidcAuthValidator.OidcAuthValidator`
/// resolution path on call sites.
type private Impl(issuer: string, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout
    let url = discoveryUrl issuer

    interface IConfigValidator with
        member _.Name = sprintf "oidc-auth (%s)" issuer
        member _.Timeout = timeout

        member _.Validate() = async {
            try
                let! response = httpClient.Value.GetAsync url |> Async.AwaitTask

                if not response.IsSuccessStatusCode then
                    return Error(sprintf "discovery endpoint at %s returned HTTP %d" url (int response.StatusCode))
                else
                    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                    if hasField body "authorization_endpoint" && hasField body "token_endpoint" then
                        return Ok
                    else
                        return
                            Error(sprintf "discovery doc at %s is missing authorization_endpoint or token_endpoint" url)
            with ex ->
                return Error(sprintf "discovery endpoint at %s unreachable: %s" url ex.Message)
        }

/// Construct a validator for an explicit issuer URL. Apps with
/// per-deployment custom config (a YAML file, KMS secrets) build the
/// URL themselves and call this directly.
let create (issuer: string) : IConfigValidator = Impl(issuer) :> IConfigValidator

/// Read `TOOLUP_OIDC_ISSUER` from the environment and return a
/// validator if set, `None` otherwise. Mirrors the
/// `SmtpNotificationSink.fromEnv` convenience pattern but returns an
/// option rather than failing fast — preflight is GP 13 lightweight-
/// default territory: a deployment without OIDC must not be punished
/// for importing the companion's props.
let tryFromEnv () : IConfigValidator option =
    match Environment.GetEnvironmentVariable "TOOLUP_OIDC_ISSUER" with
    | null
    | "" -> None
    | issuer -> Some(create issuer)