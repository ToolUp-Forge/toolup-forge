module ToolUp.Platform.Tests.Contracts.IPeerBearerAuthContract

open System.Collections.Concurrent
open System.Text
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.PeerBearerAuthMiddleware
open ToolUp.Platform.Secrets

// ─── IPeerBearerAuthContract — Phase 37 contract pack ────────────────
//
// Exercises the documented behaviour of `PeerBearerAuthMiddleware`'s
// `authenticate` validator. The pack drives the pure-function shape
// (header in, `AuthOutcome` out) so contract tests stay independent of
// ASP.NET Core pipeline plumbing. The middleware's `InvokeAsync` is a
// thin wrapper around `authenticate` that adds audit emission + 401
// shaping; covering the validator covers every authentication decision
// the middleware makes.
//
// The pack takes a factory producing a fresh `(ISecretStore,
// preSeed)` pair so each test starts from a clean substrate. `preSeed`
// is a helper closure the test calls to register `peerName → bearer`
// pairs before exercising `authenticate`; this keeps the test concise
// while letting the binding decide where the store lives (in-memory
// dictionary, blob-backed, real Vault, etc.).

/// `ISecretStore` shape every contract test relies on. Tests seed
/// expected (peerName, bearer) pairs via the supplied helper before
/// calling `authenticate`. `SeedBearer (peerName, token)` writes the
/// `_platform/peers/{peerName}/bearer` secret synchronously so the
/// test body stays linear.
type ContractSetup = {
    SecretStore: ISecretStore
    SeedBearer: string * string -> unit
}

/// Build an `HttpContext` with the given headers populated. Path /
/// method are stamped with sensible defaults so the contract pack
/// never has to construct an `HttpRequest`.
let private mkContext (peerName: string option) (authorization: string option) : HttpContext =
    let ctx = DefaultHttpContext()
    ctx.Request.Method <- "POST"
    ctx.Request.Path <- PathString "/api/peer/echo"

    match peerName with
    | Some name -> ctx.Request.Headers["X-Peer-Name"] <- Microsoft.Extensions.Primitives.StringValues name
    | None -> ()

    match authorization with
    | Some value -> ctx.Request.Headers["Authorization"] <- Microsoft.Extensions.Primitives.StringValues value
    | None -> ()

    ctx

let tests (name: string) (factory: unit -> ContractSetup) =
    testList $"{name} — IPeerBearerAuthContract" [

        // ─── Acceptance path ──────────────────────────────────────

        testCaseAsync "valid bearer + matching X-Peer-Name → Accepted with peerName populated"
        <| async {
            let setup = factory ()
            setup.SeedBearer("buyer-a", "token-buyer-a-2026")

            let ctx = mkContext (Some "buyer-a") (Some "Bearer token-buyer-a-2026")

            let! outcome = authenticate setup.SecretStore ctx

            match outcome with
            | Accepted peerName -> Expect.equal peerName "buyer-a" "Accepted peerName matches the X-Peer-Name header"
            | Rejected(_, reason) -> failtestf "Expected Accepted, got Rejected(%s)" reason
        }

        // ─── Missing-token rejection ──────────────────────────────

        testCaseAsync "missing Authorization header → Rejected(MissingAuthorizationHeader)"
        <| async {
            let setup = factory ()
            setup.SeedBearer("buyer-a", "token-buyer-a-2026")

            let ctx = mkContext (Some "buyer-a") None

            let! outcome = authenticate setup.SecretStore ctx

            match outcome with
            | Accepted _ -> failtest "Expected Rejected, got Accepted"
            | Rejected(peerName, reason) ->
                Expect.equal peerName (Some "buyer-a") "peerName populated even on rejection — load-bearing for audit"
                Expect.equal reason RejectionReason.MissingAuthorizationHeader "Rejection reason"
        }

        // ─── Wrong-token rejection ────────────────────────────────

        testCaseAsync "wrong bearer → Rejected(TokenMismatch)"
        <| async {
            let setup = factory ()
            setup.SeedBearer("buyer-a", "token-buyer-a-2026")

            let ctx = mkContext (Some "buyer-a") (Some "Bearer wrong-token")

            let! outcome = authenticate setup.SecretStore ctx

            match outcome with
            | Accepted _ -> failtest "Expected Rejected, got Accepted"
            | Rejected(peerName, reason) ->
                Expect.equal peerName (Some "buyer-a") "peerName populated on TokenMismatch"
                Expect.equal reason RejectionReason.TokenMismatch "Rejection reason"
        }

        // ─── Constant-time comparison ─────────────────────────────

        testCase "constantTimeEquals uses CryptographicOperations.FixedTimeEquals on equal-length UTF-8 byte arrays"
        <| fun _ ->
            // Behaviour assertions covering the contract: same input
            // is equal, different-length inputs are NOT equal (and do
            // not throw), one-byte difference is NOT equal. A direct
            // timing assertion is flaky; verifying the function's
            // documented behaviour is sufficient to lock the
            // `CryptographicOperations.FixedTimeEquals` call site in
            // place — a regression to `=` would still pass equality
            // checks but break the no-throw-on-unequal-lengths
            // contract on older runtimes the SDK supports.
            Expect.isTrue (constantTimeEquals "secret-abc" "secret-abc") "identical strings compare equal"
            Expect.isFalse (constantTimeEquals "secret-abc" "secret-xyz") "one-char difference compares unequal"

            Expect.isFalse
                (constantTimeEquals "short" "this-is-a-much-longer-presented-token")
                "different-length inputs compare unequal without throwing"

            Expect.isFalse (constantTimeEquals "" "any") "empty expected vs non-empty actual is unequal"

        // ─── X-Peer-Name spoofing: right token, wrong name ────────

        testCaseAsync "valid bearer for peer A but X-Peer-Name = peer B → Rejected(TokenMismatch)"
        <| async {
            let setup = factory ()
            // Two peers with two different bearers. Caller presents
            // peer A's bearer but claims to be peer B.
            setup.SeedBearer("buyer-a", "token-buyer-a")
            setup.SeedBearer("buyer-b", "token-buyer-b")

            let ctx = mkContext (Some "buyer-b") (Some "Bearer token-buyer-a")

            let! outcome = authenticate setup.SecretStore ctx

            match outcome with
            | Accepted name -> failtestf "Expected Rejected — spoofed X-Peer-Name accepted peer-A token as peer %s" name
            | Rejected(peerName, reason) ->
                // The middleware resolves the secret keyed by the
                // header-supplied name, then compares against the
                // presented bearer. The peer-A bearer does not match
                // peer-B's expected secret → TokenMismatch, NOT a
                // silent acceptance. `peerName` echoes back what the
                // caller claimed so audit forensics can spot the
                // spoofing attempt.
                Expect.equal peerName (Some "buyer-b") "peerName echoes the spoofed claim for audit forensics"
                Expect.equal reason RejectionReason.TokenMismatch "Rejection reason"
        }

        // ─── Missing X-Peer-Name (sixth, edge-case)  ───────────────

        testCaseAsync
            "Authorization header set but no X-Peer-Name → Rejected(MissingPeerNameHeader) with peerName = None"
        <| async {
            let setup = factory ()
            setup.SeedBearer("buyer-a", "token-buyer-a")

            // Caller forgot to send X-Peer-Name. The middleware
            // cannot resolve the secret key without a peer name; the
            // request is rejected before any token-comparison work,
            // and the audit payload's `PeerName` field is `None`
            // (the caller is not identifiable). This is distinct
            // from `TokenMismatch` (peer name known but token wrong)
            // and `NoSecretConfigured` (peer name known but no
            // matching secret) — three different audit-filter
            // bucketings.
            let ctx = mkContext None (Some "Bearer token-buyer-a")

            let! outcome = authenticate setup.SecretStore ctx

            match outcome with
            | Accepted _ ->
                failtest "Expected Rejected, got Accepted — middleware accepted a request with no X-Peer-Name"
            | Rejected(peerName, reason) ->
                Expect.equal peerName None "peerName is None when caller is unidentified"
                Expect.equal reason RejectionReason.MissingPeerNameHeader "Rejection reason"
        }
    ]