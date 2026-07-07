// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.LdapConfigValidator

open System
open System.Net.Sockets
open System.Threading
open ToolUp.Platform.ConfigValidation
open ToolUp.AuthProviders.LdapConfig

// ─── LDAP config preflight (Phase 9m IConfigValidator) ──────────────
//
// Startup guard for the LDAP auth provider. It is a **security-class**
// validator (`ISecurityClassValidator`) — its channel-binding and
// plaintext-bind checks protect against credentials crossing the
// network in the clear, so they must run even under
// `ServerConfig.SkipPreflight = true`. One boolean bypass lever for a
// noisy probe must never silently disable an auth-class guard.
//
// Outcomes:
//   • Error   — aborts startup. Reserved for a genuine misconfiguration
//               or security hole: no search base, no host, or a
//               plaintext bind that was NOT explicitly opted into.
//   • Warning — logged, does not abort. A weak-but-operator-chosen
//               posture: an opted-in plaintext bind, disabled
//               certificate validation, or an unreachable directory
//               (transient — never block boot over a directory that is
//               briefly down).

let private reachable (host: string) (port: int) (timeout: TimeSpan) : Async<Result<unit, string>> = async {
    try
        use client = new TcpClient()
        use cts = new CancellationTokenSource(timeout)

        do! client.ConnectAsync(host, port, cts.Token).AsTask() |> Async.AwaitTask

        return Result.Ok()
    with
    | :? OperationCanceledException ->
        return Result.Error(sprintf "no TCP response from %s:%d within %dms" host port (int timeout.TotalMilliseconds))
    | ex -> return Result.Error(sprintf "cannot reach %s:%d: %s" host port ex.Message)
}

/// Validator implementation. Implements both `IConfigValidator` and the
/// `ISecurityClassValidator` marker so it always runs under
/// `SkipPreflight`.
type private Impl(config: LdapConfig, plaintextOptedIn: bool, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = sprintf "ldap-auth (%s:%d)" config.Host config.Port
        member _.Timeout = timeout

        member _.Validate() = async {
            // 1. Hard misconfigurations abort — a provider with no
            //    host or no search base cannot authenticate anyone
            //    and would fail every request at runtime instead.
            if String.IsNullOrWhiteSpace config.Host then
                return Error "LDAP host is not configured (TOOLUP_LDAP_HOST)"
            elif String.IsNullOrWhiteSpace config.SearchBase then
                return Error "LDAP search base is not configured (TOOLUP_LDAP_SEARCH_BASE) — no user could be resolved"
            else
                // 2. Plaintext-bind security gate. A plaintext bind
                //    that was not explicitly acknowledged aborts;
                //    an acknowledged one warns loudly.
                match config.ChannelBinding with
                | Plaintext when not plaintextOptedIn ->
                    return
                        Error
                            "LDAP is configured for a plaintext bind (credentials cross the network unencrypted) without the TOOLUP_LDAP_ALLOW_PLAINTEXT opt-in — use LDAPS (default) or StartTLS"
                | Plaintext ->
                    return
                        Warning
                            "LDAP is bound in plaintext — service-account and end-user credentials cross the network unencrypted. Use LDAPS (default) or StartTLS in production."
                | Ldaps
                | StartTls ->
                    // 3. Certificate-validation posture.
                    match config.CertificateValidation with
                    | AllowUntrusted ->
                        return
                            Warning
                                "LDAP server-certificate validation is disabled (AllowUntrusted) — the channel is encrypted but not authenticated, so it is vulnerable to a man-in-the-middle. Use strict validation (optionally a pinned thumbprint) in production."
                    | Strict _ ->
                        // 4. Reachability — transient, so a Warning.
                        match! reachable config.Host config.Port timeout with
                        | Result.Ok() -> return Ok
                        | Result.Error msg ->
                            return Warning(sprintf "LDAP directory not reachable at preflight: %s" msg)
        }

/// Construct a validator over an explicit config (tests / advanced
/// callers). `plaintextOptedIn` reflects the `TOOLUP_LDAP_ALLOW_PLAINTEXT`
/// acknowledgement.
let create (config: LdapConfig) (plaintextOptedIn: bool) : IConfigValidator =
    Impl(config, plaintextOptedIn) :> IConfigValidator

/// Return a validator when LDAP auth is enabled (`TOOLUP_LDAP_AUTH`
/// truthy), reading the config from the `TOOLUP_LDAP_*` environment.
/// `None` when LDAP auth is not enabled — a deployment that doesn't use
/// it registers no validator (GP 13).
let tryFromEnv () : IConfigValidator option =
    if LdapConfig.enabledFromEnv () then
        Some(create (LdapConfig.fromEnv ()) (LdapConfig.plaintextAllowedFromEnv ()))
    else
        None