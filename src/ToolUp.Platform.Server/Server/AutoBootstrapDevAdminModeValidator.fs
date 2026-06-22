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

            // Phase 230 — the bootstrap (PlatformAdminStore.bootstrap) now
            // refuses the AutoBootstrapDevAdmin fallback in an auth-requiring
            // deployment UNLESS TOOLUP_ALLOW_DEV_ADMIN_BOOTSTRAP is explicitly
            // set. So the validator keys on that opt-in rather than on
            // RequireHttps (which is false both for local dev AND for
            // production behind a TLS-terminating proxy — the gap Phase 230
            // closes):
            //   * opt-in NOT set → the field is set but will be refused at
            //     bootstrap, AND its presence in a production config is a
            //     misconfiguration to surface loudly → Error.
            //   * opt-in set → a deliberate local auth-dev bootstrap that WILL
            //     elevate the first sign-in → Warning (must never be prod).
            let optInSet =
                let v =
                    Environment.GetEnvironmentVariable PlatformAdminStore.allowDevAdminBootstrapEnvVar

                not (String.IsNullOrWhiteSpace v) && v.Trim() <> "0"

            match config.AutoBootstrapDevAdmin with
            | Some uid when requiresAuth && not (String.IsNullOrWhiteSpace uid) ->
                if optInSet then
                    return
                        Warning(
                            sprintf
                                "ServerConfig.AutoBootstrapDevAdmin = Some \"%s\" in an auth-requiring mode with %s set — the bootstrap WILL grant Platform Admin to the first sign-in when the admin list is empty. This is a deliberate local auth-dev bootstrap; it MUST NOT be a production deployment. Production deployments leave AutoBootstrapDevAdmin = None and set TOOLUP_INITIAL_PLATFORM_ADMIN instead."
                                uid
                                PlatformAdminStore.allowDevAdminBootstrapEnvVar
                        )
                else
                    return
                        Error(
                            sprintf
                                "ServerConfig.AutoBootstrapDevAdmin = Some \"%s\" in an auth-requiring mode but %s is not set. The bootstrap will REFUSE this dev-convenience field (Phase 230 — a leaked field could otherwise grant Platform Admin to the first sign-in, including behind a TLS-terminating proxy where RequireHttps = false). Production deployments MUST leave AutoBootstrapDevAdmin = None and set TOOLUP_INITIAL_PLATFORM_ADMIN; for a deliberate local auth-dev bootstrap set %s=1."
                                uid
                                PlatformAdminStore.allowDevAdminBootstrapEnvVar
                                PlatformAdminStore.allowDevAdminBootstrapEnvVar
                        )
            | _ -> return Ok
        }