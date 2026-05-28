module ToolUp.Platform.Tests.Contracts.IOAuthCredentialFlowContract

open System
open Expecto
open ToolUp.Platform

// ─── IOAuthCredentialFlow contract pack ────────────────────────────
//
// Parametrised tests for any `IOAuthCredentialFlow` implementation.
// The factory returns the flow under test; tests do not reach the
// network — implementations bound to this pack should pass an
// in-process fake of the upstream provider's HTTP layer, mirroring
// `MockAzureBlobStorage` patterns elsewhere in the test suite.
//
// **What's covered:**
//   - `Name` is non-empty (URL-segment dispatch key).
//   - `Descriptor.DisplayName` and `Descriptor.Scopes` are populated.
//   - `BuildAuthorizeUrl` returns an absolute HTTPS URL containing
//     the substrate-supplied `state` and `redirect_uri` parameters.
//   - `ExchangeCode` happy-path returns `OAuthCredentials` with a
//     non-empty `RefreshToken`.
//   - `RefreshAccessToken` happy-path returns `OAuthAccessToken` with
//     a future `ExpiresAt`.
//   - `Revoke` returns `Ok` or `RevocationUnsupported` (never throws).
//
// **What's NOT covered (provider-specific):**
//   - Concrete URL construction (Google's `access_type=offline`,
//     Microsoft's tenant-id substitution, etc.).
//   - Error-classification correctness against real upstream
//     responses — that's covered by each provider's own integration
//     tests, env-gated against real credentials.

let tests (name: string) (factory: unit -> IOAuthCredentialFlow) =

    let mkContext () : OAuthFlowContext = {
        ScopeId = "team-scope-1"
        DataSourceId = "ds-1"
        Config = None
    }

    testList $"{name} — IOAuthCredentialFlow contract" [

        test "Name is non-empty" {
            let flow = factory ()
            Expect.isNonEmpty flow.Name "Name discriminator must not be empty"
        }

        test "Descriptor has DisplayName and Scopes populated" {
            let flow = factory ()
            Expect.isNonEmpty flow.Descriptor.DisplayName "DisplayName must not be empty"
            Expect.isNonEmpty flow.Descriptor.Scopes "Scopes must list at least one upstream scope"
        }

        testCaseAsync "BuildAuthorizeUrl returns an absolute HTTPS URL"
        <| async {
            let flow = factory ()
            let ctx = mkContext ()
            let state = "test-state-token"
            let redirect = "https://example.com/api/oauth/test-flow/callback"

            match! flow.BuildAuthorizeUrl(ctx, state, redirect) with
            | Ok url ->
                let parsed = Uri url
                Expect.isTrue parsed.IsAbsoluteUri "URL must be absolute"
                Expect.equal parsed.Scheme "https" "URL must be HTTPS (RFC 6749 §3.1.2.1)"
            | Error err -> failtestf "BuildAuthorizeUrl failed: %s" (OAuthError.toMessage err)
        }

        testCaseAsync "BuildAuthorizeUrl forwards the substrate's state and redirect_uri"
        <| async {
            let flow = factory ()
            let ctx = mkContext ()
            let state = "unique-state-9X3pQ"
            let redirect = "https://example.com/api/oauth/test-flow/callback"

            match! flow.BuildAuthorizeUrl(ctx, state, redirect) with
            | Ok url ->
                Expect.stringContains url state "URL contains the substrate-supplied state"
                Expect.stringContains url (Uri.EscapeDataString redirect) "URL contains the URL-encoded redirect_uri"
            | Error err -> failtestf "BuildAuthorizeUrl failed: %s" (OAuthError.toMessage err)
        }

        testCaseAsync "ExchangeCode happy-path returns credentials with a refresh token"
        <| async {
            let flow = factory ()
            let ctx = mkContext ()
            let redirect = "https://example.com/api/oauth/test-flow/callback"

            match! flow.ExchangeCode(ctx, "valid-test-code", redirect) with
            | Ok creds -> Expect.isNonEmpty creds.RefreshToken "RefreshToken must not be empty"
            | Error err -> failtestf "ExchangeCode failed: %s" (OAuthError.toMessage err)
        }

        testCaseAsync "RefreshAccessToken returns a token with future expiry"
        <| async {
            let flow = factory ()
            let ctx = mkContext ()

            match! flow.RefreshAccessToken(ctx, "valid-refresh-token") with
            | Ok token ->
                Expect.isNonEmpty token.Token "Access token must not be empty"
                Expect.isTrue (token.ExpiresAt > DateTime.UtcNow) "ExpiresAt must be in the future"
            | Error err -> failtestf "RefreshAccessToken failed: %s" (OAuthError.toMessage err)
        }

        testCaseAsync "Revoke returns Ok or RevocationUnsupported (never throws)"
        <| async {
            let flow = factory ()
            let ctx = mkContext ()

            // Best-effort revocation — both Ok and RevocationUnsupported
            // are valid outcomes for the contract. A network error is
            // also fine; what's NOT fine is an unhandled exception.
            try
                let! _ = flow.Revoke(ctx, "valid-refresh-token")
                ()
            with ex ->
                failtestf "Revoke threw an unhandled exception: %s" ex.Message
        }
    ]