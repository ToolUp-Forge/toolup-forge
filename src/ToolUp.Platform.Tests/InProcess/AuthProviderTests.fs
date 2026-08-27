module ToolUp.Platform.Tests.InProcess.AuthProviderTests

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.Metrics
open ToolUp.AuthProviders
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.InProcess.MockOidcServer

// ─── Recording metrics sink (Phase 9e.A) ────────────────────────────
//
// Test-only `IMetricsSink` that appends every `Increment` call to an
// internal ResizeArray so assertions can inspect per-counter / per-tag
// emission. Mirrors the `RecordingMetricsSink` already in
// `FormsServerHygieneTests`; kept local rather than promoted to a
// shared fixture because the assertion shape differs slightly per
// test pack and the type is trivially small.

type private RecordingMetricsSink() =
    let increments = ResizeArray<string * Map<string, string>>()

    interface IMetricsSink with
        member _.Record(_, _, _) = ()
        member _.Increment(name, tags) = increments.Add(name, tags)
        member _.SetGauge(_, _, _) = ()

    member _.Increments = increments :> seq<_>

// ─── HttpContext helpers ─────────────────────────────────────────────
//
// Per-provider tests rather than a shared contract: HeaderAuth and
// StaticJwt disagree on what counts as "valid credentials" (header
// presence vs HS256-signed JWT), and the interface's docstring-level
// contract (GetUser lenient, ValidateRequest strict) isn't uniformly
// upheld today (HeaderAuthProvider's ValidateRequest returns Ok for
// anonymous requests). Tests pin each provider's actual behaviour.

let private mkContext () = DefaultHttpContext() :> HttpContext

let private withHeader (name: string) (value: string) (ctx: HttpContext) =
    ctx.Request.Headers[name] <- StringValues value
    ctx

// Phase 11.C.5 Tier 3 — wrap an `HttpContext` for the IAuthProvider
// boundary. `bearerCtx` applies this at construction time so most
// callsites pass `bearerCtx token` directly to `provider.GetUser` /
// `provider.ValidateRequest`. Callers building a custom context via
// `mkContext () |> withHeader …` pipe through `toReq` at the end.
let private toReq (ctx: HttpContext) =
    ToolUp.Platform.RequestContextBuilder.ofHttpContext ctx

// ─── HS256 JWT minter (tests only) ──────────────────────────────────

module private JwtMinter =
    let private base64UrlEncode (bytes: byte[]) =
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

    /// Mint an HS256 JWT for a given set of claims. Claims is a list of
    /// (name, value) — all values are serialised as strings EXCEPT `exp`
    /// / `iat` / `nbf` which are emitted as numbers. Keep the test
    /// fixture format narrow; the provider parses the payload with
    /// System.Text.Json and reads the specific claims it cares about.
    let mint (secret: string) (claims: (string * obj) list) =
        let header = """{"alg":"HS256","typ":"JWT"}"""
        let headerB64 = base64UrlEncode (Encoding.UTF8.GetBytes header)

        let payloadObj =
            let opts = JsonSerializerOptions()
            let dict = System.Collections.Generic.Dictionary<string, obj>()

            for k, v in claims do
                dict[k] <- v

            JsonSerializer.Serialize(dict, opts)

        let payloadB64 = base64UrlEncode (Encoding.UTF8.GetBytes payloadObj)

        let message = Encoding.UTF8.GetBytes $"{headerB64}.{payloadB64}"
        use hmac = new HMACSHA256(Encoding.UTF8.GetBytes secret)
        let signature = hmac.ComputeHash message
        let sigB64 = base64UrlEncode signature

        $"{headerB64}.{payloadB64}.{sigB64}"

// ─── HeaderAuthProvider ─────────────────────────────────────────────

let private headerAuthTests =
    testList "HeaderAuthProvider" [
        testCaseAsync "GetUser returns anonymous when X-User-Id is absent"
        <| async {
            let provider = HeaderAuthProvider.HeaderAuthProvider() :> IAuthProvider
            let! user = provider.GetUser(mkContext () |> toReq)

            Expect.equal user.UserId "anonymous" "no header → anonymous"
        }

        testCaseAsync "GetUser populates UserId from X-User-Id header"
        <| async {
            let provider = HeaderAuthProvider.HeaderAuthProvider() :> IAuthProvider
            let ctx = mkContext () |> withHeader "X-User-Id" "alice" |> toReq
            let! user = provider.GetUser ctx

            Expect.equal user.UserId "alice" "UserId comes from header"
            Expect.equal user.DisplayName "alice" "DisplayName defaults to UserId"
        }

        testCaseAsync "ValidateRequest returns Ok for any request (dev-only behaviour)"
        <| async {
            // HeaderAuthProvider is dev-only and lenient: ValidateRequest
            // never returns Error. Production deployments swap in a
            // real provider — this test pins the current dev behaviour
            // so a future regression away from "always Ok" is caught.
            let provider = HeaderAuthProvider.HeaderAuthProvider() :> IAuthProvider

            match! provider.ValidateRequest(mkContext () |> toReq) with
            | Ok _ -> ()
            | Error e -> failtestf "HeaderAuth ValidateRequest should be Ok; got Error: %s" e
        }
    ]

// ─── StaticJwtAuthProvider ──────────────────────────────────────────

let private testSecret = "test-secret-at-least-32-bytes-long!!"

let private futureExp () =
    DateTimeOffset.UtcNow.AddHours(1.0).ToUnixTimeSeconds() |> box

let private pastExp () =
    DateTimeOffset.UtcNow.AddHours(-1.0).ToUnixTimeSeconds() |> box

let private bearerCtx (token: string) =
    mkContext () |> withHeader "Authorization" ("Bearer " + token) |> toReq

let private staticJwtTests =
    let provider (config: StaticJwtAuthProvider.StaticJwtConfig) =
        StaticJwtAuthProvider.StaticJwtAuthProvider(config) :> IAuthProvider

    let defaultConfig: StaticJwtAuthProvider.StaticJwtConfig = {
        Secret = testSecret
        Issuer = None
        Audience = None
    }

    testList "StaticJwtAuthProvider" [
        testCaseAsync "ValidateRequest returns Error when no token is present"
        <| async {
            let p = provider defaultConfig

            match! p.ValidateRequest(mkContext () |> toReq) with
            | Ok _ -> failtest "Expected Error for missing bearer token"
            | Error _ -> ()
        }

        testCaseAsync "ValidateRequest validates a well-formed HS256 token"
        <| async {
            let p = provider defaultConfig

            let token =
                JwtMinter.mint testSecret [
                    "sub", box "alice"
                    "name", box "Alice"
                    "email", box "alice@example.com"
                    "exp", futureExp ()
                ]

            match! p.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "Expected Ok; got Error: %s" e
            | Ok user ->
                Expect.equal user.UserId "alice" "UserId from sub"
                Expect.equal user.DisplayName "Alice" "DisplayName from name"
                Expect.equal user.Email (Some "alice@example.com") "Email from email"
        }

        testCaseAsync "GetUser lenient: valid token returns the user"
        <| async {
            let p = provider defaultConfig
            let token = JwtMinter.mint testSecret [ "sub", box "bob"; "exp", futureExp () ]
            let! user = p.GetUser(bearerCtx token)

            Expect.equal user.UserId "bob" "UserId from valid token"
        }

        testCaseAsync "GetUser lenient: missing token returns anonymous"
        <| async {
            let p = provider defaultConfig
            let! user = p.GetUser(mkContext () |> toReq)

            Expect.equal user.UserId "anonymous" "no token → anonymous"
        }

        testCaseAsync "Rejects expired tokens"
        <| async {
            let p = provider defaultConfig
            let token = JwtMinter.mint testSecret [ "sub", box "alice"; "exp", pastExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected Error for expired token"
            | Error _ -> ()
        }

        testCaseAsync "Rejects a token with no exp claim (no-expiry is never a safe default)"
        <| async {
            let p = provider defaultConfig
            // No `exp` minted at all. Pre-hardening this was accepted
            // ("no exp = no expiry"); now it must be refused, matching
            // OidcAuthProvider's MissingExpiry behaviour.
            let token = JwtMinter.mint testSecret [ "sub", box "alice" ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected Error for token with no exp claim"
            | Error _ -> ()
        }

        testCaseAsync "Rejects a token whose nbf is in the future"
        <| async {
            let p = provider defaultConfig

            let futureNbf = DateTimeOffset.UtcNow.AddHours(1.0).ToUnixTimeSeconds() |> box

            let token =
                JwtMinter.mint testSecret [ "sub", box "alice"; "exp", futureExp (); "nbf", futureNbf ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected Error for token not yet valid (future nbf)"
            | Error _ -> ()
        }

        testCaseAsync "Accepts a token whose nbf is in the past"
        <| async {
            let p = provider defaultConfig

            let pastNbf = DateTimeOffset.UtcNow.AddHours(-1.0).ToUnixTimeSeconds() |> box

            let token =
                JwtMinter.mint testSecret [ "sub", box "alice"; "exp", futureExp (); "nbf", pastNbf ]

            match! p.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "Expected Ok for past nbf; got Error: %s" e
            | Ok user -> Expect.equal user.UserId "alice" "valid nbf accepted"
        }

        testCaseAsync "Rejects tokens signed with a different secret"
        <| async {
            let p = provider defaultConfig

            let token =
                JwtMinter.mint "wrong-secret-used-to-sign-this-token" [ "sub", box "attacker"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected Error for bad signature"
            | Error _ -> ()
        }

        testCaseAsync "Rejects tokens with wrong issuer when Issuer is configured"
        <| async {
            let p =
                provider {
                    defaultConfig with
                        Issuer = Some "https://expected.example.com"
                }

            let token =
                JwtMinter.mint testSecret [
                    "sub", box "alice"
                    "iss", box "https://other.example.com"
                    "exp", futureExp ()
                ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected Error for mismatched issuer"
            | Error _ -> ()
        }

        testCaseAsync "Rejects tokens with wrong audience when Audience is configured"
        <| async {
            let p =
                provider {
                    defaultConfig with
                        Audience = Some "expected-aud"
                }

            let token =
                JwtMinter.mint testSecret [ "sub", box "alice"; "aud", box "other-aud"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected Error for mismatched audience"
            | Error _ -> ()
        }

        testCaseAsync "Accepts token when Issuer + Audience claims match config"
        <| async {
            let p =
                provider {
                    defaultConfig with
                        Issuer = Some "https://issuer.example.com"
                        Audience = Some "my-app"
                }

            let token =
                JwtMinter.mint testSecret [
                    "sub", box "alice"
                    "iss", box "https://issuer.example.com"
                    "aud", box "my-app"
                    "exp", futureExp ()
                ]

            match! p.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "Expected Ok with matching claims; got Error: %s" e
            | Ok user -> Expect.equal user.UserId "alice" "claims matched"
        }

        testCaseAsync "Rejects malformed token"
        <| async {
            let p = provider defaultConfig
            let ctx = bearerCtx "not-even-a-jwt"

            match! p.ValidateRequest ctx with
            | Ok _ -> failtest "Expected Error for malformed token"
            | Error _ -> ()
        }

        testCaseAsync "Rejects a correctly-signed token with no sub claim"
        <| async {
            let p = provider defaultConfig
            // Gap audit 2026-06-12 Auth G2 — pre-hardening this
            // authenticated as the literal "anonymous" sentinel,
            // landing the caller in the shared anonymous scope.
            let token = JwtMinter.mint testSecret [ "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected Error for token with no sub claim"
            | Error _ -> ()
        }

        testCaseAsync "Rejects a token whose sub fails identity sanitisation"
        <| async {
            let p = provider defaultConfig
            // Same sanitiser the OIDC provider applies (Auth G2) — a
            // path-traversal sub must never reach UserId.
            let token =
                JwtMinter.mint testSecret [ "sub", box "../etc/passwd"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected Error for path-traversal sub claim"
            | Error _ -> ()
        }
    ]

// ─── OidcAuthProvider ───────────────────────────────────────────────
//
// Full-loop JWKS validation via mock-HTTP injection: `fromConfigWith`
// accepts an `HttpClient` whose backing `HttpMessageHandler` we
// control. The stub serves `.well-known/openid-configuration` + JWKS
// payloads built around a fresh RSA key per fixture; JWTs are signed
// with the private half, verified by the provider against the JWKS
// public half. No real OIDC issuer, no Kestrel.
//
// Each fixture uses GUID-suffixed URLs so the module-level JWKS /
// discovery caches in `OidcAuthProvider.Jwks.fs` don't bleed state
// across tests.

module private OidcFixture =
    let private base64UrlEncodeBytes (bytes: byte[]) =
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

    type IssuerKey = {
        Rsa: RSA
        Kid: string
        IssuerUrl: string
        JwksUrl: string
        DiscoveryUrl: string
    }

    /// Fresh keypair + unique URLs per call. The RSA instance is
    /// long-lived for the duration of the test; Expecto disposes it
    /// when the test list is garbage-collected.
    let mkKey () : IssuerKey =
        let rsa = RSA.Create 2048
        let unique = Guid.NewGuid().ToString("N").Substring(0, 8)
        let issuer = $"https://oidc-fixture/{unique}"

        {
            Rsa = rsa
            Kid = $"test-key-{unique}"
            IssuerUrl = issuer
            JwksUrl = $"{issuer}/jwks.json"
            DiscoveryUrl = $"{issuer}/.well-known/openid-configuration"
        }

    let private payloadB64 (claims: (string * obj) list) =
        let dict = System.Collections.Generic.Dictionary<string, obj>()

        for k, v in claims do
            dict[k] <- v

        base64UrlEncodeBytes (Encoding.UTF8.GetBytes(JsonSerializer.Serialize dict))

    let private rsaPaddingFor =
        function
        | RS256
        | RS384
        | RS512 -> RSASignaturePadding.Pkcs1
        | PS256 -> RSASignaturePadding.Pss
        | ES256 ->
            // Sentinel never hit at runtime — callers route ES256
            // through `mintEs256` which uses ECDsa, not RSA.
            RSASignaturePadding.Pkcs1

    let private hashFor =
        function
        | RS256
        | PS256
        | ES256 -> HashAlgorithmName.SHA256
        | RS384 -> HashAlgorithmName.SHA384
        | RS512 -> HashAlgorithmName.SHA512

    /// Mint a JWT signed with an RSA-family algorithm (RS256 / RS384 /
    /// RS512 / PS256) carrying `kid` in its header. ES256 is handled
    /// separately via `mintEs256` because it uses an EC key. Claims is
    /// a list of (name, value); strings, ints, and obj are all OK —
    /// `JsonSerializer.Serialize` handles each based on its runtime
    /// type. Exponent of int64 from `futureExp` flows through correctly
    /// because boxed int64 serialises as a JSON number.
    let mintRsa (alg: JwsAlgorithm) (key: IssuerKey) (claims: (string * obj) list) =
        let algStr = JwsAlgorithm.toString alg
        let header = $"""{{"alg":"{algStr}","typ":"JWT","kid":"{key.Kid}"}}"""
        let headerB64 = base64UrlEncodeBytes (Encoding.UTF8.GetBytes header)
        let pB64 = payloadB64 claims
        let message = Encoding.UTF8.GetBytes $"{headerB64}.{pB64}"
        let signature = key.Rsa.SignData(message, hashFor alg, rsaPaddingFor alg)
        let sigB64 = base64UrlEncodeBytes signature
        $"{headerB64}.{pB64}.{sigB64}"

    /// Convenience shorthand for the historical RS256-only callers
    /// — preserves the existing test bodies without churning every
    /// site to `mintRsa RS256`.
    let mintRs256 (key: IssuerKey) (claims: (string * obj) list) = mintRsa RS256 key claims

    /// JWKS JSON containing one RSA public key. RSAParameters export
    /// with `false` keeps the private half out of the response — the
    /// stub serves only what a real JWKS endpoint would.
    let buildJwks (key: IssuerKey) =
        let p = key.Rsa.ExportParameters(false)
        let n = base64UrlEncodeBytes p.Modulus
        let e = base64UrlEncodeBytes p.Exponent
        $"""{{"keys":[{{"kty":"RSA","kid":"{key.Kid}","alg":"RS256","use":"sig","n":"{n}","e":"{e}"}}]}}"""

    // ─── EC (ES256) fixture ─────────────────────────────────────────

    type IssuerEcKey = {
        Ec: ECDsa
        Kid: string
        IssuerUrl: string
        JwksUrl: string
        DiscoveryUrl: string
    }

    /// Fresh P-256 keypair + unique URLs. Mirrors `mkKey` but binds an
    /// `ECDsa` instance instead of `RSA`. Per-test isolation keeps the
    /// module-level JWKS / discovery caches from bleeding state.
    let mkEcKey () : IssuerEcKey =
        let ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let unique = Guid.NewGuid().ToString("N").Substring(0, 8)
        let issuer = $"https://oidc-ec-fixture/{unique}"

        {
            Ec = ec
            Kid = $"test-ec-key-{unique}"
            IssuerUrl = issuer
            JwksUrl = $"{issuer}/jwks.json"
            DiscoveryUrl = $"{issuer}/.well-known/openid-configuration"
        }

    /// Mint an ES256 JWT carrying `kid` in its header. The signature
    /// is emitted in IEEE-P1363 (r||s, 64 bytes for P-256) — the JWS
    /// transport — not the DER form `ECDsa` would default to.
    let mintEs256 (key: IssuerEcKey) (claims: (string * obj) list) =
        let header = $"""{{"alg":"ES256","typ":"JWT","kid":"{key.Kid}"}}"""
        let headerB64 = base64UrlEncodeBytes (Encoding.UTF8.GetBytes header)
        let pB64 = payloadB64 claims
        let message = Encoding.UTF8.GetBytes $"{headerB64}.{pB64}"

        let signature =
            key.Ec.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)

        let sigB64 = base64UrlEncodeBytes signature
        $"{headerB64}.{pB64}.{sigB64}"

    /// JWKS JSON containing one EC public key. `ExportParameters false`
    /// drops the private scalar; the JWKS stub serves only what a real
    /// provider would. `Q.X` / `Q.Y` carry the affine coordinates the
    /// `crv = P-256` curve binds.
    let buildEcJwks (key: IssuerEcKey) =
        let p = key.Ec.ExportParameters(false)
        let x = base64UrlEncodeBytes p.Q.X
        let y = base64UrlEncodeBytes p.Q.Y

        $"""{{"keys":[{{"kty":"EC","kid":"{key.Kid}","alg":"ES256","use":"sig","crv":"P-256","x":"{x}","y":"{y}"}}]}}"""

    /// Minimal OIDC discovery document — `jwks_uri` is the only field
    /// the provider needs. Other endpoints listed for shape-realism
    /// only.
    let buildDiscovery (key: IssuerKey) =
        $"""{{"issuer":"{key.IssuerUrl}","jwks_uri":"{key.JwksUrl}","authorization_endpoint":"{key.IssuerUrl}/auth","token_endpoint":"{key.IssuerUrl}/token","id_token_signing_alg_values_supported":["RS256"]}}"""

/// Stub `HttpMessageHandler` that routes requests by absolute URL to
/// pre-registered response bodies. Anything not in the map returns
/// 404 — surfaces as `JwksUnavailable` in the provider.
type private StubHttpHandler(routes: Map<string, string>) =
    inherit HttpMessageHandler()

    override _.SendAsync(request: HttpRequestMessage, _ct: CancellationToken) : Task<HttpResponseMessage> =
        let url = string request.RequestUri

        match routes.TryFind url with
        | Some body ->
            let response = new HttpResponseMessage(HttpStatusCode.OK)
            response.Content <- new StringContent(body, Encoding.UTF8, "application/json")
            Task.FromResult response
        | None ->
            let response = new HttpResponseMessage(HttpStatusCode.NotFound)
            response.Content <- new StringContent($"no stub for {url}")
            Task.FromResult response

let private oidcTests =
    let mkClient (routes: (string * string) list) : HttpClient =
        new HttpClient(new StubHttpHandler(Map.ofList routes))

    let mkProviderExplicit (key: OidcFixture.IssuerKey) (issuer: string option) (audience: string option) =
        let client = mkClient [ key.JwksUrl, OidcFixture.buildJwks key ]

        let config = {
            Issuer = issuer
            Audience = audience
            KeySource = JwksExplicit key.JwksUrl
            TokenLocation = BearerHeader
            ClockSkewSeconds = None
            AcceptedAlgorithms = None
            PreferOidWhenPresent = None
        }

        OidcAuthProvider.fromConfigWith client None config

    let mkProviderDiscovery (key: OidcFixture.IssuerKey) (audience: string option) =
        let client =
            mkClient [
                key.DiscoveryUrl, OidcFixture.buildDiscovery key
                key.JwksUrl, OidcFixture.buildJwks key
            ]

        let config = {
            Issuer = Some key.IssuerUrl
            Audience = audience
            KeySource = JwksDiscovery key.IssuerUrl
            TokenLocation = BearerHeader
            ClockSkewSeconds = None
            AcceptedAlgorithms = None
            PreferOidWhenPresent = None
        }

        OidcAuthProvider.fromConfigWith client None config

    testList "OidcAuthProvider" [
        testCaseAsync "ValidateRequest validates an RS256 token against JwksExplicit"
        <| async {
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key None None

            let token =
                OidcFixture.mintRs256 key [
                    "sub", box "alice"
                    "name", box "Alice"
                    "email", box "alice@example.com"
                    "exp", futureExp ()
                ]

            match! p.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "Expected Ok; got Error: %s" e
            | Ok user ->
                Expect.equal user.UserId "alice" "UserId from sub"
                Expect.equal user.DisplayName "Alice" "DisplayName from name"
                Expect.equal user.Email (Some "alice@example.com") "Email from email"
        }

        testCaseAsync "ValidateRequest validates via OIDC discovery (JwksDiscovery)"
        <| async {
            // Exercises the .well-known fetch + jwks_uri extraction path,
            // confirming the discovery flow uses the injected HttpClient.
            let key = OidcFixture.mkKey ()
            let p = mkProviderDiscovery key None

            let token =
                OidcFixture.mintRs256 key [ "sub", box "bob"; "iss", box key.IssuerUrl; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "Expected Ok; got Error: %s" e
            | Ok user -> Expect.equal user.UserId "bob" "UserId from sub via discovery flow"
        }

        testCaseAsync "Phase 134: refuses a cleartext (http) discovered jwks_uri"
        <| async {
            // Issuer is https (construction-time requireHttps passes), but
            // a hostile / compromised metadata document returns an http
            // jwks_uri. The request-time guard must refuse before fetching
            // keys over a MITM-substitutable channel — even though the stub
            // WOULD serve a valid key set at that URL.
            let key = OidcFixture.mkKey ()
            let evilJwksUrl = "http://evil.example/jwks.json"

            let discoveryDoc =
                $"""{{"issuer":"{key.IssuerUrl}","jwks_uri":"{evilJwksUrl}","authorization_endpoint":"{key.IssuerUrl}/auth","token_endpoint":"{key.IssuerUrl}/token","id_token_signing_alg_values_supported":["RS256"]}}"""

            let client =
                mkClient [ key.DiscoveryUrl, discoveryDoc; evilJwksUrl, OidcFixture.buildJwks key ]

            let config = {
                Issuer = Some key.IssuerUrl
                Audience = None
                KeySource = JwksDiscovery key.IssuerUrl
                TokenLocation = BearerHeader
                ClockSkewSeconds = None
                AcceptedAlgorithms = None
                PreferOidWhenPresent = None
            }

            let p = OidcAuthProvider.fromConfigWith client None config

            let token =
                OidcFixture.mintRs256 key [ "sub", box "mallory"; "iss", box key.IssuerUrl; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected refusal of cleartext discovered jwks_uri, but validation succeeded"
            | Error _ -> ()
        }

        testCaseAsync "Phase 134: permits a loopback-http discovered jwks_uri (local dev IdP)"
        <| async {
            // The loopback carve-out applies to the discovered endpoint too,
            // so a local mock IdP serving keys over http on a loopback host
            // still validates.
            let key = OidcFixture.mkKey ()
            let loopbackJwksUrl = "http://127.0.0.1/oidc-fixture-jwks.json"

            let discoveryDoc =
                $"""{{"issuer":"{key.IssuerUrl}","jwks_uri":"{loopbackJwksUrl}","authorization_endpoint":"{key.IssuerUrl}/auth","token_endpoint":"{key.IssuerUrl}/token","id_token_signing_alg_values_supported":["RS256"]}}"""

            let client =
                mkClient [ key.DiscoveryUrl, discoveryDoc; loopbackJwksUrl, OidcFixture.buildJwks key ]

            let config = {
                Issuer = Some key.IssuerUrl
                Audience = None
                KeySource = JwksDiscovery key.IssuerUrl
                TokenLocation = BearerHeader
                ClockSkewSeconds = None
                AcceptedAlgorithms = None
                PreferOidWhenPresent = None
            }

            let p = OidcAuthProvider.fromConfigWith client None config

            let token =
                OidcFixture.mintRs256 key [ "sub", box "devuser"; "iss", box key.IssuerUrl; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "loopback-http discovered jwks_uri should be permitted; got Error: %s" e
            | Ok user -> Expect.equal user.UserId "devuser" "loopback dev IdP validates"
        }

        testCaseAsync "GetUser lenient: valid token returns the user"
        <| async {
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key None None
            let token = OidcFixture.mintRs256 key [ "sub", box "carol"; "exp", futureExp () ]
            let! user = p.GetUser(bearerCtx token)

            Expect.equal user.UserId "carol" "UserId from valid token"
        }

        testCaseAsync "GetUser lenient: missing token returns anonymous"
        <| async {
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key None None
            let! user = p.GetUser(mkContext () |> toReq)

            Expect.equal user.UserId "anonymous" "no token → anonymous"
        }

        testCaseAsync "Rejects expired tokens"
        <| async {
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key None None
            let token = OidcFixture.mintRs256 key [ "sub", box "alice"; "exp", pastExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected Error for expired token"
            | Error _ -> ()
        }

        testCaseAsync "Rejects tokens signed by a different key"
        <| async {
            // Two distinct keypairs share a kid: a forged token signed by
            // the attacker's key carries the legitimate kid in its header
            // but the provider verifies against the legitimate JWKS, which
            // is the public half of the legitimate key.
            let legitKey = OidcFixture.mkKey ()

            let attackerKey = { legitKey with Rsa = RSA.Create 2048 }

            let p = mkProviderExplicit legitKey None None

            let token =
                OidcFixture.mintRs256 attackerKey [ "sub", box "attacker"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected Error for bad signature"
            | Error _ -> ()
        }

        testCaseAsync "Rejects tokens with wrong issuer when Issuer is configured"
        <| async {
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key (Some "https://expected.example.com") None

            let token =
                OidcFixture.mintRs256 key [
                    "sub", box "alice"
                    "iss", box "https://other.example.com"
                    "exp", futureExp ()
                ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected Error for mismatched issuer"
            | Error _ -> ()
        }

        testCaseAsync "Rejects tokens with wrong audience when Audience is configured"
        <| async {
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key None (Some "expected-aud")

            let token =
                OidcFixture.mintRs256 key [ "sub", box "alice"; "aud", box "other-aud"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected Error for mismatched audience"
            | Error _ -> ()
        }

        testCaseAsync "Accepts token when Issuer + Audience claims match config"
        <| async {
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key (Some key.IssuerUrl) (Some "my-app")

            let token =
                OidcFixture.mintRs256 key [
                    "sub", box "alice"
                    "iss", box key.IssuerUrl
                    "aud", box "my-app"
                    "exp", futureExp ()
                ]

            match! p.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "Expected Ok with matching claims; got Error: %s" e
            | Ok user -> Expect.equal user.UserId "alice" "claims matched"
        }

        testCaseAsync "Rejects tokens with unknown kid"
        <| async {
            // Forge a token whose kid header doesn't match any JWKS
            // entry. The provider tries a force-refresh on kid-miss;
            // the refresh hits the same stub (still only the legit kid)
            // and returns UnknownKid.
            let legitKey = OidcFixture.mkKey ()

            let forgedKey = {
                legitKey with
                    Kid = $"forged-kid-{Guid.NewGuid():N}"
                    Rsa = RSA.Create 2048
            }

            let p = mkProviderExplicit legitKey None None

            let token =
                OidcFixture.mintRs256 forgedKey [ "sub", box "attacker"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected Error for unknown kid"
            | Error _ -> ()
        }

        testCaseAsync "Rejects tokens with no kid in the header"
        <| async {
            // Hand-mint a token without kid in the header. The provider
            // requires kid to route to a JWKS entry; absence is
            // MalformedToken "header has no kid".
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key None None

            let base64UrlEncodeBytes (bytes: byte[]) =
                Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

            let header = """{"alg":"RS256","typ":"JWT"}"""
            let headerB64 = base64UrlEncodeBytes (Encoding.UTF8.GetBytes header)

            let payload =
                let dict = System.Collections.Generic.Dictionary<string, obj>()
                dict["sub"] <- box "alice"
                dict["exp"] <- futureExp ()
                JsonSerializer.Serialize dict

            let payloadB64 = base64UrlEncodeBytes (Encoding.UTF8.GetBytes payload)
            let message = Encoding.UTF8.GetBytes $"{headerB64}.{payloadB64}"

            let signature =
                key.Rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)

            let token = $"{headerB64}.{payloadB64}.{base64UrlEncodeBytes signature}"

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected Error for missing kid"
            | Error _ -> ()
        }

        testCaseAsync "Rejects HS256-signed tokens (algorithm not RS256)"
        <| async {
            // The HS256 path belongs to StaticJwtAuthProvider; OIDC
            // rejects non-RS256 algorithms outright with
            // UnsupportedAlgorithm.
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key None None

            let hs256Token =
                JwtMinter.mint "any-secret-32-bytes-long-blah-blah!!" [ "sub", box "alice"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx hs256Token) with
            | Ok _ -> failtest "Expected Error for HS256 token against OIDC provider"
            | Error _ -> ()
        }

        testCaseAsync "Rejects malformed token"
        <| async {
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key None None
            let ctx = bearerCtx "not-even-a-jwt"

            match! p.ValidateRequest ctx with
            | Ok _ -> failtest "Expected Error for malformed token"
            | Error _ -> ()
        }

        // ─── Phase 3.A — algorithm whitelist + per-algorithm verify ──

        testCaseAsync "Default config accepts RS256 only (no AcceptedAlgorithms set)"
        <| async {
            // The whole pre-Phase-3.A test suite above is the
            // RS256-Ok proof for this lane. Pair it with an RS384
            // rejection against the same default config to lock in
            // the byte-for-byte backward-compat guarantee: today's
            // deployments inherit `AcceptedAlgorithms = None`, which
            // resolves to `[RS256]`.
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key None None

            let rs384Token =
                OidcFixture.mintRsa RS384 key [ "sub", box "alice"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx rs384Token) with
            | Ok _ -> failtest "Default RS256-only whitelist should reject RS384"
            | Error _ -> ()
        }

        testCaseAsync "RS384 token validates when whitelist includes RS384"
        <| async {
            let key = OidcFixture.mkKey ()

            let client =
                new HttpClient(new StubHttpHandler(Map.ofList [ key.JwksUrl, OidcFixture.buildJwks key ]))

            let config = {
                Issuer = None
                Audience = None
                KeySource = JwksExplicit key.JwksUrl
                TokenLocation = BearerHeader
                ClockSkewSeconds = None
                AcceptedAlgorithms = Some [ RS256; RS384 ]
                PreferOidWhenPresent = None
            }

            let p = OidcAuthProvider.fromConfigWith client None config

            let token =
                OidcFixture.mintRsa RS384 key [ "sub", box "alice"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "Expected Ok for RS384; got Error: %s" e
            | Ok user -> Expect.equal user.UserId "alice" "RS384-signed token validated"
        }

        testCaseAsync "RS512 token validates when whitelist includes RS512"
        <| async {
            let key = OidcFixture.mkKey ()

            let client =
                new HttpClient(new StubHttpHandler(Map.ofList [ key.JwksUrl, OidcFixture.buildJwks key ]))

            let config = {
                Issuer = None
                Audience = None
                KeySource = JwksExplicit key.JwksUrl
                TokenLocation = BearerHeader
                ClockSkewSeconds = None
                AcceptedAlgorithms = Some [ RS512 ]
                PreferOidWhenPresent = None
            }

            let p = OidcAuthProvider.fromConfigWith client None config

            let token =
                OidcFixture.mintRsa RS512 key [ "sub", box "alice"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "Expected Ok for RS512; got Error: %s" e
            | Ok user -> Expect.equal user.UserId "alice" "RS512-signed token validated"
        }

        testCaseAsync "PS256 token validates when whitelist includes PS256"
        <| async {
            // PS256 uses the same RSA key shape as RS256 but PSS padding.
            // Reuses the existing RSA JWKS (PSS / PKCS#1 distinction is
            // signature-side only — the JWK key material is identical).
            let key = OidcFixture.mkKey ()

            let client =
                new HttpClient(new StubHttpHandler(Map.ofList [ key.JwksUrl, OidcFixture.buildJwks key ]))

            let config = {
                Issuer = None
                Audience = None
                KeySource = JwksExplicit key.JwksUrl
                TokenLocation = BearerHeader
                ClockSkewSeconds = None
                AcceptedAlgorithms = Some [ PS256 ]
                PreferOidWhenPresent = None
            }

            let p = OidcAuthProvider.fromConfigWith client None config

            let token =
                OidcFixture.mintRsa PS256 key [ "sub", box "alice"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "Expected Ok for PS256; got Error: %s" e
            | Ok user -> Expect.equal user.UserId "alice" "PS256-signed token validated"
        }

        testCaseAsync "ES256 token validates when whitelist includes ES256 (EC JWKS)"
        <| async {
            // The Cognito / Firebase-shaped path: EC JWKS, ES256
            // signature in IEEE-P1363 form.
            let key = OidcFixture.mkEcKey ()

            let client =
                new HttpClient(new StubHttpHandler(Map.ofList [ key.JwksUrl, OidcFixture.buildEcJwks key ]))

            let config = {
                Issuer = None
                Audience = None
                KeySource = JwksExplicit key.JwksUrl
                TokenLocation = BearerHeader
                ClockSkewSeconds = None
                AcceptedAlgorithms = Some [ RS256; ES256 ]
                PreferOidWhenPresent = None
            }

            let p = OidcAuthProvider.fromConfigWith client None config

            let token = OidcFixture.mintEs256 key [ "sub", box "alice"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "Expected Ok for ES256; got Error: %s" e
            | Ok user -> Expect.equal user.UserId "alice" "ES256-signed token validated"
        }

        testCaseAsync "ES256 token rejected when whitelist is [RS256] (operator trust set wins)"
        <| async {
            // The signature itself would verify against the EC JWKS —
            // but the operator's `AcceptedAlgorithms = Some [RS256]`
            // explicitly excludes ES256. Reject with
            // `UnsupportedAlgorithm "ES256"`, not `InvalidSignature`,
            // because the rejection is policy-driven, not crypto-
            // driven.
            let key = OidcFixture.mkEcKey ()

            let client =
                new HttpClient(new StubHttpHandler(Map.ofList [ key.JwksUrl, OidcFixture.buildEcJwks key ]))

            let config = {
                Issuer = None
                Audience = None
                KeySource = JwksExplicit key.JwksUrl
                TokenLocation = BearerHeader
                ClockSkewSeconds = None
                AcceptedAlgorithms = Some [ RS256 ]
                PreferOidWhenPresent = None
            }

            let p = OidcAuthProvider.fromConfigWith client None config

            let token = OidcFixture.mintEs256 key [ "sub", box "alice"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Whitelist [RS256] should reject ES256 even with valid EC signature"
            | Error e ->
                Expect.stringContains
                    e
                    "ES256"
                    "Error message should name the rejected algorithm so the operator can widen the whitelist if intentional"
        }

        // ─── Phase 341 — azp multi-audience binding (RFC 8725 §3.9) ──

        testCaseAsync "Rejects a multi-audience token without a matching azp (RFC 8725 §3.9)"
        <| async {
            // aud carries THIS app plus a second party; without azp the
            // token was never disambiguated to this app, so it must not
            // validate even though the expected audience is a member.
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key None (Some "my-app")

            let token =
                OidcFixture.mintRs256 key [
                    "sub", box "alice"
                    "aud", box [| "my-app"; "attacker-app" |]
                    "exp", futureExp ()
                ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "multi-audience token without azp must be rejected"
            | Error _ -> ()
        }

        testCaseAsync "Accepts a multi-audience token whose azp matches the expected audience"
        <| async {
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key None (Some "my-app")

            let token =
                OidcFixture.mintRs256 key [
                    "sub", box "alice"
                    "aud", box [| "my-app"; "other-app" |]
                    "azp", box "my-app"
                    "exp", futureExp ()
                ]

            match! p.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "matching azp should validate; got Error: %s" e
            | Ok user -> Expect.equal user.UserId "alice" "multi-aud + matching azp accepted"
        }

        testCaseAsync "Rejects a multi-audience token whose azp names a different party"
        <| async {
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key None (Some "my-app")

            let token =
                OidcFixture.mintRs256 key [
                    "sub", box "alice"
                    "aud", box [| "my-app"; "attacker-app" |]
                    "azp", box "attacker-app"
                    "exp", futureExp ()
                ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "azp naming a different party must be rejected"
            | Error _ -> ()
        }

        testCaseAsync "Single-audience token is unaffected by the azp rule (no azp required)"
        <| async {
            // Backward-compat (GP 11): the azp binding only applies when
            // aud carries MORE THAN ONE entry. A single-audience token
            // with no azp continues to validate exactly as before.
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key None (Some "my-app")

            let token =
                OidcFixture.mintRs256 key [ "sub", box "alice"; "aud", box "my-app"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "single-audience token should validate without azp; got Error: %s" e
            | Ok user -> Expect.equal user.UserId "alice" "single-aud unchanged"
        }

        // ─── Phase 341 — iat-based maximum token age ─────────────────

        testCaseAsync "Rejects a token older than the configured max age"
        <| async {
            let key = OidcFixture.mkKey ()

            let client =
                new HttpClient(new StubHttpHandler(Map.ofList [ key.JwksUrl, OidcFixture.buildJwks key ]))

            let config = {
                Issuer = None
                Audience = None
                KeySource = JwksExplicit key.JwksUrl
                TokenLocation = BearerHeader
                ClockSkewSeconds = None
                AcceptedAlgorithms = None
                PreferOidWhenPresent = None
            }

            let hardening = {
                OidcAuthProvider.OidcHardening.defaults with
                    MaxTokenAgeSeconds = Some 300L
            }

            let p = OidcAuthProvider.fromConfigWithHardened client None hardening config

            // iat two hours ago, exp still in the future — expiry is fine,
            // but the absolute age exceeds the 300s bound.
            let oldIat = DateTimeOffset.UtcNow.AddHours(-2.0).ToUnixTimeSeconds() |> box

            let token =
                OidcFixture.mintRs256 key [ "sub", box "alice"; "iat", oldIat; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "token older than the configured max age must be rejected"
            | Error _ -> ()
        }

        testCaseAsync "Accepts a fresh token within the configured max age"
        <| async {
            let key = OidcFixture.mkKey ()

            let client =
                new HttpClient(new StubHttpHandler(Map.ofList [ key.JwksUrl, OidcFixture.buildJwks key ]))

            let config = {
                Issuer = None
                Audience = None
                KeySource = JwksExplicit key.JwksUrl
                TokenLocation = BearerHeader
                ClockSkewSeconds = None
                AcceptedAlgorithms = None
                PreferOidWhenPresent = None
            }

            let hardening = {
                OidcAuthProvider.OidcHardening.defaults with
                    MaxTokenAgeSeconds = Some 300L
            }

            let p = OidcAuthProvider.fromConfigWithHardened client None hardening config
            let freshIat = DateTimeOffset.UtcNow.ToUnixTimeSeconds() |> box

            let token =
                OidcFixture.mintRs256 key [ "sub", box "alice"; "iat", freshIat; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "fresh token within max age should validate; got Error: %s" e
            | Ok user -> Expect.equal user.UserId "alice" "fresh token accepted"
        }

        testCaseAsync "Rejects a token with no iat when a max age is configured"
        <| async {
            let key = OidcFixture.mkKey ()

            let client =
                new HttpClient(new StubHttpHandler(Map.ofList [ key.JwksUrl, OidcFixture.buildJwks key ]))

            let config = {
                Issuer = None
                Audience = None
                KeySource = JwksExplicit key.JwksUrl
                TokenLocation = BearerHeader
                ClockSkewSeconds = None
                AcceptedAlgorithms = None
                PreferOidWhenPresent = None
            }

            let hardening = {
                OidcAuthProvider.OidcHardening.defaults with
                    MaxTokenAgeSeconds = Some 300L
            }

            let p = OidcAuthProvider.fromConfigWithHardened client None hardening config
            // No iat minted — the age bound cannot be honoured, so reject.
            let token = OidcFixture.mintRs256 key [ "sub", box "alice"; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "max-age configured but no iat must be rejected"
            | Error _ -> ()
        }

        testCaseAsync "Default provider ignores iat when no max age is configured (GP 11)"
        <| async {
            // A very old iat is harmless without a configured bound — the
            // default provider preserves prior behaviour byte-for-byte.
            let key = OidcFixture.mkKey ()
            let p = mkProviderExplicit key None None
            let oldIat = DateTimeOffset.UtcNow.AddDays(-30.0).ToUnixTimeSeconds() |> box

            let token =
                OidcFixture.mintRs256 key [ "sub", box "alice"; "iat", oldIat; "exp", futureExp () ]

            match! p.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "old iat with no max age should validate; got Error: %s" e
            | Ok user -> Expect.equal user.UserId "alice" "iat ignored without a max-age bound"
        }
    ]

// ─── OidcAuthProvider — metrics emission (Phase 9e.A) ───────────────
//
// Confirms the per-instance `IMetricsSink` wiring lands the canonical
// `toolup.auth.validate.*` counters with the `provider=oidc` tag, and
// that the no-metrics constructor path is a no-op (no module-level
// state side effects between fixtures — the prior `setMetricsSink`
// pattern carried that hazard).

let private oidcMetricsTests =
    let mkClient (routes: (string * string) list) : HttpClient =
        new HttpClient(new StubHttpHandler(Map.ofList routes))

    let mkConfig (key: OidcFixture.IssuerKey) : AuthConfig = {
        Issuer = None
        Audience = None
        KeySource = JwksExplicit key.JwksUrl
        TokenLocation = BearerHeader
        ClockSkewSeconds = None
        AcceptedAlgorithms = None
        PreferOidWhenPresent = None
    }

    testList "OidcAuthProvider — metrics emission" [
        testCaseAsync "Successful validation increments validate.success with provider=oidc"
        <| async {
            let key = OidcFixture.mkKey ()
            let client = mkClient [ key.JwksUrl, OidcFixture.buildJwks key ]
            let sink = RecordingMetricsSink()

            let provider =
                OidcAuthProvider.fromConfigWithMetrics client None (Some(sink :> IMetricsSink)) (mkConfig key)

            let token = OidcFixture.mintRs256 key [ "sub", box "alice"; "exp", futureExp () ]

            match! provider.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "Expected Ok; got Error: %s" e
            | Ok _ ->
                let entries = sink.Increments |> Seq.toList

                Expect.equal entries.Length 1 "Exactly one increment expected for a single successful validation"

                let counter, tags = entries[0]
                Expect.equal counter AuthMetrics.ValidateSuccess "Success counter name"

                Expect.equal (Map.tryFind AuthMetrics.ProviderTag tags) (Some "oidc") "Provider tag identifies oidc"
        }

        testCaseAsync "Missing token increments validate.no_token"
        <| async {
            let key = OidcFixture.mkKey ()
            let client = mkClient [ key.JwksUrl, OidcFixture.buildJwks key ]
            let sink = RecordingMetricsSink()

            let provider =
                OidcAuthProvider.fromConfigWithMetrics client None (Some(sink :> IMetricsSink)) (mkConfig key)

            match! provider.ValidateRequest(mkContext () |> toReq) with
            | Ok _ -> failtest "Expected Error for missing bearer token"
            | Error _ ->
                let counters = sink.Increments |> Seq.map fst |> Seq.toList

                Expect.contains counters AuthMetrics.ValidateNoToken "no_token counter emitted"
        }

        testCaseAsync "Expired token increments validate.expired"
        <| async {
            let key = OidcFixture.mkKey ()
            let client = mkClient [ key.JwksUrl, OidcFixture.buildJwks key ]
            let sink = RecordingMetricsSink()

            let provider =
                OidcAuthProvider.fromConfigWithMetrics client None (Some(sink :> IMetricsSink)) (mkConfig key)

            let token = OidcFixture.mintRs256 key [ "sub", box "alice"; "exp", pastExp () ]

            match! provider.ValidateRequest(bearerCtx token) with
            | Ok _ -> failtest "Expected Error for expired token"
            | Error _ ->
                let counters = sink.Increments |> Seq.map fst |> Seq.toList

                Expect.contains counters AuthMetrics.ValidateExpired "expired counter emitted"
        }

        testCaseAsync "Provider built without a sink elides emission and does not throw"
        <| async {
            // Constructed via the legacy `fromConfigWith` (no metrics)
            // — the validation path must complete identically, including
            // the same set of outcomes a metered provider would emit.
            // The absence of a sink is signalled by the provider running
            // to completion without any module-level state being touched
            // (the prior `setMetricsSink` setter pattern is retired).
            let key = OidcFixture.mkKey ()
            let client = mkClient [ key.JwksUrl, OidcFixture.buildJwks key ]
            let provider = OidcAuthProvider.fromConfigWith client None (mkConfig key)

            let token = OidcFixture.mintRs256 key [ "sub", box "alice"; "exp", futureExp () ]

            match! provider.ValidateRequest(bearerCtx token) with
            | Error e -> failtestf "Expected Ok; got Error: %s" e
            | Ok user -> Expect.equal user.UserId "alice" "validation succeeds without a sink"
        }

        testCaseAsync "Two provider instances bind independent sinks (no cross-instance pollution)"
        <| async {
            // The setter pattern leaked sinks across providers via the
            // module-level `metricsSink`. Per-instance binding under
            // 9e.A removes that hazard — assert it directly: a
            // validation against provider A's sink does not surface on
            // provider B's sink.
            let key = OidcFixture.mkKey ()
            let client = mkClient [ key.JwksUrl, OidcFixture.buildJwks key ]
            let sinkA = RecordingMetricsSink()
            let sinkB = RecordingMetricsSink()

            let providerA =
                OidcAuthProvider.fromConfigWithMetrics client None (Some(sinkA :> IMetricsSink)) (mkConfig key)

            let providerB =
                OidcAuthProvider.fromConfigWithMetrics client None (Some(sinkB :> IMetricsSink)) (mkConfig key)

            let token = OidcFixture.mintRs256 key [ "sub", box "alice"; "exp", futureExp () ]

            let! _ = providerA.ValidateRequest(bearerCtx token)

            Expect.equal (Seq.length sinkA.Increments) 1 "A sink saw provider A's increment"
            Expect.equal (Seq.length sinkB.Increments) 0 "B sink saw no leakage from provider A"
        }
    ]

// ─── OidcAuthProvider — IAuthProvider contract via mock issuer ──────
//
// Same provider as `oidcTests`, but driven through the reusable
// `IAuthProviderContract` pack over a *real* Kestrel OIDC issuer +
// genuine OIDC discovery (no `StubHttpMessageHandler`). This adds
// real-socket / real-discovery fidelity and is the deferred Phase 3b
// "local OIDC mock issuer for e2e testing" deliverable.
//
// `lazy` + `testSequenced` boot the issuer once for the whole pack;
// the trailing teardown case stops Kestrel after the contract cases
// (sequenced lists run in declaration order).

let private oidcMockIssuerContract =
    let server = lazy (MockOidcServer.start MockOidcConfig.defaults)

    let fixture: IAuthProviderContract.AuthProviderContractFixture =
        let s = server.Value

        let config = {
            Issuer = Some s.IssuerUrl
            Audience = None
            KeySource = JwksDiscovery s.IssuerUrl
            TokenLocation = BearerHeader
            ClockSkewSeconds = None
            AcceptedAlgorithms = None
            PreferOidWhenPresent = None
        }

        let provider = OidcAuthProvider.fromConfigWith (new HttpClient()) None config

        {
            Name = "OidcAuthProvider via MockOidcServer"
            Provider = provider
            ValidCtx = fun () -> bearerCtx (s.MintAccessToken())
            ExpectedUserId = MockOidcConfig.defaults.Subject
            ExpiredCtx = fun () -> bearerCtx (s.MintExpiredToken())
            EmptyCtx = fun () -> mkContext () |> toReq
        }

    testSequenced (
        testList "OidcAuthProvider (mock issuer)" [
            IAuthProviderContract.tests fixture
            testCase "teardown: stop mock issuer"
            <| fun () ->
                if server.IsValueCreated then
                    (server.Value :> IDisposable).Dispose()
        ]
    )

// ─── OIDC construction guards (Gap audit 2026-06-12 Auth G3) ────────
//
// Cleartext discovery / JWKS URLs are refused at construction — a MITM
// on an http fetch substitutes the key set and forged tokens validate.
// Loopback http is the dev escape hatch (local mock IdP without TLS).

let private oidcConstructionTests =
    let mkConfig (keySource: KeySource) = {
        Issuer = None
        Audience = None
        KeySource = keySource
        TokenLocation = BearerHeader
        ClockSkewSeconds = None
        AcceptedAlgorithms = None
        PreferOidWhenPresent = None
    }

    testList "OidcAuthProvider construction" [
        testCase "refuses an http non-loopback issuer"
        <| fun () ->
            Expect.throwsT<ArgumentException>
                (fun () ->
                    OidcAuthProvider.fromConfig None (mkConfig (JwksDiscovery "http://idp.example.com"))
                    |> ignore)
                "cleartext issuer must be refused at construction"

        testCase "refuses an http non-loopback explicit JWKS URL"
        <| fun () ->
            Expect.throwsT<ArgumentException>
                (fun () ->
                    OidcAuthProvider.fromConfig None (mkConfig (JwksExplicit "http://idp.example.com/jwks.json"))
                    |> ignore)
                "cleartext JWKS URL must be refused at construction"

        testCase "permits an http loopback issuer (dev escape hatch)"
        <| fun () ->
            OidcAuthProvider.fromConfig None (mkConfig (JwksDiscovery "http://127.0.0.1:54321"))
            |> ignore

        testCase "permits an https issuer"
        <| fun () ->
            OidcAuthProvider.fromConfig None (mkConfig (JwksDiscovery "https://idp.example.com"))
            |> ignore
    ]

// ─── AuthProvider.fromEnv dispatch (Gap audit 2026-06-12 Auth G6) ────
//
// Explicit misconfiguration refuses at startup; only genuinely-unset
// mode gets the dev-only HeaderAuthProvider fallback (GP 11). Env vars
// are process-global, so the list is sequenced and each case snapshots
// + restores what it touches.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private fromEnvTests =
    let withEnv (pairs: (string * string option) list) (body: unit -> unit) =
        let priors =
            pairs |> List.map (fun (n, _) -> n, Environment.GetEnvironmentVariable n)

        try
            for n, v in pairs do
                Environment.SetEnvironmentVariable(n, v |> Option.toObj)

            body ()
        finally
            for n, prior in priors do
                Environment.SetEnvironmentVariable(n, prior)

    let marker =
        { new IAuthProvider with
            member _.GetUser _ = async { return AuthenticatedUser.anonymous }
            member _.ValidateRequest _ = async { return Error "marker" }
            member _.IsCryptographicallyVerified = true
        }

    let oidcStub: AuthProvider.OidcAuthBuilder = fun _ _ -> marker

    testSequenced (
        testList "AuthProvider.fromEnv" [
            testCase "unset TOOLUP_AUTH_MODE falls back to the dev HeaderAuthProvider"
            <| fun () ->
                withEnv [ "TOOLUP_AUTH_MODE", None; "TOOLUP_OIDC_ISSUER", None ] (fun () ->
                    let p = AuthProvider.fromEnv silentLogger oidcStub
                    Expect.isTrue (p :? HeaderAuthProvider.HeaderAuthProvider) "unset mode keeps the dev fallback")

            testCase "oidc mode with an issuer dispatches to the OIDC builder"
            <| fun () ->
                withEnv
                    [
                        "TOOLUP_AUTH_MODE", Some "oidc"
                        "TOOLUP_OIDC_ISSUER", Some "https://idp.example.com"
                    ]
                    (fun () ->
                        let p = AuthProvider.fromEnv silentLogger oidcStub
                        Expect.isTrue (obj.ReferenceEquals(p, marker)) "oidc branch builds via the supplied builder")

            testCase "oidc mode without an issuer refuses startup"
            <| fun () ->
                withEnv [ "TOOLUP_AUTH_MODE", Some "oidc"; "TOOLUP_OIDC_ISSUER", None ] (fun () ->
                    Expect.throwsT<InvalidOperationException>
                        (fun () -> AuthProvider.fromEnv silentLogger oidcStub |> ignore)
                        "explicit OIDC intent must never degrade to header trust")

            testCase "unrecognised TOOLUP_AUTH_MODE refuses startup"
            <| fun () ->
                withEnv [ "TOOLUP_AUTH_MODE", Some "oidcc"; "TOOLUP_OIDC_ISSUER", None ] (fun () ->
                    Expect.throwsT<InvalidOperationException>
                        (fun () -> AuthProvider.fromEnv silentLogger oidcStub |> ignore)
                        "a typo'd mode must refuse rather than boot in header-trust")
        ]
    )

// ─── Phase 341 — strict fail-closed on stale JWKS ────────────────────
//
// Drives the internal `OidcAuthProviderJwks.getJwksCore` directly (via
// InternalsVisibleTo) so the time-gated stale window is deterministic:
// seed the process cache with a successful fetch, then force a re-fetch
// (an already-elapsed ttl) against a failing client. Default mode serves
// the stale cache; strict mode (fail-closed) surfaces the fetch error.
//
// Phase 463 note: the forcing device here used to be `TimeSpan.Zero`, and
// is now a one-tick ttl. Zero no longer means "already expired" — it means
// the JWKS cache is DISABLED, which by design serves nothing at all,
// stale fallback included (see `OidcJwksTtlTests`). A one-tick ttl is
// what this case always meant: expired, but still a cache.

let private oidcStaleJwksTests =
    testList "OidcAuthProvider — strict JWKS fail-closed (Phase 341)" [
        testCaseAsync "default serves stale cached keys on refresh failure; strict mode fails closed"
        <| async {
            let key = OidcFixture.mkKey ()

            let okClient =
                new HttpClient(new StubHttpHandler(Map.ofList [ key.JwksUrl, OidcFixture.buildJwks key ]))

            // Empty route map → every fetch 404s (JwksUnavailable).
            let failClient = new HttpClient(new StubHttpHandler(Map.empty))

            let tenMin = TimeSpan.FromMinutes 10.0
            let oneMin = TimeSpan.FromMinutes 1.0
            // Expired for any entry seeded a moment ago, but still an
            // ENABLED cache — see the Phase 463 note above.
            let elapsed = TimeSpan.FromTicks 1L

            // 1. Seed the process cache with a successful fetch.
            let! seeded = OidcAuthProviderJwks.getJwksCore okClient silentLogger key.JwksUrl false false tenMin oneMin

            // 2. Default (failClosed=false): an elapsed ttl forces a re-fetch
            //    that fails → the stale fallback serves the seeded key set.
            let! staleDefault =
                OidcAuthProviderJwks.getJwksCore failClient silentLogger key.JwksUrl false false elapsed oneMin

            // 3. Strict (failClosed=true): same stale window → Error, no serve.
            let! staleStrict =
                OidcAuthProviderJwks.getJwksCore failClient silentLogger key.JwksUrl false true elapsed oneMin

            match seeded with
            | Ok keys -> Expect.equal keys.Count 1 "seed fetch returns the one JWKS key"
            | Error e -> failtestf "seed fetch should succeed; got %A" e

            match staleDefault with
            | Ok keys -> Expect.equal keys.Count 1 "default mode serves the stale cached key set"
            | Error e -> failtestf "default mode should serve stale keys on fetch failure; got %A" e

            match staleStrict with
            | Ok _ -> failtest "strict mode must fail closed on a stale-key window, not serve cached keys"
            | Error _ -> ()
        }
    ]

// ─── Phase 341 — OidcAuthValidator audience-none preflight warning ───
//
// A reachable issuer (MockOidcServer) is required so the reachability
// probe succeeds and the audience-none advisory can surface as a
// `Warning` (a probe failure would be the more-severe `Error`).

let private oidcAudiencePreflightTests =
    let server = lazy (MockOidcServer.start MockOidcConfig.defaults)

    testSequenced (
        testList "OidcAuthValidator audience preflight (Phase 341)" [
            testCaseAsync "warns when audience is unset (aud check would be skipped)"
            <| async {
                let s = server.Value
                let v = OidcAuthValidator.createWithAudience s.IssuerUrl None

                match! v.Validate() with
                | ConfigValidation.Warning msg ->
                    Expect.stringContains msg "audience" "warning names the unbound audience"
                | other -> failtestf "expected a Warning for unset audience; got %A" other
            }

            testCaseAsync "ok when an audience is configured"
            <| async {
                let s = server.Value
                let v = OidcAuthValidator.createWithAudience s.IssuerUrl (Some "my-app")

                match! v.Validate() with
                | ConfigValidation.Ok -> ()
                | other -> failtestf "expected Ok with a configured audience; got %A" other
            }

            testCase "teardown: stop mock issuer"
            <| fun () ->
                if server.IsValueCreated then
                    (server.Value :> IDisposable).Dispose()
        ]
    )

// ─── Aggregated ─────────────────────────────────────────────────────

let tests =
    testList "AuthProviders" [
        headerAuthTests
        staticJwtTests
        oidcTests
        oidcConstructionTests
        fromEnvTests
        oidcMetricsTests
        oidcMockIssuerContract
        oidcStaleJwksTests
        oidcAudiencePreflightTests
    ]