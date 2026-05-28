# Phase 3d.A — Pending-invite admin UX completion

**What changes.** Two consumer-visible surfaces ship in `ToolUp.Platform.Client`:

1. A **Pending invites** view in `TeamManagerUI` exposing the `IssuePendingInviteByEmail` / `ListPendingInvitesByEmail` / `RevokePendingInviteByEmail` methods that Phase 3d / Cluster A1 (`99fa8a2`) added to `ITeamInviteApi` but left without an SDK-shipped UI. An admin (Owner/Admin) can now issue, list, and revoke pending email invitations without a custom Feliz module.
2. **Auto-resume** in the `/invite/{token}` accept page. An unauthenticated visitor's invitation token is already stashed in `sessionStorage` (the existing Phase 3d behaviour); after this phase the page also observes the auth-token transition and re-fires `AcceptInvite` automatically when sign-in completes. Recipients no longer need to manually re-open the invitation link after authenticating.

**Scope.** Client-side only. No server-side change — the API surface this phase exposes was shipped with Phase 3d. The pending-invite-by-email consumption path (`tryConsumePendingForUser` invoked by `ScopeResolutionMiddleware`) is unchanged.

**Default behaviour is unchanged for deployments without team-mode auth.** `Anonymous` / `NoAuthUI` deployments do not render `TeamManagerUI` and do not mount the `/invite/{token}` dispatcher; nothing in this phase activates in those modes.

## What was added

### `TeamManagerUI`

- New `View.PendingInvites of teamId` case + entry button on the team-details view (shown only when the caller can manage the team).
- "Pending email invites" sub-tab (currently the only sub-tab — link-based invitation UI lands as a sibling sub-tab when that follow-on phase ships).
- "Invite by email (no link)" modal: email input + role selector (`Member` / `Admin`) + expiry-in-days selector (default 7 days, matching `TeamInviteTypes.DefaultExpiry`). On submit, calls `IssuePendingInviteByEmail`; success refreshes the list + clears the form; typed `Error` is surfaced inline via the existing module-level error banner.
- Per-row Revoke action with a confirmation modal. Revoke is idempotent at the substrate (per `RevokePendingInviteByEmail` doc) so a double-click leaves the operator in a consistent state.
- Empty-state copy: _"No pending email invitations. Use 'Invite by email' to add one — the recipient will auto-join the team on their first sign-in matching the email."_

### `InviteAccept`

- One-shot auth-token-change observer installed alongside the existing first-mount accept flow. When the visitor is unauthenticated, the existing `stashToken` write remains; a polling subscription on `UserSession.onAuthTokenChange` then waits for the token to transition from `None` to `Some _`. On that transition the stashed token is drained from `sessionStorage` and `AcceptInvite` is invoked with it. Success / error rendering is reused.
- `sessionStorage` key cleanup on every terminal state (`Accepted` / `Failed` / second-mount re-acceptance) so a subsequent sign-in for a different invite cannot replay a stale stash.

### `UserSession`

- New `onAuthTokenChange : (string option -> unit) -> (unit -> unit)` helper. Returns a dispose callback. Internally a `setInterval`-backed loop polls `getAuthToken ()` every 2 s and fires the callback on a `None ↔ Some` transition; identical-value polls do not fire. This is the same auth-bridge polling pattern Phase 3b shipped, scoped to per-component observers rather than the bridge's global refresh.
- `AuthUIProvider` reviewed and intentionally **not** extended. The module's role is the sign-in-UI handler registry (the `setHandlers` / `gate` pattern from Phase 13a); a state-transition observer fits naturally in `UserSession`, which already owns the auth-token storage + the periodic refresh from the auth bridge.

## When to adopt

The Pending invites view activates automatically for any deployment that wires the SDK's built-in `TeamManagerUI` (`Mode = Team` / `MultiTeam` with `ClientConfig.TeamManager` left at its default). Consumers that swap in an `ExternalTeamManager` do not pick this up automatically — they extend their own UI to call the same `ITeamInviteApi` methods if pending-by-email is in scope for their product.

The InviteAccept auto-resume activates for any deployment that registers the `/invite/{token}` public-entry dispatcher (the Phase 3d default in `Team` / `MultiTeam` modes).

No `ClientConfig` change is required. No `ServerConfig` change is required.

## Diff to apply

For deployments that already adopted Phase 3d, the upgrade is bundle-only — bump the `ToolUp.Platform.Client` package and re-publish the client bundle. No source edits in the consumer.

For deployments that hand-rolled their own team-management UI in place of `TeamManagerUI` and want the new pending-by-email surface:

```fsharp
// Add an ITeamInviteApi proxy at module level (mirrors the existing
// TeamApi proxy pattern — header freshness is the CsrfClient request
// guard's job per the Phase 64 convention).
let private inviteApi: ITeamInviteApi =
    Api.makeProxy<ITeamInviteApi> (
        routeBuilder = TeamInviteApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

// Issue a pending-by-email invitation (the no-link path):
let! result =
    inviteApi.IssuePendingInviteByEmail {
        TeamId = teamId
        Email = "carol@example.com"
        Role = TeamRole.Member
        ExpiresIn = Some (TimeSpan.FromDays 7.0)
    }

// List for a team (Owner/Admin only):
let! pending = inviteApi.ListPendingInvitesByEmail teamId
// pending : Result<(string * PendingInviteByEmail) list, string>

// Revoke (idempotent):
let! () = inviteApi.RevokePendingInviteByEmail "carol@example.com"
```

For deployments that drive `/invite/{token}` from their own component instead of `ToolUp.Platform.InviteAccept.render`, install the new `UserSession.onAuthTokenChange` hook on mount:

```fsharp
React.useEffect (
    (fun () ->
        let dispose =
            UserSession.onAuthTokenChange (fun token ->
                match token, stashedInviteToken () with
                | Some _, Some stashed ->
                    clearStashedInviteToken ()
                    triggerAccept stashed
                | _ -> ())

        React.createDisposable dispose),
    [||])
```

## Verification

1. `dotnet build ToolUp.Forge.sln` clean.
2. `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` passes the extended `TeamInvitation` test list including the new server-side regression:
   - `PendingInviteStore.remove then matching-email sign-in is a no-op; no auto-join audit`. Tests the revoke → consume-attempt round-trip against the in-process `tryConsumePendingForUser` helper, asserting the post-revoke semantic the new TeamManagerUI revoke action commits to. Drives the revoke through the cache-bypass substrate (`PendingInviteStore.remove`) rather than `RevokePendingInviteByEmail` to avoid the documented module-level-cache trampling that affects sibling parallel tests of `listAll`-shaped paths — the API handler's gate enforcement is already covered transitively by the existing `IssuePendingInviteByEmail` test.

   The `InviteAccept` stash-resume flow is browser-side (sessionStorage + `UserSession.onAuthTokenChange` polling + React mount lifecycle) and is verified manually per step 4 below. The pure-F# resume predicate `InviteAccept.shouldResumeAccept` is a four-line pattern match; adding a Platform.Tests project reference on Platform.Client (which would pull the full Fable/Feliz/Elmish transitive surface into a .NET test runner that never reaches that code) was judged a worse trade than the manual verification step.
3. Manual: in a `Team`-mode deployment, sign in as an Owner, open the Teams page → click "Manage" on a team → click "Pending invites" → "Invite by email". Enter a colleague's email + role + expiry, submit. Confirm the entry appears on the list. Have the colleague sign in for the first time with the matching email; confirm they auto-join.
4. Manual: in an incognito window, open a valid `/invite/{token}` URL without a current session. Confirm the page redirects to sign-in. Complete sign-in; confirm the page transitions to "Welcome to <team>" without re-opening the link.

## Rollback

The new surface is additive and gated by existing `ClientConfig.Mode` / `ClientConfig.TeamManager` defaults. Reverting requires re-publishing the consumer bundle against the previous SDK version; no on-disk state migrates. The `sessionStorage` key (`toolup.pendingInviteToken`) has been written by Phase 3d already — leaving it behind is harmless (consumed on the next valid invite acceptance, ignored otherwise).

## Consumers

Downstream consumers in `Team` / `MultiTeam` modes adopt automatically once they pick up the SDK bump — the surface arrives via `TeamManagerUI` auto-injection. Consumers in `Individual` / `Anonymous` modes are **N-A**.
