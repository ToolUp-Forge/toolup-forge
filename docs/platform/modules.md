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
open Elmish
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

// Server.fs (in the App composition root, src/ToolUpApp-Server/Server.fs)
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
    |> ServerModule.withGuardedApi helloApiFactory       // Fable.Remoting API
    |> ServerModule.withDataTypes [ helloDataType ]      // data type detection + processing
    |> ServerModule.withConfig helloConfigSchema         // per-module config
    |> ServerModule.withNeedsData [ "SalesData" ]        // declares dependency
    |> ServerModule.withProvides [ "HelloProcessed" ]    // declares output
    |> ServerModule.withAITools [ helloTool ]            // AI-callable tools
```

Only `create` is mandatory. Each `with*` helper adds a facet; the order doesn't matter. The composition root assembles every module's `ServerModule` record into the running `ServerApp`.

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
