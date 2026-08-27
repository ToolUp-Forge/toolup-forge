# ToolUp.AI.SampleClientTool.Client

Apache-2.0 reference companion — Fable browser-side handler for the sample client-resident-tool calculator. **Reference-only**: not a production companion; the tool body is a trivial arithmetic operation chosen so the sample's job is unambiguously to exercise the seam, not to model any real domain.

## Why this exists

Phase 46.B (2026-05-22) — the SDK keeps the `IClientToolAuthorizer` substrate seam + `ClientToolDispatch` round-trip whether or not an external companion is composed against it. A seam without in-tree consumers can drift invisibly until the next external companion breaks at integration time. This sample is the smallest possible in-tree consumer — it gives the dispatch round-trip a permanent compose-clean smoke test and serves as the second binding subject for the Phase 46.A `IClientToolDispatchContract` portability pack.

## The full round-trip in 30 seconds

```
┌──────────────┐                                      ┌──────────────┐
│ Agent loop   │  1. emits ClientToolInvoke SSE       │ Browser      │
│ (server)     │ ───────────────────────────────────► │ SampleHandler│
│              │                                      │              │
│              │                              ┌───────│ - decode JSON│
│              │                              │       │ - compute    │
│              │                              │       │ - serialise  │
│              │  2. POST /api/ai/tool-result │       └──────────────┘
│              │ ◄────────────────────────────┘
│              │
│ ───►         │  3. agent loop's TCS resolves;
│ model        │     `Result` reaches the chat
└──────────────┘
```

Step 0 (not shown): `IClientToolAuthorizer` is consulted server-side *before* step 1. A `Deny` short-circuits the whole flow — no SSE emit, the model gets a typed `Denied` tool-result, an audit row lands in `IEventStore`. See [`docs/ai/extending.md`](../../../docs/ai/extending.md) §"Client-resident tool authorization contract".

## Wiring a new client-resident-tool companion (≤10 min)

Follow these four steps to write your own. The sample is the smallest possible reference; a real companion would extend each step but the shape doesn't change.

### 1. Define the wire-format records (Core tier — Fable + .NET shared)

```fsharp skip=fragment
// SampleToolTypes.fs
namespace MyCompany.AITool

[<Literal>]
let MyToolName = "my_company.my_tool"

type MyRequest = { ... }
type MyResponse = { ... }

module MyOps =
    let compute (req: MyRequest) : MyResponse = ...
```

Both server and client reference this Core project so the wire shape is exactly one definition.

### 2. Register the tool with `AIServerApp` (Server tier)

```fsharp skip=fragment
// Compose.fs (server side)
open ToolUp.AI
open MyCompany.AITool

let toolDefinition: AIToolDefinition = {
    Name = MyToolName
    Description = "..."
    Parameters = [ ... ]
    SourceModule = "my_company"
    EmitsActions = None
    Location = ClientResident  // ← this is the load-bearing line
    Surface = Both
}

let private clientResidentStub _ctx _argsJson = async {
    return failwith "ClientResident executor must not run server-side"
}

let register (app: AIServerApp) : AIServerApp = {
    app with Base = { app.Base with AITools = app.Base.AITools @ [ toolDefinition, clientResidentStub ] }
}
```

For production deployments add `registerWithPolicy` that folds an `IClientToolAuthorizer` into the composition root's `ServiceConfig` — see [`Server/Compose.fs`](../ToolUp.AI.SampleClientTool.Server/Server/Compose.fs).

### 3. Implement the browser-side handler (Client tier — Fable)

```fsharp skip=fragment
// SampleHandler.fs
module MyCompany.AITool.Browser.MyHandler

open Fable.SimpleJson
open ToolUp.AI.Client
open MyCompany.AITool

let private handler (_ctx: ClientToolRuntime.ClientToolContext, argsJson: string) : Async<string> = async {
    let request = Json.parseAs<MyRequest> argsJson
    let response = MyOps.compute request   // your domain logic
    return Json.serialize response
}

let install () : unit =
    ClientToolRuntime.register MyToolName handler
```

The tuple input form (`ClientToolContext * string`) is non-negotiable — Fable v5's `register` mis-curries 2-arg functions stored in a `Dictionary` (the docstring on `ClientToolExecutor` in `ClientToolRuntime.fs` carries the explanation).

### 4. Compose

Server side:
```fsharp skip=fragment
AIServerApp.create factory configStore
|> AIServerApp.withConfig config
|> MyCompany.AITool.Server.Compose.register
|> AIServerApp.run
```

Client side (in your shell's boot sequence, before `AIClientConfig.run`):
```fsharp skip=fragment
MyCompany.AITool.Browser.MyHandler.install ()
```

## Validate against the SDK contract packs

Bind both Phase 46 packs to your authorizer + handler in your companion's test project:

```fsharp skip=fragment
open ToolUp.Platform.Tests.Contracts

let authorizerTests =
    IClientToolAuthorizerContract.tests {
        Name = "MyCompanyAuthorizer"
        Authorizer = MyCompanyAuthorizer(myPolicy) :> IClientToolAuthorizer
        AllowedCall = (MyToolName, "{}", Some "MyModule", Some "/page")
        DeniedCall = ("blocked.tool", "{}", Some "MyModule", Some "/page")
    }

let dispatchTests =
    IClientToolDispatchContract.tests {
        Name = "MyCompanyAuthorizer + handler"
        Authorizer = MyCompanyAuthorizer(myPolicy) :> IClientToolAuthorizer
        AllowedToolName = MyToolName
        DeniedToolName = "blocked.tool"
        Simulator = fun _evt -> Some "..."  // result the sim returns
    }
```

The packs cover identity-by-value, idempotency, never-throws on malformed input, parallel-call independence (authorizer pack), and the full Allow / Deny round-trip including audit-row emission (dispatch pack). See [`src/ToolUp.AI/TECHNICAL_GUIDE.md`](../../ToolUp.AI/TECHNICAL_GUIDE.md) §"Client-resident companion authoring" for the full walkthrough.

## Beyond a calculator — what a real companion would do differently

The sample's handler runs pure arithmetic and returns. A real client-resident-tool companion typically:

- **Dispatches a typed `Msg` into a module's MVU** (the AI-controllable-field pattern — a `_platform.ui.set_field`-shaped tool walks the active module's state through the shell's existing dispatch path). The handler then returns a confirmation JSON describing what changed.
- **Reads in-flight client state** (active grid selection, in-progress workflow step) to honour the user's "currently viewing" context — `ClientToolContext` carries `ActiveModule` and `ActivePage` for exactly this reason.
- **Mutates state and is therefore subject to the trust boundary** — a deployment composes this companion via `registerWithPolicy` with an authorizer that gates the mutation by module / field / button. The authorizer is consulted server-side **before** the SSE emit, so a denied call never reaches the handler.

The seam contract is the same regardless of complexity — the contract packs above apply unchanged.

## See also

- [`Core/README.md`](../ToolUp.AI.SampleClientTool.Core/README.md) — shared types.
- [`Server/README.md`](../ToolUp.AI.SampleClientTool.Server/README.md) — server-side compose.
- [`docs/ai/extending.md`](../../../docs/ai/extending.md) §"Client-resident tool authorization contract" — seam invariants + portability packs.
- [`src/ToolUp.AI/TECHNICAL_GUIDE.md`](../../ToolUp.AI/TECHNICAL_GUIDE.md) §"Client-resident companion authoring" — full walkthrough.
- [`src/ToolUp.Platform.Tests/Contracts/IClientToolAuthorizerContract.fs`](../../ToolUp.Platform.Tests/Contracts/IClientToolAuthorizerContract.fs) and [`IClientToolDispatchContract.fs`](../../ToolUp.Platform.Tests/Contracts/IClientToolDispatchContract.fs) — contract packs.
