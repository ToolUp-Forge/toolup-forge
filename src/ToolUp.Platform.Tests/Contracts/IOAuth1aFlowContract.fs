// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IOAuth1aFlowContract

open System
open System.Net.Http
open Expecto
open ToolUp.Platform

// ─── Phase 10g — IOAuth1aFlow conformance pack ──────────────────────────
//
// Parametrised over any `IOAuth1aFlow` implementation. Asserts the portable
// contract: a populated identity + descriptor, a request-token bounce that
// yields an absolute authorisation URL + a request-token pair, an
// access-token exchange that yields an access-token pair, and a per-call
// `Sign` that attaches an `Authorization: OAuth …` HMAC-SHA1 header and
// never caches (signing after token rotation still signs). The signing
// correctness itself is pinned separately against the RFC 5849 reference
// vector in `OAuth1aSubstrateTests`.

let tests (name: string) (factory: unit -> IOAuth1aFlow) =
    let mkCtx () : OAuth1aFlowContext = {
        ScopeId = "team-scope-1"
        ResourceId = "res-1"
    }

    let authHeader (req: HttpRequestMessage) =
        if req.Headers.Contains "Authorization" then
            String.concat "" (req.Headers.GetValues "Authorization")
        else
            ""

    testList $"{name} — IOAuth1aFlow contract" [

        testCase "Name is a non-empty discriminator"
        <| fun () ->
            let flow = factory ()
            Expect.isNotEmpty flow.Name "Name populated"

        testCase "Descriptor carries a display name"
        <| fun () ->
            let flow = factory ()
            Expect.isNotEmpty flow.Descriptor.DisplayName "DisplayName populated"

        testCaseAsync "BuildRequestTokenUrl (leg 1) returns an absolute authorise URL + a request-token pair"
        <| async {
            let flow = factory ()

            match! flow.BuildRequestTokenUrl(mkCtx (), "https://app.example.com/api/oauth1a/x/callback") with
            | Ok rt ->
                Expect.stringStarts rt.AuthorizeUrl "http" "authorise URL is absolute"
                Expect.isNotEmpty rt.RequestToken "request token present"
                Expect.isNotEmpty rt.RequestTokenSecret "request-token secret present (to stash)"
            | Error e -> failtestf "expected Ok; got %A" e
        }

        testCaseAsync "ExchangeRequestTokenForAccess (leg 3) returns an access-token pair"
        <| async {
            let flow = factory ()

            match! flow.ExchangeRequestTokenForAccess(mkCtx (), "req-token", "req-secret", "verifier-123") with
            | Ok pair ->
                Expect.isNotEmpty pair.Token "access token present"
                Expect.isNotEmpty pair.TokenSecret "access-token secret present"
            | Error e -> failtestf "expected Ok; got %A" e
        }

        testCaseAsync "Sign attaches an Authorization: OAuth HMAC-SHA1 header"
        <| async {
            let flow = factory ()

            use req =
                new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1/data?x=1")

            let pair = {
                Token = "acc-token"
                TokenSecret = "acc-secret"
            }

            do! flow.Sign(req, pair)
            let header = authHeader req
            Expect.stringStarts header "OAuth " "OAuth scheme"
            Expect.stringContains header "oauth_signature" "signature present"
            Expect.stringContains header "oauth_signature_method=\"HMAC-SHA1\"" "HMAC-SHA1 method"
        }

        testCaseAsync "Sign after token rotation still signs (no cached bearer — 1.0a signs every call)"
        <| async {
            let flow = factory ()

            use req =
                new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1/write")

            do!
                flow.Sign(
                    req,
                    {
                        Token = "rotated"
                        TokenSecret = "rotated-secret"
                    }
                )

            Expect.stringStarts (authHeader req) "OAuth " "signed with the rotated pair"
        }
    ]