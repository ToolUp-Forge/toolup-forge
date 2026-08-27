# Compile-checked documentation snippets

The `fsharp` code blocks in `docs/**` and `src/ToolUp.Platform/technical-guide/**` are
compiled against the real SDK on every push and pull request.

Run it exactly as CI does:

```
dotnet run --project Build.fsproj -- VerifyDocSnippets
```

**Why.** A reader copies a fenced block verbatim. When a phase renames or removes the thing
the block calls, the block becomes a lie that compiles nowhere and fails at the reader's first
build — and nothing notices, because docs are prose to every test in the repo. The two
instances that motivated this gate were found minutes apart by a human reading the page: a
stale OIDC block in `docs/platform/auth.md` carrying three separate falsehoods, and a
`Mode = Individual` block in the technical guide teaching a field retired by Phase 66.

What the gate asserts is **that every SDK name a snippet uses still exists, with the shape
shown** — not that the block is a runnable program.

## Writing a snippet

Nothing to do. Write the block; it is checked.

```fsharp
let config = { ServerConfig.defaults with Surfaces = Surfaces.individual }
```

You do **not** add `open` lines for the sake of the harness. Ambient context is supplied
out-of-band so the docs stay copy-clean (see [Context](#context) below).

## When a block genuinely cannot be compiled

Mark the fence with a reason from a **closed set**. Anything else — a bare `skip`, an
invented reason, any other attribute — fails the target by name and line.

````markdown
```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withStorage (LocalFileStorage("./data") :> IBlobStorage)
|> ...
```
````

| Reason | Means |
|---|---|
| `fragment` | An excerpt: an elided body (`\|> ...`), or it reads locals belonging to a surrounding program the page does not show. |
| `signature` | An `.fsi`-shaped API listing (`module M =` with `val` bindings). Not implementation F#, so it cannot be compiled as such. |
| `anti-pattern` | Deliberately wrong, shown as a "don't". |

The marker is invisible to readers: every Markdown renderer takes the **first** word of the
info string as the language, so `fsharp skip=fragment` still highlights as F#.

**"It does not compile" is not a reason.** `skip` claims a block is not *checkable*. A block
that is checkable and wrong belongs in the baseline below — or, better, gets fixed.

**Reach for `fragment` last.** A skip buys silence: nothing then checks the block's SDK names, so
the next rename rots it invisibly — the exact drift class this gate exists to catch, occurring in
the blocks the gate cannot see. Most blocks marked `fragment` are accurate and merely read locals
the page never shows in full; those belong in a [per-page ambient preamble](ambient/README.md),
which keeps them under the gate. `fragment` is right only for a genuinely elided, prose-shaped
excerpt.

## What is not in scope

Two trees, and they are the same class: **point-in-time documents whose code deliberately
reflects a state other than the current surface.**

- `docs/migrations/**` — a migration doc's job is to show the **retired** shape beside its
  replacement, usually in the same block. Compiling it is category-incorrect, because the old
  shape must not compile; that is the point of the page.
- `docs/design/**` — a design record states what was **proposed**. Where implementation
  diverged, rewriting the blocks against the shipped surface would destroy exactly the value the
  document has, and marking them individually would mean widening the closed skip set to cover
  "historically accurate".

Both are declared exclusions in `Build.fs` rather than a marker on each of their blocks,
precisely because a per-block marker there would be the easy opt-out that gets reached for
everywhere else. Widening the list needs the same argument, and it is a visible diff.

## Context

Snippets are excerpts, and the docs must not grow ceremony a reader then copies. So context is
supplied by the harness, in four layers, none of which touch the markdown:

1. **A universal preamble** — the ambient `open`s any ToolUp source file has.
2. **A per-tree preamble** — a page under `docs/rag/` is read in the context of the RAG package.
3. **A per-page ambient preamble** — an optional F# file at `ambient/<doc path>.fs` declaring the
   composition-root locals a page's blocks conventionally read (`config`, an Elmish `Model`, a
   page-local loader). This is what lets an accurate excerpt be *checked* rather than
   `skip=fragment`'d into silence. See [`ambient/README.md`](ambient/README.md).
4. **Page accumulation** — an `open` written in an earlier block of the same page applies to
   later blocks, which is how a reader reads a page top to bottom.

Each block then compiles as its own module in its own generated file under `generated/`
(gitignored; regenerated on every run). One file per *block* rather than per page is
load-bearing: F# abandons a file at its first parse error, so a single malformed block in a
shared file would mask every later block on the page.

Errors point at the **markdown**, not at a generated artefact — each generated file carries an
F# `# <line> "<path>"` directive, so the compiler itself reports
`docs/platform/auth.md(88,13): error FS1129: …`.

A snippet that names a package this project does not reference reports its symbols as
undefined. That is a harness gap, not doc drift — add the `ProjectReference` to
`ToolUp.DocSnippets.fsproj`. The target refuses to attribute an unplaceable compiler error to a
documentation block, so this failure mode is loud rather than silent.

## `known-drift.txt` — empty, and enforced empty

When the gate landed, **231 of 655** in-scope blocks already named an API the SDK does not
have. Fixing them all at once was a docs project, and marking them `skip=` would have been a
lie, so they were recorded in `known-drift.txt` as a **ratchet** that could only shrink.

**The ratchet reached zero.** Empty is now the enforced state: any entry in that file fails the
gate. A block that does not compile is a failure to fix against the current SDK surface, not a
line to record — and the only reason the list existed was drift that predated the gate, which no
longer exists.

`--update-baseline` still rewrites the file wholesale from a run, and is still the documented
escape for a deliberate re-measurement. It is not a way of going green: whatever it writes there
fails the gate too.

If you are reading a line in that file, the key is the **full triple**
`<doc path>#<ordinal> <hash>`, which is also the literal text of the line's first two fields.
Never match on the hash alone: identical illustrative blocks in different files legitimately
share one, and a hash-only prune during the 2026-08-21 burn-down duly deleted the wrong file's
line, caught only by the next full run.

## `corpus-floor.txt`

The number of blocks that compile, recorded as a **high-water mark**.

A static floor catches a collapsed harness and nothing else. It cannot ratchet: as the corpus
grows, the gap between the floor and the truth widens into room for silent hollowing, because
skip-marking a block that used to compile costs one checked block and the gate says nothing.

- **Below the mark fails.** Blocks that used to be checked no longer are. If the loss is
  deliberate and argued — a tree exclusion, a page deleted — lower the number **by hand**, in the
  same commit, so the decision is in the diff a reviewer reads. Deliberately not a flag: an
  automated lower is the one motion this guard exists to make expensive.
- **Above the mark rewrites it** and says so. Growth is always legitimate and must not red a
  build over a docs addition, but the new number lands in your working tree and rides your own
  commit. Only the number is rewritten — every comment in the file is preserved, so the argument
  written beside a past shrink is not erased by the next growth.

## Reading the summary

```
blocks compiled : 271 (high-water mark 271)
passing         : 271
known drift     : 0 (docs-snippets/known-drift.txt)
new failures    : 0
fixed-but-listed: 0
skip=fragment     359
skip=signature    54
ambient pages   : 31 (docs-snippets/ambient/)
redeclared types: 296 in 166 block(s) — 290 compared, 6 not comparable, 12 ambiguous
fragment symbols: 359 fragment(s) walked — 1398 identifier(s) checked, 707 resolved,
                  31 local, 625 outside, 35 ambiguous; 172 record region(s)
unresolved opens: 9 skipped block(s) — illustrative, or moved?
```

The last lines measure the **blind spot**, which is the part worth watching once the failures are
zero.

`skip=` counts are the blocks nothing checks. `ambient pages` is how many pages have bought their
way out of that. `unresolved opens` names skipped blocks whose `open` does not resolve — read it
as a watchlist, not a defect count: an `open` of a deliberately fictional vendor namespace is
right in an illustrative fragment, while an `open` of a real SDK namespace that has since moved
is rot the gate cannot act on, because the block declared itself uncheckable. Only reading them
tells you which.

## The fragment symbol-existence lint

`skip=fragment` exempts a block from **compilation**, never from being true, so a rename rots it
silently — the drift class this gate exists to catch, in the pool the gate cannot see. Two
compile-free arms hold those blocks to a weaker claim: every dotted, capitalized identifier
(`ServerApp.withStorage`) and every record-construction field label either **resolves against the
public-surface name universe** the `api-baselines/` render, or names something the block itself
introduces, or fails the target by name and line.

It asserts **existence, not correctness** — a renamed API is caught, a retyped one is not. Zero
findings is the enforced state, and there is no baseline: it landed with its corpus burnt down.

**What it will not fire on**, so a fragment stays free to be an excerpt: a lowercase root
(`ctx.Progress.Report`), a placeholder or vendor name nothing in the universe answers to
(`MyModule.analyse`, `Fable.Core.JsInterop`), a name the block or any earlier block on the page or
the page's ambient preamble declares, an ambiguous simple name, and anything inside a string
literal, a comment, or an `open` / `namespace` / `module` path. The governing idea is one line: the
lint speaks only about names the surface owns. The full rule, with the measurements behind each
threshold, is at the `VerifyDocSnippets` header in `Build.fs`.

**It is a bridge and it is meant to be deleted.** Every fragment converted to a compiled block —
usually by giving its page an [ambient preamble](ambient/README.md) — leaves the lint's universe by
construction, because the scan reads `skip=fragment` and nothing else. When the `skip=fragment`
count reaches the floor that conversion work lands on (the residue of genuinely elided,
prose-shaped excerpts), the whole section goes, census line included. The `fragments walked` figure
is what that decision reads.

## Why this project is not in the solution

`generated/` is build output, absent from a fresh clone. A project in `ToolUp.Forge.sln` whose
sources do not exist would break `dotnet build ToolUp.Forge.sln` for everyone who has not run
the extractor. It is `IsPackable=false` and produces no public surface.

## See also

- The design rationale, in full, at the `VerifyDocSnippets` target header in `Build.fs`.
- [`docs/platform/testing-conventions.md`](../docs/platform/testing-conventions.md) — the
  repo's test-tier conventions.
- The `doc-snippets` job in [`.github/workflows/checks.yml`](../.github/workflows/checks.yml).
