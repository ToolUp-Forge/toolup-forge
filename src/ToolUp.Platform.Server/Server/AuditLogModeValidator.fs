module ToolUp.Platform.AuditLogModeValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 6l.B — AuditLog off in authenticated modes ────────────────
//
// `ServerConfig.AuditLog` defaults to `NoAuditLog`. In Anonymous /
// dev / demo deployments that's correct — audit emission has cost and
// no caller is going to forensic-trail an anonymous session. In any
// authenticated mode (`Individual` / `Team` / `MultiTeam` /
// `AuthenticatedEphemeral`) it is *almost certainly* wrong:
//   - SOC 2 / ISO 27001 / HIPAA / GDPR Article 30 all require an
//     audit trail of admin actions.
//   - Post-incident forensics ("when was this team's permission
//     changed? by whom?") becomes impossible.
//   - The Phase 9 emission sites (`ScopeResolutionMiddleware`,
//     `PlatformApi` handlers, `fileManagementApi`) keep firing their
//     `IAuditLog.Record` calls; they just go to `NoOpAuditLog` and
//     vanish silently. No log line, no `/dev/inspect` panel saying
//     "audit is OFF", no way to detect the misconfiguration short of
//     trying to read an audit trail and finding nothing.
//
// Unlike Phase 6l.A's HeaderAuthProvider validator, this one emits
// `Warning` rather than `Error`. Some legitimate authenticated
// deployments deliberately disable audit (dev / demo / cost
// optimisation in non-regulated environments), and refusing startup
// would break them. The `Warning` surfaces:
//   - At startup as a `Warn`-level log line.
//   - In the `/dev/inspect` Validators panel (Phase 9m snapshot —
//     persists across restarts; an operator who misses the startup
//     line can still see it).
//   - To external monitors via the snapshot's stable JSON shape.
//
// Deployments that *want* audit off in authenticated mode acknowledge
// the warning by leaving the configuration as-is; the warning stays
// visible as a permanent reminder. Deployments that didn't realise
// they had it off get a clear nudge.

/// Phase 6l.B — config validator that warns when an authenticated
/// `Mode` is paired with `NoAuditLog`. Does not refuse startup —
/// some deployments legitimately want this combination.
type AuditLogModeValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "audit-log-mode"
        member _.Timeout = timeout

        member _.Validate() = async {
            let requiresAuth = DeploymentConfig.requiresAnyAuth config

            let auditOff =
                match config.AuditLog with
                | NoAuditLog -> true
                | EnabledAuditLog -> false

            if requiresAuth && auditOff then
                return
                    Warning(
                        sprintf
                            "ServerConfig.Surfaces = %s but ServerConfig.AuditLog = NoAuditLog. Audit emission sites (login, permission changes, file ops, team mutations) are firing into a no-op — no compliance trail is being kept. Set ServerConfig.AuditLog = EnabledAuditLog (or env var equivalent) unless this is a deliberate dev/demo configuration."
                            (DeploymentConfig.surfacesLabel config)
                    )
            else
                return Ok
        }