# Phase 1210 — a draft note lives outside an MSBuild import

**What changes for a contributor:** a change that rides or advances the core `<Version>` appends its one-line draft
note to [`DRAFT-NOTES.md`](../../DRAFT-NOTES.md) at the repository root, not to `Directory.Build.props`. Same grammar,
same append-only rule, same merge driver. No package surface, pin, wire format or version moves.

## Why

Phase 1194 replayed the last twenty server-touching landings through `Select --source graph` and found sixteen still
on the full lane, nearly all through `msbuild-import` firing on `Directory.Build.props`
([`1194-platform-pack-partition.md`](1194-platform-pack-partition.md#the-twenty-phase-replay)). The rule is right:
every project imports the props, and the project graph cannot see past an import, so a props diff must run every
test. The note was in the wrong place. A one-line prose record of what a change did has no business in a file every
project imports, and every versioned change wrote one there.

## The readers of the list, and where it went

Every reader of the draft-note list, found by searching the tree for the marker, the driver name and the phrase:

| Reader | What it reads | After this phase |
|---|---|---|
| `dev-scripts/merge-baselines.ps1`, mode `draftnote` | the marker line; the list runs from it to the comment's closing `-->`, or to the end of the file when none closes it | unchanged behaviour: in `DRAFT-NOTES.md` no `-->` closes the list, so it runs to the end of the file, which is where a change appends. Its help text now names the file |
| `.gitattributes` | binds `merge=toolup-draftnote` to one path | `/DRAFT-NOTES.md`; the props have no driver |
| `Build.fs`, `Setup` | registers `toolup-draftnote` → the `draftnote` mode by name, with no path | unchanged |
| `BaselineMergeDriverTests` (draft-note list) | the live file's marker, against the driver's | reads `DRAFT-NOTES.md`, and asserts the props carry no list and no binding |
| `Changelog.fs` / `Changelog --draft` | the API baselines and migration docs; **not** the note | unchanged; nothing to move |
| `SemVerBump.fs` / `VerifySemVerBump` | the `<Version>` in the props, with XML comments blanked first; **not** the note | unchanged; the `<Version>` stays in the props |
| a contributor | `CONTRIBUTING.md`, `docs/platform/release-process.md`, `CLAUDE.md`'s versioning section | all three name `DRAFT-NOTES.md` |

**The home is `DRAFT-NOTES.md`, not `CHANGELOG.md`'s draft section.** The shard defaulted to the changelog on the
premise that its unreleased draft section already holds the draft's prose and is read by `Changelog.fs`. Checked
against the tree, the premise does not hold since Phase 1149: the tracked `CHANGELOG.md` holds the released sections
only, the `Changelog` target rewrites the whole file from tags, baselines and migration docs, and `Changelog --check`
fails on any section it did not render ("A section headed `unreleased` in it is the pre-release-render form; the
render drops it"). `--draft` prints the unreleased section to stdout and commits nothing. A note written into the
changelog would be dropped by the next render and refused by `--check` before that. Making the renderer preserve a
hand-written region would reintroduce the concurrent-edit conflict 1149 removed. So the notes get their own file,
beside the changelog, and the file's header says why it is neither the props nor the changelog.

**The marker line is the same string.** It is spelled with `=` because an XML comment cannot contain a double
hyphen; that reason is gone in Markdown, but keeping the string means the driver and its test needed no change of
behaviour, and a branch cut before this phase that carries the driver still merges the new file.

**The props keep** the `<Version>`, the generated version-group regions and one comment pointing at
`DRAFT-NOTES.md`. The paragraph-form notes above the `<Version>` (0.21.0 to the 0.38.0 draft) are history no change
appends to any more, so they stay where they are; moving them would cost one more full-lane landing and select
nothing later.

## What the gate reads

`BaselineMergeDriverTests` opens `DRAFT-NOTES.md` from the repository, so the gate reads it. It is declared on the
`Platform` row's `rowReads` in `GateManifest.fs` and rendered into `gate-reads.json`'s `reads` by `Gate --render`,
not listed in `notRead`; `GateReadsCensusTests` holds the declaration to the literal. A note never travels alone, so
a landing that appends one re-gates for its code in any case. What changes is the lane: an import forces the full
lane, an unowned root file selects nothing.

## The lane tests

`AffectedLaneTests` keeps `msbuild-import` exactly as it was: a props change still forces everything, pinned by the
same row and the same case. Its near misses gain `DRAFT-NOTES.md`. The graph-source list gains the inverse case: a
diff of one part's source plus `DRAFT-NOTES.md` forces nothing, fires no rule, selects and skips exactly the packs the
source alone selects, and names the note as an unowned path; the same diff with `Directory.Build.props` in place of
the note forces the full lane (the falsifier).

## The twenty-landing replay, before and after

The same twenty landings as 1194's table: for each phase, the non-merge commits on `main`'s first-parent history
whose subject names the phase, oldest to newest, and the union of their paths. The method reproduces 1194's path and
server-tier counts for all twenty. Both columns ran `dotnet run --project Build.fsproj -- Select --source graph
--paths <file>` on this phase's tree. **Before** is the landing's paths as they landed. **After** relocates the
landing's draft-note change: where every commit's props diff is inside XML comments (the props with comments
stripped are byte-identical before and after each commit), `Directory.Build.props` is replaced by `DRAFT-NOTES.md`;
where a commit also changed the props' content, the props stay and `DRAFT-NOTES.md` is added. That test found a real
props change in exactly two landings, both a `<Version>` cut (1060's `d7e51a37`, 0.37.0 → 0.38.0; 1059's
`f88577f4`, 0.31.0 → 0.32.0), and it flags each of them when run against it, so it is not passing vacuously.

| Phase | Commits (oldest … newest) | Paths (server-tier) | Before: rules fired, lane | After: rules fired, lane | Packs selected / skipped (both) |
|---|---|---|---|---|---|
| 1197 | 12, `4385f2a9` … `a49fd469` | 173 (7) | `build-script`, `msbuild-import`, `solution` — full | `build-script`, `solution` — full | 39 / 9 |
| 1198 | 7, `bc1c074c` … `abf8d1ad` | 2019 (573) | `build-script`, `msbuild-import` — full | `build-script` — full | 40 / 8 |
| 1193 | 14, `dae65730` … `6fdd32d5` | 35 (13) | `build-script`, `msbuild-import`, `solution` — full | `build-script`, `msbuild-import`, `solution` — full | 44 / 4 |
| 875 | 2, `18b1a84d` … `378e359b` | 25 (6) | `build-script`, `msbuild-import` — full | `build-script` — full | 44 / 4 |
| 876 | 18, `ec54cfaa` … `d60e728a` | 382 (26) | `build-script`, `msbuild-import` — full | `build-script` — full | 45 / 3 |
| 1067 | 1, `e3daafed` | 85 (30) | `msbuild-import` — full | none — **affected** | 39 / 9 |
| 1167 | 5, `b391e573` … `3b540174` | 14 (1) | `msbuild-import` — full | none — **affected** | 33 / 15 |
| 1165 | 8, `e9a3de07` … `51aeb41b` | 24 (6) | `msbuild-import` — full | `msbuild-import` — full | 44 / 4 |
| 1161 | 6, `2a57b112` … `307452d7` | 29 (3) | `msbuild-import` — full | none — **affected** | 44 / 4 |
| 1157 | 6, `1cf969b9` … `c43917ef` | 130 (6) | `build-script`, `msbuild-import` — full | `build-script` — full | 44 / 4 |
| 1148 | 44, `9abc2994` … `b221be15` | 208 (110) | `build-script`, `msbuild-import` — full | `build-script` — full | 44 / 4 |
| 1090 | 5, `537908b7` … `12bba2a7` | 35 (11) | `msbuild-import` — full | none — **affected** | 44 / 4 |
| 1089 | 17, `a8677e98` … `841759d5` | 38 (7) | `msbuild-import` — full | none — **affected** | 44 / 4 |
| 1060 | 3, `c5672920` … `d7e51a37` | 25 (7) | `build-script`, `msbuild-import` — full | `build-script`, `msbuild-import` — full | 44 / 4 |
| 1136 | 1, `0dd7a313` | 12 (1) | `msbuild-import` — full | none — **affected** | 33 / 15 |
| 1137 | 2, `69707ac9` … `7259b9fa` | 10 (3) | none — affected | none — affected | 37 / 11 |
| 1051 | 4, `35eacf30` … `67579a62` | 32 (12) | none — affected | none — affected | 36 / 12 |
| 1064 | 7, `e25961ae` … `6b0d7280` | 21 (10) | none — affected | none — affected | 37 / 11 |
| 1050 | 1, `487cd344` | 10 (1) | none — affected | none — affected | 35 / 13 |
| 1059 | 13, `8a9cc9a6` … `e64608a9` | 50 (16) | `build-script`, `msbuild-import` — full | `build-script`, `msbuild-import` — full | 44 / 4 |

**Full-lane landings: 16 before, 10 after.** Six landings whose only forcing path was their draft note (1067, 1167,
1161, 1090, 1089, 1136) now run the affected lane, and the graph selects exactly what it selected for them before;
the note moved nothing else.

**The ten that still force, and why each is real:**

| Phase | Forcing path | Commit(s) | Reason |
|---|---|---|---|
| 1197 | `ToolUp.Forge.sln`, `GateManifest.fs` | `4385f2a9`, `1576a5fb`, `8f49634c`, `88fbe17f`, `0b27deae`, `a6857441`, `4dc3ec6f`, `fea784c2`, `a49fd469` | each companion family's test pack enters the solution and gains its gate-manifest row |
| 1198 | `Build.fs`, `Build.fsproj`, `VerifyGate.fs` | `bc1c074c` | the namespace codemod target and its source in the build script |
| 1193 | `ToolUp.Forge.sln`, `TemplateGatePackages.fs`, `src/ToolUp.Sdk/build/ToolUp.Sdk.props` | `d390076b`, `6cad253f` | six new part assemblies in the solution and the template gate's package list; the SDK manifest import lists them |
| 875 | `Build.fs` | `378e359b` | the config-key registry's build-script consumers |
| 876 | `Build.fs` | `8a353830` | the codemod rules for the options records |
| 1165 | `Directory.Packages.props` | `38eec4e5` | four package pins enter central package management |
| 1157 | `Build.fs` | `c43917ef` | an entry of the scope-literal gate's held list |
| 1148 | `Build.fs` | 43 of its 44 commits, `9abc2994` … `b221be15` | each package's sweep edits the scope-literal gate's held counts |
| 1060 | `Build.fs`, `Directory.Build.props` | `57f85970`, `d7e51a37` | the `VerifyScopeLiterals` target; the 0.38.0 `<Version>` cut |
| 1059 | `Build.fs`, `Directory.Build.props` | `2f2f2e20`, `f88577f4` | the result-bearing-write gate; the 0.32.0 `<Version>` cut |

Every one of the ten moved an import, a solution or the build script for a reason of its own. One of them is easy to
miss in 1194's table: 1165 fired `msbuild-import` on its draft note AND on `Directory.Packages.props`, and the table
names the rule once, so it read as a note-only landing. Relocating the note leaves the package pins, which force the
full lane rightly.

## Adopting it

- **A branch cut before this phase that appended a note to `Directory.Build.props`** conflicts there when it merges
  `main`, because `main` no longer has the list. Take `main`'s props and append the branch's note line to the end of
  `DRAFT-NOTES.md`.
- **Nothing to re-run.** `Setup` registers the same three drivers; the binding moved in the committed
  `.gitattributes`, which every clone reads.
- **Rollback:** revert the phase's commits. The list returns to the props and the binding with it.
