// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.OidcClassifyTokenTests

open Expecto
open ToolUp.Platform
open ToolUp.AuthProviders.Oidc.OidcClient

// ─── classifyStoredTokenWith — opaque-shape hardening + classification ──
//
// `classifyStoredTokenWith` is the test-doubles entry point for the
// browser-side `classifyStoredToken`. It takes the stored access token
// + a payload decoder as parameters so the decision logic is exercised
// from .NET-side Expecto without the production browser shim
// (`atob` + `JS.JSON.parse` + `localStorage`-backed `getAccessToken`).
//
// Headline coverage: a 3-segment-shaped token whose payload cannot be
// base64-decoded / JSON-parsed classifies as `OpaqueToken`, not
// `StaleJwt`. Without the rule, Microsoft Graph encrypted-payload
// ("nord") tokens — which present three dot-segments but whose middle
// segment is opaque — trip the JWT branch, fail the issuer + exp checks
// (because the decoder returned no claims), and the shell launches a
// doomed refresh against a token the server-side validator has not yet
// seen. Treating un-decodable 3-segment tokens as opaque defers the
// validity question to the server (the only side that can answer it
// for an opaque token) and avoids the refresh storm.

let private testIssuer = "https://issuer.example.test"

let private cfg (issuer: string) : OidcUIConfig = {
    Issuer = issuer
    ClientId = "test-client-id"
    RedirectUri = "https://app.example.test/auth/callback"
    Scopes = [ "openid"; "profile"; "email" ]
    PostLogoutRedirectUri = None
    ValidateIdToken = None
    BearerToken = None
    SecondaryFlow = None
    RefreshPolicy = None
}

let private nowSeconds = 1_800_000_000.0 // fixed anchor (~2027); tests roll exp around it.
let private futureExp = nowSeconds + 3600.0
let private pastExp = nowSeconds - 600.0

/// Token shaped to have N dot-separated segments — content is
/// irrelevant since the decoder is supplied by each test.
let private segments (count: int) =
    System.String.Join(".", Array.create count "x")

let private alwaysOpaqueDecoder: string -> JwtClaimsExtract option = fun _ -> None

let private decoderReturning (claims: JwtClaimsExtract) : string -> JwtClaimsExtract option = fun _ -> Some claims

let tests: Test =
    testList "OidcClient.classifyStoredTokenWith" [

        testCase "no stored token → NoToken"
        <| fun () ->
            let result =
                classifyStoredTokenWith (cfg testIssuer) nowSeconds None alwaysOpaqueDecoder

            Expect.equal result NoToken ""

        testCase "1-segment token → OpaqueToken"
        <| fun () ->
            let result =
                classifyStoredTokenWith (cfg testIssuer) nowSeconds (Some(segments 1)) alwaysOpaqueDecoder

            Expect.equal result OpaqueToken ""

        testCase "2-segment token → OpaqueToken"
        <| fun () ->
            let result =
                classifyStoredTokenWith (cfg testIssuer) nowSeconds (Some(segments 2)) alwaysOpaqueDecoder

            Expect.equal result OpaqueToken ""

        testCase "3-segment-shaped token whose payload does NOT decode → OpaqueToken (the hardening)"
        <| fun () ->
            // Matches the Microsoft Graph "nord" token shape: three dot
            // segments but the middle segment is encrypted, so the
            // browser-side decoder (atob → JSON.parse) raises. Before
            // this hardening the result was StaleJwt and the shell
            // triggered a doomed refresh against a token whose claims
            // it never read.
            let result =
                classifyStoredTokenWith (cfg testIssuer) nowSeconds (Some(segments 3)) alwaysOpaqueDecoder

            Expect.equal
                result
                OpaqueToken
                "3-segment-but-non-decodable MUST NOT be StaleJwt — that triggers a doomed refresh."

        testCase "decoded JWT, matching iss + future exp → FreshJwt"
        <| fun () ->
            let decode =
                decoderReturning {
                    Iss = Some testIssuer
                    Exp = Some futureExp
                }

            let result =
                classifyStoredTokenWith (cfg testIssuer) nowSeconds (Some(segments 3)) decode

            Expect.equal result FreshJwt ""

        testCase "decoded JWT, matching iss + past exp → StaleJwt"
        <| fun () ->
            let decode =
                decoderReturning {
                    Iss = Some testIssuer
                    Exp = Some pastExp
                }

            let result =
                classifyStoredTokenWith (cfg testIssuer) nowSeconds (Some(segments 3)) decode

            Expect.equal result StaleJwt ""

        testCase "decoded JWT, mismatched iss + future exp → StaleJwt"
        <| fun () ->
            let decode =
                decoderReturning {
                    Iss = Some "https://other-issuer.example.test"
                    Exp = Some futureExp
                }

            let result =
                classifyStoredTokenWith (cfg testIssuer) nowSeconds (Some(segments 3)) decode

            Expect.equal result StaleJwt ""

        testCase "decoded JWT, missing iss claim → StaleJwt"
        <| fun () ->
            let decode = decoderReturning { Iss = None; Exp = Some futureExp }

            let result =
                classifyStoredTokenWith (cfg testIssuer) nowSeconds (Some(segments 3)) decode

            Expect.equal result StaleJwt ""

        testCase "decoded JWT, missing exp claim → StaleJwt"
        <| fun () ->
            let decode = decoderReturning { Iss = Some testIssuer; Exp = None }

            let result =
                classifyStoredTokenWith (cfg testIssuer) nowSeconds (Some(segments 3)) decode

            Expect.equal result StaleJwt ""

        testCase "iss trailing-slash normalisation: config without slash, token iss with slash"
        <| fun () ->
            // Documented Entra / Auth0 quirk handling.
            let decode =
                decoderReturning {
                    Iss = Some(testIssuer + "/")
                    Exp = Some futureExp
                }

            let result =
                classifyStoredTokenWith (cfg testIssuer) nowSeconds (Some(segments 3)) decode

            Expect.equal result FreshJwt ""

        testCase "iss trailing-slash normalisation: config with slash, token iss without slash"
        <| fun () ->
            let decode =
                decoderReturning {
                    Iss = Some testIssuer
                    Exp = Some futureExp
                }

            let result =
                classifyStoredTokenWith (cfg (testIssuer + "/")) nowSeconds (Some(segments 3)) decode

            Expect.equal result FreshJwt ""
    ]