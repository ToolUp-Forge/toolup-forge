// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.GoogleIdentityCompanionTests

open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
// The bridge speaks the redirect flow's `AuthError` vocabulary rather
// than a companion-local error DU — one error screen serves both entry
// points, so the assertions below name those cases directly.
open ToolUp.AuthProviders.Oidc.OidcTypes
open ToolUp.AuthProviders.GoogleIdentity.GoogleIdentityConfig
open ToolUp.AuthProviders.GoogleIdentity.GoogleIdentityClient

// ─── Google Identity Services companion ──────────────────────────────
//
// Three surfaces, all reachable from .NET:
//
//  1. The credential bridge's decision function
//     (`evaluateCredentialWith`) — the pure core of "may this GIS
//     credential become the session bearer". The browser wrapper's
//     collaborators (the clock, the base64/JSON decoder, the callback
//     payload) are parameters precisely so the rules are pinned here
//     rather than only in a browser someone remembered to open. The
//     stubbed decoder IS the stubbed GIS callback: in production the
//     only difference is where the string came from.
//  2. The CSP contributor's declared sources.
//  3. The CSP preflight validator's warn / Ok decision over a live
//     `IServiceCollection`.
//
// Precedent for reaching a client-tier companion from the .NET pack:
// `OidcClassifyTokenTests` over `classifyStoredTokenWith`.

let private clientId = "1234567890-abcdefg.apps.googleusercontent.com"

let private cfg: OidcUIConfig =
    GoogleIdentityUIConfig.toOidcUIConfig (GoogleIdentityUIConfig.create clientId)

let private nowSeconds = 1_800_000_000.0 // fixed anchor (~2027); tests roll exp around it.
let private futureExp = nowSeconds + 3600.0
let private pastExp = nowSeconds - 3600.0

/// A credential shaped like a JWT — three dot-separated segments. The
/// content is irrelevant: every test supplies its own decoder.
let private jwtShaped = "header.payload.signature"

let private claims iss aud exp nonce = {
    Iss = iss
    Aud = aud
    Exp = exp
    Nonce = nonce
}

/// The claim set a healthy GIS credential carries for this app.
let private goodClaims =
    claims (Some "https://accounts.google.com") (Some clientId) (Some futureExp) None

let private decoderReturning (c: GoogleCredentialClaims) : string -> GoogleCredentialClaims option = fun _ -> Some c

let private undecodable: string -> GoogleCredentialClaims option = fun _ -> None

let private evaluate credential decoder =
    evaluateCredentialWith cfg None nowSeconds credential decoder

let private evaluateWithNonce expected credential decoder =
    evaluateCredentialWith cfg expected nowSeconds credential decoder

let private bridgeTests =
    testList "GoogleIdentityClient.evaluateCredentialWith" [

        testCase "healthy credential → AcceptCredential carrying the id_token"
        <| fun () ->
            let result = evaluate (Some jwtShaped) (decoderReturning goodClaims)

            Expect.equal
                result
                (AcceptCredential jwtShaped)
                "a credential whose iss / aud / exp all match the configuration is the session bearer"

        testCase "no credential on the callback payload → MissingCode"
        <| fun () ->
            let result = evaluate None (decoderReturning goodClaims)

            Expect.equal
                result
                (RejectCredential MissingCode)
                "an absent credential is the GIS analogue of a missing code"

        testCase "empty / whitespace credential → MissingCode"
        <| fun () ->
            let result = evaluate (Some "   ") (decoderReturning goodClaims)

            Expect.equal
                result
                (RejectCredential MissingCode)
                "a blank credential string is absent, not malformed — the callback fired with nothing in it"

        testCase "credential that is not JWT-shaped → MalformedIdToken"
        <| fun () ->
            let result = evaluate (Some "not-a-jwt") (decoderReturning goodClaims)

            Expect.equal result (RejectCredential MalformedIdToken) "fewer than three dot-segments cannot be a JWS"

        testCase "JWT-shaped but undecodable payload → MalformedIdToken, NOT accepted"
        <| fun () ->
            // Deliberately the OPPOSITE call from `classifyStoredToken`,
            // which defers an undecodable 3-segment token to the server as
            // `OpaqueToken`. Here we are deciding what to MAKE the bearer,
            // and a value whose claims cannot be read cannot be bound to
            // this application at all.
            let result = evaluate (Some jwtShaped) undecodable

            Expect.equal
                result
                (RejectCredential MalformedIdToken)
                "an unreadable credential is refused rather than deferred — the bridge is admitting, not classifying"

        testCase "issuer from another provider → IdTokenIssuerInvalid"
        <| fun () ->
            let wrong =
                claims (Some "https://login.microsoftonline.com/tenant/v2.0") (Some clientId) (Some futureExp) None

            let result = evaluate (Some jwtShaped) (decoderReturning wrong)

            Expect.equal
                result
                (RejectCredential IdTokenIssuerInvalid)
                "a credential minted by a different IdP is never this session's bearer"

        testCase "missing iss claim → IdTokenIssuerInvalid"
        <| fun () ->
            let wrong = claims None (Some clientId) (Some futureExp) None
            let result = evaluate (Some jwtShaped) (decoderReturning wrong)

            Expect.equal
                result
                (RejectCredential IdTokenIssuerInvalid)
                "absent is not the same as matching — an unstated issuer fails the check"

        testCase "bare-host `accounts.google.com` iss is accepted"
        <| fun () ->
            // Google emits BOTH forms depending on surface and vintage,
            // and documents both as correct. A scheme-strict comparison
            // here would reject live, valid credentials.
            let bareHost =
                claims (Some "accounts.google.com") (Some clientId) (Some futureExp) None

            let result = evaluate (Some jwtShaped) (decoderReturning bareHost)

            Expect.equal
                result
                (AcceptCredential jwtShaped)
                "the schemeless issuer form Google also emits must not be treated as a foreign issuer"

        testCase "trailing slash on iss is tolerated"
        <| fun () ->
            let trailing =
                claims (Some "https://accounts.google.com/") (Some clientId) (Some futureExp) None

            let result = evaluate (Some jwtShaped) (decoderReturning trailing)
            Expect.equal result (AcceptCredential jwtShaped) "a trailing slash is not a different issuer"

        testCase "credential addressed to a different client id → IdTokenAudienceInvalid"
        <| fun () ->
            let wrong =
                claims (Some "https://accounts.google.com") (Some "someone-elses-client-id") (Some futureExp) None

            let result = evaluate (Some jwtShaped) (decoderReturning wrong)

            Expect.equal
                result
                (RejectCredential IdTokenAudienceInvalid)
                "a Google-issued credential for ANOTHER application is the case audience binding exists to catch"

        testCase "expired credential → IdTokenExpired"
        <| fun () ->
            let stale =
                claims (Some "https://accounts.google.com") (Some clientId) (Some pastExp) None

            let result = evaluate (Some jwtShaped) (decoderReturning stale)
            Expect.equal result (RejectCredential IdTokenExpired) "a dead credential never becomes a live session"

        testCase "credential expiring inside the clock-skew window is still accepted"
        <| fun () ->
            let borderline =
                claims (Some "https://accounts.google.com") (Some clientId) (Some(nowSeconds - 30.0)) None

            let result = evaluate (Some jwtShaped) (decoderReturning borderline)

            Expect.equal
                result
                (AcceptCredential jwtShaped)
                "60s skew tolerance matches the redirect flow — the two entries must not disagree about the same token"

        testCase "missing exp claim → IdTokenExpired"
        <| fun () ->
            let noExp = claims (Some "https://accounts.google.com") (Some clientId) None None
            let result = evaluate (Some jwtShaped) (decoderReturning noExp)

            Expect.equal
                result
                (RejectCredential IdTokenExpired)
                "a credential with no stated lifetime is not treated as immortal"

        testCase "nonce configured and matched → accepted"
        <| fun () ->
            let bound =
                claims (Some "https://accounts.google.com") (Some clientId) (Some futureExp) (Some "n-abc123")

            let result =
                evaluateWithNonce (Some "n-abc123") (Some jwtShaped) (decoderReturning bound)

            Expect.equal result (AcceptCredential jwtShaped) "a matched nonce binds the credential to this attempt"

        testCase "nonce configured and mismatched → NonceMismatch"
        <| fun () ->
            let replayed =
                claims (Some "https://accounts.google.com") (Some clientId) (Some futureExp) (Some "n-stale")

            let result =
                evaluateWithNonce (Some "n-abc123") (Some jwtShaped) (decoderReturning replayed)

            Expect.equal
                result
                (RejectCredential NonceMismatch)
                "a credential minted against a different nonce is a replay against this app"

        testCase "nonce configured but absent from the credential → NonceMismatch"
        <| fun () ->
            let result =
                evaluateWithNonce (Some "n-abc123") (Some jwtShaped) (decoderReturning goodClaims)

            Expect.equal
                result
                (RejectCredential NonceMismatch)
                "asking for a nonce and getting none back is a failed binding, not an unchecked one"

        testCase "no nonce configured → the credential's nonce claim is not consulted"
        <| fun () ->
            let carriesOne =
                claims (Some "https://accounts.google.com") (Some clientId) (Some futureExp) (Some "whatever")

            let result = evaluate (Some jwtShaped) (decoderReturning carriesOne)

            Expect.equal
                result
                (AcceptCredential jwtShaped)
                "Google does not require a nonce for the credential flow; not asking for one is a supported configuration"

        testCase "issuer check precedes audience check"
        <| fun () ->
            // Both wrong: the reported error must be stable, so an
            // operator reading the console sees one cause rather than
            // whichever the implementation happened to test first.
            let bothWrong =
                claims (Some "https://evil.example") (Some "someone-else") (Some futureExp) None

            let result = evaluate (Some jwtShaped) (decoderReturning bothWrong)

            Expect.equal
                result
                (RejectCredential IdTokenIssuerInvalid)
                "the wrong-provider diagnosis outranks the wrong-audience one"
    ]

let private configProjectionTests =
    testList "GoogleIdentityUIConfig" [

        testCase "create defaults to button-only — One Tap is opt-in"
        <| fun () ->
            let config = GoogleIdentityUIConfig.create clientId

            Expect.isFalse
                config.OneTap
                "auto-prompting a deployment's users is not a default the SDK may choose (GP 11)"

            Expect.isFalse config.AutoSelect "silent re-sign-in is likewise opt-in"

        testCase "withOneTap turns the prompt on and changes nothing else"
        <| fun () ->
            let baseline = GoogleIdentityUIConfig.create clientId
            let opted = GoogleIdentityUIConfig.withOneTap baseline

            Expect.isTrue opted.OneTap "the opt-in helper is the whole of the change"
            Expect.equal { opted with OneTap = false } baseline "no other field moves"

        testCase "projects onto the shared OIDC config with Google's fixed issuer"
        <| fun () ->
            let projected =
                GoogleIdentityUIConfig.toOidcUIConfig (GoogleIdentityUIConfig.create clientId)

            Expect.equal
                projected.Issuer
                "https://accounts.google.com"
                "the projection is what makes a GIS session and a redirect-flow session the same session"

            Expect.equal projected.ClientId clientId "the client id is the audience the bridge binds against"

            Expect.equal
                projected.ValidateIdToken
                (Some true)
                "the credential IS the bearer, so id_token validation is on"

        testCase "an unset RedirectUri projects to empty rather than a guess"
        <| fun () ->
            let projected =
                GoogleIdentityUIConfig.toOidcUIConfig (GoogleIdentityUIConfig.create clientId)

            Expect.equal
                projected.RedirectUri
                ""
                "GIS's popup UX never redirects; inventing a callback URL would be a wrong value that looks right"
    ]

let private cspContributorTests =
    testList "GoogleIdentityServicesCspContributor" [

        testCase "declares script / frame / connect / style on Google's accounts host"
        <| fun () ->
            let sources =
                (GoogleIdentityServicesCspContributor() :> ICspContributor).RequiredSources

            Expect.contains
                sources
                (ScriptSrc "https://accounts.google.com/gsi/client")
                "without script-src the library never downloads"

            Expect.contains
                sources
                (FrameSrc "https://accounts.google.com/gsi/")
                "without frame-src the library downloads and then fails to render"

            Expect.contains sources (ConnectSrc "https://accounts.google.com/gsi/") "GIS calls back to Google's origin"

            Expect.contains
                sources
                (StyleSrc "https://accounts.google.com/gsi/style")
                "GIS installs its own stylesheet for the branded button"

        testCase "path-prefix sources end in a slash"
        <| fun () ->
            // CSP matches a source path EXACTLY unless it ends in `/`.
            // The same trap `OidcIssuerCspContributor` documents from the
            // other direction: a source without the trailing slash allows
            // one URL and blocks every sibling path the library uses.
            let sources =
                (GoogleIdentityServicesCspContributor() :> ICspContributor).RequiredSources

            let pathPrefixed =
                sources
                |> List.choose (function
                    | FrameSrc url
                    | ConnectSrc url -> Some url
                    | _ -> None)

            Expect.isNonEmpty pathPrefixed "the frame / connect sources are the prefix-matched ones"

            for url in pathPrefixed do
                Expect.stringEnds url "/" (sprintf "%s must end in `/` or CSP matches that exact path only" url)

        testCase "the library URL is stated once and shared"
        <| fun () ->
            Expect.equal
                GoogleIdentityServicesCspContributor.LibraryUrl
                "https://accounts.google.com/gsi/client"
                "the contributor and the client loader must not drift apart on the URL they allow / fetch"
    ]

let private cspValidatorTests =
    let serverConfig hardening = {
        ServerConfig.defaults with
            SecurityHardening = hardening
    }

    let validate (config: ServerConfig) (services: IServiceCollection) =
        (GoogleIdentityCspValidator.GoogleIdentityCspValidator(config, services) :> IConfigValidator).Validate()
        |> Async.RunSynchronously

    let isWarning =
        function
        | Warning _ -> true
        | _ -> false

    testList "GoogleIdentityCspValidator" [

        testCase "hardening off → Ok, nothing to widen"
        <| fun () ->
            let services = ServiceCollection() :> IServiceCollection
            let result = validate (serverConfig NoSecurityHardening) services

            Expect.equal result Ok "no policy is emitted at all, so a missing contributor cannot block anything"

        testCase "hardening on with no Google contributor → Warning"
        <| fun () ->
            let services = ServiceCollection() :> IServiceCollection
            let result = validate (serverConfig DefaultSecurityHardening) services

            Expect.isTrue
                (isWarning result)
                "this is the silent failure the validator exists for — green boot, button that never renders"

        testCase "hardening on with the contributor composed → Ok"
        <| fun () ->
            let services = ServiceCollection() :> IServiceCollection

            services.AddSingleton<ICspContributor>(GoogleIdentityServicesCspContributor() :> ICspContributor)
            |> ignore

            let result = validate (serverConfig DefaultSecurityHardening) services
            Expect.equal result Ok "the composed contributor is exactly what the preflight is asking for"

        testCase "a hand-rolled contributor covering the same host also satisfies it"
        <| fun () ->
            // The question is "can the library load", not "did you use
            // our type" — a deployment that widened its policy its own
            // way must not be nagged.
            let services = ServiceCollection() :> IServiceCollection

            let handRolled =
                { new ICspContributor with
                    member _.RequiredSources = [
                        ScriptSrc "https://accounts.google.com"
                        FrameSrc "https://accounts.google.com"
                    ]
                }

            services.AddSingleton<ICspContributor>(handRolled) |> ignore

            let result = validate (serverConfig DefaultSecurityHardening) services
            Expect.equal result Ok "a broader hand-rolled allowance is still an allowance"

        testCase "script-src alone is not enough → Warning"
        <| fun () ->
            let services = ServiceCollection() :> IServiceCollection

            let scriptOnly =
                { new ICspContributor with
                    member _.RequiredSources = [ ScriptSrc "https://accounts.google.com/gsi/client" ]
                }

            services.AddSingleton<ICspContributor>(scriptOnly) |> ignore

            let result = validate (serverConfig DefaultSecurityHardening) services

            Expect.isTrue
                (isWarning result)
                "the library downloads and then fails to render — the more confusing of the two failures"

        testCase "an unrelated contributor does not satisfy it"
        <| fun () ->
            let services = ServiceCollection() :> IServiceCollection

            services.AddSingleton<ICspContributor>(AgGridCdnCspContributor() :> ICspContributor)
            |> ignore

            let result = validate (serverConfig DefaultSecurityHardening) services
            Expect.isTrue (isWarning result) "a contributor for some other origin says nothing about Google's"
    ]

let tests: Test =
    testList "GoogleIdentity companion" [ bridgeTests; configProjectionTests; cspContributorTests; cspValidatorTests ]