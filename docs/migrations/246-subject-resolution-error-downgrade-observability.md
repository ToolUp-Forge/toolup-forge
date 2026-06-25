# Phase 246 — Subject-resolution `Error`-downgrade observability + fail-closed storage-scope default

**Ships in:** `ToolUp.Platform.Server` (`Middleware.fs` — `ScopeResolutionMiddleware`,
`StorageScopeDerivation`). **SDK 0.9.4.** Additive observability + one secure-default flip.

## What changes

Two adjacent silent-behaviour gaps on the scope-resolution path, both in `ScopeResolutionMiddleware`.

### 1. The authorization-shaped resolver `Error` → anonymous downgrade is now observable

When `ISubjectResolver.Resolve` returns `Error err`, the middleware synthesises an `AnonymousSession`
fallback. Pre-246 that was silent (`| Error _ -> ()`), so two *returned* (never thrown) outcomes —
`NotTeamMember teamId` (active-team pointer set but the user is no longer a member) and
`UnsupportedSubject kind` (the deployment's `Surfaces` admit no shape for this authenticated subject)
— looked exactly like an ordinary anonymous request. The Phase 234 "A1" catch only fires for *thrown*
exceptions, so it never saw these.

The middleware now emits a **distinct structured `Warn` + best-effort audit** naming the bridged case
(`NotTeamMember` / `UnsupportedSubject`), the `userId`, and the request method/path. The audit reuses
the existing `AuthScopeResolutionFailed` event with the case name as `ExceptionKind` — queryable, and
distinct from both a normal anonymous request (which emits nothing) and an A1 infra throw (which
carries a real .NET exception type name). `SubjectResolutionFailed` is **suppressed** here (the
resolver already logged the underlying throw and A1 audits the infra-failure class — no duplicate).
Every observability call is wrapped, exactly as A1 — a logger/audit failure on the auth path never
brings the request down.

The decision of what to emit is the pure `Middleware.resolverDowngradeSignal : SubjectResolutionError
-> (string * string) option` (`Some(case, detail)` to emit, `None` to suppress).

### 2. Storage-scope persistence now fails *closed* for an undeclared subject kind

`StorageScopeDerivation.persistenceFor` resolved a subject's persistence flag by matching a
`SurfaceProfile` from `config.Surfaces`; with no match it fell back to `Option.defaultValue true` —
i.e. **persistent**. A subject kind absent from a deployment's declared `Surfaces` (e.g. an
`AnonymousSession` fallback in a `[ individual; multiTeam ]` deployment that never declared
`Anonymous`) therefore silently landed in a **persistent** `session-{sid}` container keyed on a
client-suppliable session id — fail-open data retention under an undeclared scope.

The fallback is now **fail-closed (`false` / ephemeral)**, and the first time it fires for a given
subject kind the middleware logs a one-time-per-process diagnostic `Warn` naming the kind and the
declared `Surfaces` shape.

## Diff to apply

**Nothing** for a deployment whose declared `Surfaces` cover every producible subject kind — the
fail-closed default never fires, and it gains the downgrade observability for free.

The fail-closed flip is a **behaviour change only** for a deployment whose `Surfaces` omit a kind its
resolver can still produce. That kind's storage scope flips from persistent to ephemeral. If such a
deployment genuinely wants that kind to persist, declare the matching `SurfaceProfile` (e.g. add
`SurfaceProfile.anonymous` / `anonymousPersistent` to `Surfaces`). This is **not data loss** — it is
the secure default applying to a scope that was never declared.

The new downgrade `Warn` is **expected, not an error**, when a user is mid-team-removal
(`NotTeamMember`). A sustained rate indicates a `Surfaces` / membership misconfiguration.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- Full Expecto suite — green, including the new `Phase 246 — subject-resolution downgrade
  observability` pack: `resolverDowngradeSignal` names `NotTeamMember`/`UnsupportedSubject` and
  suppresses `SubjectResolutionFailed`; an undeclared subject kind derives `Persist = false`; a
  declared kind is unchanged; the fail-closed diagnostic is once-per-kind, not per request.

## Rollback

The observability is additive — removing it restores the silent downgrade. The fail-closed default is
the only behaviour flip; to revert it, restore `Option.defaultValue true` in `persistenceFor` — but
that re-opens the fail-open data-retention-under-undeclared-scope gap. Preferred forward fix for an
affected deployment is to declare the producible kind in `Surfaces`, not to revert the default.
