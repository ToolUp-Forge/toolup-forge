// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.OidcBearerStrategyTests

open System
open System.Collections.Generic
open System.Net.Http
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.AuthProviders
open ToolUp.AuthProviders.Oidc
open ToolUp.AuthProviders.Oidc.OidcAppConfig
open ToolUp.AuthProviders.Oidc.OidcTypes
open ToolUp.AuthProviders.Oidc.OidcStateMachine
open ToolUp.AuthProviders.Oidc.OidcClient
open ToolUp.Platform.Tests.InProcess.MockOidcServer

// ─── Bearer-token strategy ───────────────────────────────────────────
//
// An identity provider whose access tokens are OPAQUE — Google always,
// Auth0 until an API audience is configured — signs a user in
// successfully and then 401s every subsequent API call: the
// server-side bearer path validates a JWT against the issuer's JWKS,
// and an opaque string has nothing to validate.
//
// `BearerTokenKind` lets a deployment declare that the session should
// store and send the `id_token` instead. The id_token is a JWT by OIDC
// mandate, signed by the same key set, carrying `iss` = the issuer and
// `aud` = the client id — so the UNCHANGED server-side provider
// validates it end to end. That last fact is what this file's headline
// case proves against a real Kestrel issuer: the opaque access token
// is refused and the id_token from the same `/token` response is
// accepted, by one provider instance, over one socket.
//
// Four layers are covered, cheapest first:
//
//   1. preset metadata     — which presets default to which strategy,
//                            and WHY the predicate is not simply
//                            `not expectsDecodableAccessToken`;
//   2. config resolution   — explicit consumer choice beats preset
//                            default beats `AccessTokenBearer`, and
//                            the projection to the client tier;
//   3. the pure decision   — `decideBearerToken` over both response
//                            shapes, including the deliberate refusal
//                            to fall back;
//   4. the full loop       — sign-in -> authenticated call -> refresh
//                            -> authenticated call, against a mock
//                            issuer configured the way Google behaves.

let private clientId = "bearer-strategy-client"

let private redirectUri = "https://app.example.test/auth/callback"

let private handCfg =
    OidcAppConfig.create "https://issuer.example.test" clientId redirectUri

// ─── Layer 1 — preset metadata ───────────────────────────────────────

let private presetDefaults =
    testList "PresetKind.defaultBearerToken" [

        testCase "Google defaults to the id_token bearer"
        <| fun () ->
            Expect.equal
                (PresetKind.defaultBearerToken Google)
                IdTokenBearer
                "Google access tokens are opaque with no knob that changes it — the access-token strategy cannot be made to work."

        testCase "Generic defaults to the access token despite expectsDecodableAccessToken = false"
        <| fun () ->
            // The distinction the whole helper exists for. `Generic`
            // answers `false` to "expects a decodable access token"
            // because the SDK has NO PROVIDER KNOWLEDGE, not because
            // the token is known to be opaque. Defaulting these
            // deployments to the id_token would change working
            // behaviour on an absence of information.
            Expect.isFalse (PresetKind.expectsDecodableAccessToken Generic) "precondition"

            Expect.equal
                (PresetKind.defaultBearerToken Generic)
                AccessTokenBearer
                "`expectsDecodableAccessToken = false` must NOT be the predicate — Generic says it for a different reason."

        testCase "Auth0 defaults to the access token despite expectsDecodableAccessToken = false"
        <| fun () ->
            // Auth0's opacity IS fixable — configure an API audience in
            // the dashboard and pass it as the `audience` extra
            // parameter. The SDK must not pre-empt a remedy the
            // deployment owns.
            Expect.isFalse (PresetKind.expectsDecodableAccessToken Auth0) "precondition"

            Expect.equal
                (PresetKind.defaultBearerToken Auth0)
                AccessTokenBearer
                "Auth0's opacity is a configuration choice, not a fixed provider property."

        testCase "every Entra preset defaults to the access token"
        <| fun () ->
            for kind in
                [
                    EntraWorkforce
                    EntraExternalId
                    EntraExternalIdWithDomain "login.contoso.com"
                ] do
                Expect.equal
                    (PresetKind.defaultBearerToken kind)
                    AccessTokenBearer
                    $"Entra issues decodable v2 JWT access tokens (%s{PresetKind.label kind})"

        testCase "opaqueAccessTokenIsUnfixable is true for Google alone"
        <| fun () ->
            let kinds = [
                Generic
                EntraWorkforce
                EntraExternalId
                EntraExternalIdWithDomain "login.contoso.com"
                Auth0
                Google
            ]

            let unfixable = kinds |> List.filter PresetKind.opaqueAccessTokenIsUnfixable

            Expect.equal unfixable [ Google ] "only Google's opacity survives every configuration lever"

        testCase "defaultBearerToken agrees with opaqueAccessTokenIsUnfixable on every case"
        <| fun () ->
            // The two helpers encode the same fact for two different
            // consumers (the config resolver, and coherence rule 15).
            // Pin the agreement so one can't drift from the other.
            for kind in
                [
                    Generic
                    EntraWorkforce
                    EntraExternalId
                    EntraExternalIdWithDomain "d"
                    Auth0
                    Google
                ] do
                let expected =
                    if PresetKind.opaqueAccessTokenIsUnfixable kind then
                        IdTokenBearer
                    else
                        AccessTokenBearer

                Expect.equal (PresetKind.defaultBearerToken kind) expected (PresetKind.label kind)

        testCase "BearerTokenKind.label is stable"
        <| fun () ->
            Expect.equal (BearerTokenKind.label AccessTokenBearer) "access-token" ""
            Expect.equal (BearerTokenKind.label IdTokenBearer) "id-token" ""
    ]

// ─── Layer 2 — config resolution + projection ────────────────────────

let private resolution =
    testList "OidcAppConfig.resolveBearerToken" [

        testCase "hand-built config with no preset resolves to the access token (GP 11)"
        <| fun () -> Expect.equal (OidcAppConfig.resolveBearerToken handCfg) AccessTokenBearer ""

        testCase "the google preset resolves to the id_token with no consumer ceremony"
        <| fun () ->
            let cfg = OidcPresets.google clientId redirectUri
            Expect.equal cfg.BearerToken None "the preset states nothing; the DEFAULT carries it"
            Expect.equal (OidcAppConfig.resolveBearerToken cfg) IdTokenBearer ""

        testCase "an explicit consumer setting beats the preset default"
        <| fun () ->
            let cfg = {
                OidcPresets.google clientId redirectUri with
                    BearerToken = Some AccessTokenBearer
            }

            Expect.equal
                (OidcAppConfig.resolveBearerToken cfg)
                AccessTokenBearer
                "a consumer stating a strategy knows something the preset cannot."

        testCase "an explicit setting also works where no preset would supply one"
        <| fun () ->
            let cfg = {
                handCfg with
                    BearerToken = Some IdTokenBearer
            }

            Expect.equal (OidcAppConfig.resolveBearerToken cfg) IdTokenBearer ""

        testCase "every other preset resolves to the access token"
        <| fun () ->
            let cfgs = [
                OidcPresets.generic "https://issuer.example.test" clientId redirectUri
                OidcPresets.entraWorkforce "00000000-0000-0000-0000-000000000000" clientId redirectUri
                OidcPresets.entraExternalId "contoso" clientId redirectUri
                OidcPresets.entraExternalIdWithDomain "contoso" "login.contoso.com" clientId redirectUri
                OidcPresets.auth0 "tenant.auth0.com" clientId redirectUri
            ]

            for cfg in cfgs do
                Expect.equal (OidcAppConfig.resolveBearerToken cfg) AccessTokenBearer ""

        testCase "toClientConfig projects a DECIDED value, not the raw option"
        <| fun () ->
            let projected =
                OidcAppConfig.toClientConfig (OidcPresets.google clientId redirectUri)

            Expect.equal
                projected.BearerToken
                (Some IdTokenBearer)
                "the client tier must never have to know what a PresetKind is."

        testCase "toClientConfig of a hand-built config projects the access token explicitly"
        <| fun () ->
            let projected = OidcAppConfig.toClientConfig handCfg
            Expect.equal projected.BearerToken (Some AccessTokenBearer) ""

        testCase "OidcUIConfig.resolveBearerToken: None is today's behaviour (GP 11)"
        <| fun () ->
            let ui = OidcUIConfig.defaults "https://issuer.example.test" clientId redirectUri
            Expect.equal ui.BearerToken None "defaults must not opt anyone in"
            Expect.equal (OidcUIConfig.resolveBearerToken ui) AccessTokenBearer ""

        testCase "OidcUIConfig.resolveBearerToken honours an explicit value"
        <| fun () ->
            let ui = {
                OidcUIConfig.defaults "https://issuer.example.test" clientId redirectUri with
                    BearerToken = Some IdTokenBearer
            }

            Expect.equal (OidcUIConfig.resolveBearerToken ui) IdTokenBearer ""
    ]

// ─── Layer 3 — the pure decision ─────────────────────────────────────

let private decision =
    testList "OidcStateMachine.decideBearerToken" [

        testCase "access-token strategy selects the access token"
        <| fun () ->
            let r =
                decideBearerToken AccessTokenBearer {
                    AccessToken = "opaque-access"
                    IdToken = Some "the.id.token"
                }

            Expect.equal r (Ok "opaque-access") ""

        testCase "access-token strategy is total — no id_token required"
        <| fun () ->
            let r =
                decideBearerToken AccessTokenBearer {
                    AccessToken = "the.access.token"
                    IdToken = None
                }

            Expect.equal r (Ok "the.access.token") ""

        testCase "id-token strategy selects the id_token"
        <| fun () ->
            let r =
                decideBearerToken IdTokenBearer {
                    AccessToken = "opaque-access"
                    IdToken = Some "the.id.token"
                }

            Expect.equal r (Ok "the.id.token") ""

        testCase "id-token strategy REFUSES rather than falling back to the access token"
        <| fun () ->
            // The load-bearing negative. A fallback would store a
            // credential the deployment already told us cannot be
            // validated (callback path), or silently swap the
            // session's bearer to a different token class mid-session
            // (refresh path). Neither is recoverable without a signal.
            let r =
                decideBearerToken IdTokenBearer {
                    AccessToken = "opaque-access"
                    IdToken = None
                }

            match r with
            | Ok bearer -> failtestf "expected refusal, got Ok %s" bearer
            | Error(TokenExchangeFailed msg) ->
                Expect.stringContains msg "id-token" "the message must name the strategy"
                Expect.stringContains msg "id_token" "and the missing field"
            | Error other -> failtestf "expected TokenExchangeFailed, got %A" other

        testCase "the refusal is diagnosable, not just typed"
        <| fun () ->
            let r = decideBearerToken IdTokenBearer { AccessToken = "a"; IdToken = None }

            match r with
            | Error e ->
                let d = OidcTokenStore.diagnose e
                Expect.equal d.Kind "TOKEN_EXCHANGE_FAILED" ""
                Expect.isSome d.Hint "an operator needs somewhere to go"
            | Ok _ -> failtest "expected refusal"
    ]

// ─── Layer 3b — classifier agreement ─────────────────────────────────
//
// `classifyStoredToken` reads the BEARER slot, so the strategy and the
// classifier agree by construction rather than by a rule anyone
// maintains. These cases pin that: the same provider's two tokens
// classify differently, and only the id_token reaches `FreshJwt` —
// which is what makes the cold-start and stale-rescue paths work for
// an opaque-access-token provider at all.

let private classifierAgreement =
    let issuer = "https://accounts.google.com"
    let now = 1_800_000_000.0

    let ui = {
        OidcUIConfig.defaults issuer clientId redirectUri with
            BearerToken = Some IdTokenBearer
    }

    // The browser decoder returns `None` for anything it cannot
    // base64-then-JSON-parse — which an opaque token never can be.
    let decoder (token: string) : JwtClaimsExtract option =
        if token.StartsWith "ya29." then
            None
        else
            Some {
                Iss = Some issuer
                Exp = Some(now + 3600.0)
            }

    testList "classifyStoredToken agreement with the bearer strategy" [

        testCase "an opaque access token in the bearer slot classifies OpaqueToken"
        <| fun () ->
            let r = classifyStoredTokenWith ui now (Some "ya29.aaa.bbb.ccc") decoder
            Expect.equal r OpaqueToken "server has to decide — the client read no claims"

        testCase "an id_token in the bearer slot classifies FreshJwt"
        <| fun () ->
            let r = classifyStoredTokenWith ui now (Some "h.p.s") decoder

            Expect.equal
                r
                FreshJwt
                "this is the point of the strategy: the stale-token rescue path becomes reachable for this provider."

        testCase "an expired id_token in the bearer slot classifies StaleJwt"
        <| fun () ->
            let expired (_: string) : JwtClaimsExtract option =
                Some {
                    Iss = Some issuer
                    Exp = Some(now - 60.0)
                }

            Expect.equal (classifyStoredTokenWith ui now (Some "h.p.s") expired) StaleJwt ""
    ]

// ─── Layer 3c — coherence rules 14 + 15 ──────────────────────────────

let private coherence =
    let messages (outcomes: OidcCoherenceValidator.RuleOutcome list) =
        outcomes |> List.map OidcCoherenceValidator.RuleOutcome.message

    let hasErrorMatching (needle: string) outcomes =
        outcomes
        |> List.exists (function
            | OidcCoherenceValidator.RuleError m -> m.Contains needle
            | _ -> false)

    let hasWarningMatching (needle: string) outcomes =
        outcomes
        |> List.exists (function
            | OidcCoherenceValidator.RuleWarning m -> m.Contains needle
            | _ -> false)

    testList "OidcCoherenceValidator — bearer-strategy rules" [

        testCase "Rule 14: id_token bearer with Audience <> ClientId → ERROR"
        <| fun () ->
            // The failure this rule exists to catch is invisible until
            // the first API call: sign-in succeeds and every request
            // then fails audience validation, because an id_token's
            // `aud` is always the client id.
            let cfg = {
                handCfg with
                    BearerToken = Some IdTokenBearer
                    Audience = "https://api.example.test"
            }

            let outcomes = OidcCoherenceValidator.evaluate cfg

            Expect.isTrue
                (hasErrorMatching "`id-token` bearer strategy" outcomes)
                $"expected a rule-14 error, got %A{messages outcomes}"

        testCase "Rule 14: id_token bearer with Audience = ClientId → no error"
        <| fun () ->
            let cfg = {
                handCfg with
                    BearerToken = Some IdTokenBearer
            }

            let outcomes = OidcCoherenceValidator.evaluate cfg
            Expect.isFalse (hasErrorMatching "bearer strategy" outcomes) ""

        testCase "Rule 14: the google preset is coherent out of the box"
        <| fun () ->
            // The preset sets Audience = ClientId and defaults to the
            // id_token, so the pairing it ships cannot trip its own
            // rule. Worth pinning — a preset that fails its own
            // validator is the least useful kind of preset.
            let outcomes =
                OidcCoherenceValidator.evaluate (OidcPresets.google clientId redirectUri)

            Expect.isFalse
                (outcomes |> List.exists OidcCoherenceValidator.RuleOutcome.isError)
                $"the google preset must be error-free: %A{messages outcomes}"

        testCase "Rule 14: access-token bearer with a different Audience → no error"
        <| fun () ->
            // The Auth0 shape — an API identifier as the audience — is
            // entirely legitimate under the default strategy.
            let cfg = {
                OidcPresets.auth0 "tenant.auth0.com" clientId redirectUri with
                    Audience = "https://api.example.test"
            }

            let outcomes = OidcCoherenceValidator.evaluate cfg
            Expect.isFalse (hasErrorMatching "bearer strategy" outcomes) ""

        testCase "Rule 15: google preset overridden back to the access token → WARN"
        <| fun () ->
            let cfg = {
                OidcPresets.google clientId redirectUri with
                    BearerToken = Some AccessTokenBearer
            }

            let outcomes = OidcCoherenceValidator.evaluate cfg

            Expect.isTrue
                (hasWarningMatching "opaque access tokens with no configuration" outcomes)
                $"expected a rule-15 warning, got %A{messages outcomes}"

        testCase "Rule 15: does NOT fire on Auth0 (opacity is fixable by configuration)"
        <| fun () ->
            let outcomes =
                OidcCoherenceValidator.evaluate (OidcPresets.auth0 "tenant.auth0.com" clientId redirectUri)

            Expect.isFalse
                (hasWarningMatching "opaque access tokens with no configuration" outcomes)
                "Auth0's remedy is a dashboard audience — warning here would report a non-problem."

        testCase "Rule 15: does NOT fire on Generic (no provider knowledge)"
        <| fun () ->
            let outcomes =
                OidcCoherenceValidator.evaluate (OidcPresets.generic "https://issuer.example.test" clientId redirectUri)

            Expect.isFalse (hasWarningMatching "opaque access tokens with no configuration" outcomes) ""

        testCase "Rule 15: does NOT fire on the google preset's own default"
        <| fun () ->
            let outcomes =
                OidcCoherenceValidator.evaluate (OidcPresets.google clientId redirectUri)

            Expect.isFalse (hasWarningMatching "opaque access tokens with no configuration" outcomes) ""

        testCase "Rule 11's provenance line reports the resolved bearer"
        <| fun () ->
            let outcomes =
                OidcCoherenceValidator.evaluate (OidcPresets.google clientId redirectUri)

            Expect.isTrue
                (outcomes
                 |> List.exists (function
                     | OidcCoherenceValidator.RuleOk m -> m.Contains "bearer: id-token"
                     | _ -> false))
                $"%A{messages outcomes}"
    ]

// ─── Layer 4 — the full loop against a real issuer ───────────────────
//
// The acceptance case. A mock issuer configured the way Google
// behaves — opaque access tokens, a signed id_token addressed to the
// client id — is driven through the real `/token` endpoint over a real
// socket, and the tokens it returns are put in front of a real
// `OidcAuthProvider` built from an ordinary `AuthConfig`.
//
// The server side needed NO change for this to work, and that is the
// claim under test rather than an aside: the id_token is an ordinary
// RS256 JWT and the provider was always able to validate one. What was
// missing was any way for the client to say "send THAT one".

let private tokenEndpointResponse (http: HttpClient) (issuerUrl: string) (form: (string * string) list) = async {
    let content = new FormUrlEncodedContent(form |> List.map KeyValuePair |> List.toSeq)
    let! response = http.PostAsync($"{issuerUrl}/token", content) |> Async.AwaitTask
    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
    let doc = JsonDocument.Parse body

    let read (name: string) =
        match doc.RootElement.TryGetProperty name with
        | true, v -> Some(v.GetString())
        | _ -> None

    return {|
        AccessToken = read "access_token" |> Option.defaultValue ""
        IdToken = read "id_token"
    |}
}

let private fullLoop =
    let server =
        lazy (MockOidcServer.start (MockOidcConfig.opaqueAccessTokenIssuer clientId))

    let noIdTokenOnRefreshServer =
        lazy
            (MockOidcServer.start {
                MockOidcConfig.opaqueAccessTokenIssuer clientId with
                    ReissueIdTokenOnRefresh = false
            })

    /// An `AuthConfig` a deployment would actually write for this
    /// issuer: audience = the client id, keys from discovery. Nothing
    /// here mentions a bearer strategy — the server does not have one.
    let authConfigFor (issuerUrl: string) : AuthConfig = {
        Issuer = Some issuerUrl
        Audience = Some clientId
        KeySource = JwksDiscovery issuerUrl
        TokenLocation = BearerHeader
        ClockSkewSeconds = None
        AcceptedAlgorithms = None
        PreferOidWhenPresent = None
        ClaimMapping = None
    }

    let validate (provider: IAuthProvider) (token: string) = async {
        let ctx = DefaultHttpContext() :> HttpContext
        ctx.Request.Headers.Authorization <- Microsoft.Extensions.Primitives.StringValues $"Bearer {token}"
        return! provider.ValidateRequest(RequestContextBuilder.ofHttpContext ctx)
    }

    testSequenced (
        testList "id_token bearer — full loop against a mock opaque-access-token issuer" [

            testCase "sign-in → authenticated call → refresh → authenticated call"
            <| fun () ->
                let s = server.Value
                use http = new HttpClient()

                let provider =
                    OidcAuthProvider.fromConfigWith (new HttpClient()) None (authConfigFor s.IssuerUrl)

                // ── sign-in: the authorization-code exchange ──
                let exchange =
                    tokenEndpointResponse http s.IssuerUrl [
                        "grant_type", "authorization_code"
                        "client_id", clientId
                        "code", "mock-auth-code"
                        "code_verifier", "verifier"
                    ]
                    |> Async.RunSynchronously

                // Google's opaque tokens carry a `ya29.` prefix, so
                // "contains a dot" is the wrong shape test — count JWS
                // segments, which is what every consumer of this token
                // actually does.
                Expect.notEqual
                    (exchange.AccessToken.Split('.').Length)
                    3
                    "precondition: this issuer's access token must not be JWS-shaped, or the fixture proves nothing."

                Expect.isSome exchange.IdToken "an `openid` sign-in returns an id_token"

                // The client's decision. Nothing browser-specific about
                // it — this is exactly what `handleCallback` runs.
                let bearer =
                    decideBearerToken IdTokenBearer {
                        AccessToken = exchange.AccessToken
                        IdToken = exchange.IdToken
                    }
                    |> function
                        | Ok b -> b
                        | Error e -> failtestf "bearer selection failed: %A" e

                // ── the negative control, on the same provider ──
                match validate provider exchange.AccessToken |> Async.RunSynchronously with
                | Ok _ ->
                    failtest
                        "the opaque access token MUST be refused — if it validates, this issuer is not modelling the problem."
                | Error _ -> ()

                // ── the authenticated call ──
                match validate provider bearer |> Async.RunSynchronously with
                | Ok user ->
                    Expect.equal user.UserId MockOidcConfig.defaults.Subject "the id_token carries the same subject"
                | Error e -> failtestf "the id_token bearer must validate against an unchanged provider: %A" e

                // ── refresh ──
                let refreshed =
                    tokenEndpointResponse http s.IssuerUrl [
                        "grant_type", "refresh_token"
                        "client_id", clientId
                        "refresh_token", "mock-refresh-token"
                    ]
                    |> Async.RunSynchronously

                let refreshedBearer =
                    decideBearerToken IdTokenBearer {
                        AccessToken = refreshed.AccessToken
                        IdToken = refreshed.IdToken
                    }
                    |> function
                        | Ok b -> b
                        | Error e -> failtestf "refresh bearer selection failed: %A" e

                Expect.notEqual
                    refreshedBearer
                    bearer
                    "a refresh must ROTATE the bearer, not re-store the one that was about to expire."

                // ── the authenticated call after refresh ──
                match validate provider refreshedBearer |> Async.RunSynchronously with
                | Ok user -> Expect.equal user.UserId MockOidcConfig.defaults.Subject ""
                | Error e -> failtestf "the reissued id_token must validate too: %A" e

            testCase "an issuer that does not reissue an id_token on refresh fails the refresh, loudly"
            <| fun () ->
                let s = noIdTokenOnRefreshServer.Value
                use http = new HttpClient()

                let refreshed =
                    tokenEndpointResponse http s.IssuerUrl [
                        "grant_type", "refresh_token"
                        "client_id", clientId
                        "refresh_token", "mock-refresh-token"
                    ]
                    |> Async.RunSynchronously

                Expect.isNone refreshed.IdToken "fixture precondition"

                match
                    decideBearerToken IdTokenBearer {
                        AccessToken = refreshed.AccessToken
                        IdToken = refreshed.IdToken
                    }
                with
                | Ok bearer ->
                    failtestf
                        "expected refusal — silently storing %s would swap the session's bearer to a token class the server cannot validate."
                        bearer
                | Error _ -> ()

            testCase "strategy OFF against the same issuer reproduces the broken behaviour exactly"
            <| fun () ->
                // The control that makes the whole phase legible: with
                // no strategy declared, the client picks the opaque
                // access token and the server refuses it. This is what
                // every Google deployment did before, and what a
                // deployment that opts out still gets (GP 11).
                let s = server.Value
                use http = new HttpClient()

                let provider =
                    OidcAuthProvider.fromConfigWith (new HttpClient()) None (authConfigFor s.IssuerUrl)

                let exchange =
                    tokenEndpointResponse http s.IssuerUrl [
                        "grant_type", "authorization_code"
                        "client_id", clientId
                        "code", "mock-auth-code"
                        "code_verifier", "verifier"
                    ]
                    |> Async.RunSynchronously

                let ui = OidcUIConfig.defaults s.IssuerUrl clientId redirectUri

                let bearer =
                    decideBearerToken (OidcUIConfig.resolveBearerToken ui) {
                        AccessToken = exchange.AccessToken
                        IdToken = exchange.IdToken
                    }

                Expect.equal bearer (Ok exchange.AccessToken) "an undeclared strategy must send the access token"

                match validate provider exchange.AccessToken |> Async.RunSynchronously with
                | Ok _ -> failtest "the opaque token cannot validate"
                | Error _ -> ()

            testCase "teardown: stop mock issuers"
            <| fun () ->
                if server.IsValueCreated then
                    (server.Value :> IDisposable).Dispose()

                if noIdTokenOnRefreshServer.IsValueCreated then
                    (noIdTokenOnRefreshServer.Value :> IDisposable).Dispose()
        ]
    )

let tests: Test =
    testList "OidcClient.bearerStrategy" [
        presetDefaults
        resolution
        decision
        classifierAgreement
        coherence
        fullLoop
    ]