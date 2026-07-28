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

## Cross-module queries — declare a contract

When one module needs to *ask* another something (rather than react to what it published), the
channel is `IModuleQueryBus`. The bus routes on `(TargetModule, QueryKey)` and carries a JSON
`Payload` string, so a hand-written call site makes the caller and the handler agree on three
things by hand — the module name, the key, and the payload's shape. None of the three is checked
until the request runs; a typo is a `NoHandler` and a shape drift is a deserialisation failure,
both at request time and both on whichever deployment happens to exercise that path.

**A `ModuleQueryContract<'Req,'Resp>` is one value that carries all three.** Declare it once in
the *providing* module's shared tier — the file both the server and the Fable client compile —
and reference that value from both ends:

```fsharp
// Reports/SharedTypes.fs — the providing module's shared tier
module Reports.SharedTypes

open ToolUp.Platform

type LatestReq = { DatasetId: string; Top: int }
type LatestResp = { Label: string; Score: decimal }

let latest = ModuleQueryBus.contract<LatestReq, LatestResp> "Reports" "latest"
```

```fsharp
// Reports/Server.fs — the provider answers it
let serverModule =
    ServerModule.create "Reports"
    |> ServerModule.withQueryContract Reports.SharedTypes.latest (fun _ req -> async {
        return { Label = req.DatasetId; Score = 1.5m }
    })
```

```fsharp
// any other module — the caller asks it
let! result =
    ModuleQueryBus.askContract bus access Reports.SharedTypes.latest { DatasetId = id; Top = 5 }
// result : Result<LatestResp, ModuleQueryError> option
```

The caller never spells the key, and `handle`'s parameter and return types come from the contract
— so a renamed key, a reordered field, or a changed response record breaks the **build** at
whichever end stopped matching, which is the whole point.

The client tier mirrors it exactly: `ModuleQueryClient.contract` declares one against
`Fable.SimpleJson`, `ClientModule.withQueryContract` registers a client-side handler, and
`ModuleQueryClient.askContract` asks. The two tiers differ only in which serialiser is baked into
the contract's codecs; the wire shape they produce is the same, which is why a client-declared
contract and a server-declared one on the same key interoperate.

**Nothing changes underneath.** `withQueryContract` lowers the contract onto the ordinary
`QueryHandlers` / `ClientQueryHandlers` list, and `askContract` builds the ordinary
`ModuleQueryRequest`. The registry, the RBAC check, the compose-time duplicate-key rejection, the
`ModuleSurface` label and the tracing spans all see exactly what the stringly path produces, and
the bytes on the wire are unchanged (GP 11). Registering a contract on a module whose `Name` is
not the contract's `TargetModule` is the one *new* rejection — the bus routes on the registering
module's name, so a mismatch would answer under a key no caller of that contract ever asks for.
That fails at compose time, naming both.

**The stringly path stays — as the interop fallback.** `ModuleQueryHandler.typed` /
`ModuleQueryBus.ask` (and their client twins) are unchanged and still correct. Reach for them
when the other end cannot reference the contract value: a caller in another deployment, a script
or admin tool poking the bus by name, a non-F# peer. Both registration styles coexist on one
module, and a contract-registered handler answers a stringly ask (and vice versa) as long as the
payload shape matches — which is exactly the drift the contract removes between two F# call sites
and cannot remove from a hand-written one.

When a payload does not decode — either direction — the failure comes back as
`Error (HandlerFailed …)` whose message names the contract (`"Reports.latest"`) and which side
failed, rather than as an opaque exception message. No new error case: the typed error channel is
the `ModuleQueryError` the bus already returns.

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

## Authoring against a deployment — `HostEnvelope`

`ModuleSurface` answers *what does my module offer*. Writing a module **into an application that
already exists** raises the mirror question first: *what can my module rely on here?* Which
substrate is actually composed, which data-type wire names and query keys are already taken,
which route prefixes are occupied, which config knobs this deployment resolved to what.

`HostEnvelope.describe` is that answer — one read-only descriptor of a specific composition's
**offer surface**. It is the type of the module-shaped hole.

```fsharp
open ToolUp.Platform

let modules = [ Orders.Server.serverModule; Inventory.Server.serverModule ]
let app = ServerApp.empty |> ServerApp.addModules modules |> ServerApp.withAuditSink mySink

let envelope = HostEnvelope.describe (app, modules)
```

The modules are passed alongside the app because `addModule` *fans* a `ServerModule` into the
app's accumulators and keeps no registration record — the same reason the other derived lenses
take a `ServerModule list`. Everything else comes from the app. Pass each module's erased client
registration too (`HostEnvelope.describeWith (app, [ m, Some (box (MyModule.ClientView.register ())) ])`)
when the pages / feature-flag / event-topic side of each module surface matters.

| Field | What it answers |
|---|---|
| `EnvelopeCapabilities` | how much of each kind this deployment composed — modules, companions, data types, tools, grounding metrics, subject hierarchies — with their `ComponentId`s. A kind with nothing composed still appears, count `0`. |
| `EnvelopeSlots` | every companion slot forge can compose, each marked `FilledSlot` (with the impl sub-ids for a multi-impl slot) or `OpenSlot`. **The open slots are the load-bearing half** — they are exactly what a module may *not* rely on here. |
| `EnvelopeModules` | each composed module's `ModuleSurface`, unchanged. The data-type ids (each *is* the wire `TypeName`), query-bus keys already answered, tool names already taken, and the substrate each module implies. |
| `EnvelopeKnobs` | each composition-shaping `ServerConfig` knob with both its admissible value set *and* the value this deployment resolved. |
| `EnvelopeRoutes` | the occupied route prefixes and exact routes, attributed to their declaring module, with the admit set guarding each — the prefix space a new module must not collide with. |
| `EnvelopePlatform` | the platform assembly + version the envelope was derived under. |

**Derived, never hand-listed.** Every axis is a projection of a derivation that already exists:
the layers group the composition manifest and are *seeded* from the `ComponentKind` union; the
slot universe is `ComposableSurface.slots ()` reflected off the `ServerApp` record, joined
against the manifest's composed slots; the knob schemas are reflected off `ServerConfig`'s
enum-like fields with the value read off the live config; the routes are *filtered from the
module surfaces*, not re-derived. A drift-guard test re-derives each axis independently and
asserts set-equality — **a composition that gains a companion slot, a config knob, a component
kind, a module or a route, and an envelope that misses it, fails that test.**

### Regenerating and diffing envelopes

`HostEnvelope.toJson` (and the `describeJson` shorthand) projects the whole envelope to canonical
JSON, so an external authoring tool consumes it as a pinned snapshot **without linking the server
assembly**. Every list is emitted in a stable sort order and record fields serialise in
declaration order, so the same composition always yields byte-identical output.

```fsharp
// In the deployment (an admin endpoint, a CLI target, a test):
System.IO.File.WriteAllText("envelope.staging.json", HostEnvelope.describeJson (app, modules))
```

Pin the stamp beside whatever you generated from it:

```fsharp
let stamp = HostEnvelope.stampOf envelope
// { StampSchemaVersion = 1; StampPlatformVersion = "0.9.4.0"; StampContentHash = "8f3c…" }
```

Later, against a live app, ask whether the snapshot is still true:

```fsharp
match HostEnvelope.staleness pinnedStamp (HostEnvelope.describe (app, modules)) with
| [] -> () // still exactly true of this deployment
| reasons -> printfn "envelope stale: %s" (String.concat ", " reasons)
```

The reasons are stable codes, one per axis that moved, so a consumer can tell the two cases
apart: `HostEnvelope.ContentChangedReason` means *regenerate — the deployment changed*;
`HostEnvelope.SchemaVersionMovedReason` means *the envelope shape itself changed, upgrade the
tool*; `HostEnvelope.PlatformVersionMovedReason` means the forge build moved underneath.
`HostEnvelope.isCurrent` is the boolean shorthand.

Diffing two deployments is the same operation across environments rather than across time: derive
an envelope per environment, write both JSON files, and diff them. Because the projection is
canonical, the diff is the *composition* difference and nothing else — a slot that is filled in
production and open in staging shows up as exactly that line.

Nothing is built until a caller asks (GP 13), and the SDK names no module or vendor anywhere in
the derivation (GP 9) — the shape carries only `ComponentId`s, companion-interface names, and
strings drawn from the deployment's own registrations.

## The module's conformance pack — `ModuleContract`

A module is registered **twice** — once server-side as a `ServerModule`, once client-side as an
`ErasedModule` — through two composition roots that never see each other. Nothing in the SDK checks
that the two halves agree. `ModuleContract` is that check: a reusable law set, parameterised over a
witness, that a module binds in **one test file**.

```fsharp
open ToolUp.Platform.Tests.Contracts

let myModuleWitness =
    ModuleContract.witness (
        MyModule.Server.serverModule,       // the ServerModule registration
        MyModule.ClientView.register (),    // the ErasedModule registration
        "Contoso.Orders"                    // the declared top-level namespace root
    )
    |> ModuleContract.withExportedTypes (
        ModuleContract.exportedTypesOf typeof<MyModule.SharedTypes.Order>.Assembly)

let tests = ModuleContract.laws "Contoso.Orders module" myModuleWitness
```

The five laws:

| Law | What it asserts | Why it matters |
|---|---|---|
| **Server/client id parity** | `ServerModule.Name` and `ClientModule.Definition.Id` resolve to the same `ComponentId` (Phase 580's `ModuleIdentity.componentIdOf`) | `Name` is an **id token**, not a display name: it is the RBAC permission key, the `ServerConfig.ModuleNames` entry, and the client's `Model.ModuleStates` map key. An id left unset on the client is derived from the display `Name` with spaces stripped — so a server module called `"Hello World"` and a client module called `"Hello World"` do **not** match. |
| **Wire-`TypeName` uniqueness** | no two of the module's `DataType` / `DataTypeDisplay` registrations share an id | `DataType.Id` *is* the wire `TypeName` its `Process` stamps onto the emitted `ProcessedData`; a repeat collides silently. |
| **NeedsData satisfiability** | the module's `NeedsData` gate is satisfied by the data types the composition advertises | a gate no composition can satisfy renders the module's empty state forever. |
| **Action emitter↔decoder coverage** | every `EmitsActions` declaration targeting this module is decoded by this module's `ActionDecoder` | `ActionDeclaration` is an inspection surface, not an enforcement contract — an undeclared decoder drops the action silently. |
| **Top-level-namespace convention** | every type the module package exports sits under one declared root | two packages each exporting a bare `DatasetView` cannot compose into one deployment. |

**Where the laws read from.** The first two read the module's [`ModuleSurface`](#the-modules-label--modulesurface)
descriptor, not ad-hoc reflection. The last three cannot fully: as the `Opaque` table above records,
`NeedsData` is a predicate and `ActionDecoder` is a function, so neither has an enumerable key set.
Those laws therefore **probe** what is observable — the predicate is *evaluated* against the
advertised ids, and the decoder is *called* with each declared action key. That is an honest
approximation rather than the full law, and the limits are explicit: behaviour outside the probed
inputs is not covered, and the reverse direction (a decoder key no tool emits) is not observable at
all.

Three chainers widen the witness where a module legitimately needs it — each one a visible
declaration, not a silent loosening:

- `withExportedTypes` — **required.** The namespace law *fails* a witness that declares no exported
  types rather than passing vacuously. Use `exportedTypesOf myAssembly`, or an explicit `typeof<…>`
  list when the client tier is source-injected via `.Client.props` and has no assembly of its own.
- `withAvailableDataTypes` — when the module consumes a data type another module registers.
- `withActionProbePayload` — when the decoder validates payload shape and the default `"{}"` probe
  would misreport.

**Adoption.** The pack lives at `src/ToolUp.Platform.Tests/Contracts/ModuleContract.fs` beside the
other contract packs. That project is `IsPackable=false`, so — as with every existing contract pack —
an out-of-tree module repo copies the file into its own test project; it depends only on Expecto plus
the `ToolUp.Platform.*` packages the module already references, and the SDK names no module anywhere
in it (GP 9). The in-repo reference binding is `samples/HelloWorld`, and the pack's own self-test
binds deliberately non-conforming modules to prove each law fails. Full detail:
[`docs/migrations/582-module-contract-pack.md`](../migrations/582-module-contract-pack.md).

## Packaging a module for Fable consumers — the layout contract, checked

The 4-file pattern above assumes the module lives *inside* the deployment: the consumer's client
project imports `MyModule.Client.props` off disk, so the client compile list is a build-graph
detail nobody ships. A module distributed as a **NuGet package** cannot do that. Its client tier
has to travel *as source* — Fable compiles F#, not IL — so the `.fs` files and the project file
that orders them are packed into the nupkg under `fable/`, and the consumer's Fable package
loader extracts and compiles them alongside its own client code. That is the same
source-in-nupkg convention every client-tier SDK package uses:

```xml
<Content Include="**\*.fsproj;**\*.fs;**\*.svg"
         Exclude="**\*.fs.js;**\bin\**;**\obj\**"
         PackagePath="fable\" />
```

The project file packed under `fable/` is the module's **shadow project** — the compile list the
consumer's Fable build actually reads. For a single-project packaged module it is literally the
module's own `.fsproj`, packed by the glob above; for a module that keeps the 4-file split (server
files `<Compile>`d, client files `<None>`d) it is a second project file carrying just the client
compile list. Either shape works. What neither shape has, on its own, is a guard.

**Four ways the layout drifts, all silent, all discovered by the consumer:**

| Drift | What the consumer sees |
|---|---|
| A client file the shadow project doesn't list | Fable fails on an unresolved module — in *their* build, naming *your* namespace |
| A server-only file inside the Fable-compiled set | Fable chokes on a server-only API (`System.Data`, Giraffe, an `IBlobStorage` call) |
| Compile-order drift between the two projects | F# compile order is semantic; a swap is a hard compile error downstream |
| An asset (or the shadow project itself) with no `PackagePath` entry | A missing icon at runtime, or Fable never finding the source at all |

None of these fail the module's own build, its own tests, or `dotnet pack`. The module ships,
and the first person to find out is someone else.

### The check

`ToolUp.Platform.Build` states the contract as four **laws** and checks them over two parsed
project files plus a pack manifest. It is a pure comparison — no MSBuild evaluation, no Fable
invocation, no consumer app — so it runs in milliseconds, **before** `Pack`, in the module's own
pipeline:

| Law | Id in the failure message | What it requires |
|---|---|---|
| `ShadowSubsetLaw` | `shadow-subset` | The shadow's `Compile` set corresponds to the main project's declared client files — nothing extra, nothing missing |
| `ShadowExclusionLaw` | `server-exclusion` | No file the module declares server-only is Fable-compiled *or* packed under `fable/` |
| `ShadowCompileOrderLaw` | `compile-order` | Files common to both projects appear in the same relative order |
| `ShadowAssetPathLaw` | `asset-path` | The shadow project file, every file it compiles, and every declared asset are present in the packed layout |

Every failure renders as `[law-id] subject — explanation`, so a failed build says which law broke
over which file, not just that something is wrong.

**What the module declares** is a `PackagedModuleContract`: which files are server-only (by name or
by directory prefix), which assets must ship, where the Fable root is (`fable` by convention), and
what the shadow project file is called. Nothing is inferred — the laws check the projects and the
pack against what the author declared, which is what keeps the check from being tautological.

### Wiring it

In the packaged module repo's own `Build.fs`, before `Pack`:

```fsharp
open ToolUp.Platform
open ToolUp.Platform.Build

let layout =
    { PackagedModuleCheckOptions.forProject "src/My.Module/My.Module.fsproj" with
        ShadowProject = "src/My.Module/My.Module.Fable.fsproj"   // omit for the single-project shape
        Contract =
            { PackagedModuleContract.create "My.Module" "My.Module.Fable.fsproj" with
                ServerOnlyFiles = [ "Server.fs" ]
                ServerOnlyDirectories = [ "Server/" ]
                RequiredAssets = [ "icons/chart.svg" ] } }

init args
registerTargets config
PackagedModuleConformance.registerTarget layout
execute args
```

`dotnet run -- VerifyPackagedModule` then fails on any of the four laws.

**Where the pack manifest comes from** is the `ManifestSource`, and the default is the interesting
one:

- `FromPackDeclarations` (the default) derives the manifest from the main project's own
  `PackagePath`-bearing items, expanded against the project directory — *what the project says it
  will pack*. Nothing has been packed yet, which is exactly why this is the pre-`Pack` gate.
- `FromNupkg path` reads a produced `.nupkg`'s entry list — the post-`Pack` confirmation that what
  shipped matches what was declared.
- `FromStagedDirectory dir` reads a staged folder mirroring the package root.

### Binding it as a test instead

The same check binds in the module's own test project. `assertConformant` raises with the full
report on any violation and is silent when conformant, so it needs no test-framework dependency
(the Build package carries none):

```fsharp
test "the packaged layout is conformant" {
    PackagedModuleConformance.assertConformant layout
}
```

`verify layout` returns the `ShadowLayoutViolation list` when a test wants to assert on a specific
law rather than on the whole report.

### Wildcards

Includes are expanded against the project directory, so the `**\*.fs` pack glob every client-tier
package uses resolves to concrete files and the laws are decided on real paths. When a source list
is built from XML *without* a root directory (`Load.sourceListFromXml label None xml`), wildcard
includes stay unexpanded and the subset law reports each one as **undecidable** rather than
passing on an empty comparison — a check that cannot see the files says so.
