// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.OidcDiagnoseTests

open Expecto
open ToolUp.AuthProviders.Oidc.OidcTypes
open ToolUp.AuthProviders.Oidc.OidcTokenStore

// ─── diagnose : AuthError -> AuthDiagnostic ──────────────────────────
//
// `diagnose` is the developer-facing structured counterpart to
// `describeError`. UI code consumes `describeError` (deliberately
// opaque on security-sensitive branches); structured logs + the auth
// tracer consume `diagnose` (carries the withheld sub-cause + an
// actionable hint).
//
// These tests pin the `Kind` strings, since they're stable identifiers
// downstream consumers tag metrics with — silently renaming one of
// them would invalidate every dashboard that filters on it. The
// per-variant cases also assert that the *security-sensitive* branches
// (signature, nonce, issuer, audience, expiry) carry a richer
// developer-facing sub-cause than `describeError` exposes to the UI.

let tests: Test =
    testList "OidcClient.diagnose" [

        testCase "DiscoveryFailed → kind DISCOVERY_FAILED + carries underlying message"
        <| fun () ->
            let d = diagnose (DiscoveryFailed "DNS timeout")
            Expect.equal d.Kind "DISCOVERY_FAILED" ""
            Expect.equal d.SubCause (Some "DNS timeout") ""
            Expect.isSome d.Hint "operators get a tenant-GUID-vs-domain hint"

        testCase "InvalidState → kind PKCE_STATE_MISMATCH + sub-cause names PKCE state"
        <| fun () ->
            let d = diagnose InvalidState
            Expect.equal d.Kind "PKCE_STATE_MISMATCH" ""
            Expect.isSome d.SubCause "developer must see the structural reason"
            Expect.stringContains d.SubCause.Value "state" "sub-cause must explicitly name `state`"

        testCase "MissingCode → kind CALLBACK_MISSING_CODE + hint points at error_description"
        <| fun () ->
            let d = diagnose MissingCode
            Expect.equal d.Kind "CALLBACK_MISSING_CODE" ""
            Expect.isSome d.Hint ""

            Expect.stringContains
                d.Hint.Value
                "error_description"
                "hint must point operator at the issuer's error fields"

        testCase "IssuerError carries the issuer code + description in the sub-cause"
        <| fun () ->
            let d = diagnose (IssuerError("invalid_grant", Some "redirect_uri mismatch"))
            Expect.equal d.Kind "ISSUER_RETURNED_ERROR" ""
            Expect.isSome d.SubCause ""
            Expect.stringContains d.SubCause.Value "invalid_grant" ""
            Expect.stringContains d.SubCause.Value "redirect_uri mismatch" ""

        testCase "IssuerError without description still names the code"
        <| fun () ->
            let d = diagnose (IssuerError("access_denied", None))
            Expect.equal d.Kind "ISSUER_RETURNED_ERROR" ""
            Expect.isSome d.SubCause ""
            Expect.stringContains d.SubCause.Value "access_denied" ""

        testCase "TokenExchangeFailed carries the underlying message + enumerates common causes"
        <| fun () ->
            let d = diagnose (TokenExchangeFailed "401 Unauthorized")
            Expect.equal d.Kind "TOKEN_EXCHANGE_FAILED" ""
            Expect.equal d.SubCause (Some "401 Unauthorized") ""
            Expect.isSome d.Hint ""
            Expect.stringContains d.Hint.Value "PKCE verifier" "hint must call out PKCE verifier as a common cause"

        testCase "NetworkError carries the underlying message"
        <| fun () ->
            let d = diagnose (NetworkError "Failed to fetch")
            Expect.equal d.Kind "NETWORK_ERROR" ""
            Expect.equal d.SubCause (Some "Failed to fetch") ""

        testCase "NonceMismatch (security-sensitive) → kind ID_TOKEN_NONCE_MISMATCH + names replay as a possibility"
        <| fun () ->
            let d = diagnose NonceMismatch
            Expect.equal d.Kind "ID_TOKEN_NONCE_MISMATCH" ""
            Expect.isSome d.SubCause "developer sees the structural reason the UI hides"
            Expect.isSome d.Hint ""
            Expect.stringContains d.Hint.Value "replay" "hint must surface the replay possibility"

        testCase "MalformedIdToken → kind ID_TOKEN_MALFORMED + sub-cause describes parse failure"
        <| fun () ->
            let d = diagnose MalformedIdToken
            Expect.equal d.Kind "ID_TOKEN_MALFORMED" ""
            Expect.isSome d.SubCause ""

        testCase "IdTokenSignatureInvalid (security-sensitive) → richer sub-cause than describeError"
        <| fun () ->
            let d = diagnose IdTokenSignatureInvalid
            let userFacing = describeError IdTokenSignatureInvalid
            Expect.equal d.Kind "ID_TOKEN_SIGNATURE_INVALID" ""
            Expect.isSome d.SubCause ""
            // anti-tampering stance: user message MUST stay opaque, dev sub-cause MUST NOT.
            Expect.stringContains d.SubCause.Value "JWKS" "developer sub-cause must name the JWKS-side reason"
            Expect.isFalse (userFacing.Contains "JWKS") "describeError must NOT leak JWKS reasoning to the UI"

        testCase "IdTokenIssuerInvalid → kind + hint references Entra tenant GUID pitfall"
        <| fun () ->
            let d = diagnose IdTokenIssuerInvalid
            Expect.equal d.Kind "ID_TOKEN_ISSUER_INVALID" ""
            Expect.isSome d.Hint ""

            Expect.stringContains
                d.Hint.Value
                "tenantGuid"
                "hint must reference the workforce-Entra GUID-vs-domain pitfall"

        testCase "IdTokenAudienceInvalid → hint references the workforce-Entra access_as_user scope"
        <| fun () ->
            let d = diagnose IdTokenAudienceInvalid
            Expect.equal d.Kind "ID_TOKEN_AUDIENCE_INVALID" ""
            Expect.isSome d.Hint ""
            Expect.stringContains d.Hint.Value "access_as_user" "hint must name the load-bearing workforce-Entra scope"

        testCase "IdTokenExpired → kind ID_TOKEN_EXPIRED + names clock-skew tolerance"
        <| fun () ->
            let d = diagnose IdTokenExpired
            Expect.equal d.Kind "ID_TOKEN_EXPIRED" ""
            Expect.isSome d.SubCause ""

            Expect.stringContains
                d.SubCause.Value
                "clock-skew"
                "sub-cause must reference the documented clock-skew tolerance"

        testCase "every kind is unique (would-be silent renames are caught)"
        <| fun () ->
            // If two AuthError variants ever collapse onto the same Kind
            // string, downstream metrics double-count one and zero-count
            // the other. This pins the surface against silent regressions.
            let allKinds = [
                (diagnose (DiscoveryFailed "")).Kind
                (diagnose InvalidState).Kind
                (diagnose MissingCode).Kind
                (diagnose (IssuerError("", None))).Kind
                (diagnose (TokenExchangeFailed "")).Kind
                (diagnose (NetworkError "")).Kind
                (diagnose NonceMismatch).Kind
                (diagnose MalformedIdToken).Kind
                (diagnose IdTokenSignatureInvalid).Kind
                (diagnose IdTokenIssuerInvalid).Kind
                (diagnose IdTokenAudienceInvalid).Kind
                (diagnose IdTokenExpired).Kind
            ]

            Expect.equal (List.length (List.distinct allKinds)) (List.length allKinds) ""
    ]