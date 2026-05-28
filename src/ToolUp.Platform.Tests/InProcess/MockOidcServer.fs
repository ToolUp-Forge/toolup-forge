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
}

module MockOidcConfig =
    let defaults = {
        Subject = "mock-user"
        Email = "mock-user@example.test"
        Name = "Mock User"
        TokenLifetime = TimeSpan.FromMinutes 30.0
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
    /// contract case).
    let mintToken (lifetime: TimeSpan) =
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
            JsonSerializer.Serialize d

        let hB = b64url (Encoding.UTF8.GetBytes header)
        let pB = b64url (Encoding.UTF8.GetBytes payload)
        let msg = Encoding.UTF8.GetBytes $"{hB}.{pB}"
        let sg = rsa.SignData(msg, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        $"{hB}.{pB}.{b64url sg}"

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
            Func<HttpContext, Task>(fun ctx ->
                let token = mintToken cfg.TokenLifetime

                let body =
                    let d = Dictionary<string, obj>()
                    d["access_token"] <- box token
                    d["token_type"] <- box "Bearer"
                    d["expires_in"] <- box (int cfg.TokenLifetime.TotalSeconds)
                    d["refresh_token"] <- box "mock-refresh-token"
                    d["id_token"] <- box token
                    JsonSerializer.Serialize d

                ctx.Response.ContentType <- "application/json"
                ctx.Response.WriteAsync body)
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