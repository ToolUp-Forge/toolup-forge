# Phase 69e — end-to-end typed validation on input records (consumer migration)

> **Substrate status: complete.** The v0 (top-level, server-tier-only) engine has been extended to the full planned surface: **nested-record / list-element / option-of-record traversal**, the **`[<Custom>]` `IFieldValidator`** escape hatch with `IValidationContext`, the `[<MinValue>]` / `[<MaxValue>]` / `[<Uri>]` attributes, **family-agnostic recognition** of the Fable-safe `ToolUp.Platform.*` mirrors, and the **Forms-schema integration** bridge. (A localisable message catalogue remains a future addition — violation messages are the built-in English ones today.)

## What changes

Input-record fields opt into **dispatcher-enforced** validation via attributes — `[<MinLength 3>]`, `[<MaxLength 50>]`, `[<NotEmpty>]`, `[<Regex "...">]`, `[<Email>]`, `[<Uri>]`, `[<Range(1, 100)>]`, `[<MinValue 0>]`, `[<MaxValue 100>]`, or `[<Custom(typeof<MyValidator>)>]`. Before invoking the handler the dispatcher walks the input record — **recursively into nested records, list / array / seq elements, and option-wrapped records** — collects every violation in one pass (collect-then-emit, no short-circuit), and on any failure short-circuits with an `ErrorCategory.Validation` envelope carrying a `violations` array of `{ Path; Code; Message }` (`Path` is dotted/indexed: `Address.Postcode`, `Lines[2].Sku`). The handler never runs. Removes the "forgot to validate" defect class.

There is nothing to compose: validation is **on whenever attributes exist**. A method whose input record carries no validation attributes anywhere in its tree is absent from the startup classification, so the per-request lookup is a fast miss (GP 13 — zero cost when unused).

**Family-agnostic.** forge API records sit in `ToolUp.Platform.Core` (Fable-compiled) and carry the tier-shared `ToolUp.Platform.*` attribute mirrors; server-tier records may use the `ToolUp.Remoting.Server.*` family directly. The engine recognises **both** by simple type name and normalises them to the same enforcement — so a Fable-compiled Core API record's validation fires exactly like a server-tier record's. (Before this phase the Core mirrors existed but were dead: validation on a Fable record silently never ran.)

## Diff to apply

```fsharp
open ToolUp.Platform // tier-shared mirrors, Fable-safe on Core API records

type LineItem = {
    [<NotEmpty>]
    Sku: string
    [<MinValue 1.0>]
    Qty: int
}

type OrderInput = {
    [<MinLength 3>]
    Name: string
    [<Email>]
    Contact: string
    Lines: LineItem list          // validated per element → Lines[i].Sku
    Billing: Address option       // validated when Some → Billing.Postcode
}

type OrderApi = {
    PlaceOrder: OrderInput -> Async<Result<OrderId, string>>
}
```

Then **delete the hand-rolled `if input.Name.Length < 3 then failwith "..."` checks** from the handler — the attribute is the enforcement now. Annotate per record, removing the matching handler-internal check, so you never double-validate or drift between the two. On failure the client receives the categorised envelope with a `FieldViolation` list — `{ Path; Code; Message }` per violation.

### Custom validators

`[<Custom(typeof<MyValidator>)>]` where `MyValidator` implements `IFieldValidator` (a parameterless, stateless server-tier class). It receives the per-request `IValidationContext` (subject id + correlation id):

```fsharp
type UniqueWithinTenant() =
    interface IFieldValidator with
        member _.Validate(value, ctx) =
            // ctx.SubjectId / ctx.CorrelationId available
            None // Some "message" to reject
```

### Forms integration (Phase 21)

When an input record is shared between the transport and a Form, `ToolUp.Forms.Server.ValidationAttributeBridge.fieldsFromRecord recordType` produces a `FieldSchema list` from the same attributes — `[<MinLength 3>]` → `LengthRange(Some 3, None)`, `[<Range(18, 120)>]` → a `NumberField` bound + `NumberRange` validator, `[<Email>]` → a tagged `Regex`, `[<NotEmpty>]` → `Required = true`. Client-side form rendering then matches server-side transport enforcement from one attribute set.

## Verification

1. `dotnet build` — clean.
2. Invoke a method with a too-short `[<MinLength 3>]` field: the response is the `Validation`-categorised envelope with one `violations` entry; the handler did not run (no side effect, no audit row).
3. A nested bad field reports a dotted path (`Address.Postcode`); a bad list element reports an indexed path (`Lines[2].Sku`); both surface alongside top-level violations in one envelope.
4. Contract packs: `InProcess/ValidationTests.fs` (`ToolUp.Platform.Tests`) — per-attribute eval, nested/list/option traversal, both attribute families, the `[<Custom>]` + `IValidationContext` path, the new Range/MinValue/MaxValue/Uri attributes; `InProcess/ValidationBridgeTests.fs` (`ToolUp.Forms.Tests`) — the Forms `FieldSchema` bridge.

## Adoption note

The substrate, the family-agnostic recognition, and the Forms bridge ship ready. Annotating the **existing** forge SDK input records is left **adoption-incremental on purpose**: adding a validator newly *rejects* inputs a handler previously accepted, so each record is annotated together with removing its matching handler-internal check (per the diff above) rather than swept blind — a behaviour change, not a mechanical rename. New input records should be born annotated.

## Rollback

Remove the validation attributes (and restore handler-internal checks if you deleted them). The `ErrorCategory.Validation` envelope is the existing 69b category — older clients reading only `Category` are unaffected; richer clients render the per-field `violations`.

## See also

- [69-family-overview.md](69-family-overview.md) — where validation sits in the dispatcher pre-flight chain.
- Substrate: `src/ToolUp.Platform.Server/Server/Remoting/Validation.fs`; Forms bridge: `src/ToolUp.Forms.Server/Server/ValidationAttributeBridge.fs`.
