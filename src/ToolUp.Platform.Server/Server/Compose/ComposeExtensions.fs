namespace ToolUp.Platform

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

/// Phase 1052 — what a companion or builder adds to a deployment, as data:
/// an owner and a kind. One case today. Phase 1067 adds `Hosted` (with its
/// `Placement`); Phase 876 adds `Substrate` and the `Requires` / `Provides`
/// ordering. Each widens this union rather than re-introducing a fold.
[<NoEquality; NoComparison>]
type ContributionKind =
    /// DI registrations, applied after the SDK's own and after the
    /// consumer's `ComposeExtensions.ServiceConfig`, in the order the
    /// contributions were added.
    | Service of (IServiceCollection -> IServiceCollection)

/// Phase 1052 — one contribution to a deployment. `Owner` is the
/// contributing component's own `ComponentId` (GP 9: the contributor names
/// itself; the SDK names no companion), so the composition manifest and
/// the dispatcher's footprint can read who registered what without
/// folding closures apart.
[<NoEquality; NoComparison>]
type Contribution = {
    Owner: ComponentId
    Kind: ContributionKind
}

[<RequireQualifiedAccess>]
module Contribution =

    /// A `Service` contribution: `owner` registers `register`'s services.
    let service (owner: ComponentId) (register: IServiceCollection -> IServiceCollection) : Contribution = {
        Owner = owner
        Kind = Service register
    }