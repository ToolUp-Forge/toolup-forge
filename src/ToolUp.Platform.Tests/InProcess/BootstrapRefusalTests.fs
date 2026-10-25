module ToolUp.Platform.Tests.InProcess.BootstrapRefusalTests

// ─── Phase 1051 — a bootstrap fault refuses the boot ────────────────────
//
// A storage that THROWS on the platform-admin or runtime-config read used
// to be logged at `Error` and the boot continued — with no platform admin,
// or with a stub runtime-config store whose `Set` returned `Error`. Now the
// fault is carried to the startup preflight as a structural-class `Error`
// row naming the store and the remedy, so the boot is refused (and
// `SkipPreflight` cannot skip it), while the storage validator's own row
// stays the first thing the refusal reports.

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// A storage whose reads throw — the fault class the bootstrap used to
/// swallow. Writes and probes delegate, so only the read path is broken.
let private throwingReads () : IBlobStorage =
    let live = InMemoryBlobStorage() :> IBlobStorage

    { new IBlobStorage with
        member _.Upload(c, b, x) = live.Upload(c, b, x)
        member _.Download(_, _) = async { return raise (InvalidOperationException "storage read exploded") }
        member _.Delete(c, b) = live.Delete(c, b)
        member _.List(c, p) = live.List(c, p)
        member _.Exists(c, b) = live.Exists(c, b)
        member _.GetMetadata(c, b) = live.GetMetadata(c, b)
        member _.DownloadRange(c, b, o, l) = live.DownloadRange(c, b, o, l)
        member _.CanComposeFrom = false
        member _.ComposeFrom(c, t, s) = live.ComposeFrom(c, t, s)
        member _.Erase(c, p, policy, dry) = live.Erase(c, p, policy, dry)
    }

/// The bootstrap-fault rows a registration left in `services`.
let private faultRows (services: IServiceCollection) =
    services
    |> Seq.choose (fun d ->
        match d.ImplementationInstance with
        | :? ComposeAuth.BootstrapFaultValidator as v -> Some v
        | _ -> None)
    |> List.ofSeq

let private validate (v: ComposeAuth.BootstrapFaultValidator) =
    (v :> ConfigValidation.IConfigValidator).Validate() |> Async.RunSynchronously

let private withEnv (name: string) (value: string) (body: unit -> unit) =
    let prior = Environment.GetEnvironmentVariable name

    try
        Environment.SetEnvironmentVariable(name, value)
        body ()
    finally
        Environment.SetEnvironmentVariable(name, prior)

let tests =
    testSequenced (
        testList "Phase 1051 — a bootstrap fault refuses the boot" [
            test "a runtime-config read that throws refuses at preflight, naming the store and the remedy" {
                let services = ServiceCollection() :> IServiceCollection

                ComposeAuth.registerPlatformRuntimeConfig services ServerConfig.defaults (throwingReads ()) silentLogger
                |> ignore

                match faultRows services with
                | [ row ] ->
                    match validate row with
                    | ConfigValidation.ValidationResult.Error message ->
                        Expect.stringContains message "PlatformRuntimeConfig" "names the store"
                        Expect.stringContains message "storage read exploded" "carries the cause"
                        Expect.stringContains message "restore access to the storage" "names the remedy"
                    | other -> failtestf "expected an Error row, got %A" other

                    Expect.isTrue
                        (box row :? ConfigValidation.IStructuralClassValidator)
                        "structural class, so SkipPreflight cannot skip it"

                    match row.Fault with
                    | ComposeAuth.BootstrapRefused(store, _, _) ->
                        Expect.equal store "PlatformRuntimeConfig" "typed fault"
                    | other -> failtestf "expected BootstrapRefused, got %A" other
                | rows -> failtestf "expected one bootstrap-fault row, got %d" rows.Length
            }

            test "a platform-admin read that throws refuses at preflight instead of booting with no admin" {
                withEnv ConfigKeys.Names.initialPlatformAdmin "admin-1" (fun () ->
                    let services = ServiceCollection() :> IServiceCollection

                    ComposeAuth.registerPlatformAdminStore
                        services
                        ServerConfig.defaults
                        (throwingReads ())
                        (AuditLog.NoOpAuditLog() :> IAuditLog)
                        silentLogger
                    |> ignore

                    match faultRows services with
                    | [ row ] ->
                        match validate row with
                        | ConfigValidation.ValidationResult.Error message ->
                            Expect.stringContains message "PlatformAdmin" "names the store"
                            Expect.stringContains message "platform-admins.json" "names where it lives"
                        | other -> failtestf "expected an Error row, got %A" other
                    | rows -> failtestf "expected one bootstrap-fault row, got %d" rows.Length)
            }

            test "a healthy storage registers no fault row" {
                let services = ServiceCollection() :> IServiceCollection
                let storage = InMemoryBlobStorage() :> IBlobStorage

                ComposeAuth.registerPlatformRuntimeConfig services ServerConfig.defaults storage silentLogger
                |> ignore

                ComposeAuth.registerPlatformAdminStore
                    services
                    ServerConfig.defaults
                    storage
                    (AuditLog.NoOpAuditLog() :> IAuditLog)
                    silentLogger
                |> ignore

                Expect.isEmpty (faultRows services) "nothing to refuse"
            }
        ]
    )