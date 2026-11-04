# Comparisons with a day offset — this delivery against last week, or the same weekday last year

**Ships in:** ToolUp.Facts.Presentation and ToolUp.Facts.Presentation.Server 0.39.0. Rides the 0.39.0 draft. The
class is additive: a union case, functions and one compose verb, and no existing request or schema changes.

**Affected:** nobody until they opt in. The one source-level effect: code that matches **exhaustively** over
`PresentationCompareTo` now sees a second case (FS0025, an error under warnings-as-errors).

## What changes

1. **`PresentationCompareTo.DaysBefore of days: int`.** A comparison draws its delivery against the one `days`
   before it: 7 is the same day last week, 364 the same weekday last year. The validator holds it from 1 to
   `PresentationCompareTo.MaxDaysBefore` (3,660) and refuses anything else naming `compareTo`.
2. **One canonical text.** `compareTo` stays a string: `"previousDelivery"` or `"daysBefore:<days>"`
   (`PresentationCompareTo.text` / `tryParse`; case and surrounding space are ignored on read). A stored request
   without an offset is byte-identical to before.
3. **A second read.** `PresentationRequest.toQueries` returns the period's read and then a read of the one metric
   over the comparison day alone (`PresentationRequest.comparisonDay`). The builders take the answers in that order.
   Every caller that answers `toQueries` in order, which is all of the SDK's, needs no change.
4. **The gap rule holds.** A withheld point on the comparison day is absent and counted (notes and provenance); a
   comparison day with no facts, or whose read was refused, is named and draws no chart, never a zero.
5. **The assistant is told only what the deployment offers.** `PresentationCompose.withComparisonOffsets` lists each
   offset as a `compareTo` baseline in `render_chart`, `layout_add_block` and `layout_set_request`
   (`RenderTools.definitionOffering`, `RenderTools.toolsOffering`, `LayoutTools.definitionOffering`). Without it every
   tool schema is unchanged, byte for byte.

## What to do

Offer the offsets your users compare by, after composing the tools:

```diff
  let app, ai =
      (app, AIOptions.create factory providerProfile)
      |> PresentationCompose.withRenderTools
      |> PresentationCompose.withLayoutTools
+     |> PresentationCompose.withComparisonOffsets [ 7; 364 ]
```

Request a year-on-year comparison in code:

```diff
  let request = {
      comparison with
-         CompareTo = Some PresentationCompareTo.PreviousDelivery
+         CompareTo = Some(PresentationCompareTo.DaysBefore 364)
  }
```

If you answer `toQueries` yourself, answer every query it returns, in order. A builder handed the period's answer
alone refuses with `AnswersMismatch` naming the comparison day's read.

If you match exhaustively over `PresentationCompareTo`, add the case:

```diff
  match compareTo with
  | PresentationCompareTo.PreviousDelivery -> "the delivery before"
+ | PresentationCompareTo.DaysBefore days -> sprintf "%d days before" days
```

## Verify

- `PresentationRequest.toJson` of the request carries `"compareTo":"daysBefore:364"` and decodes back to it.
- The block's chart has the comparison day and the delivery as its two series; a subject withheld on either day is
  absent and named in the notes.
- A deployment that does not call `withComparisonOffsets` lists the same provider tool definitions as before.

## Rollback

Remove the `withComparisonOffsets` call and set `CompareTo` back to `PreviousDelivery` or `None`. A stored request
carrying `daysBefore:<days>` cannot be read by an SDK older than 0.39.0; rewrite it before downgrading.
