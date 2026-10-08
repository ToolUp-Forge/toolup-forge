# A publication reader is its own subject, under any Surfaces

**Ships in:** ToolUp.Platform.Core (`Subject.PublicationReader`, `SubjectKind.PublicationReaderKind`,
`AuditSubject.ReaderAudit`, `AuditSubjectKind.ReaderAuditKind`, `AuditEvent.PublicationReaderSignedIn`,
`SubjectResolution.publicationReader`), ToolUp.Platform.Server (`StorageScopeDerivation.tryFromSubject`,
`SurfaceEnforcement.evaluateAdmitted`). Breaking, rides the 0.25.0 draft (Phase 1002).

**Affected:**
- Code that matches exhaustively on `Subject`, `SubjectKind`, `AuditSubject`, `AuditSubjectKind` or `AuditEvent`.
- Code that calls `StorageScopeDerivation.fromSubject` with a subject it did not resolve itself.

Behaviour changes only for a deployment that sets `ClaimMapping.PublicationReaders`.

## What changes

**1. Readers resolve the same way under every Surfaces list.**
- **Before.** Phase 996 resolved a reader through the app's own subject shapes. On a deployment serving only teams
  (`team`, `multiTeam`, `anonymousAndTeam`), a reader in no team resolved `UnsupportedSubject`, fell back to
  anonymous, and every `Publication` page answered it 401.
- **Now.** A principal admitted as a `PublicationReader` resolves to `Subject.PublicationReader userId`. This happens
  after a share-token claim and before Surfaces is consulted.
- The scope middleware applies the same rule whatever `ISubjectResolver` is composed.
- An app member in no team still resolves `UnsupportedSubject` under team-only Surfaces, exactly as before.

**2. A reader holds no scope of its own.**
- No `user-<id>`, `team-<id>` or session container is derived for it, and no `StorageScope` is stashed.
- `fromSubject` raises for a reader. Use `tryFromSubject` where a reader can reach.
- `configScope`, `flagScope`, `canAccessModule`, `hasPermission` and the request viewer give it nothing.
- It reads the publishing scope only through the audience gate of a `Publication` page that names its readers.

**3. A reader is refused on every `/api` route. This narrows Phase 996.**
- Phase 996 let a reader through routes open to anonymous callers. Surface enforcement now refuses it
  `403 publication_reader_not_admitted` on every `/api` route.
- That includes the anonymous-only `/api/` bridge and the query-param SSE routes (`/api/notifications`,
  `/api/ai/events`).
- Reason: such handlers fall back to a scope derived from the caller's id, which would hand the scope-less reader
  one of its own.
- Publication pages are server-rendered and need no `/api` call.

**4. A reader's sign-in is audited under `_platform`.**
- The first request in a session window writes one `PublicationReaderSignedIn` row under `_platform`. It records:
  - the subject kind (`publication-reader`);
  - the reader id;
  - the roles it was admitted with;
  - the provider;
  - the time.
- A reader never writes `UserLoggedIn`.

## Migrate

```fsharp skip=fragment
// an exhaustive match: add the cases (a reader is never a member, and never anonymous)
| Subject.PublicationReader readerId -> ...      // refuse, or serve publication reads only
| PublicationReaderKind -> ...
| AuditSubject.ReaderAudit readerId -> ...
| AuditEvent.PublicationReaderSignedIn p -> ...
// a scope derived from a subject: a reader has none
match StorageScopeDerivation.tryFromSubject logger config subject with
| Some scope -> ...
| None -> ...                                    // a publication reader: refuse
```

A custom `ISubjectResolver` should apply `SubjectResolution.publicationReader` first. The middleware enforces it
either way.

## Verify

These tests cover the change:

- **`GatedSsrTests` §7.** The publication scenario runs under `individual+team`, `anonymousAndIndividual`,
  `individual`, `team`, `multiTeam` and `anonymousAndTeam`:
  - the reader reads its page, is confined everywhere else, and holds no scope;
  - a member outside the group gets 403;
  - a teamless member under team-only Surfaces is challenged.
- **Anonymous-open routes.** They refuse the reader.
- **Sign-in.** It writes one `_platform` row.
- **`ISubjectResolverContract`.** Step 1b and the teamless-member pin.

**Rollback:** revert the phase. Readers then again depend on Surfaces (401 under team-only lists) and pass routes
open to anonymous callers.
