module ToolUp.Platform.Tests.InProcess.SecurityHardeningTests

open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.SurfaceEnforcement

let private contributor (sources: CspSourceDirective list) =
    { new ICspContributor with
        member _.RequiredSources = sources
    }

let private ctxFor (method: string) (path: string) =
    let c = DefaultHttpContext()
    c.Request.Method <- method
    c.Request.Path <- PathString(path)
    c :> HttpContext

[<Tests>]
let tests =
    testList "Phase 9j — security hardening" [

        test "NoSecurityHardening → empty policy (default deployment unchanged)" {
            Expect.equal (SecurityHardening.buildPolicy NoSecurityHardening []) "" "no header stamped"
        }

        test "Default policy carries the conservative baseline" {
            let p = SecurityHardening.buildPolicy DefaultSecurityHardening []
            Expect.stringContains p "default-src 'self'" "default-src"
            Expect.stringContains p "script-src 'self'" "script-src self (Vite-bundled, no unsafe-inline)"
            Expect.stringContains p "style-src 'self' 'unsafe-inline'" "style-src keeps unsafe-inline under Default"
            Expect.stringContains p "connect-src 'self'" "connect-src"
            Expect.stringContains p "frame-ancestors 'none'" "clickjacking lockdown"
            Expect.stringContains p "form-action 'self'" "form-action"
            Expect.stringContains p "base-uri 'self'" "base-uri"
            Expect.stringContains p "frame-src 'none'" "no contributed frame origins → locked"
            Expect.isFalse (p.Contains "object-src") "object-src is Strict-only"
            Expect.isFalse (p.Contains "upgrade-insecure-requests") "upgrade is Strict-only"
        }

        test "Strict drops unsafe-inline and adds object-src / upgrade" {
            let p = SecurityHardening.buildPolicy StrictSecurityHardening []
            Expect.isFalse (p.Contains "'unsafe-inline'") "strict removes unsafe-inline entirely"
            Expect.stringContains p "style-src 'self'" "style-src tightened to self"
            Expect.stringContains p "object-src 'none'" "object-src none"
            Expect.stringContains p "upgrade-insecure-requests" "upgrade-insecure-requests"
        }

        test "Contributed sources fold into the correct directives" {
            let p =
                SecurityHardening.buildPolicy DefaultSecurityHardening [
                    ConnectSrc "https://issuer.example.com"
                    ScriptSrc "https://cdn.example.com"
                    FrameSrc "https://embed.example.com"
                ]

            Expect.stringContains p "connect-src 'self' https://issuer.example.com" "issuer → connect-src"
            Expect.stringContains p "script-src 'self' https://cdn.example.com" "cdn → script-src"
            Expect.stringContains p "frame-src 'self' https://embed.example.com" "frame host promotes off 'none'"
        }

        test "Duplicate contributed origins are deduped" {
            let p =
                SecurityHardening.buildPolicy DefaultSecurityHardening [
                    ConnectSrc "https://a.example"
                    ConnectSrc "https://a.example"
                ]

            Expect.equal
                (p.IndexOf "https://a.example")
                (p.LastIndexOf "https://a.example")
                "origin appears exactly once"
        }

        test "OidcIssuerCspContributor: inert when env unset, contributes when set" {
            let v = OidcIssuerCspContributor() :> ICspContributor
            System.Environment.SetEnvironmentVariable(OidcIssuerCspContributor.EnvVar, null)
            Expect.equal v.RequiredSources [] "unset → no directive"

            System.Environment.SetEnvironmentVariable(OidcIssuerCspContributor.EnvVar, "https://issuer.example.org")

            try
                Expect.equal v.RequiredSources [ ConnectSrc "https://issuer.example.org" ] "set → connect-src issuer"
            finally
                System.Environment.SetEnvironmentVariable(OidcIssuerCspContributor.EnvVar, null)
        }

        test "AiProviderCspContributor adds both provider API hosts" {
            let v = AiProviderCspContributor() :> ICspContributor

            Expect.equal
                v.RequiredSources
                [ ConnectSrc "https://api.anthropic.com"; ConnectSrc "https://api.openai.com" ]
                "anthropic + openai connect-src"
        }

        test "aggregate: NoSecurityHardening short-circuits regardless of contributors" {
            let services = ServiceCollection()

            services.AddSingleton<ICspContributor>(contributor [ ConnectSrc "https://x.example" ])
            |> ignore

            let resolved =
                SecurityHardening.aggregate services {
                    ServerConfig.defaults with
                        SecurityHardening = NoSecurityHardening
                }

            Expect.equal resolved.Header "" "default deployment gets no header"
        }

        test "aggregate: folds every registered ICspContributor" {
            let services = ServiceCollection()

            services.AddSingleton<ICspContributor>(contributor [ ConnectSrc "https://one.example" ])
            |> ignore

            services.AddSingleton<ICspContributor>(contributor [ ScriptSrc "https://two.example" ])
            |> ignore

            let resolved =
                SecurityHardening.aggregate services {
                    ServerConfig.defaults with
                        SecurityHardening = DefaultSecurityHardening
                }

            Expect.stringContains resolved.Header "https://one.example" "first contributor folded"
            Expect.stringContains resolved.Header "https://two.example" "second contributor folded"
        }

        test "ResolvedCspPolicy.empty is the empty header" {
            Expect.equal SecurityHardening.ResolvedCspPolicy.empty.Header "" "empty header"
        }

        test "CSRF gate: state-changing /api/* requires a token in auth-requiring deployments" {
            // Phase 66 Stream A.6 — `requiresValidation` reads the
            // route's `SurfaceRequirement` and skips CSRF when the
            // admit set contains `AnonymousKind` or `ClaimBearerKind`
            // (no logged-in session to bind a nonce against). On an
            // auth-requiring deployment (`Surfaces = Surfaces.individual`) the
            // strict `userOrTeam` default applies to unregistered
            // routes, so POST /api/Foo/Bar still requires CSRF.
            // `ServerConfig.defaults` is `Surfaces = Surfaces.anonymous`, which
            // maps every `/api/` path to `public_` via the bridge
            // — that's the new "anonymous deployment skips CSRF"
            // semantics, exercised by the prefix-exempt tests below.
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
            }

            let registry = SurfaceRequirementRegistry.fromServerConfig cfg

            Expect.isTrue (Csrf.requiresValidation registry (ctxFor "POST" "/api/Foo/Bar")) "POST /api → guarded"
        }

        test "CSRF gate: anonymous-only deployment skips state-changing /api/* (no session to bind)" {
            // The flipside — `Surfaces = Surfaces.anonymous` deployments admit
            // every `/api/` path with `public_`, so the matrix
            // says "anonymous can reach this" and the CSRF gate
            // correctly skips (the threat model is session-bound).
            let registry = SurfaceRequirementRegistry.fromServerConfig ServerConfig.defaults

            Expect.isFalse
                (Csrf.requiresValidation registry (ctxFor "POST" "/api/Foo/Bar"))
                "POST /api → not guarded in anonymous deployments"
        }

        test "CSRF gate: safe methods are exempt" {
            let registry = SurfaceRequirementRegistry.fromServerConfig ServerConfig.defaults

            Expect.isFalse (Csrf.requiresValidation registry (ctxFor "GET" "/api/Foo/Bar")) "GET → not guarded"
        }

        test "CSRF gate: the token endpoint itself is exempt" {
            let registry = SurfaceRequirementRegistry.fromServerConfig ServerConfig.defaults

            Expect.isFalse
                (Csrf.requiresValidation registry (ctxFor "POST" Csrf.TokenPath))
                "token endpoint exempt (else the client could never bootstrap)"
        }

        test "CSRF gate: non-/api paths are exempt" {
            let registry = SurfaceRequirementRegistry.fromServerConfig ServerConfig.defaults

            Expect.isFalse (Csrf.requiresValidation registry (ctxFor "POST" "/somewhere")) "non /api/ exempt"
        }

        test "CSRF gate: anonymous share-token prefixes are exempt" {
            let cfg = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.anonymous
            }

            let registry = SurfaceRequirementRegistry.fromServerConfig cfg

            Expect.isFalse
                (Csrf.requiresValidation registry (ctxFor "POST" "/api/public/forms/abc"))
                "share-token-gated public submit has no session to bind a token to"
        }

        test "CSRF gate: peer-bearer prefixes are exempt" {
            let cfg = {
                ServerConfig.defaults with
                    PeerRoutePrefixes = [ "/api/peer/" ]
            }

            let registry = SurfaceRequirementRegistry.fromServerConfig cfg

            Expect.isFalse
                (Csrf.requiresValidation registry (ctxFor "DELETE" "/api/peer/echo"))
                "the bearer IS the authentication, not cookie/session"
        }
    ]