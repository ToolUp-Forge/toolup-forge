// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.McpHost.McpCompose

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Compose.Server
open ToolUp.AI.AICompose
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Compose

// ─── The MCP host companion ──────────────────────────────────────────
//
// `McpOptions` holds the host's own settings; `withMcp options` registers
// the companion with `ServerApp.withCompanion`, so it is applied once,
// when the deployment resolves, to the deployment as every builder left
// it. It brings its own DI singletons and mounts its handler onto the
// SDK's route chain through `ComposeExtensions.Handlers`.
//
// It projects the registry of AI tools with their schemas, which exists
// only where the AI tier is composed (`AICompose.withAI`, or
// `RAGCompose.withRAG`, which composes it). A deployment with no AI
// surface has no tool registry, and an MCP endpoint over an empty
// registry is a protocol host with nothing to host.
//
// **GP 13, twice over.** A deployment that never calls `withMcp` is
// byte-for-byte unchanged BY CONSTRUCTION — there is nothing to strip,
// because nothing was ever added. And a deployment that composes the
// companion but sets `NoMcpHost` contributes nothing, which is the
// strip-imports guarantee `PeerCompose` makes for the peer substrate.
//
// **The gate is on the companion's options, not on `ServerConfig`, and
// that is a deliberate departure from the `XxxMode`-field convention.**
// Adding a field to `ServerConfig` retypes that record's compiler-generated
// constructor, which breaks every full-literal construction of it
// (FS0764) and regenerates the Core public-API baseline — a breaking
// change to a released core type, made on behalf of a companion, to
// express something the companion can express itself. The convention
// exists for STORE substrates, which `ServerApp` itself registers and
// which therefore have nowhere else to read a flag from; a companion
// with its own options has somewhere else.
//
// **Two compose-time refusals, both loud, both before anything
// registers.** A composition that has not enabled service accounts has
// no credential for an agent to present, and a composition that declares
// ceilings it cannot count is a composition whose budget silently does
// nothing. Each fails when the deployment resolves, naming the property
// and the remedy, rather than at the first request naming a null.

/// Whether the composed MCP host is live.
///
/// `NoMcpHost` is the strip-imports path: the companion contributes no
/// handler, no DI registration and no middleware. Present so a deployment can carry one
/// composition root across environments and turn the agent surface off
/// in the ones that should not have it, without deleting code.
type McpHostMode =
    | NoMcpHost
    | EnabledMcpHost

/// What the MCP host companion composes.
type McpOptions = {
    /// Whether the host is live. `EnabledMcpHost` from `create` — a
    /// composition that reached for this root wants the endpoint.
    Mode: McpHostMode
    /// Route, reported version, and the per-agent resource envelopes.
    Options: McpHost.McpHostOptions
    /// Where agent tool grants live. `None` composes the blob-backed
    /// default over the deployment's resolved `IBlobStorage`.
    GrantStore: IAgentToolGrantStore option
    /// Where per-agent budget consumption lives. `None` composes the
    /// blob-backed ledger over the same storage — and only
    /// when a budget is actually declared, so a deployment with no
    /// ceilings registers nothing (GP 13).
    BudgetLedger: IBudgetLedger option
}

[<RequireQualifiedAccess>]
module McpOptions =
    /// An MCP host at the default route, no ceilings, and the blob-backed
    /// grant store.
    let create () : McpOptions = {
        Mode = EnabledMcpHost
        Options = McpHost.McpHostOptions.defaults
        GrantStore = None
        BudgetLedger = None
    }

    /// Turn the host on or off without changing anything else.
    let withMcpHost (mode: McpHostMode) (app: McpOptions) : McpOptions = { app with Mode = mode }

    /// Mount the endpoint at a different path.
    let withRoute (route: string) (app: McpOptions) : McpOptions = {
        app with
            Options = { app.Options with Route = route }
    }

    /// Report this deployment's own build identity to connecting agents.
    let withServerVersion (version: string) (app: McpOptions) : McpOptions = {
        app with
            Options = {
                app.Options with
                    ServerVersion = version
            }
    }

    /// The resource envelope every agent gets unless an override says
    /// otherwise.
    let withDefaultPolicy (policy: AgentPolicy) (app: McpOptions) : McpOptions = {
        app with
            Options = {
                app.Options with
                    DefaultPolicy = policy
            }
    }

    /// A per-account override. The named account is governed ENTIRELY by
    /// `policy` — see `McpHostOptions.PolicyOverrides`.
    let withAgentPolicy (accountId: string) (policy: AgentPolicy) (app: McpOptions) : McpOptions = {
        app with
            Options = {
                app.Options with
                    PolicyOverrides = app.Options.PolicyOverrides |> Map.add accountId policy
            }
    }

    /// Supply a grant store — a test double, or a companion over another
    /// substrate.
    let withGrantStore (store: IAgentToolGrantStore) (app: McpOptions) : McpOptions = {
        app with
            GrantStore = Some store
    }

    /// Supply the budget ledger rather than composing the blob-backed
    /// default. The shape a deployment already running compute budgets
    /// uses, so one ledger serves every domain.
    let withBudgetLedger (ledger: IBudgetLedger) (app: McpOptions) : McpOptions = {
        app with
            BudgetLedger = Some ledger
    }

    /// Does this composition declare a BUDGET (as opposed to a rate
    /// limit, which needs a different substrate)?
    let internal declaresBudget (app: McpOptions) : bool =
        not (AgentBudget.isUnrestricted app.Options.DefaultPolicy.Budget)
        || app.Options.PolicyOverrides
           |> Map.exists (fun _ policy -> not (AgentBudget.isUnrestricted policy.Budget))

    /// Refuse a composition whose agents have no credential to present.
    ///
    /// The MCP host authenticates exactly one way: a service
    /// account token on the standard bearer header, validated by the
    /// middleware `ServerConfig.Stores.ServiceAccounts = EnabledServiceAccounts`
    /// registers. Without it no request can ever resolve to an agent, so
    /// every call answers 401 — a deployment that looks composed and
    /// serves nothing. Caught here rather than discovered there.
    let internal enforceServiceAccounts (config: ServerConfig) : unit =
        match config.Stores.ServiceAccounts with
        | NoServiceAccounts ->
            failwith
                "The MCP host authenticates agents with service-account tokens, and this composition has \
                 ServerConfig.Stores.ServiceAccounts = NoServiceAccounts — so no request could ever resolve to an agent \
                 principal and every MCP call would answer 401. Set ServiceAccounts to EnabledServiceAccounts (or \
                 supply an external store) alongside McpCompose.withMcp, or compose McpOptions.withMcpHost NoMcpHost \
                 if the agent surface is deliberately off in this environment."
        | _ -> ()

    /// Refuse a composition that declares budget ceilings it has no way
    /// to count.
    ///
    /// A budget needs an `IBudgetLedger`; the default is blob-backed and
    /// needs `IBlobStorage`. A composition with neither would admit every
    /// call — the fail-open posture both substrates take, and the right
    /// one at REQUEST time — while its author believed a ceiling was in
    /// force. Fail-open at runtime and fail-loud at startup are not in
    /// tension: the first keeps a storage blip from becoming an outage,
    /// the second keeps a composition mistake from becoming a silent
    /// non-control.
    let internal enforceBudgetSubstrate (options: McpOptions) (app: ServerApp) : unit =
        if declaresBudget options && options.BudgetLedger.IsNone && app.Storage.IsNone then
            failwith
                "The MCP host was composed with per-agent budget ceilings, but this composition has neither an \
                 IBudgetLedger (McpOptions.withBudgetLedger) nor an IBlobStorage for the default blob-backed \
                 ledger to use — so nothing could count the consumption and every call would be admitted. Supply \
                 one, or drop the budget from the agent policy."

    /// The DI contribution: the grant store, and the budget ledger when
    /// one is needed.
    let internal services (app: McpOptions) (svc: IServiceCollection) : IServiceCollection =
        match app.GrantStore with
        | Some store -> svc.TryAddSingleton<IAgentToolGrantStore>(store)
        | None ->
            svc.TryAddSingleton<IAgentToolGrantStore>(fun (sp: System.IServiceProvider) ->
                let blobs = sp.GetRequiredService<IBlobStorage>()
                AgentToolGrantStore.BlobAgentToolGrantStore blobs :> IAgentToolGrantStore)

        if declaresBudget app then
            match app.BudgetLedger with
            | Some ledger -> svc.TryAddSingleton<IBudgetLedger>(ledger)
            | None ->
                // `TryAdd`, so a deployment that already composed a ledger
                // for its compute budgets keeps it and both domains share
                // one row store and one set of contention characteristics.
                svc.TryAddSingleton<IBudgetLedger>(fun (sp: System.IServiceProvider) ->
                    let blobs = sp.GetRequiredService<IBlobStorage>()
                    BlobBudgetLedger blobs :> IBudgetLedger)

        svc

/// The MCP host companion's identity.
let private owner: ComponentId = ComponentId.forCompanionSlot "ToolUp.AI.McpHost"

/// Apply the MCP host onto `app`: the route and the grant store / budget
/// ledger registrations, after the two compose-time refusals. `NoMcpHost`
/// returns `app` unchanged.
///
/// **Advanced.** Consumers compose the host with `withMcp`. Hidden from
/// IntelliSense via `[<EditorBrowsable>]`.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
let composeMcp (options: McpOptions) (app: ServerApp) : ServerApp =
    match options.Mode with
    | NoMcpHost -> app
    | EnabledMcpHost ->
        McpOptions.enforceServiceAccounts (ServerApp.effectiveConfig app)
        McpOptions.enforceBudgetSubstrate options app

        let baseExt = app.Extensions

        let mergedExt: ComposeExtensions = {
            baseExt with
                Handlers = baseExt.Handlers @ [ McpHost.routes options.Options ]
        }

        { app with Extensions = mergedExt }
        |> ServerApp.addContribution (Contribution.service owner (McpOptions.services options))

/// **Sets** the MCP host companion: composed once, when the deployment
/// resolves, onto the deployment as every builder left it. Compose the AI
/// tier it projects beside it:
///
///     ServerApp.empty
///     |> ServerApp.withConfig config
///     |> AICompose.withAI aiOptions
///     |> McpCompose.withMcp (McpOptions.create () |> McpOptions.withServerVersion "1.4.0")
///     |> ServerApp.run
let withMcp (options: McpOptions) (app: ServerApp) : ServerApp =
    ServerApp.withCompanion owner (composeMcp options) app