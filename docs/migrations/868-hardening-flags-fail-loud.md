<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK) -->

# Phase 868 — positive hardening flags fail loud

## What changes

Six boolean environment keys whose `true` turns something on used to read any
unrecognised value as `false`, silently. They now stop startup instead:

`TOOLUP_REQUIRE_HTTPS`, `TOOLUP_MIGRATE_WEBHOOK_SECRETS`,
`TOOLUP_BACKFILL_MISSED_TICKS`, `TOOLUP_EVENT_TRIGGER_CATCHUP`,
`TOOLUP_HEALTH_STATE_TRACKING`, `TOOLUP_NOTIFY_INVITER_ON_INVITE_EXPIRY`.

Accepted: `1` / `true` / `yes` / `on` and `0` / `false` / `no` / `off`,
case-insensitive. Unset still means off. Anything else (`Ture`, `enabled`, `2`)
throws from `ServerConfig.fromEnv` with the key, the value as written, and the
accepted set:

```text
TOOLUP_REQUIRE_HTTPS=Ture is not a recognised boolean value. Expected one of: 1, true, yes, on (case-insensitive) → on; 0, false, no, off → off. Unset the variable to use the default (false).
```

Two smaller changes apply to every boolean key, strict and lenient alike:

- Surrounding whitespace is trimmed before matching, so `"yes "` now reads as
  `yes`. Before, the lenient parser read it as `false`.
- A value that is only whitespace counts as unset.

Nothing changes for the `TOOLUP_ACCEPT_*` escape hatches or
`TOOLUP_SKIP_PREFLIGHT`: a malformed value still reads as `false`, which leaves
the check they would have disabled running. No `ServerConfig` field is added.

## Affected deployments

Only one whose environment, or whose configuration manifest, holds one of the six
keys with a value outside the accepted set. A well-formed value binds exactly as
before. The deployment was running with that feature off; after the upgrade it
refuses to start until the value is fixed.

## Find it before upgrading

List the six keys as the process will see them and flag anything malformed:

```powershell
$keys = 'TOOLUP_REQUIRE_HTTPS','TOOLUP_MIGRATE_WEBHOOK_SECRETS','TOOLUP_BACKFILL_MISSED_TICKS',
        'TOOLUP_EVENT_TRIGGER_CATCHUP','TOOLUP_HEALTH_STATE_TRACKING','TOOLUP_NOTIFY_INVITER_ON_INVITE_EXPIRY'
$ok = '1','true','yes','on','0','false','no','off'
foreach ($k in $keys) {
    $v = [Environment]::GetEnvironmentVariable($k)
    if ($v -and $v.Trim() -and ($ok -notcontains $v.Trim().ToLowerInvariant())) { "$k = '$v'  <-- malformed" }
}
```

Run it in the same environment as the server (the container spec, the service
definition, the CI variable group), then search any `toolup.config.json`
manifest for the same keys; a JSON `true` / `false` is always fine.

## Fix

Set the value you meant (`1` or `0` is unambiguous), or unset the key to take
the default. A value that was a typo for `true` means the protection was never on:
after fixing it, the deployment now runs with that protection on, so test it.

## Verify

Start the server with the corrected environment. A remaining malformed value
fails at startup with the message above.

## Rollback

Downgrade the SDK package, or unset the offending key. There is no data
migration.
