// SPDX-License-Identifier: MIT
// Copyright (c) Zaid Ajaj and Fable.Remoting contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Remoting.MsgPack.Write

open System.IO
open System
open System.Text
open FSharp.Reflection
open FSharp.NativeInterop
open System.Reflection
open System.Collections.Concurrent

// Phase 902 — the .NET MessagePack writer (`makeSerializer`,
// `serializeObj` and the TypeShape engine behind them) lives in
// ToolUp.Platform.Server, in a module of this same name, which F# resolves
// as one with this half. What stays here is the writer Fable compiles.

module Fable =
    let private serializerCache =
        System.Collections.Generic.Dictionary<string, Action<obj, ResizeArray<byte>>>()

    let private cacheGetOrAdd (typ: Type, f) =
        match serializerCache.TryGetValue typ.FullName with
        | true, f -> f
        | _ ->
            serializerCache.Add(typ.FullName, f)
            f

    let inline private write32bitNumber b1 b2 b3 b4 (out: ResizeArray<byte>) writeFormat =
        if b2 > 0uy || b1 > 0uy then
            if writeFormat then
                out.Add Format.Uint32

            out.Add b1
            out.Add b2
            out.Add b3
            out.Add b4
        elif (b3 > 0uy) then
            if writeFormat then
                out.Add Format.Uint16

            out.Add b3
            out.Add b4
        else
            if writeFormat then
                out.Add Format.Uint8

            out.Add b4

    let inline private writeUnsigned32bitNumber (n: UInt32) (out: ResizeArray<byte>) =
        write32bitNumber (n >>> 24 |> byte) (n >>> 16 |> byte) (n >>> 8 |> byte) (byte n) out

    let inline private writeNil (out: ResizeArray<byte>) = out.Add Format.Nil

    let inline private writeBool x (out: ResizeArray<byte>) =
        out.Add(if x then Format.True else Format.False)

    let private writeSignedNumber bytes (out: ResizeArray<byte>) =
        if BitConverter.IsLittleEndian then
            Array.rev bytes |> out.AddRange
        else
            out.AddRange bytes

    // Phase 802 — every integer arm picks its format by the rule in
    // `Format.fs`'s header, byte for byte the rule the .NET writer
    // follows: a non-negative value rides the narrowest UNSIGNED format
    // whose top bit stays clear, a negative one the narrowest SIGNED
    // format other than `Int16`. So no emitted integer reads as a different value at any
    // width the reader accepts.
    let private writeUInt64 (n: UInt64) (out: ResizeArray<byte>) =
        if n < 128UL then
            out.Add(Format.fixposnum n)
        elif n <= 32767UL then
            // Never `Uint8` for 128..255: that byte's top bit is the sign
            // bit of an int8.
            out.Add Format.Uint16
            out.Add(n >>> 8 |> byte)
            out.Add(byte n)
        elif n <= 2147483647UL then
            out.Add Format.Uint32
            out.Add(n >>> 24 |> byte)
            out.Add(n >>> 16 |> byte)
            out.Add(n >>> 8 |> byte)
            out.Add(byte n)
        else
            // Above `Int32.MaxValue` the 32-bit form would read as a
            // negative int32. A value above `Int64.MaxValue` (a uint64
            // source only) has no wider format.
            out.Add Format.Uint64
            out.Add(n >>> 56 |> byte)
            out.Add(n >>> 48 |> byte)
            out.Add(n >>> 40 |> byte)
            out.Add(n >>> 32 |> byte)
            out.Add(n >>> 24 |> byte)
            out.Add(n >>> 16 |> byte)
            out.Add(n >>> 8 |> byte)
            out.Add(byte n)

    let private writeInt64 (n: int64) (out: ResizeArray<byte>) =
        if n >= 0L then
            writeUInt64 (uint64 n) out
        elif n >= -32L then
            out.Add(Format.fixnegnum n)
        elif n >= -128L then
            out.Add Format.Int8
            out.Add(byte n)
        // No `Int16`, as on .NET: a Fable reader before Phase 802 does
        // not sign-extend it, so `Int32` is the narrowest signed form
        // every reader generation decodes correctly.
        elif n >= -2147483648L then
            out.Add Format.Int32
            out.Add(n >>> 24 |> byte)
            out.Add(n >>> 16 |> byte)
            out.Add(n >>> 8 |> byte)
            out.Add(byte n)
        else
            out.Add Format.Int64
            writeSignedNumber (BitConverter.GetBytes n) out

    let private writeByte (b: byte) (out: ResizeArray<byte>) = writeUInt64 (uint64 b) out

    let inline private writeString (str: string) (out: ResizeArray<byte>) =
        let str = Encoding.UTF8.GetBytes str

        if str.Length < 32 then
            out.Add(Format.fixstr str.Length)
        else
            if str.Length < 256 then out.Add Format.Str8
            elif str.Length < 65536 then out.Add Format.Str16
            else out.Add Format.Str32

            writeUnsigned32bitNumber (uint32 str.Length) out false

        out.AddRange str

    let private writeSingle (n: float32) (out: ResizeArray<byte>) =
        out.Add Format.Float32
        writeSignedNumber (BitConverter.GetBytes n) out

    let private writeDouble (n: float) (out: ResizeArray<byte>) =
        out.Add Format.Float64
        writeSignedNumber (BitConverter.GetBytes n) out

    let private writeBin (data: byte[]) (out: ResizeArray<byte>) =
        if data.Length < 256 then out.Add Format.Bin8
        elif data.Length < 65536 then out.Add Format.Bin16
        else out.Add Format.Bin32

        writeUnsigned32bitNumber (uint32 data.Length) out false

        out.AddRange data

    let inline private writeDateTime (out: ResizeArray<byte>) (dto: DateTime) =
        out.Add(Format.fixarr 2uy)
        writeInt64 dto.Ticks out
        writeInt64 (int64 dto.Kind) out

    let inline private writeDateTimeOffset (out: ResizeArray<byte>) (dto: DateTimeOffset) =
        out.Add(Format.fixarr 2uy)
        writeInt64 dto.Ticks out
        writeInt64 (int64 dto.Offset.TotalMinutes) out

#if NET6_0_OR_GREATER
    let inline private writeDateOnly (out: ResizeArray<byte>) (date: DateOnly) = writeInt64 (int64 date.DayNumber) out

    // `Ticks` is an int64, and the .NET writer emits it through
    // `writeInt64`; routing it the same way keeps the two hosts' bytes one.
    let inline private writeTimeOnly (out: ResizeArray<byte>) (time: TimeOnly) = writeInt64 time.Ticks out
#endif

    let private writeArrayHeader len (out: ResizeArray<byte>) =
        if len < 16 then
            out.Add(Format.fixarr len)
        elif len < 65536 then
            out.Add Format.Array16
            out.Add(len >>> 8 |> FSharp.Core.Operators.byte)
            out.Add(FSharp.Core.Operators.byte len)
        else
            out.Add Format.Array32
            writeUnsigned32bitNumber (uint32 len) out false

    let private writeDecimal (n: decimal) (out: ResizeArray<byte>) =
        let bits = Decimal.GetBits n

        out.Add(Format.fixarr 4)

        // Phase 802 — each word is an int32 and travels as one, so a
        // negative word keeps a signed format.
        for b in bits do
            writeInt64 (int64 b) out

    let rec private writeArray (out: ResizeArray<byte>) t (arr: System.Collections.ICollection) =
        writeArrayHeader arr.Count out

        for x in arr do
            writeObject x t out

    and private writeMap
        (out: ResizeArray<byte>)
        keyType
        valueType
        (dict: System.Collections.Generic.IDictionary<obj, obj>)
        =
        let length = dict.Count

        if length < 16 then
            out.Add(Format.fixmap length)
        elif length < 65536 then
            out.Add Format.Map16
            out.Add(length >>> 8 |> FSharp.Core.Operators.byte)
            out.Add(FSharp.Core.Operators.byte length)
        else
            out.Add Format.Map32
            writeUnsigned32bitNumber (uint32 length) out false

        for kvp in dict do
            writeObject kvp.Key keyType out
            writeObject kvp.Value valueType out

    and private writeSet (out: ResizeArray<byte>) t (set: System.Collections.ICollection) =
        writeArrayHeader set.Count out

        for x in set do
            writeObject x t out

    and inline private writeRecord (out: ResizeArray<byte>) (types: Type[]) (vals: obj[]) =
        writeArrayHeader vals.Length out

        for i in 0 .. vals.Length - 1 do
            writeObject vals.[i] types.[i] out

    and inline private writeTuple (out: ResizeArray<byte>) (types: Type[]) (vals: obj[]) = writeRecord out types vals

    and private writeUnion (out: ResizeArray<byte>) tag (types: Type[]) (vals: obj[]) =
        if vals.Length = 0 then
            out.Add(Format.fixarr 1uy)
            out.Add(Format.fixposnum tag)
        else
            out.Add(Format.fixarr 2uy)
            out.Add(Format.fixposnum tag)

            // write the field directly instead of using an array if the union case has a single field
            // saves 1 byte
            if vals.Length = 1 then
                writeObject vals.[0] types.[0] out
            else
                writeArrayHeader vals.Length out

                for i in 0 .. vals.Length - 1 do
                    writeObject vals.[i] types.[i] out

    and writeObject (x: obj) (t: Type) (out: ResizeArray<byte>) =
#if !FABLE_COMPILER
        raise (
            NotSupportedException
                "This function is meant to be used in Fable, please use serializeObj or makeSerializer."
        )
#else
        if isNull x then
            writeNil out
        else

            match serializerCache.TryGetValue(t.FullName) with
            | true, writer -> writer.Invoke(x, out)
            | _ ->
                if FSharpType.IsRecord t then
                    let fieldTypes = FSharpType.GetRecordFields t |> Array.map _.PropertyType

                    cacheGetOrAdd(
                        t,
                        Action<_, _>(fun x out -> writeRecord out fieldTypes (FSharpValue.GetRecordFields x))
                    )
                        .Invoke(x, out)
                elif t.IsArray then
                    let elementType = t.GetElementType()

                    cacheGetOrAdd(
                        t,
                        Action<_, _>(fun x out -> writeArray out elementType (x :?> System.Collections.ICollection))
                    )
                        .Invoke(x, out)
                elif FSharpType.IsUnion t then
                    cacheGetOrAdd(
                        t,
                        Action<_, _>(fun x out ->
                            let case, fields = FSharpValue.GetUnionFields(x, t)
                            let fieldTypes = case.GetFields() |> Array.map _.PropertyType
                            writeUnion out case.Tag fieldTypes fields)
                    )
                        .Invoke(x, out)
                elif FSharpType.IsTuple t then
                    let fieldTypes = FSharpType.GetTupleElements t

                    cacheGetOrAdd(
                        t,
                        Action<_, _>(fun x out -> writeTuple out fieldTypes (FSharpValue.GetTupleFields x))
                    )
                        .Invoke(x, out)
                elif t.IsEnum then
                    cacheGetOrAdd(t, Action<_, _>(fun x out -> writeInt64 (box x :?> int |> int64) out)).Invoke(x, out)
                elif t.IsGenericType then
                    let tDef = t.GetGenericTypeDefinition()
                    let genArgs = t.GetGenericArguments()

                    if tDef = typedefof<_ list> then
                        let elementType = genArgs |> Array.head

                        cacheGetOrAdd(
                            t,
                            Action<_, _>(fun x out -> writeArray out elementType (x :?> System.Collections.ICollection))
                        )
                            .Invoke(x, out)
                    elif tDef = typedefof<_ option> then
                        cacheGetOrAdd(
                            t,
                            Action<_, _>(fun x out ->
                                let opt = x :?> _ option
                                let tag, values = if Option.isSome opt then 1, [| opt.Value |] else 0, [||]
                                writeUnion out tag genArgs values)
                        )
                            .Invoke(x, out)
                    elif
                        tDef = typedefof<System.Collections.Generic.Dictionary<_, _>>
                        || tDef = typedefof<Map<_, _>>
                    then
                        let keyType = genArgs.[0]
                        let valueType = genArgs.[1]

                        cacheGetOrAdd(
                            t,
                            Action<_, _>(fun x out ->
                                writeMap
                                    out
                                    keyType
                                    valueType
                                    (box x :?> System.Collections.Generic.IDictionary<obj, obj>))
                        )
                            .Invoke(x, out)
                    elif tDef = typedefof<Set<_>> then
                        let elementType = genArgs |> Array.head

                        cacheGetOrAdd(
                            t,
                            Action<_, _>(fun x out -> writeSet out elementType (x :?> System.Collections.ICollection))
                        )
                            .Invoke(x, out)
                    else
                        failwithf "Cannot serialize %s." t.Name
                elif
                    t.FullName = "Microsoft.FSharp.Core.int16`1"
                    || t.FullName = "Microsoft.FSharp.Core.int32`1"
                    || t.FullName = "Microsoft.FSharp.Core.int64`1"
                then
                    cacheGetOrAdd(t, Action<_, _>(fun x out -> writeInt64 (x :?> int64) out)).Invoke(x, out)
                elif t.FullName = "Microsoft.FSharp.Core.decimal`1" then
                    cacheGetOrAdd(t, Action<_, _>(fun x out -> writeDecimal (x :?> decimal) out)).Invoke(x, out)
                elif t.FullName = "Microsoft.FSharp.Core.float`1" then
                    cacheGetOrAdd(t, Action<_, _>(fun x out -> writeDouble (x :?> float) out)).Invoke(x, out)
                elif t.FullName = "Microsoft.FSharp.Core.float32`1" then
                    cacheGetOrAdd(t, Action<_, _>(fun x out -> writeSingle (x :?> float32) out)).Invoke(x, out)
                else
                    failwithf "Cannot serialize %s." t.Name
#endif

    let inline writeType<'T> (x: 'T) (out: ResizeArray<byte>) =
#if !FABLE_COMPILER
        raise (
            NotSupportedException
                "This function is meant to be used in Fable, please use serializeObj or makeSerializer."
        )
#else
        writeObject x typeof<'T> out
#endif

#if FABLE_COMPILER
    serializerCache.Add(typeof<byte>.FullName, fun x out -> writeByte (x :?> byte) out)
    serializerCache.Add(typeof<sbyte>.FullName, fun x out -> writeInt64 (x :?> sbyte |> int64) out)
    serializerCache.Add(typeof<unit>.FullName, fun _ out -> writeNil out)
    serializerCache.Add(typeof<bool>.FullName, fun x out -> writeBool (x :?> bool) out)
    serializerCache.Add(typeof<char>.FullName, fun x out -> writeString (x :?> string) out) // There are only strings in JS
    serializerCache.Add(typeof<string>.FullName, fun x out -> writeString (x :?> string) out)
    serializerCache.Add(typeof<int>.FullName, fun x out -> writeInt64 (x :?> int |> int64) out)
    serializerCache.Add(typeof<int16>.FullName, fun x out -> writeInt64 (x :?> int16 |> int64) out)
    serializerCache.Add(typeof<int64>.FullName, fun x out -> writeInt64 (x :?> int64) out)
    serializerCache.Add(typeof<UInt32>.FullName, fun x out -> writeUInt64 (x :?> UInt32 |> uint64) out)
    serializerCache.Add(typeof<UInt16>.FullName, fun x out -> writeUInt64 (x :?> UInt16 |> uint64) out)
    serializerCache.Add(typeof<UInt64>.FullName, fun x out -> writeUInt64 (x :?> UInt64) out)
    serializerCache.Add(typeof<float32>.FullName, fun x out -> writeSingle (x :?> float32) out)
    serializerCache.Add(typeof<float>.FullName, fun x out -> writeDouble (x :?> float) out)
    serializerCache.Add(typeof<decimal>.FullName, fun x out -> writeDecimal (x :?> decimal) out)
    serializerCache.Add(typeof<byte[]>.FullName, fun x out -> writeBin (x :?> byte[]) out)
    serializerCache.Add(typeof<bigint>.FullName, fun x out -> writeBin ((x :?> bigint).ToByteArray()) out)
    serializerCache.Add(typeof<Guid>.FullName, fun x out -> writeBin ((x :?> Guid).ToByteArray()) out)
    serializerCache.Add(typeof<DateTime>.FullName, fun x out -> writeDateTime out (x :?> DateTime))
    serializerCache.Add(typeof<DateTimeOffset>.FullName, fun x out -> writeDateTimeOffset out (x :?> DateTimeOffset))
#if NET6_0_OR_GREATER
    serializerCache.Add(typeof<DateOnly>.FullName, fun x out -> writeDateOnly out (x :?> DateOnly))
    serializerCache.Add(typeof<TimeOnly>.FullName, fun x out -> writeTimeOnly out (x :?> TimeOnly))
#endif
    serializerCache.Add(typeof<TimeSpan>.FullName, fun x out -> writeInt64 (x :?> TimeSpan).Ticks out)
#endif