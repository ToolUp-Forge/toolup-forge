# Team output visibility

A team can limit who sees the output its modules publish — to the whole team, to the team's admins, or
to platform admins. These are the same three levels as [conversation
visibility](../ai/conversation-visibility.md). Phase 896 added this, and it adds nothing else.

## Two axes, deliberately separate

| Axis | Decides | Where it is enforced |
|---|---|---|
| **Module permission** | whether a user may **use** a module: open it, call its API, have the assistant call its tools | the module permission map (`AccessContext.ModulePermissions`), per route and per tool |
| **Team output visibility** | who may **see** the restricted output a module published into the team | the disclosure gate, at every egress door |

**The asymmetry is intended.** A user may be allowed to see a module's published output without being
allowed to use the module. Assistant tools are filtered by module permission. Retrieval, fact queries,
exports and the browse surface serve published output to the team, whatever the viewer's module
permissions are. This is the documented behaviour, not a gap. Do not "fix" it by adding a
module-permission check to a fact door: the operator decided against that restriction, and a test pins
the current behaviour.

## What a level covers

The module says what is publishable, and the team says who sees it. A module classifies each fact it
publishes with one of the three disclosure classes. The team's output level applies to one of them:

| Disclosure class | Who sees it |
|---|---|
| `Surfaceable` | every viewer the scope admits. The team level does not apply. |
| `Restricted policyRef` | viewers that the deployment's policy **and** the team's output level both admit |
| `Internal` | nobody, at any door |

The rule is `TeamOutputVisibility.canSee` (`ToolUp.Platform.Core`), and it is the only place the rule
is written:

| Level | Who sees a `Restricted` fact |
|---|---|
| `TeamVisible` | every viewer the scope admits (the default, and the behaviour before Phase 896) |
| `TeamAdmins` | the team's `Owner` and `Admin` roles |
| `PlatformAdmins` | holders of `PlatformRole.PlatformAdmin` |

An unknown policy still denies. When the deployment has registered no `DisclosurePolicy` for a fact's
`policyRef`, the fact is withheld from every viewer, platform admins included.

## The viewer is resolved, never supplied

`DisclosurePolicyResolver` (`policyRef -> surface -> bool option`) sees no viewer, so on its own it can
only show a restricted fact to everyone or to no one. `ViewerAwareDisclosurePolicyResolver` sits beside
it and also receives a `DisclosureViewer`: the viewer's team role, whether they are a platform admin, and
the team's output level. The existing resolver type is unchanged. A composition that supplies only the
existing resolver decides exactly as before.

The **gate** builds the `DisclosureViewer`. No door builds it, and no door's arguments feed into it:

- **An HTTP request.** The viewer is the one that scope resolution stamped on the request. The
  `principal` argument that a door passes to the gate is still recorded in the audit row, but it has no
  effect on the decision.
- **An assistant turn.** The turn runs on the chat worker, where the request's ambient context does not
  flow. The turn re-establishes the viewer from the request items it carried forward.
- **A job, a webhook, or a sweep over a carried scope.** There is no viewer, so the check runs as the
  **least-privileged viewer**: no team role and not a platform admin. That viewer sees exactly what
  `TeamVisible` admits and nothing more.
- **A door that reaches a wider audience than the requester.** This applies to publishing a narrative,
  to a webhook and to a peer answer. The door is judged for the least-privileged viewer, so publishing
  cannot widen who sees restricted output. Retrieval, tool results, exports and browsing reach the
  person who asked, so they are judged for that person.

A team role is looked up only when the level is narrower than `TeamVisible`. If the team's level cannot
be read, every `Restricted` fact is denied under `team-output-visibility-unreadable`.

## Stored with the conversation level, under the same rules

A team's output level is stored in the same per-team policy record as its conversation level: the same
blob, updated by the same guarded read-modify-write, and under the same change rules. Since Phase 936
the record, its store (`TeamPolicyStore`) and everything below belong to the platform tier
(`ToolUp.Platform.Core` and `ToolUp.Platform.Server`), not to the AI assistant. A deployment that
composes facts without the assistant therefore honours each team's choice, and its owners can set it.

- The deployment sets the default and the allowed levels:
  `FactsCompose.withTeamOutputVisibility defaultLevel allowed`.
- Only the team `Owner` changes the level, and only within the allowed set. Only a platform admin can
  select `PlatformAdmins`, or change away from it.
- A narrower level applies at once, because every fact is re-checked at every door on every read.
- Every change is audited as `Custom:TeamOutputVisibilityChanged` and names who made it, the old level
  and the new level.
- `TeamOutputVisibilityApi` (`ToolUp.Platform`) reads and sets the level for the caller's active team.
  The platform mounts it in every deployment. Until the axis is composed it answers `Enabled = false`
  and refuses a change.

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withStorage blob
|> FactsCompose.withFactStore
|> FactsCompose.withTeamOutputVisibility TeamVisible [ TeamVisible; TeamAdmins ]
|> ServerApp.run
```

A team that has never chosen a level stores nothing new, so its record is byte-identical to one written
before Phase 896. The record keeps its original blob name, `team-policies/ai-conversation-visibility.json`,
so records written before Phase 936 read back unchanged. The gate reads each team's level through
`ITeamOutputVisibilitySource`. `withTeamOutputVisibility` registers the platform's source over the record
unless the deployment registered its own. With no source at all, every team is at the deployment default.

The declaration is projected onto the composition manifest as the knobs `TeamOutputVisibility.Default`
and `TeamOutputVisibility.Allowed`, which the composition inspector shows. It is not on `/dev/inspect`,
which reports `ServerConfig` rather than the manifest. The move is described in
[the Phase 936 migration note](../migrations/936-team-policy-record-platform-tier.md).

## The one coupling: conversations can quote output

An answer in a conversation can quote a restricted fact. Suppose output is limited to team admins and
conversations are visible to the whole team. A member could then read an admin's answer and see a fact
they cannot see directly.

The control is a check that runs when the team's **output level or conversation level changes**. It
refuses a combination in which the conversation level admits a member that the output level does not,
and the refusal names both levels. The levels do not form a chain: `TeamAdmins` and `PlatformAdmins`
each admit someone the other does not. The allowed combinations are therefore an equal pair, or any
conversation level with `TeamVisible` output. To narrow output, first narrow conversations to the same
level. The deployment's two defaults are checked the same way at startup.

The check applies only where conversations exist, which means where the AI assistant is composed. The
assistant registers its conversation declaration whenever it is composed. A facts-only deployment has
no conversations to quote output in, so neither the change check nor the startup check applies there.

Citations are **not** re-checked when a conversation is opened. Doing that would be the complicated
control, and it is deliberately not offered.

## Deliberately not offered

- **Job roles.** The three levels are the only vocabulary.
- **Per-user grants.** No single user can be granted access above the level.
- **Per-column audiences.** A level covers all of the team's restricted output, not individual fields.
- **Row-level restriction inside a team.** To separate regions, create a team for each region and add
  the same modules to each team. Knowledge and fact scopes are already keyed by team.
- **Re-checking citations on open.** See the coupling check above.
