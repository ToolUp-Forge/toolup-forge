# The remoting decoder algebra

*Phase 785. Depends on Phase 783 (the `DecodeError` vocabulary), Phase 784 (the wire
differential corpus) and Phase 786 (the reader's length, width and depth bounds).*

The remoting readers are interpreters over `System.Type`: a mutable-position cursor plus
`FSharp.Reflection` over whatever type the API record declares. That is generic, and it is what
the lineage this transport forked from shipped. It is also why "total on every input" was never a
well-formed statement about it — the input includes an open, possibly recursive type graph, and
the failure mode is a throw from somewhere inside 1,800 lines of type-shape walking.

An open-source F# wire decoder whose combinators were proved total has the opposite shape: a
**closed value model**, **pure combinators** that return a named `Result`, **structural recursion**
that only ever descends into a subterm, and per-type decoders written as closed matches composed
from those combinators. This page documents that shape as it now exists for the MessagePack wire.

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
record's own shape — which is how Phase 69k's generated decoders will be covered by Phase 787's
theorem by construction rather than by a second proof.

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

### The named, bounded set the SDK ships

`PlatformDecoders` covers two platform-owned API records and every wire type they carry:
`IHealthMonitorApi` (ten records plus five `Result<_, string>` returns) and
`IDeploymentVerificationApi` (five records plus one). A record outside that set keeps the reflection
path **by design, not by omission**: the facet's `Reflection` count under the verified profile, once
this set is exhausted, is the measured trigger for the source generator — and a further hand-written
decoder makes that number smaller and the case for the generator weaker.

The set was chosen from the platform's own always-mounted surfaces rather than from a traffic
ranking. There is no per-API-record call counter in this repository to read one from; the tier
records per-request metrics and audit envelopes, and nothing counts calls per record.

---

## 6. The composition-profile facet

Under `CompositionProfile.Verified`, a registered API record with no algebra decoder is a **boot
preflight refusal naming the record**. Under every other profile the facet is informational and boot
proceeds. It is never a build gate (GP 13 keeps the lightweight platform untouched), and a
deployment that declares no API records declares nothing to check — so an existing verified
deployment that upgrades boots exactly as it did.

The deployment verification report carries the facet per record with the `Verified` / `Observed`
split it already uses: **`Verified` only where the record decodes through the algebra AND the wire
corpus draws the shapes it carries**, `Observed` otherwise. A deployment's own assertion about
itself never reads as a pass. A `Reflection` record is `Observed` and never `Failed`, whatever the
profile — reflection decoding is the shipped default and every deployment's baseline, and a report
that reddened on it would be reporting the platform's own posture as a defect.

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
             (Phase 69e's domain); the STJ path until 785.F; the reflection
             fallback for any record not opted in
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

## 8. The JSON wire — a decision, not an implementation (785.F)

**Decision: the same algebra should extend to the JSON wire in a follow-on phase, and NOT over
`ToolUp.AI.Wire`'s `JsonValue` as that type stands.**

`ToolUp.AI.Wire.JsonValue` (`src/ToolUp.AI.Wire/JsonValue.fs`) is the obvious carrier and was
assessed as such. It is already the right SHAPE: a six-case closed value model, FSharp.Core-only, no
`System.Text.Json` dependency, compiling unchanged on both the .NET and Fable hosts, with
insertion-ordered object members so serialisation is byte-stable — and `Platform.Core` already
references the package it lives in, so adopting it would cost no new dependency. Its `JsonValue`
companion module already carries total accessors in exactly the `option`-returning style the
combinators want.

The blocker is one field: **`JNumber of float`**. A single IEEE double cannot carry what the
MessagePack algebra's whole width discipline exists to preserve —

* `int64` beyond 2^53 loses precision silently, which is the class of failure Phase 786 was written
  to end;
* `decimal` is exact on the MessagePack wire (four 32-bit words) and would become approximate;
* the SOURCE WIDTH is gone entirely, so `asInt32` could not distinguish the same-width
  reinterpretation the format depends on from a genuine narrowing, and would have to fall back to a
  range test — the rule the Phase 784 corpus proved wrong for this transport.

Phase 784 also pinned a live defect on this wire that bears directly on the decision: the STJ
converter set loses up to one tick on `TimeSpan` (142 of 2,000 drawn values), where MessagePack is
exact. That is the same class of loss — a numeric carrier that cannot represent what the type
declares — arriving through a different door.

So the follow-on phase adopts the algebra over a JSON value model whose numeric case preserves the
token's **lexical form** (the digits as written, plus a parsed convenience), and the choice between
widening `JsonValue` with such a case and declaring a sibling model in `ToolUp.Remoting` is that
phase's to make on the evidence of what else consumes `JsonValue` by then. What is decided here is
that the JSON wire is in scope for the same treatment, and that a `float`-only numeric carrier
disqualifies any model from carrying it.

Until that phase ships, the JSON path is outside the trust-boundary claim above, and the statement
says so.

---

## See also

* `src/ToolUp.Platform.Core/Shared/Remoting/MsgPack/Value.fs` — the value model and its measure.
* `src/ToolUp.Platform.Core/Shared/Remoting/Decode.fs` — the combinators.
* `src/ToolUp.Platform.Core/Shared/Remoting/DecoderRegistry.fs` — the opt-in seam.
* `src/ToolUp.Platform.Core/Shared/Remoting/PlatformDecoders.fs` — the named, bounded set.
* `src/ToolUp.Platform.Core/Shared/Remoting/MsgPack/Format.fs` — the reader's three bounds.
* `src/ToolUp.Platform.Tests/Remoting/DecoderAlgebraTests.fs` — the differential, the round-trip
  law, the purity pins and the committed go-red case.
* `src/ToolUp.Platform.Tests/Remoting/WireCorpus.fs` — the corpus the differential runs over.
