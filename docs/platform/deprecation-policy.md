# Deprecation policy — how the SDK retires public surface (Phase 258)

A frozen API that cannot shed anything calcifies. A frozen API that sheds things without warning is
not frozen at all. This is the mechanism in between: how a `ToolUp.*` public member is marked for
retirement, how long it stays, what the notice must say, and the one point at which it may actually
disappear.

It applies to every member of every packable `ToolUp.*` assembly — the set the
[public-API approval gate](#the-gate) renders. It does **not** apply to internal or vendored code: a
`module internal` has no consumer to warn, and the gate never sees it.

## The three rules

1. **Removal happens only at a major boundary.** Within a major version the public surface may grow
   and may be *marked*; it may not shrink. Removing a public member is the one sanctioned breaking
   change and it waits for the next major. (While the SDK is on `0.x`, the SemVer-on-`0.x` policy
   lets a **minor** carry a break — but a *deprecated* member still follows this rule rather than
   that licence. The point of marking something is to give consumers a window; taking it away at the
   next minor spends the marking for nothing.)

2. **A member is marked before it is removed, never simultaneously.** `[<Obsolete>]` lands in a
   release that still ships the member and still works. A member that appears and disappears inside
   one release cycle was never deprecated; it was deleted.

3. **The notice names both a replacement and a removal target.** A deprecation warning a consumer
   cannot act on is worse than none — it says something is going away and withholds both what to
   move to and by when. This half is machine-checked; see [the gate](#the-gate).

## The deprecation window

**A member marked `[<Obsolete>]` stays for at least one full minor release before the major that may
remove it.** In practice, on a `0.x` line:

```
0.23.0   member ships, unmarked
0.24.0   member marked [<Obsolete>] — still present, still works, warns at compile time
  …      any number of further minors; the member is still there
1.0.0    the member MAY be removed
```

The floor is one minor, not one release: marking a member in `0.24.0` and cutting `1.0.0` from the
same commit gives a consumer no release in which to migrate. The ceiling is deliberately absent — a
member may stay obsolete indefinitely. Retiring surface is a cost paid by every consumer, so
"remove it because we can" is not a reason; "the replacement has been available for a year and the
old path is now a maintenance burden" is.

**After `1.0.0`** the window widens with the release cadence rather than the version number: mark in
any `1.x` minor, remove no earlier than `2.0.0`.

## The message format

```fsharp skip=fragment
[<System.Obsolete("Use Foo.bar instead. Removed in 1.0.")>]
```

Two facts, in whatever wording reads best:

| Half | What it must convey | Accepted phrasings include |
|---|---|---|
| **Replacement** | the thing to move to, named precisely enough to grep | `use …`, `prefer …`, `replaced by …`, `moved to …`, `see …`, `compose …`, `call …` |
| **Removal target** | the release by which it goes | `removed in 1.0`, `will be removed in a future major`, `retired in a future minor` |

The recogniser is permissive about wording and strict about the two facts. That is a deliberate
choice: a rigid template would have rejected every deprecation notice the SDK already shipped, each
of which says the right two things in its own words, and a gate whose first act is to rewrite
compliant prose teaches authors to satisfy the parser rather than the reader. If a genuinely new
phrasing appears, extend the phrase list in
[`PublicApiApproval.fs`](../../src/ToolUp.Platform.Tests/Contracts/PublicApiApproval.fs) rather than
bending the notice to fit.

**`"removed in a future major"` is accepted while the SDK is on `0.x`**, because `1.0.0` is the
declared boundary and naming it is not yet a commitment anyone can make precisely. Once `1.0.0` is
cut, prefer a concrete target (`Removed in 2.0.`); the vaguer form stays legal so a `0.x`-era notice
does not have to be reworded, but new notices should name the version.

Say the *why* too where it is not obvious — a link to a migration doc costs one clause and saves the
consumer a search:

```fsharp skip=fragment
[<System.Obsolete("Vendor-named case — use ProviderAuthUI (\"clerk\", box config) instead. See docs/migrations/494-vendor-neutral-auth-ui.md. ClerkAuthUI will be removed in a future major version.")>]
```

## What marking does to the baseline

Nothing breaking — and this is the part worth understanding before you mark anything.

The [public-API approval gate](../../src/ToolUp.Platform.Tests/Contracts/PublicApiApproval.fs)
renders each assembly's surface as a set of one-line tokens and diffs it against
`api-baselines/<assembly>.approved.txt`. Marking a member emits a **separate marker line** beside
its token; the member's own token is left exactly as it was:

```
ToolUp.Platform.AgGrid.ThemeClass (class)
ToolUp.Platform.AgGrid.ThemeClass (class)  (obsolete)
```

So a deprecation scores as an **addition** — non-breaking, but unfolded, so the gate fails until the
baseline is regenerated and committed in the same PR. That failure is the review checkpoint: a
deprecation is a public act, and it lands in a diff a reviewer sees.

The alternative — rewriting the member's token in place (`… [obsolete]`) — is **forbidden**, because
the original token would vanish and the comparer cannot tell that from a real removal: it would
report a BREAKING change for what this policy calls a minor. Both shapes are pinned by fixtures so
the rule cannot be undone by accident.

The marker deliberately carries **no message**. Tokens are compared as a set, so folding the message
in would make every reworded notice read as a removal-plus-addition — a breaking diff for a copy
edit. The message is graded separately, by content.

### Known limit: a deprecated *union case* is not marked

`[<Obsolete>]` on an F# discriminated-union **case** produces no marker, and its message is not
graded. The renderer emits a DU case as its nested class plus that class's `Item` properties; the
compiler puts the attribute on the case's factory method (`AuthUIMode.NewClerkAuthUI`), which the
renderer does not emit at all — that omission predates this policy and applies to every DU in the
SDK. Making the factory renderable would widen ~every DU's baseline at once, which is a large
unrelated regen, so it was left alone.

`AuthUIMode.ClerkAuthUI` is the one live instance, and its notice already names both halves. Mark a
retiring case as usual — the compiler warning, which is what a consumer actually reads, works
exactly the same — but know that the gate is not watching this one, and write the notice as if it
were.

## The gate

Both halves run in the `ToolUp.Platform.Tests` pack, so they fire on `pwsh ./verify.ps1` locally and
in the `verify-all` CI job (which runs `VerifyAll`, which runs the pack) — the same check, shifted
left to the build rather than bolted on as a separate CI step.

| Check | Fails when |
|---|---|
| **Surface drift** (Phase 175 / 618) | a member is removed (BREAKING), or a marker/member is added and the baseline was not regenerated (non-breaking, unfolded) |
| **Message policy** (Phase 258) | a public `[<Obsolete>]` carries no message, or a message missing either the replacement or the removal target |

A message-policy failure names the member, quotes the offending message, says which half is missing,
and states plainly that nothing is broken and no baseline has drifted — because the reader of a red
run needs to know they are fixing a sentence, not a surface.

Regenerating after a marking is the ordinary scoped regen — name the assemblies, never `=1`:

```powershell
$env:TOOLUP_APPROVE_API = "ToolUp.Platform.Client"
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
$env:TOOLUP_APPROVE_API = $null
```

## Deprecating something — the checklist

1. Ship the replacement first, in a release of its own if it is substantial.
2. Add `[<Obsolete("…")>]` with both halves. Add a `docs/migrations/` note if the move is more than
   a rename, and cite it in the message.
3. Leave the old member **working**. A deprecation is a warning, not a soft removal: it must not
   start throwing, logging, or degrading.
4. Build, run the Platform pack, regenerate the affected baselines (scoped), commit them with the
   change.
5. At the next major, and no earlier: delete the member, regenerate the baseline, and record the
   removal in the release notes. That regen is the one time the gate's BREAKING arm is meant to
   fire, and the baseline edit is what makes the break reviewable.

## Rationale (GP 11)

[GP 11](../../CLAUDE.md#guiding-principles) says an existing deployment that upgrades stays
byte-for-byte identical until it opts in. A deprecation is the only sanctioned way to eventually
*stop* honouring that for a given member, so it is fenced on all sides: the member keeps working for
the whole window, the consumer is told what to do and by when, the marking itself is a reviewed diff,
and the removal waits for the boundary at which consumers already expect to read a migration guide.

This is what keeps a 1.0 freeze a *living* freeze — evolvable across majors — rather than a dead
one that can only ever accumulate.

## See also

- [`src/ToolUp.Platform.Tests/Contracts/PublicApiApproval.fs`](../../src/ToolUp.Platform.Tests/Contracts/PublicApiApproval.fs) — the renderer, the comparer, and the message policy
- [`docs/migrations/258-deprecation-lifecycle.md`](../migrations/258-deprecation-lifecycle.md) — what Phase 258 changed and what a consumer has to do about it
- [`CLAUDE.md` § Public-API approval baselines](../../CLAUDE.md#public-api-approval-baselines-phase-175) — the operational gotchas of the gate
