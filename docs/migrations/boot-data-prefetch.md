# Boot-time data prefetch

**Forge change:** `feat(client): prefetch the data snapshot at boot` (`6e167d7`).

## What changes

The shell now fetches the file snapshot (`FileManagementApi.ListFiles`) during
`bootLoadCommandsFor` whenever a `DataManager` is configured. Its `Processed`
entries are stored in a new `Model.PrefetchedProcessedData` field and merged
into `ProcessedData` by `resolveProcessedData` until the DataManager module is
actually mounted (at which point the module's live state is authoritative, so no
stale or duplicate entries).

Result: data modules see their uploaded data immediately on load, instead of
only after the user navigates to the Data Manager page.

The Home overview's per-tool counts (sourced from the server data catalog, a
separate store that stays zero for file-based modules) are also augmented from
the same `ProcessedData` context, so the landing page reflects loaded data the
moment the background prefetch lands.

## Consumer action

**None.** This is internal shell behaviour, additive and automatic. Any consumer
that composes a `DataManager` (`DefaultDataManager` / `MappingDataManager` /
`ExternalDataManager`) picks it up on upgrade with no code change. Deployments
with `NoDataManager` are unaffected (no fetch is issued).

## Verification

1. Upload a file via the Data Manager, then reload the app.
2. Navigate straight to a data-consuming module **without** opening the Data
   Manager — its data-source picker should already list the uploaded file.
3. The Home landing page should show a non-zero count for that tool once the
   background fetch completes.

## Rollback

Revert `6e167d7`. No consumer-side changes to undo.
