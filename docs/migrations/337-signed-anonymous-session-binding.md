# Migration — Phase 337: signed anonymous-session binding

**Status:** behavioural, plus one **source-breaking** record widening (`SubjectResolutionRequest` gains `SessionIdVerified: bool`). Affects every deployment that declares a `SurfaceProfile.Anonymous` surface — not only, as in Phase 135, those composing a real session migrator.

## What changes

Phase 135 bound the anonymous session id to the browser with a server-issued, DataProtection-sealed HttpOnly cookie, and gated the anonymous→authenticated **migration** on that binding. It did not close the **scope-selection** leg: `DefaultSubjectResolver` still built `Subject.AnonymousSession` from the self-asserted `X-User-Id` header, so a caller could address any anonymous session's storage scope simply by naming its id. The migration gate protected the *lift*; the *data* was still reachable directly.

Nor could the Phase 135 mechanism have closed it as written. It minted a binding for whatever id an unbound browser asserted — trust-on-first-use — so an attacker presenting the victim's id on request one was handed a valid binding for it.

Phase 337 inverts the direction of trust:

1. **The sealed cookie carries the session id.** `AnonymousSessionBinding.boundSessionId` recovers it via `Unprotect`, which authenticates the seal before the payload is read — so a returned value is server-issued by construction. `X-User-Id` can only ever *echo* it.
2. **`SubjectResolutionRequest` gains `SessionIdVerified: bool`.** The server-tier extractor sets it from the seal; `SessionId` may still carry a claimed value, but a resolver honouring the contract cannot let an unverified one select a scope. This is carried by the type rather than by convention (GP 4), so a third-party `ISubjectResolver` is held to the same bar — the `ISubjectResolverContract` pack asserts it.
3. **`DefaultSubjectResolver` mints a fresh session** when the id is unverified or absent, rather than honouring the claim. `Middleware.fallbackAnonymous` (the resolver-error path) applies the identical gate.
4. **`ScopeResolutionMiddleware` seals whatever session the request resolved to** (`AnonymousSessionBinding.ensureBound`), so a first-time visitor is issued a session and arrives verified — and continuous — on their next request. It is a no-op when the browser is already bound, so a steady-state anonymous request emits no `Set-Cookie`.
5. **`AnonymousSessionMigrationMiddleware` reads the sealed id directly** instead of reading `X-User-Id` and re-checking the binding. Equivalent in effect, but there is no longer an unverified value on the path into the migrator. Its own mint is removed — `ScopeResolutionMiddleware` owns minting now, for every anonymous subject.

## Consumer action

- **Deployments with no anonymous surface:** none. No anonymous subject resolves, so no cookie is issued and nothing changes.
- **Deployments declaring `SurfaceProfile.Anonymous`:** no code change required. Anonymous callers now receive the `toolup-anon-binding` HttpOnly cookie on their first scope-resolved request (`/api/*`, `/dev/*`). **Confirm your client sends cookies on those calls** — a `fetch` with the default `credentials: "same-origin"` does; a cross-origin client needs `credentials: "include"` and the server needs a matching CORS policy. A client that never returns the cookie gets a fresh session per request rather than a stable one.
- **Anyone constructing `SubjectResolutionRequest` by hand** (a custom middleware, a test double, an out-of-process resolver host): add `SessionIdVerified`. **`false` is the correct value unless you cryptographically verified the id yourself** — a caller that cannot verify must not assert that it did.
- **Anyone implementing `ISubjectResolver`:** honour the flag. Bind the `ISubjectResolverContract` pack, which now fails an implementation that lets an unverified id select a scope.

### One-time scope rotation on persistent anonymous surfaces

A deployment using `SurfaceProfile.anonymousPersistent` keys stored data on `session-{id}`. Before this phase that id came from the client and survived across visits; now it is server-issued, so **existing anonymous visitors are issued a new session on their first request after the upgrade and will not see their prior anonymous data.** Authenticated and team scopes are untouched.

This is unavoidable rather than incidental: the old ids were exactly the client-chosen values the phase exists to stop honouring, so there is no migration that preserves them without preserving the vulnerability. On the default `Ephemeral` anonymous persistence the rotation is invisible. If prior anonymous data must survive, migrate it into authenticated scopes via `IAnonymousSessionMigrator` **before** upgrading.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — `InProcess/AnonymousSessionBindingTests.fs` (11 cases): a forged id does not select its scope; a signed id round-trips; the seal beats a mismatching `X-User-Id`; a tampered cookie fails closed onto a fresh session; a first-time visitor's session is continuous across two requests; `ensureBound` emits no cookie in steady state; the migrator receives the sealed id rather than the asserted header, and migrates nothing without a valid seal.
- `Contracts/ISubjectResolverContract.fs` — the anonymous branch now asserts an unverified id yields a fresh session, for every implementation binding the pack.

## Rollback

Revert the `SessionIdVerified` gate in `DefaultSubjectResolver` and `Middleware.fallbackAnonymous`. This **re-opens the anonymous-scope IDOR** — any caller can address any anonymous session's data by naming its id, and, with a migrator composed, have it lifted into their account on sign-in. Roll back only if a binding-cookie defect is worse than that, and re-fix forward.
