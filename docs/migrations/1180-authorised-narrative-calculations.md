# Phase 1180 — authorised narrative calculations

**What changes.** A grounded narrative could state only Facts: "revenue rose 2.7% on last week" was
refused, because a figure computed from two Facts is not a reference to anything. A deployment may
now declare the calculations a report may state. The model requests one through a `calculate` tool,
the server computes it by the declared formula, and the grounding gate derives it again from the
cited Facts before it admits it. The model never computes a figure. A number it writes that no Fact
and no authorised calculation produced is still refused.

**Nothing changes for a deployment that declares none.** There is no tool, no kind and no new
instruction line. A `calc:` reference is refused as an unregistered kind, as before. Every change to
the public surface is additive.

The full description is in [`docs/platform/grounded-narratives.md`](../platform/grounded-narratives.md)
under "Authorised calculations".

## Declaring calculations

1. Build the declarations once and validate them. `NarrativeCalculations.declare` returns every
   problem by name: a malformed name, a version below 1, no metric, places outside 0 to 10, a currency
   that is not ISO 4217, a format the operator cannot be shown in, and a duplicate name.

   ```fsharp skip=fragment
   let calculations =
       NarrativeCalculations.declare [
           {
               Name = "revenue-change"
               Version = 1
               Operator = CalculationOperator.PercentChange // from, to
               Metrics = [ "revenue" ]
               Rounding = { Places = 1; Mode = CalculationRoundingMode.HalfEven }
               Format = CalculationFormat.Percent
           }
           {
               Name = "revenue-gap"
               Version = 1
               Operator = CalculationOperator.Difference // from, to
               Metrics = [ "revenue" ]
               Rounding = { Places = 0; Mode = CalculationRoundingMode.HalfAwayFromZero }
               Format = CalculationFormat.Currency "GBP"
           }
       ]
       |> Result.defaultWith (fun refusals -> failwith (String.concat "; " refusals))
   ```

   The operators are `percentChange`, `difference`, `ratio`, `share`, `sum` and `mean`. Read an
   operator from configuration with `CalculationOperator.tryParse`, which refuses any other name.

2. Register them with the fact tier, so the gate re-derives `calc:` references. With no fact store
   composed this changes nothing.

   ```fsharp skip=fragment
   app |> FactsCompose.withFactStore |> FactsCompose.withNarrativeCalculations calculations
   ```

3. Give the grounded run the `calculate` tool from the SAME value:

   ```fsharp skip=fragment
   GroundedNarrativeRunOptions.create (NarrativeGrounding.factTools @ NarrativeCalculations.tools calculations)
   ```

   A run given the tool without step 2 fails closed: every `calc:` reference is refused as an
   unregistered kind.

4. **Bump `Version` whenever a declaration's operator, metrics, rounding or format changes.** A
   reference issued under the old version is then refused as superseded, naming the reference the
   calculation has under the new one. A declaration changed without a bump no longer matches the
   content addresses issued under it, so its references are refused.

## The prompt guidance

A run whose tools include `calculate` adds one line to its instruction
(`GroundedNarrativeRun.calculationRule`): to state a change, difference, ratio, share, sum or mean,
call `calculate` with a declared calculation and the input Fact ids, cite the reference it returns as
`[[label|value|calc:...]]` with the value exactly as rendered, and never compute a figure. The tool's
description lists the declared calculations and the order of each one's inputs. A narrative's own
wording (its prompt or a section's guidance) may name the calculations a section should use, for
example "state the change in revenue on last week with revenue-change". It should never ask the model
to work a figure out.

## Reading a certificate

A published narrative's `grounding-certificate/v2` body now also seals each calculation it cites: an
admission of kind `calc` at its location, with the attributes `address`, `declaration`,
`declarationVersion`, `format`, `inputs`, `operation`, `result` and `rounding`. The inputs also join
the Facts the chain cites. A v1 certificate, and a v2 certificate with no calculation, are
byte-for-byte unchanged.

## Verification

- `NarrativeCalculationTests.fs` in the Platform test pack.
- In a deployment: request a declared calculation in a run and check that the published narrative's
  certificate carries the `calc` admission.

## Rollback

Remove the `withNarrativeCalculations` call and the tools from the run options. Narratives already
published keep their certificates, which verify unchanged. A narrative regenerated afterwards may
cite no `calc:` reference.
