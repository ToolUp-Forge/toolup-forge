# Phase 1149 — `CHANGELOG.md` is rendered at release, the draft note is append-only, and the merge drivers survive worktrees

**Who this is for:** contributors and release operators. No package surface moves; a consumer of the
`ToolUp.*` packages has nothing to do.

## What changes

1. **A change no longer regenerates `CHANGELOG.md`.** The tracked file holds the released sections only.
   `publish-nuget.yml` renders it on the release tag, before the pack, and keeps it as the run's `changelog`
   artefact; the release's follow-up commit carries it to `main`. The released sections are byte-identical
   to what per-change regeneration produced, because both read the same tags, baselines and migration notes
   (checked on the tree this phase branched from: the old file minus its `[0.38.0] — unreleased` section is
   exactly the new render). To read the unreleased section, run
   `dotnet run --project Build.fsproj -- Changelog --draft`; it prints to stdout and writes nothing.
2. **The draft note in `Directory.Build.props` is append-only.** Below `<Version>` a marker line opens a
   list; each change appends one line at its end, `- <phase> (<version>): <class> — <one sentence>`, and
   never edits one. The paragraph blocks above `<Version>` stay as they are; only notes added from now on
   take the new form.
3. **`Setup` registers three drivers, through the clone's common directory.** `toolup-members` and
   `toolup-coverage` as before, plus `toolup-draftnote` for `/Directory.Build.props`. The script path is
   the PRIMARY checkout's (`git rev-parse --git-common-dir`, then its parent), so removing the worktree
   that ran `Setup` no longer disables the drivers for the whole clone. `Setup --check` names a dead or
   missing registration, and `VerifyAll` warns on a dead one.

## Do this once per clone

```powershell
git pull --ff-only                                         # in the primary checkout: it hosts the script
dotnet run --project Build.fsproj -- Setup                 # from any checkout of the clone
dotnet run --project Build.fsproj -- Setup --check         # exit 0, three `ok` lines
```

A clone that skips this still works: git falls back to its line merge, and two appended draft notes
conflict exactly as two paragraphs did.

## The transition merge

A branch cut before this change that regenerated `CHANGELOG.md` conflicts with it once, on the removed
`unreleased` section. Resolve it by rendering, never by picking a side:
`dotnet run --project Build.fsproj -- Changelog`, then `git add CHANGELOG.md`. The same branch's
paragraph above `<Version>` merges as it always did.

## Open phases to amend

These open phases declare `CHANGELOG.md` only to regenerate it ("a `CHANGELOG.md` entry"). Each drops
`CHANGELOG.md` from its declared files and its tasks, and declares `Directory.Build.props` for its
draft-note line instead where it does not already: 1053, 1054, 1055, 1056, 1057, 1058, 1061, 1062, 1063,
1065, 1067, 1068, 1070, 1071, 1072, 1073, 1074, 1075, 1078, 1079, 1080, 1081, 1083, 1084, 1085, 1086,
1087, 1090, 1091, 1092, 1093, 1094, 1097, 1098, 1099, 1100, 1114, 1135, 1138, 1141, 1142, 1143, 1144,
1146, 1147, 1148, 1151, 1152, 1153, 1154, 1155, 1156.

**1096 needs more than that.** Its task "the changelog is held at release" adds `Changelog --check` to
`publish-nuget.yml` and a lint requiring it. At a tag that check fails by construction now, because the
tracked file gains the tag's section only once the tag exists. This phase renders at release instead,
which is that task's purpose; 1096 drops the task, or keeps only its stale-heading finding.

## Rollback

Revert the commit. On the reverted tree, `dotnet run --project Build.fsproj -- Changelog` puts the
draft section back. A clone that ran the new `Setup`
keeps a harmless `toolup-draftnote` registration that no attribute names.
