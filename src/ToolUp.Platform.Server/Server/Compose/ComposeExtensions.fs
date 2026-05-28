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