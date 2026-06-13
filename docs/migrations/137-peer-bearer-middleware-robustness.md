# Migration — Phase 137: peer-bearer middleware robustness

**Status:** behavioural robustness, no API change. Relevant only to deployments that compose the InterPlatform peer substrate (`PeerServerApp` / `/peer/*` routes). A correctly-configured peer call is byte-for-byte unchanged — same 401/200 outcomes, secret still read per-call so rotation stays immediate.

## What changes

`PeerBearerAuthMiddleware` (the Phase 37 shared-bearer-token edge — distinct from the Phase 18 HS256-JWT `JwtPeerAuthProvider`) had two edge defects:

1. **Fail-closed-but-wrong-shape.** When a `/peer/*` path matched but no `ISecretStore` was registered, the middleware did a hard `:?> ISecretStore` downcast → `null` → `NullReferenceException` → a **500 with no `PeerCallRejected` audit event**. It denied the request (correct) but in the wrong shape and blinded the peer audit trail for exactly the misconfiguration case. It now uses a typed `match` on the resolved service: absence yields a clean **401 + `PeerCallRejected "no_secret_store"`**, mirroring the share-token middleware.
2. **Untrusted input into a key path.** `X-Peer-Name` was interpolated into `peers/{peerName}/bearer` after only a `.Trim()`. It is now validated (via the platform `IdentitySanitiser` charset policy) before the key is built; a name containing a path separator / `..` / control chars fails closed with **401 + `PeerCallRejected "invalid_peer_name"`** (distinct from `missing_peer_name_header`), so an `ISecretStore` companion that maps keys onto a filesystem/HTTP path can't be probed for traversal.

## Consumer action

None for correctly-configured peer deployments. Operators monitoring the peer audit stream gain two new reject reason codes (`no_secret_store`, `invalid_peer_name`) — a `/peer/*` misconfiguration now surfaces as an auditable 401 rather than an opaque 500.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — `PeerBearerAuthTests.fs`: a `/peer/*` request with no `ISecretStore` registered returns 401 + `PeerCallRejected "no_secret_store"` (not 500); an `X-Peer-Name` of `../other` returns 401 + audit with no key built from the traversal string; a correctly-configured call is unchanged.

## Rollback

Restore the `:?> ISecretStore` downcast and drop the `X-Peer-Name` charset guard. The no-secret-store path returns to a 500 with no audit; the peer name returns to `.Trim()`-only. No persisted state involved.
