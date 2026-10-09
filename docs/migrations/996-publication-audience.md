# A published page serves a publication audience

**Ships in:** ToolUp.Platform.Core (`PrincipalAdmission`, `AuthenticatedUser.Admission`,
`AccessContext.Admission`, `ClaimMapping.PublicationReaders`), ToolUp.AuthProviders.Oidc (`applyAdmission`),
ToolUp.Platform.Server (`SurfaceEnforcement.evaluateAdmitted`), ToolUp.PublicRendering (`PageAudience.Publication`,
`PublicationAudience`, `PublicationRead`, `PublicationAudit`). Breaking, ships in 0.25.1 (developed in the 0.25.0 draft) (Phase 996).

**Affected:** code that builds `AuthenticatedUser`, `AccessContext` or `ClaimMapping` as a full record literal,
and code that matches exhaustively on `PageAudience`. Behaviour is unchanged until a deployment sets
`PublicationReaders`.

## What changes

**1. A page can name readers outside the app.** `PageAudience.Publication { Readers; PublishingScope }` admits a
principal holding one of `Readers` as a directory role (an identity-provider role or group, after
`ClaimMapping.GroupAliases`). Nothing else admits: not team membership, module permissions or the platform-admin
role. An app member outside the group gets 403. `PublishingScope` is the storage scope the page reads, for
example `team-{teamId}`. It is fixed per page, or set from the route when the page producer resolves the slug.
In frontmatter the page reads `audience: publication:<scope>:<reader>,<reader>`. A value missing its scope or
its readers admits nobody.

**2. Readers are admitted, but never as app users.** `ClaimMapping.PublicationReaders` names the reader roles.
When the member rule (`RequiredRoles`) refuses a token but it holds one of these roles, the token is admitted
with `Admission = PublicationReader`. That reader is then refused (403) on:

- every `/api` route that does not admit anonymous callers, including the assistant, module routes and team
  creation;
- every page whose audience is not `Public` or a `Publication` it reads;
- search.

Signing in consumes no pending team invite. The provider refuses to build when `PublicationReaders` is set and
`RequiredRoles` is empty.

**3. The page reads the publishing scope, and every read is audited.**
`PublicationRead.latestNarrative store disclosure events audience slug tag reader` (or `latestNarrativeIn
services …`) returns the latest narrative tagged `tag` in `PublishingScope`. It runs only once the audience
admits the reader. It re-checks disclosure at `FactNarrativePublication`, and `Withheld` means a cited fact is no
longer disclosable. The page handler records every publication page decision, and every page decision for a
reader, as `PublicationServed` / `PublicationRefused`. The read records `PublicationNarrativeRead` /
`PublicationWithheld`. Rows are written to `IEventStore` under the publishing scope (`_platform` for a page that
is not a publication), source module `_publication`, and carry the reader id, the scope, the slug and the
narrative id.

**4. A pre-existing posture, now recorded.** Without a member rule, a signed-in principal in no team is an
individual-user subject. The default `/api` requirement (`userOrTeam`) admits it, including team creation's
route. An issuer that serves more people than your users therefore needs `RequiredRoles`.

## Migrate

```fsharp skip=fragment
// a record literal: add the field (or copy-and-update from AuthenticatedUser.anonymous / ClaimMapping.none)
{ ... ; DirectoryRoles = []; Admission = PrincipalAdmission.Member }        // AuthenticatedUser
{ ... ; TokenRoles = []; Admission = PrincipalAdmission.Member }            // AccessContext
{ ... ; ApiRoleGrants = Set.empty; PublicationReaders = [] }                // ClaimMapping
// an exhaustive match on PageAudience: add the case
| PageAudience.Publication audience -> ...
```

To publish to readers outside the app, keep the group object ids in configuration and map them with an alias:

```fsharp skip=fragment
{ ClaimMapping.directoryRoles with
    GroupAliases = Map[readerGroupIdFromConfig, "report-readers"]
    RequiredRoles = [ "app-user" ]                 // who IS an app user
    PublicationReaders = [ "report-readers" ] }
```

Give the page `PageAudience.Publication { Readers = [ "report-readers" ]; PublishingScope = "team-<id>" }`, and
build its body from `PublicationRead.latestNarrativeIn ctx.RequestServices …`, not from a read under the
caller's scope.

## Verify

`GatedSsrTests` §7:

- a group member who is not an app user gets 200 with the publishing team's narrative;
- the same reader gets 403 on `/api`, CreateTeam, the assistant, every other gated page and another team's
  publication;
- an app member outside the group gets 403 on the publication;
- an unchanged mapping admits exactly as before;
- the unsafe build is refused.

**Rollback:** clear `PublicationReaders` and change `Publication` pages back to `ScopeGated`.
