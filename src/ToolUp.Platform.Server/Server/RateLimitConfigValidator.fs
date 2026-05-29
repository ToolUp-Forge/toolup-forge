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
// first-request stack trace or a total outage. Phase 66 Stream C.3:
// validates the `Default` policy plus every `PerShape` entry; the
// default `RateLimitConfig.none` carries no policies so it passes
// trivially (the no-limiter case is covered by `RateLimitModeValidator`).

/// Range validator for `ServerConfig.RateLimit`. Rejects values the
/// BCL fixed-window limiter cannot accept, before Kestrel binds.
type RateLimitConfigValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "rate-limit-config"
        member _.Timeout = timeout

        member _.Validate() = async {
            // Phase 66 Stream C.3 — validate every policy the config
            // carries: the `Default` (if any) plus each `PerShape` entry.
            // Each feeds FixedWindowRateLimiterOptions identically, so each
            // gets the same range check, labelled by its origin.
            let labelledPolicies = [
                match config.RateLimit.Default with
                | Some p -> "Default", p
                | None -> ()
                for KeyValue(kind, p) in config.RateLimit.PerShape do
                    (sprintf "PerShape[%A]" kind), p
            ]

            let problemsFor (label: string) (rl: RateLimitPolicy) = [
                if rl.PermitLimit <= 0 then
                    sprintf
                        "%s.PermitLimit = %d (must be > 0; the BCL limiter rejects <= 0, and 0 would 429 every request)"
                        label
                        rl.PermitLimit
                if rl.WindowSeconds <= 0 then
                    sprintf
                        "%s.WindowSeconds = %d (must be > 0; TimeSpan.FromSeconds of a non-positive value yields a non-positive Window the limiter rejects)"
                        label
                        rl.WindowSeconds
                if rl.QueueLimit < 0 then
                    sprintf "%s.QueueLimit = %d (must be >= 0)" label rl.QueueLimit
            ]

            let problems =
                labelledPolicies |> List.collect (fun (label, rl) -> problemsFor label rl)

            match problems with
            | [] -> return Ok
            | _ ->
                return
                    Error(
                        sprintf
                            "ServerConfig.RateLimit has out-of-range values: %s. These feed FixedWindowRateLimiterOptions directly; an invalid record throws an opaque ArgumentOutOfRangeException on the first request its partition is created for (or, with PermitLimit = 0, 429s all traffic). Fix the record, e.g. ServerConfig.RateLimit = RateLimitConfig.uniform { PermitLimit = 100; WindowSeconds = 60; QueueLimit = 20 }."
                            (String.concat "; " problems)
                    )
        }