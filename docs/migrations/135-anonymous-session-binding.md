# Migration — Phase 135: anonymous-session ownership binding

**Status:** behavioural, no API signature change. A deployment on the default `NoOpAnonymousSessionMigrator` (the stock configuration) is byte-for-byte unchanged and emits no binding cookie. Only a deployment that composes a **real** `IAnonymousSessionMigrator` is affected — and the change closes a horizontal-data-theft hole in that path.

## What changes

The anonymous→authenticated session-migration seam previously read the inbound, fully client-controlled `X-User-Id` header verbatim as the *source* anonymous-session id and handed it to `IAnonymousSessionMigrator.Migrate(sid, subject)`, which copies everything under `session-{sid}` scope into the authenticating user's scope. Nothing proved the authenticating browser ever owned anonymous session `sid` (the id is a client-generated GUID that also rides in plaintext headers), so a signed-in attacker who learned a victim's anonymous GUID could migrate the victim's anonymous data into their own account with one request.

Now:

1. **A server-issued, signed, HttpOnly anonymous-session binding cookie** is minted at first anonymous request (new `AnonymousSessionBinding.fs` — sealed via the platform DataProtection key ring already persisted for multi-instance/restart safety; constant-time verify, fail-closed on any tampering).
2. **`AnonymousSessionMigrationMiddleware` resolves the source `anonymousSessionId` from the validated binding cookie**, never from `X-User-Id`. An absent or invalid binding ⇒ **no migration** (it does not fall back to the header).
3. **`IAnonymousSessionMigrator.Migrate` documents** that `anonymousSessionId` MUST be a server-verified, browser-bound value — implementers must never accept it from a self-asserted header/body.
4. The binding-cookie mint is **gated on a real migrator being registered** — a `NoOp` deployment emits no cookie and pays nothing (GP 13).

`IAnonymousSessionMigrator.Migrate`'s signature is unchanged; the contract it documents is stronger.

## Consumer action

- **Stock (`NoOp`) deployments:** none.
- **Deployments with a real `IAnonymousSessionMigrator`:** no code change is required (the SDK middleware now sources the id from the binding automatically), but confirm your migrator's own callers — if anything invokes `Migrate` outside the SDK middleware, it MUST pass a server-verified id, never a raw header value. The migration now silently performs **no** move when the binding is absent (e.g. a client that never received the cookie), which is the intended fail-closed behaviour.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — `AnonymousSessionMigrationTests.fs`: an authenticated request carrying `X-User-Id: <another browser's anon GUID>` with no matching binding performs no migration; a tampered binding fails closed; a valid binding migrates once; a `NoOp` deployment emits no cookie.

## Rollback

Revert `AnonymousSessionMigrationMiddleware` to read the source id from `X-User-Id` and remove `AnonymousSessionBinding.fs` + the mint. This **re-opens the horizontal-data-theft hole** for any deployment with a real migrator — roll back only if a binding-cookie defect is worse than the IDOR, and re-fix forward.
