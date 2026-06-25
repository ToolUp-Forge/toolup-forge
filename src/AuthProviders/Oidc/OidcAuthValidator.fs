module ToolUp.AuthProviders.OidcAuthValidator

open System
open System.Net.Http
open System.Text.Json
open System.Threading
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
//
// ─── Phase 248 reachability-timeout knob ─────────────────────────────
//
// The probe deadline defaults to `IConfigValidator.defaultTimeout` (5s).
// A constrained tier (Azure App Service Linux B1, etc.) can have its
// first outbound HTTPS call after a cold start exceed 5s (TLS handshake
// + DNS + HttpClient JIT), which previously left a consumer only one
// lever: drop the reachability probe entirely — at which point a
// genuinely misconfigured / unreachable issuer ships green and surfaces
// only at first user sign-in as a 401, not at deploy.
//
// `TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS` *extends* (or shortens) the probe
// budget instead, so the boot-time signal is preserved on slow tiers
// (GP 1 — a deploy-environment-specific timing budget belongs in
// `fromEnv`, not hardcoded; GP 11 — preserve the misconfiguration
// signal; GP 13 — additive opt-in, byte-for-byte unchanged when unset).

/// Shared HttpClient — module-level `lazy` is thread-safe and
/// `HttpClient` is designed for reuse. Creating one per call would
/// exhaust ephemeral sockets under repeat preflights (e.g. CI loops).
let private httpClient = lazy (new HttpClient())

let private discoveryPath = "/.well-known/openid-configuration"

let private discoveryUrl (issuer: string) : string = issuer.TrimEnd('/') + discoveryPath

let private hasField (json: string) (field: string) : bool =
    use doc = JsonDocument.Parse json
    doc.RootElement.TryGetProperty field |> fst

/// Env var that overrides the discovery-reachability probe deadline.
[<Literal>]
let timeoutEnvVar = "TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS"

/// Upper sanity bound on the override (ms). A budget beyond this is
/// almost certainly a typo (seconds keyed as ms, an extra zero). The
/// aggregator additionally clamps any per-validator timeout to its 10s
/// global budget at runtime; this bound exists so a clearly-absurd value
/// is *rejected with a message* at parse time rather than silently
/// clamped. Range-guard pattern mirrors `RateLimitConfigValidator`.
[<Literal>]
let maxTimeoutMs = 300_000 // 5 minutes

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
            // Enforce the probe deadline inside the validator (independent
            // of the aggregator's outer cap) so a *timeout* surfaces this
            // validator's own message — which names the timeout knob — and
            // not the aggregator's generic "exceeded timeout" line. The
            // aggregator's per-validator cap remains a backstop.
            use cts = new CancellationTokenSource(timeout)

            try
                let! response = httpClient.Value.GetAsync(url, cts.Token) |> Async.AwaitTask

                if not response.IsSuccessStatusCode then
                    return Error(sprintf "discovery endpoint at %s returned HTTP %d" url (int response.StatusCode))
                else
                    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                    if hasField body "authorization_endpoint" && hasField body "token_endpoint" then
                        return Ok
                    else
                        return
                            Error(sprintf "discovery doc at %s is missing authorization_endpoint or token_endpoint" url)
            with
            | :? OperationCanceledException ->
                return
                    Error(
                        sprintf
                            "discovery endpoint at %s did not respond within %dms — if this is a cold-start timing issue, extend the budget via %s (e.g. %s=15000) rather than dropping the reachability probe."
                            url
                            (int timeout.TotalMilliseconds)
                            timeoutEnvVar
                            timeoutEnvVar
                    )
            | ex -> return Error(sprintf "discovery endpoint at %s unreachable: %s" url ex.Message)
        }

/// A validator that fails preflight with a fixed message — used when
/// `TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS` is set but unparseable / out of
/// range, so a typo'd budget is rejected at boot with a clear message
/// rather than silently defaulted. Keeps the `oidc-auth (issuer)` name so
/// it lands in the same `/dev/inspect` slot the real probe would.
type private InvalidConfig(issuer: string, message: string) =
    interface IConfigValidator with
        member _.Name = sprintf "oidc-auth (%s)" issuer
        member _.Timeout = IConfigValidator.defaultTimeout
        member _.Validate() = async { return Error message }

/// Parse the optional `TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS` override.
/// `Ok None` — unset (use the default); `Ok (Some ts)` — a valid
/// override; `Error msg` — set but non-numeric / non-positive / absurd,
/// surfaced as a preflight `Error` rather than silently defaulted.
let private parseTimeoutOverride () : Result<TimeSpan option, string> =
    match Environment.GetEnvironmentVariable timeoutEnvVar with
    | null
    | "" -> Result.Ok None
    | raw ->
        match Int32.TryParse(raw.Trim()) with
        | true, ms when ms > 0 && ms <= maxTimeoutMs -> Result.Ok(Some(TimeSpan.FromMilliseconds(float ms)))
        | _ ->
            Result.Error(
                sprintf
                    "%s=%s is invalid — expected a positive integer of milliseconds in (0, %d]. Fix or unset it; do not disable the OIDC reachability probe to work around a cold-start-slow issuer."
                    timeoutEnvVar
                    raw
                    maxTimeoutMs
            )

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
///
/// Phase 248: when `TOOLUP_OIDC_PREFLIGHT_TIMEOUT_MS` is set, it tunes
/// the probe deadline (default 5s). A set-but-invalid value yields a
/// validator that fails preflight with a clear message — never a silent
/// fallback to the default.
let tryFromEnv () : IConfigValidator option =
    match Environment.GetEnvironmentVariable "TOOLUP_OIDC_ISSUER" with
    | null
    | "" -> None
    | issuer ->
        match parseTimeoutOverride () with
        | Result.Ok None -> Some(Impl issuer :> IConfigValidator)
        | Result.Ok(Some ts) -> Some(Impl(issuer, ts) :> IConfigValidator)
        | Result.Error msg -> Some(InvalidConfig(issuer, msg) :> IConfigValidator)