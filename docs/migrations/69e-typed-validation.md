# Phase 69e — typed validation on API input records

> **Substrate status: partial.** The attribute set + dispatcher engine below are shipped and usable today (`Server/Remoting/Validation.fs`). The fuller surface the phase plans — nested-record traversal, multi-argument validation, a custom-validator escape hatch (`IFieldValidator`), localisable message catalogue, and Forms-schema integration — has not landed yet. This recipe describes the shipped v0 and will be updated when the full substrate ships.

## What changes

Validation rules are declared as attributes on the **fields of an API method's input record**. The dispatcher classifies each API record once at startup; at request time it deserialises the first argument and evaluates the field attributes **before invoking the handler**. Any violation short-circuits with a categorised `Validation` error envelope carrying per-field details — the handler never runs.

There is nothing to compose: validation is **on whenever attributes exist**. Methods whose inputs carry no validation attributes pay zero per-call cost (the startup classifier skips them; the per-request lookup misses fast).

Shipped attributes (`ToolUp.Remoting.Server`): `[<MinLength n>]`, `[<MaxLength n>]`, `[<NotEmpty>]`, `[<Regex pattern>]`, `[<Email>]`, `[<Range(min, max)>]`. Attributes compose — a field can carry several, and all violations from one bad input are collected into a single envelope.

**v0 scope limits:** only the **first** argument of a method is validated, and only its **top-level** fields (nested records and list elements are not yet traversed). Hand-rolled checks inside the handler remain the right tool for anything deeper until the follow-up lands.

## Diff to apply

Annotate the input record where the rule lives today as a handler-internal check:

```fsharp
// Before — rule hidden inside the handler:
type CreateProjectRequest = { Name: string; Quantity: int }
// handler: if input.Quantity <= 0 then failwith "quantity must be positive"

// After — rule visible at the declaration site, enforced pre-dispatch:
type CreateProjectRequest = {
    [<MinLength 3; MaxLength 80>]
    Name: string
    [<Range(1.0, 1000.0)>]
    Quantity: int
}
```

Then delete the now-redundant handler-internal check. On failure the client receives the categorised envelope with a `FieldViolation` list — `{ Path; Code; Message }` per violation (e.g. `Path = "Name"; Code = "MinLength"`), which a client renders directly against the offending field.

## Verification

1. `dotnet build` — clean.
2. Call an annotated method with an invalid value (e.g. a 2-char `Name`): the response is the `Validation`-categorised error envelope listing the violation; the handler did not execute (no side effect, no audit row).
3. Call with a valid value: behaviour is byte-for-byte the pre-annotation response.
4. A field carrying multiple attributes reports **all** its violations from one bad input in one envelope (collect-then-emit, not first-then-stop).

## Rollback

Remove the attributes — the method reverts to unvalidated dispatch with no other change. Attributes are inert metadata; no store, config flag, or compose call is involved.

## See also

- [69-family-overview.md](69-family-overview.md) — where validation sits in the dispatcher pre-flight chain.
- Substrate: `src/ToolUp.Platform.Server/Server/Remoting/Validation.fs`.
