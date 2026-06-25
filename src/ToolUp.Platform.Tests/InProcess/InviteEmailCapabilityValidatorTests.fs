module ToolUp.Platform.Tests.InProcess.InviteEmailCapabilityValidatorTests

open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// Phase 247 — `InviteEmailCapabilityValidator` warns when a team-scoped,
// auth-required deployment mounts the invite-by-email surface with no
// `IUserDirectory` companion (so invite emails silently never send),
// unless the acknowledgement knob is set. Self-gates to `Ok` for
// non-team / anonymous deployments and when a directory is wired.

let private cfg (surfaces: SurfaceProfile list) (ack: bool) : ServerConfig = {
    ServerConfig.defaults with
        Surfaces = surfaces
        AcceptInviteByEmailWithoutDirectory = ack
}

/// Minimal stand-in directory — the validator only checks for the
/// presence of an `IUserDirectory` registration, never calls it.
let private stubDirectory () : IUserDirectory =
    { new IUserDirectory with
        member _.SearchUsers(_, _) = async { return Result.Ok [] }
        member _.ResolveUsers _ = async { return Result.Ok [] }
        member _.NotifyInvitation _ = async { return Result.Ok() }
    }

let private servicesWith (directory: IUserDirectory option) : IServiceCollection =
    let services = ServiceCollection() :> IServiceCollection

    match directory with
    | Some d -> services.AddSingleton<IUserDirectory>(d) |> ignore
    | None -> ()

    services

let private validate (config: ServerConfig) (directory: IUserDirectory option) : ValidationResult =
    let v =
        InviteEmailCapabilityValidator.InviteEmailCapabilityValidator(config, servicesWith directory)
        :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "Phase 247 — invite-by-email capability validator" [

        test "Team scope + auth + no IUserDirectory + no ack → Warning" {
            match validate (cfg Surfaces.team false) None with
            | Warning msg ->
                Expect.stringContains msg "IUserDirectory" "names the missing companion"
                Expect.stringContains msg "EMAIL" "names the silent-email consequence"
                Expect.stringContains msg "AcceptInviteByEmailWithoutDirectory" "documents the ack knob"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "MultiTeam scope + no IUserDirectory → Warning" {
            match validate (cfg Surfaces.multiTeam false) None with
            | Warning msg -> Expect.stringContains msg "IUserDirectory" "names the missing companion"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Team scope + no IUserDirectory + acknowledgement knob → Ok" {
            Expect.equal
                (validate (cfg Surfaces.team true) None)
                Ok
                "the out-of-band-notification posture is acknowledged"
        }

        test "Team scope + IUserDirectory registered → Ok" {
            Expect.equal
                (validate (cfg Surfaces.team false) (Some(stubDirectory ())))
                Ok
                "a wired directory sends invite emails — no gap"
        }

        test "Individual (non-team) deployment → Ok (no invite surface)" {
            Expect.equal
                (validate (cfg Surfaces.individual false) None)
                Ok
                "no team scope ⇒ ITeamInviteApi is not auto-mounted"
        }

        test "Anonymous (non-auth) deployment → Ok" {
            Expect.equal (validate (cfg Surfaces.anonymous false) None) Ok "no auth ⇒ no invite surface"
        }

        test "Validator metadata is well-formed" {
            let v =
                InviteEmailCapabilityValidator.InviteEmailCapabilityValidator(
                    cfg Surfaces.team false,
                    servicesWith None
                )
                :> IConfigValidator

            Expect.equal v.Name "invite-email-capability" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]