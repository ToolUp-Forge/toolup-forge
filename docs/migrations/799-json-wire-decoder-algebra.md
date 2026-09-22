# Phase 799 — The JSON wire joins the decoder algebra

**Applies to:** every deployment (the server's argument decode seam changed); any composition root
that registers decoders; any reader of `RemotingDecoderFacet`.
**Breaking:** no wire change — no byte on either remoting wire moved, and no type nobody registered
decodes differently. Additive surface on `ToolUp.Platform.Core` (`ToolUp.Remoting.Json.JsonValue`,
`JsonDecode`, `JsonDecoders`, `PlatformJsonDecoders`) and `ToolUp.Platform.Server` (`JsonRead`,
`RemotingDecoderFacet.inspectServedArguments` / `describeArguments`).
**Action required:** none to keep working. Four platform API records now take their arguments
through the algebra — see "What changes for those four".

## What changes

Phase 785 decided (doc page §8) that the closed decoder algebra should extend to the JSON wire, and
not over `ToolUp.AI.Wire.JsonValue`, whose `JNumber of float` loses `int64` past 2^53, exact
decimals and the token's source form. This phase ships that:

1. **`ToolUp.Remoting.Json.JsonValue`** — six closed cases, no null, insertion-ordered members, a
   `size` measure, and `Number of lexical: string`: the digits as written. `tryInt64` /
   `tryUInt64` / `tryDecimal` / `tryFloat` read the width a decoder needs from the text, exactly.
2. **`JsonRead`** (Server) — the one `JsonElement`-to-value pass, bounded in depth (64) and width
   (a million per container), numbers taken with `GetRawText()`; `tryParse` for text.
3. **`JsonDecode`** — the 785 combinator surface over the model: `field` by NAME (absent is a
   refusal), `optionalField` for `option` fields, integer arms that require an integral token and a
   range fit, `asDecimal` exact, `asInt64` / `asUInt64` in the writer's signed-string form too,
   `asTimeSpan` exact (decimal milliseconds × 10 000, nearest tick — the tick STJ truncates away),
   `union` on the case name in the writer's shape, `asMap` with a key parser, tuples, `option`,
   `result`, `DateOnly` / `TimeOnly` (.NET only).
4. **The argument seam consults `JsonDecoders` first.** `FableConverters.tryDeserialise<'T>` and
   `tryDeserialiseElement` — the Phase 783 seam every argument passes through — look the target
   type up in the JSON registry (same key and shape as `RemotingDecoders`) and decode through the
   algebra when it is there; a miss is STJ's typed deserialise, byte for byte as before.
5. **`PlatformJsonDecoders`** — hand-written decoders for the platform's own argument types,
   registered by `ServerApp.run` beside the MessagePack set: `TeamRole`, `PresenceLocation`,
   `EntityLockRef`, `AuditTrailQuery`, `WireProvenanceRef`, `WireProvenanceDirection`,
   `WireProvenanceChainRequest`, `TeamInviteIssueRequest`, `PinRequest`, `CreateTeamRequest`.
6. **The served facet gains its argument side.** `RemotingDecoderFacet.inspectServedArguments`
   classifies every served record by argument types against `JsonDecoders`; `ServerApp.run` logs
   `remoting argument decoders: N of M served API record(s) …`. Advisory under every profile.
7. The trust-boundary statement's "the STJ path until 785.F" becomes "the STJ path for any
   argument type not registered"; the report's narrowing text says the same.

## What changes for those four records

`IPresenceApi`, `IAuditViewApi`, `IProvenanceQueryApi` and `ITeamInviteApi` now decode every
argument through the algebra. For a well-formed request nothing is observable. For a malformed one
the refusal is the algebra's, and it is stricter than STJ's in exactly these ways:

| Payload | STJ (before) | Algebra (now) |
|---|---|---|
| `null` for a record argument | a null reference reaches the handler | refused: `expected object, got null` |
| a declared non-`option` field absent | `null` in that field reaches the handler | refused, naming the member |
| `"7"` where an `int` is declared | accepted (`AllowReadingFromString`) | refused: a string is not a number |
| `[]` where a `Map` is declared | an empty map | refused: `expected object, got array` |
| a surplus member | ignored | ignored (unchanged — the additive read path) |

Every refusal is a `validation`-category 400 with a path, before the handler runs — the shape Phase
783 established. A client that sent any of the first four was already sending something the
handler would have had to guard against; it now hears about it by name.

**The `{high, low, unsigned}` form of `int64`** that raw `JSON.stringify` of a Fable `Long` produces
is not admitted by `asInt64` (the SDK's client never emits it — `Fable.SimpleJson` writes a number or
the signed string). None of the four records takes an `int64` argument, so no traffic changes; a
consumer registering a decoder for a record that does should know.

## Registering your own

```fsharp skip=fragment
open ToolUp.Remoting.Json

let createOrder: JsonDecoder<CreateOrder> =
    JsonDecode.succeed (fun sku qty note -> { Sku = sku; Quantity = qty; Note = note })
    |> JsonDecode.apply (JsonDecode.field "Sku" JsonDecode.asString)
    |> JsonDecode.apply (JsonDecode.field "Quantity" JsonDecode.asInt32)
    |> JsonDecode.apply (JsonDecode.optionalField "Note" JsonDecode.asString)

// at composition, before ServerApp.run:
JsonDecoders.register<CreateOrder> createOrder
```

Registration is idempotent and explicit; there is no static initialiser. A type you do not register
is unchanged.

## Verification

- `Phase 799` in `ToolUp.Platform.Tests`: every pinned and generated corpus shape decodes to the
  declared value through the algebra and agrees with STJ; the 785.F losses are closed (2^53 + 1,
  `Decimal.MaxValue`, the lost tick); every corpus mutation with a JSON payload has a declared
  algebra outcome; 133 generated per-shape mutations meet theirs, with STJ's class reported beside
  each; the bounded pass refuses depth and width by name; the combinators are total (no throw on
  any shape), pure and reflection-free (IL pin); the seam consults the registry; the platform set
  agrees with STJ over drawn values; the argument facet reports the ratio.
- `Phase 783`: an in-process request against a registered argument type is refused with the
  algebra's path on a missing or mistyped member, and unchanged on the happy path.
- `Remoting STJ wire corpus / TimeSpan`: the converter still loses up to one tick; the algebra
  loses none, over the same 2,000 draws.

## Rollback

Revert the phase's commits. No wire bytes changed. A root that registered JSON decoders removes the
registrations with the revert; the four platform records return to STJ's typed deserialise.
