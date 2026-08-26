# Migration — Phase 528: session registry + revocation

**Status:** additive and opt-in, plus one **source-breaking** record widening in each of two config records (`ServerConfig` gains `SessionRegistry`, `ClientConfig` gains `SessionSecurity`). A deployment that builds its config with `{ ServerConfig.defaults with … }` / `{ ClientConfig.defaults with … }` — the documented shape — is unaffected and behaves byte-for-byte as before.

## What changes

Before this phase a bearer credential stayed valid until its natural expiry. There was no active-sessions view, no sign-out-everywhere, and no admin force-revoke, so a leaked token or a lost laptop could only be answered by rotating whatever signed the token — which signs out everybody. Phase 463 named that gap as the revocation window; this closes it.

1. **`ISessionRegistry`** (`Record` / `Touch` / `ListForUser` / `Revoke` / `RevokeAllForUser` / `IsRevoked`) is the new seam, honouring all six portability rules — a Redis-backed companion is implementable without touching it. The blob-backed default persists one JSON record per session at `_platform/sessions/{scopeId}/`.
2. **The session id is derived, never minted.** It is a one-way hash of the credential the caller already presents (the bearer token's `jti` where the token carries one, else a hash of the credential itself; for an anonymous subject, the Phase 337 server-sealed session id). So it is stable across instances with nothing to synchronise, revocable at exactly one credential's granularity, and **not itself a credential** — listing it back to a user or accepting it in a revoke call grants nothing, and no new client-visible secret exists.
3. **`SessionRevocationMiddleware`** refuses a revoked session before dispatch and records/touches a live one. It sits after `SurfaceEnforcementMiddleware`, so an unauthenticated caller is told they are not authenticated rather than that a session they do not have was revoked.
4. **`ISessionApi`** exposes list / revoke-one / sign-out-everywhere for the caller's own sessions, plus a team-admin force-revoke bounded to the caller's own team.
5. **Two `AuditEvent` cases** — `SessionRevoked` and `AllSessionsRevoked` — recorded under the session's scope.
6. **`SessionSecurityUI`** renders the caller's sessions with per-device and sign-out-everywhere actions.

## Consumer action

- **Deployments that do not opt in:** none. `ServerConfig.SessionRegistry` defaults to `NoSessionRegistry`, which registers no store, no middleware and no route (the `ISessionApi` surface 404s); `ClientConfig.SessionSecurity` defaults to `NoSessionSecurity`. Nothing is recorded and nothing is refused (GP 11 / GP 13).
- **To turn it on**, set both halves — the server-side registry and the client-side page. Unpairing them is the one configuration worth avoiding: a client page with no server registry renders a security surface whose every call 404s, and a page that cannot list your sessions is indistinguishable, to the person reading it, from a page saying you have none.

```fsharp
// Server composition root
let serverConfig = {
    ServerConfig.defaults with
        SessionRegistry = BlobSessionRegistry SessionRegistryOptions.defaults
}

// Client composition root
let clientConfig = {
    ClientConfig.defaults with
        SessionSecurity = DefaultSessionSecurity
}
```

- **Choosing the revocation window.** `SessionRegistryOptions.RevocationCacheSeconds` (default `30`) is the bounded staleness of the middleware's in-process cache, and it *is* the revocation window — a revoked session keeps working for at most that long on any one instance. Set it to `0` to pay a store read per authenticated request and have a revoke bite immediately. The cache is one-sided: a revoked verdict is held long (revocation is terminal), so the only staleness that can exist is a session honoured slightly *after* it was revoked, never one honoured again.

```fsharp
SessionRegistry = BlobSessionRegistry { SessionRegistryOptions.defaults with RevocationCacheSeconds = 0 }
```

- **Multi-instance deployments.** The store is correct across instances; the middleware's cache is per-instance, so a revocation reaches instance B within B's own window rather than instantly — the same shape `PerScopeKeyResolver`'s cache has. Either set `RevocationCacheSeconds = 0`, or compose a `CustomSessionRegistry` whose implementation invalidates peers over `INotificationChannel`.
- **Anyone implementing `ISessionRegistry`:** bind the `ISessionRegistryContract` pack. Two clauses are easy to get wrong and are pinned by it — `Record` must not clobber a stored record's `CreatedAt` or resurrect a revoked one (otherwise every revocation lasts until the holder's next request, i.e. the substrate does nothing), and `IsRevoked` must **fail open** on an unknown session or an unreachable store. It is a revocation list consulted *after* `IAuthProvider` has validated the credential; answering `true` on a miss would turn a store outage into a fleet-wide sign-out.
- **Anyone constructing `ServerConfig` / `ClientConfig` by full record literal** rather than from `defaults`: add the new field.

### What is and is not recorded

A session is recorded only where a stable credential exists to derive from. A header-auth deployment that presents no bearer token derives nothing and records nothing — inventing a per-request id would fill the store with single-use rows nobody can act on. `ClaimBearer` subjects are deliberately excluded: a share-token claim already has its own revocation path (`IShareTokenStore.Revoke`), and a second place to revoke the same thing is a place the two can disagree.

`SessionRecord` carries a truncated, normalised `User-Agent` and never an IP address. The record is listed back to the user, and a session list is a poor place to accumulate a location history; it exists to answer "is this the browser I am sitting at?", which a browser+OS pair answers and a fingerprint would over-answer.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — `Contracts/ISessionRegistryContract.fs` (15 cases: record idempotence without clobbering, revoked records survive re-recording, list scoping, revoke idempotence preserving the first actor, per-scope isolation of both list and revoke, the staleness bound, and fail-open) plus `InProcess/SessionRegistryTests.fs` (12 cases: the derivation's stability and one-wayness, retention filtering, and the traversal refusal at the scope seam).
- `cd samples/MinimalClient && dotnet fable -o output --noCache` — the client tier compiles with `SessionSecurityUI` in it.

## Rollback

Set `SessionRegistry = NoSessionRegistry`. Nothing else has to be undone: the store, the middleware and the route all disappear with the flag, and stored `SessionRecord` blobs are inert (they are read only through the registry). Revocations already made stop being enforced, so any credential you revoked becomes usable again until its natural expiry — rotate the signing key instead if that matters.
