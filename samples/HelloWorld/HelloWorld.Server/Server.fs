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
        ConfigKeys.Names.initialTeamId, "helloworld"
        // A loopback developer machine: no proxy in front, no secret worth
        // keeping, and a notification stream with no JWT to carry. Each is
        // the acknowledgement the preflight names, stated here rather than
        // left for every developer to type.
        ConfigKeys.Names.trustForwardedHeaders, "false"
        ConfigKeys.Names.acceptPlaintextSecrets, "true"
        ConfigKeys.Names.acceptQueryParamSseAuthInAuthMode, "true"
    ]
}

/// Whether the development identity profile is the one in force.
let private devIdentitySelected () =
    ConfigResolution.profile ()
    |> Option.exists (fun selected -> selected.Name = devIdentityProfile.Name)
#endif

[<EntryPoint>]
let main _ =
#if DEBUG
    // Profiles are declared before the logger reads its first key.
    ConfigProfiles.declare devIdentityProfile
#endif
    let logger = ConsoleLogger.fromEnv ()

    let config = {
        ServerConfig.fromEnv logger ServerConfigOverrides.referenceApp with
            ModuleVisibility = SurfacingModuleVisibility
            Notifications = InMemoryNotifications
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