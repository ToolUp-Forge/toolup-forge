// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.OidcSignInContractTests

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.AuthProviders
open ToolUp.AuthProviders.Oidc.OidcTypes
open ToolUp.AuthProviders.Oidc.OidcIdTokenValidator
open ToolUp.AuthProviders.Oidc.OidcDiscovery
open ToolUp.AuthProviders.Oidc.OidcClient
open ToolUp.Platform.Tests.InProcess.MockOidcServer

// ─── OIDC sign-in flow contract test (Phase 3b deferred follow-up) ──
//
// Drives the full OIDC authorisation-code chain against the in-process
// `MockOidcServer`:
//
//   1. GET /authorize?redirect_uri=...&state=... → 302 with `code` +
//      `state` echoed back.
//   2. POST /token (code exchange) → access_token + id_token bundle.
//   3. `OidcAuthProvider.ValidateRequest` over a bearer context using
//      the returned access_token → `Ok` carrying the seeded `sub`.
//
// The existing `oidcMockIssuerContract` in `AuthProviderTests.fs`
// (lines 671–703) exercises the "given a minted token, validate it"
// half of the contract — `IAuthProviderContract` over a bearer
// fixture. This test exercises the *upstream* half — `/authorize` →
// `/token` → token-in-hand — that a real browser / headless harness
// would drive. Together the two cover the full sign-in chain.
//
// **Not a PKCE proof-of-possession test.** The mock issuer accepts
// the bare code unconditionally; that matches its role as a CI
// fixture, not a security-grade reference issuer. The
// `MockOidcServer` file header documents this explicitly. A future
// browser-headless harness can layer real PKCE on top of the same
// endpoints.

// ─── Local helpers (private duplicates of `AuthProviderTests.fs`
//     idioms; lifting them into a shared module would require
//     exporting from another test file's private surface and isn't
//     worth the coupling). ───────────────────────────────────────────

let private mkContext () = DefaultHttpContext() :> HttpContext

let private withHeader (name: string) (value: string) (ctx: HttpContext) =
    ctx.Request.Headers[name] <- StringValues value
    ctx

// Phase 11.C.5 Tier 3 — wrap `HttpContext` into `RequestContext` at
// construction time so callers pass `bearerCtx token` directly to
// `provider.GetUser` / `provider.ValidateRequest`.
let private bearerCtx (token: string) =
    mkContext ()
    |> withHeader "Authorization" ("Bearer " + token)
    |> ToolUp.Platform.RequestContextBuilder.ofHttpContext

/// Minimal `application/json` parser — the mock issuer's `/token`
/// response is a flat object with five well-known keys. Using
/// `System.Text.Json` keeps the test free of any Fable-side JSON
/// dependency the existing auth tests don't already pull in.
let private parseTokenResponse (json: string) =
    let doc = JsonDocument.Parse json
    let root = doc.RootElement

    {|
        AccessToken = root.GetProperty("access_token").GetString()
        TokenType = root.GetProperty("token_type").GetString()
        ExpiresIn = root.GetProperty("expires_in").GetInt32()
        RefreshToken = root.GetProperty("refresh_token").GetString()
        IdToken = root.GetProperty("id_token").GetString()
    |}

// ─── Phase 3b.A — client-side id_token validation helpers ────────────
//
// Build a synthetic id_token + matching JWKS entry from a fresh RSA
// key. Tests then drive `validateIdTokenWith` with stubbed verifier +
// JWKS resolver to exercise the orchestration in pure .NET — the
// production WebCrypto path is browser-only and out of reach for the
// Expecto runner; the pure-F# validators it composes are what's
// covered here.

let private b64urlBytes (bytes: byte[]) =
    Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

let private mkIdToken (rsa: RSA) (kid: string) (issuer: string) (audience: obj) (expEpoch: int64) =
    let header = sprintf """{"alg":"RS256","typ":"JWT","kid":"%s"}""" kid

    let payload =
        let d = System.Collections.Generic.Dictionary<string, obj>()
        d["iss"] <- box issuer
        d["aud"] <- audience
        d["exp"] <- box expEpoch
        d["sub"] <- box "test-user"
        JsonSerializer.Serialize d

    let hB = b64urlBytes (Encoding.UTF8.GetBytes header)
    let pB = b64urlBytes (Encoding.UTF8.GetBytes payload)
    let signedBytes = Encoding.UTF8.GetBytes(hB + "." + pB)

    let signature =
        rsa.SignData(signedBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

    hB + "." + pB + "." + b64urlBytes signature

/// .NET-side analogue of the production WebCrypto verifier. The
/// orchestrator's contract is: given matching key + signedBytes +
/// signature, the verifier returns true; otherwise false. RSA
/// signature verification via the BCL is the natural .NET stand-in
/// (server-side `OidcAuthProvider` uses the same `VerifyData` call
/// shape).
let private dotnetVerifier (rsa: RSA) : string -> Jwk -> byte[] -> byte[] -> Async<bool> =
    fun alg _jwk signedBytes signature -> async {
        match alg with
        | "RS256" -> return rsa.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        | _ -> return false
    }

let private stubResolver (kid: string) (jwk: Jwk) : string -> Async<Result<Jwk, AuthError>> =
    fun requestedKid -> async {
        if requestedKid = kid then
            return Ok jwk
        else
            return Error IdTokenSignatureInvalid
    }

let private mkCfg (issuer: string) (clientId: string) (validate: bool option) : ToolUp.Platform.OidcUIConfig = {
    Issuer = issuer
    ClientId = clientId
    RedirectUri = "https://app.example.test/auth/callback"
    Scopes = [ "openid"; "profile"; "email" ]
    PostLogoutRedirectUri = None
    ValidateIdToken = validate
    BearerToken = None
    SecondaryFlow = None
    RefreshPolicy = None
}

let private kid = "phase-3ba-test-key"
let private testIssuer = "https://issuer.example.test"
let private testClientId = "test-client-id"

let private fixedNow = 1_800_000_000L // 2027-01-15-ish — well-defined; tests roll exp around this anchor.
let private futureExp = fixedNow + 3600L
let private pastExp = fixedNow - 600L

let private phase3bATests: Test =
    testList "OIDC id_token validation (Phase 3b.A)" [

        // ─── Pure-F# claim validators ────────────────────────────────

        testCase "validateIssuer rejects when iss differs"
        <| fun () ->
            let claims = {
                Issuer = Some "https://other.example.test"
                Audience = [ testClientId ]
                ExpiresAt = Some futureExp
            }

            Expect.equal (validateIssuer testIssuer claims) (Error IdTokenIssuerInvalid) "wrong issuer rejected"

        testCase "validateIssuer accepts on exact match"
        <| fun () ->
            let claims = {
                Issuer = Some testIssuer
                Audience = [ testClientId ]
                ExpiresAt = Some futureExp
            }

            Expect.equal (validateIssuer testIssuer claims) (Ok()) "matching issuer accepted"

        testCase "validateIssuer rejects on missing iss claim"
        <| fun () ->
            let claims = {
                Issuer = None
                Audience = [ testClientId ]
                ExpiresAt = Some futureExp
            }

            Expect.equal (validateIssuer testIssuer claims) (Error IdTokenIssuerInvalid) "missing iss rejected"

        testCase "validateAudience accepts when aud list contains clientId"
        <| fun () ->
            let claims = {
                Issuer = Some testIssuer
                Audience = [ "other-app"; testClientId ]
                ExpiresAt = Some futureExp
            }

            Expect.equal (validateAudience testClientId claims) (Ok()) "multi-aud token with matching entry accepted"

        testCase "validateAudience rejects when aud doesn't contain clientId"
        <| fun () ->
            let claims = {
                Issuer = Some testIssuer
                Audience = [ "different-app" ]
                ExpiresAt = Some futureExp
            }

            Expect.equal
                (validateAudience testClientId claims)
                (Error IdTokenAudienceInvalid)
                "non-matching aud rejected"

        testCase "validateExpiry rejects when exp is past beyond skew"
        <| fun () ->
            let claims = {
                Issuer = Some testIssuer
                Audience = [ testClientId ]
                ExpiresAt = Some pastExp
            }

            Expect.equal
                (validateExpiry defaultClockSkewSeconds fixedNow claims)
                (Error IdTokenExpired)
                "past exp rejected"

        testCase "validateExpiry accepts when exp is within 60s clock skew"
        <| fun () ->
            // exp 30s in the past — inside the 60s skew window
            let claims = {
                Issuer = Some testIssuer
                Audience = [ testClientId ]
                ExpiresAt = Some(fixedNow - 30L)
            }

            Expect.equal (validateExpiry defaultClockSkewSeconds fixedNow claims) (Ok()) "exp within skew accepted"

        testCase "validateExpiry rejects on missing exp"
        <| fun () ->
            let claims = {
                Issuer = Some testIssuer
                Audience = [ testClientId ]
                ExpiresAt = None
            }

            Expect.equal
                (validateExpiry defaultClockSkewSeconds fixedNow claims)
                (Error IdTokenExpired)
                "missing exp rejected"

        // ─── Structural JWT parser ───────────────────────────────────

        testCase "parseIdToken accepts a well-formed RS256 token"
        <| fun () ->
            use rsa = RSA.Create 2048
            let token = mkIdToken rsa kid testIssuer (box testClientId) futureExp

            match parseIdToken token with
            | Ok parsed ->
                Expect.equal parsed.Header.Algorithm "RS256" "header alg lifted"
                Expect.equal parsed.Header.Kid (Some kid) "header kid lifted"
                Expect.equal parsed.Claims.Issuer (Some testIssuer) "iss lifted"
                Expect.equal parsed.Claims.Audience [ testClientId ] "single-string aud lifted to list"
                Expect.equal parsed.Claims.ExpiresAt (Some futureExp) "exp lifted as int64"
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "parseIdToken handles aud as array"
        <| fun () ->
            use rsa = RSA.Create 2048

            let token =
                mkIdToken rsa kid testIssuer (box [| "other-aud"; testClientId |]) futureExp

            match parseIdToken token with
            | Ok parsed ->
                Expect.equal parsed.Claims.Audience [ "other-aud"; testClientId ] "array aud lifted to ordered list"
            | Error e -> failtestf "expected Ok, got %A" e

        testCase "parseIdToken rejects fewer than 3 segments as MalformedIdToken"
        <| fun () -> Expect.equal (parseIdToken "only.two") (Error MalformedIdToken) "two-segment input rejected"

        testCase "parseIdToken rejects empty string as MalformedIdToken"
        <| fun () -> Expect.equal (parseIdToken "") (Error MalformedIdToken) "empty input rejected"

        testCase "parseIdToken rejects invalid base64 payload as MalformedIdToken"
        <| fun () ->
            Expect.equal
                (parseIdToken "header.@@not-base64@@.signature")
                (Error MalformedIdToken)
                "bad-base64 payload rejected"

        // ─── End-to-end orchestrator (stubbed verifier + resolver) ───

        testCaseAsync "validateIdTokenWith — happy path returns Ok"
        <| async {
            use rsa = RSA.Create 2048
            let token = mkIdToken rsa kid testIssuer (box testClientId) futureExp
            let jwk = { Kid = kid; RawJwk = obj () }
            let cfg = mkCfg testIssuer testClientId (Some true)

            let! result = validateIdTokenWith (dotnetVerifier rsa) (stubResolver kid jwk) fixedNow cfg token

            Expect.equal result (Ok()) "fully-valid token validates"
        }

        testCaseAsync "validateIdTokenWith — tampered signature → IdTokenSignatureInvalid"
        <| async {
            use rsa = RSA.Create 2048
            let goodToken = mkIdToken rsa kid testIssuer (box testClientId) futureExp
            // Flip a byte in the signature segment.
            let parts = goodToken.Split('.')
            let sigBytes = base64UrlDecode parts[2]
            sigBytes[0] <- sigBytes[0] ^^^ 0xFFuy
            let tampered = parts[0] + "." + parts[1] + "." + b64urlBytes sigBytes
            let jwk = { Kid = kid; RawJwk = obj () }
            let cfg = mkCfg testIssuer testClientId (Some true)

            let! result = validateIdTokenWith (dotnetVerifier rsa) (stubResolver kid jwk) fixedNow cfg tampered

            Expect.equal result (Error IdTokenSignatureInvalid) "tampered signature rejected"
        }

        testCaseAsync "validateIdTokenWith — wrong issuer → IdTokenIssuerInvalid"
        <| async {
            use rsa = RSA.Create 2048

            let token =
                mkIdToken rsa kid "https://different-issuer.example.test" (box testClientId) futureExp

            let jwk = { Kid = kid; RawJwk = obj () }
            let cfg = mkCfg testIssuer testClientId (Some true)

            let! result = validateIdTokenWith (dotnetVerifier rsa) (stubResolver kid jwk) fixedNow cfg token

            Expect.equal result (Error IdTokenIssuerInvalid) "wrong iss rejected"
        }

        testCaseAsync "validateIdTokenWith — audience mismatch → IdTokenAudienceInvalid"
        <| async {
            use rsa = RSA.Create 2048
            let token = mkIdToken rsa kid testIssuer (box "wrong-client-id") futureExp
            let jwk = { Kid = kid; RawJwk = obj () }
            let cfg = mkCfg testIssuer testClientId (Some true)

            let! result = validateIdTokenWith (dotnetVerifier rsa) (stubResolver kid jwk) fixedNow cfg token

            Expect.equal result (Error IdTokenAudienceInvalid) "wrong aud rejected"
        }

        testCaseAsync "validateIdTokenWith — expired token → IdTokenExpired"
        <| async {
            use rsa = RSA.Create 2048
            let token = mkIdToken rsa kid testIssuer (box testClientId) pastExp
            let jwk = { Kid = kid; RawJwk = obj () }
            let cfg = mkCfg testIssuer testClientId (Some true)

            let! result = validateIdTokenWith (dotnetVerifier rsa) (stubResolver kid jwk) fixedNow cfg token

            Expect.equal result (Error IdTokenExpired) "expired token rejected"
        }

        testCaseAsync "validateIdTokenWith — unknown kid → IdTokenSignatureInvalid"
        <| async {
            use rsa = RSA.Create 2048
            let token = mkIdToken rsa "different-kid" testIssuer (box testClientId) futureExp
            let jwk = { Kid = kid; RawJwk = obj () }
            let cfg = mkCfg testIssuer testClientId (Some true)

            let! result = validateIdTokenWith (dotnetVerifier rsa) (stubResolver kid jwk) fixedNow cfg token

            Expect.equal result (Error IdTokenSignatureInvalid) "unresolved kid surfaces as IdTokenSignatureInvalid"
        }

        // ─── Opt-in toggle (handler-level behaviour the orchestrator
        //     itself doesn't decide — call site in `handleCallback`
        //     gates on `cfg.ValidateIdToken = Some true`). Below proves
        //     the orchestrator runs the pipeline when given a token; the
        //     toggle test pins the cfg defaults that pin the call-site
        //     contract.) ───────────────────────────────────────────────

        testCase "OidcUIConfig.defaults sets ValidateIdToken = None (off)"
        <| fun () ->
            let cfg =
                ToolUp.Platform.OidcUIConfig.defaults
                    "https://issuer.example.test"
                    "client-id"
                    "https://app.example.test/cb"

            Expect.equal cfg.ValidateIdToken None "defaults keeps Phase 3b.A validation opt-in"

        testCase "ValidateIdToken = Some false equivalent to None for handler gating"
        <| fun () ->
            // `handleCallback` runs `validateIdToken` only when the
            // config is `Some true` — both `None` and `Some false`
            // skip the new pipeline (back-compat path).
            let off1 = mkCfg testIssuer testClientId None
            let off2 = mkCfg testIssuer testClientId (Some false)
            let on = mkCfg testIssuer testClientId (Some true)

            let shouldRun (c: ToolUp.Platform.OidcUIConfig) =
                match c.ValidateIdToken with
                | Some true -> true
                | _ -> false

            Expect.isFalse (shouldRun off1) "None skips validation"
            Expect.isFalse (shouldRun off2) "Some false skips validation"
            Expect.isTrue (shouldRun on) "Some true runs validation"

    ]

// ─── Test pack ───────────────────────────────────────────────────────

let tests: Test =
    // `lazy` + `testSequenced` boot the issuer once for the whole pack;
    // the trailing teardown case stops Kestrel after the contract
    // cases run (sequenced lists run in declaration order). Same
    // pattern as `oidcMockIssuerContract` in AuthProviderTests.fs.
    let server = lazy (MockOidcServer.start MockOidcConfig.defaults)

    let mockIssuerPack =
        testSequenced (
            testList "OidcSignInFlow (mock issuer)" [

                testCaseAsync "/.well-known/openid-configuration is reachable + advertises endpoints"
                <| async {
                    let s = server.Value
                    use http = new HttpClient()

                    let! body =
                        http.GetStringAsync($"{s.IssuerUrl}/.well-known/openid-configuration")
                        |> Async.AwaitTask

                    let doc = JsonDocument.Parse body
                    let root = doc.RootElement

                    Expect.equal (root.GetProperty("issuer").GetString()) s.IssuerUrl "issuer field == base URL"

                    Expect.equal
                        (root.GetProperty("authorization_endpoint").GetString())
                        $"{s.IssuerUrl}/authorize"
                        "authorization_endpoint advertised"

                    Expect.equal
                        (root.GetProperty("token_endpoint").GetString())
                        $"{s.IssuerUrl}/token"
                        "token_endpoint advertised"
                }

                testCaseAsync "/authorize redirects with code + state echoed"
                <| async {
                    let s = server.Value
                    // allowAutoRedirect = false so we can inspect the 302 + Location header
                    let handler = new HttpClientHandler(AllowAutoRedirect = false)
                    use http = new HttpClient(handler)

                    // Plain concatenation: F# interpolated strings parse `%2F`
                    // and `%3A` as format specifiers, so URL-encoded values
                    // must live outside the `$"..."` form.
                    let url =
                        s.IssuerUrl
                        + "/authorize?response_type=code&client_id=cid&redirect_uri=http%3A%2F%2Flocalhost%2Fcb&state=test-state-123"

                    let! resp = http.GetAsync url |> Async.AwaitTask

                    Expect.equal resp.StatusCode HttpStatusCode.Redirect "/authorize must 302 (auth-code flow)"
                    let location = string resp.Headers.Location
                    Expect.stringContains location "code=" "redirect carries an auth code"
                    Expect.stringContains location "state=test-state-123" "state parameter echoes back unchanged"
                }

                testCaseAsync "/token exchange returns access_token + id_token (RS256 / Bearer)"
                <| async {
                    let s = server.Value
                    use http = new HttpClient()

                    let! resp = http.PostAsync($"{s.IssuerUrl}/token", null) |> Async.AwaitTask

                    Expect.equal resp.StatusCode HttpStatusCode.OK "code exchange must 200 OK"

                    let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                    let tokens = parseTokenResponse body

                    Expect.equal tokens.TokenType "Bearer" "token_type is Bearer (RFC 6750)"
                    Expect.isGreaterThan tokens.AccessToken.Length 0 "access_token issued"
                    Expect.isGreaterThan tokens.IdToken.Length 0 "id_token issued"
                    Expect.isGreaterThan tokens.ExpiresIn 0 "expires_in positive"
                }

                testCaseAsync "OidcAuthProvider validates the token returned by /token (end-to-end sign-in)"
                <| async {
                    let s = server.Value

                    // 1. Exchange code → access_token.
                    use http = new HttpClient()
                    let! resp = http.PostAsync($"{s.IssuerUrl}/token", null) |> Async.AwaitTask
                    resp.EnsureSuccessStatusCode() |> ignore
                    let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                    let tokens = parseTokenResponse body

                    // 2. Construct OidcAuthProvider against the live issuer.
                    //    Discovery resolves /jwks via the discovery doc; no
                    //    explicit jwks_uri is supplied, so this also
                    //    exercises the JwksDiscovery code path end-to-end.
                    let config = {
                        Issuer = Some s.IssuerUrl
                        Audience = None
                        KeySource = JwksDiscovery s.IssuerUrl
                        TokenLocation = BearerHeader
                        ClockSkewSeconds = None
                        AcceptedAlgorithms = None
                        PreferOidWhenPresent = None
                        ClaimMapping = None
                    }

                    let provider = OidcAuthProvider.fromConfigWith (new HttpClient()) None config

                    // 3. Validate.
                    let ctx = bearerCtx tokens.AccessToken

                    match! provider.ValidateRequest(ctx) with
                    | Error e -> failtestf "expected Ok validating /token-issued bearer; got Error: %s" e
                    | Ok user ->
                        Expect.equal
                            user.UserId
                            MockOidcConfig.defaults.Subject
                            "validated UserId mirrors mock issuer's sub claim"

                        Expect.isFalse (AuthenticatedUser.isAnonymous user) "validated user is non-anonymous"
                }

                testCase "teardown: stop mock issuer"
                <| fun () ->
                    if server.IsValueCreated then
                        (server.Value :> IDisposable).Dispose()

            ]
        )

    testList "OIDC sign-in" [ mockIssuerPack; phase3bATests ]