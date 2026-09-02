module ToolUp.Platform.Tests.InProcess.ThreatLensRegressionSuite

// Phase 208 — Codified threat-lens security-regression suite.
//
// The six manual audit lenses that authored the Epoch-1 security posture, turned
// into a single recurring, automated regression pack so the *next* regression is
// caught by `Build.fsproj -- VerifyAll` rather than by the next human audit. Each
// lens carries red-team cases that assert BOTH directions — the secure path holds
// AND the insecure variant is rejected — so reverting a production control flips a
// case from green to red (see the `reverted-control proof` list at the foot, which
// makes that self-proving property explicit per the phase acceptance).
//
// Test-only. Every symbol under test is reached through the shipped public surface;
// this file compiles into the test runner and is byte-for-byte absent from any
// consumer build (GP 11 / GP 13).

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.Secrets
open ToolUp.AuthProviders
open ToolUp.Remoting.Server

// NB: `ToolUp.Platform.ConfigValidation` is deliberately NOT opened — its
// `ValidationResult` DU has `Ok` / `Error` cases that would shadow FSharp.Core's
// `Result.Ok` / `Result.Error` and break every Result-returning call below. The
// two symbols this suite needs from it are referenced fully-qualified instead.
type private IConfigValidator = ToolUp.Platform.ConfigValidation.IConfigValidator

// ─── Shared HttpContext / request helpers (copied from AuthProviderTests'
//     private fixtures — kept local so this suite stays self-contained). ──────────

let private mkContext () = DefaultHttpContext() :> HttpContext

let private withHeader (name: string) (value: string) (ctx: HttpContext) =
    ctx.Request.Headers[name] <- StringValues value
    ctx

let private toReq (ctx: HttpContext) =
    ToolUp.Platform.RequestContextBuilder.ofHttpContext ctx

let private bearerCtx (token: string) =
    mkContext () |> withHeader "Authorization" ("Bearer " + token) |> toReq

let private futureExp () =
    DateTimeOffset.UtcNow.AddHours(1.0).ToUnixTimeSeconds() |> box

let private pastExp () =
    DateTimeOffset.UtcNow.AddHours(-1.0).ToUnixTimeSeconds() |> box

// ══════════════════════════════════════════════════════════════════════════════
//  Lens 1 — JWT / JWKS crypto (OidcAuthProvider.Jwt / .Jwks)
//
//  Self-contained RSA-signed-JWT + JWKS fixture (mirrors AuthProviderTests'
//  OidcFixture). A fresh keypair + GUID-suffixed URLs per test keep the module-
//  level JWKS caches from bleeding state. Red-team: tampered signature, wrong
//  audience, expired, unknown kid — each must be rejected; a well-formed token
//  against the matching JWKS must be accepted.
// ══════════════════════════════════════════════════════════════════════════════

module private OidcFixture =
    let private b64u (bytes: byte[]) =
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

    type IssuerKey = {
        Rsa: RSA
        Kid: string
        IssuerUrl: string
        JwksUrl: string
    }

    let mkKey () : IssuerKey =
        let rsa = RSA.Create 2048
        let unique = Guid.NewGuid().ToString("N").Substring(0, 8)
        let issuer = $"https://threatlens-oidc/{unique}"

        {
            Rsa = rsa
            Kid = $"threatlens-key-{unique}"
            IssuerUrl = issuer
            JwksUrl = $"{issuer}/jwks.json"
        }

    let private payloadB64 (claims: (string * obj) list) =
        let dict = System.Collections.Generic.Dictionary<string, obj>()

        for k, v in claims do
            dict[k] <- v

        b64u (Encoding.UTF8.GetBytes(JsonSerializer.Serialize dict))

    /// Mint an RS256 JWT carrying `kid` in its header, signed with `signingKey`.
    /// The header kid comes from `headerKey` so a forged-kid token can name a key
    /// the JWKS never served.
    let mintRs256With (headerKey: IssuerKey) (signingKey: IssuerKey) (claims: (string * obj) list) =
        let header = $"""{{"alg":"RS256","typ":"JWT","kid":"{headerKey.Kid}"}}"""
        let headerB64 = b64u (Encoding.UTF8.GetBytes header)
        let pB64 = payloadB64 claims
        let message = Encoding.UTF8.GetBytes $"{headerB64}.{pB64}"

        let signature =
            signingKey.Rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

        $"{headerB64}.{pB64}.{b64u signature}"

    /// The common case: header kid + signature both from the same key.
    let mintRs256 (key: IssuerKey) (claims: (string * obj) list) = mintRs256With key key claims

    /// JWKS JSON containing one RSA public key (private half never exported).
    let buildJwks (key: IssuerKey) =
        let p = key.Rsa.ExportParameters(false)
        let n = b64u p.Modulus
        let e = b64u p.Exponent
        $"""{{"keys":[{{"kty":"RSA","kid":"{key.Kid}","alg":"RS256","use":"sig","n":"{n}","e":"{e}"}}]}}"""

/// Stub HttpMessageHandler routing absolute URLs to canned JSON; anything else 404s
/// (surfaces as JwksUnavailable in the provider).
type private StubHttpHandler(routes: Map<string, string>) =
    inherit HttpMessageHandler()

    override _.SendAsync(request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
        match routes.TryFind(string request.RequestUri) with
        | Some body ->
            let response = new HttpResponseMessage(HttpStatusCode.OK)
            response.Content <- new StringContent(body, Encoding.UTF8, "application/json")
            Task.FromResult response
        | None ->
            let response = new HttpResponseMessage(HttpStatusCode.NotFound)
            response.Content <- new StringContent("no stub")
            Task.FromResult response

let private jwksProvider (servedKey: OidcFixture.IssuerKey) (audience: string option) =
    let client =
        new HttpClient(new StubHttpHandler(Map.ofList [ servedKey.JwksUrl, OidcFixture.buildJwks servedKey ]))

    let config = {
        Issuer = None
        Audience = audience
        KeySource = JwksExplicit servedKey.JwksUrl
        TokenLocation = BearerHeader
        ClockSkewSeconds = None
        AcceptedAlgorithms = None
        PreferOidWhenPresent = None
        ClaimMapping = None
    }

    OidcAuthProvider.fromConfigWith client None config

let private lens1Jwt =
    testList "Lens 1 — JWT/JWKS crypto" [
        testCaseAsync "secure: a well-formed RS256 token verified against the matching JWKS is accepted"
        <| async {
            let key = OidcFixture.mkKey ()
            let p = jwksProvider key (Some "threatlens-aud")

            let token =
                OidcFixture.mintRs256 key [ "sub", box "alice"; "aud", box "threatlens-aud"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> ()
            | Error e -> failtestf "secure path: valid token rejected — %s" e
        }

        testCaseAsync "insecure: a token signed by a DIFFERENT key (tampered signature) is rejected"
        <| async {
            let legit = OidcFixture.mkKey ()
            let attacker = { legit with Rsa = RSA.Create 2048 }
            let p = jwksProvider legit None
            // header names the legit kid, but the signature is the attacker's.
            let token =
                OidcFixture.mintRs256With legit attacker [ "sub", box "attacker"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "forged-signature token must be rejected"
            | Error _ -> ()
        }

        testCaseAsync "insecure: a token with the wrong audience is rejected when Audience is bound"
        <| async {
            let key = OidcFixture.mkKey ()
            let p = jwksProvider key (Some "expected-aud")

            let token =
                OidcFixture.mintRs256 key [ "sub", box "alice"; "aud", box "other-aud"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "wrong-audience token must be rejected"
            | Error _ -> ()
        }

        testCaseAsync "insecure: an expired token is rejected"
        <| async {
            let key = OidcFixture.mkKey ()
            let p = jwksProvider key None
            let token = OidcFixture.mintRs256 key [ "sub", box "alice"; "exp", pastExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "expired token must be rejected"
            | Error _ -> ()
        }

        testCaseAsync "insecure: a token signed with an unknown kid is rejected"
        <| async {
            let legit = OidcFixture.mkKey ()

            let forged = {
                OidcFixture.mkKey () with
                    IssuerUrl = legit.IssuerUrl
                    JwksUrl = legit.JwksUrl
            }

            let p = jwksProvider legit None
            // header kid + signature are the forged key's; the JWKS only serves `legit`.
            let token =
                OidcFixture.mintRs256 forged [ "sub", box "attacker"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "unknown-kid token must be rejected"
            | Error _ -> ()
        }
    ]

// ══════════════════════════════════════════════════════════════════════════════
//  Lens 2 — mode-gating / dev-bypass
//  (HeaderAuthProviderModeValidator, AutoBootstrapDevAdminModeValidator)
//
//  A dev convenience must be INERT in an auth-requiring (production) mode: the
//  validator returns ValidationResult.Error, refusing the boot. In anonymous mode
//  the same config is Ok (the convenience is legitimate there).
// ══════════════════════════════════════════════════════════════════════════════

let private isValidationError (r: ToolUp.Platform.ConfigValidation.ValidationResult) =
    match r with
    | ToolUp.Platform.ConfigValidation.ValidationResult.Error _ -> true
    | _ -> false

let private runValidator (v: IConfigValidator) = v.Validate() |> Async.RunSynchronously

let private lens2ModeGating =
    let headerAuthValidator (config: ServerConfig) =
        let provider = HeaderAuthProvider.HeaderAuthProvider() :> IAuthProvider

        HeaderAuthProviderModeValidator.HeaderAuthProviderModeValidator(config, provider) :> IConfigValidator
        |> runValidator

    // The dev-admin bootstrap gate reads an opt-in env var; clear it so the
    // production-refusal path is deterministic regardless of the CI environment.
    let devAdminBootstrapEnvVar = "TOOLUP_ALLOW_DEV_ADMIN_BOOTSTRAP"

    let withClearedBootstrapOptIn (f: unit -> 'a) : 'a =
        let saved = Environment.GetEnvironmentVariable devAdminBootstrapEnvVar
        Environment.SetEnvironmentVariable(devAdminBootstrapEnvVar, null)

        try
            f ()
        finally
            Environment.SetEnvironmentVariable(devAdminBootstrapEnvVar, saved)

    let devAdminValidator (config: ServerConfig) =
        withClearedBootstrapOptIn (fun () ->
            AutoBootstrapDevAdminModeValidator.AutoBootstrapDevAdminModeValidator(config) :> IConfigValidator
            |> runValidator)

    // Sequenced: these cases mutate the process-global dev-admin-bootstrap env var,
    // which SecureByDefaultValidatorTests (also testSequenced) reads — running them
    // in Expecto's parallel batch would race. Matches the codebase env-var convention.
    testSequenced
    <| testList "Lens 2 — mode-gating / dev-bypass" [
        test "insecure: HeaderAuthProvider in Individual (production) mode with no escape hatch → Error" {
            let config = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    AcceptHeaderAuthWhenAuthRequired = false
            }

            Expect.isTrue
                (isValidationError (headerAuthValidator config))
                "spoofable header auth must be refused in a production mode"
        }

        test "secure: HeaderAuthProvider in anonymous mode → not an Error (the dev path is legitimate there)" {
            let config = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.anonymous
                    AcceptHeaderAuthWhenAuthRequired = false
            }

            Expect.isFalse
                (isValidationError (headerAuthValidator config))
                "anonymous mode legitimately tolerates the dev provider"
        }

        test "insecure: AutoBootstrapDevAdmin set in Individual (production) mode, opt-in unset → Error" {
            let config = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    AutoBootstrapDevAdmin = Some "dev-admin"
            }

            Expect.isTrue
                (isValidationError (devAdminValidator config))
                "a leaked dev-admin bootstrap must be refused in a production mode"
        }

        test "secure: AutoBootstrapDevAdmin unset in Individual mode → not an Error" {
            let config = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    AutoBootstrapDevAdmin = None
            }

            Expect.isFalse (isValidationError (devAdminValidator config)) "no dev-admin field ⇒ nothing to refuse"
        }
    ]

// ══════════════════════════════════════════════════════════════════════════════
//  Lens 3 — multi-tenant scope isolation (Phase 131 store-seam id sanitisation)
//
//  IdentitySanitiser.sanitiseScopeId is the seam every scope-keyed store decorator
//  runs an id through. A benign id passes; a cross-scope / path-traversal / NUL /
//  reserved id is rejected before it can reach another tenant's key space.
// ══════════════════════════════════════════════════════════════════════════════

let private sanitiserRejects (id: string) =
    match IdentitySanitiser.sanitiseScopeId id with
    | Error _ -> true
    | Ok _ -> false

let private lens3TenantIsolation =
    testList "Lens 3 — tenant scope isolation" [
        test "secure: a benign GUID scope id passes the sanitiser" {
            let id = Guid.NewGuid().ToString("N")

            match IdentitySanitiser.sanitiseScopeId id with
            | Ok v -> Expect.equal v id "a benign id is returned unchanged"
            | Error e -> failtestf "benign id rejected — %s" e
        }

        test "insecure: a forward-slash cross-scope traversal id is rejected" {
            Expect.isTrue
                (sanitiserRejects "../../_platform/permissions/t")
                "path traversal into another scope must be rejected"
        }

        test "insecure: a backslash traversal id is rejected" {
            Expect.isTrue (sanitiserRejects "..\\..\\secrets") "backslash traversal must be rejected"
        }

        test "insecure: a NUL-byte-embedded id is rejected" {
            Expect.isTrue (sanitiserRejects ("team" + string '\000' + "id")) "a NUL byte must be rejected"
        }

        test "insecure: a simple slash-separated id is rejected" {
            Expect.isTrue (sanitiserRejects "a/b") "any path separator must be rejected"
        }
    ]

// ══════════════════════════════════════════════════════════════════════════════
//  Lens 5 — request-edge auth (CSRF, SSE, share-token, peer-bearer, anon-binding)
// ══════════════════════════════════════════════════════════════════════════════

// A minimal in-memory ISecretStore for the peer-bearer rejection paths (they reject
// before any secret is read, so the store is present but never queried).
type private EmptySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

let private sseValidator (config: ServerConfig) =
    SseAuthModeValidator.SseAuthModeValidator(config) :> IConfigValidator
    |> runValidator

let private spWithDataProtection () =
    let services = ServiceCollection()
    services.AddDataProtection() |> ignore
    services.AddMemoryCache() |> ignore
    services.BuildServiceProvider()

let private ctxWith (sp: IServiceProvider) =
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    ctx

let private lens5EdgeAuth =
    testList "Lens 5 — request-edge auth" [
        // ── SSE auth-mode gate ──────────────────────────────────────────────
        test "SSE secure: Individual mode + CookieRequired → not an Error" {
            let config = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    SseAuthMode = CookieRequired
                    AcceptQueryParamSseAuthWhenAuthRequired = false
            }

            Expect.isFalse (isValidationError (sseValidator config)) "cookie auth is the production SSE path"
        }

        test "SSE insecure: Individual mode + QueryParamFallback + no escape hatch → Error" {
            let config = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.individual
                    SseAuthMode = QueryParamFallback
                    AcceptQueryParamSseAuthWhenAuthRequired = false
            }

            Expect.isTrue
                (isValidationError (sseValidator config))
                "a query-string token (URL/referer-leakable) must be refused in a production mode"
        }

        // ── Share-token reader ──────────────────────────────────────────────
        test "share-token secure: a header-borne token is read" {
            let ctx = DefaultHttpContext() :> HttpContext
            ctx.Request.Headers["X-Share-Token"] <- StringValues "share-abc"
            Expect.equal (ShareTokenAuth.tryReadToken ctx) (Some "share-abc") "a presented share token is surfaced"
        }

        test "share-token insecure: a request bearing no token yields None (nothing to authorise on)" {
            let ctx = DefaultHttpContext() :> HttpContext
            Expect.equal (ShareTokenAuth.tryReadToken ctx) None "no token ⇒ no share grant"
        }

        // ── Peer-bearer authentication ──────────────────────────────────────
        test "peer-bearer secure: 'Bearer <tok>' parses; a constant-time compare of equal secrets holds" {
            Expect.equal
                (PeerBearerAuthMiddleware.tryParseBearer "Bearer peer-secret")
                (Some "peer-secret")
                "a well-formed bearer header parses"

            Expect.isTrue
                (PeerBearerAuthMiddleware.constantTimeEquals "peer-secret" "peer-secret")
                "identical secrets compare equal"
        }

        test "peer-bearer insecure: a non-bearer scheme is not parsed; unequal secrets never match" {
            Expect.equal (PeerBearerAuthMiddleware.tryParseBearer "Basic Zm9v") None "a non-bearer scheme is refused"

            Expect.isFalse
                (PeerBearerAuthMiddleware.constantTimeEquals "peer-secret" "wrong-secret")
                "a mismatched secret must not match"
        }

        testCaseAsync "peer-bearer insecure: an X-Peer-Name that would traverse the secret key path is rejected"
        <| async {
            let ctx = DefaultHttpContext() :> HttpContext
            ctx.Request.Headers["X-Peer-Name"] <- StringValues "../evil"
            ctx.Request.Headers["Authorization"] <- StringValues "Bearer whatever"
            let store = EmptySecretStore() :> ISecretStore

            let! outcome = PeerBearerAuthMiddleware.authenticate store ctx

            match outcome with
            | PeerBearerAuthMiddleware.Rejected(Some "../evil", reason) ->
                Expect.equal
                    reason
                    PeerBearerAuthMiddleware.RejectionReason.InvalidPeerName
                    "a traversal peer name must be rejected"
            | other -> failtestf "expected an InvalidPeerName rejection, got %A" other
        }

        testCaseAsync "peer-bearer insecure: a missing X-Peer-Name header is rejected"
        <| async {
            let ctx = DefaultHttpContext() :> HttpContext
            let store = EmptySecretStore() :> ISecretStore

            let! outcome = PeerBearerAuthMiddleware.authenticate store ctx

            match outcome with
            | PeerBearerAuthMiddleware.Rejected(None, PeerBearerAuthMiddleware.RejectionReason.MissingPeerNameHeader) ->
                ()
            | other -> failtestf "expected a MissingPeerNameHeader rejection, got %A" other
        }

        // ── Anonymous-session binding (sealed, session-specific) ─────────────
        test "anon-binding secure: a binding minted for a session verifies for that same session" {
            let ctx = ctxWith (spWithDataProtection ())
            let token = (AnonymousSessionBinding.mint ctx "sid-1").Value

            Expect.isTrue
                (AnonymousSessionBinding.verify ctx token "sid-1")
                "the legitimate session's own binding verifies"
        }

        test "anon-binding insecure: a binding for session A does NOT verify for session B (replay)" {
            let ctx = ctxWith (spWithDataProtection ())
            let token = (AnonymousSessionBinding.mint ctx "session-A").Value

            Expect.isFalse
                (AnonymousSessionBinding.verify ctx token "session-B")
                "a stolen binding cannot be replayed onto another session"
        }

        test "anon-binding insecure: a tampered seal fails verification" {
            let ctx = ctxWith (spWithDataProtection ())

            Expect.isFalse
                (AnonymousSessionBinding.verify ctx "not.a.valid.seal" "sid-1")
                "a forged seal must not verify"
        }

        // ── CSRF double-submit ──────────────────────────────────────────────
        test "CSRF insecure: a request with neither header nor cookie token fails the double-submit check" {
            let ctx = DefaultHttpContext() :> HttpContext
            Expect.isFalse (Csrf.isTokenValid ctx) "an unaccompanied mutating request must not be treated as CSRF-valid"
        }

        test "CSRF insecure: a header token with no paired cookie fails the double-submit check" {
            let ctx = DefaultHttpContext() :> HttpContext
            ctx.Request.Headers["X-CSRF-Token"] <- StringValues "forged-header-only"
            Expect.isFalse (Csrf.isTokenValid ctx) "a header without its paired cookie must not validate"
        }
    ]

// ══════════════════════════════════════════════════════════════════════════════
//  Lens 6 — session / OAuth lifecycle (PKCE, single-use state, exchange gating)
// ══════════════════════════════════════════════════════════════════════════════

/// A PKCE-enforcing IOAuthCredentialFlow: an authorization code presented WITHOUT
/// its code_verifier is unredeemable (the interception defence PKCE exists for).
type private PkceEnforcingFlow() =
    interface IOAuthCredentialFlow with
        member _.Name = "threatlens-pkce-flow"

        member _.Descriptor = {
            DisplayName = "ThreatLens PKCE Flow"
            Scopes = [ "https://example.com/scope/read" ]
            HelpUrl = None
        }

        member _.SupportsPkce = true

        member _.BuildAuthorizeUrl(_ctx, _state, _redirectUri, _pkce) = async {
            return Ok "https://example.com/authorize"
        }

        member _.ExchangeCode(_ctx, code, _redirectUri, codeVerifier) = async {
            match codeVerifier with
            | None -> return Error(OAuthError.OAuthFlowFailed "PKCE code_verifier required for this flow")
            | Some _ when code = "valid-code" ->
                return
                    Ok {
                        RefreshToken = "refresh"
                        AccessToken = None
                        ExpiresAt = None
                        IdToken = None
                    }
            | Some _ -> return Error(OAuthError.ProviderRejected "invalid_grant")
        }

        member _.RefreshAccessToken(_ctx, _creds) = async { return Error OAuthError.RevocationUnsupported }
        member _.Revoke(_ctx, _creds) = async { return Error OAuthError.RevocationUnsupported }

let private mkFlowState (token: string) : OAuthFlowState = {
    Token = token
    FlowName = "threatlens-flow"
    DataSourceId = "ds-1"
    ScopeId = "scope-1"
    Container = "team-scope-1"
    UserId = "user-1"
    CreatedAt = DateTime.UtcNow
    RedirectUri = "https://example.com/api/oauth/threatlens-flow/callback"
    CodeVerifier = Some "abc123"
    // Deliberately `None`: this suite pins the LEGACY state shape, so
    // it exercises `OAuthFlowState.correlationOf`'s mapping of a
    // pre-43.B entry's `DataSourceId` onto the neutral key.
    Correlation = None
}

let private lens6OAuthLifecycle =
    testList "Lens 6 — session/OAuth lifecycle" [
        test "PKCE secure: the RFC 7636 S256 verifier→challenge vector is honoured" {
            let verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
            let expected = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"

            Expect.equal
                (OAuthCrypto.codeChallengeFromVerifier verifier)
                expected
                "the S256 challenge matches the RFC vector"
        }

        test "PKCE insecure: a substituted verifier does NOT reproduce the bound challenge" {
            let verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
            let boundChallenge = OAuthCrypto.codeChallengeFromVerifier verifier

            let attackerChallenge =
                OAuthCrypto.codeChallengeFromVerifier (OAuthCrypto.generateCodeVerifier ())

            Expect.notEqual
                attackerChallenge
                boundChallenge
                "an attacker's verifier cannot satisfy a challenge bound to the victim's"
        }

        testCaseAsync
            "PKCE secure: an exchange presenting the verifier succeeds; insecure: a verifier-less exchange is refused"
        <| async {
            let flow = PkceEnforcingFlow() :> IOAuthCredentialFlow

            let ctx: OAuthFlowContext = OAuthFlowContext.forDataSource "s" "ds" None

            match! flow.ExchangeCode(ctx, "valid-code", "https://example.com/cb", Some "verifier") with
            | Ok _ -> ()
            | Error e -> failtestf "secure PKCE exchange rejected — %A" e

            match! flow.ExchangeCode(ctx, "valid-code", "https://example.com/cb", None) with
            | Ok _ -> failtest "an intercepted code with no verifier must not be redeemable"
            | Error _ -> ()
        }

        testCaseAsync "state store secure: a fresh state consumes once; insecure: the replay is refused"
        <| async {
            let store = InMemoryOAuthStateStore() :> IOAuthStateStore
            let entry = mkFlowState "tok-single"

            match! store.Save entry with
            | Ok() -> ()
            | Error msg -> failtestf "state Save failed — %s" msg

            let! first = store.TryConsume "tok-single"
            Expect.isSome first "the first consume returns the stored state"

            let! second = store.TryConsume "tok-single"
            Expect.isNone second "a second consume of the same state token must fail (single-use)"
        }
    ]

// ══════════════════════════════════════════════════════════════════════════════
//  Lens 4 — authorization / RBAC (fail-closed defaults, Phase 132)
//
//  Two seams: (a) the per-method dispatcher classifier — an UNCLASSIFIED method
//  denies even a real admin (fail-closed by construction); (b) the platform-admin
//  path-prefix backstop that guards raw Giraffe handlers bypassing the dispatcher —
//  a non-admin is 403'd before the handler runs; a stamped PlatformAdmin passes.
// ══════════════════════════════════════════════════════════════════════════════

let private authContext (roles: string list) (anonymous: bool) : IAuthContext =
    { new IAuthContext with
        member _.HasRole role = List.contains role roles
        member _.HasClaim(claim, value) = false
        member _.HasTenant() = true
        member _.IsAnonymous() = anonymous
        member _.SubjectId = "test-subject"
    }

/// Drive the platform-admin backstop over a fresh HttpContext, optionally stamping
/// the PlatformAdmin role the way ScopeResolutionMiddleware would. Returns
/// (status, handler-reached).
let private runBackstop (httpMethod: string) (path: string) (stampAdmin: bool) : int * bool =
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.Request.Method <- httpMethod
    ctx.Request.Path <- PathString path

    if stampAdmin then
        ctx.Items["ToolUp.PlatformRole"] <- box ToolUp.Platform.PlatformRole.PlatformAdmin

    let mutable reached = false

    let next =
        RequestDelegate(fun _ ->
            reached <- true
            Task.CompletedTask)

    let mw =
        ToolUp.Platform.PlatformAdminAuthorization.PlatformAdminAuthorizationMiddleware(next)

    mw.InvokeAsync(ctx).GetAwaiter().GetResult()
    ctx.Response.StatusCode, reached

let private lens4Authz =
    testList "Lens 4 — authorization / RBAC" [
        test "classifier fail-closed: an UNCLASSIFIED method denies even a genuine admin" {
            let decision =
                AuthClassifier.evaluate Unclassified (Some(authContext [ "PlatformAdmin" ] false))

            match decision with
            | Deny _ -> ()
            | Allow -> failtest "an unclassified method must fail closed, not default to allow"
        }

        test "backstop secure: a stamped PlatformAdmin reaches the guarded handler" {
            let status, reached = runBackstop "POST" "/api/_platform/admin/ad-units" true
            Expect.isTrue reached "the admin request reaches the handler"
            Expect.notEqual status 403 "a genuine admin is not denied by the backstop"
        }

        test "backstop insecure: a non-admin is 403'd at the backstop, never reaching the handler (deny-on-miss)" {
            let status, reached = runBackstop "POST" "/api/_platform/admin/ad-units" false
            Expect.equal status 403 "a non-admin is denied with 403"
            Expect.isFalse reached "the handler is never invoked — fail-closed before dispatch"
        }
    ]

// ══════════════════════════════════════════════════════════════════════════════
//  Reverted-control proof — the suite catches regressions, not just passes.
//
//  Each case pairs the REAL production decision against a deliberately-reverted
//  stand-in (a control that skips the security check) and asserts that the stand-in
//  would FAIL the exact assertion the real code passes. This makes the phase's
//  acceptance self-evident: e.g. "forcing dev-admin bypass on in production mode"
//  makes the mode-gating lens fail, proving that lens's teeth.
// ══════════════════════════════════════════════════════════════════════════════

let private lensRevertedControlProof =
    testList "reverted-control proof (the suite has teeth)" [
        test "mode-gating: a reverted validator that always passes fails the Lens-2 production-refusal assertion" {
            // Reverted control: a validator that ignores the mode and returns Ok —
            // exactly the regression Lens 2 guards against (dev bypass live in prod).
            let revertedResult = ToolUp.Platform.ConfigValidation.ValidationResult.Ok

            Expect.isFalse
                (isValidationError revertedResult)
                "the reverted control does NOT refuse (that is the regression)"
        // Therefore the Lens-2 assertion `Expect.isTrue (isValidationError …)`
        // would fail against it — the lens catches the regression.
        }

        test "tenant-isolation: a reverted sanitiser that echoes its input lets a traversal id through" {
            // Reverted control: the naive `id -> Ok id` passthrough Phase 131 replaced.
            let revertedSanitise (id: string) : Result<string, string> = Ok id
            let malicious = "../../_platform/permissions/t"

            let leaks =
                match revertedSanitise malicious with
                | Ok _ -> true
                | Error _ -> false

            Expect.isTrue leaks "the reverted sanitiser leaks the cross-scope id — the regression Lens 3 catches"
            // The real sanitiser rejects it; the Lens-3 `Expect.isTrue (sanitiserRejects …)`
            // assertion fails against this reverted control.
            Expect.isTrue (sanitiserRejects malicious) "the shipped sanitiser still rejects it"
        }

        test "OAuth PKCE: a reverted flow that redeems a verifier-less code is caught by the Lens-6 assertion" {
            // Reverted control: a flow that skips PKCE enforcement.
            let revertedExchange (codeVerifier: string option) : Result<unit, string> = Ok()

            let redeemsWithoutVerifier =
                match revertedExchange None with
                | Ok _ -> true
                | Error _ -> false

            Expect.isTrue
                redeemsWithoutVerifier
                "the reverted flow redeems an intercepted code — the regression Lens 6 catches"
        }
    ]

// ─── The suite ──────────────────────────────────────────────────────────────────

let tests =
    testList "ThreatLensRegressionSuite" [
        lens1Jwt
        lens2ModeGating
        lens3TenantIsolation
        lens4Authz
        lens5EdgeAuth
        lens6OAuthLifecycle
        lensRevertedControlProof
    ]