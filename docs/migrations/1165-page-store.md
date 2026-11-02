# 1165 — The page store and the layer fold

**Breaking, 0.38.0** (two closed shapes widen); additive otherwise. A module page can now be a stored
document in layers — the platform seed and the platform's own edits, then a team's arrangement, then a
viewer's — each layer a hash-chained op stream in the published Fuaran-UI op vocabulary, folded platform,
then team, then individual. Saving is a gated, audited act per layer.

## What changes

| Surface | Change |
|---|---|
| `TeamPermissions` (ToolUp.Platform.Core) | gains `Pages : Map<string, PageGrant list>` — explicit page-save grants, `userId → grants`. A full record literal adds `Pages = Map.empty` (FS0764); `{ TeamPermissions.empty with … }` is unchanged. A stored document without the field reads as empty and is written back byte-identically. |
| `PageGrant` (Core) | new: `SavePlatform`, `SaveTeam`, `SaveOwn`, with `PageGrant.toToken` / `ofToken`. |
| `AuditEvent` (Core) | gains `PageSaved`, `PageReseeded`, `PageDetached`, `PageWriteRefused` (payloads in `PlatformAuditPayloads.fs`). An exhaustive match over `AuditEvent` adds the four cases. |
| ToolUp.Platform.Server | gains `PageLayers` (the fold, `invert`, the canonical op codec), `IPageDocumentStore` + `BlobPageDocumentStore`, `PageStoreOptions`, `PageAuthority`, and `PageDocumentStoreCompose.withPageDocumentStore`. It now references the published Apache-2.0 packages `Fuaran.Core.OpStream` (at the Fuaran Core pin) and `Fuaran.UI.Ops.Abstractions`, `Fuaran.UI.Ops`, `Fuaran.UI.OpStream.Abstractions` (at the Fuaran-UI pin) from nuget.org. |

## Adopting it — one line

```fsharp skip=fragment
ServerApp.empty
|> PageDocumentStoreCompose.withPageDocumentStore PageStoreOptions.defaults
```

The store resolves the registered `IDataObjectStore`, `IPermissionStore` and `IAuditLog` (a team deployment
registers all three) and the roles of the registered `ITeamStore`. `PageStoreOptions.defaults` keeps the
individual layer **off** and the platform layer a platform admin's. To enable the individual layer, or to let
one named team's document grant `SavePlatform`:

```fsharp skip=fragment
PageDocumentStoreCompose.withPageDocumentStore
    { PageStoreOptions.defaults with
        IndividualLayer = IndividualLayer.GrantedOnly   // or EveryViewer
        PlatformGrantTeam = Some "operations" }
```

## Who may save what (deny by default)

| Layer | Satisfied by |
|---|---|
| platform (save, seed, re-seed, detach) | `PlatformRole.PlatformAdmin`; or `SavePlatform` in the document of the `PlatformGrantTeam`, never a team's own |
| team | `PlatformAdmin`; the team's `Owner` or `Admin`; `SaveTeam` in the team's document |
| individual | refused (`LayerDisabled`) unless enabled: `EveryViewer`, or `GrantedOnly` with `SaveOwn` in the team's document |

Every save, reset, seed, re-seed and detach writes an audit row; every refusal writes `PageWriteRefused`
under the caller's scope.

## Semantics worth knowing

- **A seed is written once.** `Seed` on a seeded page answers `AlreadySeeded`, `Diverged` (the stored seed
  differs) or `Detached`; it never replaces the stored seed. Adopting a new compiled default is `Reseed`, an
  operator act, which re-folds the caller's layers and returns every op that no longer applies.
- **A conflict is reported, never dropped.** `LayerConflict` names the op's act (layer, principal, sequence)
  and, when an earlier act removed its target, that act (`ConflictCause.Removed`). A save whose ops would not
  apply to the layer's current fold is refused (`OpsDoNotApply`) and nothing is stored.
- **Stored ops are the vocabulary minus `UpdateState` and `ReplaceRoot`** (refused as `OpOutsideVocabulary`).
- **Undo** is `PageLayers.invert` / `invertAll` against the document the op applied to, appended as a save.
- **The document is bytes until the typed page document ships.** The seed is the page's canonical wire JSON
  and each layer carries its demanded-document sidecar as opaque bytes. When the Fuaran-UI pin reaches the
  release that publishes the typed `PageDocument`, the seed and sidecar adopt it; the stored layer format
  (`toolup.page-layer/1`) is versioned for that step.

## Verify

`dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter-test-list "Page store and layer fold"`.

## Rollback

Remove the `withPageDocumentStore` line. Page grants already written stay in the permission documents and
are ignored by a build without the store; the stored layers stay in the object store under `pages/…`.
