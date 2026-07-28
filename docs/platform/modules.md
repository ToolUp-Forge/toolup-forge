# Module convention

A **module** is the unit of domain composition in a ToolUp app. The shell handles routing, persistence, scope resolution, auth, AI tool registration; modules handle the data and the UI for one capability.

This page covers the 4-file pattern, how to register a module, multi-page modules, data type registration, and how modules expose AI tools.

## The 4-file pattern

Every module under `src/Modules/<MyModule>/` follows this structure:

| File | Purpose | Compiled by |
|---|---|---|
| `SharedTypes.fs` | API record, DTOs, domain types | Server + Client |
| `Server.fs` | Server-side routines, data processing, `DataType` records, AI tool executors | Server |
| `ClientModel.fs` | Elmish `Model`, `Msg`, `init`, `update` | Client (Fable) |
| `ClientView.fs` | Feliz view + `register()` returning `ErasedModule` | Client (Fable) |

Plus:
- `MyModule.fsproj` — lists `SharedTypes` + `Server` as `<Compile>`, the two client files as `<None>` so Fable doesn't see them in the server graph.
- `MyModule.Client.props` — MSBuild props injecting the client files into the consumer's Client project via `<_ToolUpPlatformClientSources>`. Hidden from Solution Explorer.

This is the canonical convention. The cross-tier (Core / Server / Client) split that applies to **SDK companions** (because they ship as publishable NuGet packages) does **not** apply to modules. Modules are deployment-specific domain code; they don't get NuGet-packaged; the single-fsproj + `.Client.props` source-injection pattern is deliberate.

## Minimum module — Hello World

```fsharp
// SharedTypes.fs
module HelloWorld.SharedTypes

type HelloApi = { DoThing: string -> Async<string> }
```

```fsharp
// Server.fs
module HelloWorld.Server

let routine (input: string) : string = sprintf "did: %s" input
```

```fsharp
// ClientModel.fs
module HelloWorld.ClientModel
open ToolUp.Elmish
open ToolUp.Platform

type Model = { Text: string }
type Msg = NoOp

let init () : Model * Cmd<Msg> = { Text = "" }, Cmd.none
let update _ m = m, Cmd.none
```

```fsharp
// ClientView.fs
module HelloWorld.ClientView
open Feliz
open ToolUp.Platform
open HelloWorld.ClientModel

let view (model: Model) (dispatch: Msg -> unit) =
    Html.div [],                                    // left panel
    Html.div [ Html.text model.Text ]               // right panel

let register () : ErasedModule =
    ClientModule.create {
        Init = init
        Update = update
        Name = "Hello World"
        Icon = "/svg/chart.svg"
    }
    |> ClientModule.withView view
    |> ClientModule.register
```

That's it. The view signature `Model -> (Msg -> unit) -> ReactElement * ReactElement` returns left + right panels; the shell wraps them in `SplitPanel(l, r)`.

The runnable version of this minimum module lives at `samples/HelloWorld/`.

## Why API factories live in the composition root

The minimum module above doesn't ship an HTTP API — it's pure routine. When a module needs an HTTP API, the API record is **assembled in the composition root**, not in the module's own `Server.fs`.

```fsharp
// Server.fs (in the module)
module HelloWorld.Server

let echoRoutine (input: string) : string = sprintf "echo: %s" input

// Server.fs (in the app's server composition root)
open HelloWorld

let helloApiFactory (ctx: HttpContext) : HelloApi =
    let scope = ctx.GetScope()
    {
        DoThing = fun input -> async { return HelloWorld.Server.echoRoutine input }
    }

let helloModule =
    ServerModule.create "HelloWorld"
    |> ServerModule.withGuardedApi helloApiFactory
```

Why? The factory takes `HttpContext` and calls things like `FileManagement.getFileContents` or `makePermissionGuardedApi` — both server-only, injected into the consuming server project via `ToolUp.Platform.Server.props`. Module fsprojs don't import that; they only see shared types. So modules stay framework-agnostic, and the composition root assembles them into the framework-bound API records.

## Registering a module

A module registers itself via `ClientModule.register` (client) and is added to the server composition via `ServerModule.create ... |> ServerApp.addModules` (server).

The full server-side registration:

```fsharp
let helloModule =
    ServerModule.create "HelloWorld"
    |> ServerModule.withGuardedApi helloApiFactory       // ToolUp.Remoting API
    |> ServerModule.withDataTypes [ helloDataType ]      // data type detection + processing
    |> ServerModule.withConfig helloConfigSchema         // per-module config
    |> ServerModule.withNeedsData [ "SalesData" ]        // declares dependency
    |> ServerModule.withProvides [ "HelloProcessed" ]    // declares output
    |> ServerModule.withAITools [ helloTool ]            // AI-callable tools
    |> ServerModule.withDefaultSurfaceRequirement
           SurfaceRequirement.userOrTeam                 // default per-route requirement
    |> ServerModule.withRoutePrefix "/api/hello/"        // optional — required only when
                                                         // other modules in the deployment
                                                         // share a path prefix
    |> ServerModule.withRouteSurfaceRequirement
           ("POST", "/api/hello/public/submit")
           SurfaceRequirement.claimBearerOnly            // per-route override
```

Only `create` is mandatory. Each `with*` helper adds a facet; the order doesn't matter. The composition root assembles every module's `ServerModule` record into the running `ServerApp`.

## Surface requirements per route

Every server route declares which subject kinds may reach it via `SurfaceRequirement`. The module-level `DefaultSurfaceRequirement` covers the common case; `withRouteSurfaceRequirement` overrides per route when one endpoint diverges from the module default. The six named helpers cover the common shapes — `SurfaceRequirement.public_`, `.authenticated`, `.userOrTeam`, `.teamScoped`, `.anonymousOnly`, `.claimBearerOnly` — see [`surfaces.md`](surfaces.md#per-route-surfacerequirements) for the helper table and the request-resolution flow.

The strict global default (when no module or route declaration applies) is `userOrTeam`. Fail-closed is the rule — a forgotten declaration produces a 403 the operator notices, not a silent public exposure. The compose-time `SurfaceCoherenceValidator` raises an error when a module's declared default or a per-route override is unreachable under the deployment's `Surfaces` list, so misconfiguration surfaces at startup rather than at first traffic.

## Client-side `Visibility`

The client module registration carries a `Visibility: SubjectKind -> bool` predicate. The shell's sidebar filter hides modules whose predicate returns `false` for the current `Subject`. Four smart constructors cover the common shapes:

```fsharp
let registerSalesAnalysis () : ErasedModule =
    ClientModule.create {
        Init = init
        Update = update
        Name = "Sales Analysis"
        Icon = "/svg/sales.svg"
    }
    |> ClientModule.withView view
    |> ClientModule.withVisibility Visibility.visibleToAuthenticated
    |> ClientModule.register
```

- `Visibility.visibleToAll` — every subject kind (default; matches pre-66 behaviour).
- `Visibility.visibleToAuthenticated` — `UserKind` + `TeamMemberKind` + `ClaimBearerKind`. The right shape for any admin-shaped module in a mixed-mode deployment.
- `Visibility.visibleToAnonymous` — `AnonymousKind` only. Use for sign-up flows that should disappear once the visitor has signed in.
- `Visibility.visibleTo [kinds]` — explicit list of admitted kinds. The escape hatch when the three named helpers don't fit.

`Visibility` controls **discovery** — what appears in the sidebar — not authorisation. The server-side `SurfaceRequirement` is the gate; the client predicate removes the surface from the menu for subjects that wouldn't be allowed to reach it anyway. The two declarations move together: a module whose server declares `DefaultSurfaceRequirement = SurfaceRequirement.userOrTeam` carries `Visibility = visibleToAuthenticated` client-side, and so on.

## Modules vs pages

A **module** is one Elmish MVU (one `Model` / `Msg` / `init` / `update`). A **page** is a sidebar-visible entry rendered against that MVU.

Single-page modules (the default) keep the legacy `View: 'Model -> ('Msg -> unit) -> ReactElement * ReactElement` contract — the shell wraps the tuple in `SplitPanel(l, r)`.

Multi-page modules opt in with `ClientModule.withPages`, declaring one view per page keyed by `PageConfig.Route`. Each page view returns a `PageContent` value directly (`SplitPanel | Stacked | FullWidth | Dashboard | Custom`), picking its own layout shape:

```fsharp
let datasetView model dispatch : PageContent =
    SplitPanel(leftPanel model dispatch, rightPanel model dispatch)

let analyseView model dispatch : PageContent =
    FullWidth (analysisGrid model dispatch)

let registerSalesAnalysis () : ErasedModule =
    ClientModule.create {
        Init = init
        Update = update
        Name = "Sales Analysis"
        Icon = "/svg/sales.svg"
    }
    |> ClientModule.withPages [
        { Route = "/dataset";  Label = "Dataset"; View = datasetView }
        { Route = "/analyse";  Label = "Analyse"; View = analyseView }
    ]
    |> ClientModule.register
```

- Sidebar Id: single-page modules use the module Id; multi-page modules use `"{moduleId}{pageRoute}"` (routes start with `/`, which acts as the separator).
- `ModuleStates` is keyed by module Id, NOT by page. Navigation between pages of the same module does not re-initialise; all pages share the same `Model`.
- Page-level layout (`SplitPanel | Stacked | FullWidth | Dashboard | Custom`) is the page's choice. Use `Custom` only when the built-in shapes genuinely don't fit, since it bypasses the shell's gutter conventions.

Adding pages doesn't change storage, event, or notification wiring — pages are a presentation concern, not a persistence concern.

## Data type registration

Modules that handle file data declare `DataType` records in `Server.fs`. Each `DataType` has:

- **`Info`** — `DataTypeInfo` with `Id` (string constant), `DisplayName`, and optional `Schema`.
- **`Detect: string -> bool`** — given file contents, returns true if this `DataType` applies.
- **`Process: string * string -> obj * ProcessedFileEntry`** — given `(fileName, contents)`, returns a boxed result + a `ProcessedFileEntry` for the file manager.

```fsharp
module MyModule.DataType

open ToolUp.Platform

[<Literal>]
let MyDataTypeId = "MyDataType"

let myDataTypeInfo : DataTypeInfo = {
    Id = MyDataTypeId
    DisplayName = "My Data"
    Schema = None
}

let myDataType : DataType = {
    Info = myDataTypeInfo
    Id = MyDataTypeId
    Detect = fun contents ->
        let headers = CsvHeaders.parse contents
        headers |> CsvHeaders.containsAll ["required"; "headers"]
    Process = fun (fileName, contents) ->
        // parse, return (box result, ProcessedFileEntry)
        ...
}
```

The composition root wraps each `DataType` in a `ServerModule.withDataTypes [...]` declaration. Multiple modules can declare data types; the first-match-wins order is the registration order in the composition root's module list.

Client-side, modules render summaries of their processed data via `DataTypeDisplay.RenderSummary: obj list -> ReactElement`. The shell collects every entry of a given `DataType` and hands the list to the registered display.

`CsvHeaders` helpers are optional — detection can use any predicate (CSV headers, JSON shape, byte-level signature, etc.).

## Consuming processed data in a view

Modules consume processed data from upstream modules via the `ProcessedDataContext`:

```fsharp
let view (model: Model) (dispatch: Msg -> unit) : ReactElement * ReactElement =
    let processed = React.useContext ProcessedDataContext
    let salesData = processed |> ProcessedData.tryGet<SalesEntry> "SalesData"
    // render against salesData
```

`processed` is a `Map<DataTypeId, obj list>`. Use `ProcessedData.tryGet<'T>` to unbox to the typed value. This is the only sanctioned cross-module data flow — modules consume what other modules produce, declared via `withNeedsData` and `withProvidesProcessedData`.

Modules NEVER reach into another module's namespace or call another module's `update` function directly.

## AI tool exposure

Modules can declare AI tools that the LLM can call. The declaration lives in `Server.fs` and is registered via `ServerModule.withAITools`:

```fsharp
let myTool : AIToolDefinition = {
    Name = "my_module.analyse"
    Description = "Run sales analysis over selected SKUs."
    Parameters = ToolParameterSchema.create [
        "skus", ParamType.StringArray, "List of SKU IDs to analyse"
        "weeks", ParamType.Integer, "Number of weeks of history"
    ]
    Executor = fun ctx args -> async {
        let skus = args |> JsonValue.getStringArray "skus"
        let weeks = args |> JsonValue.getInt "weeks"
        let! result = MyModule.Server.runAnalysis ctx skus weeks
        return ToolResult.ok (Json.serialize result)
    }
    Visibility = ToolVisibility.ServerSide   // or ClientResident for UI-control tools
    Capabilities = ToolCapabilities.empty
}
```

The agent loop (in `ToolUp.AI.Server`) picks up registered tools, builds the LLM's tool schema, and routes tool calls to the right executor. See the [AI companion docs](../ai/) for the full tool-authoring guide.

## Module-private AI context

Modules can also export a `ModuleAIContext` that gets injected into the system prompt when the user chats from that module's view:

```fsharp
let moduleContext : ModuleAIContext = {
    ModuleName = "MyModule"
    SystemPrompt = "You are helping the user analyse sales data. The active dataset has columns X, Y, Z..."
}
```

Registered at `composeWithAI` time, looked up via the `ActiveModule` field on each `AIMessageRequest`. See the AI companion docs for the layered system-prompt composition (platform + team + module).

## Text inputs use local React state, not Elmish model

Inputs where the user types freely (AI chat, budget fields, search boxes) use `React.useState` for local display state. Only dispatch an Elmish message when the user explicitly submits (Enter / button click). Do **not** add `UpdateInput`-style messages that fire `prop.onChange` on every keystroke.

```fsharp
let view (model: Model) (dispatch: Msg -> unit) =
    let inputValue, setInputValue = React.useState ""
    Html.input [
        prop.value inputValue
        prop.onChange (setInputValue : string -> unit)
        prop.onKeyDown (fun e ->
            if e.key = "Enter" then
                dispatch (Submit inputValue)
                setInputValue "")
    ]
```

This isn't a style preference — Elmish dispatches synchronously, and per-keystroke dispatch on a heavyweight `update` is what causes input lag.

## Module independence

Modules:
- **Have no compile-time dependencies on each other.** Shared domain types live in a separate "shared types" project consumed by every module that needs them.
- **Communicate via persisted data, events, or AI tools.** Never via imports.
- **Don't reach into SDK internals.** Use the public `register()` surface only.
- **Don't `open` another module's namespace** in production code.

If two modules need to coordinate, the right shape is one emits events / publishes processed data; the other subscribes / consumes. Direct cross-module imports are a red flag for the design.

## When to split a module into pages, vs new modules

A page is part of one MVU; a new module is its own MVU. Use pages when:
- The pages share state (selected SKU is visible on both the "Dataset" and "Analyse" pages).
- The pages share data flow (loading the dataset on page A makes it available on page B).
- The pages are different presentation modes of the same underlying domain operation.

Use a new module when:
- The state is unrelated.
- The MVU lifecycle is independent.
- The data contracts differ enough that sharing a `Model` would be a Frankenstein.

When unsure, start with pages; split into a separate module if the `Model` / `Msg` start sprouting cases that only one page cares about.

## The module's label — `ModuleSurface`

Every module registration already says what the module contributes; `ModuleSurface.describe`
gathers that into one read-only descriptor — **the module's label.** It is what a composition
(an admin dashboard, a scaffolding tool, a conformance check, a composition-time graph rule)
can rely on without reading the module's source.

```fsharp
open ToolUp.Platform

let surface = ModuleSurface.describe MyModule.Server.serverModule

// or, with the client registration too — pages, flag keys and event
// topics only exist on the client side:
let full =
    ModuleSurface.describeWith (MyModule.Server.serverModule, Some (box (MyModule.ClientView.register ())))
```

`Provides` is what the module offers a composition: its registered data types (each keyed by
the wire `TypeName` its `Process` stamps onto the emitted `ProcessedData`), the query-bus keys
it answers, the AI tools it exposes, the route prefixes and exact-match routes it owns with the
`SurfaceRequirement` guarding each, the background jobs it schedules, the observability signals
and grounding metrics / subject hierarchies it declares, the config fields it publishes, and
its pages. `Needs` is what it requires back: the substrate interfaces its own registrations
imply, plus the feature-flag keys and cross-module event topics it reads. Every entry carries
the registration field it came from, and — where the kind has one — its Phase 279 `ComponentId`,
so a surface joins directly against the composition manifest and the platform-level
`ComposableSurface` descriptor (what forge *can* compose — this is the same idea one level
down, for one module).

**Derived, never hand-listed.** Every value comes out of the module's own `ServerModule` /
`ErasedModule`; every registration-field name comes out of `nameof`, so a rename breaks the
build. The descriptor also reports its own coverage — `Coverage` names every registration field
it classifies, `Unclassified` names every field the live record carries that it does not, and
`Stale` the reverse. Both lists are empty on a healthy build, and a drift-guard test asserts
that: **add a field to a registration record without teaching the descriptor and the test
fails**, rather than the label silently going short.

**Honest about what a registration cannot expose.** Some fields carry a *function*, so there
are no keys to enumerate, and those are reported in `Opaque` — named, counted, with the reason
— instead of being guessed at or quietly dropped:

| Field | Why it is opaque |
|---|---|
| `ServerModule.Handlers` | a Giraffe `HttpHandler` is a closure; its routes are unreachable. The declared route surface is `RoutePrefixes` / `RouteSurfaceRequirements` — a module that wants its routes on its label declares them. |
| `ErasedModule.NeedsData` | a predicate `(DataTypeId -> bool) -> bool`, not a declared key set — the ids it accepts are not enumerable. |
| `ErasedModule.ActionDecoder` | a `(actionKey, payloadJson) -> Msg option` function — the action keys it accepts are not enumerable. |
| outbound queries | no registration field declares the `(TargetModule, QueryKey)` pairs a module *asks* for; those are ordinary `IModuleQueryBus.Ask` calls. The needs side reports the substrate it can derive and says so. |

`ModuleSurface.toJson` (and the `describeJson` shorthand) projects the descriptor through the
SDK's canonical JSON converter set, deterministically — the same registration always yields
byte-identical output — so an external tool can snapshot a module's label without linking the
server assembly.

Nothing is built until a caller asks (GP 13), and the SDK names no module anywhere in the
derivation (GP 9) — the shape carries only `ComponentId`s, companion-interface names, and
strings drawn from the module's own registrations.
