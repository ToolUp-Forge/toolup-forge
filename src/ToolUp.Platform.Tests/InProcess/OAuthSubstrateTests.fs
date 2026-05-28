module ToolUp.Platform.Tests.InProcess.OAuthSubstrateTests

open System
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

        member _.BuildAuthorizeUrl(_ctx, state, redirectUri) = async {
            let url =
                sprintf
                    "https://example.com/oauth/authorize?client_id=fake&response_type=code&state=%s&redirect_uri=%s"
                    (Uri.EscapeDataString state)
                    (Uri.EscapeDataString redirectUri)

            return Ok url
        }

        member _.ExchangeCode(_ctx, code, _redirectUri) = async {
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