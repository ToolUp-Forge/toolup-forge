module ToolUp.Platform.BootstrapTeamValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 5f — BootstrapTeam env-pair coherence ─────────────────────
//
// `BootstrapTeam.bootstrap` creates the initial team and adds the
// bootstrap admin as its Owner. The two env vars must be set
// together: `TOOLUP_INITIAL_TEAM_NAME` alone tells the bootstrap to
// create a team, but without `TOOLUP_INITIAL_PLATFORM_ADMIN` there's
// no admin user-id to add as Owner. The runtime path logs `Error`
// and no-ops in that case, but a startup-time refusal is the right
// shape — the operator clearly *intended* to bootstrap (they set the
// team-name var) so a misconfiguration should fail loud rather than
// silently produce a deployment with no team.
//
// Error (not Warning) for the same reason: a half-set env pair is a
// configuration mistake, not a posture choice. The fix is two
// equally cheap one-liners (either set the admin env var too, or
// unset the team-name env var to skip the bootstrap).
//
// Independent of `TeamCreationPolicyValidator`: that one fires on
// the wedge-by-omission shape (admin store empty, both env vars
// unset, policy = PlatformAdminOnly). This one fires on the half-
// configured-bootstrap shape (team-name set, admin unset). The two
// can both surface against the same deployment; their messages name
// different fixes.

/// Config validator that errors when `TOOLUP_INITIAL_TEAM_NAME` is
/// set but `TOOLUP_INITIAL_PLATFORM_ADMIN` is unset.
type BootstrapTeamValidator(?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "bootstrap-team-env-pair"
        member _.Timeout = timeout

        member _.Validate() = async {
            // Phase 698 — through the Phase-696 `ConfigResolution` seam, so the
            // validator sees the same pair the bootstrap itself will read.
            let nameValue = ConfigResolution.tryValue BootstrapTeam.initialTeamNameEnvVar

            let adminValue = ConfigResolution.tryValue BootstrapTeam.initialAdminEnvVar

            let nameSet = nameValue |> Option.exists (String.IsNullOrWhiteSpace >> not)
            let adminSet = adminValue |> Option.exists (String.IsNullOrWhiteSpace >> not)

            if nameSet && not adminSet then
                return
                    Error(
                        sprintf
                            "%s = '%s' is set but %s is unset. BootstrapTeam needs both: the team-name env var tells it which team to create, and the admin env var tells it which user to add as Owner. A team without an Owner is unmanageable, so the bootstrap path refuses to create one. Fix: set %s=<userId>, or unset %s to skip the bootstrap entirely."
                            BootstrapTeam.initialTeamNameEnvVar
                            ((nameValue |> Option.defaultValue "").Trim())
                            BootstrapTeam.initialAdminEnvVar
                            BootstrapTeam.initialAdminEnvVar
                            BootstrapTeam.initialTeamNameEnvVar
                    )
            else
                return Ok
        }