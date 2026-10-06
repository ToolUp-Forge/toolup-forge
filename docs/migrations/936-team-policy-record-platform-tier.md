# Phase 936 — the per-team policy record moves to the platform tier

**What changes for you.** Team output visibility now works in a deployment that composes facts
without the AI assistant. Before this phase the per-team policy record, its store and the
`ITeamOutputVisibilitySource` the disclosure gate reads lived in the AI companion. A facts-only
deployment got the deployment default for every team, and an owner had no way to change it. The record,
its store, the source, the startup check and the output API now live in the platform tier.
`FactsCompose.withTeamOutputVisibility` registers them itself.

**Stored records are unchanged.** The record keeps its blob name,
`team-policies/ai-conversation-visibility.json`, in the team's container, and its JSON shape. A record
written before this phase reads back with the same levels on both axes. No migration runs and nothing
is rewritten. The name is historical, and it is kept on purpose: renaming the blob would mean reading two
names and writing one under the compare-and-swap, which adds a migration hazard for no gain.

**What breaks, and for whom.** These changes break only against the unreleased 0.24.1 draft. Types move
between assemblies, so code that names them needs a different `open`. The wire contract is unchanged.
`TeamOutputVisibilityApi` keeps its name, so its route is unchanged too.

| Was (0.24.1 draft) | Now |
|---|---|
| `ToolUp.AI.TeamOutputVisibilityApi`, `ToolUp.AI.TeamOutputVisibilityView` (`ToolUp.AI.Core`) | `ToolUp.Platform.TeamOutputVisibilityApi`, `ToolUp.Platform.TeamOutputVisibilityView` (`ToolUp.Platform.Core`) |
| `TeamConversationPolicyStore.TeamConversationVisibilitySettings` (+ module: `unrestricted`, `create`, `describeAllowed`) | `ToolUp.Platform.TeamConversationVisibilitySettings` (`ToolUp.Platform.Core`); `resolve` became `TeamPolicySettings.conversationOrUnrestricted` |
| `TeamConversationPolicyStore.TeamConversationVisibilityChange` | `ToolUp.Platform.TeamVisibilityChange` (`ToolUp.Platform.Core`) |
| `TeamConversationPolicyStore.TeamConversationPolicyRecord`, `TeamOutputPolicyRecord` (+ modules) | `ToolUp.Platform.TeamConversationPolicyRecord`, `ToolUp.Platform.TeamOutputPolicyRecord` (`ToolUp.Platform.Core`); the byte functions moved to `TeamPolicyRecordCodec` (`serialise`, `conversationOf`, `outputOf`) |
| `TeamConversationPolicyStore.TeamConversationPolicyStore` | `ToolUp.Platform.TeamPolicyStore.TeamPolicyStore` (`ToolUp.Platform.Server`), same members |
| `TeamConversationPolicyStore.TeamPolicyOutputVisibilitySource` | `ToolUp.Platform.TeamPolicyStore.TeamPolicyOutputVisibilitySource` |
| `TeamConversationPolicyStore.TeamVisibilityDefaultsValidator(IServiceProvider)` | `ToolUp.Platform.TeamPolicyStore.TeamVisibilityDefaultsValidator(TeamOutputVisibilitySettings, IServiceCollection)` |
| `TeamConversationPolicyStore.OutputVisibilityAudit` | `ToolUp.Platform.TeamPolicyStore.OutputVisibilityAudit` |
| `TeamConversationPolicyStore.teamOutputVisibilityApi` | `ToolUp.Platform.TeamPolicyStore.teamOutputVisibilityApi` |
| `TeamConversationPolicyStore.ConversationViewer` (a record) | the same name, now an abbreviation of `ToolUp.Platform.TeamPolicyStore.TeamPolicyViewer` |

`ServerApp` gains a field, `TeamOutputVisibility: TeamOutputVisibilitySettings option`, which
`ServerApp.empty` sets to `None`. Code that builds a `ServerApp` with `{ ServerApp.empty with … }` is
unaffected. A full record literal needs the field.

```diff
 open ToolUp.Platform
+open ToolUp.Platform.TeamPolicyStore
 open ToolUp.AI.TeamConversationPolicyStore

-let store = TeamConversationPolicyStore storage
+let store = TeamPolicyStore storage
-let bytes = TeamConversationPolicyRecord.serialise record
+let bytes = TeamPolicyRecordCodec.serialise record TeamOutputPolicyRecord.empty
```

## What each tier now owns

- **Platform.** The platform owns the record and both declarations. It registers
  `ITeamOutputVisibilitySource` and the `team-visibility-defaults` startup check from
  `withTeamOutputVisibility`. It mounts `TeamOutputVisibilityApi` in every deployment, as it does
  `IUserDirectoryApi`. Until the axis is composed, the API answers `Enabled = false` and refuses a
  change. Before this phase the API was mounted only where the assistant was composed. A deployment that
  registers its own `ITeamOutputVisibilitySource` keeps it, because the registration is a `TryAdd`.
- **AI assistant.** The assistant reads the platform record for the conversation level, and keeps the
  conversation rule, its audit rows and `TeamConversationVisibilityApi`. Whenever the assistant is
  composed it registers `TeamConversationVisibilitySettings` (`unrestricted` unless
  `withTeamConversationVisibility` declared one). That registration tells the platform that
  conversations exist, so the policy-change check and the startup check apply. In a facts-only
  deployment neither runs, because nothing can quote the output.

**The startup check now runs.** Phase 896 registered `TeamVisibilityDefaultsValidator` from a factory.
The preflight aggregator refuses factory registrations: it needs an instance it can run at compose time.
The validator is now registered as an instance, reads the conversation declaration from the finished
service collection, and so works whichever axis is composed first.

## Where the setting is visible

`ServerApp.compositionManifest` projects the declaration as two config knobs,
`TeamOutputVisibility.Default` and `TeamOutputVisibility.Allowed`. The composition inspector reads the
same manifest. A composition that does not declare it projects neither knob, so its manifest is unchanged.
The `/dev/inspect` report is **not** extended. That report is built from `ServerConfig` and the
descriptor snapshot at compose time, not from the manifest, and the declaration lives on neither, so
adding it there would mean a second projection of the same fact.

## Verifying

Run the Phase 896 and Phase 859 lists of `ToolUp.Platform.Tests`. The Phase 896 list includes the Phase 936 cases:
a facts-only composition honours an owner's level, a pre-936 record reads back byte-identical, the
startup check runs through preflight in either composition order, and the manifest carries the knobs.

## Rollback

Revert the phase. Stored records need no action, because the blob and its shape did not change.
