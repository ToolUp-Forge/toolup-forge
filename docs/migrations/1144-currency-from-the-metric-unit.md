# Money renders in the metric's declared currency: `FactRendering.renderFor`

**Ships in:** ToolUp.Facts.Core and ToolUp.Facts.Server 0.38.0.

**Affected:** a deployment that registers a metric with `Dimensionality = "currency"`, an ISO 4217 `Unit`
(`"GBP"`, `"EUR"`, …) and a `C` display format (`"C0"`, `"C2"`). Nothing breaks at compile time: the change
is additive in surface and visible in output.

## What changes

1. **The visible change.** Every fact-rendering door (the retrieval resolver, `query_facts`,
   `query_metric_population`, `aggregate_metric`, the fact browse handler and the narrative grounding gate) now
   renders such a metric with its currency's symbol. A GBP revenue fact declared `C0` that rendered `¤21,240`
   renders `£21,240`. This is the fix, not a regression: `¤` is invariant culture's generic currency symbol,
   and it reached users verbatim because the model quotes a fact's rendering as it is.
2. **What does not change.** Grouping, the decimal separator, the digits and the negative form `(£21,240)` stay
   invariant. A non-currency metric, a currency metric under an `N`, `F` or custom format, a `Unit` that is not
   an ISO code, and a metric the registry does not hold render byte for byte as before.
   `FactRendering.render <format> <value>` is unchanged.
3. **The new surface (Facts.Core).**
   - `FactRendering.renderFor (metric: MetricDefinition option) (value: FactValue)`: the rendering as the metric
     declares it. `None` renders verbatim, as `render ""` does.
   - `CurrencySymbols.table`, `CurrencySymbols.tryFind` and `CurrencySymbols.prefix`: the ISO-code-to-symbol
     table, read with no ICU culture data, so a container running with
     `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` renders the same as any other host. A code the table does not hold
     renders with the code as its prefix (`CHF 1,299`), never `¤`.

## What to do

**Nothing, to take the fix.** Upgrade and the symbol appears.

**If you worked around `¤` with a custom format, return to `C0` / `C2`.** A custom format such as `"£#,##0"`
renders the right symbol but hard-codes it into the format string, where nothing else can read it, and every
deployment with money has to rediscover it. Declaring the currency in `Unit` and a `C` format says the same thing
once:

```diff
 {
     Id = "revenue"
     Name = "Revenue"
     Unit = "GBP"
     Dimensionality = "currency"
-    DisplayFormat = "£#,##0"
+    DisplayFormat = "C0"
     …
 }
```

The numeric answer gate verifies a quoted `£21,240` the same way under either declaration: it reads a fact's
precision off its rendering, and both render `£21,240`.

**If you render a fact yourself**, call `FactRendering.renderFor` with the metric your registry holds, rather
than `FactRendering.render metric.DisplayFormat`, so your door renders what the SDK's doors render.

## Verification

- A GBP metric declared `C0` renders `£21,240` in `query_facts`, `query_metric_population` and the browse view.
- A metric declared under a non-currency format renders exactly as before.
- `AnswerVerifier.NumericFidelity.verify "Revenue was £21,240." …` verifies against the GBP fact.

## Rollback

Pin the previous release. A deployment that needs the old `¤` rendering for one metric can declare its format
without the unit (`Unit = ""`), which `renderFor` renders as `render` does.
