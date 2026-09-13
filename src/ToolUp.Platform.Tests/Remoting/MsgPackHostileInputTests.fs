module ToolUp.Platform.Tests.Remoting.MsgPackHostileInputTests

open System
open System.Buffers.Binary
open System.Diagnostics
open System.IO
open Expecto
open ToolUp.Remoting

// ─── Phase 786 — the MsgPack reader under hostile input ──────────────
//
// Three defects, three bounds, one pack. Before this phase the reader
// believed every number, every length prefix and every level of nesting
// a payload claimed: an `Int64` aimed at an `int` field was truncated and
// the decode SUCCEEDED, a five-byte `Array32` header allocated from its
// own prefix before a single element was read, and nesting recursed on
// the wire's say-so until the stack died — which on .NET is process
// death, not an exception.
//
// **Whose input is this?** The reader's one production call site is
// `Remoting.withBinarySerialization` in the CLIENT tier: it decodes the
// SERVER's binary response. Server arguments arrive as JSON and decode
// through System.Text.Json, never through here. So "hostile" means a
// compromised, mismatched or simply wrong server as seen by a browser —
// plus every consumer of the reader as public API, which is what makes
// this worth bounding at the reader rather than at the caller. The
// bounds are stated in `MsgPack/Format.fs`'s header.
//
// Every bound is probed in BOTH directions. A guard that refuses
// everything passes a one-directional test exactly as well as a correct
// one, and the honest failure mode of a length check is refusing a
// legitimate payload — so each hostile case is paired with the largest
// well-formed payload the same guard must still admit.
//
// ── Why the well-formed side is part hand-built bytes, part writer
//    round-trip ─────────────────────────────────────────────────────────
//
// Building the well-formed fixtures by serialising a value and reading it
// back is the obvious shape, and it is used below wherever it holds. The
// `str` and `decimal` arms are ALSO pinned against hand-built bytes, and
// that is deliberate rather than belt-and-braces.
//
// Both arms were discovered broken while this pack was being written, on
// the EMITTER side: `Write.writeString` put out a correct `A5` header
// over five bytes of unrelated memory for the string `probe`, and
// `Write.writeDecimal` lost a word of `Decimal.GetBits`, turning
// `12345.6789m` into `123456789m` in one run and `42m` into
// `180388626474m` in another. The cause was `Write.fs`'s `stackalloc`
// helper returning a `Span` over its OWN frame: honoured as an `inline`
// the allocation lands in the caller and the span is valid, and with the
// F# optimiser off it is not honoured, so the caller read whatever the
// next call reused that stack for. Debug corrupt, Release correct. Phase
// 784 fixed it by giving each call site its own buffer, and that fix is
// in this tree.
//
// The fixtures stay hand-built because of what the bug was: a round-trip
// assertion over an emitter cannot distinguish a correct codec from two
// halves agreeing on nonsense, and it did not distinguish them here. The
// round trips below are kept too, now that the emitter is sound — they
// catch a different class, an encoding the reader silently starts
// accepting a variant of.

/// A recursive shape, so a payload can nest arbitrarily deep through the
/// reader's union arm. `Leaf` is tag 0, `Node` is tag 1 (declaration
/// order), which `hand-crafted bytes are the bytes the writer emits`
/// below pins rather than assumes.
type Nest =
    | Leaf of int
    | Node of Nest

/// The round-trip probe: every field's arm is one the emitter gets right,
/// so a failure here is about the reader. Strings live in `Labelled`
/// below, which is pinned against hand-built bytes instead.
type Probe = {
    Count: int
    Tags: int list
    Scores: Map<int, int>
    Moment: DateTime
}

/// The hand-crafted probe: a record whose first field is a `str`.
type Labelled = { Name: string; Count: int }

/// `frames` nested union frames: `Node (Node (… (Leaf 7)))`. Each frame
/// is `fixarr 2` + the case tag; the innermost carries the int field.
let private nested (frames: int) = [|
    for _ in 1 .. frames - 1 do
        yield MsgPack.Format.fixarr 2
        yield 1uy // Node
    yield MsgPack.Format.fixarr 2
    yield 0uy // Leaf
    yield 7uy // its int field, as a fixposnum
|]

let private int64Payload (value: int64) =
    let bytes = Array.zeroCreate 9
    bytes[0] <- MsgPack.Format.Int64
    BinaryPrimitives.WriteInt64BigEndian(Span(bytes, 1, 8), value)
    bytes

let private uint64Payload (value: uint64) =
    let bytes = Array.zeroCreate 9
    bytes[0] <- MsgPack.Format.Uint64
    BinaryPrimitives.WriteUInt64BigEndian(Span(bytes, 1, 8), value)
    bytes

/// A 32-bit length prefix on a body far too small to hold it — the
/// "gigabytes from a five-byte header" shape, for whichever format byte
/// carries a `uint32` length.
let private overClaimed (format: byte) (claimed: uint32) =
    let bytes = Array.zeroCreate 6
    bytes[0] <- format
    BinaryPrimitives.WriteUInt32BigEndian(Span(bytes, 1, 4), claimed)
    bytes

let private refusalOf (payload: byte[]) (t: Type) =
    match MsgPack.Read.Reader(payload).TryRead t with
    | Ok value -> failtestf "expected a refusal decoding %s, got %A" t.Name value
    | Error error -> error

let private valueOf (payload: byte[]) (t: Type) =
    match MsgPack.Read.Reader(payload).TryRead t with
    | Ok value -> value
    | Error error -> failtestf "expected %s to decode, got a refusal: %s" t.Name (DecodeError.render error)

let private encode (value: obj) =
    use buffer = new MemoryStream()
    MsgPack.Write.serializeObj value buffer
    buffer.ToArray()

let private roundTrip<'T> (value: 'T) =
    valueOf (encode (box value)) typeof<'T> :?> 'T

/// The four shapes the phase names, plus the two cross-sign narrowings a
/// `float`-based width test could not have decided. Each entry must
/// refuse; the pack asserts that once per shape with its own reason, and
/// once collectively under a time and allocation ceiling.
let private hostileCorpus = [
    "a narrowed int64", int64Payload 4294967296L, typeof<int>
    "a uint64 past Int64.MaxValue", uint64Payload UInt64.MaxValue, typeof<int64>
    "a negative value aimed at an unsigned target", [| MsgPack.Format.Int8; 0xFFuy |], typeof<uint32>
    "a 2 GiB array claim", overClaimed MsgPack.Format.Array32 2147483647u, typeof<int[]>
    "an array claim past Int32.MaxValue", overClaimed MsgPack.Format.Array32 2147483648u, typeof<int[]>
    "a 2 GiB map claim", overClaimed MsgPack.Format.Map32 2147483647u, typeof<Map<string, int>>
    "a str running past the buffer", [| MsgPack.Format.Str8; 200uy; 0x61uy; 0x62uy; 0x63uy |], typeof<string>
    "a bin running past the buffer", [| MsgPack.Format.Bin8; 100uy; 0x00uy |], typeof<byte[]>
    "a 10,000-deep nesting", nested 10_000, typeof<Nest>
]

let tests =
    testList "Phase 786 — MsgPack hostile input" [

        // ── 786.A — width-exact integers ──────────────────────────────

        test "an int64 that does not fit the target width refuses instead of truncating" {
            // 4294967296 = 2^32. Before this phase `int32 n` made that a
            // perfectly good `0` and the decode SUCCEEDED, so the typed
            // contract said `int` and the wire delivered something else
            // with nothing in between to notice. That silent `0` is the
            // falsifier: if this assertion is ever relaxed, the value it
            // would have to accept is zero.
            let error = refusalOf (int64Payload 4294967296L) typeof<int>

            Expect.equal error.Expected "Int32" "the refusal names the width that was wanted"
            Expect.stringContains error.Found "out-of-range" "and that the value did not fit it"
            Expect.stringContains error.Found "4294967296" "naming the value it actually found"
        }

        test "an int64 that DOES fit the target width still decodes" {
            // The other direction: the guard must not have become
            // `refuse every Int64 payload`, which would pass the
            // assertion above unchanged.
            Expect.equal (valueOf (int64Payload 7L) typeof<int>) (box 7) "a representable value is unaffected"

            Expect.equal
                (valueOf (int64Payload (int64 Int32.MinValue)) typeof<int>)
                (box Int32.MinValue)
                "and so is each end of the target's own range"

            Expect.equal
                (valueOf (int64Payload (int64 Int32.MaxValue)) typeof<int>)
                (box Int32.MaxValue)
                "including the top of it"
        }

        test "the two cross-sign narrowings refuse — the cases a float range test cannot decide" {
            // A `uint64` above Int64.MaxValue and a negative value aimed
            // at an unsigned target are both exactly representable only
            // in the domain the check widens into. A common `float`
            // domain loses the low bits at 2^63 and would accept the
            // first.
            let big = refusalOf (uint64Payload UInt64.MaxValue) typeof<int64>
            Expect.equal big.Expected "Int64" "a uint64 past Int64.MaxValue is not an int64"

            let negative = refusalOf [| MsgPack.Format.Int8; 0xFFuy |] typeof<uint32>
            Expect.equal negative.Expected "UInt32" "and -1 is not a uint32"

            // Both values are legitimate for their OWN targets.
            Expect.equal
                (valueOf (uint64Payload UInt64.MaxValue) typeof<uint64>)
                (box UInt64.MaxValue)
                "the same bytes decode where the target can hold them"

            Expect.equal (valueOf [| MsgPack.Format.Int8; 0xFFuy |] typeof<int32>) (box -1) "and so does the negative"
        }

        test "the narrow arms — byte, sbyte, int16, uint16 — refuse over-wide values" {
            for target, payload in
                [
                    typeof<byte>, int64Payload 256L
                    typeof<sbyte>, int64Payload 128L
                    typeof<int16>, int64Payload 32768L
                    typeof<uint16>, int64Payload 65536L
                ] do
                let error = refusalOf payload target

                Expect.equal
                    error.Expected
                    target.Name
                    (sprintf "%s refuses the first value past its width" target.Name)

            // …and accept the last value inside it.
            Expect.equal (valueOf (int64Payload 255L) typeof<byte>) (box 255uy) "byte accepts 255"
            Expect.equal (valueOf (int64Payload 127L) typeof<sbyte>) (box 127y) "sbyte accepts 127"
            Expect.equal (valueOf (int64Payload 32767L) typeof<int16>) (box 32767s) "int16 accepts 32767"
            Expect.equal (valueOf (int64Payload 65535L) typeof<uint16>) (box 65535us) "uint16 accepts 65535"
        }

        // ── 786.B — length against the bytes actually present ─────────

        test "a 2 GiB array header on a six-byte body refuses before allocating" {
            let error = refusalOf (overClaimed MsgPack.Format.Array32 2147483647u) typeof<int[]>

            Expect.stringContains error.Expected "array" "the refusal is about the array's length"
            Expect.stringContains error.Found "2147483647" "naming what the prefix claimed"
            Expect.stringContains error.Found "remaining" "against what was actually left"
        }

        test "a length above Int32.MaxValue refuses — it arrives as a NEGATIVE int" {
            // 0x80000000 elements is how a two-gibibyte claim actually
            // presents: `ReadUInt32() |> int` wraps it to -2147483648, so
            // a guard written only as `len > remaining` would wave it
            // through and `Array.CreateInstance` would then throw
            // something that is not a refusal.
            let error = refusalOf (overClaimed MsgPack.Format.Array32 2147483648u) typeof<int[]>
            Expect.stringContains error.Expected "array" "the negative length is refused by the same check"
        }

        test "a str and a bin whose lengths run past the buffer refuse" {
            let str =
                refusalOf [| MsgPack.Format.Str8; 200uy; 0x61uy; 0x62uy; 0x63uy |] typeof<string>

            Expect.stringContains str.Expected "str" "the refusal names the shape whose length over-claimed"
            Expect.stringContains str.Found "200" "and the length it claimed"

            let bin = refusalOf [| MsgPack.Format.Bin8; 100uy; 0x00uy |] typeof<byte[]>
            Expect.stringContains bin.Expected "bin" "likewise for bin"
        }

        test "a map claiming more entries than two bytes each can afford refuses" {
            let error =
                refusalOf (overClaimed MsgPack.Format.Map32 2147483647u) typeof<Map<string, int>>

            Expect.stringContains error.Expected "map" "a map entry costs at least a key byte and a value byte"
        }

        test "the length guard admits exactly what fits, and refuses exactly one more" {
            // The boundary in both directions, which is what separates a
            // correct guard from one that refuses everything. Three
            // fixposnums after a `fixarr 3` header leave exactly three
            // bytes for three elements.
            Expect.equal
                (valueOf [| MsgPack.Format.fixarr 3; 1uy; 2uy; 3uy |] typeof<int list>)
                (box [ 1; 2; 3 ])
                "a length equal to the bytes remaining is well-formed and decodes"

            let error = refusalOf [| MsgPack.Format.fixarr 4; 1uy; 2uy; 3uy |] typeof<int list>
            Expect.stringContains error.Expected "array" "one element more than the bytes present refuses"

            let str =
                valueOf [| MsgPack.Format.Str8; 3uy; 0x61uy; 0x62uy; 0x63uy |] typeof<string>

            Expect.equal str (box "abc") "and a str whose length is exactly the bytes remaining decodes"
        }

        test "a collection past the initial-capacity ceiling still decodes correctly" {
            // The growth path, which only runs above the ceiling. A
            // 4,000-element array exercises it several doublings deep;
            // the assertion is on the VALUES, so a growth bug that lost
            // or duplicated elements cannot pass.
            let source = [| 0..3999 |]
            let decoded = roundTrip source

            Expect.equal decoded source "growing past the ceiling preserves every element in order"

            let asList = roundTrip (List.ofArray source)
            Expect.equal asList (List.ofArray source) "the list deserializer likewise"

            let asMap = source |> Array.map (fun i -> i, i * 2) |> Map.ofArray
            Expect.equal (roundTrip asMap) asMap "and the map deserializer"

            let asSet = Set.ofArray source
            Expect.equal (roundTrip asSet) asSet "and the set deserializer"
        }

        // ── 786.C — the depth bound ───────────────────────────────────

        test "hand-crafted nesting bytes are the bytes the writer emits" {
            // The deep payloads below are built by hand, because
            // constructing a 10,000-deep value and serialising it would
            // recurse 10,000 frames in the WRITER — proving nothing about
            // the reader and quite possibly dying first. This pins the
            // hand-crafted encoding against the real emitter at a depth
            // where both are safe, so the hostile payloads cannot quietly
            // stop being the shape they claim to be.
            Expect.equal (nested 3) (encode (box (Node(Node(Leaf 7))))) "three frames, hand-built and emitted, agree"

            Expect.equal (roundTrip (Node(Node(Leaf 7)))) (Node(Node(Leaf 7))) "and that shape round-trips"
        }

        test "a 10,000-deep nesting refuses, and the process survives to say so" {
            let error = refusalOf (nested 10_000) typeof<Nest>

            Expect.stringContains error.Expected "nesting" "the refusal is about depth"
            Expect.stringContains error.Expected (string MsgPack.Format.DefaultMaxDepth) "naming the bound it exceeded"
            Expect.isNonEmpty error.Path "and carries the byte offset it gave up at"
        }

        test "the depth bound admits exactly the default, and refuses exactly one more" {
            Expect.isTrue
                (match MsgPack.Read.Reader(nested MsgPack.Format.DefaultMaxDepth).TryRead typeof<Nest> with
                 | Ok _ -> true
                 | Error _ -> false)
                "nesting exactly as deep as the bound is accepted"

            let error = refusalOf (nested (MsgPack.Format.DefaultMaxDepth + 1)) typeof<Nest>
            Expect.stringContains error.Expected "nesting" "one container deeper refuses"
        }

        test "the per-reader override replaces the default in both directions" {
            // The bound is declared where the reader can see it and
            // overridden at the seam that constructs one. A
            // `RemotingOptions` field could not reach this code: that
            // record is server-tier, and this reader's only production
            // caller is the client decoding a response.
            Expect.isTrue
                (match MsgPack.Read.Reader(nested 8, 8).TryRead typeof<Nest> with
                 | Ok _ -> true
                 | Error _ -> false)
                "a tightened reader still accepts nesting at its own bound"

            match MsgPack.Read.Reader(nested 9, 8).TryRead typeof<Nest> with
            | Ok value -> failtestf "expected the tightened bound to refuse, got %A" value
            | Error error -> Expect.stringContains error.Expected "8" "and refuses past it, naming the override"
        }

        // ── the whole corpus, bounded ─────────────────────────────────

        test "every hostile payload refuses in bounded time and bounded memory" {
            // The claim the bounds exist to support, measured rather than
            // argued: no payload here may cost time or allocation
            // proportional to what it CLAIMS, only to what it carries.
            // A single un-guarded 2 GiB array header would allocate ~17 GB
            // of object references and take the runner out with it — the
            // memory kill the gate reports as exit 143 — so the ceiling
            // below is not a micro-budget, it is the difference between
            // "refused" and "process gone".
            //
            // Warm the reflection caches first on the same target types,
            // so the measurement is about the hostile reads rather than
            // about first-use delegate construction.
            roundTrip [| 1; 2; 3 |] |> ignore
            roundTrip (Map.ofList [ 1, 2 ]) |> ignore
            roundTrip (Node(Leaf 1)) |> ignore

            valueOf [| MsgPack.Format.Str8; 3uy; 0x61uy; 0x62uy; 0x63uy |] typeof<string>
            |> ignore

            let allocationCeiling = 16L * 1024L * 1024L
            let before = GC.GetAllocatedBytesForCurrentThread()
            let clock = Stopwatch.StartNew()

            for name, payload, target in hostileCorpus do
                match MsgPack.Read.Reader(payload).TryRead target with
                | Error _ -> ()
                | Ok value -> failtestf "%s decoded to %A instead of refusing" name value

            clock.Stop()
            let allocated = GC.GetAllocatedBytesForCurrentThread() - before

            Expect.isLessThan
                allocated
                allocationCeiling
                (sprintf
                    "the whole hostile corpus allocated %d bytes — nothing here may allocate from a prefix"
                    allocated)

            Expect.isLessThan
                clock.Elapsed.TotalSeconds
                10.0
                "and none of it may spend time proportional to a claimed length"
        }

        // ── well-formed traffic is untouched ──────────────────────────

        test "well-formed traffic round-trips unchanged" {
            // The regression half. Every bound above is a refusal on the
            // hostile side and a no-op on this one; a change that tightens
            // one too far shows up here first.
            let instant = DateTime(2026, 9, 13, 11, 22, 33, DateTimeKind.Utc)

            let probe = {
                Count = 42
                Tags = [ 1; 2; 3 ]
                Scores = Map.ofList [ 1, 10; 2, 20 ]
                Moment = instant
            }

            Expect.equal (roundTrip probe) probe "a record with a list, a map and a date"
            Expect.equal (roundTrip (Some 7)) (Some 7) "an option"
            Expect.equal (roundTrip (None: int option)) None "and its empty case"
            Expect.equal (roundTrip [| 1uy; 2uy; 3uy |]) [| 1uy; 2uy; 3uy |] "a byte array (the bin arm)"
            Expect.equal (roundTrip (Set.ofList [ 3; 1; 2 ])) (Set.ofList [ 1; 2; 3 ]) "a set"
            Expect.equal (roundTrip (7, 8)) (7, 8) "a tuple"
            Expect.equal (roundTrip ([]: int list)) [] "an empty list — a zero length is not an over-claim"

            // The two arms the emitter used to corrupt, round-tripped now
            // that Phase 784 has fixed it. They would have gone red on
            // this tree yesterday, which is the point of keeping them.
            Expect.equal (roundTrip "probe") "probe" "a short string — the emitter's stackalloc path"
            Expect.equal (roundTrip "") "" "an empty string"
            Expect.equal (roundTrip "unicode — ✓") "unicode — ✓" "a multi-byte string"
            Expect.equal (roundTrip 12345.6789m) 12345.6789m "a decimal carrying a scale"
            Expect.equal (roundTrip (7, "seven")) (7, "seven") "a tuple mixing a number and a string"

            let guid = Guid.NewGuid()
            Expect.equal (roundTrip guid) guid "a Guid"
            Expect.equal (roundTrip instant) instant "a DateTime, kind and all"
            Expect.equal (roundTrip (DateTimeOffset instant)) (DateTimeOffset instant) "a DateTimeOffset"
            Expect.equal (roundTrip (TimeSpan.FromMinutes 90.0)) (TimeSpan.FromMinutes 90.0) "a TimeSpan"
            Expect.equal (roundTrip (Node(Leaf 3))) (Node(Leaf 3)) "a recursive union at a legal depth"
        }

        test "the str and decimal arms decode their well-formed bytes" {
            // Pinned against hand-built bytes rather than an emitter
            // round trip, for the reason in this file's header. Each
            // fixture is the encoding the format defines, written out.
            Expect.equal
                (valueOf [| MsgPack.Format.fixstr 5; 0x70uy; 0x72uy; 0x6Fuy; 0x62uy; 0x65uy |] typeof<string>)
                (box "probe")
                "a fixstr decodes its own bytes, not a zero-filled buffer of the right length"

            Expect.equal
                (valueOf [| MsgPack.Format.Str8; 6uy; 0xE2uy; 0x9Cuy; 0x93uy; 0x61uy; 0x62uy; 0x63uy |] typeof<string>)
                (box "✓abc")
                "a multi-byte UTF-8 string counts BYTES, not characters"

            Expect.equal (valueOf [| MsgPack.Format.fixstr 0 |] typeof<string>) (box "") "an empty string"

            Expect.equal
                (valueOf
                    [|
                        MsgPack.Format.fixarr 2
                        MsgPack.Format.fixstr 3
                        0x61uy
                        0x62uy
                        0x63uy
                        7uy
                    |]
                    typeof<Labelled>)
                (box { Name = "abc"; Count = 7 })
                "a record whose first field is a str"

            // 12345.6789m is `Decimal.GetBits` = [123456789; 0; 0; 262144],
            // each written by the format's own 32-bit number rule: a
            // `Uint32` where the high half is set, a `Uint8` where it is
            // not. The fourth word carries the scale, which is exactly the
            // word the emitter drops.
            let decimalBytes = [|
                MsgPack.Format.fixarr 4
                MsgPack.Format.Uint32
                0x07uy
                0x5Buy
                0xCDuy
                0x15uy
                MsgPack.Format.Uint8
                0x00uy
                MsgPack.Format.Uint8
                0x00uy
                MsgPack.Format.Uint32
                0x00uy
                0x04uy
                0x00uy
                0x00uy
            |]

            Expect.equal
                (valueOf decimalBytes typeof<decimal>)
                (box 12345.6789m)
                "a decimal with a scale reads all four words"
        }

        test "the whole-value re-encode is byte-identical, not merely equal" {
            // Equality after a round trip would still hold if the reader
            // had started accepting a DIFFERENT encoding of the same
            // value. Re-encoding what was decoded pins the bytes too.
            let probe = {
                Count = 42
                Tags = [ 1; 2 ]
                Scores = Map.ofList [ 1, 10 ]
                Moment = DateTime(2026, 9, 13, 11, 22, 33, DateTimeKind.Utc)
            }

            let original = encode (box probe)
            Expect.equal (encode (box (roundTrip probe))) original "decode ∘ encode is the identity on the wire"
        }
    ]