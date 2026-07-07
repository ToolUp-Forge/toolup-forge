# Migration — `VectorScope.User`: per-user KB retrieval isolation (GAP-1 security fix)

**What changed.** `ToolUp.Platform.VectorKnowledgeTypes.VectorScope` gains a fourth case:

```fsharp
type VectorScope =
    | Platform
    | Deployment
    | Team of teamId: string
    | User of userId: string   // NEW
```

**Why.** Before this change, KnowledgeBase ingestion routed *every non-team* caller's
chunks (Individual mode, AuthenticatedEphemeral, anonymous session) to the single shared
`VectorScope.Deployment`, and `RetrievalPipeline.authorisedScopes` admitted `Deployment`
for every authenticated user. Net effect: one user's privately-uploaded document was
retrievable — and quotable by the AI assistant — for a *different* user. The blob/index
container boundary (`user-{id}`) isolated per user; the vector layer did not. `User`
restores that boundary at the vector layer (GP 4).

**Behaviour after the fix.**
- Ingestion: a `user-{id}` KB container now maps to `VectorScope.User {id}` (was
  `Deployment`). A `team-{id}` container is unchanged (`Team {id}`).
- Retrieval: a non-team caller's scope list is `[ Platform; Deployment; User ctx.UserId ]`
  (was `[ Platform; Deployment ]`). `authorisedScopes` admits `User userId` only when
  `ctx.UserId = userId`, so no caller can read another's chunks.
- `Deployment` is now reserved for genuinely deployment-wide shared content only.

## Do I need to do anything?

**Most consumers: no.** The change is server-side and additive (GP 11). Scope derivation
and authorisation are internal to `ToolUp.KnowledgeBase.Server` / `ToolUp.RAG.Server`; the
`ServerConfig` surface is unchanged and an existing deployment upgrades with no config edit.

**If you pattern-match on `VectorScope`** (e.g. a custom `IVectorStore` /
`ISparseIndex` companion, a custom retrieval label, an audit projection), add a `User`
arm. The compiler flags the incomplete match (FS0025). The in-tree convention:

```fsharp
match scope with
| Platform -> "platform"
| Deployment -> "deployment"
| Team teamId -> $"team:{teamId}"
| User userId -> $"user:{userId}"   // add this
```

For the reverse mapping (key string → scope), branch on the `user:` prefix **before** the
team fallback:

```fsharp
elif sk.StartsWith "user:" then User(sk.Substring "user:".Length)
```

A custom vector store that lazy-loads `Team` scopes on first access must lazy-load `User`
scopes the same way (they are per-user, not known at construction). See
`InMemoryVectorStore` / `InMemoryBM25Index` for the reference pattern (`| Team _ | User _`).

## Existing-data caveat (operator action for affected deployments)

This fix stops *new* per-user chunks landing in `Deployment`. Chunks a non-team user
uploaded **before** upgrading remain under `VectorScope.Deployment` and stay cross-readable
until re-ingested. A deployment that already served real per-user KB content should
re-ingest (or purge + re-upload) those documents so they move into the owning user's
`User` scope. Fresh deployments and team-only deployments are unaffected.

## Verification

- `dotnet build ToolUp.Forge.sln`
- `dotnet run --project Build.fsproj -- VerifyAll`
- New regression pack: `ToolUp.Platform.Tests` →
  `KnowledgeUserScopeIsolation` (a `user-A` upload is not retrievable by a `user-B`
  caller; a user retrieves only their own chunks; store-level namespace isolation).
