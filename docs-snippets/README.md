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

## What is not in scope

`docs/migrations/**`, as a tree. A migration doc's job is to show the **retired** shape beside
its replacement, usually in the same block; compiling it is category-incorrect, because the old
shape must not compile — that is the point of the page. This is one declared exclusion in
`Build.fs` rather than a marker on each of its 436 blocks, precisely because a per-block marker
there would be the easy opt-out that gets reached for everywhere else.

## Context

Snippets are excerpts, and the docs must not grow ceremony a reader then copies. So context is
supplied by the harness, in three layers, none of which touch the markdown:

1. **A universal preamble** — the ambient `open`s any ToolUp source file has.
2. **A per-tree preamble** — a page under `docs/rag/` is read in the context of the RAG package.
3. **Page accumulation** — an `open` written in an earlier block of the same page applies to
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

## `known-drift.txt`

When the gate landed, **230 of 655** in-scope blocks already named an API the SDK does not
have. Fixing them all is a docs project; marking them `skip=` would be a lie. They are recorded
in `known-drift.txt`, keyed by a hash of each block's own text.

The list is a **ratchet**, and it fails in both directions:

- a failing block **absent** from the list fails the gate — new drift cannot land;
- a listed block that **now compiles** also fails the gate, demanding its line be deleted.

Because the key is the block's content hash, editing a listed block retires its entry and the
block must then compile — a broken snippet cannot be quietly rewritten into a differently-broken
one.

So: **the list may only shrink.** Fixing a snippet means correcting it against the current SDK
surface and deleting its line. `--update-baseline` rewrites the file wholesale and exists for
first seeding and deliberate re-measurement; it is never part of making CI pass.

## Why this project is not in the solution

`generated/` is build output, absent from a fresh clone. A project in `ToolUp.Forge.sln` whose
sources do not exist would break `dotnet build ToolUp.Forge.sln` for everyone who has not run
the extractor. It is `IsPackable=false` and produces no public surface.

## See also

- The design rationale, in full, at the `VerifyDocSnippets` target header in `Build.fs`.
- [`docs/platform/testing-conventions.md`](../docs/platform/testing-conventions.md) — the
  repo's test-tier conventions.
- The `doc-snippets` job in [`.github/workflows/checks.yml`](../.github/workflows/checks.yml).
