# Migration — Phase 225: Team-create integrity hardening

**What changed**

1. **`ITeamStore` gains `DeleteTeam`.** A new abstract member:
   ```fsharp
   abstract DeleteTeam: teamId: string -> Async<Result<unit, string>>
   ```
   Used by the create path to roll back a half-created team when the
   owner-membership write fails.
2. **Team ids are now full-width GUIDs.** `createTeamCore` mints
   `Guid.NewGuid().ToString("N")` (32 hex chars) instead of the previous
   `[..7]` 8-char slice. The id is the data partition key, so the old
   32-bit space was birthday-collision-prone at scale.
3. **`TeamStore.CreateTeam` fails closed on an existing team blob** — a
   duplicate id returns `Error "Team '<id>' already exists"` instead of
   silently overwriting (which would co-tenant two teams onto one
   `team-{id}` container).
4. **Team-create is atomic.** A failed owner-membership write rolls back
   the team blob and returns `Error`; `TeamCreated` is audited only once
   the team is whole (record + owner).
5. **Server-side team-name validation** — empty/whitespace and
   over-length (>100 chars) names are rejected in `createTeamCore`, even
   when the Remoting API is called directly (bypassing the client).

**Who must act**

- **Consumers using the default `TeamStore`** (the overwhelming majority):
  **no action.** The default store implements the
  new member; ids, validation, and rollback are internal.
- **Consumers with a custom `ITeamStore` implementation:** add the
  `DeleteTeam` member, or the type no longer satisfies the interface:
  ```fsharp
  member _.DeleteTeam(teamId: string) = async {
      // delete the team's metadata record; a half-created team has no
      // members to clean up, so deleting the team record is sufficient.
      return! storage.Delete(container, teamBlobName teamId)
  }
  ```
  A decorator (e.g. a `SanitisingTeamStore`-style wrapper) should
  delegate it: `member _.DeleteTeam(teamId) = inner.DeleteTeam(teamId)`
  (sanitise `teamId` first if the decorator guards write keys).

**Verification**

- `dotnet build` your custom-store project — the compiler flags any
  missing `DeleteTeam` member (FS0366).
- Team creation still succeeds; a forced duplicate id is rejected, not
  overwritten; a name longer than 100 chars is rejected.

**Rollback**

Revert the forge commit. No data migration is involved — existing
short-id teams keep working (the id is opaque; only newly-minted ids are
wider). The `DeleteTeam` addition is the only source-breaking change.
