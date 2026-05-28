module ToolUp.Platform.TeamCreationPolicyValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 5f — TeamCreationPolicy wedge detector ────────────────────
//
// `TeamCreationPolicy = PlatformAdminOnly` (the default since Phase
// 5f) gates `TeamApi.CreateTeam` on `IPlatformAdminStore.IsAdmin`. A
// `Team` / `MultiTeam` deployment without any Platform Admin AND
// without `TOOLUP_INITIAL_PLATFORM_ADMIN` set is wedged: no one will
// ever pass the gate, and the bootstrap-admin path that would seed
// the first admin is also unset, so no admin can ever come into
// existence to unwedge it. The validator surfaces this at startup so
// the operator catches it before users sign in to an unusable
// platform.
//
// Error (not Warning) because the deployment as configured cannot
// produce a usable Team-mode experience — there is no path from
// "fresh boot" to "user creates a team". The operator either sets
// `TOOLUP_INITIAL_PLATFORM_ADMIN` (recommended, secure-by-default
// stays on) or flips the policy to `AnyAuthenticatedUser` (explicit
// opt-in to the pre-5f open-membership shape). Both are one-line
// fixes; an Error here is the right severity.
//
// Inert outside `Team` / `MultiTeam` (the other modes don't register
// `ITeamStore`, so `CreateTeam` is already an `Error "Team management
// not available in this mode"`). Inert when policy is
// `AnyAuthenticatedUser` (the gate is off; no wedge possible).

/// Config validator that errors when a `Team` / `MultiTeam`
/// deployment combines `TeamCreationPolicy = PlatformAdminOnly` with
/// an empty Platform Admin list AND no bootstrap-admin env var.
type TeamCreationPolicyValidator(config: ServerConfig, platformAdminStore: IPlatformAdminStore, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "team-creation-policy-wedge"
        member _.Timeout = timeout

        member _.Validate() = async {
            let teamScoped = DeploymentConfig.hasTeamScope config

            match teamScoped, config.TeamCreationPolicy with
            | true, PlatformAdminOnly ->
                let! admins = platformAdminStore.ListPlatformAdmins()

                let envSet =
                    Environment.GetEnvironmentVariable BootstrapTeam.initialAdminEnvVar
                    |> String.IsNullOrWhiteSpace
                    |> not

                if List.isEmpty admins && not envSet then
                    return
                        Error(
                            sprintf
                                "Surfaces = %s with TeamCreationPolicy = PlatformAdminOnly, but the Platform Admin list is empty and %s is unset. No caller can satisfy the CreateTeam gate, so the deployment will boot into an unusable state (every Team-mode user lands on an empty shell with no way to create a team). Fix: set %s=<userId> so the first admin is seeded automatically (recommended, secure default), or set TeamCreationPolicy = AnyAuthenticatedUser on ServerConfig (preserves the pre-5f open-membership shape)."
                                (DeploymentConfig.surfacesLabel config)
                                BootstrapTeam.initialAdminEnvVar
                                BootstrapTeam.initialAdminEnvVar
                        )
                else
                    return Ok
            | _ -> return Ok
        }