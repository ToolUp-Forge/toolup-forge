# 2026-08-18 — Federation wire rename + the signed-shape separator registry (breaking)

**What changes.** Nine normative wire values moved across four commits on 2026-08-18. Eight were renamed to strip an implementation brand from the federation-seam specification ahead of its public cut; the ninth harmonised the one remaining version suffix that disagreed with the rest. **Every one of them is a breaking wire change: a signature minted under an old value does not verify under the new one.** Verification fails closed, as it should, but it fails.

There is no compatibility window and none is offered. Peers on both sides of a seam move together.

## The values

Domain separators — each opens the canonical bytes something is signed over, so a change to one invalidates every signature already minted over that shape.

| Shape | Before | After | Commit |
|---|---|---|---|
| Clean-room approval record | `toolup.cleanroom.approval/1` | `fuaran.federation.cleanroom.approval/1` | `44e6a354` |
| Promoted artifact | `toolup.promoted-artifact/1` | `fuaran.federation.promoted-artifact/1` | `44e6a354` |
| Clean-room template | `toolup.cleanroom.template/1` | `fuaran.federation.cleanroom.template/1` | `02d0b769` |
| Activation authorisation | `toolup.activation.authorisation/1` | `fuaran.federation.activation.authorisation/1` | `02d0b769` |
| Signal-feed delivery | `toolup.signalfeed.delivery/1` | `fuaran.federation.signalfeed.delivery/1` | `d6026046` |
| **Worker signed outcome** | **`toolup.signed-outcome.v1`** | **`toolup.signed-outcome/1`** | **Phase 654** |

Contract ids and the reserved spec-payload media type, renamed alongside them in `44e6a354`:

| Kind | Before | After |
|---|---|---|
| Contract id | `toolup.model-execution` | `fuaran.model-execution` |
| Contract id | `toolup.model-execution.diagnostics` | `fuaran.model-execution.diagnostics` |
| Media type | `application/vnd.toolup.model-spec` | `application/vnd.fuaran.model-spec` |

**The worker-outcome tag keeps its `toolup` branding, deliberately.** It names a ToolUp-specific protocol whose header is literally `X-ToolUp-Worker-Signature`, it ships in a ToolUp repo, and the branding is accurate rather than a leak. Only the version suffix moved, so that all six separators now share one scheme. Keeping the brand and harmonising the suffix are independent decisions.

## What a consumer must do

1. **Re-mint, do not migrate.** No signed artefact carries a version field that can be re-pointed, and no signature over an old separator will verify after these changes. Anything still needed must be produced again by a deployment running the new code:
   - clean-room **template approvals** — both parties re-approve, since an approval binds to the template's content hash and that hash moved;
   - **activation authorisations** — re-issue;
   - **promoted-artifact acceptance signatures** — re-sign on transfer;
   - **worker outcome signatures** — nothing persistent to re-mint; workers sign per callback, so an unpatched worker simply starts being rejected (see 3).
2. **Upgrade both sides of a seam together.** Signal-feed idempotency keys are recomputed by the *receiving* partner, so a version skew does not merely fail verification: two peers compute different keys for the same emission, cross-boundary deduplication stops matching, and a retry reads as a fresh delivery. That is a real loss of the at-most-once property, not a cosmetic mismatch.
3. **Update out-of-tree workers that sign outcomes.** Any worker building the `X-ToolUp-Worker-Signature` payload from a hard-coded `toolup.signed-outcome.v1` first line must be changed to `toolup.signed-outcome/1`. The payload is otherwise unchanged: newline-separated domain tag, lowercase-`D` handle id, artifact hash, diagnostics hash, `t` byte-for-byte as sent. A worker that is not updated has its callbacks refused, which is the correct fail-closed behaviour but is silent from the worker's side beyond the rejection.
4. **Update any re-implementation of these encodings** in another language. The published wire text lives in `docs/interplatform/FEDERATION_WIRE.md`; the worker payload is in `docs/platform/external-compute.md`.

## Why the fourth commit exists at all

The first three passes each found separators the previous had missed, because nothing in the SDK could enumerate the set — they were hand-written literals in separate modules with no mechanical relationship to anything, and each pass was a fresh invalidation of the same artefacts. Phase 654 replaced that with a closed `SignedShape` union in `ToolUp.Platform.Core` and an exhaustively-matched `SignedShape.separator`, so the set is now a match expression rather than a grep and a new signed shape cannot compile without one.

**Building it found a sixth separator all three passes had missed** — the promoted-artifact tag, which was embedded inside a `sprintf` format string rather than bound to a name, so every sweep looking for a standalone separator literal walked past it. Its value was already correct from `44e6a354`, so nothing further broke; what changed is that it is now enumerable. That discovery is the clearest argument for the registry: a grep finds what it is shaped to find.

## Verification

- `SignedShapeSeparatorTests` (`src/ToolUp.Platform.Tests/InProcess/`) derives its cases by reflecting over the `SignedShape` union, so a new shape inherits format validation, the pairwise-collision check and a demand for a pinned digest without anyone remembering to add them.
- Each shape carries a digest pinned **through its own canonical encoder** over a fixed input, so the pin proves the registry reaches the bytes rather than merely agreeing with itself.
- Phase 654 was shown to be value-preserving everywhere it claimed to be: five of the six shapes digest identically before and after the refactor, and only the worker-outcome tag moved.
