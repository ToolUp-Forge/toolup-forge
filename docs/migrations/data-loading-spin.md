# `Icons.dataLoading` now spins instead of cycling colour

**Class:** visual-only, non-breaking. **Consumer action: none.**

## What changed

`ToolUp.Platform.Icons.dataLoading` — the ToolUp brand mark (chevron + dot)
used by `BrandMarkLoader` and `StateViews.inlineLoading` — previously animated
only its gradient colour (pink → magenta → violet → blue via SMIL `<animate>`
on the gradient stops). That colour cycle alone was too subtle to read as
"working" at small sizes.

The mark now **physically rotates** as well: the same chevron-and-dot, spun via
SMIL `<animateTransform type="rotate">` (1s, linear, continuous) with the
gradient colour cycle kept underneath. Rotation is the dominant "working"
signal; the colour cycle adds brand character. It remains self-coloured
(ignores the surrounding `currentColor` cascade) and still animates via SMIL —
not a CSS `<style>` block — so it survives the `vite-plugin-svgr` → SVGO
pipeline intact.

`spinner` (the neutral, `currentColor`-tinted arc for deployments that prefer
not to show the brand mark) is unchanged.

## Consumer action

None. The change is internal to the shipped SVG asset; every call site
(`BrandMarkLoader`, `inlineLoading`) picks it up on the version bump with no
code change and no behavioural change beyond the rendered animation.

## Verification

Any boot screen (`LoadingIndicator = BrandMarkLoader`) or page with a
"recomputing" inline state shows the brand mark spinning rather than slowly
shifting colour.

## Rollback

Restore the previous `src/ToolUp.Platform.Client/Client/icons/data-loading.svg`
(the gradient-colour-cycle variant). No data, schema, or API surface is
involved.
