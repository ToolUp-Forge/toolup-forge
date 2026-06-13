module ToolUp.Platform.AutoBootstrapDevAdminModeValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── AutoBootstrapDevAdmin in authenticated modes ───────────────────
//
// `ServerConfig.AutoBootstrapDevAdmin = Some uid` makes the platform-
// admin bootstrap grant Platform Admin to `uid` when the admin list is
// empty and `TOOLUP_INITIAL_PLATFORM_ADMIN` is unset. It is documented
// "dev convenience — production deployments MUST leave this None" and
// logs at Warn *if* it actually fires, but nothing surfaces the
// dangerous configuration at preflight. A real production deployment on
// a real auth provider (OIDC / Clerk) that leaves this field set
// silently makes the first legitimate sign-in a Platform Admin —
// `HeaderAuthProviderModeValidator` does NOT catch this, it fires only
// for `HeaderAuthProvider`. This validator turns the documented MUST
// into an always-evaluated preflight signal.
//
// Warning (not Error): the field exists precisely so dev composition
// roots can set it (inside their own `#if DEBUG`) in auth-requiring
// local modes; a hard refusal would break the workflow the field is
// for. Mirrors `NotificationChannelInstanceValidator`'s soft-touch
// posture for configuration that is dev-legitimate but prod-dangerous.

/// Config validator that warns when `AutoBootstrapDevAdmin` is set in
/// an auth-requiring platform mode — production deployments must rely
/// on `TOOLUP_INITIAL_PLATFORM_ADMIN` instead.
type AutoBootstrapDevAdminModeValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "auto-bootstrap-dev-admin-mode"
        member _.Timeout = timeout

        member _.Validate() = async {
            let requiresAuth = DeploymentConfig.requiresAnyAuth config

            // Phase 129 — an auth-requiring deployment that requires HTTPS
            // is the production shape where a leaked AutoBootstrapDevAdmin
            // is a live privilege escalation: refuse startup. A local
            // auth-dev deployment (RequireHttps = false, the default) keeps
            // the Warning the field is legitimately used under. (Keys on
            // RequireHttps alone, NOT TrustForwardedHeaders — the latter
            // defaults to true, so it would mis-flag local dev as
            // internet-facing and break the field's intended workflow. This
            // is exactly the stricter `isHttpsTerminatedHere` intent.)
            let internetFacing = DeploymentConfig.isHttpsTerminatedHere config

            match config.AutoBootstrapDevAdmin with
            | Some uid when requiresAuth && not (String.IsNullOrWhiteSpace uid) ->
                let message =
                    sprintf
                        "ServerConfig.AutoBootstrapDevAdmin = Some \"%s\" in an auth-requiring mode. When the platform-admin list is empty and TOOLUP_INITIAL_PLATFORM_ADMIN is unset, the bootstrap silently grants Platform Admin to the first sign-in — a privilege-escalation vector if this dev-convenience field leaks into a production deployment (HeaderAuthProviderModeValidator does not catch this; it fires only for HeaderAuthProvider). Production deployments MUST leave AutoBootstrapDevAdmin = None and set TOOLUP_INITIAL_PLATFORM_ADMIN instead. Verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                        uid

                if internetFacing then
                    return Error message
                else
                    return Warning message
            | _ -> return Ok
        }