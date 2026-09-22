# The remoting decoder algebra

*Phase 785. Depends on Phase 783 (the `DecodeError` vocabulary), Phase 784 (the wire
differential corpus) and Phase 786 (the reader's length, width and depth bounds). Extended to the
JSON wire by Phase 799 — §8.*

The remoting readers are interpreters over `System.Type`: a mutable-position cursor plus
`FSharp.Reflection` over whatever type the API record declares. That is generic, and it is what
the lineage this transport forked from shipped. It is also why "total on every input" was never a
well-formed statement about it — the input includes an open, possibly recursive type graph, and
the failure mode is a throw from somewhere inside 1,800 lines of type-shape walking.

An open-source F# wire decoder whose combinators were proved total has the opposite shape: a
**closed value model**, **pure combinators** that return a named `Result`, **structural recursion**
that only ever descends into a subterm, and per-type decoders written as closed matches composed
from those combinators. This page documents that shape as it now exists for the MessagePack wire —
and, since Phase 799 (§8), for the JSON wire the server decodes its arguments from.

The algebra is **opt-in**. A type with a registered decoder decodes through it; a type without one
decodes through the reflection reader exactly as it did before. Adopting the SDK version carrying
this phase changes no deployment's behaviour until it registers something (GP 11).

---

## 1. The value model

`ToolUp.Remoting.MsgPack.Value` (`src/ToolUp.Platform.Core/Shared/Remoting/MsgPack/Value.fs`) —
nine cases, immutable, with no `null`:

| Case | Carries |
|---|---|
| `Value.Nil` | — the absent value, as a CASE |
| `Value.Bool` | `bool` |
| `Value.Int` | `int64` × `IntegerWidth` |
| `Value.UInt` | `uint64` × `IntegerWidth` |
| `Value.Float` | `float` × `FloatWidth` (`Single` \| `Double`) |
| `Value.Str` | `string` |
| `Value.Bin` | `byte[]` |
| `Value.Arr` | `Value list` |
| `Value.Map` | `(Value * Value) list`, in wire order |

Three properties are load-bearing, and all three are properties of the DECLARATION rather than of
any decoder written over it.

**Closed.** Nine cases and no extension point, so a decoder's match over them is exhaustive by
construction — which is what makes "accepts the declared type or refuses by name" something a
compiler checks rather than something a reviewer audits.

**No null.** Every case carries a value and the absent value is a case, so a decoder never needs a
null guard and can never fault on one. The reflection path cannot say this: its `Format.Nil` arm
hands out `box null` whatever the target type, which is why Phase 784's `wrong-tag-nil-for-record`
mutation is ACCEPTED there.

**Well-founded.** `Value.size` is 1 for a leaf and 1 + the sizes of its children for a container, so
every subterm is strictly smaller than its container and a recursion that descends only into
subterms terminates. It ships as executable code rather than as a comment so a test can assert the
decrease rather than a reader taking it on trust.

### Two absences, both deliberate

**There is no `Ext` case.** The MessagePack specification has an ext family (`0xc7`–`0xc9`,
`0xd4`–`0xd8`) and a timestamp extension; this transport's reader has neither. An ext byte matches
no arm of `Read` and falls through to its final refusal, so an `Ext` case would be surface every
consumer has to match on and no payload can reach. `MsgPack/Format.fs`'s header records the same
fact from the reader's side.

**The integer case carries the SOURCE WIDTH, not just the value.** Phase 786 established that the
width rule for this format is about INFORMATION and not about signed range: `writeSByte` puts
`-128y` on the wire as `uint8 128`, and `writeDecimal`'s four words ride `write32bitNumber`, so a
negative `int32` arrives as `uint32 0xFFFFFFFF` and the TARGET type recovers the sign. A value model
that dropped the wire's width would force every decoder to re-derive a rule the corpus has already
proved wrong once.

---

## 2. The one bytes-to-`Value` pass

`Reader.ReadValue()` / `Reader.TryReadValue()` sit beside the existing `Read` / `TryRead` in
`MsgPack/Read.fs`. Same format dispatch, calling the same Phase 786 guards rather than re-deriving
them — `RequireAvailable` (every length prefix measured against the bytes actually remaining) and
`EnterContainer` (`Format.DefaultMaxDepth`, 64, overridable per reader). Nothing on the algebra path
can be weaker than the path beside it, and a bound added to one is added to both.

It adds one guard of its own: **a format byte read past the end of the payload is a named refusal**
rather than an `IndexOutOfRangeException`. That guard is in `ReadValue` and not in `ReadByte`,
because `ReadByte` is the reflection reader's hot path too and moving its behaviour belongs to the
phase that owns it.

`TryReadValue` returns `Result<Value, DecodeError>` and never throws for a malformed payload. Like
`TryRead`, **the reader is left where the refusal happened**: read one value per `Reader` on the
refusal path and do not resume.

---

## 3. The combinators

`ToolUp.Remoting.Decode` (`src/ToolUp.Platform.Core/Shared/Remoting/Decode.fs`).
`Decoder<'T> = Value -> Result<'T, DecodeError>`.

| Group | Combinators |
|---|---|
| Pipeline | `succeed`, `fail`, `map`, `bind`, `apply`, `run` |
| Scalars | `asBool`, `asUnit`, `asString`, `asChar` |
| Integers | `asInt32`, `asInt64`, `asInt16`, `asByte`, `asSByte`, `asUInt16`, `asUInt32`, `asUInt64`, `asTimeSpan` |
| Floats | `asFloat`, `asFloat32` |
| Binary | `asBytes`, `asGuid` |
| Arrays | `items`, `exactly`, `index`, `field`, `list`, `array`, `asSet` |
| Maps | `entries`, `asMap` |
| Tuples | `tuple2`, `tuple3`, `tuple4` (Phase 800) |
| Unions | `union`, `case0`, `payload`, `fields` (Phase 800), `stringEnum`, `option`, `result` |
| Composite scalars | `asDateTime`, `asDateTimeOffset`, `asDecimal` |

Four properties hold of every one of them, and each is pinned by a case in
`src/ToolUp.Platform.Tests/Remoting/DecoderAlgebraTests.fs`:

1. **Total** — `Ok` or `Error` on every `Value`. Nothing throws, nothing raises
   `DecodeException`, and there is no `failwith` in the module.
2. **Pure** — no mutable state, no cursor, no cache, no clock. Applied twice to one value, a
   decoder answers twice the same.
3. **Reflection-free** — no `FSharp.Reflection`, no `typeof` dispatch, no `System.Type`. A decoder
   is a closed match over nine cases, which is why its totality is a property a compiler checks.
4. **Structurally recursive** — every recursive combinator descends only into a subterm, whose
   `Value.size` is strictly smaller.

### Width is refused, never cast

`asInt32` applied to an `int64` value that does not fit an `int32` is an `Error`, not a narrowing.
The combinators apply Phase 786's information rule (`Value.signedFits` / `Value.unsignedFits`) and
never a range test of their own:

* a NEGATIVE value never decodes into an unsigned target;
* a source NO WIDER than the target always survives, sign reinterpretation included — that is the
  format, not a loophole;
* a source WIDER than the target must fit the target's own range.

`asFloat32` likewise refuses a `float64` source: widening a `float32` to a `float` is exact and is
admitted, narrowing the other way is not.

### Records are positional, and `field` is what makes the path named

The writer emits a record as an ARRAY of its fields in declaration order (`Write.writeRecord`), so
there are no keys to look a field up by. `field "Name" 0` decodes element 0 and annotates any
refusal beneath it with `Name`, so a consumer reads

```
expected string at Probes.Name, got nil
```

rather than a byte offset. **The path is a property of the DECODER rather than of the wire** — which
is exactly what a hand-written or generated decoder can supply and a reflection walk cannot.

### Two wire facts worth knowing before you write one

**A field-less union is still `[tag]`, not a case name.** `Write.makeSerializerAux` emits a
case-name string only for a union attributed `[<StringEnum>]`; a union whose cases all carry no
fields goes through `writeUnion` like any other. Use `union` with `case0` arms; `stringEnum` is for
the attribute and nothing else.

**A union case's payload is handed over raw.** A case with no fields writes `[tag]` and has no
payload slot; a single-field case writes the field DIRECTLY into the second slot; a several-field
case writes them as an inner array there. A single-field case whose field is itself an array is
therefore indistinguishable from a multi-field case by inspection, so `union` hands the case decoder
a `Value option` and the case's own arity decides — `case0` for no payload, `payload` for one field,
and (since Phase 800) `fields n` for several. `fields` is the combinator the case AUTHOR chooses,
never a runtime guess: it adds the arity check that makes an inner array of the wrong width a named
refusal, and it is what the generator emits for every case with more than one field. `payload` still
admits a pipeline over the inner array as it always has, so nothing that used it changes — the
difference is that `payload` reads the positions its pipeline declares and ignores a trailing
element, as a record decoder does, while `fields` refuses it.

**A tuple is a record with no names.** `Write.writeTuple` emits a tuple exactly as it emits a
record — an array of the elements, positionally — so `int * string` and a two-field record put the
same bytes on the wire, and what makes the term a tuple is only the decoder that reads it.
`tuple2` / `tuple3` / `tuple4` (Phase 800) are an arity check followed by `index` reads: an array of
the wrong width is refused naming both arities, never sliced, and a refusal beneath an element
carries `[n]` because the element has no name to carry.

**A recursive type needs no combinator — it needs a `let rec`.** A union that reaches itself
(`ColumnExpr`'s `Concat of parts: ColumnExpr list * …`) is decoded by an ordinary recursive
binding whose body is eta-expanded, so the recursive reference is read on the first decode rather
than while the module initialises:

```fsharp skip=fragment
type Tree =
    | Leaf of string
    | Branch of label: string * children: Tree list

let rec tree: Decoder<Tree> =
    fun value ->
        (Decode.union "Tree" (function
            | 0 -> Some(Decode.payload (Decode.asString |> Decode.map Leaf))
            | 1 ->
                Some(
                    Decode.fields
                        2
                        (Decode.succeed (fun label children -> Branch(label, children))
                         |> Decode.apply (Decode.field "label" 0 Decode.asString)
                         |> Decode.apply (Decode.field "children" 1 (Decode.list tree)))
                )
            | _ -> None))
            value
```

It terminates for the reason every combinator does: `tree` is only ever reached through `field`,
`index`, `list` or `fields`, each of which descends into a strictly smaller subterm — and the
one-pass reader's 64-container ceiling bounds the value's depth before any decoder runs. Since
Phase 816 the generator emits exactly this shape for a cycle, as one `let rec … and …` group per
strongly connected component of the type graph, leaving every binding outside a cycle the plain
`let` it always was.

---

## 4. How a consumer writes a decoder

The applicative pipeline: `succeed` the constructor, then one `apply (field …)` per field in
declaration order.

```fsharp skip=fragment
open ToolUp.Remoting

type Address = {
    Line1: string
    Postcode: string
    Country: string
}

let address: Decoder<Address> =
    Decode.succeed (fun line1 postcode country -> {
        Line1 = line1
        Postcode = postcode
        Country = country
    })
    |> Decode.apply (Decode.field "Line1" 0 Decode.asString)
    |> Decode.apply (Decode.field "Postcode" 1 Decode.asString)
    |> Decode.apply (Decode.field "Country" 2 Decode.asString)
```

A union dispatches on the tag the writer emits, and an unrecognised tag REFUSES rather than falling
back:

```fsharp skip=fragment
type DeliveryOutcome =
    | Accepted of id: System.Guid * at: System.DateTimeOffset
    | Rejected of reason: string
    | Pending

let deliveryOutcome: Decoder<DeliveryOutcome> =
    Decode.union "DeliveryOutcome" (function
        | 0 ->
            Some(
                Decode.fields
                    2
                    (Decode.succeed (fun id at -> Accepted(id, at))
                     |> Decode.apply (Decode.field "id" 0 Decode.asGuid)
                     |> Decode.apply (Decode.field "at" 1 Decode.asDateTimeOffset))
            )
        | 1 -> Some(Decode.payload (Decode.asString |> Decode.map Rejected))
        | 2 -> Some(Decode.case0 Pending)
        | _ -> None)
```

A tuple is one combinator over its element decoders, and a method returning one registers it under
the tuple type itself:

```fsharp skip=fragment
let roleByName: Decoder<string * DeliveryOutcome> =
    Decode.tuple2 Decode.asString deliveryOutcome

RemotingDecoders.register<string * DeliveryOutcome> roleByName
```

Register it, and register the METHOD RETURN TYPE too — the client's response serializer is handed
the method's return type, not the record inside it:

```fsharp skip=fragment
RemotingDecoders.register<Address> address
RemotingDecoders.register<Result<Address, string>> (Decode.result address Decode.asString)
```

`RemotingDecoders.register` is idempotent; call it once at composition. The client's
`Remoting.withBinarySerialization` calls `PlatformDecoders.registerAll ()` itself, so the platform's
own decoders are live wherever the binary wire is.

**The applicative pipeline is the shape a generator emits** — one `apply` per field, read off the
record's own shape — which is how the generated decoders are covered by Phase 787's theorem by
construction rather than by a second proof.

### Registering a decoder you did not write by hand (Phase 801)

A hand-written decoder is read before it is registered. A generated one is not, and the failure the
algebra cannot see is the one where every combinator is total and correct and the DECODER is wrong —
two same-typed fields swapped, a case index off by one — so it accepts the bytes and yields a
well-typed value that is not the one the server wrote. `RemotingDecoders.verify` is the gate for
that: it draws random values of the type by reflection (`DecoderShapes.draw`), writes each with the
platform's own serializer, decodes the bytes through the candidate AND through the reflection
reader, and refuses on the first divergence, naming the draw and rendering both values.

```fsharp skip=fragment
match RemotingDecoders.verify<Address> RemotingDecoders.DefaultDraws RemotingDecoders.DefaultSeed address with
| Ok verification -> RemotingDecoders.register<Address> address   // verification.Draws agreed
| Error(DecoderDiverges v) -> failwithf "draw %d: %s" v.Divergence.Value.Draw v.Divergence.Value.Candidate
| Error(DecoderUndrawable(t, why)) -> failwithf "%s cannot be drawn: %s" t why

// or, in one call — refuses rather than registers on a divergence:
RemotingDecoders.registerVerified<Address> address
```

The check is **differential, .NET-only, and seeded** — the Fable client has no reflection reader to
compare against, so it registers what the .NET side verified, and a seed makes a refusal
reproducible. A generated module carries the whole set as `verifyAll draws seed` and
`registerAllVerified ()`; `PlatformDecoders` is such a module, and the SDK's test pack runs its
`verifyAll` over every registration before the file is allowed to differ from the generator's
emission.

---

## 5. The opt-in and fallback rule

* A type with a registered decoder decodes through the algebra.
* A type without one decodes through the reflection reader, unchanged.
* A registry **miss is the reflection path, never an error**. The lookup is by `Type.FullName`, so a
  generic instantiation a host renders differently costs the algebra rather than the decode.
* The seam is wired at the CLIENT's binary-response decode
  (`src/ToolUp.Platform.Client/Client/Remoting/Remoting.fs`), because that is where the MessagePack
  bytes are: `MsgPack.Read.Reader` has exactly one production call site in the tree. Server
  arguments arrive as `Choice<byte[], JsonElement>` and decode through System.Text.Json, so a
  MessagePack decoder registered against a server dispatch branch would never be consulted.
  `GeneratedDispatchRegistry` is unchanged — it is keyed by API-record type and holds route
  handlers, which is a different key and a different payload on a different wire.

### The set the SDK ships is generated, and it is the whole platform (Phase 801)

`PlatformDecoders` is **the generator's own emission** over every API record the platform declares
— 38 records, every wire type they carry and every method return — committed as source under
`src/ToolUp.Platform.Core/Shared/Remoting/PlatformDecoders.fs` and pinned by a contract test that
regenerates the file in memory and fails on the first differing byte. Nothing in it is hand-written
and nothing in it is trusted on sight: the same test runs `PlatformDecoders.verifyAll` over every
registration against the reflection reader before the committed file is allowed to stand. The rule
for changing a platform wire type is therefore one line —
`TOOLUP_REGEN_PLATFORM_DECODERS=1 dotnet <ToolUp.Platform.Tests.dll> --filter-test-list "Phase 801"`
rewrites the file; commit it with the type.

Until Phase 801 the module covered two hand-written records (`IHealthMonitorApi`,
`IDeploymentVerificationApi`), and this section said the facet's `Reflection` count was the measured
trigger for the generator. That trigger fired: Phases 800, 816 and 817 closed the last three
expressibility gaps (tuples and several-field cases, recursive types, the one `obj` field), the
census reached 38 of 38, and the generated set replaced the hand-written one. A platform record on
the reflection path is now a **regression the contract test names**, not a design choice.

A consumer's own records are unchanged by this: they decode through the reflection reader until the
consumer registers a decoder — hand-written, or generated with `ToolUp.Remoting.Generator` and
registered through `registerAllVerified`.

---

## 6. The composition-profile facet

Under `CompositionProfile.Verified`, a served API record with no algebra decoder is a **boot
preflight refusal naming the record**. Under every other profile the facet is informational and boot
proceeds. It is never a build gate (GP 13 keeps the lightweight platform untouched).

**The facet enumerates what the composition SERVES, not what a root declared (Phase 801).**
`Api.make` is the one place every mount passes through, and it records the record type it is handed
(`ServedApiRecords`); `RemotingDecoderFacet.inspectServed` classifies that set by reading each
record's method return types off its own shape, so a record a root forgot to declare is a
`Reflection` line rather than an absence. Corpus coverage stays a declaration — a mounted record
proves nothing about whether the corpus draws its shapes — taken from `coveredApiRecords` by name and
`false` otherwise. `RemotingDecoderFacet.coverage` is the ratio, and `ServerApp.run` logs it once at
boot:

```
remoting decoders: 38 of 38 served API record(s) decode through the closed algebra (profile standard, algebra decoders advisory)
```

The declared form (`inspect` / `inspectPlatform`) is kept for a root that wants to grade a list it
composed itself; the two go through the same classifier, so a served record and a declared one are
graded identically. The deployment verification report derives its section from the served set when
a root supplies none.

The deployment verification report carries the facet per record with the `Verified` / `Observed`
split it already uses: **`Verified` only where the record decodes through the algebra AND the wire
corpus draws the shapes it carries**, `Observed` otherwise. A deployment's own assertion about
itself never reads as a pass. A `Reflection` record is `Observed` and never `Failed`, whatever the
profile — reflection decoding is the shipped default for a consumer's own records, and a report
that reddened on it would be reporting a consumer's posture as a defect.

The facet binds the vertical built on this platform and nothing else: the coordination plane it is
composed alongside takes no remoting from here at all, by the cross-pillar rule.

---

## 7. What this improves / what it does not

The trust-boundary statement this facet carries, verbatim — and the text the Phase 777 entry for
remoting decoders cites this page for rather than restating. Its **mechanism reference** is
[`proofs/README.md`](../../proofs/README.md) (Phase 787), whose four-rung claims ladder says which
part of the statement below is proved, which is differentially tested, which is assumed, and which
is not claimed at all:

```
Crosses:     client bytes on both remoting wires, at every request
Mediates:    the closed value model with length and depth bounds (786);
             total combinators that accept the declared type or refuse by name (785);
             the wire corpus (784) and the extracted-model differential host (787)
Claim:       no client bytes can drive the decode edge to divergence, unbounded
             allocation, or a silently mistyped value; every decode is an accept
             of the declared type or a named refusal routed to the validation
             envelope before any handler or pre-flight stage runs
Not claimed: authorisation, tenancy or session integrity of a decoded value;
             the bytes-to-value pass (bounded, not proved); semantic validity
             (Phase 69e's domain); the STJ path for any argument type not
             registered (799); the reflection fallback for any record not
             opted in
```

Three things the statement's "Not claimed" list covers that are worth stating concretely, because
each is a measured finding rather than a caution:

* **One narrowing is unrefusable at the reader, by construction.** `writeInt64` compacts
  `2147483648L` into bytes that are byte-for-byte a well-formed `int32 -2147483648` — the shape
  `writeDecimal`'s sign word actually travels in. The value model faithfully carries a 32-bit source,
  and a 32-bit source into a 32-bit target is admitted by the information rule, correctly: refusing
  it would refuse every negative decimal the corpus pins. Closing it needs the EMITTER to stop
  flattening a signed value into an unsigned format, which is a wire break.
* **An extra trailing element is accepted.** A record decoder reads the positions it declares and
  ignores what follows, in agreement with the reflection reader. That is what keeps an additive wire
  change non-breaking — a server that grows a field must not break every client compiled against the
  older record — and it is the one evolution property this wire has.
* **`DateOnly` and `TimeOnly` have no combinator.** Phase 784 measured that the Fable MessagePack
  reader refuses both types outright, so a primitive for them would decode on one host and not the
  other. This ships a cross-host algebra or it ships nothing; the reflection path keeps both working
  on .NET exactly as before.

---

## 8. The JSON wire — shipped (Phase 799, closing 785.F)

Phase 785 decided that the algebra should extend to the JSON wire, and NOT over
`ToolUp.AI.Wire.JsonValue` as it stood: its `JNumber of float` cannot carry `int64` past 2^53,
exact decimals, or the token's source form — the class of loss Phase 784 pinned live on this wire
(the STJ converter loses up to one `TimeSpan` tick). Phase 799 ships the extension.

### The model — `ToolUp.Remoting.Json.JsonValue`

A sibling model in the remoting namespace rather than a widened `AI.Wire` one: that type is
matched exhaustively in forty-five files across the provider mappings, and a seventh case is a
break in every one of them. Six closed cases, no null, insertion-ordered members, a `size`
measure — and **`Number of lexical: string`**: the token text as written, never parsed to a
`float` on the way in. `JsonValue.tryInt64` / `tryUInt64` / `tryDecimal` / `tryFloat` read the
width a decoder needs from the text, exactly. The number grammar is RFC 8259's, checked by hand
so both hosts run the same code.

### The pass — `JsonRead`

`JsonRead.tryRead` builds the model from a `JsonElement` System.Text.Json has already parsed (the
dispatcher's outer-array parse hands each argument over as one), under the same bounds discipline
the MessagePack pass has: **depth** (64, agreeing with the reader's `Format.DefaultMaxDepth` and
STJ's own parse bound) and **width** (a million elements or members per container). Both are named
refusals. `GetRawText()` on a number token is what keeps the digits verbatim. `JsonRead.tryParse`
takes text, for a caller holding a string; the parse is STJ's, and a document that is not JSON is
a refusal naming what the parser saw, never an exception.

### The combinators — `JsonDecode`

The §3 surface, transposed to what this wire is:

* **Records are NAMED.** `field "Name" decoder` looks a member up by name; an absent member is a
  refusal naming it. `optionalField` is the arm for a field declared `option` — the writer's
  `None` is `null`, and an older client's omission reads the same way.
* **Width is read from the text.** The integer arms require an integral token (`1`, not `1.0` or
  `1e0` — the writer never emits an integer that way) and a range fit; `asDecimal` reads the
  digits as written; `asInt64` / `asUInt64` also accept the STRING form the writer emits
  (`"+42"`, signed so a JavaScript reader cannot take it for a float).
* **`asTimeSpan` is exact.** The writer emits total milliseconds as a double; the STJ reader
  multiplies back and TRUNCATES, which is the tick Phase 784 saw lost. The combinator parses the
  token as a `decimal`, scales by ten thousand in decimal arithmetic and rounds to the nearest
  tick — recovering the tick the writer started from for every span the double could carry.
  `StjRoundTripTests.timeSpanTickLoss` now measures both paths over the same 2,000 draws: the
  converter loses, the algebra does not.
* **Unions dispatch on the CASE NAME**, in the writer's shape — a field-less case is its name as a
  string, a case with fields is `{"Case": payload}` with the payload the single field or an array
  of several (`fields n`). The three legacy READ shapes the STJ converter also tolerates
  (`{tag,name,fields}`, `__typename`, `["Case", …]`) are not admitted; a decoder accepts what the
  writer writes.
* **`asMap` takes a key parser** (`JsonDecode.Key.string` / `int32` / `int64` / `guid` /
  `parse`), because a non-string key arrives as the property NAME — the key's own JSON text.
* `DateOnly` / `TimeOnly` ARE here (.NET only): this wire's decode seam is server-side, so the
  cross-host argument that keeps them out of the MessagePack algebra does not apply.

Every combinator is total, pure and reflection-free, and `JsonDecoderAlgebraTests` pins each the
way `DecoderAlgebraTests` does — including the IL pin.

### The seam — server ARGUMENT decoding

This is the adoption that matters, and it is the other direction from §5's. The MessagePack algebra
guards the CLIENT's decode of the server's response. The server's own decode edge is the argument
side: client JSON arriving at the Phase 783 seam (`FableConverters.tryDeserialise` /
`tryDeserialiseElement`), which until now went straight into STJ's typed `Deserialize`. **The seam
now consults `JsonDecoders` first** — the JSON twin of `RemotingDecoders`, same key, same
`register<'T>` / `tryGet` shape — and a registered type decodes through one bounded pass and its
decoder; a miss is the STJ path, exactly as before. Nothing about a type nobody registered changes.

What changes for a registered type is what the differential arm reports beside the STJ path
(`JsonDecoderAlgebraTests`, "the algebra refuses strictly more"): a `null` for a record, a missing
declared field, a quoted `"7"` at `int`, an array where a `Map` was declared, and malformed text
are all named refusals routed to the `validation` envelope before the handler runs — where STJ
accepted the first four (the null and the absent field as `null` references, the quoted number
under `AllowReadingFromString`, the empty array as an empty map) and threw on the last.

**The platform's own first set — `PlatformJsonDecoders`.** Hand-written, in the shape the generator
will emit once it learns this wire (69k.B), registered by `ServerApp.run` beside the MessagePack
set, and chosen so four platform records take every argument through the algebra: `IPresenceApi`,
`IAuditViewApi`, `IProvenanceQueryApi`, `ITeamInviteApi`. Deliberately NOT in it: `string`,
`Guid` and the primitive tuples, which are shared with every consumer's own records — a `string`
decoder registered here would change how a consumer's string arguments read on upgrade (a `null`
would refuse rather than arrive), and a platform-set registration is a statement about the
platform's records (GP 11).

### The facet, on this side

`RemotingDecoderFacet.inspectServedArguments` classifies the served set (§6) by each record's
ARGUMENT types against `JsonDecoders`, the same binding shape and the same classifier, and
`ServerApp.run` logs a second line:

```
remoting argument decoders: 4 of 38 served API record(s) take every argument through the JSON algebra (profile standard, advisory)
```

It is **advisory under every profile** for now, deliberately: the first set is narrow, and a
`Verified` deployment that refused on a served record with no JSON decoder would refuse nearly
every deployment on the day it shipped. It becomes mandatory under `Verified` when the generator
emits argument decoders and the platform's records are covered by construction — the road the
response facet travelled between Phases 785 and 801.

### What moved in the trust-boundary statement

The §7 "Not claimed" list read "the STJ path until 785.F". It now reads "the STJ path for any
argument type not registered" — the same opt-in boundary the reflection fallback has always had,
stated for the second wire. A registered argument type is inside the claim: its bytes become a
value of the declared type or a named refusal, with bounded depth and width, before any handler
runs.

---

## See also

* `src/ToolUp.Platform.Core/Shared/Remoting/MsgPack/Value.fs` — the value model and its measure.
* `src/ToolUp.Platform.Core/Shared/Remoting/Decode.fs` — the combinators.
* `src/ToolUp.Platform.Core/Shared/Remoting/DecoderRegistry.fs` — the opt-in seam.
* `src/ToolUp.Platform.Core/Shared/Remoting/PlatformDecoders.fs` — the generated platform set.
* `src/ToolUp.Platform.Core/Shared/Remoting/Json/JsonValue.fs`, `JsonDecode.fs`,
  `JsonDecoderRegistry.fs`, `PlatformJsonDecoders.fs` — the JSON wire (Phase 799); the pass is
  `src/ToolUp.Platform.Server/Server/Remoting/Json/JsonRead.fs`.
* `src/ToolUp.Platform.Tests/Remoting/JsonDecoderAlgebraTests.fs` — the JSON differential.
* `src/ToolUp.Platform.Core/Shared/Remoting/MsgPack/Format.fs` — the reader's three bounds.
* `src/ToolUp.Platform.Tests/Remoting/DecoderAlgebraTests.fs` — the differential, the round-trip
  law, the purity pins and the committed go-red case.
* `src/ToolUp.Platform.Tests/Remoting/WireCorpus.fs` — the corpus the differential runs over.
