// SPDX-License-Identifier: MIT
// Copyright (c) Zaid Ajaj and Fable.Remoting contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Remoting.MsgPack.Format

// ─── Phase 786 — what the reader REFUSES, and why it is a refusal ─────
//
// The format bytes below say what a well-formed document may look like.
// They say nothing about how much of one a reader should be willing to
// believe, and a decoder that believes all of it has three ways to be
// hurt by a payload it did not write. Each is bounded, and each bound is
// a REFUSAL — a `DecodeError` naming what was expected and what was
// found — never a best-effort narrowing, a truncation, or a clamp:
//
//   1. **Width.** An integer is decoded at the width the TARGET TYPE
//      declares. A payload whose value does not fit that width refuses;
//      it is not cast down. Before this phase an `Int64` payload aimed at
//      an `int` field became `int32 n` and the decode SUCCEEDED, so the
//      typed contract said one thing and the wire delivered another with
//      nothing in between to notice. No default — the target type is the
//      bound.
//
//   2. **Length.** Every length-prefixed read (`str`, `bin`, `array`,
//      `map`, and the `set` and collection shapes carried inside an
//      array) checks its prefix against the bytes ACTUALLY REMAINING,
//      using the minimum encoded size of one element (1 byte for an
//      array or a string or a bin, 2 for a map entry), BEFORE anything is
//      allocated from it. A five-byte `Array32` header can therefore no
//      longer ask for a gigabyte. No default — the payload's own length
//      is the bound. Note a length above `Int32.MaxValue` arrives as a
//      NEGATIVE `int`, which is how a 2 GiB claim presents in practice;
//      that is refused by the same check.
//
//   3. **Depth.** Containers nest no deeper than `DefaultMaxDepth`,
//      overridable per reader (`Reader(data, maxDepth)`). Unbounded
//      nesting is not an exception on .NET — it is a stack overflow,
//      which is process death and cannot be caught, so this bound is the
//      only thing that makes a hostile payload survivable at all.
//
// Two things this reader does NOT have, stated because their absence is
// easy to misread as a gap in the bounds above. There is no **ext**
// family here at all (no `0xc7`–`0xc9`, no `0xd4`–`0xd8`): an ext byte
// matches no arm and falls through to the reader's final refusal, so
// there is no ext length to guard. And there is no `timestamp`
// extension; `DateTime` and friends ride the integer and array arms.
//
// ─── Phase 802 — what the WRITER emits, so that rule 1 can refuse ─────
//
// Rule 1 can only refuse a narrowing it can SEE. The reader admits a
// source no wider than its target even when the value's top bit is set
// (`uint32 0x80000000` at an `int32` target reads as `-2147483648`),
// because writers before this phase put signed values on the wire in
// unsigned formats of the same width: `writeDecimal`'s four int32 words
// as `uint32`, `writeSByte`'s negatives as `uint8`. And they compacted:
// `writeInt64 2147483648L` left as `Uint32 80 00 00 00` — byte for byte
// a well-formed `int32 -2147483648`, so `wrong-width-int64-into-int32`
// was unrefusable at any reader.
//
// Both writers (the .NET one and the one Fable compiles) now pick every
// integer's format by one rule, and the rule is about the TOP BIT:
//
//   * A NON-NEGATIVE value rides the narrowest unsigned format whose top
//     bit stays clear — fixposnum below 128, then `Uint16` up to 32767,
//     `Uint32` up to `Int32.MaxValue`, `Uint64` above. A positive value
//     above a signed width's maximum goes one width UP, never into the
//     unsigned form of the same width, so no integer leaves as `Uint8`.
//   * A NEGATIVE value rides the narrowest signed format that holds it —
//     fixnegnum down to -32, then `Int8`, `Int32`, `Int64` — with one
//     deliberate gap: no `Int16`. The Fable reader before this phase did
//     not sign-extend `Int16` (`d1 80 00` came back as 32768), so a
//     browser client still running it would read a negative int16 as a
//     positive number. Every reader generation reads `Int32` correctly,
//     so values in -32768..-129 pay two bytes for that. The reader is
//     fixed in this phase; the gap stays because old clients do not
//     update when the server does.
//   * The one emission with its top bit set is a `uint64` source above
//     `Int64.MaxValue`, which has no wider format to move up into.
//
// So no integer either writer emits reads as a different value at any
// narrower width the reader accepts: a value too wide for its target
// always arrives in a format WIDER than the target, which rule 1
// measures and refuses by name. The decimal words and the sbyte arm
// follow the same rule — each word is an int32 and travels as one.
//
// **Compatibility, stated exactly.** This is a change to the BYTES and
// not to the reader's RULES, and it is wire-compatible in both
// directions: every form the writer now emits is one both reader
// generations decode to the same value — measured, not assumed: the
// Fable parity pack's first run over the re-pinned corpus caught the
// `Int16` misread above, which is why that format is not emitted — and
// the reader still admits the same-width forms older writers emit, so a
// pre-802 peer on either end decodes identically. What is NOT preserved is byte
// identity: the corpus fixtures that carried such values were re-pinned
// in the same commit, and anything outside this repository that pins
// bytes sees the same re-pin. Removing the reader's same-width admission
// WOULD be a wire break — it would refuse a pre-802 writer's negative
// decimals — and it is deliberately not done here.

/// Phase 786 — how deeply containers may nest before the reader refuses.
/// A `Reader` takes an override; this is what it uses when given none.
/// Chosen against real traffic rather than against the format: the
/// deepest shape this transport actually carries is a handful of records
/// inside a list inside a response envelope, so 64 is roughly an order of
/// magnitude of headroom over anything a contract produces, while staying
/// far enough below the stack's limit that the refusal always wins the
/// race against the overflow.
[<Literal>]
let DefaultMaxDepth = 64

[<Literal>]
let Nil = 0xc0uy

[<Literal>]
let False = 0xc2uy

[<Literal>]
let True = 0xc3uy

let inline fixposnum value = byte value
let inline fixnegnum value = byte value ||| 0b11100000uy

[<Literal>]
let Uint8 = 0xccuy

[<Literal>]
let Uint16 = 0xcduy

[<Literal>]
let Uint32 = 0xceuy

[<Literal>]
let Uint64 = 0xcfuy

[<Literal>]
let Int8 = 0xd0uy

[<Literal>]
let Int16 = 0xd1uy

[<Literal>]
let Int32 = 0xd2uy

[<Literal>]
let Int64 = 0xd3uy

let inline fixstr len = 160uy + byte len

[<Literal>]
let Str8 = 0xd9uy

[<Literal>]
let Str16 = 0xdauy

[<Literal>]
let Str32 = 0xdbuy

[<Literal>]
let Float32 = 0xcauy

[<Literal>]
let Float64 = 0xcbuy

let inline fixarr len = 144uy + byte len

[<Literal>]
let Array16 = 0xdcuy

[<Literal>]
let Array32 = 0xdduy

[<Literal>]
let Bin8 = 0xc4uy

[<Literal>]
let Bin16 = 0xc5uy

[<Literal>]
let Bin32 = 0xc6uy

let inline fixmap len = 128uy + byte len

[<Literal>]
let Map16 = 0xdeuy

[<Literal>]
let Map32 = 0xdfuy