# A publication reader's `/api` refusal row names the route class and its roles

**Ships in:** ToolUp.Platform.Core (`SurfaceDeniedPayload.RouteClass`, `SurfaceDeniedPayload.ReaderRoles`),
ToolUp.Platform.Server (`SurfaceEnforcement.ReaderRouteClass`, `SurfaceEnforcement.isPublicationReader`). Ships in
0.28.0 (Phase 1006).

**Who is affected:**
- Code that builds a `SurfaceDeniedPayload` as a full record literal. The record has two new fields, so such code
  fails with FS0764.
- Audit consumers that want to tell apart the routes a publication reader probed.

Every other deployment sees no change. A row for any subject other than a reader is byte-identical to its earlier
form.

## What changes

**A reader's `/api` refusal was already audited, and it still is.** Surface enforcement refuses a publication reader
`403 publication_reader_not_admitted` on every `/api` route. Each refusal writes one `SurfaceDenied` row under
`_platform`. The row carries:
- the subject kind (`publication-reader`) and the reader's id;
- the method, and the path without its query string;
- the denial code.

The default `IAuthAuditHook` also writes a coalesced `AuthorizationDenied` row. For each route and reader it writes the
first refusal in a 60-second window, then one summary row whose `DedupCount` is the number of refusals it suppressed.

**New: the reader's `SurfaceDenied` row also records:**
- `RouteClass`: `sse`, `upload`, `assistant`, `team-management`, `anonymous-open`, `module` or `api`
  (`SurfaceEnforcement.ReaderRouteClass`). The first class that matches wins, in that order.
- `ReaderRoles`: the roles the reader was admitted with.

Both fields are `None` for every other subject, and the row omits them then. No request body, query string or header
value reaches the row. An upload is recognised by its declared `multipart/form-data` media type, and only the class
name is recorded. The publishing scope is not recorded, because it is never known on an `/api` route.

**New: a failed audit write on the denial path is logged.** It used to be swallowed silently. This covers a write that
throws and one that fails after it has started. The response does not change: the request is still refused with the
same 403.

## Migrate

```fsharp skip=fragment
// a full SurfaceDeniedPayload literal: add the two fields
{ Method = m; Path = p; SubjectKind = k; SubjectId = id; DenialCode = c; Hint = h
  CorrelationId = cid; OccurredAt = at
  RouteClass = None; ReaderRoles = None }
```

A row written before 0.28.0 decodes with both fields `None`.

## Verify

`GatedSsrTests`, "Phase 1006 — a reader's /api refusals are audited". Each test was made to fail once before it was
trusted. The tests cover:
- the route classifier, class by class;
- a non-reader row being byte-identical to its earlier form, and a pre-1006 row decoding;
- one row per route class, with the reader, the class and its roles, and no request content;
- repeated refusals coalescing into a leading row and a counted summary;
- a member's 403 carrying no reader fields;
- a failing audit write, synchronous or asynchronous, still refused and logged.

## Rollback

Revert the phase's commits. No data migration is needed: rows written for subjects other than a reader are unchanged.
