# ToolUp.DeadCode

Lists definitions in this repository that have **no call sites**.

```bash
dotnet run --project Build.fsproj -- DeadCodeReport            # the report
dotnet run --project Build.fsproj -- DeadCodeReport --verbose  # every finding
dotnet run --project tools/ToolUp.DeadCode -- --json           # machine-readable
dotnet run --project tools/ToolUp.DeadCode -- --fail-on-dead   # exit 1 on any finding
```

Shipped by Phase 626. **Read "What this does not find" before trusting a number
from here.**

---

## Why this exists

`DatadogLogsAuditSink.extractEventScopeId` was a 132-arm match over `AuditEvent`.
It drifted **51 arms behind** the DU and nothing failed — because nothing called
it. Phase 66 had promoted `ScopeId` to a first-class `AuditEnvelope` field, every
sibling audit sink moved to `envelope.ScopeId`, and this one helper kept
compiling in place, silently, for as long as it took someone to read it by hand.

The general question that instance raised was not "is this one dead" but **"how
would anyone find the next one?"** — and the answer was that nothing in the repo
could.

## What the compiler already tells us

Nothing useful, and this was measured rather than assumed. With
`--warnon:1182` enabled on a probe project on the workspace's F# 10 / .NET 10
baseline:

| Shape | Warned? |
|---|---|
| unused **local** binding inside a function | **yes** — `FS1182` |
| module-level `let private`, zero callers | no |
| module-level `let internal`, zero callers | no |
| module-level `let` (public), zero callers | no |
| `type private`, no users | no |

FS1182 is scoped to a single function body. It cannot see across bindings, let
alone across files or projects, so the exact shape that bit us —
a module-level `let private` — is the one shape it is guaranteed to miss.

Turning `--warnon:1182` on repo-wide would therefore not have caught
`extractEventScopeId`, and is a separate (worthwhile) question about local
hygiene rather than an answer to this one.

## The one idea this rests on

The hard part is **not** finding definitions. It is false positives.

This SDK ships `.fs` source under `fable/` in its nupkgs. A Fable consumer
extracts that source and compiles it into their own project — so a helper with
no in-repo caller may be a deliberate public affordance whose callers live in
someone else's tree entirely. No analysis run inside this repository can see
those callers. Not a grep, not a call graph, not a full FSharp.Compiler.Service
typed-reachability pass. **Precision on that axis is not purchasable at any
price**, which is the reason this tool does not attempt an expensive analysis:
the expensive option costs a great deal and does not fix the thing that
dominates the noise.

So instead of filtering false positives heuristically, the tool **restricts its
scope to definitions that cannot have one**, by language rule:

### Tier P — module-level `let private`

F# confines a `private` binding's callers to the declaring module, and a module
is declared in exactly one file. The search corpus is therefore **that one
file**, and within it the analysis is complete. A consumer who extracts the
Fable-packed source still cannot call it — their code is in different modules.
The dominant false-positive class is *dissolved by construction*, not filtered.

This is the tier that matters: 6,220 of the 6,239 candidates, and the motivating
instance was one of them.

### Tier I — module-level `let internal`

Callers are confined to the declaring assembly, so the corpus is the whole repo
(a safe superset — see "Corpus widening" below). **Excluded** when the owning
project either:

- packs its source under `fable/` — the consumer compiles it into *their*
  assembly, where `internal` is reachable from their code; or
- grants `InternalsVisibleTo` — the internals are legally reachable from another
  assembly's source.

59 of 78 `internal` candidates are excluded on those grounds and counted as
`skipped (escapes)` in the report, so the tool's own coverage stays legible
rather than looking like a clean sweep.

### Public bindings — deliberately out of scope

Their caller set is unbounded, so "no in-repo caller" carries no information at
all. Reporting them is precisely the failure mode where an analysis flags every
Fable-packed helper and trains its reader to ignore it.

Narrowing the *public* surface is a real and separate question with its own
instrument: **[Phase 256](../../../Diametrical/roadmap/phases/256-public-api-surface-minimization-sweep.md)**,
which triages every symbol in `api-baselines/*.approved.txt` as intended-contract
vs accidental-plumbing and applies `internal`/`private`.

**256 shrinks what is *exposed*; this finds what is *unreachable*.** Neither
subsumes the other, and the motivating instance proves it: `extractEventScopeId`
was `private`, so it never appeared in a public baseline and 256 structurally
could not have seen it. Conversely this tool says nothing about an over-exposed
public helper with fifty in-repo callers.

## Report, never delete

Exit code is **0 by default, even with findings**. Unreachable-today is not
always unwanted — a seam awaiting its first implementor is the obvious
counter-example and this SDK is full of them. Deletion stays a human decision.

`--fail-on-dead` exits 1 for a caller who wants a gate.

### The count, and why this is a report and not a CI gate

**First run (Phase 626, 2026-07-31): 13 unreferenced, out of 6,239 candidates
across 1,957 source files.** All 13 were hand-verified as true positives — no
false positive in the run. Phase 626 then resolved one of them
(`extractEventScopeId`, deleted), leaving a **standing backlog of 12**.

At that count a gate would be *feasible* — the count is not the constraint. The
constraint is that **the tool cannot distinguish "dead" from
"not yet used", and in this SDK both are legitimate.** A gate makes deliberately
keeping an unused seam cost a suppression mechanism, and the pressure then runs
toward deleting seams to get the build green — which is the opposite of what
GP 13's opt-in-substrate design wants.

Promote it to a gate when **both** hold:

1. the backlog is at zero, so the gate starts green rather than needing a
   baseline; and
2. a suppression affordance exists — an attribute or an allowlist file — so
   "keep this deliberately" is expressible in-tree.

Until then: run it periodically, and read it.

## What this does not find

Written down so a future reader does not over-trust the output. Every one of
these errs toward **under**-reporting (a missed finding), except where marked.

1. **Public and default-visibility bindings.** Out of scope by design, above.
2. **Types, DU cases, record fields, and members.** Only `let`-bound module-level
   *values* are analysed. A private record type used solely through inference is
   never textually mentioned at its use sites, so including types would produce
   over-reports — the one direction that destroys trust — and they are excluded
   for that reason rather than by oversight.
3. **Local bindings.** Excluded because the compiler already warns (FS1182);
   reporting them would duplicate a signal that exists and dilute one that does not.
4. **Dead clusters peel one layer per run.** The analysis is direct-reference,
   not transitive-reachability. If dead `f` calls `g` and nothing else calls `g`,
   only `f` is reported; `g` surfaces on the *next* run after `f` is removed.
   Verified deliberately during Phase 626's red-proof.
5. **Reflection.** A binding invoked via `BindingFlags.NonPublic` looks dead.
   There are ~23 `BindingFlags.NonPublic` sites in `src/`; none currently target
   a module-level `let private` value, but a future one would not be seen.
6. **Shadowed names.** When a name is declared twice in its corpus, occurrences
   cannot be attributed, so the candidate is classed `Ambiguous` and never
   reported (17 at the time of writing).
7. **Text-level matching, not symbol resolution.** A record field or an unrelated
   type sharing the binding's name counts as a reference. Under-reports.
8. **Interpolated strings are treated as code, on purpose.** `$"{formatThing x}"`
   contains a genuine reference, so blanking it would turn a live helper into a
   false positive. The cost is that literal prose inside an interpolated string
   can mask a dead binding. The safe side of the trade.
9. **Corpus widening for `internal`.** Tier I searches the whole repo rather than
   modelling each project's exact `<Compile>` closure. A same-named binding in an
   unrelated project counts as a reference. Under-reports; keeps the tool free of
   an MSBuild evaluation it would otherwise need.
10. **Conditional compilation.** `#if` branches are scanned as ordinary text, so
    a reference inside an inactive branch still counts.
11. **`Ambiguous`, `skipped` and `Self-reference only` are not findings.**
    "Self-reference only" means a recursive binding nothing else calls — almost
    certainly dead, but reported separately so the claim stays exactly as strong
    as its evidence.

## Implementation notes

Single-file F# console app, `FSharp.Core` only, no FSharp.Compiler.Service and
no MSBuild evaluation — deliberately, per the reasoning above. Not in
`ToolUp.Forge.sln`, matching `tools/ToolUp.PortGuard`. Full run over ~1,960
source files takes about 6 seconds.

Source text is passed through a **blanking pass** that overwrites comments and
non-interpolated string literals with spaces, preserving line and column geometry
so prose cannot masquerade as a call site. This is not cosmetic: one of the 13
findings (`SurfaceCoherenceValidator.severity`) is mentioned in a `//` comment
elsewhere in its own file, and without blanking it would read as live.

Two lexing details are load-bearing and were both found by reading output rather
than exit codes:

- The binding regex ends in a negative lookahead, **not** `\b`. A `\b` backtracks
  off F#'s apostrophe-suffixed identifiers — no word boundary exists after the
  `'` — so `let private member'` was extracted as `member`, and references were
  then counted against the wrong name. That produced three phantom findings.
- `'` is an identifier character (`x'`) and a generic-parameter sigil (`'T`) as
  well as a char delimiter, so char literals are only blanked when the text
  genuinely lexes as one. Mis-handling `'"'` would desynchronise the string
  scanner for the rest of the file.
