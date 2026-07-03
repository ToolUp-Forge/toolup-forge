# Phase 292 — descriptor schema-version + migration (consumer migration)

**What changes.** The serializable `CompositionDescriptor` ([Phase 284](284-composition-descriptor.md))
gains a **schema version** (`CompositionDescriptor.Version`, stamped at
`CompositionDescriptor.CurrentSchemaVersion` by `create`) and a **forward migration**
(`CompositionDescriptorVersion.migrate`), so a descriptor authored / persisted against an older forge
loads cleanly into a newer one. The ergonomic `ServerApp.ofManifest` now runs `migrate` **before**
composing; a descriptor whose version is newer than this forge understands fails with a readable
version-gap error rather than silently mis-loading.

**Scope.** Purely additive and opt-in (GP 11 + GP 13): a deployment that never persists descriptors
never migrates one, and the versioned build path is a strict superset of the pure builder — a
current-version descriptor migrates as a **byte-identical no-op**. Total (GP 4): the version gap is
data (`MigrationError`), rendered to a readable message, never an unchecked mis-load.

## The shape

```fsharp
type CompositionDescriptor = {
    Version: int                          // NEW — the schema version tag
    Components: ComponentSelection list
    Config: ServerConfig
}

// CompositionDescriptor.CurrentSchemaVersion : int   // = 1 today
// CompositionDescriptor.create        stamps CurrentSchemaVersion
// CompositionDescriptor.createVersioned <v> …        for a persisted / older-version descriptor

type DescriptorMigrationError =                       // distinct from Core's MigrationError
    | DescriptorTooNew of found: int * current: int   // authored against a later forge
    | UnknownDescriptorVersion of found: int          // negative / corrupt

type VersionedBuildError =
    | MigrationFailed of DescriptorMigrationError
    | BuildFailed of DescriptorError

// CompositionDescriptorVersion.migrate    : descriptor -> Result<descriptor, DescriptorMigrationError>
// CompositionDescriptorVersion.ofManifest : catalogue -> descriptor -> Result<ServerApp, VersionedBuildError>
// ServerApp.ofManifest (catalogue, d)     — raising; runs migrate first
```

## Migration behaviour

- **Current version** → `Ok` unchanged (no-op).
- **Older version** (in `[0, current)`) → the forward-migration steps run and the current version is
  stamped. Today the only prior version is `0` (an unversioned legacy descriptor, whose shape is a
  subset of the current one), so the upgrade is a re-stamp; each future schema bump adds its
  transform, keyed on the source version.
- **Newer version** → `Error (DescriptorTooNew (found, current))` — never silently down-migrated.
- **Negative / unknown** → `Error (UnknownDescriptorVersion found)`.

```fsharp
// A persisted descriptor loaded from JSON arrives with whatever Version it was written at.
match CompositionDescriptorVersion.ofManifest catalogue loadedDescriptor with
| Ok app  -> app |> ServerApp.run
| Error e -> eprintfn "%s" (CompositionDescriptorVersion.renderError e); exit 1

// Or the raising form, which migrates then composes:
ServerApp.ofManifest (catalogue, loadedDescriptor) |> ServerApp.run
```

## Verification

- `InProcess/CompositionDescriptorVersionTests.fs` in `ToolUp.Platform.Tests`: `create` stamps the
  current version; a current-version descriptor is a no-op migrate; an older (v0) descriptor migrates
  and composes equivalently to a current one; a too-new version fails with a readable version-gap
  message; a negative version is rejected as unknown; `ServerApp.ofManifest` raises the version-gap
  message; the total `CompositionDescriptorVersion.ofManifest` surfaces the failure as
  `MigrationFailed`.
- The Phase 175 public-API baseline treats the new field + types as additive surface growth — no
  `.approved.txt` edit needed.

## Rollback

Stop persisting descriptors across forge versions; the version tag defaults to the current version on
every freshly-authored descriptor, so nothing changes for a deployment that always builds its
descriptor in-process. Or revert the Phase 292 forge commit — the `Version` field and
`CompositionDescriptorVersion` module are additive; no persisted state is involved.
