# Migration — Phase 264: host-state binding-source projection seam

**Status:** additive, opt-in. Existing `ClientHostView.withElementView` (Phase 110) callers compile and run unchanged; a pipeline that never constructs a projection is byte-for-byte identical to a pre-264 build (GP 11 / GP 13). No existing signature is touched.

## What changes

The Phase 110 host-capability seam lets an external typed-tree UI language *be* a `ClientModule` view and routes its typed **actions** (`Navigate` / `Call` / `Notify` / `Dispatch`), but shipped no **read-side**: a data-driven hosted tree had to bake state into the tree at construction and re-emit the whole tree on every change. Phase 264 ships the neutral read-side a tree resolves its data bindings against.

New public surface:

| Symbol | Package / file | Purpose |
|---|---|---|
| `HostBindingSources` (+ `empty` / `ofQueryResults` / `ofState` / `tryResolve`) | `ToolUp.Platform.Core`, `Shared/HostBindingSources.fs` | Renderer-neutral key→value namespace: `QueryResults: Map<string,obj>` (host-projected domain values) + `State: Map<string,obj>` (renderer-maintained UI state). No tree-language type appears (GP 1). |
| `IHostStateProjection<'Model>` (+ `IHostStateProjection.ofFunc`) | `ToolUp.Platform.Client`, `Client/HostStateProjection.fs` | A host's projection of its Elmish `Model` into `HostBindingSources` (CSR). |
| `ClientBoundHostView.withBoundElementView` | `ToolUp.Platform.Client`, `Client/HostStateProjection.fs` | `withElementView`-shaped builder step whose view also receives the projected `HostBindingSources`. |
| `ServerHostStateProjection` (+ `forScope` / `forScopeDefault` / `defaultProjector`) | `ToolUp.PublicRendering`, `Server/HostStateProjection.fs` | Scope-bound SSR projection of Phase 112 authoritative live-session state into the same `HostBindingSources` shape. |

### Why `HostBindingSources` lives in `Core` (and not the client file)

The phase brief sketched the type in the client file. It ships in `ToolUp.Platform.Core` instead, because **both** the CSR projection (`ToolUp.Platform.Client`) and the SSR projection (`ToolUp.PublicRendering`) must produce the *same* type, and `ToolUp.PublicRendering` references Core + Server only — never the Fable client tier. Two same-named types in `namespace ToolUp.Platform` across the two assemblies would be ambiguous at any consumer (e.g. the test project) that references both. GP 10: a type shared across the client/server boundary lives in the shared floor. The client *projection seam* (`IHostStateProjection`, `withBoundElementView`) stays in the client file as the brief intended.

### Scope isolation is structural (GP 4)

A `ServerHostStateProjection` captures exactly one `StorageScope` at construction. Every `Project()` resolves only within `scope.ScopeId`'s partition (`ILiveSessionHost.ListSessions scope.ScopeId`). The projection holds no handle to another scope, so a projection built for scope A is structurally incapable of reading scope B's sessions — there is no runtime filter to forget, mirroring the Phase 112 host's own structural cross-scope denial.

## Adopting it

### Client (CSR)

```fsharp
ClientModule.create spec
|> ClientBoundHostView.withBoundElementView
       (IHostStateProjection.ofFunc (fun m ->
           HostBindingSources.ofQueryResults (Map [ "count", box m.Count ])))
       (fun model dispatch host sources ->
           // the hosted runtime resolves bindings against `sources`
           MyTreeRuntime.render (page model) host sources)
|> ClientModule.register
```

### Server (SSR)

```fsharp
// `scope` is the requesting principal's resolved StorageScope.
let projection = ServerHostStateProjection.forScopeDefault liveSessionHost scope
let! sources = projection.Project ()
// render the tree server-side, resolving its bindings against `sources`
```

A tree resolves a key the same way on both paths: `HostBindingSources.tryResolve key sources` (`QueryResults` shadows `State`).

## Verification

- `dotnet build ToolUp.Forge.sln`
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — the `Phase 264 — host-state binding-source projection seam` pack (client round-trip, SSR scope isolation, toy read-side on both paths, GP 13 interning + OSS grep-guard).
- Fable verification: `cd samples/MinimalClient && dotnet fable -o output` (the new client file flows through the Fable compiler via the Client tier).

## Breaking change

None. New types + new files; no existing signature changed. The Phase 110 `samples/ToyTreeBinding` toy gains an additive `Bind` node + tier-neutral `resolve` (the read-side neutrality proof) — an in-tree sample, no public-surface impact.

## Rollback

Remove the four new files and their `<Compile>` registrations; revert the toy `Bind`/`resolve` addition. Nothing else references the seam.
