# Phase 1148 — every scope renders through `ScopeKey`, and a storage scope can say which key it is

**Who this is for:** module and companion authors who build or read a storage scope by hand. The package
surface grows (additive); nothing a consumer compiles against is removed or retyped.

## What changes

1. **`StorageScope.tryKey`, `StorageScope.tryTeamId` and `ResolvedScope.Key`** (`ToolUp.Platform.Core`). A
   `StorageScope` names the `ScopeKey` it was rendered from: `tryKey` reads the container back through
   `ScopeKey.tryParseContainer` and returns the key only when the record's `ScopeId` is that key's
   `ScopeKey.scopeId`, so `tryKey (StorageScope.ofKey p key) = Some key` for every well-formed key.
   It is `None` for a claim scope (its `ScopeId` is its container), for a container outside the grammar,
   for an empty or padded id, and for a `ScopeId` that disagrees with its container. `tryTeamId` is the
   team case of it. `ResolvedScope.Key` is `tryKey` of the resolved scope's `Storage`, so it cannot
   disagree with the scope every store keys on; the anonymous scope has none.
2. **The tree spells no scope by hand.** Every `"_platform"` that names the reserved shard is
   `ScopeKey.PlatformContainer`; every `team-` / `user-` container is `ScopeKey.container`; every reader
   that tested a prefix with `StartsWith` reads through `ScopeKey.tryParseContainer` or
   `StorageScope.tryKey`. The bytes written are unchanged (the renderers are the byte-for-byte grammar
   pinned by `ScopeKeyTests`), and so is every container already at rest.
3. **`VerifyScopeLiterals` is a zero, not a ratchet.** The per-package ceiling table is gone: any
   gate-shaped literal under `src/` outside `ScopeKey.fs` is red, except the sites named in
   `scopeLiteralExceptions` in `Build.fs`, each with its file, count and reason (a literal that has the
   grammar's spelling but is not a scope, such as a `ComponentId` or a configuration schema key).

## What a reader may notice

The swept readers now use the strict parser, so three degenerate shapes read differently. None can be
produced through the sanitised team and user stores.

- A `team-` or `user-` container whose id is empty or padded (`"team-"`, `"user- alice"`) is no longer
  read as a team or user scope.
- A `StorageScope` whose `ScopeId` disagrees with its team-shaped container is no longer read as that
  team by the handlers that ask for the request's team (configuration, feature flags, forms, webhooks,
  maintenance, health, usage, service status, OAuth, the AI tool registry, the fact disclosure gate).
  Before, a claim scope whose id was itself `team-x` was read as the team `team-x`.
- `ScopeEnumeration.containerForTeam` prefixes a degenerate `team-` id (`"team-"` becomes
  `"team-team-"`); a well-formed `team-{id}` container is still returned unchanged.

## Do this

Nothing, unless you build scopes by hand in your own code. If you do, render them:

```fsharp skip=fragment
// before
let container = $"team-{teamId}"
let teamId = if scope.Container.StartsWith "team-" then Some scope.ScopeId else None

// after
let container = ScopeKey.container (ScopeKey.Team teamId)
let teamId = StorageScope.tryTeamId scope
```

## Verify

`dotnet run --project Build.fsproj -- VerifyScopeLiterals` prints `scope literals : 0` and the excepted
sites; the `Phase 1060 — ScopeKey` list in the Platform test pack pins `tryKey`, `tryTeamId` and
`ResolvedScope.Key` against every grammar case and every absent shape.

## Rollback

Revert the phase's commits. No data at rest moves in either direction.
