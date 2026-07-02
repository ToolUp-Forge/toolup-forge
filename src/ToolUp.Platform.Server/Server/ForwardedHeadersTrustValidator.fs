module ToolUp.Platform.ForwardedHeadersTrustValidator

open System
open ToolUp.Platform

// ─── Phase 6l.K / Phase 325 — forwarded-headers trust posture ───────
//
// `TrustForwardedHeaders = true` registers `app.UseForwardedHeaders()`.
// Pre-325 that always meant `KnownIPNetworks` / `KnownProxies` cleared
// — the SDK trusted `X-Forwarded-Proto`, `X-Forwarded-For`,
// `X-Forwarded-Host` from any peer. That's correct behind a
// TLS-terminating cloud load balancer (ALB / Cloudflare / nginx) where
// the proxy is the only hop and replaces the client's headers; in any
// other topology a caller rotating `X-Forwarded-For` bypasses IP rate
// limiting and poisons audit / access logs, and (with `RequireHttps =
// false`) a spoofed `X-Forwarded-Proto: https` flips `Request.IsHttps`
// — cookie-secure flags, OIDC `RedirectUri` generation, any TLS branch.
//
// Phase 325 adds the scoping Phase 6l.K's warning anticipated:
// `ServerConfig.TrustedProxyCidrs` populates
// `ForwardedHeadersOptions.KnownIPNetworks` so forwarded headers are
// honoured only from declared proxy networks, and this validator
// escalates the unscoped trust-any-peer posture from `Warning` to
// `Error` in auth-requiring modes (mirroring the
// `JobSchedulerInstanceValidator` Error-plus-escape-hatch shape,
// Phase 6l.F). Anonymous-only deployments keep the Phase 6l.K
// `Warning` — the rate-limit-bypass signal without a refusal.

/// Phase 325 — CIDR parsing for the trusted-proxy allowlist. Strict:
/// an entry must be an IPv4/IPv6 network in `<base-address>/<prefix>`
/// form with the host bits zero (`System.Net.IPNetwork.TryParse`
/// semantics). Shared by this preflight validator (refuses startup
/// before the pipeline is built) and by
/// `ConfigurePipeline.buildForwardedHeadersOptions` (fail-loud
/// backstop for `SkipPreflight` deployments).
module Cidr =
    /// Parse one CIDR entry; `Error` carries the offending entry verbatim.
    let parse (entry: string) : Result<Net.IPNetwork, string> =
        match Net.IPNetwork.TryParse entry with
        | true, network ->
            // .NET's `TryParse` silently normalises non-zero host bits
            // (`10.0.0.1/8` → `10.0.0.0/8`) — a wider trust grant than
            // the operator wrote down. Reject the entry instead of
            // widening it: the written base address must equal the
            // parsed network's base address (byte equality, so IPv6
            // case / zero-compression variants still pass).
            let addressPart = (entry.Split '/')[0]

            match Net.IPAddress.TryParse(addressPart.Trim()) with
            | true, address when address.Equals network.BaseAddress -> Ok network
            | _ -> Error entry
        | false, _ -> Error entry

    /// Parse the whole allowlist; `Error` carries every malformed entry.
    let parseAll (entries: string list) : Result<Net.IPNetwork list, string list> =
        let parsed = entries |> List.map parse

        let bad =
            parsed
            |> List.choose (function
                | Error e -> Some e
                | Ok _ -> None)

        match bad with
        | [] ->
            parsed
            |> List.choose (function
                | Ok n -> Some n
                | Error _ -> None)
            |> Ok
        | bad -> Error bad

    /// The shared malformed-entry message — the validator returns it as a
    /// preflight `Error`; the pipeline's options builder throws it.
    let malformedMessage (bad: string list) : string =
        sprintf
            "ServerConfig.TrustedProxyCidrs (TOOLUP_TRUSTED_PROXY_CIDRS) contains malformed CIDR entr%s: %s. Each entry must be an IPv4/IPv6 network in CIDR form with host bits zero (e.g. 10.0.0.0/8, 192.168.1.0/24, 2001:db8::/32)."
            (if List.length bad = 1 then "y" else "ies")
            (bad |> List.map (sprintf "'%s'") |> String.concat ", ")

// Opened AFTER the `Cidr` module — `ValidationResult`'s `Ok` / `Error`
// cases would otherwise shadow FSharp.Core's `Result` cases inside it.
open ToolUp.Platform.ConfigValidation

/// Phase 6l.K + Phase 325 — config validator over the forwarded-headers
/// trust posture. `TrustForwardedHeaders = false` → `Ok`. A populated
/// `TrustedProxyCidrs` → `Ok` (trust is scoped by the pipeline); a
/// malformed entry → `Error`. The unscoped trust-any-peer posture
/// (empty allowlist) is an `Error` in auth-requiring modes unless the
/// operator sets `AcceptForwardedHeadersFromAnyProxy`, and remains the
/// Phase 6l.K `Warning` in anonymous-only modes.
type ForwardedHeadersTrustValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "forwarded-headers-trust"
        member _.Timeout = timeout

        member _.Validate() = async {
            if not config.TrustForwardedHeaders then
                // No forwarded-headers middleware registered — no spoof surface.
                return Ok
            else
                match Cidr.parseAll config.TrustedProxyCidrs with
                | Result.Error bad ->
                    // Malformed allowlist entries can never be intended —
                    // fail preflight before the pipeline throws the same
                    // message at `UseForwardedHeaders` registration.
                    return Error(Cidr.malformedMessage bad)
                | Result.Ok(_ :: _) ->
                    // Scoped trust: the pipeline populates
                    // `ForwardedHeadersOptions.KnownIPNetworks` from the
                    // allowlist, so X-Forwarded-* from an out-of-range peer
                    // is ignored. Nothing to warn about.
                    return Ok
                | Result.Ok [] when config.AcceptForwardedHeadersFromAnyProxy ->
                    // Operator attestation: a single trusted proxy that
                    // strips client-supplied X-Forwarded-* headers fronts
                    // every request path. Same silencing contract as the
                    // other `Accept*` escape hatches.
                    return Ok
                | Result.Ok [] ->
                    // Trust-any-peer posture. The X-Forwarded-For spoof
                    // (rate-limit bypass + audit/access-log poisoning)
                    // exists in every mode — including anonymous
                    // internet-facing deployments — and persists even when
                    // RequireHttps = true. The X-Forwarded-Proto / IsHttps
                    // spoof is the additional consequence specific to
                    // RequireHttps = false.
                    let httpsClause =
                        if config.RequireHttps then
                            ""
                        else
                            " With RequireHttps = false, a plain-HTTP request carrying X-Forwarded-Proto: https also makes Request.IsHttps return true, fooling cookie-secure flags / OIDC RedirectUri / any TLS branch — set TOOLUP_REQUIRE_HTTPS=1 if a TLS terminator runs upstream."

                    let remedy =
                        " Declare your TLS terminator's network(s) in TOOLUP_TRUSTED_PROXY_CIDRS (comma-separated CIDRs, e.g. 10.0.0.0/8) so forwarded headers are trusted only from those peers, or set ServerConfig.AcceptForwardedHeadersFromAnyProxy = true (TOOLUP_ACCEPT_FORWARDED_HEADERS_FROM_ANY_PROXY=1) if a single trusted proxy that strips client-supplied X-Forwarded-* headers fronts every request path."

                    let message =
                        sprintf
                            "ServerConfig.Surfaces = %s + TrustForwardedHeaders = true, but TrustedProxyCidrs is empty and RequireHttps = %b — KnownIPNetworks / KnownProxies are cleared, so the SDK trusts X-Forwarded-For / X-Forwarded-Proto from ANY peer, not only your proxy. A caller rotating X-Forwarded-For bypasses IP rate limiting and poisons audit / access logs (rate limiting keys on the post-rewrite RemoteIpAddress); this applies in every mode, including anonymous internet-facing deployments.%s%s"
                            (DeploymentConfig.surfacesLabel config)
                            config.RequireHttps
                            httpsClause
                            remedy

                    // Phase 325 escalation: in an auth-requiring mode the
                    // unscoped posture refuses startup (spoofed headers feed
                    // identity-adjacent branches); anonymous-only deployments
                    // keep the Phase 6l.K warning.
                    if DeploymentConfig.requiresAnyAuth config then
                        return Error message
                    else
                        return Warning message
        }