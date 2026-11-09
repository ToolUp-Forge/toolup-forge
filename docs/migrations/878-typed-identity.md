# 878 — Typed identity: a team id cannot be passed where a user id is expected

**Breaking, 0.42.0.** `UserId` and `TeamId` are single-case types in `ToolUp.Platform.Core`
(`Shared/Types/Identity.fs`). Every platform store and service seam that takes a user or a team id takes it
typed, `ScopeKey.Team` / `ScopeKey.User` carry the typed id, and the Core records that hold one outside the
remoting wire hold it typed. Passing a `TeamId` where a `UserId` is expected is a compile error at every one of
those sites.

**Nothing on the wire or at rest moves.** Each wrapper encodes as the bare string it wraps:

- under Fable it is `[<Erase>]`, so the value in the browser IS the string (no allocation; Fable's reflection
  reports the field as `string`, so a browser codec reads and writes the bare id);
- on .NET the remoting converter set (`FableConverters`) writes an erased single-case wrapper as its field, and
  the type also carries a `JsonConverter` for a serialiser configured without that set, so a persisted record,
  an audit payload or an SSE envelope keeps its bytes, and a document written before this release reads back;
- `ToString` returns the wrapped string on .NET, so an interpolated blob name renders as it did.

The team store's blob names and documents are pinned as literals (`IdentityBytesAtRestTests`), and the remoting
corpus under `tests/remoting-corpus/` is unchanged.

## What changes

| Before | After |
|---|---|
| `ScopeKey.Team of teamId: string`, `ScopeKey.User of userId: string` | `ScopeKey.Team of teamId: TeamId`, `ScopeKey.User of userId: UserId`; `ScopeKey.container` / `scopeId` render the same strings |
| `StorageScope.tryTeamId : StorageScope -> string option` | `StorageScope.tryTeamId : StorageScope -> TeamId option` |
| `ITeamStore` members over `teamId: string` / `userId: string`; `GetActiveTeam : … -> Async<string option>` | over `TeamId` / `UserId`; `GetActiveTeam : UserId -> Async<TeamId option>`; `ActiveTeamPolicy.ensureActiveTeam` takes the typed ids |
| every other store and service seam taking a `…userId` / `…teamId` string (`IPermissionStore`, `IPlatformAdminStore`, `IPresenceTracker`, `ISessionRegistry`, `IShareTokenStore`, `INotificationPreferenceStore`, `IUserClaims`, `IExternalContactStore`, `IServiceAccountStore`, `IConversationStore`, `IUserSchemaStore`, `IDataExporter` / `IErasureHandler` and every `Erase(…, subjectUserId, …)`, `ITenantLifecycle`, `ITenantFleet`, `IDeployPipeline`, `IBuildOrchestrator`, `IJobScheduler.TriggerOnce`, `IModelRegistry`, `IAdminMutationApprovalStore`, `IGrantConsentStore`, `IMigrationStatusStore`, `IPublicationReaderGrantStore`, `ITeamOutputVisibilitySource`, `IPlatformAIKeyStore`, …) | the parameter is `UserId` / `TeamId` |
| Core records holding a user or team id that never cross the remoting wire (`AuthenticatedUser`, `AccessContext`, `Subject`, `DeclaredPrincipal`, the audit payloads, `MembershipChangedPayload`, `SessionRecord`, …) | the field is `UserId` / `TeamId` (a full record literal wraps the id) |
| feature-tier records holding a user or team id (the AI budget, latency, consent and approval records, the Facts publication consent / revocation / refusal cases, `FactToolContext`, the passkey credential, challenge and session records, `ScimConfig.TeamId`, the RAG ingestion jobs' `OriginatingUserId`, the reporting AI callers, the scheduling booking payloads and calendar links, `OfflineReplayContext`, `Tenant.OwnerUserId`, `OAuthFlowState`, `RequestViewer`, `ScopeKind` and the other server-tier records) | the field is `UserId` / `TeamId` |
| `type EntityId = string`, `type TenantId = string`, `type ScopeId = string` | removed: each abbreviated `string`, and a type annotation naming one is `string` |
| — | `UserId.tryCreate` / `TeamId.tryCreate` (refuse a null, empty or padded id), `tryParse`, `value`, `.Value`; `Identity.isWellFormed`; `BlobMapStore.Read` / `Update` over a `ScopeKey` |

**What did not change, deliberately:** a record that crosses the remoting wire keeps its `string` id. Its
shape is pinned by the remoting corpus and its generated client decoders, and a typed field there is a wire
change this release does not make. The client tier's own UI state (form inputs, configuration, message catalogues) also keeps strings: a
half-typed id in a text box is not yet an id. A Google Workspace sender account (`GoogleDirectoryConfig.SenderUserId`, `EntraDirectory`'s
sender) is the directory's own identity, not a platform user, and stays a string. Those wire records are the API DTOs: `TeamInfo`, `TeamMembership`,
`CreateTeamRequest`, the invite, session and notification API records, and the other records reachable from an
API record. The handler that serves one converts at the seam (`.Value` out, `UserId x` in).

## The rewrite

Run the codemod with `--phase 878` (rules `878-*`). It rewrites what is mechanical and lists the rest:

```diff
-let scope = ScopeKey.Team "acme"
+let scope = ScopeKey.Team(TeamId "acme")
```

`878-typed-seam-call` lists a call to a seam whose id parameters are typed now. Wrap the string where you hold
one, validating an id that came from a request:

```fsharp skip=fragment
do! teams.AddMember(TeamId teamId, UserId userId, Member)

match UserId.tryCreate (ctx.Request.Query["user"].ToString()) with
| Ok user -> …
| Error why -> …
```

and read `.Value` (or `UserId.value`) where a string is needed — a log line, a wire DTO, a blob name built by
hand. `878-scopekey-payload` lists a `ScopeKey.Team` / `ScopeKey.User` built from a non-literal or matched in
a pattern: the pattern binds the typed id. `878-team-id-reader` lists `StorageScope.tryTeamId`, which answers
`TeamId option`. `878-retired-abbreviation` rewrites `EntityId`, `TenantId` and `ScopeId` in a type position
(after `:`, `of`, `*` or `<`) to `string`; `878-retired-abbreviation-site` lists one it could not place, such as
a return type after `->`. An implementer of a retyped seam changes its member annotations to the typed ids.

## Verifying

Build. Every site the compiler names is a place where an untyped id met a typed seam; the fix is the wrap or
the `.Value` above, never a cast. `IdentitySeamCensusTests` (in the platform test pack) fails on an interface
member that takes a `…userId` / `…teamId` string, so a new seam cannot regress.

## Rollback

Pin the 0.41 packages. There is no compatibility shim: a string id at a typed seam is what this release exists
to refuse.
