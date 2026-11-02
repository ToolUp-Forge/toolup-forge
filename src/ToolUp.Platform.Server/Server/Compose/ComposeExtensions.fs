namespace ToolUp.Platform.Compose

open ToolUp.Platform

open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection

/// Extension hook for companion packages (ToolUp.AI, future distributed
/// task companions, etc.) to contribute handlers and DI services without
/// `compose` having to know about them. `Handlers` are appended to the
/// router; `ServiceConfig` runs after the SDK's own service registrations
/// so companions can depend on SDK services being present.
/// `NotificationConsumers` lets a wrapping companion declare that it
/// publishes to `INotificationChannel` so `compose`'s `NotificationsAuto`
/// resolution flips to `InMemoryNotifications` automatically (Phase 1g).
/// Each entry is a free-form string identifying the consumer ("AI",
/// "RAG", …); the values are surfaced in `/dev/inspect` for diagnostics
/// but otherwise unused. Apps that explicitly set
/// `ServerConfig.Notifications` override the auto-detection.
/// `PreMiddleware` and `PostMiddleware` (Phase 1f) accumulate
/// `IApplicationBuilder` thunks that run at documented pipeline
/// positions: `Pre` runs after CORS + security headers but BEFORE
/// `ScopeResolutionMiddleware`, so allowlists / IP gates / per-tenant
/// rejections short-circuit before scope is resolved; `Post` runs
/// AFTER `app.UseGiraffe router`, so consumers can register fallback
/// handlers, custom 404 pages, or debug-only routes without forking
/// `compose`. Thunks are applied in registration order.
/// Phase 1052 — `ServiceConfig` holds the consumer's own registrations, and
/// `ServerApp.withExtensions` sets the record whole. A companion or builder
/// adds its DI registrations as a
/// `Contribution` (`ServerApp.addContribution` / `ServerApp.addServiceConfig`),
/// which `ServerApp.run` folds after `ServiceConfig` with `ServiceConfig.append`.
type ComposeExtensions = {
    Handlers: HttpHandler list
    ServiceConfig: (IServiceCollection -> IServiceCollection) option
    NotificationConsumers: string list
    PreMiddleware: (IApplicationBuilder -> IApplicationBuilder) list
    PostMiddleware: (IApplicationBuilder -> IApplicationBuilder) list
}

module ComposeExtensions =
    let empty: ComposeExtensions = {
        Handlers = []
        ServiceConfig = None
        NotificationConsumers = []
        PreMiddleware = []
        PostMiddleware = []
    }

/// Phase 1052 — the one fold that appends a DI registration to a
/// `ComposeExtensions.ServiceConfig`-shaped value. `ServiceConfig` is an
/// option of a function, so every contributor used to fold by hand; this
/// is that fold, written once. A `Build.fs` check refuses a second copy.
[<RequireQualifiedAccess>]
module ServiceConfig =

    /// Append `register` after `current`: the result runs `current` first,
    /// then `register`, so registrations compose in registration order
    /// (a later `AddSingleton` of the same service wins on resolve, exactly
    /// as it would written out in one `ConfigureServices`).
    let append
        (register: IServiceCollection -> IServiceCollection)
        (current: (IServiceCollection -> IServiceCollection) option)
        : (IServiceCollection -> IServiceCollection) option =
        match current with
        | None -> Some register
        | Some existing -> Some(fun services -> register (existing services))

/// Where a `Middleware` contribution runs: `Pre` after CORS and
/// the security headers but before scope resolution (allowlists, request
/// rejections), `Post` after the router (fallbacks, custom 404s) — the two
/// positions `ComposeExtensions.PreMiddleware` / `PostMiddleware` name.
[<RequireQualifiedAccess>]
type MiddlewarePosition =
    | Pre
    | Post

/// Phase 1052 — what a companion or builder adds to a deployment, as data:
/// an owner and a kind. It also carries the kinds that project onto the
/// router and the preflight, and the seams a contribution requires and
/// provides, by which `ContributionPlan.order` sequences the plan.
[<NoEquality; NoComparison>]
type ContributionKind =
    /// DI registrations, applied after the SDK's own and after the
    /// consumer's `ComposeExtensions.ServiceConfig`, in plan order.
    | Service of (IServiceCollection -> IServiceCollection)
    /// A unit of background work, registered through `Hosting.register`
    /// against the deployment's final `ServerConfig` (every override
    /// folded) at the same fixed point as a `Service` contribution — so
    /// whether it runs is decided by its `Placement`, once, with the
    /// configuration `run` composes.
    | Hosted of BackgroundUnit
    /// Routes, appended to the router after the consumer's
    /// `ComposeExtensions.Handlers`, in plan order.
    | Routes of HttpHandler list
    /// A startup preflight validator, run after the
    /// deployment's own (`ServerApp.withConfigValidator`), in plan order.
    | Validator of ConfigValidation.IConfigValidator
    /// An `IApplicationBuilder` thunk at `MiddlewarePosition`,
    /// applied after the consumer's `ComposeExtensions` thunks at the same
    /// position, in plan order.
    | Middleware of MiddlewarePosition * (IApplicationBuilder -> IApplicationBuilder)
    /// DI registrations that need values `compose` built — the event
    /// store, the audit log, the notification channel — handed over as the
    /// `Substrate` at the same fixed point as a `Service` contribution, so a
    /// companion never builds a second container to find them.
    | Staged of (Substrate -> IServiceCollection -> IServiceCollection)

/// Phase 1052 — one contribution to a deployment. `Owner` is the
/// contributing component's own `ComponentId` (GP 9: the contributor names
/// itself; the SDK names no companion), so the composition manifest and
/// the dispatcher's footprint can read who registered what without
/// folding closures apart.
///
/// `Requires` names the seams this contribution needs ANOTHER
/// contribution to provide; `Provides` names the seams it registers for
/// the others. The SDK's own substrate (storage, event store, scheduler,
/// …) is registered before any contribution runs and is never declared.
/// Both default to empty, which leaves a contribution where it was added.
[<NoEquality; NoComparison>]
type Contribution = {
    Owner: ComponentId
    Kind: ContributionKind
    Requires: SeamId list
    Provides: SeamId list
}

[<RequireQualifiedAccess>]
module Contribution =

    let private ofKind (owner: ComponentId) (kind: ContributionKind) : Contribution = {
        Owner = owner
        Kind = kind
        Requires = []
        Provides = []
    }

    /// A `Service` contribution: `owner` registers `register`'s services.
    let service (owner: ComponentId) (register: IServiceCollection -> IServiceCollection) : Contribution =
        ofKind owner (Service register)

    /// A `Staged` contribution: `owner` registers services built from the
    /// substrate `compose` hands over.
    let staged (owner: ComponentId) (register: Substrate -> IServiceCollection -> IServiceCollection) : Contribution =
        ofKind owner (Staged register)

    /// A `Hosted` contribution: `owner` contributes the background `unit`.
    let hosted (owner: ComponentId) (unit: BackgroundUnit) : Contribution = ofKind owner (Hosted unit)

    /// A `Routes` contribution: `owner` mounts `handlers`.
    let routes (owner: ComponentId) (handlers: HttpHandler list) : Contribution = ofKind owner (Routes handlers)

    /// A `Validator` contribution: `owner` adds a preflight check.
    let validator (owner: ComponentId) (check: ConfigValidation.IConfigValidator) : Contribution =
        ofKind owner (Validator check)

    /// A `Middleware` contribution: `owner` applies `thunk` at
    /// `position`.
    let middleware
        (owner: ComponentId)
        (position: MiddlewarePosition)
        (thunk: IApplicationBuilder -> IApplicationBuilder)
        : Contribution =
        ofKind owner (Middleware(position, thunk))

    /// **adds** seams `contribution` needs another contribution
    /// to provide; the plan runs every provider of each before it.
    let requires (seams: SeamId list) (contribution: Contribution) : Contribution = {
        contribution with
            Requires = contribution.Requires @ seams
    }

    /// **adds** seams `contribution` provides to the others.
    let provides (seams: SeamId list) (contribution: Contribution) : Contribution = {
        contribution with
            Provides = contribution.Provides @ seams
    }

/// Why a contribution plan cannot be ordered. Each case names
/// both ends: who needs the seam, and who (if anyone) provides it.
type ContributionRefusal =
    /// `requirer` requires `seam`, and no other contribution provides it.
    | UnmetRequirement of requirer: ComponentId * seam: SeamId
    /// The requirements close a loop: each entry's owner requires the seam
    /// the NEXT entry's owner provides, and the last entry's seam is
    /// provided by the first entry's owner.
    | RequirementCycle of (ComponentId * SeamId) list

[<RequireQualifiedAccess>]
module ContributionRefusal =

    /// The refusal as one line naming both ends and the remedy.
    let describe (refusal: ContributionRefusal) : string =
        match refusal with
        | UnmetRequirement(requirer, seam) ->
            sprintf
                "contribution plan: '%s' requires seam '%s', which no other contribution provides. Add the contribution that provides it, or drop the requirement."
                (ComponentId.value requirer)
                seam.Value
        | RequirementCycle edges ->
            let owners = edges |> List.map fst
            let providers = (List.tail owners) @ [ List.head owners ]

            let links =
                List.map2
                    (fun (owner, seam: SeamId) provider ->
                        sprintf
                            "'%s' requires '%s' (provided by '%s')"
                            (ComponentId.value owner)
                            seam.Value
                            (ComponentId.value provider))
                    edges
                    providers

            sprintf
                "contribution plan: the requirements form a cycle: %s. Break one of the requirements."
                (String.concat ", " links)

/// The root orders the plan once: every provider of a seam
/// runs before every contribution that requires it, and contributions with
/// no edge between them keep the order they were added in (so a plan that
/// declares no seams runs exactly as added — GP 11).
[<RequireQualifiedAccess>]
module ContributionPlan =

    /// Order `contributions`, or refuse with both ends named.
    let order (contributions: Contribution list) : Result<Contribution list, ContributionRefusal> =
        let items = Array.ofList contributions

        // The contributions other than `i` that provide `seam`.
        let providersOf (i: int) (seam: SeamId) = [
            for p in 0 .. items.Length - 1 do
                if p <> i && List.contains seam items[p].Provides then
                    p
        ]

        let unmet =
            items
            |> Array.indexed
            |> Array.tryPick (fun (i, c) ->
                c.Requires
                |> List.tryFind (fun seam -> List.isEmpty (providersOf i seam))
                |> Option.map (fun seam -> UnmetRequirement(c.Owner, seam)))

        match unmet with
        | Some refusal -> Error refusal
        | None ->
            // Each contribution's predecessors: every other provider of
            // every seam it requires, with the seam that links them.
            let preds =
                items
                |> Array.mapi (fun i c -> [
                    for seam in List.distinct c.Requires do
                        for p in providersOf i seam do
                            p, seam
                ])

            // Kahn's algorithm, always taking the earliest-added ready
            // contribution, so the order is deterministic and stable.
            let rec place (placed: int list) (remaining: int list) =
                match remaining with
                | [] -> Ok(placed |> List.rev |> List.map (fun i -> items[i]))
                | first :: _ ->
                    let isPlaced i = List.contains i placed

                    match remaining |> List.tryFind (fun i -> preds[i] |> List.forall (fst >> isPlaced)) with
                    | Some next -> place (next :: placed) (remaining |> List.filter ((<>) next))
                    | None ->
                        // Every remaining contribution waits on another
                        // remaining one, so following the first unplaced
                        // predecessor from any of them reaches a loop.
                        let step i =
                            preds[i] |> List.find (fun (p, _) -> not (isPlaced p))

                        let rec walk (path: int list) (i: int) =
                            match List.tryFindIndex ((=) i) path with
                            | Some at -> path |> List.take (at + 1) |> List.rev
                            | None -> walk (i :: path) (fst (step i))

                        // In loop order: each requirer, then the provider
                        // it waits on.
                        walk [] first
                        |> List.map (fun i -> items[i].Owner, snd (step i))
                        |> RequirementCycle
                        |> Error

            place [] [ 0 .. items.Length - 1 ]