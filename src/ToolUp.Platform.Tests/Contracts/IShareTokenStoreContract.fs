module ToolUp.Platform.Tests.Contracts.IShareTokenStoreContract

open System
open Expecto
open ToolUp.Platform

// ─── IShareTokenStore contract pack ──────────────────────────────────
//
// Parametrised tests for any `IShareTokenStore` implementation. Each
// test asks the factory for a fresh `(store, scopeA, scopeB)` triple
// so concurrent runs against a shared substrate cannot interfere
// (scopes are GUID-suffixed by convention in the binding).
//
// Coverage targets the interface contract — issue / validate / mark-
// used / revoke / list. Wire-format details (HMAC over JSON, base64url
// of payload + signature) are an implementation concern of
// `BlobShareTokenStore` and out of scope here.

let private mkRequest scopeId resourceKind resourceId attributedHandle : ShareTokenIssueRequest = {
    ScopeId = scopeId
    ResourceKind = resourceKind
    ResourceId = resourceId
    AttributedHandle = attributedHandle
    IssuedBy = "alice"
    ExpiresAt = None
    UseLimit = None
    RateLimit = None
}

let private okOrFail label result =
    match result with
    | Ok v -> v
    | Error err -> failtestf "%s: expected Ok, got %A" label err

let tests (name: string) (factory: unit -> IShareTokenStore * string * string) =

    testList $"{name} — IShareTokenStore contract" [

        // ─── Issue → Validate round-trip ──────────────────────────

        testCaseAsync "Issue returns a token whose Validate yields the same claim"
        <| async {
            let store, scopeA, _ = factory ()
            let req = mkRequest scopeA "forms.publishable" "form-1" (Some "alice@example.com")
            let! issued = store.Issue req
            let token = okOrFail "Issue" issued

            Expect.equal token.Claim.ScopeId scopeA "ScopeId echoed"
            Expect.equal token.Claim.ResourceKind "forms.publishable" "ResourceKind echoed"
            Expect.equal token.Claim.ResourceId "form-1" "ResourceId echoed"
            Expect.equal token.Claim.AttributedHandle (Some "alice@example.com") "AttributedHandle echoed"
            Expect.equal token.Claim.IssuedBy "alice" "IssuedBy echoed"
            Expect.equal token.Claim.UsedCount 0 "UsedCount starts at 0"
            Expect.isFalse token.Claim.Revoked "fresh token is not revoked"

            let! validated = store.Validate token.Token

            match validated with
            | Ok claim -> Expect.equal claim.TokenId token.Claim.TokenId "Validate returns same TokenId"
            | Error err -> failtestf "Validate failed: %A" err
        }

        // ─── Default lifetime + use limit ─────────────────────────

        testCaseAsync "Issue applies DefaultUseLimit when caller passes None"
        <| async {
            let store, scopeA, _ = factory ()
            let req = mkRequest scopeA "forms.publishable" "form-default-uselimit" None
            let! issued = store.Issue req
            let token = okOrFail "Issue" issued

            Expect.equal token.Claim.UseLimit ShareTokenTypes.DefaultUseLimit "default UseLimit applied"
        }

        testCaseAsync "Issue with explicit Some None UseLimit produces unlimited"
        <| async {
            let store, scopeA, _ = factory ()

            let req = {
                mkRequest scopeA "forms.publishable" "form-unlimited" None with
                    UseLimit = Some None
            }

            let! issued = store.Issue req
            let token = okOrFail "Issue" issued

            Expect.equal token.Claim.UseLimit None "explicit Some None → unlimited"
        }

        // ─── Validate failure cases ───────────────────────────────

        testCaseAsync "Validate rejects malformed tokens"
        <| async {
            let store, _, _ = factory ()

            let! result = store.Validate "not-a-valid-token"

            match result with
            | Error ShareTokenError.Malformed -> ()
            | other -> failtestf "Expected Malformed, got %A" other
        }

        testCaseAsync "Validate rejects tampered signatures"
        <| async {
            let store, scopeA, _ = factory ()
            let req = mkRequest scopeA "forms.publishable" "form-tamper" None
            let! issued = store.Issue req
            let token = okOrFail "Issue" issued
            // Flip the FIRST character of the signature segment.
            //
            // Why not the last char: a 32-byte HMAC base64url-encodes
            // to 43 chars without padding; the trailing char carries
            // only 4 data bits + 2 padding-zero bits. Flipping `A`
            // (canonical for value 0 / `000000`) to `B` (`000001`)
            // changes only the padding bits — lenient base64 decoders
            // drop those, the decoded bytes are unchanged, the HMAC
            // matches, and Validate returns Ok. Roughly 1 in 16
            // HMACs end in `A`, surfacing as a 6%-flake in the suite.
            //
            // The first char is always in a full-data position
            // (bits 0-5 of byte 0), so flipping always changes real
            // data bits and the HMAC verification fails deterministically.
            let parts = token.Token.Split('.')
            let sig' = parts[2]

            let flipped = (if sig'[0] = 'A' then "B" else "A") + sig'.Substring(1)

            let tampered = sprintf "%s.%s.%s" parts[0] parts[1] flipped

            let! result = store.Validate tampered

            match result with
            | Error ShareTokenError.InvalidSignature -> ()
            | other -> failtestf "Expected InvalidSignature, got %A" other
        }

        // ─── Use-limit semantics ──────────────────────────────────

        testCaseAsync "MarkUsed bumps UsedCount; second call past UseLimit=1 fails"
        <| async {
            let store, scopeA, _ = factory ()
            let req = mkRequest scopeA "forms.publishable" "form-uselimit" None
            let! issued = store.Issue req
            let token = okOrFail "Issue" issued

            // First MarkUsed succeeds.
            let! firstUse = store.MarkUsed(scopeA, token.Claim.TokenId)
            okOrFail "first MarkUsed" firstUse

            // Validate now reports UseLimitExceeded because UsedCount == UseLimit.
            let! reValidate = store.Validate token.Token

            match reValidate with
            | Error ShareTokenError.UseLimitExceeded -> ()
            | other -> failtestf "Expected UseLimitExceeded after first use, got %A" other

            // Second MarkUsed fails because UsedCount + 1 would exceed UseLimit.
            let! secondUse = store.MarkUsed(scopeA, token.Claim.TokenId)

            match secondUse with
            | Error ShareTokenError.UseLimitExceeded -> ()
            | other -> failtestf "Expected UseLimitExceeded on second MarkUsed, got %A" other
        }

        // ─── Revocation ───────────────────────────────────────────

        testCaseAsync "Revoke marks token RevokedToken on subsequent Validate"
        <| async {
            let store, scopeA, _ = factory ()
            let req = mkRequest scopeA "forms.publishable" "form-revoke" None
            let! issued = store.Issue req
            let token = okOrFail "Issue" issued

            let! revoked = store.Revoke(scopeA, token.Claim.TokenId, "admin")
            okOrFail "Revoke" revoked

            let! validated = store.Validate token.Token

            match validated with
            | Error ShareTokenError.RevokedToken -> ()
            | other -> failtestf "Expected RevokedToken after Revoke, got %A" other
        }

        testCaseAsync "Revoke is idempotent — second call returns Ok"
        <| async {
            let store, scopeA, _ = factory ()
            let req = mkRequest scopeA "forms.publishable" "form-revoke-idempotent" None
            let! issued = store.Issue req
            let token = okOrFail "Issue" issued

            let! firstRevoke = store.Revoke(scopeA, token.Claim.TokenId, "admin")
            okOrFail "first Revoke" firstRevoke

            let! secondRevoke = store.Revoke(scopeA, token.Claim.TokenId, "admin")

            match secondRevoke with
            | Ok() -> ()
            | Error err -> failtestf "Second Revoke should be idempotent Ok, got %A" err
        }

        // ─── Listing ──────────────────────────────────────────────

        testCaseAsync "ListByResource returns all tokens for that resource"
        <| async {
            let store, scopeA, _ = factory ()

            let req1 =
                mkRequest scopeA "forms.publishable" "form-list" (Some "alice@example.com")

            let req2 = mkRequest scopeA "forms.publishable" "form-list" (Some "bob@example.com")

            let req3 =
                mkRequest scopeA "forms.publishable" "form-other" (Some "carol@example.com")

            let! _ = store.Issue req1
            let! _ = store.Issue req2
            let! _ = store.Issue req3

            let! listed = store.ListByResource(scopeA, "forms.publishable", "form-list")
            Expect.equal listed.Length 2 "two tokens for form-list"

            let handles = listed |> List.choose _.AttributedHandle |> List.sort
            Expect.equal handles [ "alice@example.com"; "bob@example.com" ] "both handles present"
        }

        testCaseAsync "ListByResource scope-isolates — scopeA tokens not visible from scopeB"
        <| async {
            let store, scopeA, scopeB = factory ()
            let req = mkRequest scopeA "forms.publishable" "form-iso" (Some "alice@example.com")
            let! _ = store.Issue req

            let! fromB = store.ListByResource(scopeB, "forms.publishable", "form-iso")
            Expect.equal fromB.Length 0 "scopeB sees none of scopeA's tokens"
        }

        // ─── Cross-resource splice rejection ──────────────────────

        testCaseAsync "Validate rejects tokens whose resource was substituted"
        <| async {
            let store, scopeA, _ = factory ()
            // Issue two tokens for different resources, then try to use
            // token A's prefix with token B's signature segment — the
            // tokenId-prefix mismatch should trip InvalidSignature.
            let! a = store.Issue(mkRequest scopeA "forms.publishable" "form-A" None)
            let! b = store.Issue(mkRequest scopeA "forms.publishable" "form-B" None)
            let tA = (okOrFail "Issue A" a).Token
            let tB = (okOrFail "Issue B" b).Token
            let aParts = tA.Split('.')
            let bParts = tB.Split('.')
            let frankenstein = sprintf "%s.%s.%s" aParts[0] bParts[1] bParts[2]

            let! result = store.Validate frankenstein

            match result with
            | Error ShareTokenError.InvalidSignature
            | Error ShareTokenError.Malformed -> ()
            | other -> failtestf "Expected InvalidSignature/Malformed for frankenstein token, got %A" other
        }
    ]