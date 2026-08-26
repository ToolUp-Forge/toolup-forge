module ToolUp.Platform.Tests.Contracts.ISessionRegistryContract

open System
open Expecto
open ToolUp.Platform

// ─── ISessionRegistry contract pack (Phase 528) ──────────────────────
//
// Parametrised tests for any `ISessionRegistry` implementation. The
// factory returns a fresh registry plus two distinct scope ids, so
// per-scope-isolation assertions have non-colliding scopes and concurrent
// runs against shared substrate cannot interfere.
//
// Coverage follows the phase's named set — record / list / revoke /
// isolation / staleness-bound:
//
//   * **Record** round-trips, and is idempotent WITHOUT clobbering: a
//     returning credential keeps its original `CreatedAt` and does not
//     resurrect a revoked record. Both halves matter and neither is
//     obvious from the signature.
//   * **List** returns only the named user's sessions, active and
//     revoked alike.
//   * **Revoke** flips one session, is idempotent, preserves the FIRST
//     revocation's actor and timestamp, and reports `NotFound` for an
//     unknown id.
//   * **RevokeAllForUser** revokes every active session for one user,
//     returns the count that actually moved (so a repeat returns 0), and
//     leaves other users alone.
//   * **Isolation** (GP 4) — the same user id in two scopes has two
//     independent session sets; a revoke in one scope does not reach the
//     other, and neither does a list.
//   * **Staleness bound** — `IsRevoked` reflects a `Revoke` on the very
//     next call. The store carries NO staleness of its own; the
//     documented revocation window
//     (`SessionRegistryOptions.RevocationCacheSeconds`) is entirely the
//     CALLER's cache. An implementation that answered stale here would
//     make that window unbounded and unknowable, which is the one
//     property the phase's acceptance criterion turns on.
//   * **IsRevoked fails open** — an unknown session id answers `false`,
//     never `true`. The registry is a revocation list consulted after the
//     credential has already been validated; answering `true` on a miss
//     would convert an empty store into a fleet-wide sign-out.
//
// A session id must be a derived 64-char lowercase hex string (see
// `SessionIdentity` in the server tier). The helper below mints
// conforming ids so the pack tests the contract rather than an
// implementation's id validation.

/// A conforming derived session id, distinct per `seed`. Deterministic so
/// a failure names a stable id.
let sessionId (seed: string) : string =
    use h = System.Security.Cryptography.SHA256.Create()

    h.ComputeHash(Text.Encoding.UTF8.GetBytes seed)
    |> Array.map _.ToString("x2")
    |> String.concat ""

let private baseTime = DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)

let private record (scopeId: string) (userId: string) (seed: string) : SessionRecord = {
    SessionId = sessionId seed
    UserId = userId
    ScopeId = scopeId
    DeviceDescriptor = "Test Browser"
    AuthProvider = "TestAuthProvider"
    CreatedAt = baseTime
    LastSeenAt = baseTime
    Status = ActiveSession
    RevokedAt = None
    RevokedBy = None
}

let private expectOk (label: string) (result: Result<'a, SessionError>) : 'a =
    match result with
    | Ok v -> v
    | Error e -> failtestf "%s failed: %A" label e

let tests (name: string) (factory: unit -> ISessionRegistry * string * string) =

    testList $"{name} — ISessionRegistry contract" [

        // ─── Record ───────────────────────────────────────────────

        testCaseAsync "Record then ListForUser round-trips the session"
        <| async {
            let registry, scope, _ = factory ()

            let! recorded = registry.Record(record scope "user-a" "a1")
            let stored = expectOk "Record" recorded

            Expect.equal stored.UserId "user-a" "user id survives the round-trip"
            Expect.equal stored.DeviceDescriptor "Test Browser" "device descriptor survives"
            Expect.equal stored.AuthProvider "TestAuthProvider" "auth provider survives"
            Expect.equal stored.Status ActiveSession "a fresh record is active"

            let! listed = registry.ListForUser(scope, "user-a")
            let sessions = expectOk "ListForUser" listed

            Expect.hasLength sessions 1 "exactly the one recorded session"
            Expect.equal sessions[0].SessionId (sessionId "a1") "the recorded session id"
        }

        testCaseAsync "Record is idempotent and does not reset CreatedAt"
        <| async {
            let registry, scope, _ = factory ()

            let! _ = registry.Record(record scope "user-a" "a1")

            // The same credential returning later. `CreatedAt` is what
            // answers "signed in since when?" on the session list, so a
            // returning request must not overwrite it — that would make
            // every session look brand new.
            let returning = {
                record scope "user-a" "a1" with
                    CreatedAt = baseTime.AddDays 5.0
                    LastSeenAt = baseTime.AddDays 5.0
            }

            let! second = registry.Record returning
            let stored = expectOk "second Record" second

            Expect.equal stored.CreatedAt baseTime "CreatedAt is the FIRST record's, not the returning one's"

            let! listed = registry.ListForUser(scope, "user-a")
            Expect.hasLength (expectOk "ListForUser" listed) 1 "re-recording does not duplicate the session"
        }

        testCaseAsync "Record does not resurrect a revoked session"
        <| async {
            let registry, scope, _ = factory ()

            let! _ = registry.Record(record scope "user-a" "a1")
            let! _ = registry.Revoke(scope, sessionId "a1", "user-a")

            // A revoked credential that keeps being presented must stay
            // revoked. If `Record` overwrote the stored row, every
            // revocation would last exactly until the holder's next
            // request — i.e. the substrate would do nothing at all.
            let! again = registry.Record(record scope "user-a" "a1")
            let stored = expectOk "Record after revoke" again

            Expect.equal stored.Status RevokedSession "the stored record is still revoked"

            let! revoked = registry.IsRevoked(scope, sessionId "a1")
            Expect.isTrue revoked "IsRevoked still reports the session as revoked"
        }

        // ─── List ─────────────────────────────────────────────────

        testCaseAsync "ListForUser returns only the named user's sessions"
        <| async {
            let registry, scope, _ = factory ()

            let! _ = registry.Record(record scope "user-a" "a1")
            let! _ = registry.Record(record scope "user-a" "a2")
            let! _ = registry.Record(record scope "user-b" "b1")

            let! listed = registry.ListForUser(scope, "user-a")
            let sessions = expectOk "ListForUser" listed

            Expect.hasLength sessions 2 "both of user-a's sessions"

            Expect.isTrue
                (sessions |> List.forall (fun s -> s.UserId = "user-a"))
                "no other user's session leaks into the list"
        }

        testCaseAsync "ListForUser includes revoked sessions"
        <| async {
            let registry, scope, _ = factory ()

            let! _ = registry.Record(record scope "user-a" "a1")
            let! _ = registry.Record(record scope "user-a" "a2")
            let! _ = registry.Revoke(scope, sessionId "a1", "user-a")

            let! listed = registry.ListForUser(scope, "user-a")
            let sessions = expectOk "ListForUser" listed

            // Filtering revoked records away would make a revoke look
            // identical to a failed request that reloaded a shorter list.
            Expect.hasLength sessions 2 "the revoked session is still listed"

            Expect.equal
                (sessions |> List.filter SessionRecord.isActive |> List.length)
                1
                "exactly one of them is still active"
        }

        // ─── Revoke ───────────────────────────────────────────────

        testCaseAsync "Revoke flips the session and records the actor"
        <| async {
            let registry, scope, _ = factory ()

            let! _ = registry.Record(record scope "user-a" "a1")

            let! revoked = registry.Revoke(scope, sessionId "a1", "admin-x")
            expectOk "Revoke" revoked

            let! listed = registry.ListForUser(scope, "user-a")
            let stored = (expectOk "ListForUser" listed)[0]

            Expect.equal stored.Status RevokedSession "status flipped"
            Expect.equal stored.RevokedBy (Some "admin-x") "actor recorded"
            Expect.isSome stored.RevokedAt "revocation timestamp recorded"
        }

        testCaseAsync "Revoke is idempotent and preserves the first revocation"
        <| async {
            let registry, scope, _ = factory ()

            let! _ = registry.Record(record scope "user-a" "a1")
            let! _ = registry.Revoke(scope, sessionId "a1", "admin-x")

            let! listed1 = registry.ListForUser(scope, "user-a")
            let first = (expectOk "first list" listed1)[0]

            let! second = registry.Revoke(scope, sessionId "a1", "someone-else")
            expectOk "second Revoke" second

            let! listed2 = registry.ListForUser(scope, "user-a")
            let after = (expectOk "second list" listed2)[0]

            // Overwriting would erase the forensic answer to "who cut
            // this off, and when" — the reason to keep the record at all.
            Expect.equal after.RevokedBy (Some "admin-x") "the FIRST actor is preserved"
            Expect.equal after.RevokedAt first.RevokedAt "the FIRST timestamp is preserved"
        }

        testCaseAsync "Revoke reports NotFound for an unknown session"
        <| async {
            let registry, scope, _ = factory ()

            let! revoked = registry.Revoke(scope, sessionId "never-recorded", "user-a")

            match revoked with
            | Error(SessionError.NotFound _) -> ()
            | other -> failtestf "expected NotFound, got %A" other
        }

        testCaseAsync "RevokeAllForUser revokes every active session and counts them"
        <| async {
            let registry, scope, _ = factory ()

            let! _ = registry.Record(record scope "user-a" "a1")
            let! _ = registry.Record(record scope "user-a" "a2")
            let! _ = registry.Record(record scope "user-b" "b1")

            let! revoked = registry.RevokeAllForUser(scope, "user-a", "user-a")
            Expect.equal (expectOk "RevokeAllForUser" revoked) 2 "both of user-a's sessions moved"

            let! othersSession = registry.IsRevoked(scope, sessionId "b1")
            Expect.isFalse othersSession "user-b's session is untouched"

            // A repeat is a no-op returning 0 — which is what lets the
            // caller report something truthful rather than "done" twice.
            let! again = registry.RevokeAllForUser(scope, "user-a", "user-a")
            Expect.equal (expectOk "second RevokeAllForUser" again) 0 "nothing left to revoke"
        }

        // ─── Isolation (GP 4) ─────────────────────────────────────

        testCaseAsync "The same user id in two scopes keeps independent session sets"
        <| async {
            let registry, scopeA, scopeB = factory ()

            let! _ = registry.Record(record scopeA "user-a" "a1")
            let! _ = registry.Record(record scopeB "user-a" "b1")

            let! inA = registry.ListForUser(scopeA, "user-a")
            let! inB = registry.ListForUser(scopeB, "user-a")

            let sessionsA = expectOk "list in A" inA
            let sessionsB = expectOk "list in B" inB

            Expect.hasLength sessionsA 1 "scope A sees only its own"
            Expect.hasLength sessionsB 1 "scope B sees only its own"

            Expect.equal sessionsA[0].SessionId (sessionId "a1") "scope A's session"
            Expect.equal sessionsB[0].SessionId (sessionId "b1") "scope B's session"
        }

        testCaseAsync "A revoke in one scope does not reach another"
        <| async {
            let registry, scopeA, scopeB = factory ()

            let! _ = registry.Record(record scopeA "user-a" "shared")
            let! _ = registry.Record(record scopeB "user-a" "shared")

            let! _ = registry.RevokeAllForUser(scopeA, "user-a", "user-a")

            let! inA = registry.IsRevoked(scopeA, sessionId "shared")
            let! inB = registry.IsRevoked(scopeB, sessionId "shared")

            Expect.isTrue inA "scope A's session is revoked"
            // Same user id, same session id, different tenant. Without
            // scope in the key this is the cross-tenant leak GP 4 exists
            // to make structurally impossible.
            Expect.isFalse inB "scope B's identically-named session is untouched"
        }

        // ─── Staleness bound ──────────────────────────────────────

        testCaseAsync "IsRevoked reflects a revocation on the very next call"
        <| async {
            let registry, scope, _ = factory ()

            let! _ = registry.Record(record scope "user-a" "a1")

            let! before = registry.IsRevoked(scope, sessionId "a1")
            Expect.isFalse before "an active session is not revoked"

            let! _ = registry.Revoke(scope, sessionId "a1", "user-a")

            // The store carries no staleness of its own. Every documented
            // millisecond of the revocation window belongs to the
            // caller's cache (`SessionRegistryOptions.RevocationCacheSeconds`)
            // — an implementation that cached here would make that window
            // unbounded and unknowable.
            let! after = registry.IsRevoked(scope, sessionId "a1")
            Expect.isTrue after "the revocation is visible immediately"
        }

        testCaseAsync "IsRevoked fails open on an unknown session"
        <| async {
            let registry, scope, _ = factory ()

            let! unknown = registry.IsRevoked(scope, sessionId "never-recorded")

            // Answering `true` on a miss would turn an empty store into a
            // fleet-wide sign-out. The registry is consulted AFTER the
            // credential has been validated, so `false` returns to the
            // pre-registry posture rather than admitting anyone new.
            Expect.isFalse unknown "an unknown session is not treated as revoked"
        }

        // ─── Touch ────────────────────────────────────────────────

        testCaseAsync "Touch never fails a request"
        <| async {
            let registry, scope, _ = factory ()

            let! unknown = registry.Touch(scope, sessionId "never-recorded", baseTime)
            expectOk "Touch on an unknown session" unknown

            let! _ = registry.Record(record scope "user-a" "a1")
            let! _ = registry.Revoke(scope, sessionId "a1", "user-a")

            // A touch is a liveness signal, not an assertion. Failing the
            // request that carried it — for a session that is merely
            // unknown or already revoked — would be absurd.
            let! onRevoked = registry.Touch(scope, sessionId "a1", baseTime.AddHours 1.0)
            expectOk "Touch on a revoked session" onRevoked
        }

        testCaseAsync "Touch advances LastSeenAt past its precision floor"
        <| async {
            let registry, scope, _ = factory ()

            let! _ = registry.Record(record scope "user-a" "a1")

            // Well past minute-grain, so a conformant implementation must
            // record it however coarsely it batches (GP 12 rule 6).
            let later = baseTime.AddHours 2.0
            let! touched = registry.Touch(scope, sessionId "a1", later)
            expectOk "Touch" touched

            let! listed = registry.ListForUser(scope, "user-a")
            let stored = (expectOk "ListForUser" listed)[0]

            Expect.isTrue (stored.LastSeenAt > baseTime) "LastSeenAt advanced"
        }
    ]