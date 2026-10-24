# Publication readers are granted inside the platform

**Ships in:** ToolUp.Platform.Core (`PublicationReaderGrant`, `AccessContext.PublicationGrants`,
`AccessContext.publicationGrantsFor`, `IPublicationReaderApi`, `PublicationReaderRoster`, `PublicationReaderEntry`,
`AuditEvent.PublicationReaderGranted` / `PublicationReaderRevoked`), ToolUp.Platform.Server
(`IPublicationReaderGrantStore`, `BlobPublicationReaderGrantStore`, `IPublicationReaderRoleSource`,
`PublicationReaderApiHandler`), ToolUp.AuthProviders.Oidc (`applyAdmissionWithGrant`), ToolUp.PublicRendering
(`PublicationReaderRoles`), ToolUp.Platform.Client (the team manager's publication-readers panel). Breaking for code
that constructs `AccessContext` or `TeamManagerMessages` as a full record literal, or matches exhaustively on
`AuditEvent`; ships in the 0.32.0 draft (Phase 1107).

**Affected:**
- A full `AccessContext` literal: add `PublicationGrants = []` (or build from `AccessContext.unrestricted`).
- A full `TeamManagerMessages` literal (a translated catalog): add the ten `PublicationReader*` strings.
- An exhaustive `match` on `AuditEvent`: add the two new cases.

Nothing changes for a deployment until a team's Owner or Admin grants a reader. Directory-group readers keep working
unchanged.

## The grant model

- **Chosen: an explicit reader grant.** The grant is `(principal, publishing scope, reader role)`, held in a grant
  store beside the permission store (blob-backed by default, one blob per grant under `_platform`). It is
  administered by an Owner or Admin of the publishing team. A grant names exactly one role, and only a role the
  scope's publications declare. There is no wildcard.
- **Rejected: a reader team** (a team whose membership grants reader roles). Team membership is exactly what Phase 996
  says must never admit to a publication. A reader team would also make the reader a `TeamMember`, with a team scope,
  module defaults and a place in the team switcher. Confining it would take a new team kind threaded through the team
  store, invites and the switcher.
- The grant is not part of `IPermissionStore` or `TeamPermissions`. Sign-in has to ask "does this principal hold any
  grant, in any scope?", and a per-team document cannot answer that without scanning every team. Widening those types
  would also break every decorator of the interface and the wire type.

## What changes

1. **The gate.** `AudienceGate`'s `Publication` arm admits a principal holding one of the audience's readers either
   as a provider-asserted role (`TokenRoles`, as before) or as a grant of that role for that publishing scope
   (`PublicationGrants`). Nothing else admits. Team membership, module permissions and the platform-admin role are
   still refused. A grant is read by no other audience, so it is never a `ScopeGated` role.
2. **Admission at sign-in.** The OIDC provider refuses a principal that fails `ClaimMapping.RequiredRoles`. If that
   principal holds at least one grant, it is now admitted as a `PublicationReader`. The tenant rule binds it exactly
   as it binds any reader. The grant store is resolved from the request's services. Where no store is composed, or
   the store cannot be read, nothing extra is admitted.
3. **Per request.** `ScopeResolutionMiddleware` reads the principal's grants on page routes, and on every route for a
   reader. They ride into `AccessContext.PublicationGrants`. A revocation therefore takes effect at the next request:
   a grant-only reader is refused at sign-in, and a member is refused the page. A reader's sign-in audit row lists its
   granted roles too.
4. **Confinement is unchanged.** A grant-only reader is a `PublicationReader`. It is served `Public` pages and its
   own publications only, and refused every `/api` route.
5. **The admin client.** The team details view shows a **Publication readers** panel to the team's real Owner or Admin,
   on the active team. From it they can list, add (by sign-in user id, per declared role) and remove readers.
   - The panel is backed by `IPublicationReaderApi` at `/api/IPublicationReaderApi/*`, mounted wherever teams are
     served.
   - Each grant and revocation is audited under the team.
   - Declared roles come from `IPublicationReaderRoleSource`, which public rendering registers over its page listing.
     A deployment that serves its publications without public rendering registers its own role source; without one,
     every grant is refused.

## Verification

`dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Phase 1107"` should
report 6 tests passed: the gate truth table, confinement, admission, the store, the admin API and the pipeline.

## Rollback

Revoke every grant from the panel, or delete the `_platform/publication-readers/` blobs. Admission and every page
gate are then exactly as before.
