module ToolUp.Platform.Tests.InProcess.OAuth1aSubstrateTests

open System
open Expecto
open ToolUp.Platform

// ─── Phase 10g — OAuth 1.0a substrate ───────────────────────────────────
//
// The load-bearing assertion is the RFC 5849 HMAC-SHA1 signer against the
// canonical, widely-published reference vector (the Twitter "Creating a
// signature" worked example), whose expected signature is fixed and
// reproducible independent of any provider. Plus percent-encoding edge
// cases and the request-token state store's single-use / TTL / scope
// semantics.

let tests =
    testList "OAuth1a" [

        // ── RFC 5849 §3.6 percent-encoding ──

        testCase "percentEncode leaves unreserved characters and encodes the rest"
        <| fun () ->
            Expect.equal (OAuth1aSigner.percentEncode "Az09-._~") "Az09-._~" "unreserved pass through"
            Expect.equal (OAuth1aSigner.percentEncode " ") "%20" "space is %20 not +"
            Expect.equal (OAuth1aSigner.percentEncode "+") "%2B" "plus is encoded"
            Expect.equal (OAuth1aSigner.percentEncode "=") "%3D" "equals is encoded"
            Expect.equal (OAuth1aSigner.percentEncode "&") "%26" "ampersand is encoded"
            Expect.equal (OAuth1aSigner.percentEncode "a b!c") "a%20b%21c" "mixed"

        // ── The canonical HMAC-SHA1 reference vector ──

        testCase "computeSignature matches the reference base string + independently-verified HMAC-SHA1"
        <| fun () ->
            // The widely-published OAuth 1.0a worked example (Twitter's
            // "Creating a signature"). Two independent checks pin correctness:
            //   1. The signature BASE STRING must equal Twitter's documented
            //      base string exactly — the authoritative RFC-5849
            //      canonicalization (percent-encoding + parameter sorting +
            //      double-encoding) check.
            //   2. The HMAC-SHA1 of that base string under the documented
            //      signing key, cross-verified with `openssl dgst -sha1 -hmac`
            //      to be `hCtSmYh+iHYCEqBWrE7C7hYmtUk=`. (Twitter's docs PUBLISH
            //      `tnnArxj06cWHq44gCs1OSKk/jLY=`, a long-known non-reproducible
            //      value — the documented base string + key do not hash to it;
            //      every conforming implementation, incl. openssl, produces the
            //      value asserted here.)
            let httpMethod = "POST"
            let baseUrl = "https://api.twitter.com/1.1/statuses/update.json"

            let parameters = [
                "status", "Hello Ladies + Gentlemen, a signed OAuth request!"
                "include_entities", "true"
                "oauth_consumer_key", "xvz1evFS4wEEPTGEFPHBog"
                "oauth_nonce", "kYjzVBB8Y0ZFabxSWbWovY3uYSQ2pTgmZeNu2VS4cg"
                "oauth_signature_method", "HMAC-SHA1"
                "oauth_timestamp", "1318622958"
                "oauth_token", "370773112-GmHxMAgYyLbNEtIKZeRNFsMKPR9EyMZeS9weJAEb"
                "oauth_version", "1.0"
            ]

            let consumerSecret = "kAcSOqF21Fu85e7zjz7ZN2U4ZRhfV3WpwPAoE3Z7kBw"
            let tokenSecret = "LswwdoUaIvS8ltyTt5jkRh4J50vUPVVHtR2YPi5kE"

            let expectedBaseString =
                "POST&https%3A%2F%2Fapi.twitter.com%2F1.1%2Fstatuses%2Fupdate.json&include_entities%3Dtrue%26oauth_consumer_key%3Dxvz1evFS4wEEPTGEFPHBog%26oauth_nonce%3DkYjzVBB8Y0ZFabxSWbWovY3uYSQ2pTgmZeNu2VS4cg%26oauth_signature_method%3DHMAC-SHA1%26oauth_timestamp%3D1318622958%26oauth_token%3D370773112-GmHxMAgYyLbNEtIKZeRNFsMKPR9EyMZeS9weJAEb%26oauth_version%3D1.0%26status%3DHello%2520Ladies%2520%252B%2520Gentlemen%252C%2520a%2520signed%2520OAuth%2520request%2521"

            Expect.equal
                (OAuth1aSigner.signatureBaseString httpMethod baseUrl parameters)
                expectedBaseString
                "base string matches the documented reference"

            let signature =
                OAuth1aSigner.computeSignature httpMethod baseUrl parameters consumerSecret tokenSecret

            Expect.equal
                signature
                "hCtSmYh+iHYCEqBWrE7C7hYmtUk="
                "HMAC-SHA1 signature matches the openssl-verified value for the documented base string"

        testCase
            "buildAuthorizationHeaderValue produces the reference signature (percent-encoded) and only oauth_* params"
        <| fun () ->
            let consumer = {
                ConsumerKey = "xvz1evFS4wEEPTGEFPHBog"
                ConsumerSecret = "kAcSOqF21Fu85e7zjz7ZN2U4ZRhfV3WpwPAoE3Z7kBw"
            }

            let token = {
                Token = "370773112-GmHxMAgYyLbNEtIKZeRNFsMKPR9EyMZeS9weJAEb"
                TokenSecret = "LswwdoUaIvS8ltyTt5jkRh4J50vUPVVHtR2YPi5kE"
            }

            let requestParams = [
                "status", "Hello Ladies + Gentlemen, a signed OAuth request!"
                "include_entities", "true"
            ]

            let header =
                OAuth1aSigner.buildAuthorizationHeaderValue
                    consumer
                    (Some token)
                    "POST"
                    "https://api.twitter.com/1.1/statuses/update.json"
                    requestParams
                    "kYjzVBB8Y0ZFabxSWbWovY3uYSQ2pTgmZeNu2VS4cg"
                    "1318622958"

            Expect.stringStarts header "OAuth " "OAuth scheme prefix"
            // The signature is percent-encoded in the header (/ → %2F, = → %3D).
            Expect.stringContains
                header
                "oauth_signature=\"hCtSmYh%2BiHYCEqBWrE7C7hYmtUk%3D\""
                "signature present + encoded"

            Expect.stringContains header "oauth_consumer_key=\"xvz1evFS4wEEPTGEFPHBog\"" "consumer key present"
            // Request parameters (status / include_entities) are signed but
            // NOT rendered into the Authorization header.
            Expect.isFalse (header.Contains "status=") "request params excluded from the header"

        testCase "the request-token leg signs with an empty token secret (consumerSecret&)"
        <| fun () ->
            // Leg-1 request-token fetch: no token yet. The signing key is
            // consumerSecret + "&" with an empty token secret.
            Expect.equal (OAuth1aSigner.signingKey "cs" "") "cs&" "empty token secret"
            Expect.equal (OAuth1aSigner.signingKey "c s" "t s") "c%20s&t%20s" "both encoded"

        // ── Request-token state store ──

        testCase "state store round-trips and is single-use"
        <| fun () ->
            let store = InMemoryOAuth1aStateStore.create ()

            let state = {
                ScopeId = "team-a"
                FlowName = "garmin"
                RequestTokenSecret = "rt-secret"
                CreatedAt = DateTime.UtcNow
            }

            store.Save("req-token-1", state) |> Async.RunSynchronously

            let first =
                store.TakeValid("req-token-1", TimeSpan.FromMinutes 10.0)
                |> Async.RunSynchronously

            match first with
            | Some s ->
                Expect.equal s.ScopeId "team-a" "scope preserved"
                Expect.equal s.RequestTokenSecret "rt-secret" "secret preserved"
            | None -> failtest "expected the saved state"

            // Single-use — a second take is empty.
            let second =
                store.TakeValid("req-token-1", TimeSpan.FromMinutes 10.0)
                |> Async.RunSynchronously

            Expect.isNone second "state is consumed single-use"

        testCase "state store treats an entry older than the TTL as absent"
        <| fun () ->
            let store = InMemoryOAuth1aStateStore.create ()

            let stale = {
                ScopeId = "team-a"
                FlowName = "garmin"
                RequestTokenSecret = "rt-secret"
                CreatedAt = DateTime.UtcNow.AddMinutes -20.0
            }

            store.Save("req-token-2", stale) |> Async.RunSynchronously

            let result =
                store.TakeValid("req-token-2", TimeSpan.FromMinutes 10.0)
                |> Async.RunSynchronously

            Expect.isNone result "an expired pending authorisation is not resumable"

        testCase "an absent request token yields None (surfaces as StateTokenMismatch upstream)"
        <| fun () ->
            let store = InMemoryOAuth1aStateStore.create ()

            let result =
                store.TakeValid("never-saved", TimeSpan.FromMinutes 10.0)
                |> Async.RunSynchronously

            Expect.isNone result "no pending authorisation"
    ]