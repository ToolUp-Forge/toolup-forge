// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module HelloWorld.Server

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.Compose.Server
open ToolUp.Remoting.Server
open HelloWorld.Module.SharedTypes
open HelloWorld.Module.Server
open ToolUp.Platform.Infra.Metrics
open ToolUp.Platform.Config
open ToolUp.Platform.Compose
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.Jobs

// HelloWorld composition root.
// An authenticated deployment of the canonical `Surfaces.individual` shape:
// `ServerConfigOverrides.referenceApp` declares `Some Surfaces.individual`,
// so `fromEnv` pins the app to it unless the operator overrides with
// TOOLUP_PLATFORM_SURFACES. Compare samples/MinimalApp, whose
// `ServerConfigOverrides.empty` falls through to `Surfaces.anonymous`.
//
// What it composes:
//
//   1. The sibling `HelloWorld.Module` (the four-file minimum module), its
//      handler mounted with two seams passed explicitly: a console-printing
//      `IRemotingTelemetry` sink in place of the `IMetricsSink` bridge, and
//      a `ForgeAuthContext` resolver evaluated per call. Without them
//      `Api.make` composes its defaults (the `IMetricsSink` bridge and the
//      stamped principal, `AccessContext.ofItems`); either is correct, and
//      the worked example surfaces both.
//
//   2. The server halves of what the client's shell opens: module-visibility
//      profiles (`SurfacingModuleVisibility`) for the profile editor, and the
//      in-process notification stream the shell subscribes to on an
//      authenticated surface.
//
//   3. In Debug builds only, the development identity profile below: the
//      team surface with a stable header identity that is a platform admin
//      and owns a bootstrap team, so the whole shell can be opened locally
//      with no identity provider. It is selected, never defaulted:
//      TOOLUP_PROFILE=helloworld-dev-identity (what `../run.ps1` sets).

module HelloWorldRoutes =
    /// Console-printing `IRemotingTelemetry` sink. Emits one line per
    /// method completion with the method name, wall-clock elapsed ms,
    /// outcome (`ok`/`error`), and the per-request correlation id.
    /// Suitable as a worked example — a production
    /// deployment would route through `ServerApp.withMetricsSink` and
    /// let the default `Api.make` bridge emit through there.
    let consoleTelemetry: IRemotingTelemetry =
        { new IRemotingTelemetry with
            member _.OnMethodCompleted t =
                let outcomeText =
                    match t.Outcome with
                    | MethodOutcome.Succeeded -> "ok"
                    | MethodOutcome.Failed ex -> sprintf "error: %s" ex.Message
                    | MethodOutcome.RateLimited retryAfter -> sprintf "rate-limited: retry after %A" retryAfter

                let correlationText =
                    match t.CorrelationId with
                    | Some id -> id
                    | None -> "-"

                printfn
                    "[hello/telemetry] method=%s elapsed_ms=%d outcome=%s correlation=%s"
                    t.MethodName
                    t.ElapsedMs
                    outcomeText
                    correlationText
        }

    /// "Request started at" resolver — evaluated per call, not
    /// snapshotted at composition: the timestamp is fresh on every
    /// request, so a closure-captured value couldn't substitute.
    let requestStartedAtResolver (ctx: HttpContext) : Async<ForgeAuthContext> = async {
        let startedAt = DateTimeOffset.UtcNow

        // The per-request context's claim slot carries the stamped
        // timestamp; handler code reads it via `HasClaim ("startedAt",
        // None)` returning `true` AND inspecting the value through
        // whatever side-channel applies. The worked-example shape
        // simply lets the resolver evaluate per call so the timing is
        // observable.
        return
            { new ForgeAuthContext with
                member _.HasRole _ = false

                member _.HasClaim(claim, _) =
                    match claim with
                    | "startedAt" -> true
                    | _ -> false

                member _.HasTenant() = false
                member _.IsAnonymous() = true
                member _.SubjectId = sprintf "anonymous:started-at:%O" startedAt
            }
    }

    /// Server-side implementation of `HelloWorldApi`. The single Echo
    /// method delegates to the Module's pure `echoRoutine` — the
    /// Module owns the domain logic; this composition root owns the
    /// HTTP wiring.
    let helloWorldImpl (_: HttpContext) : HelloWorldApi = {
        Echo = fun request -> async { return echoRoutine request }
    }

    /// Mount the worked-example handler via `Api.make`. Both seams
    /// passed explicitly so the trace lines render on stdout; remove
    /// the explicit args to fall back to the default bridges (forge's
    /// `IMetricsSink` + the stamped principal (`AccessContext.ofItems`)
    /// reader). The route is the default `/api/<type>/<method>`, the
    /// one the module's client proxy (`Api.makeProxy`) calls.
    let routes =
        Api.make (helloWorldImpl, telemetry = consoleTelemetry, authContext = requestStartedAtResolver)

/// Console `IMetricsSink` that prints every emission. Composed via
/// `ServerApp.withMetricsSink` so the default `Api.make` bridge in any
/// non-overridden handler emits through here — proves the seam is
/// active end-to-end. Production deployments swap this for the
/// Prometheus / OpenTelemetry companion sink.
type ConsoleMetricsSink() =
    interface IMetricsSink with
        member _.Record(name, value, tags) =
            printfn "[metrics/record] %s=%g %A" name value tags

        member _.Increment(name, tags) = printfn "[metrics/inc] %s %A" name tags

        member _.SetGauge(name, value, tags) =
            printfn "[metrics/gauge] %s=%g %A" name value tags

#if DEBUG
/// The bootstrap team the development identity owns.
[<Literal>]
let private devIdentityTeam = "helloworld"

/// The development identity: the team surface, with the header identity
/// `dev-admin` made the platform admin and the owner of a bootstrap team.
/// The header-trust provider is the SDK's dev default (TOOLUP_AUTH_MODE
/// unset), and the acknowledgement below is what lets it run on an
/// authenticated surface; anyone who can reach the port can claim any
/// identity, which is why the profile exists only in a Debug build and only
/// when selected. A Release build never declares it, so selecting it there
/// refuses startup and lists the profiles that do exist.
let private devIdentityProfile: ConfigKeys.ConfigProfile = {
    Name = "helloworld-dev-identity"
    Description =
        "HelloWorld on a developer machine: the team surface, with the header identity dev-admin as platform admin and owner of a bootstrap team. Debug builds only."
    Requires = []
    Values = [
        ConfigKeys.Names.platformSurfaces, "team"
        ConfigKeys.Names.acceptHeaderAuthInAuthMode, "true"
        ConfigKeys.Names.initialPlatformAdmin, "dev-admin"
        ConfigKeys.Names.initialTeamName, "Hello World team"
        ConfigKeys.Names.initialTeamId, devIdentityTeam
        // A loopback developer machine: no proxy in front, no secret worth
        // keeping, and a notification stream with no JWT to carry. Each is
        // the acknowledgement the preflight names, stated here rather than
        // left for every developer to type.
        ConfigKeys.Names.trustForwardedHeaders, "false"
        ConfigKeys.Names.acceptPlaintextSecrets, "true"
        ConfigKeys.Names.acceptQueryParamSseAuthInAuthMode, "true"
        // Data ingestion's OAuth flows pin their redirect to the sample's
        // own origin (the first port of its claim, `ports.json`) rather than
        // deriving it from each request's Host.
        ConfigKeys.Names.oauthRedirectBase, "http://127.0.0.1:5140"
    ]
}

/// Whether the development identity profile is the one in force.
let private devIdentitySelected () =
    ConfigResolution.profile ()
    |> Option.exists (fun selected -> selected.Name = devIdentityProfile.Name)
#endif

#if DEBUG
/// Under the development identity, one row on each of the jobs and
/// data-migration admin pages: a scheduled job in the bootstrap team's scope
/// (it succeeds, once a day, so a check sees it run only when asked) and a
/// data type at its initial schema version (a pass over it finds nothing to
/// upgrade). The data type claims no file, so no upload ever reaches it.
let private withAdminFixtures (m: ServerModule) : ServerModule =
    if devIdentitySelected () then
        let greeting: DataType = {
            Info = {
                Id = "HelloWorldGreeting"
                DisplayName = "Hello World greeting"
                Schema = None
            }
            Id = "HelloWorldGreeting"
            SchemaVersion = DataTypes.initialSchemaVersion
            Migrations = []
            Detect = fun _ -> async { return false }
            Process = fun _ -> async { return failwith "the greeting data type claims no file" }
        }

        let greet =
            { new IJobHandler with
                member _.Execute _ = async { return JobResult.Success }
            }

        m
        |> ServerModule.withDataTypes [ greeting ]
        |> ServerModule.withScheduledJob (
            ScheduledJobDeclaration.create "helloworld-greeting" greet (CronTrigger "0 3 * * *")
            |> ScheduledJobDeclaration.withScopes [ devIdentityTeam ]
        )
    else
        m

/// Under the development identity, data ingestion is enabled, so the
/// data-source admin the client opens lists the sources (none is declared).
/// Its OAuth flows need the redirect base that profile pins, so it stays off
/// elsewhere.
let private dataIngestion () =
    if devIdentitySelected () then
        FeatureMode.Enabled
    else
        FeatureMode.Disabled
#else
let private withAdminFixtures (m: ServerModule) : ServerModule = m

let private dataIngestion () = FeatureMode.Disabled
#endif

[<EntryPoint>]
let main _ =
#if DEBUG
    // Profiles are declared before the logger reads its first key.
    ConfigProfiles.declare devIdentityProfile
#endif
    let logger = ConsoleLogger.fromEnv ()

    // The client opens the session-security page, the platform-users admin,
    // the data-subject-request admin and the external-contact admin, so the
    // server composes what each calls: a session registry over the blob store,
    // the tenant lifecycle the offboard flow drives, data subject requests and
    // the contact store over the entity store it is kept in. The audit log is
    // kept, so the audit trail has rows. It opens the background-jobs and
    // data-migration admins too, so the server composes the in-process job
    // scheduler and the data-migration routes (the trigger only: no startup
    // sweep). Under the development identity it enables data ingestion too,
    // for the data-source admin, which keeps the source configs in the blob
    // store.
    let config =
        let fromEnv = ServerConfig.fromEnv logger ServerConfigOverrides.referenceApp

        {
            fromEnv with
                ModuleVisibility = SurfacingModuleVisibility
                Notifications = InMemoryNotifications
                TenantLifecycle = FeatureMode.Enabled
                AuditLog = FeatureMode.Enabled
                JobScheduler = InProcessJobScheduler
                DataMigrations = ManualDataMigrations
                DataIngestion = dataIngestion ()
                DataSubjectRequests =
                    DataSubjectRequestMode.Enabled(DataSubjectRequestConfig.sync ErasurePolicy.Tombstone)
                Stores = {
                    fromEnv.Stores with
                        SessionRegistry = BlobSessionRegistry SessionRegistryOptions.defaults
                        EntityStore = FeatureMode.Enabled
                        ExternalContactStore = FeatureMode.Enabled
                }
        }

    // `ServerModule.Name` is the module's ID TOKEN, not its display name —
    // it is the RBAC permission key, the `ServerConfig.ModuleNames` entry,
    // and it must equal the client `ClientModule.Definition.Id` (which
    // `ClientModule.create` derives from the display Name with spaces
    // stripped: "Hello World" -> "HelloWorld"). The `ModuleContract`
    // parity law asserts the two agree.
    let helloWorldModule =
        ServerModule.create "HelloWorld"
        |> ServerModule.withHandlers [ HelloWorldRoutes.routes ]
        |> withAdminFixtures

    // The team surface refuses to start on the compose default's implicit
    // header-trust provider; under the development identity it is named
    // explicitly, so the choice is visible where the app is composed.
    let withDevIdentityAuth (app: ServerApp) =
#if DEBUG
        if devIdentitySelected () then
            app |> ServerApp.withAuth (HeaderAuthProvider.HeaderAuthProvider())
        else
            app
#else
        app
#endif

    ServerApp.empty
    |> ServerApp.withConfig config
    |> withDevIdentityAuth
    |> ServerApp.withLogger logger
    |> ServerApp.withMetricsSink (ConsoleMetricsSink())
    |> ServerApp.addModule helloWorldModule
    |> ServerApp.run