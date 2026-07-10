module ToolUp.Platform.Tests.InProcess.PasskeyAuthProviderTests

// ─── Phase 443 — WebAuthn / passkey auth companion tests ─────────────
//
// Covers the four 443.D bullets: ceremony round-trip (registration then
// assertion, driven through a stub `IFido2` so the orchestration —
// persistence, counter update, identity resolution — is exercised
// without hand-authoring real attestation crypto; Fido2NetLib's own
// vectors cover the crypto), signature-counter regression (clone
// detection), invite gating, and challenge expiry. Plus the session-
// token round-trip proving the minted JWT is the same shape
// StaticJwtAuthProvider validates, the config-validator preflight, and
// the blob-backed credential store.
//
// Fido2NetLib types are fully qualified rather than `open`ed: the
// `Fido2NetLib` namespace shadows F#'s `None`, so opening it turns every
// `= None` into a resolution error.

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.Tests.Contracts
open ToolUp.AuthProviders.Passkey.PasskeyTypes
open ToolUp.AuthProviders.Passkey.PasskeyStores
open ToolUp.AuthProviders.Passkey.PasskeyRegistrationPolicy
open ToolUp.AuthProviders.Passkey.PasskeyCeremony
open ToolUp.AuthProviders.Passkey.PasskeySessionToken
open ToolUp.AuthProviders.Passkey.PasskeyConfigValidator

// ─── Fixtures ────────────────────────────────────────────────────────

let private baseConfig =
    PasskeyConfig.create "example.com" "Example" [ "https://example.com" ]

/// A minimal but valid `CredentialCreateOptions` — Fido2 4.x marks
/// Rp / User / Challenge / PubKeyCredParams as `required`, so the empty
/// ctor won't compile. Used as the boxed `Options` in a pending
/// registration challenge; the stub `IFido2` ignores its content.
let private minimalCreateOptions () : Fido2NetLib.CredentialCreateOptions =
    Fido2NetLib.CredentialCreateOptions(
        Rp = Fido2NetLib.PublicKeyCredentialRpEntity("example.com", "Example", ""),
        User = Fido2NetLib.Fido2User(Id = [| 0uy |], Name = "x", DisplayName = "x"),
        Challenge = [| 1uy |],
        PubKeyCredParams = ResizeArray<Fido2NetLib.PubKeyCredParam>()
    )

/// A stub `IFido2` returning canned ceremony results, so the ceremony
/// orchestration can be tested without real WebAuthn crypto.
let private stubFido2
    (regCredential: Fido2NetLib.Objects.RegisteredPublicKeyCredential)
    (assertResult: Fido2NetLib.Objects.VerifyAssertionResult)
    : Fido2NetLib.IFido2 =
    { new Fido2NetLib.IFido2 with
        member _.RequestNewCredential(_) = minimalCreateOptions ()
        member _.MakeNewCredentialAsync(_, _: CancellationToken) = Task.FromResult regCredential
        member _.GetAssertionOptions(_) = Fido2NetLib.AssertionOptions()
        member _.MakeAssertionAsync(_, _: CancellationToken) = Task.FromResult assertResult
    }

let private registeredCredential
    (id: byte[])
    (signCount: uint32)
    (userHandle: byte[])
    : Fido2NetLib.Objects.RegisteredPublicKeyCredential =
    Fido2NetLib.Objects.RegisteredPublicKeyCredential(
        Id = id,
        PublicKey = [| 9uy; 9uy |],
        SignCount = signCount,
        User = Fido2NetLib.Fido2User(Id = userHandle, Name = "alice", DisplayName = "Alice"),
        Transports = [||]
    )

let private assertionResult (id: byte[]) (signCount: uint32) : Fido2NetLib.Objects.VerifyAssertionResult =
    Fido2NetLib.Objects.VerifyAssertionResult(CredentialId = id, SignCount = signCount)

let private blobStore () =
    InMemoryBlobStorage.InMemoryBlobStorage() :> BlobStorage.IBlobStorage

// credential id bytes [1;2;3] → base64url "AQID"
let private credBytes = [| 1uy; 2uy; 3uy |]
let private credId = Base64Url.encode credBytes

let private regPending (userId: string) : PendingChallenge = {
    Kind = Registration
    Options = box (minimalCreateOptions ())
    UserId = Some userId
    UserHandle = Some(userHandleFor userId)
    DisplayName = Some "Alice"
    Email = Some "alice@example.com"
    Username = Some userId
    Grant = Some "Bootstrap"
    ExpiresAt = DateTime.UtcNow.AddMinutes 5.0
}

let private assertPending: PendingChallenge = {
    Kind = Assertion
    Options = box (Fido2NetLib.AssertionOptions())
    UserId = Option<string>.None
    UserHandle = Option<string>.None
    DisplayName = Option<string>.None
    Email = Option<string>.None
    Username = Some "alice"
    Grant = Option<string>.None
    ExpiresAt = DateTime.UtcNow.AddMinutes 5.0
}

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 443 — passkey auth companion" [

        // ── 443.A / 443.D — ceremony round-trip ──
        testCaseAsync "registration persists a credential, assertion resolves the identity + advances the counter"
        <| async {
            let creds = PasskeyCredentialStore(blobStore ())
            let userId = "alice"

            // Register: stub returns a credential id [1;2;3], counter 0.
            let regFido =
                stubFido2
                    (registeredCredential credBytes 0u (Base64Url.decode (userHandleFor userId)))
                    (assertionResult credBytes 1u)

            let! reg = completeRegistration regFido creds (regPending userId) "{}"

            let record =
                match reg with
                | Ok r -> r
                | Error e -> failtestf "registration failed: %s" e

            Expect.equal record.CredentialId credId "credential id persisted (base64url)"
            Expect.equal record.UserId userId "record bound to the enrolling identity"
            Expect.equal record.SignCount 0u "initial sign counter stored"

            let! fetched = creds.TryGet credId
            Expect.isSome fetched "credential is retrievable from the blob store"

            // Assert: stub returns counter 1 (> stored 0) → accepted.
            let! assertion = completeAssertion regFido creds assertPending """{"rawId":"AQID"}"""

            match assertion with
            | Ok r ->
                Expect.equal r.UserId userId "assertion resolved the credential's identity"
                Expect.equal r.SignCount 1u "sign counter advanced to the asserted value"
            | Error e -> failtestf "assertion failed: %s" e
        }

        // ── 443.A / 443.D — clone detection (counter regression) ──
        testCase "isCounterRegression flags a non-advancing counter"
        <| fun _ ->
            Expect.isTrue (isCounterRegression 5u 3u) "5 -> 3 is a regression"
            Expect.isTrue (isCounterRegression 5u 5u) "5 -> 5 is a regression (replay)"
            Expect.isFalse (isCounterRegression 5u 6u) "5 -> 6 advances"
            Expect.isFalse (isCounterRegression 0u 0u) "0 -> 0: authenticator without counters"
            Expect.isFalse (isCounterRegression 0u 4u) "0 -> 4: first counted assertion"

        testCaseAsync "assertion with a regressed counter is rejected"
        <| async {
            let creds = PasskeyCredentialStore(blobStore ())

            // Pre-store a credential already at counter 5.
            let! _ =
                creds.Save {
                    CredentialId = credId
                    PublicKey = Base64Url.encode [| 9uy; 9uy |]
                    SignCount = 5u
                    UserHandle = userHandleFor "alice"
                    UserId = "alice"
                    DisplayName = "Alice"
                    Email = Option<string>.None
                    Transports = []
                    CreatedAt = DateTime.UtcNow
                }

            // Stub returns counter 3 (< stored 5) → clone.
            let fido =
                stubFido2 (registeredCredential credBytes 0u [||]) (assertionResult credBytes 3u)

            let! result = completeAssertion fido creds assertPending """{"rawId":"AQID"}"""

            match result with
            | Ok _ -> failtest "a regressed counter must be rejected"
            | Error e -> Expect.stringContains (e.ToLowerInvariant()) "counter" "rejection cites the counter regression"
        }

        // ── 443.B — invite gating ──
        testCase "registration is invite-gated by default"
        <| fun _ ->
            let cfg = baseConfig // AllowOpenRegistration = false
            Expect.isError (decide cfg false false false) "no session / invite / bootstrap ⇒ refused"

            match decide cfg true false false with
            | Ok ExistingSession -> ()
            | other -> failtestf "an authenticated session should grant ExistingSession, got %A" other

            match decide cfg false true false with
            | Ok Bootstrap -> ()
            | other -> failtestf "a valid bootstrap token should grant Bootstrap, got %A" other

            match decide cfg false false true with
            | Ok PendingInvite -> ()
            | other -> failtestf "a pending invite should grant PendingInvite, got %A" other

        testCase "open registration bypasses the gate when explicitly enabled"
        <| fun _ ->
            let cfg = {
                baseConfig with
                    AllowOpenRegistration = true
            }

            match decide cfg false false false with
            | Ok OpenRegistration -> ()
            | other -> failtestf "AllowOpenRegistration should grant OpenRegistration, got %A" other

        testCase "bootstrap token compares in constant time and rejects a mismatch"
        <| fun _ ->
            let cfg = {
                baseConfig with
                    BootstrapToken = Some "s3cr3t-bootstrap"
            }

            Expect.isTrue (bootstrapMatches cfg (Some "s3cr3t-bootstrap")) "matching token accepted"
            Expect.isFalse (bootstrapMatches cfg (Some "wrong")) "mismatched token rejected"
            Expect.isFalse (bootstrapMatches cfg (Option<string>.None)) "absent token rejected"
            Expect.isFalse (bootstrapMatches baseConfig (Some "anything")) "no configured token ⇒ rejected"

        // ── 443.A / 443.D — challenge store expiry + single-use ──
        testCase "challenge store rejects an expired challenge and consumes on first take"
        <| fun _ ->
            let store = InMemoryPasskeyChallengeStore() :> IPasskeyChallengeStore

            let expired = {
                assertPending with
                    ExpiresAt = DateTime.UtcNow.AddSeconds -1.0
            }

            store.Put("expired", expired)
            Expect.isNone (store.TryTake "expired") "an expired challenge is not returned"

            let live = {
                assertPending with
                    ExpiresAt = DateTime.UtcNow.AddMinutes 2.0
            }

            store.Put("live", live)
            Expect.isSome (store.TryTake "live") "a live challenge is returned"
            Expect.isNone (store.TryTake "live") "a challenge is single-use (consumed on first take)"

        // ── 443.A — session token is the StaticJwt shape ──
        testCaseAsync "minted session token validates through StaticJwtAuthProvider"
        <| async {
            let secret = "passkey-session-signing-secret-0123456789"

            let cfg = {
                baseConfig with
                    Issuer = Some "toolup-passkey"
                    Audience = Some "toolup-app"
            }

            let token =
                mint secret cfg {
                    UserId = "alice"
                    DisplayName = "Alice"
                    Email = Some "alice@example.com"
                }

            let provider =
                StaticJwtAuthProvider.StaticJwtAuthProvider(
                    {
                        Secret = secret
                        Issuer = cfg.Issuer
                        Audience = cfg.Audience
                    }
                )
                :> IAuthProvider

            let ctx = DefaultHttpContext() :> HttpContext
            ctx.Request.Headers["Authorization"] <- StringValues("Bearer " + token)
            let! result = provider.ValidateRequest(RequestContextBuilder.ofHttpContext ctx)

            match result with
            | Ok user ->
                Expect.equal user.UserId "alice" "sub resolves to the passkey identity"
                Expect.equal user.DisplayName "Alice" "name claim carried"
                Expect.equal user.Email (Some "alice@example.com") "email claim carried"
            | Error e -> failtestf "the minted token should validate: %s" e

            // Wrong secret ⇒ rejected (signature integrity).
            let wrongProvider =
                StaticJwtAuthProvider.StaticJwtAuthProvider(
                    {
                        Secret = "a-different-secret"
                        Issuer = cfg.Issuer
                        Audience = cfg.Audience
                    }
                )
                :> IAuthProvider

            let! bad = wrongProvider.ValidateRequest(RequestContextBuilder.ofHttpContext ctx)
            Expect.isError bad "a token signed with a different secret must not validate"
        }

        // ── 443.D — config-validator preflight ──
        testCase "config validator enforces RP/origin match, https, and surfaces open registration"
        <| fun _ ->
            match validateConfig baseConfig with
            | ConfigValidation.ValidationResult.Ok -> ()
            | other -> failtestf "a coherent config should pass, got %A" other

            match
                validateConfig {
                    baseConfig with
                        RelyingPartyId = "elsewhere.org"
                }
            with
            | ConfigValidation.ValidationResult.Error _ -> ()
            | other -> failtestf "an RP id that is not a suffix of the origin must fail, got %A" other

            match
                validateConfig {
                    baseConfig with
                        Origins = [ "http://example.com" ]
                }
            with
            | ConfigValidation.ValidationResult.Error _ -> ()
            | other -> failtestf "a cleartext non-loopback origin must fail, got %A" other

            match
                validateConfig {
                    baseConfig with
                        Origins = [ "http://localhost:5000" ]
                        RelyingPartyId = "localhost"
                }
            with
            | ConfigValidation.ValidationResult.Ok -> ()
            | other -> failtestf "loopback http is exempt, got %A" other

            match
                validateConfig {
                    baseConfig with
                        AllowOpenRegistration = true
                }
            with
            | ConfigValidation.ValidationResult.Warning _ -> ()
            | other -> failtestf "open registration should surface a warning, got %A" other

        // ── credential store round-trip ──
        testCaseAsync "credential store round-trips save / get / list / exists / remove"
        <| async {
            let creds = PasskeyCredentialStore(blobStore ())

            let record: PasskeyCredentialRecord = {
                CredentialId = credId
                PublicKey = Base64Url.encode [| 7uy |]
                SignCount = 2u
                UserHandle = userHandleFor "bob"
                UserId = "bob"
                DisplayName = "Bob"
                Email = Option<string>.None
                Transports = [ "internal" ]
                CreatedAt = DateTime.UtcNow
            }

            let! _ = creds.Save record
            let! exists = creds.Exists credId
            Expect.isTrue exists "saved credential exists"

            let! byUser = creds.ListByUserId "bob"
            Expect.equal byUser.Length 1 "credential listed under its user"
            Expect.isEmpty (byUser |> List.filter (fun r -> r.UserId <> "bob")) "no cross-user leakage"

            let! _ = creds.Remove credId
            let! stillExists = creds.Exists credId
            Expect.isFalse stillExists "removed credential is gone"
        }
    ]