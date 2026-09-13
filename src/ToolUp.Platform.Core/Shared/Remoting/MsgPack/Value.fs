// SPDX-License-Identifier: MIT
// Copyright (c) Zaid Ajaj and Fable.Remoting contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.MsgPack

// ─── Phase 785 — the closed value model ──────────────────────────────
//
// The reflection reader (`Read.Reader.Read`) is an interpreter over
// `System.Type`: it walks an open, possibly recursive type graph while
// advancing a mutable cursor, so "total on every input" is not a
// well-formed statement about it — the input includes the type graph.
// An open-source F# wire decoder whose combinators were proved total
// has the opposite shape, and this file is the first half of it: a
// SMALL, CLOSED, IMMUTABLE value model with no null, produced from the
// bytes in one pass, over which per-type decoders are closed matches
// rather than type-directed reflection.
//
// Three properties are load-bearing, and each is a property of THIS
// declaration rather than of any decoder written over it:
//
//   * **Closed.** Nine cases, enumerated here, and no extension point.
//     A decoder's match over them is exhaustive by construction, which
//     is what makes "accepts the declared type or refuses by name" a
//     statement a compiler can check rather than a claim a reviewer has
//     to audit.
//   * **No null.** Every case carries a value; the absent value is
//     `Nil`, a case. So a decoder never needs a null guard and can never
//     fault on one — which the reflection path cannot say, because it
//     hands `box null` out of its `Format.Nil` arm.
//   * **Well-founded.** `Value.size` below is strictly smaller for every
//     subterm than for its container, so a recursion that only ever
//     descends into a subterm terminates. That is the measure Phase 787's
//     theorem discharges its recursion against.
//
// **There is no `Ext` case, and its absence is a finding rather than an
// omission.** The MessagePack specification has an ext family
// (`0xc7`–`0xc9`, `0xd4`–`0xd8`) and a timestamp extension; this
// transport's reader has neither — `Format.fs`'s own header records it,
// and an ext byte matches no arm of `Read` and falls through to its
// final refusal. A case the one-pass reader can never produce would be
// surface every consumer has to match on and no payload can reach.

/// Phase 785 — the width class the FORMAT BYTE declared, carried
/// through the value model so a decoder can apply Phase 786's
/// information rule without re-reading the bytes.
///
/// This is the WIRE's width, never the target's. Phase 786 established
/// why the distinction matters: `writeSByte` puts `-128y` on the wire as
/// `uint8 128`, and `writeDecimal`'s four words ride
/// `write32bitNumber`, so a negative `int32` arrives as `uint32
/// 0xFFFFFFFF`. A source no wider than its target therefore always
/// survives, sign reinterpretation included, and only a WIDER source has
/// to fit the target's range. A decoder that forgot the source width and
/// applied a signed-range rule would refuse well-formed traffic — the
/// Phase 784 corpus pins three such fixtures.
///
/// `Fixnum` is a distinct case rather than an alias for `Bits8` because
/// it is a distinct format (the value rides inside the format byte), and
/// a value model that cannot tell them apart cannot round-trip a
/// document to the bytes it came from. It is EIGHT bits wide for the
/// purposes of the information rule, which is what `bits` says.
[<RequireQualifiedAccess>]
type IntegerWidth =
    /// A positive fixint (`0x00`–`0x7f`) or a negative fixint
    /// (`0xe0`–`0xff`) — the value is the format byte.
    | Fixnum
    /// `uint8` / `int8`.
    | Bits8
    /// `uint16` / `int16`.
    | Bits16
    /// `uint32` / `int32`.
    | Bits32
    /// `uint64` / `int64`.
    | Bits64

[<RequireQualifiedAccess>]
module IntegerWidth =

    /// How many bits the source format carries. `Fixnum` answers 8: its
    /// payload is seven bits of magnitude (or five plus a sign), so it
    /// is contained in a byte, and Phase 786's `Read` arms already pass
    /// 8 for both fixint arms.
    let bits =
        function
        | IntegerWidth.Fixnum -> 8
        | IntegerWidth.Bits8 -> 8
        | IntegerWidth.Bits16 -> 16
        | IntegerWidth.Bits32 -> 32
        | IntegerWidth.Bits64 -> 64

    /// Stable lowercase label for a refusal's `Found` text.
    let label =
        function
        | IntegerWidth.Fixnum -> "fixnum"
        | IntegerWidth.Bits8 -> "8-bit"
        | IntegerWidth.Bits16 -> "16-bit"
        | IntegerWidth.Bits32 -> "32-bit"
        | IntegerWidth.Bits64 -> "64-bit"

/// Phase 785 — which IEEE format the value arrived in.
///
/// Carried for the same reason the integer width is: a `float32` on the
/// wire widened into a `float` is exact, and a `float` narrowed into a
/// `float32` is not, so a decoder that cannot see which it was cannot
/// refuse the lossy direction.
[<RequireQualifiedAccess>]
type FloatWidth =
    | Single
    | Double

/// Phase 785 — one MessagePack value, as this transport's wire can
/// carry it. See the file header for what closed / no-null /
/// well-founded buy, and for why there is no `Ext`.
///
/// `[<RequireQualifiedAccess>]` deliberately: `Map`, `Array`, `Bool` and
/// `Float` would otherwise shadow FSharp.Core names inside every file
/// that opens this namespace, and this type is opened by every decoder
/// there will ever be.
[<RequireQualifiedAccess>]
type Value =
    /// The absent value. A CASE, never `null` — see the header.
    | Nil
    | Bool of bool
    /// A signed wire integer with the width class its format byte
    /// declared.
    | Int of value: int64 * width: IntegerWidth
    /// An unsigned wire integer. Separate from `Int` rather than folded
    /// into it because a `uint64` above `Int64.MaxValue` has no `int64`
    /// representation at all, so one signed carrier could not hold every
    /// value the format admits.
    | UInt of value: uint64 * width: IntegerWidth
    | Float of value: float * width: FloatWidth
    | Str of string
    /// The `bin` family. The payload is a `byte[]` — the one mutable
    /// carrier in the model, because there is no immutable byte sequence
    /// in FSharp.Core that both hosts share. Treated as immutable by
    /// every combinator: nothing in `Decode` writes to one.
    | Bin of byte[]
    /// The `array` family, and every shape this transport writes through
    /// it: records (positionally), unions (tag first), tuples, lists,
    /// arrays, sets, `DateTime`, `DateTimeOffset` and `decimal`.
    | Arr of Value list
    /// The `map` family — `Map<_,_>` and `Dictionary<_,_>`. Entries are
    /// carried in wire order, not sorted, so a value round-trips to the
    /// bytes it came from.
    | Map of entries: (Value * Value) list

[<RequireQualifiedAccess>]
module Value =

    /// The structural size of a value: 1 for a leaf, 1 + the sizes of
    /// its children for a container.
    ///
    /// **The well-founded measure, and the reason it is shipped rather
    /// than left implicit.** Every subterm of a container has a strictly
    /// smaller size than the container, so a recursion that descends
    /// only into subterms cannot recur forever — which is the totality
    /// argument for every structurally-recursive combinator in `Decode`,
    /// and the measure Phase 787's theorem discharges against. Shipping
    /// it as executable code rather than as a comment means a test can
    /// assert the decrease over the Phase 784 corpus rather than a
    /// reader having to take it on trust.
    ///
    /// Not tail-recursive, deliberately: the reader that produces these
    /// values bounds nesting at `Format.DefaultMaxDepth` (64 by default),
    /// so the recursion's depth is bounded by construction and an
    /// accumulator would buy nothing but obscurity. A `Value` built by
    /// hand past that depth is the caller's own stack to spend.
    let rec size (value: Value) : int =
        match value with
        | Value.Nil
        | Value.Bool _
        | Value.Int _
        | Value.UInt _
        | Value.Float _
        | Value.Str _
        | Value.Bin _ -> 1
        | Value.Arr items -> items |> List.fold (fun total item -> total + size item) 1
        | Value.Map entries -> entries |> List.fold (fun total (key, entry) -> total + size key + size entry) 1

    /// What a refusal's `Found` field says about this value.
    ///
    /// Describes the SHAPE and, for the numeric cases, the value and its
    /// wire width — never a deep rendering of a container, because a
    /// refusal that quoted a megabyte of decoded payload back at an
    /// operator is a refusal nobody reads. `%A` is deliberately avoided:
    /// it is expensive on both hosts and structural on this one.
    let describe (value: Value) : string =
        match value with
        | Value.Nil -> "nil"
        | Value.Bool true -> "bool true"
        | Value.Bool false -> "bool false"
        | Value.Int(n, width) -> sprintf "%s signed integer %s" (IntegerWidth.label width) (string n)
        | Value.UInt(n, width) -> sprintf "%s unsigned integer %s" (IntegerWidth.label width) (string n)
        | Value.Float(n, FloatWidth.Single) -> sprintf "float32 %s" (string n)
        | Value.Float(n, FloatWidth.Double) -> sprintf "float64 %s" (string n)
        | Value.Str text -> sprintf "string of %d character(s)" text.Length
        | Value.Bin bytes -> sprintf "bin of %d byte(s)" bytes.Length
        | Value.Arr items -> sprintf "array of %d element(s)" (List.length items)
        | Value.Map entries -> sprintf "map of %d entry(ies)" (List.length entries)

    /// Phase 786's information rule, over this model's width classes.
    ///
    /// Lifted here verbatim in behaviour from `Read.integerFits` rather
    /// than shared with it: that one is an `inline` function generic over
    /// the numeric type the reader happens to hold, and this one works on
    /// the two concrete carriers the value model declares. The RULE is
    /// one rule and a test pins the two against each other over the
    /// Phase 784 corpus; the implementations are separate because the
    /// reflection reader must keep compiling unchanged (GP 11).
    ///
    ///   * A NEGATIVE value never decodes into an unsigned target, and
    ///     always fits a signed target whose minimum it clears.
    ///   * A source NO WIDER than the target always survives, sign
    ///     reinterpretation included — that is the format, not a
    ///     loophole.
    ///   * A source WIDER than the target must fit the target's range.
    ///
    /// `targetBits` of 0 names a target whose legal values are a DOMAIN
    /// rather than a width (a `DateOnly` day number, a `TimeOnly` tick
    /// count): it disables the reinterpretation rule so the range check
    /// applies to every source.
    let signedFits (sourceBits: int) (targetBits: int) (lo: int64) (hi: uint64) (n: int64) : bool =
        if n < 0L then lo < 0L && n >= lo
        elif sourceBits <= targetBits then true
        else uint64 n <= hi

    /// The unsigned half of `signedFits`. A wire value that arrived
    /// unsigned is never negative, so only the last two rules apply.
    let unsignedFits (sourceBits: int) (targetBits: int) (hi: uint64) (n: uint64) : bool =
        if sourceBits <= targetBits then true else n <= hi