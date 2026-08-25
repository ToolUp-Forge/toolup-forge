// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.SampleClientTool.Server.Compose

// ─── Phase 46.B — Sample companion server compose ────────────────────
//
// Registers the sample calculator as a `ClientResident` AI tool —
// stripped to the minimum: one tool, no module translation layer,
// no policy validator. The point is to give a future
// client-resident-companion author a worked example they can copy
// and extend.
//
// Reference-only — not a production companion. Two reasons:
//   1. No authorizer is wired by default. The seam-absent default in
//      `AIAgentEngine.runAgentLoop` is `Allow`, so this companion
//      composed without `registerWithPolicy` lets the AI fire the
//      tool unrestricted. Fine for a docs / sample / contract-pack
//      binding subject; not fine for any real deployment.
//   2. The tool body itself is a calculator. A real companion would
//      be wired to a domain-specific surface.

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Server
open ToolUp.AI
open ToolUp.AI.AICompose
open ToolUp.AI.SampleClientTool

/// Tool definition the agent loop sees. `Location = ClientResident`
/// means the executor below never runs — the loop emits a
/// `ClientToolInvoke` SSE event and the browser-side handler in
/// `ToolUp.AI.SampleClientTool.Client.SampleHandler` runs the
/// arithmetic and POSTs the result back.
let toolDefinition: AIToolDefinition = {
    Name = SampleCalcToolName
    Description =
        "Compute a simple arithmetic operation between two numbers. "
        + "Reference-only sample tool — illustrates the client-resident dispatch path."
    Parameters = [
        {
            Name = "op"
            Type = "string"
            Description = "One of '+', '-', '*', '/'."
            Required = true
            Default = None
        }
        {
            Name = "a"
            Type = "number"
            Description = "Left operand."
            Required = true
            Default = None
        }
        {
            Name = "b"
            Type = "number"
            Description = "Right operand."
            Required = true
            Default = None
        }
    ]
    SourceModule = "_sample"
    EmitsActions = None
    Location = ClientResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
}

/// Server-side stub executor. Never runs in a correctly-wired
/// deployment — the agent loop branches on `ToolLocation =
/// ClientResident` and dispatches via SSE before reaching here.
/// Fails loudly so a regression in the dispatch wiring (which would
/// be a forge-internal bug, not a companion bug) surfaces visibly.
let private clientResidentStub _ctx _argsJson = async {
    return
        failwith
            "ClientResident sample tool executor was called server-side. The agent loop's per-tool dispatch in AIAgentEngine.runAgentLoop should branch on ToolLocation before reaching here — this is a bug in the dispatch wiring."
}

/// Register the sample tool with no authorizer — seam-absent
/// behaviour is `Allow`. Suitable for the contract-pack binding
/// subject and for the InProcess round-trip test. Not suitable for
/// any deployment that doesn't want the model to call the tool
/// unrestricted.
let register (app: AIServerApp) : AIServerApp = {
    app with
        Base = {
            app.Base with
                AITools = app.Base.AITools @ [ toolDefinition, clientResidentStub ]
        }
}

/// Register the sample tool with an operator-supplied
/// `IClientToolAuthorizer`. The authorizer is folded into the
/// composition root's `ServiceConfig` so the agent loop's DI
/// resolution finds it; the tool definition is appended to
/// `Base.AITools` the same way as `register`.
let registerWithPolicy (authorizer: IClientToolAuthorizer) (app: AIServerApp) : AIServerApp =
    let svc (s: IServiceCollection) =
        s.AddSingleton<IClientToolAuthorizer>(authorizer)

    let exts = app.Base.Extensions

    let merged = {
        exts with
            ServiceConfig =
                match exts.ServiceConfig with
                | None -> Some svc
                | Some baseFn -> Some(fun s -> svc (baseFn s))
    }

    register app |> AIServerApp.withExtensions merged