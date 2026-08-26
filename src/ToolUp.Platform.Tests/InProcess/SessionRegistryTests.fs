module ToolUp.Platform.Tests.InProcess.SessionRegistryTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.SessionRegistry
open ToolUp.Platform.Tests.Contracts

// ─── Phase 528 — ISessionRegistry in-process bindings ────────────────
//
// Binds the portable `ISessionRegistryContract` pack against the shipped
// `BlobBackedSessionRegistry` over the hermetic `InMemoryBlobStorage`
// double, plus the tests the portable pack cannot express because they
// are about THIS implementation:
//
//   * the session-id derivation (`SessionIdentity`) — stability,
//     one-wayness, and the two properties the phase's "never a new
//     client-visible credential" clause rests on;
//   * retention filtering, which is a blob-backed-store choice rather
//     than an interface contract;
//   * the path-traversal refusal at the scope seam.

let private freshScopes () =
    let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
    "team-a-" + suffix, "team-b-" + suffix

// ─── Contract-pack binding ───────────────────────────────────────────

let tests =
    let factory () =
        let blobs = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
        let registry = BlobBackedSessionRegistry.create blobs None 30
        let scopeA, scopeB = freshScopes ()
        registry, scopeA, scopeB

    ISessionRegistryContract.tests "BlobBackedSessionRegistry" factory

// ─── Session-id derivation ───────────────────────────────────────────

/// A minimal unsigned JWT carrying `jti`. Never validated — the
/// derivation reads a claim for STABILITY, and validation is
/// `IAuthProvider`'s job (see `SessionIdentity.jtiOf`'s doc for why
/// doing it twice would be worse than doing it once).
let private jwtWith (jti: string) =
    let b64 (s: string) =
        Base64Url.encode (Text.Encoding.UTF8.GetBytes s)

    b64 """{"alg":"none"}"""
    + "."
    + b64 $"""{{"sub":"u","jti":"{jti}"}}"""
    + "."
    + b64 "sig"

let derivationTests =
    testList "Phase 528 — SessionIdentity derivation" [

        test "The same credential derives the same id" {
            let a = SessionIdentity.ofCredential "user-a" (jwtWith "tok-1")
            let b = SessionIdentity.ofCredential "user-a" (jwtWith "tok-1")

            // Stability across calls is what makes the id usable with no
            // shared state: every instance derives it independently and
            // arrives at the same answer.
            Expect.equal a b "derivation is deterministic"
        }

        test "A different token derives a different id" {
            let a = SessionIdentity.ofCredential "user-a" (jwtWith "tok-1")
            let b = SessionIdentity.ofCredential "user-a" (jwtWith "tok-2")

            // A re-issued token is a different sign-in and must be
            // separately revocable — otherwise "sign out that device"
            // would follow the user to their next login.
            Expect.notEqual a b "a re-issued token is a distinct session"
        }

        test "The same token for a different user derives a different id" {
            let a = SessionIdentity.ofCredential "user-a" (jwtWith "tok-1")
            let b = SessionIdentity.ofCredential "user-b" (jwtWith "tok-1")

            Expect.notEqual a b "the user id is mixed into the derivation"
        }

        test "A non-JWT credential still derives stably" {
            let a = SessionIdentity.ofCredential "user-a" "an-opaque-opaque-token"
            let b = SessionIdentity.ofCredential "user-a" "an-opaque-opaque-token"
            let c = SessionIdentity.ofCredential "user-a" "a-different-opaque-token"

            Expect.equal a b "opaque credentials derive stably"
            Expect.notEqual a c "distinct opaque credentials derive distinctly"
        }

        test "The derived id does not contain the credential" {
            let token = jwtWith "tok-secret-value"
            let derived = SessionIdentity.ofCredential "user-a" token

            // The whole reason the id can be listed back to a user and
            // accepted in a revoke call: naming it grants nothing.
            Expect.isFalse (derived.Contains "tok-secret-value") "the jti does not survive into the id"
            Expect.isFalse (derived.Contains token) "the credential does not survive into the id"
            Expect.equal derived.Length 64 "the id is a SHA-256 hex digest"
        }

        test "An anonymous session's id is the hash, not the sealed id itself" {
            let sealedId = "6f1b0c2e-0000-4000-8000-000000000001"
            let derived = SessionIdentity.ofAnonymousSession sealedId

            // The Phase 337 sealed id IS the anonymous storage-scope id
            // and rides `X-User-Id` in plaintext. Using it verbatim would
            // make the session record addressable by a value any observer
            // of the request has already seen.
            Expect.notEqual derived sealedId "the sealed id is not used verbatim"
            Expect.isFalse (derived.Contains sealedId) "the sealed id does not survive into the derived id"
        }

        test "ofSubject derives for an anonymous subject with no credential" {
            let derived = SessionIdentity.ofSubject (AnonymousSession "sealed-1") None

            Expect.equal
                derived
                (Some(SessionIdentity.ofAnonymousSession "sealed-1"))
                "an anonymous subject derives from its Phase 337 sealed id"
        }

        test "ofSubject declines an authenticated subject with no bearer credential" {
            let derived = SessionIdentity.ofSubject (Subject.AuthenticatedUser "user-a") None

            // A header-auth deployment presents no token. Inventing a
            // per-request id would fill the store with single-use rows
            // nobody can act on, which is worse than recording nothing.
            Expect.isNone derived "no credential means no derivable session"
        }

        test "ofSubject declines a ClaimBearer subject" {
            let claim: ShareTokenClaim = {
                TokenId = "t1"
                ScopeId = "team-a"
                ResourceKind = "form"
                ResourceId = "f1"
                IssuedBy = "user-a"
                IssuedAt = DateTimeOffset.UtcNow
                ExpiresAt = DateTimeOffset.UtcNow.AddDays 1.0
                UseLimit = None
                UsedCount = 0
                Revoked = false
                AttributedHandle = None
                RateLimit = None
            }

            // A share-token claim already has its own revocation path
            // (`IShareTokenStore.Revoke`); recording it here would create
            // a second, divergent place to revoke the same thing.
            Expect.isNone (SessionIdentity.ofSubject (Subject.ClaimBearer claim) (Some "tok")) "claims are not sessions"
        }
    ]

// ─── Blob-backed specifics ───────────────────────────────────────────

let blobBackedTests =
    testList "Phase 528 — BlobBackedSessionRegistry specifics" [

        testCaseAsync "A record past its retention window stops being listed"
        <| async {
            let blobs = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let now = DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)

            // Frozen clock: this assertion is about a retention BOUNDARY,
            // and a wall-clock version of it would be a time bomb (it
            // would start failing on whatever date the fixture aged past).
            let registry =
                BlobBackedSessionRegistry(blobs, None, 30, (fun () -> now)) :> ISessionRegistry

            let stale: SessionRecord = {
                SessionId = ISessionRegistryContract.sessionId "old"
                UserId = "user-a"
                ScopeId = "team-a"
                DeviceDescriptor = "Old Browser"
                AuthProvider = "TestAuthProvider"
                CreatedAt = now.AddDays -100.0
                LastSeenAt = now.AddDays -31.0
                Status = ActiveSession
                RevokedAt = None
                RevokedBy = None
            }

            let fresh = {
                stale with
                    SessionId = ISessionRegistryContract.sessionId "new"
                    LastSeenAt = now.AddDays -1.0
            }

            let! _ = registry.Record stale
            let! _ = registry.Record fresh

            let! listed = registry.ListForUser("team-a", "user-a")

            match listed with
            | Error e -> failtestf "ListForUser failed: %A" e
            | Ok sessions ->
                Expect.hasLength sessions 1 "only the in-retention session is listed"

                Expect.equal
                    sessions[0].SessionId
                    (ISessionRegistryContract.sessionId "new")
                    "the fresh session is the one kept"
        }

        testCaseAsync "A traversal scope id is refused rather than reaching a chosen path"
        <| async {
            let blobs = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let registry = BlobBackedSessionRegistry.create blobs None 30

            // Without the sanitiser this read would enumerate a chosen
            // `_platform/...` prefix — the same cross-scope sink Phase 131
            // closed at the team / permission / share-token seams.
            let! listed = registry.ListForUser("../../jobs", "user-a")

            match listed with
            | Error(SessionError.AccessDenied _) -> ()
            | other -> failtestf "expected AccessDenied for a traversal scope id, got %A" other
        }

        testCaseAsync "A session id that is not a derived digest is refused on Record"
        <| async {
            let blobs = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let registry = BlobBackedSessionRegistry.create blobs None 30

            let bogus: SessionRecord = {
                SessionId = "../../../etc/passwd"
                UserId = "user-a"
                ScopeId = "team-a"
                DeviceDescriptor = "Test"
                AuthProvider = "TestAuthProvider"
                CreatedAt = DateTimeOffset.UtcNow
                LastSeenAt = DateTimeOffset.UtcNow
                Status = ActiveSession
                RevokedAt = None
                RevokedBy = None
            }

            let! recorded = registry.Record bogus

            match recorded with
            | Error(SessionError.AccessDenied _) -> ()
            | other -> failtestf "expected AccessDenied for a non-derived session id, got %A" other
        }
    ]