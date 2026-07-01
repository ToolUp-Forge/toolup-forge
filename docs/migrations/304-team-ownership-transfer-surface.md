# Phase 304 — Team-ownership transfer surface (`TeamApi.TransferOwnership`)

**Ships in:** `ToolUp.Platform.Core` (`TeamApi`, `TeamOwnershipTransferredPayload`,
`AuditEvent.TeamOwnershipTransferred`), `ToolUp.Platform.Server` (`PlatformApiHandler`,
`AuditLog.auditEventCodecs`), `ToolUp.Platform.Client` (`TeamManagerUI`). **SDK 0.9.4+ (Wave 48).**

## What changes

Since the 2026-06-04 Platform-Management refactor, `TeamRole.Owner` was set exactly once — at team
creation via `CreateTeamWithOwner` — and was no longer assignable through any SDK-shipped UI
(`TeamManagerUI`'s role pickers expose only `[Member; Admin]` via `assignableRoles`; the invite modal
rejects `Owner`). That correctly stops an Admin self-promoting or an Owner carelessly minting a second
Owner, but it left **no affordance to transfer ownership** when the founding Owner leaves. This phase
adds that affordance as one gated operation.

```fsharp
// New TeamApi method
[<RequiresClaim "scope">]
[<Audit "Custom:TeamOwnershipTransferred">]
TransferOwnership: string * string -> Async<Result<unit, string>>   // (teamId, newOwnerUserId)
```

**Semantics.** The handler gates on the **caller's own** team role being `Owner`
(`TeamRoles.isOwner`) — *not* the Platform-Admin bypass the other membership methods honour, because
the caller *is* the outgoing Owner. The target must already be a member of the team and must not be
the caller. On success it promotes the target to `Owner` **then** demotes the caller to `Admin`
(promote-first ordering — the per-user membership store keeps each user's rows under a separate
blob/lock, so a literal single cross-user write isn't available at this seam; promoting first
guarantees the team always has ≥1 Owner, and an interruption leaves two Owners — recoverable — never
zero), and emits a `TeamOwnershipTransferred` audit event under the `team-{teamId}` scope.

Typed rejections: non-Owner caller → `"Only the team Owner can transfer ownership"`; non-member target
→ `"The new owner must be an existing member of the team"`; self-target → `"You are already the Owner
of this team"`.

### New surface

- `TeamApi.TransferOwnership: string * string -> Async<Result<unit, string>>`.
- New audit payload `TeamOwnershipTransferredPayload = { TeamId; FromUserId; ToUserId; ActorUserId }`
  and DU case **`AuditEvent.TeamOwnershipTransferred`** (+ its `eventTypeName` arm + `AuditLog`
  codec-registry row). `ActorUserId` equals `FromUserId` under the current gate; it is a distinct
  field so a future admin-driven reassignment path stays wire-compatible.
- Built-in `TeamManagerUI`: an Owner-only "Transfer ownership" action on the team-details view
  (hidden for Admin / Member / Platform-Admins who aren't the team's Owner) opening a two-step modal —
  typeahead-pick from current members, then a confirmation naming both parties.

`CreateTeamWithOwner` and the `assignableRoles` role pickers are **byte-unchanged** (GP 11).

## Diff to apply

**Nothing, for a consumer on the built-in `TeamManagerUI`** — the transfer affordance, the server
method, and the audit event all arrive with the SDK bump. No record literal changed (the addition is a
new `TeamApi` field, populated by the SDK's own `teamApiHandler`; consumers don't construct `TeamApi`).

Two consumer classes have opt-in / mechanical follow-ups:

**1. Consumers shipping an `ExternalTeamManager`.** The feature is *not* automatic for a consumer that
replaces the built-in team UI. Wire your own Owner-gated affordance against the new method:

```fsharp
// Client — via the TeamApi Fable.Remoting proxy
let teamApi : TeamApi = Api.makeProxy<TeamApi> (customOptions = UserSession.withRequestHeaders)

// on the Owner-only "transfer" action, after the user picks a current member `newOwnerId`:
Cmd.OfRemoting.call teamApi.TransferOwnership (teamId, newOwnerId) TransferDone (fun e -> ApiError e.Message)
```

The server gate is the real enforcement — gate the *button* on the caller's real membership role being
`Owner` (mirror `TeamManagerUI`, which does **not** apply the Platform-Admin bypass for this action).

**2. Consumers (or audit sinks) with an exhaustive `AuditEvent` match.** The new `TeamOwnershipTransferred`
DU case makes any wildcard-free `match` over `AuditEvent` non-exhaustive. Add a branch:

```fsharp
match evt with
| TeamOwnershipTransferred p -> // ... project p.TeamId / p.FromUserId / p.ToUserId / p.ActorUserId
| ...
```

The in-tree `AuditLog` codec registry already covers encode + decode (this is what the write path
depends on); this note applies only to consumer-side code that pattern-matches the DU directly.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean; `dotnet fable` on a `ToolUp.Platform.Client` consumer —
  compiles.
- Expecto: the new `Phase 304 — TeamApi.TransferOwnership` pack (6 cases: single-Owner invariant after
  transfer, `TeamOwnershipTransferred` audit shape, non-Owner / non-member / self rejections, no-store
  mode message) is green, and the `Phase 114 — audit-event registry exhaustiveness` reflection pack
  covers the new event automatically (encode / decode / round-trip / `eventTypeName`).

## Rollback

Purely additive — no persistence-format change. A consumer can move off the carrying SDK version with
no data migration. To revert the SDK feature, drop the `TransferOwnership` field, the
`TeamOwnershipTransferred` audit case (+ its codec row + `eventTypeName` arm), and the `TeamManagerUI`
transfer modal — but that re-opens the "no way to hand over a team" gap this phase closed.
