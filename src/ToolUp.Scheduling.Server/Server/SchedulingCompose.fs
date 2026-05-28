module ToolUp.Scheduling.SchedulingCompose

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.RemotingHelpers
open ToolUp.Platform.Server
open ToolUp.Platform.IEntityStore
open ToolUp.Scheduling.SchedulingApi
open ToolUp.Scheduling.IBookingScheduler
open ToolUp.Scheduling.SchedulingApiHandler

// ─── Phase 20 — SchedulingServerApp composition root ─────────────
//
// `SchedulingServerApp` mirrors `AIServerApp` / `RAGServerApp` shape:
// it wraps a base `ServerApp` and adds a `with*` helper for every
// `ServerApp.with*` so consumers write a single fluent pipeline.
//
// **Required substrate**: `IEntityStore` + `IEventStore`. The compose
// step does NOT enforce `ServerConfig.EntityStore = EnabledEntityStore`
// — if the consumer forgets, the DI lookup for `IEntityStore` at
// first-call time throws `null reference`, which the SDK's error
// handler propagates as `500`. Documented requirement; explicit
// enforcement is a follow-up.
//
// **Three entity registrations** flow through:
//   * `BookableResource` — index on `ResourceType`
//   * `Booking` — index on `ResourceId` / `Status` / compound
//     `(ResourceId, StartUtc-ticks)`
//   * `AvailabilityException` — index on `ResourceId`
//
// **API handler**: a single `ISchedulingApi` mounted via
// `makeApi (fun ctx -> SchedulingApiHandler.schedulingApi scheduler ctx)`.
// The handler resolves `scopeId`/`userId` from
// `HttpContext.Items` (populated by `ScopeResolutionMiddleware`).

/// Record form of compose arguments. Wraps a base `ServerApp` and
/// carries the scheduler-specific `RemindersEnabled` flag (a
/// follow-up; see `withReminders`).
type SchedulingServerApp = {
    Base: ServerApp
    /// Reminder fusion is opt-in (default `false`). When `true` the
    /// compose step would register a `BookingReminderJobHandler`
    /// against `IJobScheduler` — Phase 20 follow-up; currently no-op.
    RemindersEnabled: bool
}

module SchedulingServerApp =

    let create () : SchedulingServerApp = {
        Base = ServerApp.empty
        RemindersEnabled = false
    }

    // ─── Delegating helpers (mirror every `ServerApp.with*`) ─────

    let withConfig (c: ServerConfig) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withConfig c app.Base
    }

    let withAuth (a: IAuthProvider) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withAuth a app.Base
    }

    let withLogger (l: ILogger) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withLogger l app.Base
    }

    let withStorage (s: IBlobStorage) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withStorage s app.Base
    }

    let withNotifications (n: INotificationChannel) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withNotifications n app.Base
    }

    let withTransactionalSink (sink: INotificationSink) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withTransactionalSink sink app.Base
    }

    let withHealthCheck (check: HealthChecks.IHealthCheck) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withHealthCheck check app.Base
    }

    let withConfigValidator
        (validator: ConfigValidation.IConfigValidator)
        (app: SchedulingServerApp)
        : SchedulingServerApp =
        {
            app with
                Base = ServerApp.withConfigValidator validator app.Base
        }

    let withEncryptedBlobStorage
        (resolver: IBlobEncryptionKeyResolver)
        (app: SchedulingServerApp)
        : SchedulingServerApp =
        {
            app with
                Base = ServerApp.withEncryptedBlobStorage resolver app.Base
        }

    let withEntity<'T>
        (registration: EntityTypes.EntityRegistration<'T>)
        (app: SchedulingServerApp)
        : SchedulingServerApp =
        {
            app with
                Base = ServerApp.withEntity registration app.Base
        }

    /// Phase 9b.B — declare a composition-root-owned background job.
    /// Delegates to `ServerApp.withJobHandler`.
    let withJobHandler
        (handlerName: string, handler: IJobHandler, trigger: Trigger)
        (app: SchedulingServerApp)
        : SchedulingServerApp =
        {
            app with
                Base = ServerApp.withJobHandler (handlerName, handler, trigger) app.Base
        }

    /// Phase 9b.B — declare a composition-root-owned background job
    /// with full control over every `JobRegistration` knob. Delegates
    /// to `ServerApp.withScheduledJob`.
    let withScheduledJob (declaration: ScheduledJobDeclaration) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withScheduledJob declaration app.Base
    }

    /// Phase 9b.A — opt into back-fill of `OnEvent` jobs on detected
    /// scheduler tick drift. Delegates to `ServerApp.withBackfillMissedTicks`.
    let withBackfillMissedTicks (enabled: bool) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.withBackfillMissedTicks enabled app.Base
    }

    let withPreMiddleware
        (f: IApplicationBuilder -> IApplicationBuilder)
        (app: SchedulingServerApp)
        : SchedulingServerApp =
        {
            app with
                Base = ServerApp.withPreMiddleware f app.Base
        }

    let withPostMiddleware
        (f: IApplicationBuilder -> IApplicationBuilder)
        (app: SchedulingServerApp)
        : SchedulingServerApp =
        {
            app with
                Base = ServerApp.withPostMiddleware f app.Base
        }

    let addModule (m: ServerModule) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.addModule m app.Base
    }

    let addModules (modules: ServerModule list) (app: SchedulingServerApp) : SchedulingServerApp = {
        app with
            Base = ServerApp.addModules modules app.Base
    }

    // ─── Scheduling-specific helpers ──────────────────────────────

    /// Opt into reminder fusion. Reserved for a Phase 20 follow-up;
    /// currently a no-op so the call site can ship without breaking
    /// when the feature lands.
    let withReminders (app: SchedulingServerApp) : SchedulingServerApp = { app with RemindersEnabled = true }

    /// Drive the final composition. Registers the three booking
    /// entity types, wires `IBookingScheduler` into DI, mounts the
    /// `ISchedulingApi` handler, and delegates to `ServerApp.run`.
    let run (app: SchedulingServerApp) : int =
        // 1. Register the three entity types via the base's
        //    `withEntity` pipeline.
        let withEntities =
            app.Base
            |> ServerApp.withEntity BookingScheduler.resourceRegistration
            |> ServerApp.withEntity BookingScheduler.bookingRegistration
            |> ServerApp.withEntity BookingScheduler.exceptionRegistration

        // 2. DI registration for the default `IBookingScheduler`.
        let schedulingServiceConfig (services: IServiceCollection) =
            services.AddSingleton<IBookingScheduler>(
                System.Func<System.IServiceProvider, IBookingScheduler>(fun sp ->
                    let entityStore = sp.GetService(typeof<IEntityStore>) :?> IEntityStore
                    let eventStore = sp.GetService(typeof<IEventStore>) :?> IEventStore
                    BookingScheduler.BookingScheduler(entityStore, eventStore) :> IBookingScheduler)
            )

        // 3. API handler. Resolves `IBookingScheduler` per request
        //    out of DI; the per-request `scopeId`/`userId` come from
        //    `HttpContext.Items` inside `schedulingApi`.
        let schedulingApiHandler =
            makeApi (fun (ctx: HttpContext) ->
                let scheduler =
                    ctx.RequestServices.GetService(typeof<IBookingScheduler>) :?> IBookingScheduler

                schedulingApi scheduler ctx)

        // 4. Merge into the base extensions.
        let baseExt = withEntities.Extensions

        let mergedExt: ComposeExtensions = {
            baseExt with
                Handlers = baseExt.Handlers @ [ schedulingApiHandler ]
                ServiceConfig =
                    match baseExt.ServiceConfig with
                    | None -> Some schedulingServiceConfig
                    | Some baseFn -> Some(fun s -> schedulingServiceConfig (baseFn s))
        }

        let final = {
            withEntities with
                Extensions = mergedExt
        }

        ServerApp.run final