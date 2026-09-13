// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.McpHost.McpCompose

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Server
open ToolUp.AI.AICompose

// ─── Phase 489 — the MCP host composition root ───────────────────────
//
// `McpServerApp` wraps an `AIServerApp` the way `PeerServerApp` wraps a
// `ServerApp`: it adds the companion's own `with*` helpers, brings its
// own DI singletons, and mounts its handler onto the SDK's route chain
// through `ComposeExtensions.Handlers`.
//
// It wraps the AI tier rather than the base one because the thing it
// projects — the registry of AI tools with their schemas — exists only
// once `composeAI` has run. A deployment with no AI surface has no tool
// registry, and an MCP endpoint over an empty registry is a protocol
// host with nothing to host.
//
// **GP 13, twice over.** A deployment that never calls `McpServerApp.run`
// is byte-for-byte unchanged BY CONSTRUCTION — there is nothing to strip,
// because nothing was ever added. And a deployment that composes the
// companion but sets `NoMcpHost` short-circuits to `AIServerApp.run` of
// the same base, which is the strip-imports guarantee `PeerCompose`
// makes for the peer substrate.
//
// **The gate is on the companion record, not on `ServerConfig`, and that
// is a deliberate departure from the `XxxMode`-field convention.** Adding
// a field to `ServerConfig` retypes that record's compiler-generated
// constructor, which breaks every full-literal construction of it
// (FS0764) and regenerates the Core public-API baseline — a breaking
// change to a released core type, made on behalf of a companion, to
// express something the companion can express itself. The convention
// exists for STORE substrates, which `ServerApp` itself registers and
// which therefore have nowhere else to read a flag from; a companion
// with its own composition root has somewhere else.
//
// **Two compose-time refusals, both loud, both before anything
// registers.** A composition that has not enabled service accounts has
// no credential for an agent to present, and a composition that declares
// ceilings it cannot count is a composition whose budget silently does
// nothing. Each fails at `run` naming the property and the remedy,
// rather than at the first request naming a null.

/// Whether the composed MCP host is live.
///
/// `NoMcpHost` is the strip-imports path: `run` short-circuits to
/// `AIServerApp.run` of the same base, contributing no handler, no DI
/// registration and no middleware. Present so a deployment can carry one
/// composition root across environments and turn the agent surface off
/// in the ones that should not have it, without deleting code.
type McpHostMode =
    | NoMcpHost
    | EnabledMcpHost

/// Record form of the MCP compose arguments.
type McpServerApp = {
    /// The composed AI server the host projects.
    Base: AIServerApp
    /// Whether the host is live. `EnabledMcpHost` from `create` — a
    /// composition that reached for this root wants the endpoint.
    Mode: McpHostMode
    /// Route, reported version, and the per-agent resource envelopes.
    Options: McpHost.McpHostOptions
    /// Where agent tool grants live. `None` composes the blob-backed
    /// default over the deployment's resolved `IBlobStorage`.
    GrantStore: IAgentToolGrantStore option
    /// Where per-agent budget consumption lives. `None` composes the
    /// blob-backed Phase 689 ledger over the same storage — and only
    /// when a budget is actually declared, so a deployment with no
    /// ceilings registers nothing (GP 13).
    BudgetLedger: IBudgetLedger option
}

[<RequireQualifiedAccess>]
module McpServerApp =
    /// Wrap a composed AI server with an MCP host at the default route,
    /// no ceilings, and the blob-backed grant store.
    let create (ai: AIServerApp) : McpServerApp = {
        Base = ai
        Mode = EnabledMcpHost
        Options = McpHost.McpHostOptions.defaults
        GrantStore = None
        BudgetLedger = None
    }

    /// Turn the host on or off without changing anything else.
    let withMcpHost (mode: McpHostMode) (app: McpServerApp) : McpServerApp = { app with Mode = mode }

    /// Mount the endpoint at a different path.
    let withRoute (route: string) (app: McpServerApp) : McpServerApp = {
        app with
            Options = { app.Options with Route = route }
    }

    /// Report this deployment's own build identity to connecting agents.
    let withServerVersion (version: string) (app: McpServerApp) : McpServerApp = {
        app with
            Options = {
                app.Options with
                    ServerVersion = version
            }
    }

    /// The resource envelope every agent gets unless an override says
    /// otherwise.
    let withDefaultPolicy (policy: AgentPolicy) (app: McpServerApp) : McpServerApp = {
        app with
            Options = {
                app.Options with
                    DefaultPolicy = policy
            }
    }

    /// A per-account override. The named account is governed ENTIRELY by
    /// `policy` — see `McpHostOptions.PolicyOverrides`.
    let withAgentPolicy (accountId: string) (policy: AgentPolicy) (app: McpServerApp) : McpServerApp = {
        app with
            Options = {
                app.Options with
                    PolicyOverrides = app.Options.PolicyOverrides |> Map.add accountId policy
            }
    }

    /// Supply a grant store — a test double, or a companion over another
    /// substrate.
    let withGrantStore (store: IAgentToolGrantStore) (app: McpServerApp) : McpServerApp = {
        app with
            GrantStore = Some store
    }

    /// Supply the budget ledger rather than composing the blob-backed
    /// default. The shape a deployment already running compute budgets
    /// uses, so one ledger serves every domain.
    let withBudgetLedger (ledger: IBudgetLedger) (app: McpServerApp) : McpServerApp = {
        app with
            BudgetLedger = Some ledger
    }

    // ── delegating helpers ───────────────────────────────────────────
    //
    // The flat-superset shape `AIServerApp` takes over `ServerApp`: a
    // composition that reached this root should not have to unwrap to
    // reach the tier below it.

    /// Lift an `AIServerApp` transformation onto the wrapped base.
    let over (f: AIServerApp -> AIServerApp) (app: McpServerApp) : McpServerApp = { app with Base = f app.Base }

    /// Does this composition declare a BUDGET (as opposed to a rate
    /// limit, which needs a different substrate)?
    let private declaresBudget (app: McpServerApp) : bool =
        not (AgentBudget.isUnrestricted app.Options.DefaultPolicy.Budget)
        || app.Options.PolicyOverrides
           |> Map.exists (fun _ policy -> not (AgentBudget.isUnrestricted policy.Budget))

    /// Refuse a composition whose agents have no credential to present.
    ///
    /// The MCP host authenticates exactly one way: a Phase 527 service
    /// account token on the standard bearer header, validated by the
    /// middleware `ServerConfig.ServiceAccounts = EnabledServiceAccounts`
    /// registers. Without it no request can ever resolve to an agent, so
    /// every call answers 401 — a deployment that looks composed and
    /// serves nothing. Caught here rather than discovered there.
    let private enforceServiceAccounts (app: McpServerApp) : unit =
        match app.Base.Base.Config.ServiceAccounts with
        | NoServiceAccounts ->
            failwith
                "The MCP host authenticates agents with service-account tokens, and this composition has \
                 ServerConfig.ServiceAccounts = NoServiceAccounts — so no request could ever resolve to an agent \
                 principal and every MCP call would answer 401. Set ServiceAccounts to EnabledServiceAccounts (or \
                 supply an external store) alongside McpServerApp.run, or compose McpServerApp.withMcpHost NoMcpHost \
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
    let private enforceBudgetSubstrate (app: McpServerApp) : unit =
        if declaresBudget app && app.BudgetLedger.IsNone && app.Base.Base.Storage.IsNone then
            failwith
                "The MCP host was composed with per-agent budget ceilings, but this composition has neither an \
                 IBudgetLedger (McpServerApp.withBudgetLedger) nor an IBlobStorage for the default blob-backed \
                 ledger to use — so nothing could count the consumption and every call would be admitted. Supply \
                 one, or drop the budget from the agent policy."

    /// The DI contribution: the grant store, and the budget ledger when
    /// one is needed.
    let private services (app: McpServerApp) (svc: IServiceCollection) : IServiceCollection =
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

    /// Lift this composition onto the `ServerApp` the SDK runs.
    ///
    /// Exposed for the same reason `composeAI` is: a consumer building a
    /// further companion on top needs the composed result without driving
    /// it. Consumers should use `run`.
    let compose (app: McpServerApp) : ServerApp =
        match app.Mode with
        | NoMcpHost -> composeAI app.Base
        | EnabledMcpHost ->
            enforceServiceAccounts app
            enforceBudgetSubstrate app

            let composed = composeAI app.Base
            let baseExt = composed.Extensions
            let mcpServices = services app

            let mergedExt: ComposeExtensions = {
                baseExt with
                    Handlers = baseExt.Handlers @ [ McpHost.routes app.Options ]
                    ServiceConfig =
                        match baseExt.ServiceConfig with
                        | None -> Some mcpServices
                        | Some baseFn -> Some(fun s -> mcpServices (baseFn s))
            }

            { composed with Extensions = mergedExt }

    /// Compose and run.
    ///
    /// `NoMcpHost` is byte-for-byte `AIServerApp.run app.Base` — same
    /// composition, same handlers, same services, in the same order.
    let run (app: McpServerApp) : int = compose app |> ServerApp.run