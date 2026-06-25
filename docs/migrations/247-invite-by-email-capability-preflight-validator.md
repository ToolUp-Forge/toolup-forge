# Phase 247 — Invite-by-email capability preflight validator

**Ships in:** `ToolUp.Platform.Core` (`ServerConfig.AcceptInviteByEmailWithoutDirectory`),
`ToolUp.Platform.Server` (`InviteEmailCapabilityValidator`, `TeamInvitationHandler`). **SDK 0.9.4.**
Additive, warn-only.

## What changes

`ITeamInviteApi.IssuePendingInviteByEmail` records the pending-invite blob (the invitee auto-joins on
their next authenticated sign-in) and, **when an `IUserDirectory` companion is registered**,
opportunistically emails the invitee. With no directory wired the email step is a **silent no-op**
and the invite-form recipient typeahead degrades to a free-text box — the pending row is still
written, but the invitee is never notified. Nothing flagged this; the gap surfaced only when an
invitee complained.

New preflight validator **`invite-email-capability`** (registered in
`registerFirstPartyConfigValidators`): emits a **`Warning`** when a deployment

- requires auth, **and**
- has team scope active (the SDK auto-mounts `ITeamInviteApi` only then), **and**
- has no `IUserDirectory` registered, **and**
- has not set the acknowledgement knob.

The message names the silent-email consequence and the two fixes. It **self-gates to `Ok`** for any
non-team, anonymous, directory-wired, or knob-acknowledged deployment — byte-for-byte silent unless
the gap genuinely exists (GP 11 / GP 13). It is a Warning, not an Error: a deployment may legitimately
run "operator tells the invitee out of band".

| New knob | Effect |
|---|---|
| `ServerConfig.AcceptInviteByEmailWithoutDirectory` (env `TOOLUP_ACCEPT_INVITE_BY_EMAIL_WITHOUT_DIRECTORY=1`) | Default `false`. Set to acknowledge the out-of-band-notification posture and silence the warning. |

`TeamInvitationHandler`'s opportunistic-email branch also now emits a **once-per-process `Warn`** the
first time it skips the email for a missing directory — a runtime backstop for the "started with a
directory, lost the wiring after boot" case the preflight can't catch.

## Diff to apply

**Nothing** for a deployment that wires an `IUserDirectory`, runs no team scope, is anonymous, or
already accepts the out-of-band posture.

A team-scoped, auth-required deployment that wants invite emails to send wires a directory companion:

```fsharp
// e.g. ToolUp.AuthProviders.EntraDirectory with a sender identity
app |> ServerApp.withUserDirectory (EntraDirectory.fromEnv ())
```

A deployment that deliberately notifies invitees out of band silences the warning:

```bash
TOOLUP_ACCEPT_INVITE_BY_EMAIL_WITHOUT_DIRECTORY=1
```

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- Full Expecto suite — green, including the new `Phase 247 — invite-by-email capability validator`
  pack: team/multiTeam + no directory ⇒ `Warning`; + ack knob ⇒ `Ok`; directory wired ⇒ `Ok`;
  individual / anonymous ⇒ `Ok`.

## Rollback

Unset `TOOLUP_ACCEPT_INVITE_BY_EMAIL_WITHOUT_DIRECTORY` and the validator reverts to warning when the
gap exists. The validator is warn-only — it never blocks startup — so there is nothing to roll back
beyond ignoring the advisory line. The feature adds no runtime behaviour change to the invite path
itself (the email was already a silent no-op without a directory).
