# Team conversation visibility

In a team's shared storage every AI conversation lives in one container, so nothing about where a
conversation is stored says who may read it. Before Phase 859 that absence was the answer: any member
could list, search and open any other member's conversation. Phase 859 makes it a **declared,
per-team policy**.

## The three levels

| Level | Who can see a team conversation they did not start |
|---|---|
| `TeamVisible` | every member of the team (the default, and the behaviour before Phase 859) |
| `TeamAdmins` | the team's `Owner` and `Admin` roles |
| `PlatformAdmins` | holders of `PlatformRole.PlatformAdmin` only |

A conversation's **author always sees it**, at every level. A personal (non-team) conversation lives in
its owner's own container and is not governed by any team policy.

The rule lives in one place, `TeamConversationVisibility.canSee` (`ToolUp.AI.Core`), and every path
the assistant serves filters through it. The author is always the one the stored conversation
recorded (the first persisted message's `CreatedBy`), never a value from the request. A conversation
persisted before authors were recorded has no author to exempt, so only the level decides who sees it.

## Per team, with a deployment default

The level is a property of the **team**. The deployment declares the level a team starts with and the
levels a team may choose from; the team owner chooses within that set.

```fsharp skip=fragment
// On the base ServerApp, in the same shape as AICompose.withConversationTitling:
// teams start at TeamAdmins and may choose TeamVisible or TeamAdmins.
serverApp
|> AICompose.withTeamConversationVisibility TeamAdmins [ TeamVisible; TeamAdmins ]
```

Without the call, every team starts at `TeamVisible` and may choose any level, so a team that has
chosen nothing behaves exactly as before (GP 11). The declaration is checked at compose: an empty
allowed set, or a default outside it, fails startup.

The level is stored in the team's own small record (`team-policies/ai-conversation-visibility.json` in
the team's container) and read per request in the team's scope. It is deliberately **not** a field on
`TeamPermissions` and not a case on the admin-mutation union: either would break every full
construction or exhaustive match of a public type. The deployment default is configuration, not
history: changing it changes the level of every team that never chose one, as if it had always been in
force.

## What each path does

| Path | A conversation the caller cannot see |
|---|---|
| `ListConversations` | absent |
| `ListConversationsPage` | absent — filtered before the search and the page, so a search hit is always a conversation its viewer can open, and the count and cursor cover only what the viewer can see |
| `GetConversation` | reads as empty, exactly as an id that does not exist |
| `DeleteConversation` | a no-op returning `Ok`, exactly as an id that does not exist |
| `SetConversationOverride` | a no-op returning `Ok`, exactly as the delete |
| `GetTaskStatus` | `None` — a task is readable by the member who submitted it only, at every level |

A conversation's existence therefore never leaks through any of them.

## Who may delete or change another member's conversation

The author may always delete their own. Anyone else needs to be able to **see** the conversation and to
hold the level's **elevated role**:

| Level | Elevated role |
|---|---|
| `TeamVisible` | team `Owner` or `Admin` |
| `TeamAdmins` | team `Owner` or `Admin` |
| `PlatformAdmins` | a platform admin |

This covers `DeleteConversation` and `SetConversationOverride` (the per-conversation provider
override — the other write a caller can aim at a conversation; a rename does not exist). Under
`TeamVisible` a plain member can still read a colleague's conversation but can no longer delete it or
redirect it to another provider. Appending to another member's conversation was already refused by the
ownership gate, at every level.

## Changing the level

`TeamConversationVisibilityApi.SetConversationVisibility` sets the caller's **active** team's level;
the team is never a request field.

- Only the team **`Owner`** changes the level. A team `Admin` cannot: selecting `TeamAdmins` would be
  granting oneself the right to read colleagues' conversations, and the refusal says so.
- **`PlatformAdmins` is chosen, and left, only by a platform admin** (who must also be the team's owner).
  A team owner who is not a platform admin can neither select it nor undo it.
- A level outside the deployment's allowed set is refused, naming the set.
- The write is a guarded read-modify-write: an ETag compare-and-swap when the blob backend supports
  conditional writes, and a per-team gate in the process either way.

**Narrowing applies at once. Widening never exposes the past.** The record keeps every change with its
time, so the level in force when a conversation was created is always known — no field is added to the
conversation. A viewer must pass **both** the level in force when the conversation was created and the
level in force now. The three levels are not nested (a team admin who is not a platform admin and a
platform admin who is not a team admin each see what the other does not), so "the narrower of the two"
is exactly "passes both": moving a team from `PlatformAdmins` to `TeamAdmins` shows the older
conversations only to a team admin who is also a platform admin.

If the team's record exists but cannot be read, every check fails closed to the conversation's author,
and the level shown to members is `PlatformAdmins`, so a member is never told more people can read than
can.

## Audit

Two rows, both on the uniform `RemotingMethodAudited` event with an open-vocabulary kind, so no
exhaustive match over the audit union changes:

| Kind | When | Payload |
|---|---|---|
| `Custom:ConversationElevatedRead` | a viewer who is not the author **opens** a conversation governed by `TeamAdmins` or `PlatformAdmins` (at its creation or now) | `viewer`, `author`, `conversationId`, `level` |
| `Custom:TeamConversationVisibilityChanged` | every change of a team's level | `teamId`, `changedBy`, `oldLevel`, `newLevel` |

A listing or a search that merely shows a conversation is not audited; opening it is. Setting the level
a team already has writes nothing and audits nothing. Under a compliance-grade audit failure policy a
failed elevated-read row fails the open, so the content is never returned un-audited.

## What the member sees

The side panel and the full-page assistant show, beside the message input, one line saying who can see
the member's conversations under the active team's level. The team owner sets the level from **AI
Settings**, where every member sees the level in force and only the choices they may make are enabled.
