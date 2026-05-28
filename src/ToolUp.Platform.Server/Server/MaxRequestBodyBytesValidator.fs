module ToolUp.Platform.MaxRequestBodyBytesValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Gap audit pass-2 #3 — request body cap × rate-limit interaction ─
//
// `ServerConfig.MaxRequestBodyBytes = None` keeps Kestrel's 30 MB
// default. That's a high cap for an API-only deployment, but acceptable
// for deployments serving large file uploads. When the cap is high
// AND the deployment is internet-facing AND there's no rate-limiting,
// a single malicious client can fill a 30 MB JSON body buffer per
// permitted request and drive the host into memory pressure cheaply.
// Phase 6l.G's `RateLimitModeValidator` catches the same combination
// for the no-cap case but doesn't know about body-size; this validator
// is the per-request-size complement.
//
// Heuristic — any of these emits a Warning:
//   * `Mode != Anonymous` AND `RequireHttps = true` AND
//     `MaxRequestBodyBytes` either `None` (= 30 MB Kestrel default) OR
//     `Some bytes` where `bytes > 50 MB` AND `RateLimit = None`.
//
// API-only deployments without large-upload needs and no rate-limit
// proxy upstream are the canonical risky shape; the validator points
// the operator at the two knobs (the body cap or the rate limit).
//
// Severity: Warning. The default 30 MB is the framework default;
// upgrading to Error would be over-eager for the many small dev
// deployments that legitimately don't tune Kestrel.

let private kestrelDefaultBytes = 30L * 1024L * 1024L
let private warningThresholdBytes = 50L * 1024L * 1024L

/// Gap audit pass-2 #3 — config validator that warns when a
/// production-shaped deployment combines a high body cap with no
/// rate-limiting (cheap DoS surface).
type MaxRequestBodyBytesValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "max-request-body-bytes"
        member _.Timeout = timeout

        member _.Validate() = async {
            let requiresAuth = DeploymentConfig.requiresAnyAuth config
            let internetFacing = config.RequireHttps
            let hasRateLimit = config.RateLimit.IsSome

            let effectiveBytes =
                config.MaxRequestBodyBytes |> Option.defaultValue kestrelDefaultBytes

            let highCap = effectiveBytes > warningThresholdBytes

            if requiresAuth && internetFacing && not hasRateLimit && highCap then
                let capDescription =
                    match config.MaxRequestBodyBytes with
                    | None ->
                        sprintf
                            "%d MB (Kestrel default — TOOLUP_MAX_REQUEST_BODY_BYTES unset)"
                            (int (kestrelDefaultBytes / 1024L / 1024L))
                    | Some b -> sprintf "%d MB" (int (b / 1024L / 1024L))

                return
                    Warning(
                        sprintf
                            "ServerConfig.Surfaces = %s + RequireHttps = true + RateLimit = None + MaxRequestBodyBytes = %s. An internet-facing authenticated deployment with no rate-limiting and a high per-request body cap is a cheap memory-DoS surface — a single client can fill %s buffers per permitted request. Either tighten the body cap (set ServerConfig.MaxRequestBodyBytes = Some <bytes> to e.g. 5_242_880L for a 5 MB ceiling) or enable rate-limiting (ServerConfig.RateLimit = Some { ... }) to bound the per-client request rate. Deployments behind a rate-limiting proxy (nginx / Cloudflare / ALB) can ignore this warning."
                            (DeploymentConfig.surfacesLabel config)
                            capDescription
                            capDescription
                    )
            else
                return Ok
        }