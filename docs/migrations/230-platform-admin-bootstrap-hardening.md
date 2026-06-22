# Migration — Phase 230: Platform-admin bootstrap hardening

**What changed**

`ServerConfig.AutoBootstrapDevAdmin = Some uid` (a dev-convenience that
grants Platform Admin to the first sign-in when the admin list is empty)
could silently escalate in production — especially behind a TLS-terminating
proxy, where `RequireHttps = false` makes the deployment indistinguishable
from local dev. The fallback now requires an **explicit second opt-in**:

1. **`PlatformAdminStore.bootstrap` gained a `requiresAuth: bool` parameter.**
   In an auth-requiring deployment the `AutoBootstrapDevAdmin` fallback
   elevates **only** when `TOOLUP_ALLOW_DEV_ADMIN_BOOTSTRAP` is set
   (truthy, not `"0"`). Otherwise the bootstrap refuses (logs an Error, no
   elevation). A non-auth (anonymous) deployment needs no opt-in; the
   `TOOLUP_INITIAL_PLATFORM_ADMIN` env path (priority 1) is never gated.
2. **`AutoBootstrapDevAdminModeValidator`** now keys on that opt-in rather
   than on `RequireHttps`: field set + auth + opt-in unset → **Error**
   (closes the proxy-production gap); field set + auth + opt-in set →
   **Warning** (a deliberate local auth-dev bootstrap).

**Who must act**

- **Production deployments:** **no change** — production uses
  `TOOLUP_INITIAL_PLATFORM_ADMIN` and leaves `AutoBootstrapDevAdmin = None`.
  If `AutoBootstrapDevAdmin` was (incorrectly) relied on in an
  auth-requiring production deployment, it will now be refused at startup —
  set `TOOLUP_INITIAL_PLATFORM_ADMIN` instead.
- **Local auth-dev setups** that use `AutoBootstrapDevAdmin` with a real
  auth provider (OIDC / Clerk): also set `TOOLUP_ALLOW_DEV_ADMIN_BOOTSTRAP=1`
  in your dev env, otherwise the dev admin will no longer be bootstrapped.
- **Consumers calling `PlatformAdminStore.bootstrap` directly** (rare): add
  the `requiresAuth` argument (`DeploymentConfig.requiresAnyAuth config`)
  between `autoBootstrapDevAdmin` and `store`.

**Verification**

- A non-auth deployment bootstraps the dev admin unchanged.
- An auth-requiring deployment with the field set but the opt-in unset does
  **not** elevate (and the validator returns Error at preflight).
- With the opt-in set, the dev admin is bootstrapped (validator → Warning).

**Rollback**

Revert the forge commit. No data migration — bootstrap is one-shot and the
admin list is unaffected.
