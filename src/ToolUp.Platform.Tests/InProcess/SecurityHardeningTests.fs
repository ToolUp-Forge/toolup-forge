module ToolUp.Platform.Tests.InProcess.SecurityHardeningTests

open Expecto
open System.IO
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.SurfaceEnforcement

let private contributor (sources: CspSourceDirective list) =
    { new ICspContributor with
        member _.RequiredSources = sources
    }

/// Phase 156 — a minimal `IHttpResponseFeature` that records `OnStarting`
/// callbacks so a test can fire them and inspect the headers
/// `CspMiddleware` stamps (the default `DefaultHttpContext` response
/// feature drops `OnStarting` callbacks, so the middleware's header work
/// would otherwise be untestable in-process).
type private RecordingResponseFeature() =
    let mutable status = 200
    let mutable body: Stream = new MemoryStream() :> Stream
    let headers: IHeaderDictionary = HeaderDictionary()
    let callbacks = System.Collections.Generic.List<unit -> Task>()

    member _.FireOnStarting() =
        for cb in callbacks do
            cb().GetAwaiter().GetResult()

    interface IHttpResponseFeature with
        member _.StatusCode
            with get () = status
            and set v = status <- v

        member _.ReasonPhrase
            with get () = null
            and set _ = ()

        member _.Headers
            with get () = headers
            and set _ = ()

        member _.Body
            with get () = body
            and set v = body <- v

        member _.HasStarted = false

        member _.OnStarting(callback, state) =
            callbacks.Add(fun () -> callback.Invoke state)

        member _.OnCompleted(_callback, _state) = ()

let private nonceCtx (statusCode: int) =
    let ctx = DefaultHttpContext()
    let resp = RecordingResponseFeature()
    ctx.Features.Set<IHttpResponseFeature>(resp)
    ctx.Response.StatusCode <- statusCode
    ctx, resp

let private noOpNext = RequestDelegate(fun _ -> Task.CompletedTask)

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
            Expect.isFalse SecurityHardening.ResolvedCspPolicy.empty.NonceMode "empty policy is not nonce mode"
        }

        // ── Phase 156 — CSP source modes (nonce / hash / static) ──────

        test "Phase 156 — buildPolicyWith with empty extras == buildPolicy (GP 11 byte-for-byte)" {
            let contributed = [ ConnectSrc "https://a.example"; ScriptSrc "https://b.example" ]

            Expect.equal
                (SecurityHardening.buildPolicyWith DefaultSecurityHardening contributed [] [])
                (SecurityHardening.buildPolicy DefaultSecurityHardening contributed)
                "no extra sources → byte-for-byte the static header"
        }

        test "Phase 156 — inlineScriptHash is deterministic base64 of the sha256 (32 bytes → 44 chars)" {
            let h1 = SecurityHardening.inlineScriptHash "console.log('x')"
            let h2 = SecurityHardening.inlineScriptHash "console.log('x')"
            Expect.equal h1 h2 "same input → same hash"
            Expect.equal h1.Length 44 "base64 of 32 bytes is 44 chars"

            Expect.notEqual
                h1
                (SecurityHardening.inlineScriptHash "console.log('y')")
                "different inline bodies hash differently"
        }

        test "Phase 156 — aggregate with no source mode registered is byte-for-byte pre-156 (StaticCsp default)" {
            let services = ServiceCollection()

            services.AddSingleton<ICspContributor>(contributor [ ConnectSrc "https://issuer.example" ])
            |> ignore

            let resolved =
                SecurityHardening.aggregate services {
                    ServerConfig.defaults with
                        SecurityHardening = DefaultSecurityHardening
                }

            Expect.equal
                resolved.Header
                (SecurityHardening.buildPolicy DefaultSecurityHardening [ ConnectSrc "https://issuer.example" ])
                "absent CspSourceMode → the pre-156 static header"

            Expect.isFalse resolved.NonceMode "static mode does not flag NonceMode"
        }

        test "Phase 156 — NonceCsp bakes the nonce placeholder into script-src AND style-src" {
            let services = ServiceCollection()

            services.AddSingleton<SecurityHardening.CspSourceMode>(SecurityHardening.NonceCsp)
            |> ignore

            let resolved =
                SecurityHardening.aggregate services {
                    ServerConfig.defaults with
                        SecurityHardening = DefaultSecurityHardening
                }

            Expect.isTrue resolved.NonceMode "nonce mode flagged for CspMiddleware substitution"
            let token = sprintf "'nonce-%s'" SecurityHardening.noncePlaceholder

            Expect.stringContains
                resolved.Header
                (sprintf "script-src 'self' %s" token)
                "script-src carries the nonce placeholder"

            Expect.notEqual
                (resolved.Header.IndexOf token)
                (resolved.Header.LastIndexOf token)
                "the nonce placeholder appears in both script-src and style-src"
        }

        test "Phase 156 — HashCsp folds sha256 sources into script-src and the header is byte-stable" {
            let services = ServiceCollection()
            let inlineBody = "console.log('hello')"

            services.AddSingleton<SecurityHardening.CspSourceMode>(SecurityHardening.HashCsp [ inlineBody ])
            |> ignore

            let cfg = {
                ServerConfig.defaults with
                    SecurityHardening = DefaultSecurityHardening
            }

            let r1 = SecurityHardening.aggregate services cfg
            let r2 = SecurityHardening.aggregate services cfg

            Expect.isFalse r1.NonceMode "hash mode is byte-stable, not nonce mode"
            Expect.equal r1.Header r2.Header "hash header is identical across requests (survives caching + 304)"
            let expected = sprintf "'sha256-%s'" (SecurityHardening.inlineScriptHash inlineBody)

            Expect.stringContains
                r1.Header
                (sprintf "script-src 'self' %s" expected)
                "declared inline-script hash folded into script-src"
        }

        test "Phase 156 — NoSecurityHardening short-circuits regardless of source mode" {
            let services = ServiceCollection()

            services.AddSingleton<SecurityHardening.CspSourceMode>(SecurityHardening.NonceCsp)
            |> ignore

            let resolved =
                SecurityHardening.aggregate services {
                    ServerConfig.defaults with
                        SecurityHardening = NoSecurityHardening
                }

            Expect.equal resolved.Header "" "no hardening → no header even in nonce mode"
            Expect.isFalse resolved.NonceMode "no hardening → not nonce mode"
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

[<Tests>]
let cspMiddlewareTests =
    testList "Phase 156 — CspMiddleware nonce / hash source modes" [

        test "nonce mode: stamps a real nonce header + stashes the same nonce for layouts, unique per request" {
            let policy = {
                SecurityHardening.ResolvedCspPolicy.empty with
                    Header = sprintf "script-src 'self' 'nonce-%s'" SecurityHardening.noncePlaceholder
                    NonceMode = true
            }

            let run () =
                let ctx, resp = nonceCtx 200
                let mw = CspMiddleware(noOpNext, policy)
                mw.InvokeAsync(ctx).GetAwaiter().GetResult()
                resp.FireOnStarting()
                Csp.requestNonce ctx, ctx.Response.Headers.["Content-Security-Policy"].ToString()

            let n1, h1 = run ()
            let n2, _ = run ()

            Expect.isSome n1 "nonce stashed on HttpContext.Items for the layout accessor"

            Expect.stringContains
                h1
                (sprintf "'nonce-%s'" n1.Value)
                "the header carries the SAME nonce the layout reads"

            Expect.isFalse (h1.Contains SecurityHardening.noncePlaceholder) "the placeholder is fully substituted"
            Expect.isFalse (h1.Contains "'unsafe-inline'") "the nonce policy carries no 'unsafe-inline'"
            Expect.notEqual n1 n2 "a fresh, unique nonce per request"
        }

        test "nonce mode: suppresses the CSP header on a 304 (the cached body's nonce stays authoritative)" {
            let policy = {
                SecurityHardening.ResolvedCspPolicy.empty with
                    Header = sprintf "script-src 'self' 'nonce-%s'" SecurityHardening.noncePlaceholder
                    NonceMode = true
            }

            let ctx, resp = nonceCtx 304
            let mw = CspMiddleware(noOpNext, policy)
            mw.InvokeAsync(ctx).GetAwaiter().GetResult()
            resp.FireOnStarting()

            Expect.isFalse
                (ctx.Response.Headers.ContainsKey "Content-Security-Policy")
                "no fresh-nonce header stamped on a 304 — it would mismatch the cached body's nonce"
        }

        test "static / hash mode: byte-stable header is stamped, and is safe even on a 304" {
            let policy = {
                SecurityHardening.ResolvedCspPolicy.empty with
                    Header = "script-src 'self' 'sha256-abc'"
                    NonceMode = false
            }

            let ctx, resp = nonceCtx 304
            let mw = CspMiddleware(noOpNext, policy)
            mw.InvokeAsync(ctx).GetAwaiter().GetResult()
            resp.FireOnStarting()

            Expect.equal
                (ctx.Response.Headers.["Content-Security-Policy"].ToString())
                "script-src 'self' 'sha256-abc'"
                "static/hash header is byte-stable and safe to stamp on every status code"

            Expect.isNone (Csp.requestNonce ctx) "no per-request nonce work in static / hash mode (GP 13)"
        }

        test "per-route override wins: a handler-set CSP is not overwritten" {
            let policy = {
                SecurityHardening.ResolvedCspPolicy.empty with
                    Header = "script-src 'self' 'sha256-abc'"
                    NonceMode = false
            }

            let ctx, resp = nonceCtx 200

            ctx.Response.Headers.["Content-Security-Policy"] <-
                Microsoft.Extensions.Primitives.StringValues "default-src 'none'"

            let mw = CspMiddleware(noOpNext, policy)
            mw.InvokeAsync(ctx).GetAwaiter().GetResult()
            resp.FireOnStarting()

            Expect.equal
                (ctx.Response.Headers.["Content-Security-Policy"].ToString())
                "default-src 'none'"
                "an already-present CSP header wins (per-route override preserved)"
        }
    ]