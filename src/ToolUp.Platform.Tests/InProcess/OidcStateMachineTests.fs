// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.OidcStateMachineTests

open Expecto
open ToolUp.Platform
open ToolUp.AuthProviders.Oidc.OidcTypes
open ToolUp.AuthProviders.Oidc.OidcStateMachine

// ─── OidcStateMachine — Stage labels + pure decisions ────────────────
//
// The state-machine module exposes the named `Stage` graph + two pure
// decision functions that `handleCallback` defers to so .NET-side
// Expecto can exercise every branch without a Fable browser shim:
//
//   * `decideCallbackStart` — initial URL + PKCE-stash validation.
//   * `decideNonceValidity` — id_token nonce-binding (replay defence).
//
// `stageLabel` is also pinned here because downstream consumers tag
// metrics by the label string; a silent rename would invalidate every
// dashboard that filters on it.

let private validCallback: CallbackInputs = {
    UrlCode = Some "auth-code-xyz"
    UrlState = Some "stashed-state-abc"
    UrlError = None
    UrlErrorDescription = None
    StashedVerifier = Some "pkce-verifier-123"
    StashedState = Some "stashed-state-abc"
    StashedNonce = Some "nonce-456"
}

let tests: Test =
    testList "OidcClient.OidcStateMachine" [

        // ─── stageLabel — stable string pin ─────────────────────────

        testList "stageLabel" [
            testCase "every Stage maps to a non-empty, stable label"
            <| fun () ->
                let cases: (Stage * string) list = [
                    Booting, "booting"
                    InCallback, "in-callback"
                    ExchangingCode, "exchanging-code"
                    ValidatingIdToken, "validating-id-token"
                    PersistingTokens, "persisting-tokens"
                    RebootingToRoot, "rebooting-to-root"
                    ClassifyingStored, "classifying-stored"
                    Refreshing, "refreshing"
                    Established, "established"
                    Unauthenticated, "unauthenticated"
                    Stage.Failed MissingCode, "failed"
                ]

                for stage, expected in cases do
                    Expect.equal (stageLabel stage) expected (sprintf "label for %A" stage)

            testCase "labels are unique across Stage cases"
            <| fun () ->
                let allLabels = [
                    stageLabel Booting
                    stageLabel InCallback
                    stageLabel ExchangingCode
                    stageLabel ValidatingIdToken
                    stageLabel PersistingTokens
                    stageLabel RebootingToRoot
                    stageLabel ClassifyingStored
                    stageLabel Refreshing
                    stageLabel Established
                    stageLabel Unauthenticated
                    stageLabel (Stage.Failed MissingCode)
                ]

                Expect.equal (List.length (List.distinct allLabels)) (List.length allLabels) ""

            testCase "Failed projects to the SAME label regardless of inner AuthError"
            <| fun () ->
                // The label is for metric tagging; the inner error
                // detail goes in the AuthTransition.Outcome diagnostic.
                // Separating the two means a dashboard can count
                // "failed" sign-ins without exploding cardinality on
                // every error variant.
                let errors = [
                    MissingCode
                    InvalidState
                    NonceMismatch
                    IdTokenAudienceInvalid
                    DiscoveryFailed "x"
                    TokenExchangeFailed "y"
                ]

                for e in errors do
                    Expect.equal (stageLabel (Stage.Failed e)) "failed" ""
        ]

        // ─── toAuthState — projection to view-model ─────────────────

        testList "toAuthState" [
            testCase "every non-terminal Stage projects to Checking"
            <| fun () ->
                let transient = [
                    Booting
                    InCallback
                    ExchangingCode
                    ValidatingIdToken
                    PersistingTokens
                    RebootingToRoot
                    ClassifyingStored
                    Refreshing
                ]

                for s in transient do
                    Expect.equal (toAuthState s) Checking (sprintf "%A → Checking" s)

            testCase "Established projects to SignedIn"
            <| fun () -> Expect.equal (toAuthState Established) SignedIn ""

            testCase "Unauthenticated projects to SignedOut"
            <| fun () -> Expect.equal (toAuthState Unauthenticated) SignedOut ""

            testCase "Failed e projects to AuthState.Failed e (carries the error)"
            <| fun () ->
                let projected = toAuthState (Stage.Failed NonceMismatch)
                Expect.equal projected (AuthState.Failed NonceMismatch) ""
        ]

        // ─── isTerminal predicate ───────────────────────────────────

        testList "isTerminal" [
            testCase "Established / Unauthenticated / Failed are terminal"
            <| fun () ->
                Expect.isTrue (isTerminal Established) ""
                Expect.isTrue (isTerminal Unauthenticated) ""
                Expect.isTrue (isTerminal (Stage.Failed MissingCode)) ""

            testCase "every other Stage is non-terminal"
            <| fun () ->
                let transient = [
                    Booting
                    InCallback
                    ExchangingCode
                    ValidatingIdToken
                    PersistingTokens
                    RebootingToRoot
                    ClassifyingStored
                    Refreshing
                ]

                for s in transient do
                    Expect.isFalse (isTerminal s) (sprintf "%A should be non-terminal" s)
        ]

        // ─── decideCallbackStart ────────────────────────────────────

        testList "decideCallbackStart" [
            testCase "happy path: code + state match + verifier present"
            <| fun () ->
                match decideCallbackStart validCallback with
                | Ok start ->
                    Expect.equal start.Code "auth-code-xyz" ""
                    Expect.equal start.Verifier "pkce-verifier-123" ""
                    Expect.equal start.ExpectedNonce (Some "nonce-456") ""
                | Error e -> failtestf "expected Ok, got Error %A" e

            testCase "issuer error has priority over local checks"
            <| fun () ->
                // Even if everything else looks fine, an issuer-side
                // error short-circuits with IssuerError carrying the
                // issuer's code + description.
                let inputs = {
                    validCallback with
                        UrlError = Some "access_denied"
                        UrlErrorDescription = Some "user cancelled"
                }

                Expect.equal
                    (decideCallbackStart inputs)
                    (Error(IssuerError("access_denied", Some "user cancelled")))
                    ""

            testCase "issuer error without description"
            <| fun () ->
                let inputs = {
                    validCallback with
                        UrlError = Some "server_error"
                        UrlErrorDescription = None
                }

                Expect.equal (decideCallbackStart inputs) (Error(IssuerError("server_error", None))) ""

            testCase "missing code on URL → MissingCode"
            <| fun () ->
                let inputs = { validCallback with UrlCode = None }
                Expect.equal (decideCallbackStart inputs) (Error MissingCode) ""

            testCase "missing state on URL → InvalidState"
            <| fun () ->
                let inputs = { validCallback with UrlState = None }
                Expect.equal (decideCallbackStart inputs) (Error InvalidState) ""

            testCase "missing PKCE verifier in stash → InvalidState"
            <| fun () ->
                let inputs = {
                    validCallback with
                        StashedVerifier = None
                }

                Expect.equal (decideCallbackStart inputs) (Error InvalidState) ""

            testCase "missing stashed state → InvalidState"
            <| fun () ->
                let inputs = {
                    validCallback with
                        StashedState = None
                }

                Expect.equal (decideCallbackStart inputs) (Error InvalidState) ""

            testCase "state mismatch (URL vs stash) → InvalidState"
            <| fun () ->
                // CSRF / replay defence — the OAuth `state` parameter
                // round-trips a random token that ties the callback
                // back to the originating authorize request.
                let inputs = {
                    validCallback with
                        UrlState = Some "tampered-state-zzz"
                }

                Expect.equal (decideCallbackStart inputs) (Error InvalidState) ""

            testCase "happy path with no stashed nonce returns ExpectedNonce=None"
            <| fun () ->
                // Substrate-defensive: an absent stashed nonce
                // propagates through so the subsequent nonce-validity
                // check can accept (nothing to bind to). Production
                // always stashes a nonce, but this exercises the
                // option-handling path.
                let inputs = {
                    validCallback with
                        StashedNonce = None
                }

                match decideCallbackStart inputs with
                | Ok start -> Expect.equal start.ExpectedNonce None ""
                | Error e -> failtestf "expected Ok, got %A" e
        ]

        // ─── decideNonceValidity ────────────────────────────────────

        testList "decideNonceValidity" [
            let equals (a: string) (b: string) = a = b

            testCase "no stashed nonce → accept (substrate-defensive)"
            <| fun () ->
                let inputs: NonceInputs = {
                    StashedNonce = None
                    IdTokenPresent = false
                    IdTokenNonce = None
                }

                Expect.equal (decideNonceValidity equals inputs) (Ok()) ""

            testCase "stashed nonce + no id_token returned → NonceMismatch (issuer spec violation)"
            <| fun () ->
                // We requested `openid` scope so the issuer MUST
                // return an id_token. If they didn't, we can't bind
                // and we don't trust the access token unbacked.
                let inputs: NonceInputs = {
                    StashedNonce = Some "n-123"
                    IdTokenPresent = false
                    IdTokenNonce = None
                }

                Expect.equal (decideNonceValidity equals inputs) (Error NonceMismatch) ""

            testCase "stashed nonce + id_token present without nonce claim → NonceMismatch"
            <| fun () ->
                let inputs: NonceInputs = {
                    StashedNonce = Some "n-123"
                    IdTokenPresent = true
                    IdTokenNonce = None
                }

                Expect.equal (decideNonceValidity equals inputs) (Error NonceMismatch) ""

            testCase "stashed nonce + matching id_token nonce → Ok (replay-defence pass)"
            <| fun () ->
                let inputs: NonceInputs = {
                    StashedNonce = Some "n-123"
                    IdTokenPresent = true
                    IdTokenNonce = Some "n-123"
                }

                Expect.equal (decideNonceValidity equals inputs) (Ok()) ""

            testCase "stashed nonce + mismatched id_token nonce → NonceMismatch (REPLAY)"
            <| fun () ->
                // The defining failure case the nonce-binding test
                // exists to catch — a different sign-in flow's id_token
                // replayed at this callback URL.
                let inputs: NonceInputs = {
                    StashedNonce = Some "n-flow-A"
                    IdTokenPresent = true
                    IdTokenNonce = Some "n-flow-B"
                }

                Expect.equal (decideNonceValidity equals inputs) (Error NonceMismatch) ""

            testCase "equals function is invoked exactly once per evaluation"
            <| fun () ->
                // The production caller passes `fixedTimeStringEquals`
                // (constant-time compare against timing attacks). The
                // pure decision function must not bypass it or call it
                // multiple times in ways that defeat the guarantee.
                let mutable invocations = 0

                let countingEquals (a: string) (b: string) =
                    invocations <- invocations + 1
                    a = b

                let inputs: NonceInputs = {
                    StashedNonce = Some "x"
                    IdTokenPresent = true
                    IdTokenNonce = Some "x"
                }

                decideNonceValidity countingEquals inputs |> ignore
                Expect.equal invocations 1 ""
        ]
    ]