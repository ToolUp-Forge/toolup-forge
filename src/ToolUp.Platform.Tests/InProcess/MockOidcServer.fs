// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.MockOidcServer

// ─── In-process OIDC mock issuer (Phase 3b deferred follow-up) ────────
//
// A real Kestrel server (not an HttpMessageHandler stub) exposing the
// four endpoints a minimal OIDC issuer needs:
//
//   GET  /.well-known/openid-configuration   discovery document
//   GET  /jwks                               RS256 public key (JWKS)
//   GET  /authorize                          deterministic auth code
//   POST /token                              RS256-signed token bundle
//
// Why a real socket when `AuthProviderTests` already validates
// `OidcAuthProvider` through a `StubHttpMessageHandler`: the stub
// proves the provider's JWT/JWKS logic; this proves the same provider
// over a real HTTP stack + real OIDC discovery, and gives CI a live
// issuer a future browser/headless harness can drive the full PKCE
// `/authorize → code → /token` exchange against. The two are
// complementary, not redundant.
//
// CI-safety: binds `http://127.0.0.1:0` so the OS assigns a free port
// (no fixed-port clash across parallel test processes). Claims are
// seeded deterministically from `MockOidcConfig` so callers can assert
// exact `sub` / `email` / `name`. A fresh RSA key per instance backs
// the JWKS it publishes — "fixed key" in the sense the phase intends:
// stable for the issuer's lifetime, self-published, never rotated.

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

/// Deterministic identity the issuer mints into every token. Assert
/// against these in the contract fixture.
type MockOidcConfig = {
    Subject: string
    Email: string
    Name: string
    /// Lifetime stamped into a minted token's `exp` claim.
    TokenLifetime: TimeSpan
    /// When `true`, `/token` returns an **opaque** `access_token` — a
    /// random non-JWT string, the way Google (and Auth0 without a
    /// configured API audience) actually behave — while still
    /// returning a properly-signed RS256 `id_token`. Models the
    /// provider class that motivates the id_token bearer strategy:
    /// sign-in succeeds, and an access-token bearer then has nothing
    /// the server can validate.
    ///
    /// `false` (the default) keeps the historical behaviour where both
    /// fields carry the same signed JWT, so every pre-existing fixture
    /// is untouched.
    OpaqueAccessTokens: bool
    /// The `aud` claim minted into the `id_token`. `None` (the default)
    /// omits `aud` entirely, matching the historical mock. Set it to
    /// the client id to exercise the id_token-as-bearer path, whose
    /// whole audience contract is `aud` = client id.
    IdTokenAudience: string option
    /// When `false`, a `refresh_token` grant returns **no** `id_token`,
    /// modelling an issuer that reissues only the access token. Drives
    /// the negative leg of refresh coherence: under the id_token
    /// strategy the session cannot renew its bearer and must fail
    /// rather than silently keep or swap one.
    ReissueIdTokenOnRefresh: bool
}

module MockOidcConfig =
    let defaults = {
        Subject = "mock-user"
        Email = "mock-user@example.test"
        Name = "Mock User"
        TokenLifetime = TimeSpan.FromMinutes 30.0
        OpaqueAccessTokens = false
        IdTokenAudience = None
        ReissueIdTokenOnRefresh = true
    }

    /// An issuer shaped like Google: opaque access tokens, a signed
    /// id_token addressed to `clientId`, and an id_token reissued on
    /// every refresh.
    let opaqueAccessTokenIssuer (clientId: string) = {
        defaults with
            OpaqueAccessTokens = true
            IdTokenAudience = Some clientId
    }

let private b64url (bytes: byte[]) =
    Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

[<Literal>]
let private Kid = "mock-oidc-key"

[<Literal>]
let private AuthCode = "mock-auth-code"

type MockOidcServer(cfg: MockOidcConfig) =
    let rsa = RSA.Create 2048
    let mutable issuerUrl = ""

    let jwksJson () =
        let p = rsa.ExportParameters false
        let n = b64url p.Modulus
        let e = b64url p.Exponent

        $"""{{"keys":[{{"kty":"RSA","use":"sig","alg":"RS256","kid":"{Kid}","n":"{n}","e":"{e}"}}]}}"""

    let discoveryJson () =
        $"""{{"issuer":"{issuerUrl}","jwks_uri":"{issuerUrl}/jwks","authorization_endpoint":"{issuerUrl}/authorize","token_endpoint":"{issuerUrl}/token","end_session_endpoint":"{issuerUrl}/logout","id_token_signing_alg_values_supported":["RS256"],"response_types_supported":["code"],"subject_types_supported":["public"]}}"""

    /// RS256 JWT carrying the seeded claims. A negative `lifetime`
    /// yields an already-expired token (used for the expired-credential
    /// contract case). `audience` populates the `aud` claim when the
    /// config asks for one; `jti` makes successive mints textually
    /// distinct so a test can prove a refresh actually ROTATED the
    /// bearer rather than re-storing the same string.
    let mintTokenWith (lifetime: TimeSpan) (audience: string option) (jti: string option) =
        let now = DateTimeOffset.UtcNow
        let header = $"""{{"alg":"RS256","typ":"JWT","kid":"{Kid}"}}"""

        let payload =
            let d = Dictionary<string, obj>()
            d["sub"] <- box cfg.Subject
            d["name"] <- box cfg.Name
            d["email"] <- box cfg.Email
            d["iss"] <- box issuerUrl
            d["iat"] <- box (now.ToUnixTimeSeconds())
            d["exp"] <- box (now.Add(lifetime).ToUnixTimeSeconds())
            audience |> Option.iter (fun a -> d["aud"] <- box a)
            jti |> Option.iter (fun j -> d["jti"] <- box j)
            JsonSerializer.Serialize d

        let hB = b64url (Encoding.UTF8.GetBytes header)
        let pB = b64url (Encoding.UTF8.GetBytes payload)
        let msg = Encoding.UTF8.GetBytes $"{hB}.{pB}"
        let sg = rsa.SignData(msg, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        $"{hB}.{pB}.{b64url sg}"

    let mintToken (lifetime: TimeSpan) = mintTokenWith lifetime None None

    /// A random non-JWT string, the shape an opaque access token
    /// actually takes. Prefixed the way Google's are so a failure
    /// message reads recognisably.
    let mintOpaqueAccessToken () =
        $"ya29.{b64url (RandomNumberGenerator.GetBytes 24)}"

    let app =
        let builder = WebApplication.CreateBuilder()
        builder.Logging.ClearProviders() |> ignore
        builder.WebHost.UseUrls "http://127.0.0.1:0" |> ignore
        let a = builder.Build()

        a.MapGet(
            "/.well-known/openid-configuration",
            Func<HttpContext, Task>(fun ctx ->
                ctx.Response.ContentType <- "application/json"
                ctx.Response.WriteAsync(discoveryJson ()))
        )
        |> ignore

        a.MapGet(
            "/jwks",
            Func<HttpContext, Task>(fun ctx ->
                ctx.Response.ContentType <- "application/json"
                ctx.Response.WriteAsync(jwksJson ()))
        )
        |> ignore

        a.MapGet(
            "/authorize",
            Func<HttpContext, Task>(fun ctx ->
                let q = ctx.Request.Query
                let redirectUri = string q["redirect_uri"]
                let state = string q["state"]
                let sep = if redirectUri.Contains "?" then "&" else "?"
                ctx.Response.Redirect $"{redirectUri}{sep}code={AuthCode}&state={state}"
                Task.CompletedTask)
        )
        |> ignore

        a.MapPost(
            "/token",
            Func<HttpContext, Task>(fun ctx -> task {
                // The grant type decides whether this is the initial
                // code exchange or a refresh; the id_token-reissue
                // switch only applies to the latter.
                let! grantType = task {
                    if ctx.Request.HasFormContentType then
                        let! form = ctx.Request.ReadFormAsync()
                        return string form["grant_type"]
                    else
                        return ""
                }

                let isRefresh = grantType = "refresh_token"

                // A fresh `jti` per mint, so a refreshed id_token is
                // textually distinct from the one it replaces and a
                // test can assert rotation rather than mere presence.
                let idToken =
                    mintTokenWith cfg.TokenLifetime cfg.IdTokenAudience (Some(Guid.NewGuid().ToString "N"))

                let accessToken =
                    if cfg.OpaqueAccessTokens then
                        mintOpaqueAccessToken ()
                    else
                        // Historical shape: access_token and id_token
                        // are the same signed JWT.
                        idToken

                let body =
                    let d = Dictionary<string, obj>()
                    d["access_token"] <- box accessToken
                    d["token_type"] <- box "Bearer"
                    d["expires_in"] <- box (int cfg.TokenLifetime.TotalSeconds)
                    d["refresh_token"] <- box "mock-refresh-token"

                    if not isRefresh || cfg.ReissueIdTokenOnRefresh then
                        d["id_token"] <- box idToken

                    JsonSerializer.Serialize d

                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsync body
            })
        )
        |> ignore

        a

    /// Start Kestrel and resolve the OS-assigned base URL. Idempotent
    /// per instance — call once before minting / pointing a provider at
    /// `IssuerUrl`.
    member _.StartAsync() : Task = task {
        do! (app :> IHost).StartAsync()

        issuerUrl <-
            app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
            |> Seq.head
    }

    /// Base issuer URL (e.g. `http://127.0.0.1:54321`). Valid only
    /// after `StartAsync`. Feed this to `AuthConfig.Issuer` +
    /// `KeySource = JwksDiscovery issuerUrl`.
    member _.IssuerUrl = issuerUrl

    /// A currently-valid RS256 access token signed by the key this
    /// issuer's `/jwks` publishes.
    member _.MintAccessToken() = mintToken cfg.TokenLifetime

    /// An already-expired RS256 access token (otherwise well-formed and
    /// correctly signed) — exercises the expiry-rejection path.
    member _.MintExpiredToken() = mintToken (TimeSpan.FromMinutes -5.0)

    interface IDisposable with
        member _.Dispose() =
            (app :> IHost).StopAsync().GetAwaiter().GetResult()
            (app :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()
            rsa.Dispose()

/// Construct + start a mock issuer synchronously. Pair with `use` so
/// Kestrel is torn down when the test list completes.
let start (cfg: MockOidcConfig) : MockOidcServer =
    let server = new MockOidcServer(cfg)
    server.StartAsync().GetAwaiter().GetResult()
    server