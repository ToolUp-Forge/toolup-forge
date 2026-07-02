module ToolUp.Platform.Tests.InProcess.ForwardedHeadersTrustTests

// Phase 325 — trusted-proxy CIDR allowlist + auth-mode escalation.
// Covers the four acceptance arms: empty-allowlist-with-trust in an
// auth mode fails preflight; a populated CIDR list passes and only
// in-range peers are trusted (via the pipeline's
// `ForwardedHeadersOptions` builder); the
// `AcceptForwardedHeadersFromAnyProxy` escape hatch passes; a
// malformed CIDR fails at startup (preflight Error + builder throw).

open System.Net
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

let private cfg (surfaces: SurfaceProfile list) (cidrs: string list) (escapeHatch: bool) : ServerConfig = {
    ServerConfig.defaults with
        Surfaces = surfaces
        TrustForwardedHeaders = true
        TrustedProxyCidrs = cidrs
        AcceptForwardedHeadersFromAnyProxy = escapeHatch
}

let private validate (config: ServerConfig) : ValidationResult =
    let v =
        ForwardedHeadersTrustValidator.ForwardedHeadersTrustValidator(config) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

let private trusts (opts: Microsoft.AspNetCore.Builder.ForwardedHeadersOptions) (ip: string) : bool =
    let address = IPAddress.Parse ip
    opts.KnownIPNetworks |> Seq.exists _.Contains(address)

[<Tests>]
let tests =
    testList "Phase 325 — forwarded-headers CIDR trust allowlist" [

        test "GP 11 — defaults preserve the pre-325 shape (empty allowlist, escape hatch off)" {
            Expect.equal ServerConfig.defaults.TrustedProxyCidrs [] "allowlist defaults empty"

            Expect.isFalse ServerConfig.defaults.AcceptForwardedHeadersFromAnyProxy "escape hatch defaults off"
        }

        test "Auth mode + empty allowlist → preflight Error naming both remedies" {
            let result = validate (cfg Surfaces.individual [] false)

            match result with
            | Error msg ->
                Expect.stringContains msg "TOOLUP_TRUSTED_PROXY_CIDRS" "names the allowlist env var"

                Expect.stringContains msg "AcceptForwardedHeadersFromAnyProxy" "names the escape hatch"

                Expect.stringContains
                    msg
                    "TOOLUP_ACCEPT_FORWARDED_HEADERS_FROM_ANY_PROXY"
                    "names the escape-hatch env var"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Auth mode + populated allowlist → Ok (trust is scoped by the pipeline)" {
            let result = validate (cfg Surfaces.individual [ "10.0.0.0/8" ] false)
            Expect.equal result Ok "a declared proxy CIDR passes preflight"
        }

        test "Auth mode + empty allowlist + escape hatch → Ok (operator attestation)" {
            let result = validate (cfg Surfaces.individual [] true)
            Expect.equal result Ok "the explicit escape hatch preserves the trust-all posture"
        }

        test "Anonymous-only mode + empty allowlist → Warning, not Error (no escalation)" {
            let result = validate (cfg Surfaces.anonymous [] false)

            match result with
            | Warning msg ->
                Expect.stringContains msg "TOOLUP_TRUSTED_PROXY_CIDRS" "warning still names the allowlist fix"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Malformed CIDR entry → preflight Error naming the entry" {
            let result = validate (cfg Surfaces.individual [ "10.0.0.0/8"; "not-a-cidr" ] false)

            match result with
            | Error msg ->
                Expect.stringContains msg "'not-a-cidr'" "names the malformed entry"

                Expect.isFalse (msg.Contains "'10.0.0.0/8'") "does not implicate the valid entry"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Non-zero host bits → preflight Error (strict base-address parse)" {
            // 10.0.0.1/8 is a host address, not a network — silently
            // widening it to 10.0.0.0/8 would trust more peers than the
            // operator wrote down.
            let result = validate (cfg Surfaces.individual [ "10.0.0.1/8" ] false)

            match result with
            | Error msg -> Expect.stringContains msg "'10.0.0.1/8'" "names the host-bits entry"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Escape hatch does not mask a malformed allowlist" {
            let result = validate (cfg Surfaces.individual [ "bogus" ] true)

            match result with
            | Error msg -> Expect.stringContains msg "'bogus'" "malformed entries can never be intended"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Pipeline options — empty allowlist preserves the pre-325 trust-any-peer shape" {
            let opts =
                ConfigurePipeline.buildForwardedHeadersOptions (cfg Surfaces.anonymous [] false)

            Expect.equal opts.KnownIPNetworks.Count 0 "known networks cleared"
            Expect.equal opts.KnownProxies.Count 0 "known proxies cleared"

            let expected =
                Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                ||| Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto

            Expect.equal opts.ForwardedHeaders expected "same header set as pre-325"
        }

        test "Pipeline options — populated allowlist trusts only in-range peers" {
            let opts =
                ConfigurePipeline.buildForwardedHeadersOptions (
                    cfg Surfaces.individual [ "10.0.0.0/8"; "192.168.1.0/24" ] false
                )

            Expect.equal opts.KnownIPNetworks.Count 2 "one known network per CIDR entry"
            Expect.isTrue (trusts opts "10.1.2.3") "in-range peer (10.0.0.0/8) is trusted"
            Expect.isTrue (trusts opts "192.168.1.7") "in-range peer (192.168.1.0/24) is trusted"
            Expect.isFalse (trusts opts "11.0.0.1") "out-of-range peer is not trusted"
            Expect.isFalse (trusts opts "192.168.2.7") "adjacent /24 is not trusted"
        }

        test "Pipeline options — IPv6 CIDR entries are supported" {
            let opts =
                ConfigurePipeline.buildForwardedHeadersOptions (cfg Surfaces.individual [ "2001:db8::/32" ] false)

            Expect.isTrue (trusts opts "2001:db8::1") "in-range IPv6 peer is trusted"
            Expect.isFalse (trusts opts "2001:db9::1") "out-of-range IPv6 peer is not trusted"
        }

        test "Pipeline options — malformed CIDR fails loud at startup (SkipPreflight backstop)" {
            let thrown =
                try
                    ConfigurePipeline.buildForwardedHeadersOptions (cfg Surfaces.individual [ "not-a-cidr" ] false)
                    |> ignore

                    None
                with ex ->
                    Some ex.Message

            match thrown with
            | Some msg ->
                Expect.stringContains msg "'not-a-cidr'" "names the malformed entry"

                Expect.stringContains msg "TOOLUP_TRUSTED_PROXY_CIDRS" "names the env var"
            | None -> failtest "expected buildForwardedHeadersOptions to throw on a malformed CIDR"
        }
    ]