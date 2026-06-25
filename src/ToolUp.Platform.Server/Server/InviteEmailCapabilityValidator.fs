module ToolUp.Platform.InviteEmailCapabilityValidator

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 247 — invite-by-email capability preflight ────────────────
//
// `ITeamInviteApi.IssuePendingInviteByEmail` records the pending-invite
// blob (so the invitee auto-joins on their next authenticated sign-in
// via the `ScopeResolutionMiddleware` pending-invite hook) AND, when an
// `IUserDirectory` companion is registered, opportunistically sends the
// invitee a branded "you've been added to <team>" email. When no
// `IUserDirectory` is wired, the email step is a **silent no-op** (and
// the invite-form recipient typeahead degrades to a plain text box) —
// the pending row is still written, but the invitee is never notified.
//
// There was no preflight signal for this, even though the SDK already
// warns/refuses at startup for ~40 adjacent config concerns. The result:
// an operator stands up a team-scoped, auth-required deployment, an admin
// issues email invites, no email is ever sent, and the gap surfaces only
// when an invitee complains they were never notified.
//
// This validator makes the capability gap visible at boot. It is a
// **Warning, not an Error** — a deployment may legitimately run
// invite-by-email in an "operator tells the invitee out of band" posture
// (the pre-0.5.7 behaviour), so the signal is advisory with an explicit
// acknowledgement knob (`ServerConfig.AcceptInviteByEmailWithoutDirectory`
// / `TOOLUP_ACCEPT_INVITE_BY_EMAIL_WITHOUT_DIRECTORY=1`).
//
// Self-gates to `Ok` for any deployment that is non-team, anonymous, has
// an `IUserDirectory` wired, or has set the acknowledgement knob (GP 11 /
// GP 13 — byte-for-byte silent unless the gap genuinely exists).

/// Warn when a team-scoped, auth-required deployment mounts the
/// auto-mounted invite-by-email surface (`ITeamInviteApi`, present
/// whenever team scope is active) without an `IUserDirectory` companion —
/// so email invites silently never send. Inspects the live
/// `IServiceCollection` for an `IUserDirectory` registration, mirroring
/// the runtime `ctx.RequestServices.GetService(typeof<IUserDirectory>)`
/// resolve in `TeamInvitationHandler`'s opportunistic-email branch.
type InviteEmailCapabilityValidator(config: ServerConfig, services: IServiceCollection, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    // Evaluated inside Validate() (not the constructor) so it reflects the
    // final service collection — the aggregator runs at end-of-compose,
    // after `withUserDirectory` has had its chance to register. Mirrors
    // `DeployPlaneDepsValidator`'s services-introspection shape.
    let directoryRegistered () =
        services
        |> Seq.exists (fun d -> not (isNull d.ServiceType) && d.ServiceType = typeof<IUserDirectory>)

    interface IConfigValidator with
        member _.Name = "invite-email-capability"
        member _.Timeout = timeout

        member _.Validate() = async {
            // Self-gate: the invite surface is auth-gated, and the SDK
            // auto-mounts `ITeamInviteApi` only when team scope is active.
            // Outside that combination there is no surface to warn about.
            let relevant =
                DeploymentConfig.requiresAnyAuth config && DeploymentConfig.hasTeamScope config

            if
                relevant
                && not (directoryRegistered ())
                && not config.AcceptInviteByEmailWithoutDirectory
            then
                return
                    Warning(
                        sprintf
                            "ServerConfig.Surfaces = %s mounts the team invite-by-email surface (ITeamInviteApi.IssuePendingInviteByEmail), but no IUserDirectory companion is registered. The pending invite is still recorded — the invitee auto-joins on their next sign-in — but the invitation EMAIL is never sent and the invite-form recipient typeahead degrades to a free-text box, both silently. Wire an IUserDirectory companion (e.g. ToolUp.AuthProviders.EntraDirectory with a sender identity) so invite emails actually send, or set ServerConfig.AcceptInviteByEmailWithoutDirectory = true (TOOLUP_ACCEPT_INVITE_BY_EMAIL_WITHOUT_DIRECTORY=1) to acknowledge the out-of-band-notification posture and silence this warning. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                            (DeploymentConfig.surfacesLabel config)
                    )
            else
                return Ok
        }