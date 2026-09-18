# Phase 262 — the generated CHANGELOG and the 0.x → 1.0 upgrade guide

**Stability impact:** additive. **Consumer action required:** none — but the two documents this phase
adds are *for* consumers: [`CHANGELOG.md`](../../CHANGELOG.md) is the per-release record of the public
surface, and [`upgrading-to-1.0.md`](upgrading-to-1.0.md) is the one page to read when moving off the
`0.x` line.

## What changed

Release tooling and documentation only. **No shipped code changed behaviour** and no package version
moved, so a consumer deployment is byte-for-byte what it was (GP 11 / GP 13).

1. **A new FAKE target, `Changelog`**, renders `CHANGELOG.md` at the repo root from the committed
   public-API baselines: one `## [version]` section per release tag, newest first, each carrying the
   surface movement since the release before it as **Added / Changed / Removed**, and the migration
   notes that belong to it.

   ```powershell
   dotnet run --project Build.fsproj -- Changelog            # regenerates CHANGELOG.md
   dotnet run --project Build.fsproj -- Changelog --check    # exit 1 if the committed file is stale
   ```

   The diff per interval is the one `VerifySemVerBump` (Phase 260) takes at a release — the approval
   gate's own `compareSurface` over each package's baseline at the two tags — so the changelog cannot
   disagree with the bump check about whether a member moved. `git diff --name-only` between the two
   tags names the baselines that moved and only those are read, so the cost tracks the churn: ~24 s
   over the fifty tags on the tree this was written against.

2. **`CHANGELOG.md` is seeded with the whole 0.x line** — `v0.3.0` through `v0.22.0` plus the
   unreleased `0.23.0` draft at the top (the declared `<Version>`'s movement since the newest tag,
   over the working tree's baselines). Releases before `v0.7.0` predate the baselines and say so
   rather than reporting "no change". Every entry under `docs/migrations/` is linked
   from exactly the section it belongs to.

3. **`docs/migrations/upgrading-to-1.0.md`** consolidates every breaking rename and internalization a
   consumer absorbs between `0.x` and `1.0`: the three rename clusters the Phase 183 codemod applies
   (73 / 66 / 11.C.5) with the sites it reports instead, the six Phase 256 internalizations with the
   public entry point beside each, the withdrawn packages, the open deprecations 1.0 decides, the
   record-widening class, and the five renames deliberately *not* in 1.0.

4. **The renderer is decided on every commit** by `ChangelogTests` in the `ToolUp.Platform.Build.Tests`
   pack (31 cases): a fixture diff renders Added, Changed and Removed each represented; a member
   removed and added under one name pairs into one Changed line; the render is idempotent and
   order-insensitive; a list past its cap ends with the diff that shows the rest; the two
   whole-package cases collapse to one line; and the committed `CHANGELOG.md` opens with the
   generator's header, so a hand edit reddens the pack rather than being silently regenerated away.
   The module (`Changelog.fs`, repo root) is source-linked into both the FAKE project and the test
   pack — the arrangement `SemVerBump.fs` (260) and `V1Readiness.fs` (257) already use.

## What a section says

| Line | Reads |
|---|---|
| `## [0.22.0] — 2026-08-27` | the tag's version and its commit date; the draft is `— unreleased` and carries no date |
| `Migration notes:` | the `docs/migrations/*.md` whose file name claims the version (`0.23.0-…`, or `0.3.x-…` for a line), plus the version-less ones the release's interval introduced; a claim no release answers to (`0.4.0-…` where the first 0.4 tag was 0.4.3) falls back to its interval; only docs still in the working tree are linked |
| `_Surface since …: **breaking** — N packages moved; a added, c changed, r removed …_` | complete counts, and the lockstep class the way `VerifySemVerBump` rolls it up |
| `### Added` / `### Changed` / `### Removed` | per package, ordinal; **Changed** is a member whose identity (name up to its parameter list or type annotation) is both removed and added in the interval, rendered `before → after` |
| `- … — new package (N public members)` / `— package withdrawn (N …)` | the whole-file cases, one line each; a withdrawal names the `PackageReference` a consumer must drop before raising its pin |
| `- … and N more — \`git diff v0.21.0 v0.22.0 -- api-baselines/X.approved.txt\`` | the cap: Added lists up to 20 members per package, Changed and Removed up to 50; the command shows every one |

**No timestamp and no tree SHA anywhere.** A release's date is its tag's commit date, which does not
move, and the draft has none — so the same tags, baselines and docs render the same bytes, and
`--check` can refuse a stale file without a false alarm on every commit.

## Why it is not in `verify.ps1`

The draft section at the top moves with every commit that moves a baseline, so gating the file per
commit would make every surface change a two-file commit for no per-commit fact. It is registered
beside `VerifySemVerBump` and `V1Readiness` for the same reason those are: the question is a release
question. Regenerate it at a release, and `--check` is the release workflow's guard.

## What it said on the tree it was written against

Fifty releases; the unreleased 0.23.0 draft reads **breaking** — 63 packages moved, 7,406 members
added, 103 changed, 1,304 removed, 18 packages new (the standalone grid and chart bindings among
them) and 2 withdrawn (the Entra External ID pair, per its migration entry). The 1,155 removed from
`ToolUp.Platform.Client` are the binding promotion; the six Phase 256 families are most of the rest.
None of that is news — each was already in a migration entry — but it had never been one document, and the
point of the document is that the next release's section is a diff against this one.

## Consumer adoption

⛔ **N/A.** Forge's own release documentation — a FAKE target, a repo-root build module, a test list,
and two Markdown files. Nothing a consumer composes against changed; the consumer adoption manifests
carry this phase as not-applicable for every consumer. The upgrade guide is the consumer-facing
artifact for the 1.0 line, and it is read, not adopted.
