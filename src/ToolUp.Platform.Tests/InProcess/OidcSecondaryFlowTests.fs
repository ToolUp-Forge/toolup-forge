// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.OidcSecondaryFlowTests

open Expecto
open ToolUp.Platform
open ToolUp.AuthProviders.Oidc.OidcAppConfig
open ToolUp.AuthProviders.Oidc.OidcPresets
open ToolUp.AuthProviders.Oidc.OidcCoherenceValidator
open ToolUp.AuthProviders.Oidc.OidcStateMachine

// ─── Secondary-flow ("Sign up") affordance ───────────────────────────
//
// A sign-in screen that offers a SECOND button — "Sign up" beside
// "Sign in" — is a hosted-journey affordance, not a second protocol.
// Both buttons run the same OIDC Authorization Code + PKCE sign-in
// against the same client id and the same redirect URI, land on the
// same callback, and store the same bearer. They differ in exactly one
// thing: the extra parameters appended to the authorize request, which
// is what an identity provider routes a second journey on — an Entra
// External ID sign-up user flow (`p=<policyId>`), a Google re-consent
// (`prompt=consent`).
//
// Four layers, cheapest first:
//
//   1. absence      — the slot is `None` everywhere it was not asked
//                     for, so the single-button shell is unchanged
//                     (GP 11);
//   2. attachment   — the helpers that build a flow, and its verbatim
//                     projection to the client tier;
//   3. the request  — the EXACT authorize parameter set each flow
//                     produces, including the parity check against the
//                     shell of the client-side Entra companion this
//                     affordance replaced (removed at Phase 749; the
//                     parameter set below is its recorded request);
//   4. rule 16      — the coherence validator's refusal of a flow that
//                     cannot work.
//
// The shell's rendering itself is browser-side (Feliz / React) and out
// of reach of the Expecto runner; what is asserted here is every input
// that decides it, and the Fable compile of `OidcClient.fsproj` is what
// proves the component itself still builds for the browser.

let private clientId = "secondary-flow-client"
let private redirectUri = "https://app.example.test/auth/callback"
let private issuer = "https://issuer.example.test"

let private handCfg = OidcAppConfig.create issuer clientId redirectUri

// ─── Layer 1 — absence is the default (GP 11) ────────────────────────

let private absence =
    testList "no secondary flow unless one is declared" [

        testCase "OidcAppConfig.create declares none"
        <| fun () -> Expect.isNone handCfg.SecondaryFlow "a hand-built config renders the single-button screen"

        testCase "OidcUIConfig.defaults declares none"
        <| fun () ->
            let cfg = OidcUIConfig.defaults issuer clientId redirectUri
            Expect.isNone cfg.SecondaryFlow "the client-tier default is the single-button screen"

        testCase "every preset declares none"
        <| fun () ->
            // A second button is a PRODUCT decision — which journeys a
            // deployment offers its users — not a provider quirk, so no
            // preset may supply one on the consumer's behalf. This is
            // the whole of the GP 11 guarantee for the affordance:
            // every existing preset consumer renders exactly the screen
            // it rendered before.
            let presets = [
                "generic", generic issuer clientId redirectUri
                "entraWorkforce", entraWorkforce "tenant-guid" clientId redirectUri
                "entraExternalId", entraExternalId "contoso" clientId redirectUri
                "entraExternalIdWithDomain",
                entraExternalIdWithDomain "contoso" "login.contoso.com" clientId redirectUri
                "auth0", auth0 "tenant.auth0.com" clientId redirectUri
                "google", google clientId redirectUri
            ]

            for name, cfg in presets do
                Expect.isNone cfg.SecondaryFlow $"preset %s{name} must not declare a secondary flow"
                Expect.isNone (OidcAppConfig.toClientConfig cfg).SecondaryFlow $"...nor project one (%s{name})"

        testCase "the primary flow's authorize request is unchanged by the slot existing"
        <| fun () ->
            // The parameter set a config with no secondary flow
            // produces is the standard set and nothing else — the
            // affordance adds no parameter to the flow that did not ask
            // for it.
            let request = {
                ClientId = clientId
                RedirectUri = redirectUri
                Scopes = [ "openid"; "profile"; "email" ]
                State = "state-value"
                Nonce = "nonce-value"
                CodeChallenge = "challenge-value"
            }

            let emitted = authorizeParams request [] |> List.map fst

            Expect.equal
                emitted
                SecondaryFlow.reservedAuthorizeParams
                "the standard parameter set IS the reserved list — the two must not drift, since rule 16 enforces the latter against the former"
    ]

// ─── Layer 2 — attaching a flow ──────────────────────────────────────

let private attachment =
    testList "attaching a secondary flow" [

        testCase "withSecondaryFlow carries label + extras verbatim"
        <| fun () ->
            let cfg = handCfg |> withSecondaryFlow "Sign up" [ "p", "B2C_1_signup" ]

            match cfg.SecondaryFlow with
            | None -> failtest "expected a declared secondary flow"
            | Some flow ->
                Expect.equal flow.Label "Sign up" "label"
                Expect.equal flow.ExtraAuthorizeParams [ "p", "B2C_1_signup" ] "extras"

        testCase "withSecondaryFlow changes nothing else on the config"
        <| fun () ->
            // The affordance is additive by construction: issuer,
            // audience, scopes, redirect URI, validation and bearer
            // strategy are the SAME sign-in, so an attached flow must
            // leave every one of them untouched.
            let cfg = handCfg |> withSecondaryFlow "Sign up" [ "p", "policy" ]

            Expect.equal { cfg with SecondaryFlow = None } handCfg "only the SecondaryFlow slot moved"

        testCase "withEntraSignUpUserFlow produces the documented `p` parameter"
        <| fun () ->
            let cfg =
                entraExternalId "contoso" clientId redirectUri
                |> withEntraSignUpUserFlow "B2C_1_signup"

            match cfg.SecondaryFlow with
            | None -> failtest "expected a declared secondary flow"
            | Some flow ->
                Expect.equal flow.Label "Sign up" "the companion's button text, ported"
                Expect.equal flow.ExtraAuthorizeParams [ "p", "B2C_1_signup" ] "Entra routes user flows on `p`"

        testCase "EntraUserFlowParameter is the routing key"
        <| fun () ->
            Expect.equal
                EntraUserFlowParameter
                "p"
                "pinned: this is the parameter the removed Entra companion shell passed to beginSignInWithExtras"

        testCase "toClientConfig projects the flow verbatim"
        <| fun () ->
            // Verbatim, unlike the bearer strategy which is RESOLVED on
            // the way through: there is nothing to resolve, because no
            // preset supplies a default to resolve against.
            let cfg = handCfg |> withSecondaryFlow "Create account" [ "screen_hint", "signup" ]
            let projected = OidcAppConfig.toClientConfig cfg

            Expect.equal projected.SecondaryFlow cfg.SecondaryFlow "the client tier sees exactly what was declared"

        testCase "a Google re-consent flow is expressible in the same slot"
        <| fun () ->
            // The slot is vendor-neutral by construction. Google's
            // refresh token is only issued on a user's FIRST consent
            // unless consent is re-prompted, so an explicit
            // "Re-consent" journey is a real second button for a Google
            // deployment — expressed with no Google-specific SDK
            // surface at all.
            let cfg =
                google clientId redirectUri
                |> withSecondaryFlow "Re-consent" [ "prompt", "consent" ]

            match cfg.SecondaryFlow with
            | None -> failtest "expected a declared secondary flow"
            | Some flow -> Expect.equal flow.ExtraAuthorizeParams [ "prompt", "consent" ] "Google's own knob, same slot"

            Expect.isEmpty (evaluate cfg |> List.filter RuleOutcome.isError) "and it raises no coherence error"
    ]

// ─── Layer 3 — the authorize request each button issues ──────────────

let private authorizeRequest =
    let request = {
        ClientId = clientId
        RedirectUri = redirectUri
        Scopes = [ "openid"; "profile"; "email"; "offline_access" ]
        State = "state-value"
        Nonce = "nonce-value"
        CodeChallenge = "challenge-value"
    }

    /// What the shell passes to `beginSignInWithExtras` for each
    /// button: `[]` for "Sign in", the declared extras for the
    /// secondary flow. This mirrors `OidcAuthUI.OidcShell`'s
    /// `beginFlow` / `beginSecondaryFlow` pair exactly — the point
    /// being that there is only ONE code path and the button chooses
    /// its extras.
    let extrasFor (cfg: OidcUIConfig) (secondary: bool) =
        if secondary then
            cfg.SecondaryFlow |> Option.map _.ExtraAuthorizeParams |> Option.defaultValue []
        else
            []

    testList "authorize-request parameter set" [

        testCase "the Entra sign-up flow issues the standard set plus `p`"
        <| fun () ->
            // The parity assertion, and since Phase 749 the sole
            // surviving record of the removed companion shell's request.
            // Its sign-up button called
            //     OidcClient.beginSignInWithExtras oidcConfig [ "p", policyId ]
            // so the authorize request it issued was the standard OAuth
            // / PKCE set with `p=<policyId>` appended. This case was
            // green against that shell before it was deleted (Phase
            // 749.A), and the literal below IS that request — so the
            // preset path stays held to it, parameter for parameter.
            let cfg =
                entraExternalId "contoso" clientId redirectUri
                |> withEntraSignUpUserFlow "B2C_1_signup"
                |> OidcAppConfig.toClientConfig

            let emitted = authorizeParams request (extrasFor cfg true)

            Expect.equal
                emitted
                [
                    "response_type", "code"
                    "client_id", clientId
                    "redirect_uri", redirectUri
                    "scope", "openid profile email offline_access"
                    "state", "state-value"
                    "nonce", "nonce-value"
                    "code_challenge", "challenge-value"
                    "code_challenge_method", "S256"
                    "p", "B2C_1_signup"
                ]
                "exact parameter set, in wire order — the companion's request reproduced"

        testCase "both buttons of a dual-button shell differ ONLY in the extras"
        <| fun () ->
            let cfg =
                entraExternalId "contoso" clientId redirectUri
                |> withEntraSignUpUserFlow "B2C_1_signup"
                |> OidcAppConfig.toClientConfig

            let primary = authorizeParams request (extrasFor cfg false)
            let secondary = authorizeParams request (extrasFor cfg true)

            Expect.equal
                secondary
                (primary @ [ "p", "B2C_1_signup" ])
                "same client id, redirect URI, scopes, state, nonce and PKCE challenge — one appended parameter"

        testCase "a config with no secondary flow cannot issue a second request shape"
        <| fun () ->
            let cfg =
                entraExternalId "contoso" clientId redirectUri |> OidcAppConfig.toClientConfig

            Expect.equal
                (authorizeParams request (extrasFor cfg true))
                (authorizeParams request (extrasFor cfg false))
                "no declared flow — the secondary path is inert (and the button is never rendered)"

        testCase "extras are appended, never merged into the standard set"
        <| fun () ->
            // The reason rule 16 exists: this function does not
            // de-duplicate, so a colliding key would be emitted twice.
            // Pinned so the validator's job stays necessary and visible.
            let emitted = authorizeParams request [ "scope", "hijacked" ] |> List.map fst

            Expect.equal (emitted |> List.filter ((=) "scope") |> List.length) 2 "a colliding key is emitted twice"
    ]

// ─── Layer 4 — coherence validator rule 16 ───────────────────────────

let private rule16 =
    let hasErrorMatching (substring: string) (outcomes: RuleOutcome list) =
        outcomes
        |> List.exists (function
            | RuleError m -> m.Contains substring
            | _ -> false)

    let hasWarningMatching (substring: string) (outcomes: RuleOutcome list) =
        outcomes
        |> List.exists (function
            | RuleWarning m -> m.Contains substring
            | _ -> false)

    testList "OidcCoherenceValidator rule 16" [

        testCase "a well-formed secondary flow raises nothing"
        <| fun () ->
            let cfg =
                entraExternalId "contoso" clientId redirectUri
                |> withEntraSignUpUserFlow "B2C_1_signup"

            let outcomes = evaluate cfg
            Expect.isEmpty (outcomes |> List.filter RuleOutcome.isError) "no errors"
            Expect.isEmpty (outcomes |> List.filter RuleOutcome.isWarning) "no warnings"

        testCase "an extras key colliding with a standard parameter → ERROR"
        <| fun () ->
            let cfg =
                handCfg
                |> withSecondaryFlow "Sign up" [ "redirect_uri", "https://attacker.example/cb" ]

            Expect.isTrue
                (hasErrorMatching "already emits itself" (evaluate cfg))
                "a duplicated `redirect_uri` is the callback's security, not a cosmetic detail"

        testCase "the colliding key is named in the finding"
        <| fun () ->
            let cfg =
                handCfg
                |> withSecondaryFlow "Sign up" [ "code_challenge", "attacker-challenge" ]

            Expect.isTrue (hasErrorMatching "code_challenge" (evaluate cfg)) "the operator is told which key"

        testCase "a blank label → WARNING"
        <| fun () ->
            let cfg = handCfg |> withSecondaryFlow "  " [ "p", "policy" ]
            Expect.isTrue (hasWarningMatching "blank Label" (evaluate cfg)) "an unlabelled button is half-configured"
            Expect.isEmpty (evaluate cfg |> List.filter RuleOutcome.isError) "but it does not break sign-in"

        testCase "no extra parameters → WARNING"
        <| fun () ->
            let cfg = handCfg |> withSecondaryFlow "Sign up" []

            Expect.isTrue
                (hasWarningMatching "no extra authorize parameters" (evaluate cfg))
                "two buttons issuing one request is a misconfiguration, not a feature"

        testCase "rule 16 is preset-independent"
        <| fun () ->
            // A hand-built config (no preset at all) declares a
            // secondary flow just as legitimately as a preset one, so
            // the rule must not live behind the preset match.
            Expect.isNone handCfg.Preset "precondition: no preset"

            let cfg = handCfg |> withSecondaryFlow "Sign up" [ "state", "forged" ]
            Expect.isTrue (hasErrorMatching "already emits itself" (evaluate cfg)) ""

        testCase "no declared flow → rule 16 is silent"
        <| fun () ->
            let outcomes = evaluate handCfg

            Expect.isFalse
                (hasErrorMatching "SecondaryFlow" outcomes
                 || hasWarningMatching "SecondaryFlow" outcomes)
                "the overwhelming majority of configs declare nothing and must hear nothing"

        testCase "collidingParams reports every collision, in declaration order"
        <| fun () ->
            let flow =
                SecondaryFlow.create "Sign up" [ "scope", "x"; "p", "policy"; "client_id", "y" ]

            Expect.equal (SecondaryFlow.collidingParams flow) [ "scope"; "client_id" ] "provider-specific keys pass"
    ]

let tests: Test =
    testList "OIDC secondary flow" [ absence; attachment; authorizeRequest; rule16 ]