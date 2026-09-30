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
**is refused by `asInt64` and `asUInt64` on every profile (Phase 911)**, with a refusal that names
the two accepted forms: a JSON number, or the signed string (`"+42"`, `"-7"`; a digit string for
`uint64`). Phase 845 had admitted the shape, for parity with the server's STJ `Int64Converter` /
`UInt64Converter`, which reconstruct a value from it; Phase 911 reverses that. No writer the gates
verify against emits the shape — `Fable.SimpleJson` and the generated `JsonEncode` both write a
number or the signed string, same as `Int64Converter.Write` — so nothing but hand-written pins stood
behind it, and its `unsigned` tag was read by nothing, a lenient read no oracle could see. One
accepted form per type is also simpler than a profile branch. v0.23.0's `asInt64` / `asUInt64`
refused the shape too, so on the algebra path nothing narrows against the last tagged release;
only a tree built between Phases 845 and 911 accepted it there.

**The STJ path refuses it too (Phase 937, breaking in 0.24.0).** An argument type with no
registered decoder is read by the converter set, and `Int64Converter` / `UInt64Converter` used to
rebuild a value from `high` and `low` — as they did in v0.23.0. They no longer do: any token other
than a JSON number or the writer's string is refused, naming the type and both accepted forms in
the words `asInt64` / `asUInt64` use (`expected Int64 as a JSON number or a signed string ("+42",
"-7"), got object`). The refusal is a `validation`-category 400 before the handler runs, like every
other argument refusal on this seam; on this path it is reported at the argument, because the record
converter reads each member through a nested deserialise and STJ's path does not reach the member.
Unlike the algebra path, **this narrows against v0.23.0**: a hand-built request body that sent the
object form to an argument type with no registered decoder decoded there and is refused now. The
caller search below found no such body.

**Callers were checked before the refusal landed.** Every consuming application known to the
maintainers was searched for request bodies built by hand — raw `fetch` / `XMLHttpRequest` bodies,
`JSON.stringify` over values carrying an `int64` / `uint64`. The hits were page-script calls to
non-remoting endpoints (a consent choice, a subscription email, a push-subscription string), local
storage, and a debug log line; none sends an `int64` / `uint64` to a remoting method, so nothing
needed converting. If you build a remoting request body by hand, carry an `int64` / `uint64` as a
JSON number or the signed string — or, better, go through the SDK's own proxy, which already does.

**The reflective client reads an `int64` exactly, or refuses (Phase 911).** A response whose return
type has no registered JSON decoder is read by `Fable.SimpleJson`, which read an `int64` JSON
*number* through `int`: anything outside int32 wrapped (`5000000000` read as `705032704`, 2^53 + 1 as
`0`), and a negative number at `uint64` wrapped to near 2^64. The server's writer sends the signed
string, which was always exact, so SDK-to-SDK traffic was never affected; a number token comes from
any other writer. The proxy now rewrites a number at an `int64` / `uint64` position into the digit
string the library reads exactly, and refuses — as a `DecodeError` on `ProxyRequestException`, with
the path — a number it cannot carry exactly: fractional, negative at `uint64`, or beyond ±(2^53 − 1),
where `JSON.parse` may already have rounded it. A return type with no `int64` / `uint64` anywhere
takes the unchanged path at no cost. Streaming chunks (`IAsyncEnumerable` fields) take the same pass.
Since Phase 937 the pass also refuses an OBJECT at an `int64` / `uint64` position, the same way and
with the path: the library read the `{high, low, unsigned}` form there (at `uint64` it threw an
unnamed error instead), so the reflective read now accepts exactly what `asInt64` / `asUInt64`
accept — a digit string, or a number within ±(2^53 − 1). Like the STJ change, this narrows against
v0.23.0, whose reflective read had no such pass; no SDK writer emits the object form.

**0.24.0 version notes (Phase 937).** Breaking, wire-level only — no public signature moved: the
STJ `Int64Converter` / `UInt64Converter` and the reflective client response read refuse the Fable
`Long` object form they accepted in v0.23.0. A number or the string form decodes on both paths
exactly as before, and both writer oracles Phase 911 pinned stay green.

**A quoted `decimal` is admitted by `asDecimal` (decided Phase 885)**, because that is how the
browser writes one: `Fable.SimpleJson` sends `"1234.50"`, which the converter set always read and
the algebra refused — so a consumer API with a `decimal` argument failed from a reflective client
once its decoder was registered. The string must hold a JSON number token and nothing else. Two
companions from the same phase: on the Fable client `asDateTime` now keeps the text's kind (`…Z` is
`Utc`, not the same instant in an unspecified kind), and **`registerVerified` / `verifyDecoder` /
the generated `registerAllVerified` now verify against the browser's writer as well as the server's**
(`JsonDecoders.browserOracle`). A decoder you register through them that reads the server's text but
refuses the browser's — the pre-885 `asDecimal` was one — is now refused at registration, naming the
draw; that refusal is a real browser call that would have failed. The one such shape 885 measured
is closed by Phase 899: a `Map` keyed by a union with fields (or a tuple/record) arrives from the
browser as an array of `[key, value]` pairs, which `asMap` does not read — `JsonDecode.asMapOf`
takes a key decoder and reads both that form and the server's, and the generator plans such a map
through it (and writes it with `JsonEncode.mapOf`). A map keyed by an enum-like union, or by a
primitive outside `JsonDecode.Key`'s four, is still refused by name. See
`docs/platform/remoting-decoder-algebra.md` §8, "The two map forms".

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
- `Phase 937` in `ToolUp.Platform.Tests` (the STJ argument path) and in the Fable client harness
  (the reflective read): the object form at an `int64` and a `uint64` position is refused by name,
  pinned red first; the digit string and an in-range number still decode.
- `Remoting STJ wire corpus / TimeSpan`: the converter still loses up to one tick; the algebra
  loses none, over the same 2,000 draws.

## Rollback

Revert the phase's commits. No wire bytes changed. A root that registered JSON decoders removes the
registrations with the revert; the four platform records return to STJ's typed deserialise.
