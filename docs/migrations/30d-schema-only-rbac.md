# Phase 30d — Schema-only RBAC role + synthetic-sample substrate

## What changes

Three new public-surface additions land in `ToolUp.Platform.{Core,Server}`:

1. **`ModulePermission.SchemaOnly`** (`Shared/Types/PermissionTypes.fs`) — a fourth DU case alongside `Read` / `Write` / `Admin`. Sits **outside the read hierarchy**: `Admin` / `Write` / `Read` all imply `SchemaOnly` (more authority covers less), but `SchemaOnly` does NOT imply `Read` / `Write` / `Admin`. A partner whose only grant is `SchemaOnly` cannot inherit real-data access.
2. **`IDataCatalog.GetSyntheticSample : DataTypeId * count:int * seed:int -> Async<DataObject * byte[]>`** (`Server/IDataCatalog.fs`) — deterministic seeded synthetic-row generator. Backed by `SyntheticSampleGenerator.fs` (new file). Same `(typeId, count, seed)` → byte-identical CSV bytes, on every machine, every runtime.
3. **`SchemaOnlyGuard.assertReadAllowed`** (`Server/SchemaOnlyGuard.fs`) — the canonical "is this caller allowed to perform a real-row read on this module?" check that real-data handlers MUST wrap their `IDataObjectStore.Get` / `GetVersion` / module-specific blob-read invocations with. Refuses `SchemaOnly`-only callers + emits `SchemaOnlyAccessAttempted`.

Plus:

- Two new `AuditEvent` cases — `SyntheticSampleGenerated`, `SchemaOnlyAccessAttempted` — with payloads + `eventTypeName` + `AuditLog.serialise`/`decodeAuditEvent` round-trip.
- One new `_platform.notification_prefs` field — `schemaOnly.maxSampleRows`, `Int(Some 1, Some 100_000)`, default `100`. Per-scope cap on `GetSyntheticSample`'s row count.

### Spec deviation — `GetSyntheticSample` shape

The Phase 30d spec literal is `Async<DataObject>`. The shipped shape is `Async<DataObject * byte[]>`. Rationale: `DataObject` is metadata only; without the bytes, partners can't "iterate against representative-but-synthetic samples" (the Phase 30d goal). The tuple matches `IDataObjectStore.Get`'s shape, which is the substrate precedent for "metadata + content". The metadata's `ContentHash` is SHA-256 of the bytes, so the metadata's reproducibility property holds end-to-end.

### Spec deviation — substrate guard, not transparent substitution

The Phase 30d task line says "BlobIndex shielding: queries from a SchemaOnly user against real `_data` containers **return synthetic samples generated on demand**, not real rows". The acceptance criterion says "The same partner attempting to read a real blob via any API path **receives 403 Forbidden**". These contradict — silent substitution and an explicit 403 are different behaviours. The substrate implements the acceptance criterion: handlers return `Error "schema-only access refused"`, partners explicitly call `GetSyntheticSample` for samples.

## Diff to apply

### Consumer-side: a server handler that performs a real-row read

Before:

```fsharp
let getFileContent (scopeId: string) (objectId: string) (objectStore: IDataObjectStore) = async {
    let! result = objectStore.Get(scopeId, objectId)
    return result
}
```

After (with Phase 30d guard, when the handler has an `AccessContext` and `IAuditLog` in scope):

```fsharp
open ToolUp.Platform

let getFileContent
    (ctx: AccessContext)
    (moduleName: string)
    (auditLog: IAuditLog)
    (scopeId: string)
    (objectId: string)
    (objectStore: IDataObjectStore)
    =
    async {
        let! guard =
            SchemaOnlyGuard.assertReadAllowed
                ctx
                moduleName
                SchemaOnlyGuard.AttemptedPath.DataObjectStoreGet
                objectId
                auditLog
                scopeId

        match guard with
        | Error msg ->
            // SchemaOnly caller — refuse. Audit was already emitted
            // inside assertReadAllowed.
            return Result.Error msg
        | Ok () ->
            let! result = objectStore.Get(scopeId, objectId)
            return result |> Result.mapError string
    }
```

### Consumer-side: granting the SchemaOnly role

Permission-store grants accept the new case verbatim (wire format `"SchemaOnly"`):

```fsharp
permissionStore.SetMemberPermissions(
    teamId,
    "partner-alice",
    "Sales",
    [ ModulePermission.SchemaOnly ]
)
```

### Consumer-side: a SchemaOnly caller invoking the catalog

`GetSchema` and `GetSyntheticSample` are allowed for `SchemaOnly`. `ListObjects` is real-data metadata (object ids, content hashes) — handlers exposing it should guard with `SchemaOnlyGuard.assertReadAllowed` first.

```fsharp
let! schema = catalog.GetSchema "Sales"  // Always allowed.
let! (obj, csvBytes) = catalog.GetSyntheticSample("Sales", count = 50, seed = 42)
let csvText = System.Text.Encoding.UTF8.GetString csvBytes
// Partner iterates against `csvText` — deterministic for (Sales, 50, 42).
```

## Verification steps

1. `dotnet build src/ToolUp.Platform.Server/ToolUp.Platform.Server.fsproj` — clean, 0 errors. (Pre-existing FS0025 incomplete-match warning on `AuditLog.fs:184` from Phase 30a's auth-observability gap is unchanged by this phase.)
2. `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — full Platform.Tests Expecto suite passes 1785 / 1785.
3. `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list IDataCatalog` — 22 contract tests pass (12 of which are new Phase 30d adversarial coverage).
4. Determinism spot-check: call `IDataCatalog.GetSyntheticSample("YourType", 100, 42)` twice from a REPL; assert the returned bytes are byte-identical.
5. SchemaOnlyGuard spot-check: construct an `AccessContext` with `ModulePermissions = Map.ofList ["Foo", [ModulePermission.SchemaOnly]]`; call `SchemaOnlyGuard.isSchemaOnlyCaller ctx "Foo"` → `true`. Add `ModulePermission.Read` to the perms → `false`.

## Rollback

Phase 30d is additive — no existing API surface changes. To roll back:

1. Revert the four commits in `toolup-forge/` since this phase started.
2. Existing consumers that did NOT adopt `SchemaOnly` grants are unaffected.
3. Consumers that DID grant `SchemaOnly` will have permission documents containing `"SchemaOnly"` strings that the pre-Phase-30d wire parser does not recognise. The pre-30d `PermissionStore.Json.stringToPermission` returns `None` for unknown strings — silently dropping those grants from the effective permission set. Behaviour is fail-closed (the partner loses all access); not a security regression but a UX regression. Mitigate by clearing `SchemaOnly` grants from `_platform/permissions/*.json` before the rollback ships.

## Persistence + Phase 22 encryption-at-rest

`SyntheticSampleGenerator` does not write to `IBlobStorage`. Sample bytes are generated on-demand and returned to the caller. The phase-body task line "Synthetic-sample generation respects Phase 22 encryption-at-rest if persisted" needs no substrate work — no Phase 30d code path persists samples today. If a consumer chooses to persist samples through their own `IDataObjectStore.Save` call (e.g. for partner-side caching), the existing Phase 22 envelope-encryption decorator applies automatically because `Save` is the encryption seam.

**Confirming test (closes the residual item).** `src/ToolUp.Platform.Tests/InProcess/EncryptedBlobStorageTests.fs` carries the test _"Phase 30d — synthetic sample persisted via IDataObjectStore.Save is encrypted at rest"_. It proves the seam end-to-end:

1. Generate a deterministic sample via `SyntheticSampleGenerator.generate "Sales" schema 25 42 DefaultMaxSampleRows`.
2. Persist it through a `DataObjectStore` constructed over `EncryptedBlobStorage(inner, SingleKeyResolver.create secrets)`.
3. Read the content blob (`objects/_content/{hash}.data`) directly from the **inner, undecorated** store and assert it begins with the Phase 22 envelope magic (`EncryptionEnvelope.Magic`, "TOBL") and is **not** the plaintext synthetic CSV.
4. Assert a decorated `IDataObjectStore.Get` round-trips back to the original synthetic bytes (the seam is transparent).

The encryption seam lives at `IBlobStorage.Upload`, which `DataObjectStore.Save` routes every content write through — synthetic bytes are not special-cased, so they inherit at-rest encryption exactly as any other persisted content does. No Phase 30d substrate change was required to make this true; the test documents and guards the property.
