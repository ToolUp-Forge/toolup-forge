# Phase 257 — the v1.0 readiness scorecard (graduation gate)

**Stability impact:** additive. **Consumer action required:** none.

## What changed

Release tooling only. **No shipped code changed behaviour** and no package version moved, so a
consumer deployment is byte-for-byte what it was (GP 11 / GP 13).

1. **A new FAKE target, `V1Readiness`**, scores six preconditions for the 1.0 tag and writes
   [`docs/reference/v1-readiness.md`](../reference/v1-readiness.md) — a regenerated table, each row
   pass / fail with its measured value and the threshold it was graded against. "Are we 1.0 yet?"
   is now a deterministic, regenerable answer instead of a judgement call.

   ```powershell
   dotnet run --project Build.fsproj -- V1Readiness                   # regenerates the document
   dotnet run --project Build.fsproj -- V1Readiness --require-ready   # exit 1 unless every row passes
   ```

   The default **writes and exits 0 whatever it says** — the scorecard is a report, and on the day
   it was first generated five of its six rows were red. `--require-ready` is the graduation gate
   itself, for the release workflow at the 1.0 cut. It is registered beside `VerifySemVerBump` and,
   for the same reason, is **not** in `verify.ps1`: the question is a release question, and the
   stability row's reading of git is not a per-commit fact.

2. **The thresholds are declared, not hard-coded**, in [`v1-readiness.json`](../../v1-readiness.json)
   at the repo root. Every key is optional and falls back to a built-in default; an unknown key is
   refused rather than silently read as the default. A decision to accept a measured value — to
   carry a deprecation into 1.x, say — is a change to that file, recorded in git.

3. **The rule each row applies is decided on every commit**, by `V1ReadinessTests` in the
   `ToolUp.Platform.Build.Tests` pack: a known-good fixture scores all-green, one fixture per cause
   scores exactly that row red, every unavailable input is an explicit `not yet` row, and the
   committed inputs parse. The module (`V1Readiness.fs`, repo root) is source-linked into both the
   FAKE project and the test pack rather than copied — the arrangement `SemVerBump.fs` (260),
   `SdkManifest.fs` (326) and `PublishedSmoke.fs` (184) already use, and for the same reason.

4. **Phase 259's `ConformanceCoverage` module moved namespace**, from the test pack's to
   `ToolUp.Forge`, so the FAKE project can source-link it for the coverage row: that assembly
   defines `ToolUp.Platform.Build` as a *module*, which no namespace of the same name can coexist
   with (FS0247). The file did not move and nothing about the gate changed.

## The six rows

| Row | Reads | Passes when |
|---|---|---|
| `baseline-stable` | the release tags reachable from HEAD, each diffed against the working tree's `api-baselines/` (the doc-coverage sidecar excluded) | the newest N releases all shipped the surface the tree carries now |
| `conformance-coverage` | Phase 259's own derivation of replaceable seams and their packs | every derived seam is packed |
| `doc-coverage` | Phase 261's `api-baselines/doc-coverage.approved.txt`, summed | coverage ≥ the threshold (default 100% — the doc-coverage policy's own sentence: the 1.0 surface should be the documented surface) |
| `undecided-renames` | the rename-decision table in Phase 256's migration doc | every row states a decision — **fail-closed**: a wording the parser does not know counts as undecided |
| `adoption-pending` | the generated consumer adoption matrix, filtered to the declared consumer set | no pending (🟡) cell in a declared consumer's column |
| `open-deprecations` | the `(obsolete)` markers in the committed baselines | at most the declared allowance (default 0) |

**A missing input is a row, never a crash.** A repo with no release tag, a sidecar not yet
generated, a matrix not supplied — each renders as `⏳ not yet` with the reason, and counts as
failing. The generator's job is to say what is missing, and it cannot say so from inside an
exception.

## What the adoption row reads, and where the consumer set lives

The matrix and the consumer names are facts about private consumers, so **both arrive from outside
this repository** and neither is committed or rendered here:

```powershell
$env:TOOLUP_ADOPTION_MATRIX = '<path to the generated adoption matrix>'
$env:TOOLUP_ADOPTION_CONSUMERS = '<consumer column header>[;<another>]'
dotnet run --project Build.fsproj -- V1Readiness
```

The consumer set is declared rather than hard-coded, per the operator's 2026-09-12 decision that
this gate measures **the commercial consumer only** — every other consumer adopts deliberately and
lags by design, and is out of scope for 1.0. Widening it is a change to that declaration. The
rendered row reports *counts* — pending cells, consumers declared — and the target prints the
per-consumer split to the console, so the published document never names a consumer. A consumer
the matrix does not score is a `not yet` row naming its position in the declaration, never a zero.

## What the deprecations row measures, and why it is not what the shard asked for

The shard (2026-06-27) asked for the count of open `[<Obsolete>]` markings *without a removal
target*. Phase 258 has since gated exactly that on every verified tree — a public deprecation
whose notice omits its replacement or its removal target fails the Platform pack — so that count is
zero by construction, and a scorecard row over it could never fail. A row that cannot fail is not a
measurement.

What 1.0 actually decides is the **open set**. Every 0.x-era notice names "a future major" as its
removal target; 1.0 is that major; so each open marker is a remove-or-carry decision the cut takes.
The row counts them (seven on the tree this was written against) against a declared allowance that
defaults to zero. Carrying a deprecation into 1.x is legal under the
[deprecation policy](../platform/deprecation-policy.md) — a member may stay obsolete indefinitely —
and the way to record that decision is to raise `openDeprecations` in `v1-readiness.json`, in a
commit that says why.

## What it said on the tree it was written against

Five of six rows red: the surface has moved since `v0.22.0` (0 stable releases against 2), 56 of
153 replaceable seams are packed (36.6%), 21,635 of 52,034 public subjects are documented (41.6%),
105 cells are pending for the one declared consumer, and 7 deprecations are open. The rename table
is fully decided. None of that is news — each number was already computable from its own artefact —
but it had never been one table, and the point of the table is that the next reading is a diff
against this one.

## If a row reads `not yet`

The note says which input was missing. The two that are missing on a fresh checkout by design are
the adoption inputs above; the others (a release tag, the doc-coverage sidecar, the Phase 256
migration doc, the `api-baselines/` directory) are committed artefacts, and their absence means the
tree is not the one the scorecard was written for.

## Consumer adoption

⛔ **N/A.** This is forge's own release hygiene — a FAKE target, a repo-root build module, a
thresholds file and a test pack. Nothing a consumer composes against changed; the consumer
adoption manifests carry this phase as not-applicable for every consumer.
