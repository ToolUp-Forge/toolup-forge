// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Companions.Isolation

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Server

// ─── Phase 687 — selecting a mode, and what a profile demands ─────────
//
// `IsolationMode.InProcess` is the GP 11 default: the entry point runs
// in the caller's process exactly as the companion's own API would have
// run it, and the seam adds a `Result` wrapper and nothing else. It is
// the right mode for host-authored input and the wrong one for an
// upload, and the composition profile is where that distinction is
// enforced: under Phase 657's `CompositionProfile.Verified` an
// in-process isolation is REFUSED at composition, because a profile
// that binds every component's authority to a declared envelope cannot
// leave a native parser of user bytes unbounded and still be that
// profile.
//
// The refusal lives here, at the composition root where the profile is
// known, rather than as a new `CompositionProfileRefusal` case inside
// `ToolUp.Platform.Server`: that DU is public and closed, widening it
// is a breaking change on a frozen release, and the boot preflight
// reads a composition MANIFEST that carries no native-boundary facet
// yet. Promoting this to a manifest facet is a decision for the next
// minor, recorded in the phase outcome — not something a companion
// slips into the SDK's profile machinery from below.

/// The in-process implementation: the pre-687 behaviour behind the
/// seam's signature. No timeout, no cap, no crash containment.
[<RequireQualifiedAccess>]
module InProcessIsolation =
    /// Instantiate and invoke `entry` in this process.
    let run (entry: Type) (request: IsolatedRequest) : Async<Result<byte[], IsolationRefusal>> = async {
        match IsolationWorker.resolveEntry (defaultArg (Option.ofObj entry.AssemblyQualifiedName) entry.FullName) with
        | Error reason -> return Error(IsolationRefusal.WorkerUnavailable reason)
        | Ok resolved ->
            match IsolationWorker.invoke resolved request with
            | WorkerResponse.Answered bytes -> return Ok bytes
            | WorkerResponse.Rejected message -> return Error(IsolationRefusal.EntryFailed message)
            | WorkerResponse.Unresolvable reason -> return Error(IsolationRefusal.WorkerUnavailable reason)
    }

    /// The in-process `ICompanionIsolation`.
    let instance: ICompanionIsolation =
        { new ICompanionIsolation with
            member _.Mode = IsolationMode.InProcess
            member _.Run(entry, request) = run entry request
        }

/// A composition that cannot satisfy the isolation the profile it
/// declared requires. Refused at composition time, before anything
/// serves.
[<RequireQualifiedAccess>]
type IsolationProfileRefusal =
    /// The verified composition profile was declared and the isolation
    /// composed is in-process, so a native parser fed untrusted bytes
    /// would run unbounded in the host.
    | InProcessUnderVerifiedProfile
    /// An out-of-process mode was composed and no worker can be started
    /// on this host — the resolver's reason, verbatim.
    | WorkerUnresolvable of reason: string

[<RequireQualifiedAccess>]
module IsolationProfileRefusal =
    /// One-line operator-facing description naming the remedy.
    let describe =
        function
        | IsolationProfileRefusal.InProcessUnderVerifiedProfile ->
            "the verified composition profile requires native companions that parse untrusted bytes to run out-of-process: compose IsolationMode.OutOfProcess (or OutOfProcessWith an explicit launcher), or run CompositionProfile.Standard."
        | IsolationProfileRefusal.WorkerUnresolvable reason ->
            $"out-of-process isolation was composed but no worker can be started on this host: {reason}"

/// The seam's composition surface.
[<RequireQualifiedAccess>]
module CompanionIsolation =
    /// The `ICompanionIsolation` for a mode.
    let ofMode (mode: IsolationMode) : ICompanionIsolation =
        match mode with
        | IsolationMode.InProcess -> InProcessIsolation.instance
        | IsolationMode.OutOfProcess limits -> ProcessIsolation.create limits
        | IsolationMode.OutOfProcessWith(limits, launcher) -> ProcessIsolation.createWith limits launcher

    /// Run a statically-known entry point. The generic constraint is
    /// what the worker checks dynamically — public, parameterless
    /// constructor, implements the interface — stated at the call site
    /// so a call that cannot cross the pipe does not compile.
    let run<'Entry when 'Entry :> IIsolatedEntryPoint and 'Entry: (new: unit -> 'Entry)>
        (isolation: ICompanionIsolation)
        (request: IsolatedRequest)
        : Async<Result<byte[], IsolationRefusal>> =
        isolation.Run(typeof<'Entry>, request)

    /// Whether the composed mode runs the native call outside the host.
    let isOutOfProcess (isolation: ICompanionIsolation) : bool =
        match isolation.Mode with
        | IsolationMode.InProcess -> false
        | IsolationMode.OutOfProcess _
        | IsolationMode.OutOfProcessWith _ -> true

    /// Ask the composition-time question: can this isolation, on this
    /// host, do what its mode says? In-process always can; an
    /// out-of-process mode with the resolved launcher can only if the
    /// host's layout resolves.
    let preflight (isolation: ICompanionIsolation) : Result<unit, IsolationProfileRefusal> =
        match isolation.Mode with
        | IsolationMode.InProcess
        | IsolationMode.OutOfProcessWith _ -> Ok()
        | IsolationMode.OutOfProcess _ ->
            ProcessIsolation.resolveLauncher ()
            |> Result.map ignore
            |> Result.mapError IsolationProfileRefusal.WorkerUnresolvable

    /// The isolation a composition profile admits. `Standard` admits
    /// any mode — a deployment reading no profile keeps whatever it
    /// composed (GP 11); `Verified` refuses `InProcess`. Both then run
    /// `preflight`, so a profile is never satisfied by a mode the host
    /// cannot honour.
    let forProfile
        (profile: CompositionProfile)
        (mode: IsolationMode)
        : Result<ICompanionIsolation, IsolationProfileRefusal> =
        let isolation = ofMode mode

        match profile, mode with
        | CompositionProfile.Verified, IsolationMode.InProcess ->
            Error IsolationProfileRefusal.InProcessUnderVerifiedProfile
        | _ -> preflight isolation |> Result.map (fun () -> isolation)

    /// The boot preflight for a composed isolation: an `IConfigValidator`
    /// that fails the start (Error) when the composed mode cannot be
    /// honoured on this host, so a deployment discovers "no dotnet host"
    /// at boot rather than on the first upload.
    let configValidator (isolation: ICompanionIsolation) : ConfigValidation.IConfigValidator =
        { new ConfigValidation.IConfigValidator with
            member _.Name = "CompanionIsolation"
            member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout

            member _.Validate() = async {
                match preflight isolation with
                | Ok() -> return ConfigValidation.ValidationResult.Ok
                | Error refusal ->
                    return ConfigValidation.ValidationResult.Error(IsolationProfileRefusal.describe refusal)
            }
        }

    /// Compose an isolation onto a `ServerApp`: registers the
    /// `ICompanionIsolation` singleton through the shared
    /// `ServiceConfig` seam and adds the boot preflight above. A
    /// deployment that never calls this composes nothing and pays
    /// nothing (GP 13); the companions that offer an isolated path each
    /// take the `ICompanionIsolation` explicitly at their own `create`,
    /// so this registration is for deployments that resolve it from DI.
    let withIsolation (isolation: ICompanionIsolation) (app: ServerApp) : ServerApp =
        let register (services: IServiceCollection) =
            services.AddSingleton<ICompanionIsolation>(isolation)

        let app = ServerApp.withConfigValidator (configValidator isolation) app

        {
            app with
                Extensions = {
                    app.Extensions with
                        ServiceConfig =
                            match app.Extensions.ServiceConfig with
                            | None -> Some register
                            | Some baseFn -> Some(fun services -> register (baseFn services))
                }
        }