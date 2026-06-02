# ToolUp.Platform Technical Guide — 09. Module Conventions, Data Flow & Build

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> [← Prev: 8. UI Components & Front-End](08-ui-components.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 10. Notifications & Webhooks →](10-notifications-and-webhooks.md)

---

## The Four-File Module Convention

Every module follows the same structure:

| File | F# Declaration | Compiled By | Purpose |
|------|---------------|-------------|---------|
| `SharedTypes.fs` | `namespace Toolup` | Both dotnet and Fable | API request/response records, ToolUp.Remoting API record type |
| `Server.fs` | `module ModuleName.Server` | Server only (via `.fsproj` `<Compile>`) | Route handler implementation, data processing, `DataType` registration |
| `ClientModel.fs` | `module ModuleNameModel` | Fable only (via `.Client.props`) | Elmish Model, Msg, init, update, ToolUp.Remoting proxy |
| `ClientView.fs` | `module ModuleNameView` | Fable only (via `.Client.props`) | Feliz view function, `register()` returning `ErasedModule` |

`SharedTypes.fs` uses `namespace Toolup` so its types are accessible via `open Toolup` in both server and client code. This is consistent across all modules and avoids the need for module-specific `open` statements in consuming code.

The module `.fsproj` compiles `SharedTypes.fs` and `Server.fs` with `<Compile>`, and lists `ClientModel.fs` and `ClientView.fs` as `<None>` (visible in Solution Explorer for the module project, but not compiled by it — they're compiled by the Client project via the `.Client.props`).

## Data Flow

The standard data flow through the platform:

1. **Upload:** User selects a file in the DataManager module's client view.
2. **Transfer:** `DataManagerModel` sends the file contents to `FileManagementApi.UploadFile` via ToolUp.Remoting.
3. **Detection:** `FileManagement.detectFileType` iterates registered `DataType` records in priority order. First match wins; returns `"UnrecognisedData"` if none match.
4. **Processing:** `FileManagement.processFile` finds the matching `DataType` and calls its `Process` function, which returns a type-erased result (`obj`) plus a `ProcessedFileEntry` summary.
5. **Scope resolution:** `FileManagement.fileManagementApi` resolves the `IStorageScopeResolver` from DI, calls `resolver.Resolve(ctx)` to get the `StorageScope` for this request, then gets or creates the `SessionFileStore` for that scope.
6. **Persistence:** `SessionFileStore` saves the file contents in-memory. If `scope.Persist` is true, it also writes to `IBlobStorage` (default: local filesystem at `data/{container}/`).
7. **Client update:** The upload response includes the `ProcessedFileEntry`. The DataManager module's state picks it up via its own `update`, and the shell's next `computeProcessedData` pass aggregates it into `Model.ProcessedData`. The view re-renders with the updated `ProcessedDataContext` Provider value, so any module view `[<ReactComponent>]` calling `ProcessedData.forType` sees the new entry on the next render.
8. **Module activation:** Each module's `NeedsData` predicate is re-evaluated. Modules whose data requirements are met become active in the sidebar.
9. **Analysis:** When a user opens an analytical module and triggers an analysis, the module's client sends a request via its ToolUp.Remoting API. The server handler retrieves the file contents from `SessionFileStore` and passes them to the module's analysis function. The result is returned to the client.

At no point does any analytical module reference the DataManager or any other module. The only shared surface is the `DataTypeId` string convention — modules agree on string identifiers for data types, but this agreement is by convention, not by import.

### Post-save hooks

`SessionFileStore.AddFile` fires registered post-save hooks via `Async.Start` after the file has been persisted. Hooks are how companions extend the upload pipeline without touching `FileManagement.fs` — `ToolUp.RAG` registers a vectorisation hook here so newly-uploaded documents get embedded and indexed; future audit / notification publishers register the same way.

Hooks are configured at startup, not per-request:

```fsharp
// File-level mutables (mirror the storeEvictionMinutes pattern)
let mutable internal postSaveHooksConfig:
    (ProcessedData * ProcessedFileEntry * StorageScope -> Async<unit>) list = []

let mutable internal postSaveHooksLogger: ILogger option = None

let configurePostSaveHooks hooks = postSaveHooksConfig <- hooks
let configurePostSaveHooksLogger logger = postSaveHooksLogger <- logger
```

`compose` calls `configurePostSaveHooksLogger (Some resolvedLogger)` once. RAG's `composeWithRAG` calls `configurePostSaveHooks [ vectorisationHook ]` once. Both run before requests are served.

**Failure routing.** Each hook is wrapped in a `try / with` shim before `Async.Parallel | Async.Start`:

```fsharp
let safeHook hook = async {
    try
        do! hook (data, entry, scope)
    with ex ->
        match postSaveHooksLogger with
        | Some logger ->
            logger.Error(
                $"Post-save hook failed for file '{upload.filename}' (scope='{scope.ScopeId}')",
                Some ex)
        | None -> ()
}
```

The HTTP response has already been sent by the time hooks run, so an unhandled exception inside a hook would otherwise be silently dropped. Without this shim, a transient embedding-provider failure would leave the user thinking their document had been indexed when it hadn't. The shim never re-throws — `Async.Start`'s default behaviour on an unhandled exception is `TaskScheduler.UnobservedTaskException`, which is even worse than silent dropping.

Tests / harnesses that bypass `compose` see `postSaveHooksLogger = None` and silent-fail, matching prior behaviour exactly. The recommended pattern for tests is `configurePostSaveHooksLogger (Some testLogger)` at fixture setup.

## Build Pipeline

The FAKE build pipeline is SDK-provided. `SDK.Build.fs` defines all standard targets (Clean, RestoreClientDependencies, Build, Bundle, Run) and the dependency graph between them. The app's `Build.fs` calls `SDK.Build.registerTargets` with a `BuildConfig` and adds any app-specific targets (e.g., `Deploy-CD` for Azure deployment).

The `Run` target starts two concurrent processes:
- `dotnet watch run` for the server, with `SERVER_PORT` set as a scoped environment variable on the server process only (not globally, to avoid it leaking into the Fable compilation).
- `dotnet fable watch -c Debug` for the client, piped into `npx vite` for hot module reloading.

The `Bundle` target builds for production: `dotnet publish` for the server, `dotnet fable` + `npx vite build` for the client. The Vite build outputs to `deploy/public` and the server publishes to `deploy`.

## What Changed from the Standard Three-Project Layout

| Aspect | Standard layout | ToolUp.Platform |
|--------|--------------|--------------|
| **Project count** | 3 (Shared, Server, Client) | 1 SDK + N modules + thin entry points |
| **Shared types** | One Shared project | Each module owns its SharedTypes.fs |
| **Client compilation** | All .fs files listed in Client.fsproj | Props injection — Client.fs is the only listed file |
| **Server composition** | Hardcoded in Server.fs | `ServerApp` / `AIServerApp` / `RAGServerApp` fluent pipeline over `SDK.Server.compose` (plain `WebApplication.CreateBuilder` internally) |
| **Elmish program** | Defined in App.fs / Index.fs | `SDK.Client.run` — shell MVU in SDK |
| **Module addition** | Edit Shared, Server, and Client | Add props import + one `register()` call |
| **Module removal** | Remove from Shared, Server, Client | Remove props import + one `register()` call |
| **Type erasure** | Not needed (single app) | `ClientModule.register` — box/unbox in one place |
| **Build pipeline** | In Build.fs | SDK.Build.fs — app provides config only |
| **Auth** | Typically hardcoded | `IAuthProvider` interface — swappable per deployment |
| **File storage** | Not typically included | `IBlobStorage` — default LocalFileStorage, swappable |
| **Storage scoping** | Not typically included | `IStorageScopeResolver` — mode-aware per-request scope |
| **Team management** | Not typically included | `TeamStore` + `TeamApi` — SDK-owned CRUD |
| **Access control** | Not typically included | `AccessContext` — per-request, ready for RBAC |
| **Events** | Not typically included | `IEventStore` — inter-module communication |


---

> [← Prev: 8. UI Components & Front-End](08-ui-components.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 10. Notifications & Webhooks →](10-notifications-and-webhooks.md)
