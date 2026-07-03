// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 276 — hosted-tree navigation/route contract (client) ────────
//
// The CLIENT half of the route contract. Phase 267 lets a hosted tree
// drive a multi-page module and Phase 110's `Navigate` reaches the shell
// router — but the module's pages had no stable URL, no deep link, no
// back-button restore. `IHostRouteContract` closes that: a hosted module
// declares its routes (the neutral Core `HostRoute` list), and this maps a
// concrete path to a forge `NavigationRequest` (deep-link / back-button)
// AND restores the route's param state via the Phase 264 projection.
//
// The SSR half (`ToolUp.PublicRendering.HostRouteRegistration`) registers
// the SAME `HostRoute` list into the Phase 111 route table so each page is
// crawlable. Both halves read one neutral route type (Core `HostRoute`);
// no tree-language type appears (GP 1). A pipeline that declares no routes
// constructs no contract and pays nothing (GP 13).

/// A hosted module's client-side route contract: the neutral routes it
/// declares plus a first-match path resolver. Built from a `HostRoute` list
/// via `HostRouteContract.create`.
type IHostRouteContract =
    /// The routes this hosted module declares (path pattern + sidebar id +
    /// title). The order is the match order — a more specific literal route
    /// should precede a capturing one.
    abstract Routes: HostRoute list

    /// Resolve a concrete `path` against the declared routes; the first
    /// matching route wins. Returns the matched route + its captured params,
    /// or `None` when no declared route matches.
    abstract TryResolve: path: string -> (HostRoute * HostRouteParams) option

[<RequireQualifiedAccess>]
module HostRouteContract =

    /// Build a contract from a route list. Match order is declaration order
    /// (first match wins).
    let create (routes: HostRoute list) : IHostRouteContract =
        { new IHostRouteContract with
            member _.Routes = routes

            member _.TryResolve path =
                routes
                |> List.tryPick (fun r -> HostRoute.tryMatch r path |> Option.map (fun ps -> r, ps))
        }

    /// Deep-link the shell to a resolved route: fire `NavigationRequest` so
    /// the shell router selects the route's page (client deep-link /
    /// back-button — the same hook Phase 110's `Navigate` capability drives),
    /// and return the route's param state projected into the neutral
    /// `HostBindingSources` namespace so the hosted tree restores its param
    /// state via the Phase 264 read-side. This is the param round-trip.
    let navigate (route: HostRoute) (ps: HostRouteParams) : HostBindingSources =
        NavigationRequest.request route.SidebarId
        HostRoute.paramsToBindingSources ps

    /// Resolve a deep-link `path` against the contract and, on a match,
    /// navigate the shell + return the restored param bindings (the
    /// `HostBindingSources` the hosted tree resolves against). `None` when no
    /// declared route matches — the caller falls through to its own 404.
    let deepLink (contract: IHostRouteContract) (path: string) : HostBindingSources option =
        contract.TryResolve path |> Option.map (fun (route, ps) -> navigate route ps)