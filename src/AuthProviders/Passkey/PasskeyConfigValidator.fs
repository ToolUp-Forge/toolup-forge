// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Passkey.PasskeyConfigValidator

open System
open ToolUp.Platform.ConfigValidation
open ToolUp.AuthProviders.Passkey.PasskeyTypes

// ─── 443.D — startup preflight (security-class) ──────────────────────
//
// A WebAuthn relying-party id that is NOT a registrable suffix of the
// browser origin makes every ceremony fail at the authenticator (the
// browser refuses to scope a credential to an unrelated domain) — a
// silent-until-first-signup misconfiguration this guard turns into a
// loud startup abort. Likewise a cleartext origin: WebAuthn requires a
// secure context, so an `http://` origin (outside loopback) is a
// deployment error. Marked `ISecurityClassValidator` so it runs even
// under `ServerConfig.SkipPreflight` — an RP/origin mismatch is an
// auth-integrity hole, not a noisy optional probe.

let private isLoopbackHost (host: string) : bool =
    match host.ToLowerInvariant() with
    | "localhost"
    | "127.0.0.1"
    | "::1"
    | "[::1]" -> true
    | _ -> false

/// True when `rpId` is the origin host or a registrable parent suffix of
/// it (`example.com` covers `app.example.com`). Case-insensitive.
let isRegistrableSuffix (rpId: string) (host: string) : bool =
    let rp = rpId.ToLowerInvariant()
    let h = host.ToLowerInvariant()
    h = rp || h.EndsWith("." + rp, StringComparison.Ordinal)

/// Pure validation — returns the first `Error` (or a `Warning`), else
/// `Ok`. Separated from the interface so it is unit-testable.
let validateConfig (config: PasskeyConfig) : ValidationResult =
    if List.isEmpty config.Origins then
        Error "PasskeyConfig.Origins is empty — declare at least one browser origin the passkey ceremony runs from."
    elif String.IsNullOrWhiteSpace config.RelyingPartyId then
        Error
            "PasskeyConfig.RelyingPartyId is empty — set it to a registrable domain suffix of every origin (e.g. \"example.com\")."
    else
        let problems =
            config.Origins
            |> List.choose (fun origin ->
                match Uri.TryCreate(origin, UriKind.Absolute) with
                | false, _ -> Some $"origin '{origin}' is not an absolute URL"
                | true, uri ->
                    if config.EnforceHttps && uri.Scheme <> "https" && not (isLoopbackHost uri.Host) then
                        Some
                            $"origin '{origin}' is not https — WebAuthn requires a secure context (loopback hosts are exempt for local dev; set EnforceHttps = false only behind a terminating TLS proxy)"
                    elif not (isRegistrableSuffix config.RelyingPartyId uri.Host) then
                        Some
                            $"relying-party id '{config.RelyingPartyId}' is not a registrable suffix of origin host '{uri.Host}' — the browser will refuse every ceremony"
                    else
                        None)

        match problems with
        | [] ->
            if config.AllowOpenRegistration then
                // Not an error — an explicit, deliberate posture — but
                // surfaced so an operator can't enable open enrolment by
                // accident and never notice.
                Warning
                    "PasskeyConfig.AllowOpenRegistration = true — anyone can enrol a passkey without an invite. Intended only for open sign-up deployments."
            else
                Ok
        | first :: _ -> Error $"Passkey configuration invalid: {first}."

/// Registered via `PasskeyCompose.run` (and `ServerApp.withConfigValidator`).
type PasskeyConfigValidator(config: PasskeyConfig) =
    interface IConfigValidator with
        member _.Name = $"passkey-auth ({config.RelyingPartyId})"
        member _.Timeout = IConfigValidator.defaultTimeout
        member _.Validate() = async { return validateConfig config }

    // Security-class: an RP/origin mismatch or cleartext origin is an
    // auth-integrity hole, so this guard runs even when SkipPreflight
    // is set for the noisy optional probes.
    interface ISecurityClassValidator