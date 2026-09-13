// SPDX-License-Identifier: MIT
// Copyright (c) Zaid Ajaj and Fable.Remoting contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Remoting.MsgPack.Read

open System
open System.Text
open System.Collections
open System.Collections.Concurrent
open System.Collections.Generic
open FSharp.Reflection
// Phase 783 — the decode-refusal vocabulary. Every `failwithf` that used
// to describe a decode failure in prose now refuses with a `DecodeError`
// naming Expected / Found / Path, carried out through `DecodeException`
// and converted back to a `Result` at `Reader.TryRead`.
open ToolUp.Remoting
open System.Reflection
open Microsoft.FSharp.NativeInterop
#if !FABLE_COMPILER && NETCOREAPP2_1_OR_GREATER
open System.Buffers.Binary
#endif

#nowarn "9"
#nowarn "51"

/// Phase 786 — can this wire value reach the target type without losing
/// what the wire actually carried?
///
/// `sourceBits` is the width of the WIRE FORMAT the value arrived in and
/// `targetBits` the width of the type it is going to; `lo`/`hi` are the
/// target's signed minimum and its maximum as an unsigned magnitude.
///
/// The source's width is passed rather than derived. Deriving it from the
/// value's own static type is exact and one character long — `sizeof<'a>`
/// inside this `inline` function resolves per call site — and Fable
/// refuses it outright (`Operators.SizeOf is not supported`). The width
/// is known at every call site in `Read`, because the format byte is what
/// chose the read; passing it is the version of this that compiles on
/// both hosts, which is the only version worth having for a reader whose
/// one production caller runs under Fable.
///
/// Three rules, and the middle one is the one the wire corpus forced.
///
///   * A NEGATIVE value never decodes into an unsigned target. No format
///     this transport emits puts a negative on the wire for an unsigned
///     field, so a sign flip there is a mutation, not a convention. Into
///     a SIGNED target a negative source always fits, because a narrower
///     signed range is contained in a wider one.
///   * A source NO WIDER than the target always survives — including the
///     signed/unsigned reinterpretation this encoder depends on. That is
///     not a loophole, it is the format: `writeSByte` puts `-128y` on the
///     wire as `uint8 128`, and `writeDecimal`'s four words go through
///     `write32bitNumber`, so a negative int32 arrives as `uint32
///     0xFFFFFFFF`. The target type is what recovers the sign, and no bit
///     is lost either way. Refusing these would refuse well-formed
///     traffic — the Phase 784 corpus pins three such fixtures.
///   * A source WIDER than the target is the genuine narrowing, and it is
///     refused unless the value fits the target's own range. This is the
///     arm that catches an `int64` payload aimed at an `int` field.
///
/// A target whose legal values are a DOMAIN rather than a width — a
/// `DateOnly` day number, a `TimeOnly` tick count — passes `targetBits`
/// of 0, which disables the reinterpretation rule and leaves the range
/// check applying to every source.
let inline private integerFits (sourceBits: int) (targetBits: int) (lo: int64) (hi: uint64) n =
    if n < LanguagePrimitives.GenericZero then
        lo < 0L && int64 n >= lo
    elif sourceBits <= targetBits then
        true
    else
        uint64 n <= hi

/// Phase 786 — the refusal a failed width check raises. `Expected` names
/// the target type, so the rendered sentence reads
/// `expected Int32, got out-of-range integer 4294967296`.
let inline private refuseWidth (typ: Type) n : obj =
    DecodeError.failWith typ.Name (sprintf "out-of-range integer %A" n)

/// Phase 786 — how many elements a collection is allowed to reserve room
/// for on the strength of a length prefix alone, before it has to earn
/// the rest by actually decoding elements.
///
/// The length guard (`Reader.RequireAvailable`) already bounds a prefix
/// by the bytes remaining, so nothing here can reach a gigabyte. This is
/// the second half of the same idea: a prefix the guard ADMITS still
/// buys only a small allocation, and the remainder is paid for element by
/// element. The ceiling is deliberately well above the size of anything
/// this transport carries in practice, so the overwhelmingly common case
/// — a record, a short list, a small map — allocates exactly once, at
/// exactly the right size, as it did before this phase.
[<Literal>]
let private InitialCollectionCapacity = 1024

let interpretStringAs (typ: Type) (str: string) =
#if FABLE_COMPILER
    box str
#else
    if typ = typeof<string> then
        box str
    elif typ = typeof<char> then
        box str.[0]
    else
        // todo cacheable
        // String enum
        let case = FSharpType.GetUnionCases typ |> Array.find (fun y -> y.Name = str)
        FSharpValue.MakeUnion(case, [||])
#endif

// Phase 786 — every arm decodes at the width its TARGET TYPE declares
// and refuses a wire value that does not fit, rather than casting it
// down. The two `#if` branches are kept arm-for-arm identical in this
// respect: under Fable the same `integerFits` test runs over the JS
// number (or BigInt) the arm would otherwise have narrowed, so a client
// and a server disagree about no payload. See `Format.fs`'s header for
// why these are refusals rather than clamps.
let inline interpretIntegerAsFrom (typ: Type) (sourceBits: int) n =
#if !FABLE_COMPILER
    if typ = typeof<Int32> then
        if integerFits sourceBits 32 -2147483648L 2147483647UL n then
            int32 n |> box
        else
            refuseWidth typ n
    elif typ = typeof<Int64> then
        if integerFits sourceBits 64 Int64.MinValue 9223372036854775807UL n then
            int64 n |> box
        else
            refuseWidth typ n
    elif typ = typeof<Int16> then
        if integerFits sourceBits 16 -32768L 32767UL n then
            int16 n |> box
        else
            refuseWidth typ n
    elif typ = typeof<UInt32> then
        if integerFits sourceBits 32 0L 4294967295UL n then
            uint32 n |> box
        else
            refuseWidth typ n
    elif typ = typeof<UInt64> then
        if integerFits sourceBits 64 0L 18446744073709551615UL n then
            uint64 n |> box
        else
            refuseWidth typ n
    elif typ = typeof<UInt16> then
        if integerFits sourceBits 16 0L 65535UL n then
            uint16 n |> box
        else
            refuseWidth typ n
    elif typ = typeof<TimeSpan> then
        if integerFits sourceBits 64 Int64.MinValue 9223372036854775807UL n then
            TimeSpan(int64 n) |> box
        else
            refuseWidth typ n
#if NET6_0_OR_GREATER
    elif typ = typeof<DateOnly> then
        // The bound here is `DateOnly`'s OWN domain (day numbers up to
        // `DateOnly.MaxValue.DayNumber`), not the int32 range: outside
        // it `FromDayNumber` raises an `ArgumentOutOfRangeException`,
        // which would leave `TryRead` as something other than a refusal.
        if integerFits sourceBits 0 0L 3652058UL n then
            DateOnly.FromDayNumber(int32 n) |> box
        else
            refuseWidth typ n
    elif typ = typeof<TimeOnly> then
        // Likewise `TimeOnly`'s own domain, in ticks, for the same reason.
        if integerFits sourceBits 0 0L 863999999999UL n then
            TimeOnly(int64 n) |> box
        else
            refuseWidth typ n
#endif
    elif typ = typeof<byte> then
        if integerFits sourceBits 8 0L 255UL n then
            byte n |> box
        else
            refuseWidth typ n
    elif typ = typeof<sbyte> then
        if integerFits sourceBits 8 -128L 127UL n then
            sbyte n |> box
        else
            refuseWidth typ n
    elif typ.IsEnum then
        // Bounded at the width `Enum.ToObject` takes its value at, which
        // is 64 bits — so every wire integer reaches it, a `uint64` above
        // `Int64.MaxValue` arriving as its signed reinterpretation like
        // any other same-width value. The enum's own UNDERLYING width is
        // deliberately not checked: a value outside an enum's declared
        // cases is legal in .NET (flags combinations are the common
        // case), so there is no narrower bound to defend.
        if integerFits sourceBits 64 Int64.MinValue 9223372036854775807UL n then
            Enum.ToObject(typ, int64 n)
        else
            refuseWidth typ n
    else
        DecodeError.failWith typ.Name (sprintf "integer %A" n)
#else
    if Object.ReferenceEquals(typ, typeof<Int32>) then
        if integerFits sourceBits 32 -2147483648L 2147483647UL n then
            int32 n |> box
        else
            refuseWidth typ n
    else
        // .FullName in Fable is a function call with multiple operations, so let's compute the value just once
        let typeName = typ.FullName

        if typeName = "System.Int64" then
            if integerFits sourceBits 64 Int64.MinValue 9223372036854775807UL n then
                int64 n |> box
            else
                refuseWidth typ n
        elif Object.ReferenceEquals(typ, typeof<Int16>) then
            if integerFits sourceBits 16 -32768L 32767UL n then
                int16 n |> box
            else
                refuseWidth typ n
        elif Object.ReferenceEquals(typ, typeof<UInt32>) then
            if integerFits sourceBits 32 0L 4294967295UL n then
                uint32 n |> box
            else
                refuseWidth typ n
        elif typeName = "System.UInt64" then
            if integerFits sourceBits 64 0L 18446744073709551615UL n then
                uint64 n |> box
            else
                refuseWidth typ n
        elif Object.ReferenceEquals(typ, typeof<UInt16>) then
            if integerFits sourceBits 16 0L 65535UL n then
                uint16 n |> box
            else
                refuseWidth typ n
        elif typeName = "System.TimeSpan" then
            if integerFits sourceBits 64 Int64.MinValue 9223372036854775807UL n then
                TimeSpan(int64 n) |> box
            else
                refuseWidth typ n
#if NET6_0_OR_GREATER
#endif
        elif typeName = "Microsoft.FSharp.Core.int16`1" then
            if integerFits sourceBits 16 -32768L 32767UL n then
                int16 n |> box
            else
                refuseWidth typ n
        elif typeName = "Microsoft.FSharp.Core.int32`1" then
            if integerFits sourceBits 32 -2147483648L 2147483647UL n then
                int32 n |> box
            else
                refuseWidth typ n
        elif typeName = "Microsoft.FSharp.Core.int64`1" then
            if integerFits sourceBits 64 Int64.MinValue 9223372036854775807UL n then
                int64 n |> box
            else
                refuseWidth typ n
        elif Object.ReferenceEquals(typ, typeof<byte>) then
            if integerFits sourceBits 8 0L 255UL n then
                byte n |> box
            else
                refuseWidth typ n
        elif Object.ReferenceEquals(typ, typeof<sbyte>) then
            if integerFits sourceBits 8 -128L 127UL n then
                sbyte n |> box
            else
                refuseWidth typ n
        elif typ.IsEnum then
            if integerFits sourceBits 64 Int64.MinValue 9223372036854775807UL n then
                float n |> box
            else
                refuseWidth typ n
        else
            DecodeError.failWith typ.Name (sprintf "integer %A" n)
#endif

/// The pre-786 entry, kept at its original shape so existing callers
/// compile and link unchanged (GP 11).
///
/// A caller who arrives here has not said what width the value came from,
/// and there is no way to recover it, so this assumes the widest source
/// the format has. That is the SAFE assumption rather than the compatible
/// one: it refuses a narrowing this function used to perform silently,
/// and it refuses a same-width reinterpretation it cannot know is one.
/// `Reader` does not come through here — every arm of `Read` knows its
/// format's width and calls `interpretIntegerAsFrom` with it.
let inline interpretIntegerAs (typ: Type) n = interpretIntegerAsFrom typ 64 n

let inline interpretFloatAs (typ: Type) n =
#if FABLE_COMPILER
    box n
#else
    if typ = typeof<float32> then float32 n |> box
    elif typ = typeof<float> then float n |> box
    else DecodeError.failWith typ.Name (sprintf "float %A" n)
#endif

#if !FABLE_COMPILER
type DictionaryDeserializer<'k, 'v when 'k: equality and 'k: comparison>() =
    static let keyType = typeof<'k>
    static let valueType = typeof<'v>

    static member Deserialize(len: int, isDictionary, read: Type -> obj) =
        // Phase 786 — seed at a bounded capacity and let the collection
        // grow as entries are actually decoded, rather than reserving
        // `len` slots on the wire's say-so.
        if isDictionary then
            let dict = Dictionary<'k, 'v>(min len InitialCollectionCapacity)

            for _ in 0 .. len - 1 do
                dict.Add(read keyType :?> 'k, read valueType :?> 'v)

            box dict
        else
            let pairs = ResizeArray<'k * 'v>(min len InitialCollectionCapacity)

            for _ in 0 .. len - 1 do
                pairs.Add(read keyType :?> 'k, read valueType :?> 'v)

            pairs |> Map.ofSeq |> box

type ListDeserializer<'a>() =
    static let argType = typeof<'a>

    static member Deserialize(len: int, read: Type -> obj) =
        List.init len (fun _ -> read argType :?> 'a) |> box

type SetDeserializer<'a when 'a: comparison>() =
    static let argType = typeof<'a>

    static member Deserialize(len: int, read: Type -> obj) =
        let mutable set = Set.empty

        for _ in 0 .. len - 1 do
            set <- set.Add(read argType :?> 'a)

        set |> box
#endif

/// Reads MessagePack off `data`, refusing anything that nests deeper than
/// `maxDepth` (Phase 786 — see `Format.fs`'s header for the three bounds
/// and why each is a refusal). The single-argument constructor applies
/// `Format.DefaultMaxDepth`, so every existing call site keeps its
/// behaviour for every payload that was already well-formed (GP 11).
type Reader(data: byte[], maxDepth: int) =
    let mutable pos = 0

    /// Phase 786 — how many containers deep the reader currently is.
    /// An instance counter rather than a threaded parameter: the decode
    /// recursion runs through `Type -> obj` delegate caches and three
    /// public `*Deserializer` types whose `Deserialize` takes
    /// `read: Type -> obj`, so a parameter would retype five public
    /// generic types across two tiers — the interior rewrite Phase 785
    /// owns. A `Reader` is single-threaded and reads one value, so the
    /// counter is sound; on the refusal path the reader is finished and
    /// the count is never unwound (`TryRead` documents the same about
    /// `pos`).
    let mutable depth = 0

#if !FABLE_COMPILER
    static let arrayReaderCache = ConcurrentDictionary<Type, (int * Reader) -> obj>()
    static let mapReaderCache = ConcurrentDictionary<Type, (int * Reader) -> obj>()
    static let setReaderCache = ConcurrentDictionary<Type, (int * Reader) -> obj>()
    static let unionConstructorCache = ConcurrentDictionary<UnionCaseInfo, obj[] -> obj>()
    static let unionCaseFieldCache = ConcurrentDictionary<Type * int, UnionCaseInfo * Type[]>()
#else
    let numberBuffer = Array.zeroCreate 8

    let readNumber len bytesInterpretation =
        pos <- pos + len

        if BitConverter.IsLittleEndian then
            for i in 0 .. len - 1 do
                numberBuffer.[i] <- data.[pos - 1 - i]

            bytesInterpretation (numberBuffer, 0)
        else
            bytesInterpretation (data, pos - len)
#endif

    /// Phase 786 — the pre-786 shape, at the default nesting bound. An
    /// explicit secondary constructor rather than an optional argument:
    /// an optional parameter folds both into one widened constructor, so
    /// the existing one-argument token would disappear from the public
    /// surface and read as a removal.
    new(data: byte[]) = Reader(data, Format.DefaultMaxDepth)

    member _.ReadByte() =
        pos <- pos + 1
        data.[pos - 1]

    member _.ReadRawBin len =
        pos <- pos + len
#if NETCOREAPP2_1_OR_GREATER && !FABLE_COMPILER
        ReadOnlySpan(data, pos - len, len)
#else
        data.[pos - len .. pos - 1]
#endif

    member _.ReadString len =
        pos <- pos + len
        Encoding.UTF8.GetString(data, pos - len, len)

    member x.ReadUInt8() = x.ReadByte()

    member x.ReadInt8() = x.ReadByte() |> sbyte

    member x.ReadUInt16() = x.ReadInt16() |> uint16

    member _.ReadInt16() =
        pos <- pos + 2
#if !FABLE_COMPILER && NETCOREAPP2_1_OR_GREATER
        BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(pos - 2, 2))
#else
        (int16 data.[pos - 2] <<< 8) ||| (int16 data.[pos - 1])
#endif

    member x.ReadUInt32() = x.ReadInt32() |> uint32

    member _.ReadInt32() =
        pos <- pos + 4
#if !FABLE_COMPILER && NETCOREAPP2_1_OR_GREATER
        BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos - 4, 4))
#else
        (int data.[pos - 4] <<< 24)
        ||| (int data.[pos - 3] <<< 16)
        ||| (int data.[pos - 2] <<< 8)
        ||| (int data.[pos - 1])
#endif

    member x.ReadUInt64() = x.ReadInt64() |> uint64

    member _.ReadInt64() =
#if !FABLE_COMPILER
        pos <- pos + 8
#if NETCOREAPP2_1_OR_GREATER
        BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(pos - 8, 8))
#else
        (int64 data.[pos - 8] <<< 56)
        ||| (int64 data.[pos - 7] <<< 48)
        ||| (int64 data.[pos - 6] <<< 40)
        ||| (int64 data.[pos - 5] <<< 32)
        ||| (int64 data.[pos - 4] <<< 24)
        ||| (int64 data.[pos - 3] <<< 16)
        ||| (int64 data.[pos - 2] <<< 8)
        ||| (int64 data.[pos - 1])
#endif
#else
        readNumber 8 BitConverter.ToInt64
#endif

    member x.ReadFloat32() =
#if !FABLE_COMPILER
        let mutable b = x.ReadInt32()
        NativePtr.toNativeInt &&b |> NativePtr.ofNativeInt |> NativePtr.read<float32>
#else
        readNumber 4 BitConverter.ToSingle

    // This is faster but does not yet work because of precision errors
    //let sign = if (b >>> 31) = 0 then 1f else -1f
    //let mutable e = (b >>> 23) &&& 0xff
    //let m = b &&& 0x7fffff

    //let m =
    //    if e = 0 then
    //        if m = 0 then
    //            0f
    //        else
    //            e <- e - 126
    //            1f / float32 0x7fffff
    //    else
    //        e <- e - 127
    //        1f + float32 m / (float32 0x800000)

    //sign * m * float32 (Math.Pow (2., float e))
#endif

    member x.ReadFloat64() =
#if !FABLE_COMPILER
        let mutable b = x.ReadInt64()
        NativePtr.toNativeInt &&b |> NativePtr.ofNativeInt |> NativePtr.read<float>
#else
        readNumber 8 BitConverter.ToDouble
#endif

    member x.ReadMap(len: int, t: Type) =
#if !FABLE_COMPILER
        mapReaderCache.GetOrAdd
            (t,
             Func<_, _>(fun (t: Type) ->
                 let args = t.GetGenericArguments()

                 if args.Length <> 2 then
                     DecodeError.failWith t.Name "a map"

                 let mapDeserializer = typedefof<DictionaryDeserializer<_, _>>.MakeGenericType args
                 let isDictionary = t.GetGenericTypeDefinition() = typedefof<Dictionary<_, _>>

                 let d =
                     Delegate.CreateDelegate(
                         typeof<Func<int, bool, (Type -> obj), obj>>,
                         mapDeserializer.GetMethod "Deserialize"
                     )
                     :?> Func<int, bool, (Type -> obj), obj>

                 fun (len, x: Reader) -> d.Invoke(len, isDictionary, x.Read)))
            (len, x)
#else
        let args = t.GetGenericArguments()

        if args.Length <> 2 then
            DecodeError.failWith t.Name "a map"

        // Phase 786 — seeded at a bounded capacity and grown as entries
        // are decoded, rather than reserving `len` slots up front.
        let pairs = ResizeArray(min len InitialCollectionCapacity)

        for _ in 0 .. len - 1 do
            pairs.Add(x.Read args.[0] |> box :?> IStructuralComparable, x.Read args.[1])

        if t.GetGenericTypeDefinition() = typedefof<Dictionary<_, _>> then
            let dict = Dictionary<_, _>(min len InitialCollectionCapacity)
            pairs |> Seq.iter dict.Add
            box dict
        else
            Map.ofSeq pairs |> box
#endif

    member x.ReadSet(len: int, t: Type) =
#if !FABLE_COMPILER
        setReaderCache.GetOrAdd
            (t,
             Func<_, _>(fun (t: Type) ->
                 let args = t.GetGenericArguments()

                 if args.Length <> 1 then
                     DecodeError.failWith t.Name "a set"

                 let setDeserializer = typedefof<SetDeserializer<_>>.MakeGenericType args

                 let d =
                     Delegate.CreateDelegate(
                         typeof<Func<int, (Type -> obj), obj>>,
                         setDeserializer.GetMethod "Deserialize"
                     )
                     :?> Func<int, (Type -> obj), obj>

                 fun (len, x: Reader) -> d.Invoke(len, x.Read)))
            (len, x)
#else
        let args = t.GetGenericArguments()

        if args.Length <> 1 then
            DecodeError.failWith t.Name "a set"

        let mutable set = Set.empty

        for _ in 0 .. len - 1 do
            set <- set.Add(x.Read args.[0] |> box :?> IStructuralComparable)

        box set
#endif

    /// Phase 786 — the array is seeded at `min len
    /// InitialCollectionCapacity` and doubled, capped at `len`, as
    /// elements are actually decoded. A `len` at or below the ceiling
    /// therefore allocates exactly once at exactly the right size, as
    /// before; a larger one pays for its slots by producing elements.
    /// Because growth is capped at `len`, the array's length is exactly
    /// `len` once the loop completes and no trim is needed.
    member x.ReadRawArray(len: int, elementType: Type) =
#if !FABLE_COMPILER
        let mutable arr =
            Array.CreateInstance(elementType, min len InitialCollectionCapacity)

        for i in 0 .. len - 1 do
            if i >= arr.Length then
                let grown = Array.CreateInstance(elementType, min len (max 4 (arr.Length * 2)))
                Array.Copy(arr, grown, arr.Length)
                arr <- grown

            arr.SetValue(x.Read elementType, i)

        arr
#else
        let arr = ResizeArray(min len InitialCollectionCapacity)

        for _ in 0 .. len - 1 do
            arr.Add(x.Read elementType)

        arr.ToArray()
#endif

    member x.ReadArray(len, t) =
#if !FABLE_COMPILER
        match arrayReaderCache.TryGetValue t with
        | true, reader -> reader (len, x)
        | _ ->
#endif

        if FSharpType.IsRecord t then
#if !FABLE_COMPILER
                let fieldTypes = FSharpType.GetRecordFields t |> Array.map _.PropertyType

                let ctor = FSharpValue.PreComputeRecordConstructor(t, true)

                arrayReaderCache.GetOrAdd (t, fun (_, x: Reader) -> ctor (fieldTypes |> Array.map x.Read)) (len, x)
#else
            let props = FSharpType.GetRecordFields t
            FSharpValue.MakeRecord(t, props |> Array.map (fun prop -> x.Read prop.PropertyType))
#endif
        elif FSharpType.IsUnion t then
#if !FABLE_COMPILER
                if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<_ list> then
                    let argType = t.GetGenericArguments() |> Array.head
                    let listDeserializer = typedefof<ListDeserializer<_>>.MakeGenericType argType

                    let d =
                        Delegate.CreateDelegate(
                            typeof<Func<int, (Type -> obj), obj>>,
                            listDeserializer.GetMethod "Deserialize"
                        )
                        :?> Func<int, (Type -> obj), obj>

                    arrayReaderCache.GetOrAdd (t, fun (len, (x: Reader)) -> d.Invoke(len, x.Read)) (len, x)
                else
                    // the length parameter is ignored because the shape of the union tells us how many elements there are too
                    arrayReaderCache.GetOrAdd
                        (t,
                         fun (_, x: Reader) ->
                             let tag = x.Read typeof<int> :?> int

                             let case, fieldTypes =
                                 unionCaseFieldCache.GetOrAdd(
                                     (t, tag),
                                     fun (t, tag) ->
                                         let case =
                                             FSharpType.GetUnionCases(t, true) |> Array.find (fun x -> x.Tag = tag)

                                         let fields = case.GetFields()
                                         case, fields |> Array.map _.PropertyType
                                 )

                             let fields =
                                 // single case field is serialized directly
                                 if fieldTypes.Length = 1 then
                                     [| x.Read fieldTypes.[0] |]
                                 elif fieldTypes.Length = 0 then
                                     [||]
                                 // multiple fields are serialized in an array
                                 else
                                     // don't care about this byte, it's going to be a fixarr of length fieldTypes.Length
                                     x.ReadByte() |> ignore
                                     fieldTypes |> Array.map x.Read

                             unionConstructorCache.GetOrAdd
                                 (case, Func<_, _>(fun case -> FSharpValue.PreComputeUnionConstructor(case, true)))
                                 fields)
                        (len, x)
#else
            let tag = x.Read typeof<int> :?> int
            let case = FSharpType.GetUnionCases t |> Array.find (fun x -> x.Tag = tag)
            let fieldTypes = case.GetFields() |> Array.map _.PropertyType

            let fields =
                // single case field is serialized directly
                if fieldTypes.Length = 1 then
                    [| x.Read fieldTypes.[0] |]
                elif fieldTypes.Length = 0 then
                    [||]
                // multiple fields are serialized in an array
                else
                    // don't care about this byte, it's going to be a fixarr of length fieldTypes.Length
                    x.ReadByte() |> ignore
                    fieldTypes |> Array.map x.Read

            FSharpValue.MakeUnion(case, fields)
#endif

#if FABLE_COMPILER // Fable does not recognize Option as a union
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Option<_>> then
            let tag = x.ReadByte()

            // none case
            if tag = 0uy then
                box null
            else
                x.Read(t.GetGenericArguments() |> Array.head) |> Some |> box
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<_ list> then
            let elementType = t.GetGenericArguments() |> Array.head
            [ for _ in 0 .. len - 1 -> x.Read elementType ] |> box
#endif
        elif t.IsArray then
            x.ReadRawArray(len, t.GetElementType()) |> box
        elif FSharpType.IsTuple t then
#if !FABLE_COMPILER
                let elementTypes = FSharpType.GetTupleElements t
                let tupleCtor = FSharpValue.PreComputeTupleConstructor t

                arrayReaderCache.GetOrAdd
                    (t, fun (_, (x: Reader)) -> elementTypes |> Array.map x.Read |> tupleCtor)
                    (len, x)
#else
            FSharpValue.MakeTuple(FSharpType.GetTupleElements t |> Array.map x.Read, t)
#endif
        elif t = typeof<DateTime> then
            let dateTimeTicks = x.Read typeof<int64> :?> int64
            let kindAsInt = x.Read typeof<int64> :?> int64

            let kind =
                match kindAsInt with
                | 1L -> DateTimeKind.Utc
                | 2L -> DateTimeKind.Local
                | _ -> DateTimeKind.Unspecified

            DateTime(ticks = dateTimeTicks, kind = kind) |> box
        elif t = typeof<DateTimeOffset> then
            let dateTimeTicks = x.Read typeof<int64> :?> int64
            let timeSpanMinutes = x.Read typeof<int16> :?> int16

            DateTimeOffset(dateTimeTicks, TimeSpan.FromMinutes(float timeSpanMinutes))
            |> box

        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Set<_>> then
            x.ReadSet(len, t)
#if !FABLE_COMPILER
            elif t = typeof<System.Data.DataTable> then
                match x.ReadRawArray(2, typeof<string>) :?> string array with
                | [| schema; data |] ->
                    let t = new System.Data.DataTable()
                    t.ReadXmlSchema(new System.IO.StringReader(schema))
                    t.ReadXml(new System.IO.StringReader(data)) |> ignore
                    box t
                | otherwise -> DecodeError.failAt [ sprintf "byte %d" pos ] t.Name "an array"
            elif t = typeof<System.Data.DataSet> then
                match x.ReadRawArray(2, typeof<string>) :?> string array with
                | [| schema; data |] ->
                    let t = new System.Data.DataSet()
                    t.ReadXmlSchema(new System.IO.StringReader(schema))
                    t.ReadXml(new System.IO.StringReader(data)) |> ignore
                    box t
                | otherwise -> DecodeError.failAt [ sprintf "byte %d" pos ] t.Name "an array"
#endif
        elif t = typeof<decimal> || t.FullName = "Microsoft.FSharp.Core.decimal`1" then
#if !FABLE_COMPILER
                arrayReaderCache.GetOrAdd
                    (t, fun (_, (x: Reader)) -> x.ReadRawArray(4, typeof<int>) :?> int[] |> Decimal |> box)
                    (len, x)
#else
            x.ReadRawArray(4, typeof<int>) |> box :?> int[] |> Decimal |> box
#endif
        else
            DecodeError.failAt [ sprintf "byte %d" pos ] t.Name "an array"

    member x.ReadBin(len, t) =
        if t = typeof<Guid> then
            Guid(x.ReadRawBin len) |> box
        elif t = typeof<byte[]> then
#if NETCOREAPP2_1_OR_GREATER && !FABLE_COMPILER
            (x.ReadRawBin len).ToArray() |> box
#else
            box (x.ReadRawBin len)
#endif
        elif t = typeof<bigint> then
            bigint (x.ReadRawBin len) |> box
        else
            DecodeError.failAt [ sprintf "byte %d" pos ] t.Name "bin"

    /// Phase 786 — refuse a length prefix that claims more than the bytes
    /// remaining can possibly hold, BEFORE anything is allocated from it.
    ///
    /// `minBytesPerElement` is the smallest number of bytes one element
    /// of this shape can occupy on the wire: 1 for an array element, a
    /// string byte or a bin byte (every MessagePack value is at least one
    /// byte), 2 for a map entry (a key and a value). So the check is
    /// exact in the sense that matters — it never refuses a payload that
    /// could be well-formed, and it admits nothing that could not be.
    ///
    /// The `len < 0` arm is not defensive padding: an `Array32` or
    /// `Map32` length above `Int32.MaxValue` is converted with `int` at
    /// the call site and arrives NEGATIVE, which is how a claim of two
    /// gibibytes of elements actually presents itself.
    member private _.RequireAvailable(len: int, minBytesPerElement: int, what: string) =
        let remaining = data.Length - pos
        let affordable = remaining / minBytesPerElement

        if len < 0 || len > affordable then
            DecodeError.failAt
                [ sprintf "byte %d" pos ]
                (sprintf "%s of at most %d element(s)" what affordable)
                (sprintf "%s of %d element(s), with %d byte(s) remaining" what len remaining)

    /// Phase 786 — descend into a container, refusing past the bound.
    member private _.EnterContainer() =
        depth <- depth + 1

        if depth > maxDepth then
            DecodeError.failAt
                [ sprintf "byte %d" pos ]
                (sprintf "nesting at most %d container(s) deep" maxDepth)
                (sprintf "nesting %d container(s) deep" depth)

    member private _.ExitContainer() = depth <- depth - 1

    /// Phase 786 — the guarded forms the format arms dispatch through:
    /// length checked against the bytes present, then (for the two
    /// recursive shapes) the depth bound, then the read itself.
    member private x.ReadCheckedString(len: int) =
        x.RequireAvailable(len, 1, "str")
        x.ReadString len

    member private x.ReadCheckedBin(len: int, t: Type) =
        x.RequireAvailable(len, 1, "bin")
        x.ReadBin(len, t)

    member private x.ReadNestedArray(len: int, t: Type) =
        x.RequireAvailable(len, 1, "array")
        x.EnterContainer()
        let value = x.ReadArray(len, t)
        x.ExitContainer()
        value

    member private x.ReadNestedMap(len: int, t: Type) =
        x.RequireAvailable(len, 2, "map")
        x.EnterContainer()
        let value = x.ReadMap(len, t)
        x.ExitContainer()
        value

    member x.Read t =
        match x.ReadByte() with
        // fixstr
        | b when b ||| 0b00011111uy = 0b10111111uy ->
            b &&& 0b00011111uy |> int |> x.ReadCheckedString |> interpretStringAs t
        | Format.Str8 -> x.ReadByte() |> int |> x.ReadCheckedString |> interpretStringAs t
        | Format.Str16 -> x.ReadUInt16() |> int |> x.ReadCheckedString |> interpretStringAs t
        | Format.Str32 -> x.ReadUInt32() |> int |> x.ReadCheckedString |> interpretStringAs t
        // fixposnum
        | b when b ||| 0b01111111uy = 0b01111111uy -> interpretIntegerAsFrom t 8 b
        // fixnegnum
        | b when b ||| 0b00011111uy = 0b11111111uy -> sbyte b |> interpretIntegerAsFrom t 8
        | Format.Int64 -> x.ReadInt64() |> interpretIntegerAsFrom t 64
        | Format.Int32 -> x.ReadInt32() |> interpretIntegerAsFrom t 32
        | Format.Int16 -> x.ReadInt16() |> interpretIntegerAsFrom t 16
        | Format.Int8 -> x.ReadInt8() |> interpretIntegerAsFrom t 8
        | Format.Uint8 -> x.ReadUInt8() |> interpretIntegerAsFrom t 8
        | Format.Uint16 -> x.ReadUInt16() |> interpretIntegerAsFrom t 16
        | Format.Uint32 -> x.ReadUInt32() |> interpretIntegerAsFrom t 32
        | Format.Uint64 -> x.ReadUInt64() |> interpretIntegerAsFrom t 64
        | Format.Float32 -> x.ReadFloat32() |> interpretFloatAs t
        | Format.Float64 -> x.ReadFloat64() |> interpretFloatAs t
        | Format.Nil -> box null
        | Format.True -> box true
        | Format.False -> box false
        // fixarr
        | b when b ||| 0b00001111uy = 0b10011111uy -> x.ReadNestedArray(b &&& 0b00001111uy |> int, t)
        | Format.Array16 ->
            let len = x.ReadUInt16() |> int
            x.ReadNestedArray(len, t)
        | Format.Array32 ->
            let len = x.ReadUInt32() |> int
            x.ReadNestedArray(len, t)
        // fixmap
        | b when b ||| 0b00001111uy = 0b10001111uy -> x.ReadNestedMap(b &&& 0b00001111uy |> int, t)
        | Format.Map16 ->
            let len = x.ReadUInt16() |> int
            x.ReadNestedMap(len, t)
        | Format.Map32 ->
            let len = x.ReadUInt32() |> int
            x.ReadNestedMap(len, t)
        | Format.Bin8 ->
            let len = x.ReadByte() |> int
            x.ReadCheckedBin(len, t)
        | Format.Bin16 ->
            let len = x.ReadUInt16() |> int
            x.ReadCheckedBin(len, t)
        | Format.Bin32 ->
            let len = x.ReadUInt32() |> int
            x.ReadCheckedBin(len, t)
        | b -> DecodeError.failAt [ sprintf "byte %d" pos ] t.Name (sprintf "format byte %d" b)

    // ─── Phase 785 — the one bytes-to-`Value` pass ───────────────────
    //
    // The same format dispatch as `Read` below, producing the CLOSED
    // value model instead of a reflection-directed `obj`. It inherits
    // every Phase 786 bound unchanged — it calls the identical guarded
    // helpers (`RequireAvailable`, `EnterContainer`) rather than
    // re-deriving them — so nothing here can be weaker than the path
    // beside it, and a bound added to one is added to both.
    //
    // What it does NOT inherit is the reflection: there is no `Type`
    // argument, no cache lookup, no `FSharpType` call. The width class
    // the format byte declared is CARRIED rather than applied, so the
    // decision "does this integer fit the target" moves out of the byte
    // reader and into a total combinator the caller composes.
    //
    // Phase 787's boundary runs exactly here: the totality of this pass
    // is BOUNDED (length against bytes remaining, nesting against
    // `maxDepth`) and not proved, because it advances a mutable cursor
    // over a byte array. Everything above it — `Decode` — is proved
    // territory, because it is a pure function over an immutable term.

    /// Phase 785 — the `bin` payload as a `byte[]` on both hosts.
    member private x.ReadBinValue(len: int) : byte[] =
        x.RequireAvailable(len, 1, "bin")
#if NETCOREAPP2_1_OR_GREATER && !FABLE_COMPILER
        (x.ReadRawBin len).ToArray()
#else
        x.ReadRawBin len
#endif

    /// Phase 785 — an array term. Seeded at a bounded capacity and grown
    /// as elements are actually decoded, exactly as `ReadRawArray` is,
    /// so a length prefix the guard admits still buys only a small
    /// allocation up front.
    member private x.ReadArrayValue(len: int) : Value =
        x.RequireAvailable(len, 1, "array")
        x.EnterContainer()
        let elements = ResizeArray<Value>(min len InitialCollectionCapacity)

        for _ in 1..len do
            elements.Add(x.ReadValue())

        x.ExitContainer()
        Value.Arr(List.ofSeq elements)

    /// Phase 785 — a map term. Entries stay in WIRE ORDER: sorting here
    /// would make the value model lossy about the bytes it came from,
    /// and the round-trip law Phase 785 states is over those bytes.
    member private x.ReadMapValue(len: int) : Value =
        x.RequireAvailable(len, 2, "map")
        x.EnterContainer()
        let pairs = ResizeArray<Value * Value>(min len InitialCollectionCapacity)

        for _ in 1..len do
            let key = x.ReadValue()
            let value = x.ReadValue()
            pairs.Add((key, value))

        x.ExitContainer()
        Value.Map(List.ofSeq pairs)

    /// Phase 785 — read one `Value` from the current position.
    ///
    /// Refuses through `DecodeException` exactly as `Read` does, so
    /// `TryReadValue` converts it back to a `Result` at the same seam
    /// and the two paths share one refusal vocabulary. There is no
    /// `ext` arm because this transport has none — see `Format.fs`'s
    /// header and `Value.fs`'s.
    /// Phase 785 — refuse a FORMAT BYTE read past the end of the
    /// payload.
    ///
    /// Phase 786 bounded every length prefix against the bytes
    /// remaining, which is the bound that matters once a value has
    /// started. The byte that STARTS a value was still unguarded, so an
    /// empty payload — and the exhausted tail of a container whose
    /// header promised more elements than the bytes hold — reached
    /// `ReadByte` and escaped as an `IndexOutOfRangeException`, which is
    /// not a named refusal and is what Phase 784's
    /// `truncated-empty-payload` mutation measured.
    ///
    /// Guarded HERE rather than in `ReadByte`, and that is a scope
    /// decision rather than an oversight: `ReadByte` is on the
    /// reflection reader's hot path too, and moving its behaviour is
    /// Phase 786's cross-section, not this phase's. The algebra path
    /// claims totality and so pays for the check; the reflection path
    /// keeps the behaviour Phase 784 recorded, and the difference is
    /// exactly what "the algebra refuses strictly more" means.
    member private _.RequireByte() =
        if pos >= data.Length then
            DecodeError.failAt
                [ sprintf "byte %d" pos ]
                "a MessagePack value"
                (sprintf "end of payload after %d byte(s)" data.Length)

    member x.ReadValue() : Value =
        x.RequireByte()

        match x.ReadByte() with
        // fixstr
        | b when b ||| 0b00011111uy = 0b10111111uy -> Value.Str(x.ReadCheckedString(b &&& 0b00011111uy |> int))
        | Format.Str8 -> Value.Str(x.ReadCheckedString(x.ReadByte() |> int))
        | Format.Str16 -> Value.Str(x.ReadCheckedString(x.ReadUInt16() |> int))
        | Format.Str32 -> Value.Str(x.ReadCheckedString(x.ReadUInt32() |> int))
        // fixposnum — the value IS the format byte, and it is unsigned.
        | b when b ||| 0b01111111uy = 0b01111111uy -> Value.UInt(uint64 b, IntegerWidth.Fixnum)
        // fixnegnum — likewise the format byte, read signed.
        | b when b ||| 0b00011111uy = 0b11111111uy -> Value.Int(int64 (sbyte b), IntegerWidth.Fixnum)
        | Format.Int64 -> Value.Int(x.ReadInt64(), IntegerWidth.Bits64)
        | Format.Int32 -> Value.Int(int64 (x.ReadInt32()), IntegerWidth.Bits32)
        | Format.Int16 -> Value.Int(int64 (x.ReadInt16()), IntegerWidth.Bits16)
        | Format.Int8 -> Value.Int(int64 (x.ReadInt8()), IntegerWidth.Bits8)
        | Format.Uint8 -> Value.UInt(uint64 (x.ReadUInt8()), IntegerWidth.Bits8)
        | Format.Uint16 -> Value.UInt(uint64 (x.ReadUInt16()), IntegerWidth.Bits16)
        | Format.Uint32 -> Value.UInt(uint64 (x.ReadUInt32()), IntegerWidth.Bits32)
        | Format.Uint64 -> Value.UInt(x.ReadUInt64(), IntegerWidth.Bits64)
        | Format.Float32 -> Value.Float(float (x.ReadFloat32()), FloatWidth.Single)
        | Format.Float64 -> Value.Float(x.ReadFloat64(), FloatWidth.Double)
        | Format.Nil -> Value.Nil
        | Format.True -> Value.Bool true
        | Format.False -> Value.Bool false
        // fixarr
        | b when b ||| 0b00001111uy = 0b10011111uy -> x.ReadArrayValue(b &&& 0b00001111uy |> int)
        | Format.Array16 -> x.ReadArrayValue(x.ReadUInt16() |> int)
        | Format.Array32 -> x.ReadArrayValue(x.ReadUInt32() |> int)
        // fixmap
        | b when b ||| 0b00001111uy = 0b10001111uy -> x.ReadMapValue(b &&& 0b00001111uy |> int)
        | Format.Map16 -> x.ReadMapValue(x.ReadUInt16() |> int)
        | Format.Map32 -> x.ReadMapValue(x.ReadUInt32() |> int)
        | Format.Bin8 -> Value.Bin(x.ReadBinValue(x.ReadByte() |> int))
        | Format.Bin16 -> Value.Bin(x.ReadBinValue(x.ReadUInt16() |> int))
        | Format.Bin32 -> Value.Bin(x.ReadBinValue(x.ReadUInt32() |> int))
        | b -> DecodeError.failAt [ sprintf "byte %d" pos ] "a MessagePack value" (sprintf "format byte %d" b)

    /// Phase 785 — the total entry to the one-pass reader. The same
    /// contract `TryRead` carries, including that the reader is LEFT
    /// WHERE THE REFUSAL HAPPENED: read one value per `Reader` on the
    /// refusal path, and do not resume.
    member x.TryReadValue() : Result<Value, DecodeError> =
        try
            Ok(x.ReadValue())
        with DecodeException error ->
            Error error

    /// Phase 783 — the named-refusal entry. Decodes `t` from the current
    /// position and returns either the value or a `DecodeError` naming
    /// what was expected, what the bytes held, and the byte offset it was
    /// at. Nothing here throws for a malformed payload.
    ///
    /// **Composition direction.** Phase 783's task text proposed the
    /// opposite arrangement — `TryRead` primary, `Read` derived as
    /// `TryRead |> Result.defaultWith raise`. That is not reachable
    /// without rewriting this reader's interior, which is the work
    /// [Phase 785] owns: the decode recursion runs through static
    /// `ConcurrentDictionary` caches whose values are `Type -> obj`
    /// delegates (`arrayReaderCache`, `mapReaderCache`, `setReaderCache`)
    /// and through `DictionaryDeserializer` / `ListDeserializer` /
    /// `SetDeserializer`, which take `read: Type -> obj` by signature.
    /// Threading a `Result` through those retypes five public generic
    /// types across two tiers for no behaviour a caller can observe.
    /// So `Read` stays primary and `TryRead` adapts it, and the two
    /// arrangements deliver the identical external contract: a total
    /// `Result`-returning entry, a throwing entry that existing callers
    /// compile against unchanged (GP 11), and one closed refusal
    /// vocabulary shared by both.
    ///
    /// **The reader is left where the refusal happened.** A failed
    /// `TryRead` does NOT rewind `pos` — the payload is malformed, so
    /// there is no well-defined position to rewind to, and the byte
    /// offset in the refusal's `Path` is the diagnostic. Read one value
    /// per `Reader` on the refusal path; do not resume.
    member x.TryRead(t: Type) : Result<obj, DecodeError> =
        try
            Ok(x.Read t)
        with DecodeException error ->
            Error error