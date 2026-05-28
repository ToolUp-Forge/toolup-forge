module ToolUp.Platform.RateLimitConfigValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── RateLimitConfig range preflight ────────────────────────────────
//
// `RateLimiting.configure` feeds `RateLimitConfig` straight into
// `FixedWindowRateLimiterOptions` with no sanity check:
//   PermitLimit  -> FixedWindowRateLimiterOptions.PermitLimit (must be > 0)
//   WindowSeconds -> TimeSpan.FromSeconds (Window must be > TimeSpan.Zero)
//   QueueLimit   -> FixedWindowRateLimiterOptions.QueueLimit (must be >= 0)
//
// A misconfigured record (PermitLimit = 0, negative WindowSeconds, etc.)
// either throws an opaque `ArgumentOutOfRangeException` from inside the
// BCL limiter on the first request the partition is created for, or —
// with `PermitLimit = 0` — silently 429s *all* traffic. Both are
// production incidents that surface far from the cause.
//
// Severity: Error. An out-of-range limiter is not a degraded mode —
// it is a deployment that cannot serve traffic correctly. Aborting at
// preflight with an actionable message is strictly better than a
// first-request stack trace or a total outage. Only validates when
// `RateLimit` is `Some` (the default `None` disables the limiter
// entirely and is covered by `RateLimitModeValidator`).

/// Range validator for `ServerConfig.RateLimit`. Rejects values the
/// BCL fixed-window limiter cannot accept, before Kestrel binds.
type RateLimitConfigValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "rate-limit-config"
        member _.Timeout = timeout

        member _.Validate() = async {
            match config.RateLimit with
            | None -> return Ok
            | Some rl ->
                let problems = [
                    if rl.PermitLimit <= 0 then
                        sprintf
                            "PermitLimit = %d (must be > 0; the BCL limiter rejects <= 0, and 0 would 429 every request)"
                            rl.PermitLimit
                    if rl.WindowSeconds <= 0 then
                        sprintf
                            "WindowSeconds = %d (must be > 0; TimeSpan.FromSeconds of a non-positive value yields a non-positive Window the limiter rejects)"
                            rl.WindowSeconds
                    if rl.QueueLimit < 0 then
                        sprintf "QueueLimit = %d (must be >= 0)" rl.QueueLimit
                ]

                match problems with
                | [] -> return Ok
                | _ ->
                    return
                        Error(
                            sprintf
                                "ServerConfig.RateLimit has out-of-range values: %s. These feed FixedWindowRateLimiterOptions directly; an invalid record throws an opaque ArgumentOutOfRangeException on the first request its partition is created for (or, with PermitLimit = 0, 429s all traffic). Fix the record, e.g. ServerConfig.RateLimit = Some { PermitLimit = 100; WindowSeconds = 60; QueueLimit = 20 }."
                                (String.concat "; " problems)
                        )
        }