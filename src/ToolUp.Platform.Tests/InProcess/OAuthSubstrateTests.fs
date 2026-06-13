module ToolUp.Platform.Tests.InProcess.OAuthSubstrateTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts

// ─── InMemoryOAuthStateStore — IOAuthStateStore contract binding ────

let stateStoreTests =
    let factory () =
        InMemoryOAuthStateStore() :> IOAuthStateStore

    IOAuthStateStoreContract.tests "InMemoryOAuthStateStore" factory

// ─── FakeOAuthFlow — IOAuthCredentialFlow contract binding ──────────
//
// In-process fake flow for the IOAuthCredentialFlow contract pack.
// Real provider implementations live in companion packages
// (`src/AuthFlows/<Provider>/`, `src/DataSources/<Provider>/`); each
// binds the same contract pack against its own HTTP-layer fake.
//
// The fake honours the substrate-shape rules:
//   - `BuildAuthorizeUrl` produces an HTTPS URL containing the
//     supplied state + URL-encoded redirect_uri.
//   - `ExchangeCode("valid-test-code", _)` returns Ok credentials.
//   - `RefreshAccessToken("valid-refresh-token")` returns Ok with
//     a 60-min expiry.
//   - `Revoke` returns `RevocationUnsupported` (declares the
//     provider has no revocation endpoint).

type FakeOAuthFlow() =
    interface IOAuthCredentialFlow with
        member _.Name = "fake-flow"

        member _.Descriptor = {
            DisplayName = "Fake Flow"
            Scopes = [ "https://example.com/scope/read" ]
            HelpUrl = None
        }

        // Non-PKCE provider: declares no PKCE support, so the substrate
        // passes `None` at both ends and the wire output is the pre-PKCE
        // shape (GP 11).
        member _.SupportsPkce = false

        member _.BuildAuthorizeUrl(_ctx, state, redirectUri, _pkce) = async {
            let url =
                sprintf
                    "https://example.com/oauth/authorize?client_id=fake&response_type=code&state=%s&redirect_uri=%s"
                    (Uri.EscapeDataString state)
                    (Uri.EscapeDataString redirectUri)

            return Ok url
        }

        member _.ExchangeCode(_ctx, code, _redirectUri, _codeVerifier) = async {
            if code = "valid-test-code" then
                let creds: OAuthCredentials = {
                    RefreshToken = "fake-refresh-token-abc123"
                    AccessToken = Some "fake-access-token-xyz789"
                    ExpiresAt = Some(DateTime.UtcNow.AddHours 1.0)
                    IdToken = None
                }

                return Ok creds
            else
                return Error(OAuthError.ProviderRejected "invalid_grant")
        }

        member _.RefreshAccessToken(_ctx, refreshToken) = async {
            if refreshToken = "valid-refresh-token" then
                let token: OAuthAccessToken = {
                    Token = "fake-access-token-fresh"
                    ExpiresAt = DateTime.UtcNow.AddMinutes 60.0
                }

                return Ok token
            else
                return Error(OAuthError.ProviderRejected "invalid_grant")
        }

        member _.Revoke(_ctx, _refreshToken) = async { return Error OAuthError.RevocationUnsupported }

let credentialFlowTests =
    let factory () = FakeOAuthFlow() :> IOAuthCredentialFlow
    IOAuthCredentialFlowContract.tests "FakeOAuthFlow" factory

// ─── FakePkceOAuthFlow — PKCE-capable flow ──────────────────────────
//
// A flow that declares `SupportsPkce = true`. It appends the substrate-
// supplied `code_challenge` + `code_challenge_method` to the authorize
// URL and REQUIRES the `code_verifier` on exchange — a missing verifier
// is a hard error (an intercepted code without the verifier is useless).
// Drives both the shared contract pack (the PKCE-gated case fires) and
// the dedicated PKCE assertions below.

type FakePkceOAuthFlow() =
    interface IOAuthCredentialFlow with
        member _.Name = "fake-pkce-flow"

        member _.Descriptor = {
            DisplayName = "Fake PKCE Flow"
            Scopes = [ "https://example.com/scope/read" ]
            HelpUrl = None
        }

        member _.SupportsPkce = true

        member _.BuildAuthorizeUrl(_ctx, state, redirectUri, pkce) = async {
            let pkceParams =
                match pkce with
                | Some challenge ->
                    sprintf "&code_challenge=%s&code_challenge_method=%s" challenge.Challenge challenge.Method
                | None -> ""

            let url =
                sprintf
                    "https://example.com/oauth/authorize?client_id=fake&response_type=code&state=%s&redirect_uri=%s%s"
                    (Uri.EscapeDataString state)
                    (Uri.EscapeDataString redirectUri)
                    pkceParams

            return Ok url
        }

        member _.ExchangeCode(_ctx, code, _redirectUri, codeVerifier) = async {
            // A PKCE flow that receives no verifier rejects the exchange:
            // an intercepted code cannot be redeemed without it.
            match codeVerifier with
            | None -> return Error(OAuthError.OAuthFlowFailed "PKCE code_verifier required for this flow")
            | Some _ when code = "valid-test-code" ->
                let creds: OAuthCredentials = {
                    RefreshToken = "fake-pkce-refresh-token"
                    AccessToken = None
                    ExpiresAt = None
                    IdToken = None
                }

                return Ok creds
            | Some _ -> return Error(OAuthError.ProviderRejected "invalid_grant")
        }

        member _.RefreshAccessToken(_ctx, _refreshToken) = async {
            return
                Ok {
                    Token = "fake-pkce-access-token"
                    ExpiresAt = DateTime.UtcNow.AddMinutes 60.0
                }
        }

        member _.Revoke(_ctx, _refreshToken) = async { return Error OAuthError.RevocationUnsupported }

/// The shared contract pack, bound against the PKCE-capable fake — the
/// `SupportsPkce`-gated conformance case exercises the challenge path.
let pkceCredentialFlowTests =
    let factory () =
        FakePkceOAuthFlow() :> IOAuthCredentialFlow

    IOAuthCredentialFlowContract.tests "FakePkceOAuthFlow" factory

// ─── PKCE-specific assertions (substrate crypto + verifier gate) ─────

let pkceFlowTests =
    testList "OAuth PKCE enforcement (Phase 130)" [

        // RFC 7636 Appendix B test vector pins the S256 derivation so a
        // regression in `codeChallengeFromVerifier` (zero call sites
        // before this phase) is caught: a verifier that no longer hashes
        // to the published challenge would silently break every PKCE
        // exchange.
        test "codeChallengeFromVerifier matches the RFC 7636 S256 vector" {
            let verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
            let expected = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"

            Expect.equal
                (OAuthCrypto.codeChallengeFromVerifier verifier)
                expected
                "S256 challenge matches the RFC vector"
        }

        testCaseAsync "PKCE flow: BuildAuthorizeUrl with Some challenge emits code_challenge + S256"
        <| async {
            let flow = FakePkceOAuthFlow() :> IOAuthCredentialFlow

            let ctx: OAuthFlowContext = {
                ScopeId = "s"
                DataSourceId = "ds"
                Config = None
            }

            let verifier = OAuthCrypto.generateCodeVerifier ()
            let challenge = OAuthCrypto.codeChallengeFromVerifier verifier

            match!
                flow.BuildAuthorizeUrl(
                    ctx,
                    "state-1",
                    "https://example.com/cb",
                    Some {
                        Challenge = challenge
                        Method = "S256"
                    }
                )
            with
            | Ok url ->
                Expect.stringContains url $"code_challenge={challenge}" "challenge is on the URL"
                Expect.stringContains url "code_challenge_method=S256" "method is S256"
            | Error e -> failtestf "BuildAuthorizeUrl failed: %s" (OAuthError.toMessage e)
        }

        testCaseAsync "PKCE flow: ExchangeCode WITH the verifier succeeds"
        <| async {
            let flow = FakePkceOAuthFlow() :> IOAuthCredentialFlow

            let ctx: OAuthFlowContext = {
                ScopeId = "s"
                DataSourceId = "ds"
                Config = None
            }

            match! flow.ExchangeCode(ctx, "valid-test-code", "https://example.com/cb", Some "the-verifier") with
            | Ok creds -> Expect.isNonEmpty creds.RefreshToken "verifier-backed exchange mints credentials"
            | Error e -> failtestf "ExchangeCode failed: %s" (OAuthError.toMessage e)
        }

        testCaseAsync "PKCE flow: an intercepted code WITHOUT the verifier fails the exchange"
        <| async {
            let flow = FakePkceOAuthFlow() :> IOAuthCredentialFlow

            let ctx: OAuthFlowContext = {
                ScopeId = "s"
                DataSourceId = "ds"
                Config = None
            }

            match! flow.ExchangeCode(ctx, "valid-test-code", "https://example.com/cb", None) with
            | Ok _ ->
                failtest "Expected the verifier-less exchange to fail — an intercepted code must not be redeemable"
            | Error _ -> ()
        }
    ]

// ─── TIDY-UP (Auth-core) — upstream error-body scrub ────────────────

let refresherScrubTests =
    testList "OAuth refresher — upstream error-body scrub" [
        test "redacts a token-shaped run echoed in an upstream error body" {
            let body =
                """{"error":"invalid_request","error_description":"bad token abcdef0123456789ABCDEFghijklmnop"}"""

            let scrubbed = InProcessOAuthTokenRefresher.scrubUpstreamBody body

            Expect.isFalse
                (scrubbed.Contains "abcdef0123456789ABCDEFghijklmnop")
                "the long token-shaped run is redacted"

            Expect.stringContains scrubbed "<redacted>" "redaction marker present"
            Expect.stringContains scrubbed "invalid_request" "short error code preserved"
        }

        test "caps an oversized body" {
            let scrubbed = InProcessOAuthTokenRefresher.scrubUpstreamBody (String('x', 5000))
            Expect.isLessThanOrEqual scrubbed.Length 300 "body is truncated to a bounded length"
        }

        test "empty body → empty string" {
            Expect.equal (InProcessOAuthTokenRefresher.scrubUpstreamBody "") "" "no body, nothing to scrub"
        }
    ]