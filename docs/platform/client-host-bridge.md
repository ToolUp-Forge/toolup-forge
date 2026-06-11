# Hosting an external typed-tree UI language — `ClientHostCapabilities`

`ToolUp.Platform.Client`'s module shell renders whatever `ReactElement` a module's view returns, so a UI authored in an external **typed Node-tree language** (any language whose renderer produces a `ReactElement`) already hosts inside the SDK's `/app/*` shell. What an external runtime additionally needs is a way to turn the tree's **typed actions** into ToolUp behaviour. `ClientHostCapabilities` is that seam — four hooks, each a re-exposure of a shipped concrete:

| Capability | Routes to | Behaviour |
|---|---|---|
| `Navigate: SidebarId -> unit` | `NavigationRequest.request` | Shell/sidebar routing — `"ModuleId"` or `"ModuleId/route"` |
| `Notify: ToastIntent -> unit` | `NotificationClient.publishLocal` | A `SystemMessage` toast the shipped `ToastCentre` renders |
| `Dispatch: 'Msg -> unit` | the module's Elmish dispatch | Feeds the module's `update` loop |
| `Call<'r>(call, onSuccess, onError)` | `Cmd.OfRemoting` semantics | Runs a remoting-shaped async through the interceptor chain, mapping the outcome into the module's `Msg` |

The surface is renderer-neutral: nothing in it names any particular tree language. An adapter for language X maps X's action vocabulary onto these four hooks (and, for action *gating*, consults the [action authorizer](action-authorizer.md) before executing).

## Minimum example

```fsharp
open ToolUp.Platform

type Model = { Page: TreePage }
type Msg =
    | GotFigures of Figures
    | Errored of string

let register () : ErasedModule =
    ClientModule.create {
        Init = init
        Update = update
        Name = "Catalogue"
        Icon = "/svg/grid.svg"
    }
    |> ClientHostView.withElementView (fun model dispatch host ->
        // Render the external tree to a ReactElement; route its actions
        // through the host bag.
        MyTreeRuntime.render model.Page {|
            onNavigate = host.Navigate                     // tree's Navigate action
            onCall = fun c -> host.Call(c, GotFigures, _.Message >> Errored)
            onNotify = ToastIntent.info >> host.Notify
            onDispatch = host.Dispatch                     // tree's typed messages
        |})
    |> ClientModule.register
```

`ClientHostView.withElementView` is `withFullWidthView` plus the bag: the view receives `Model`, `dispatch`, and a `ClientHostCapabilities<'Msg>` built from that same dispatch (`ClientHostCapabilities.create dispatch` — usable directly anywhere you already hold a dispatch).

## Semantics worth knowing

- **`Call` executes immediately** (it is not a `Cmd` you return from `update`): the OfRemoting effect — interceptor chain, error-envelope categorisation — runs and the mapped `Msg` arrives via dispatch one timer hop later. External runtimes are action-handler-shaped, not MVU-command-shaped; this is the imperative bridge. Module code that *is* inside `update` should keep using `Cmd.OfRemoting` directly.
- **`Notify` rides the platform notification stream** — the toast renders with the standard `SystemMessageLevel` styling and dismiss behaviour (`Error` sticks until dismissed).
- **Cost when unused: zero.** The bag is constructed per render only by views that opt in; nothing global changes (GP 11 / GP 13).

## See also

- [`docs/platform/modules.md`](modules.md) — the module shell + builder pipeline this seam extends.
- [`docs/platform/action-authorizer.md`](action-authorizer.md) — default-deny gating for the actions a hosted runtime dispatches.
- [`docs/platform/live-sessions.md`](live-sessions.md) — the server-driven sibling (server-held tree, patches over SSE).
- [`docs/migrations/110-client-host-bridge.md`](../migrations/110-client-host-bridge.md).
