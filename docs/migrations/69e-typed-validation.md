# Phase 69e — end-to-end typed validation on input records (consumer migration)

> **Substrate status: complete.** The v0 (top-level, server-tier-only) engine has been extended to the full planned surface: **nested-record / list-element / option-of-record traversal**, the **`[<Custom>]` `IFieldValidator`** escape hatch with `IValidationContext`, the `[<MinValue>]` / `[<MaxValue>]` / `[<Uri>]` attributes, **family-agnostic recognition** of the Fable-safe `ToolUp.Platform.*` mirrors, the **Forms-schema integration** bridge, and (Phase 69e.C) a **localisable / overridable message seam** (`IValidationMessages`). Forge SDK companion API records are now annotated where validation is unambiguous (Phase 69e.H).

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

### Localising / overriding messages (Phase 69e.C)

By default each violation carries the attribute's built-in **English** message. To localise (or otherwise customise) the wording, compose an `IValidationMessages` resolver — the dispatcher hands every violation through it before building the envelope:

```fsharp
// A ViolationCode -> template map. Templates reference the violation's
// structured args ({min}/{max}/{actual}/{pattern}) and the field {path}.
let messages =
    ValidationMessages.fromTemplates (
        Map [
            "MinLength", "{path} doit comporter au moins {min} caractères (reçu {actual})"
            "Email",     "{path} n'est pas une adresse e-mail valide"
            "Range",     "{path} hors plage [{min}, {max}]"
        ]
    )

// Compose through the Api.make `customOptions` escape hatch:
Api.make (myApi, customOptions = Remoting.withValidationMessages messages)
```

A code absent from the map (or a resolver returning `None`) falls through to the built-in English message, so partial catalogues are fine. `ValidationMessages.englishTemplates` is the documented baseline a localiser copies + translates. The seam is **zero-cost when unused** (GP 13) — composing nothing keeps the built-in messages on the wire and pays nothing per request. The wire shape is unchanged either way: `FieldViolation` stays `{ Path; Code; Message }`; only the `Message` text differs.

### Forms integration (Phase 21)

When an input record is shared between the transport and a Form, `ToolUp.Forms.Server.ValidationAttributeBridge.fieldsFromRecord recordType` produces a `FieldSchema list` from the same attributes — `[<MinLength 3>]` → `LengthRange(Some 3, None)`, `[<Range(18, 120)>]` → a `NumberField` bound + `NumberRange` validator, `[<Email>]` → a tagged `Regex`, `[<NotEmpty>]` → `Required = true`. Client-side form rendering then matches server-side transport enforcement from one attribute set.

## Verification

1. `dotnet build` — clean.
2. Invoke a method with a too-short `[<MinLength 3>]` field: the response is the `Validation`-categorised envelope with one `violations` entry; the handler did not run (no side effect, no audit row).
3. A nested bad field reports a dotted path (`Address.Postcode`); a bad list element reports an indexed path (`Lines[2].Sku`); both surface alongside top-level violations in one envelope.
4. Contract packs: `InProcess/ValidationTests.fs` (`ToolUp.Platform.Tests`) — per-attribute eval, nested/list/option traversal, both attribute families, the `[<Custom>]` + `IValidationContext` path, the new Range/MinValue/MaxValue/Uri attributes; `InProcess/ValidationBridgeTests.fs` (`ToolUp.Forms.Tests`) — the Forms `FieldSchema` bridge.

## Adoption note

The substrate, the family-agnostic recognition, the Forms bridge, and the message seam ship ready.

**Forge SDK records (Phase 69e.H).** The forge SDK companion API records have now been annotated where validation is **unambiguous** — a blank required identifier / routing key / token, or a non-positive count, can only fail downstream, so the dispatcher rejects it up-front with a structured `ErrorCategory.Validation` envelope instead of an opaque later failure. Records covered: `ModuleQueryRequest` (`TargetModule` / `QueryKey`), `SetPlatformAIKeyRequest` / `SetTeamAIKeyRequest` (`ProviderId` / `TeamId` / `ApiKey`), `SlotSearchRequest` (`ResourceId` + `SlotDurationMinutes ≥ 1`), `DispatchInvitationsRequest` (`SchemaId` / `Subject` / `BodyTemplate`), `SubmitWithTokenRequest` (`Token`). The genuine annotatable surface is small by design — most forge API methods take primitives / typed-id aliases / tuples (the dispatcher validates the first **record** argument only) or already funnel input errors through a typed domain `Result`/error DU, where a transport-400 would *fragment* a clean error channel rather than improve it.

**Defence-in-depth, not replacement.** Validation runs in the **dispatcher** (HTTP path), so a pre-existing in-handler guard (e.g. the AI-keys empty-key check) is **retained** as defence-in-depth for direct in-process callers — the attribute is the primary transport-boundary guard, the handler check still protects non-HTTP invocation. (The earlier "delete the handler check" guidance applies only to ad-hoc checks fully subsumed on every call path; a guard that also covers direct callers stays.)

**New records are born annotated.** A new input record carrying a required string id / count adds the attribute at the declaration site from the start — that is the cheapest point to make "what shape is acceptable here" visible and machine-enforced.

## Rollback

Remove the validation attributes (and restore handler-internal checks if you deleted them). The `ErrorCategory.Validation` envelope is the existing 69b category — older clients reading only `Category` are unaffected; richer clients render the per-field `violations`.

## See also

- [69-family-overview.md](69-family-overview.md) — where validation sits in the dispatcher pre-flight chain.
- Substrate: `src/ToolUp.Platform.Server/Server/Remoting/Validation.fs`; Forms bridge: `src/ToolUp.Forms.Server/Server/ValidationAttributeBridge.fs`.
