# ToolUp.SAFER — minimal F# full-stack starter

A minimal F# full-stack starter for the [ToolUp Platform SDK](https://github.com/ToolUp-Forge/toolup-forge), with thanks to the [Compositional IT](https://safe-stack.github.io/) team — SAFER mirrors the SAFE Stack get-started experience for F# developers arriving from that template, and demonstrates what ToolUp brings to its MVU and RPC layers: `update` returns effects as values that the `Fuaran.MVU` runtime performs, and remote calls go through the in-tree ToolUp.Remoting.

> **Not affiliated with [SAFEr.Template](https://github.com/Dzoukr/SAFEr.Template)**, Dzoukr's independent SAFE Stack variant on NuGet — different scope, different stack assumptions, different maintainer. This template is published as `ToolUp.Templates.SAFER` and installed as `dotnet new toolup-safer`.

## What you get

A runnable F# full-stack app — **Tiny Chat** — in about 200 lines of consumer code:

- Single in-memory chat channel, no auth, no users, no persistence
- Open the page in two browser tabs, type a message in one, see it in the other (~2 s latency via polling)
- Optimistic UI: your own message appears instantly, reconciles with the server's confirmed copy
- Send-message is a `RemoteEffect.callWithRetry` value with a 3-attempt exponential-backoff retry policy
- A red error banner with the structured `ChatError` reason + correlation id appears when retries exhaust
- Process restart wipes the message ring buffer — anonymity-by-design

## When to use SAFER vs `platformsdk-solution`

SAFER is **one option among several** for starting a ToolUp Platform app — not the recommended starter. Pick by use case:

| Use case | Template |
|---|---|
| "I've used SAFE Stack; show me the ToolUp shape" | **`dotnet new toolup-safer`** |
| "Learn the SDK end-to-end with the smallest demo I can read in one sitting" | **`dotnet new toolup-safer`** |
| "Production multi-tenant app — auth, scopes, RBAC, audit, persistence, every companion" | `dotnet new platformsdk-solution` |
| "Add a second app pair to an existing platformsdk-solution" | `dotnet new platformsdk-application` |
| "Just scaffold one analysis module against my existing solution" | `dotnet new platformsdk-module` |

SAFER is anonymous + in-memory by design. For multi-tenant + auth + persistence, use `platformsdk-solution`.

## Quick start

```powershell
# Scaffold + launch
dotnet new toolup-safer -o MyApp
cd MyApp
pwsh ./run.ps1
```

`run.ps1` builds the solution, transpiles F# → JS via Fable, starts the Giraffe server on `:5000` and Vite on `:8080`, then opens your browser. Open a second tab at `http://localhost:8080/` to see chat updates flow live between tabs.

## SAFE Stack ↔ SAFER comparison

If you've used SAFE Stack, these are the patterns that changed:

| SAFE Stack | SAFER (ToolUp.Platform) |
|---|---|
| Saturn `application { ... }` DSL | `ServerApp.empty \|> ServerApp.withConfig config \|> ServerApp.withLogger logger \|> ServerApp.run` |
| `Fable.Elmish` PackageReference | Replaced by the `Fuaran.MVU` runtime and its React adapter, which `ToolUp.Platform.Client` references — no PackageReference of your own |
| `Fable.Remoting.{Client,Server,Json,Giraffe}` PackageReferences | Replaced by `ToolUp.Remoting.{Client,Server,Json,Giraffe}`, which ship inside `ToolUp.Platform.{Client,Server}` — no PackageReferences of their own |
| Manual JSON converter wiring on the server | The `ToolUp.Remoting.Json` converter set ships inside `ToolUp.Platform.Server`; nothing to register |
| Manual `RemotingBodyNormalizationMiddleware` registration | Folded into the dispatcher itself; `unit -> Async<T>` API methods just work |
| `update : Msg -> Model -> Model * Cmd<Msg>` | `update : Msg -> Model -> Model * Step<Msg, RemoteEffect<Msg>> list` — `Do` an effect value, or `Then` a follow-up message; a test compares the list |
| `Cmd.OfAsync.either api.X arg ok err` | `Do(RemoteEffect.call "Api.X" api.X arg reply)` — one reply, folded into one message |
| Transient transport-failure retry boilerplate | `RemoteEffect.callWithRetry "Api.X" retryPolicy api.X arg reply` with `RetryPolicy { MaxAttempts; InitialDelayMs; BackoffMultiplier; MaxDelayMs }` (retry-as-data per GP 12 rule 3) |
| `let mutable shellDispatch : (Msg -> unit) option = None` capture pattern | The runtime owns dispatch; a long-lived source is a `Subscription` the model lists as a resource, started and stopped by the runtime |
| Ad-hoc `Cmd.ofEffect (fun d -> SomeClient.subscribe ... \|> ignore)` (leaks on hot-reload) | The shell's long-lived sources are `Subscription` resources the runtime starts once and stops at unmount; a module hears them by topic through `ClientModule.withEventSubscription` |
| `(string * exn) -> unit` upstream `onError` | Structured `ErrorContext` (phase, module id, correlation id, exception) through `OnElmishError` |

The full list of fork additions and the source-compat guarantee is at the [forge README's "In-tree client + transport forks" section](https://github.com/ToolUp-Forge/toolup-forge#in-tree-client--transport-forks).

## Walking tour — what the demo exercises

Tiny Chat is intentionally small — every line earns its place by demonstrating one specific primitive. Open the files in this order:

### 1. `src/Modules/Chat/SharedTypes.fs` — the wire contract

`Message`, `SendMessageRequest`, `ChatError`, `ChatApi`. The classical ToolUp.Remoting shape: a record with `'TInput -> Async<'TOutput>` methods. Errors are a typed DU (`EmptyBody | EmptyName | BodyTooLong`) crossing the wire as a `Result<Message, ChatError>` — the client matches the union to render a classified red banner, not a generic "something went wrong" string. This is `ToolUp.Remoting`'s typed-error pattern in its minimal form.

### 2. `src/Modules/Chat/Server.fs` — the handler

A `ConcurrentQueue<Message>` size-capped at 50. `sendMessage` validates the request body, enqueues, and returns `Result<Message, ChatError>`. `listMessages` returns the buffer contents sorted by time. Total: ~40 lines. The server-side composition root in `src/MyApp-Server/Server.fs` doesn't need to know about chat — `ServerApp.run` discovers the API via the `Chat.fsproj` project reference and auto-mounts it at `/api/IChatApi/<MethodName>`.

### 3. `src/Modules/Chat/ClientModel.fs` — the load-bearing client logic

This is where SAFER earns its keep. Read these primitives in order:

- **Module-level proxy** (`let private chatApi: ChatApi = Api.makeProxy<ChatApi> (customOptions = UserSession.withRequestHeaders)`) — built once at module load. Identity / CSRF / correlation-id headers attach at *send* time via the SDK's request-guard. See [`docs/platform/client-remoting-proxies.md`](https://github.com/ToolUp-Forge/toolup-forge/blob/main/docs/platform/client-remoting-proxies.md) for why this pattern is canonical and per-call construction is a regression.
- **`RetryPolicy`** (`sendRetryPolicy = { MaxAttempts = 3; InitialDelayMs = 250; BackoffMultiplier = 2.0; MaxDelayMs = 2_000 }`) — declared as data, not as a callback. Tunable per-call-site without touching transport code.
- **`RemoteEffect.callWithRetry "ChatApi.SendMessage" sendRetryPolicy chatApi.SendMessage request reply`** — an effect value `update` returns as `Do send`; the runtime performs it with exponential-backoff retry on transient transport failure, and its one reply delivers either a typed `ChatError` (server rejected the message) or a transport `exn` (network blip after retries). The `update`'s `SendFailed` arm classifies both into the structured `ErrorBanner` record.
- **`RemoteEffect.call "ChatApi.ListMessages" chatApi.ListMessages () reply`** — the no-retry variant for read paths. Read failures don't trip the red banner (transient errors clear on the next poll); only write failures do.
- **Optimistic UI** — `SendRequested` appends an `OptimisticMessage` to the local list immediately, returns the retry-policied send as an effect. `SendSucceeded` removes the optimistic row and inserts the server-confirmed `Message`. `SendFailed` removes the optimistic row and shows the banner. One field in the model, two branches in `update`.

### 4. `src/Modules/Chat/ClientView.fs` — the view

A Feliz component. Two text inputs (display name, message body), a Send button, a polling-driven message list, and a conditional red error banner. ~120 lines including the polling-timer setup (`React.useEffectOnce` + `setInterval`). Polling at 2 s is the v1 simplification — see "How to extend" below for the SSE upgrade.

### 5. `src/MyApp-Client/Client.fs` — the entry point

Three lines of code under the imports. `Client.run config modules` is the SDK shell's high-level entry — it mounts the shell on the `Fuaran.MVU` runtime through its React adapter, lists the shell's own subscriptions as resources, routes errors to the reporter, and keeps the model across a hot reload on the consumer's behalf. The consumer-facing surface is `init`/`update`/`view` per module + the module's `register ()` factory. Everything else is the shell's job.

## What the SDK shell handles on your behalf

SAFER's user code touches the **consumer-facing** primitives: effect values in a step list, optimistic state, typed error DUs. The **shell-level** work is done by `Client.run` on your behalf:

- **One runtime, one dispatch.** The shell mounts on `Fuaran.MVU`; a message's follow-ups run before the next queued message, each effect is performed after the model it came from is committed, and the view renders once per drain.
- **Resources, not ad-hoc subscriptions.** The shell's long-lived sources (navigation requests, module events, the notifications stream) are `Subscription` resources: the runtime starts each once and stops each at unmount — no zombie `EventSource` in DevTools. A module hears them through `ClientModule.withEventSubscription` instead of starting its own.
- **Structured errors.** A throwing `update`, a failing effect, a resource that fails to start and a throwing view each reach the `OnElmishError` reporter as an `ErrorContext` naming its phase; a view that throws after a good render keeps the last good tree on screen.

These run every time you run `pwsh ./run.ps1`. A non-shell client embedded in a larger React app mounts its own component with `Fuaran.MVU.React` and `RemoteEffect.interpreter`; `samples/MinimalClient` in the forge repository shows the whole mount.

## How to extend

SAFER is deliberately minimal; everything below is a natural next step but explicitly out of scope for the starter:

- **SSE live updates** instead of polling — wire `INotificationChannel.Publish` in the server's `sendMessage` and `NotificationClient.subscribe` on the client. Drops the 2 s polling latency to ~milliseconds. The SDK ships `NotificationClient` with reconnection + per-event-kind dispatch; see [`docs/platform/`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/docs/platform) for the canonical wiring.
- **Persistence** — swap `ConcurrentQueue` for an `IEntityStore` (Phase 19) over `IBlobStorage`. Process restart no longer wipes; multi-instance deployments need it.
- **Multiple rooms / channels** — extend the `ChatApi` with a `ChannelId` parameter and partition the buffer by channel.
- **Auth / users** — change `TOOLUP_PLATFORM_SURFACES=anonymous` to `individual` or `team` in `run.ps1`, register an `IAuthProvider` companion (Clerk, OIDC, Entra). The chat module's API surface stays unchanged; the SDK's `AccessContext` flows through every handler.
- **Typing indicators / read receipts / message edits / message deletion / moderation** — every "real chat app" feature. SAFER's `Message` shape is intentionally minimal; extend the record + the `ChatApi` surface.

If any of these grow beyond "a small addition to SAFER", that's the signal to graduate to `dotnet new platformsdk-solution`.

## Project layout

```
.
├── MyApp.sln                       — solution file (3 fsproj + 1 module)
├── Directory.Build.props           — common MSBuild props (CPM enabled)
├── Directory.Packages.props        — coordinated ToolUp.Sdk version pin
├── global.json                     — .NET 10 SDK pin
├── nuget.config                    — nuget.org + local feed
├── run.ps1                         — `pwsh ./run.ps1` builds + launches
├── README.md                       — this file
└── src/
    ├── Modules/
    │   └── Chat/                   — the chat module (4-file convention)
    │       ├── SharedTypes.fs         — wire contract: Message, ChatApi, ChatError
    │       ├── Server.fs              — in-memory ring buffer + handler
    │       ├── ClientModel.fs         — MVU Model + Msg + init + update
    │       ├── ClientView.fs          — Feliz view + register ()
    │       ├── Chat.fsproj            — server-side fsproj
    │       └── Chat.Client.props      — MSBuild props injected into client
    ├── MyApp-Server/
    │   ├── Server.fs               — composition root (~20 lines)
    │   └── MyApp-Server.fsproj
    └── MyApp-Client/
        ├── Client.fs               — entry point (~10 lines)
        ├── MyApp-Client.fsproj
        ├── package.json
        ├── vite.config.mts
        ├── vite-fable-tailwind.mjs  — canonical Vite plugin: Tailwind scans Fable's output/
        ├── vite-fuaran-content.mjs  — canonical Vite plugin: resolves @nuget/ stylesheet imports
        ├── index.html
        ├── index.css               — the canonical styling block, then your own rules
        └── fuaran-bridge.css       — Fuaran-UI tokens answered with the shell's
```

SAFER is minimal in features, not in styling: it renders the same SDK shell as every other
template, so its client carries the same canonical Tailwind block, pins and Vite plugins
(`client-styling/tailwind/` in the forge repository). The shell draws with its utility classes and
tokens, and a Fuaran-UI tree takes the app's brand colour. Re-brand by overriding `--color-brand`
(and the other `--color-*` values) in a `:root` after the block's end marker in `index.css`.

## Versioning + compatibility

SAFER pins to `ToolUp.Sdk` `TOOLUP_SDK_VERSION` via the `Directory.Packages.props` `<ToolUpSdkVersion>` property — one line bump moves every transitive `ToolUp.*` package together. SemVer-on-`0.x` policy: minor bumps may include breaking changes; patch bumps are non-breaking. Re-scaffolding from `dotnet new toolup-safer` after an SDK release is a clean way to refresh the template's MSBuild + Vite scaffolding alongside the SDK; the chat module's source carries forward unchanged.

## Contributing + license

[Apache 2.0](https://github.com/ToolUp-Forge/toolup-forge/blob/main/LICENSE). DCO `Signed-off-by:` required on every contribution to forge. See the [contribution guide](https://github.com/ToolUp-Forge/toolup-forge/blob/main/CONTRIBUTING.md), [code of conduct](https://github.com/ToolUp-Forge/toolup-forge/blob/main/CODE_OF_CONDUCT.md), and [security disclosure](https://github.com/ToolUp-Forge/toolup-forge/blob/main/SECURITY.md).
