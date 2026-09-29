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
open System.Linq.Expressions
open TypeShape
open TypeShape_Utils

#nowarn "9"
#nowarn "51"

// Phase 902 — the .NET MessagePack writer, moved from ToolUp.Platform.Core,
// where it shipped to every Fable consumer behind a `#if !FABLE_COMPILER`
// guard. The module path is preserved: Core keeps the Fable writer
// (`Write.Fable`) in a module of this same name, and F# resolves the two as
// one. `this` names the executing assembly, which is now this one.

let this = Assembly.GetExecutingAssembly().GetType("ToolUp.Remoting.MsgPack.Write")

let internal (|BclIsInstanceOfSystemDataSet|_|) (s: TypeShape) =
    let tableTy = typeof<System.Data.DataSet>

    if s.Type = tableTy || (s.Type.IsInstanceOfType tableTy && s.Type <> typeof<obj>) then
        Shape.SomeU
    else
        None

let internal (|BclIsInstanceOfSystemDataTable|_|) (s: TypeShape) =
    let tableTy = typeof<System.Data.DataTable>

    if s.Type = tableTy || (s.Type.IsInstanceOfType tableTy && s.Type <> typeof<obj>) then
        Shape.SomeU
    else
        None

#if NETCOREAPP2_1_OR_GREATER
/// **UNSOUND — do not call. Allocate the stack buffer in the function
/// that reads it.**
///
/// This returns a `Span` over memory `NativePtr.stackalloc` obtained in
/// THIS function's frame. When the `inline` is honoured the allocation
/// lands in the caller's frame and the span is valid; when it is NOT —
/// and with the F# optimiser off it is not — the frame is popped on
/// return and the caller reads whatever the next call reuses that stack
/// for.
///
/// Found 2026-09-13 by the Phase 784 wire corpus and independently by the
/// Phase 786 regression guard: `writeString`, `writeDecimal` and
/// `writeGuid` all took their buffers from here, and the first two wrote
/// unrelated memory onto the wire — non-deterministically, under a
/// correct length header, so the payload was well-formed and wrong.
/// `writeGuid` survived only because it read its buffer back before
/// making any other call. Measured `-c Debug` corrupt, `-c Release`
/// correct, `-c Debug -p:Optimize=true` correct: the discriminator is the
/// optimiser, so production (Release) was unaffected and every Debug test
/// run in this repository was exchanging corrupt MsgPack payloads with
/// nothing looking. All three call sites now allocate their own buffer.
///
/// It is kept rather than deleted ONLY because it is public surface with
/// a committed API baseline, and removing a token from a baseline scores
/// as a breaking change. Nothing in this assembly calls it.
let inline stackalloc<'a when 'a: unmanaged> length =
    Span<'a>(NativePtr.stackalloc<'a> length |> NativePtr.toVoidPtr, length)
#endif

#if NET6_0_OR_GREATER
let internal (|DateOnly|_|) (s: TypeShape) = Shape.test<DateOnly> s

let internal (|TimeOnly|_|) (s: TypeShape) = Shape.test<TimeOnly> s
#endif

let inline write32bitNumberBytes b1 b2 b3 b4 (out: Stream) writeFormat =
    if b2 > 0uy || b1 > 0uy then
        if writeFormat then
            out.WriteByte Format.Uint32

        out.WriteByte b1
        out.WriteByte b2
        out.WriteByte b3
        out.WriteByte b4
    elif (b3 > 0uy) then
        if writeFormat then
            out.WriteByte Format.Uint16

        out.WriteByte b3
        out.WriteByte b4
    else
        if writeFormat then
            out.WriteByte Format.Uint8

        out.WriteByte b4

let inline write64bitNumberBytes b1 b2 b3 b4 b5 b6 b7 b8 (out: Stream) =
    if b4 > 0uy || b3 > 0uy || b2 > 0uy || b1 > 0uy then
        out.WriteByte Format.Uint64
        out.WriteByte b1
        out.WriteByte b2
        out.WriteByte b3
        out.WriteByte b4
        out.WriteByte b5
        out.WriteByte b6
        out.WriteByte b7
        out.WriteByte b8
    else
        write32bitNumberBytes b5 b6 b7 b8 out true

let inline write32bitNumber n (out: Stream) =
    write32bitNumberBytes (n >>> 24 |> byte) (n >>> 16 |> byte) (n >>> 8 |> byte) (byte n) out

let inline write64bitNumber n (out: Stream) =
    write64bitNumberBytes
        (n >>> 56 |> byte)
        (n >>> 48 |> byte)
        (n >>> 40 |> byte)
        (n >>> 32 |> byte)
        (n >>> 24 |> byte)
        (n >>> 16 |> byte)
        (n >>> 8 |> byte)
        (byte n)
        out

let inline write32bitNumberFull n (out: Stream) =
    out.WriteByte(n >>> 24 |> byte)
    out.WriteByte(n >>> 16 |> byte)
    out.WriteByte(n >>> 8 |> byte)
    out.WriteByte(byte n)

let inline write64bitNumberFull n (out: Stream) =
    out.WriteByte(n >>> 56 |> byte)
    out.WriteByte(n >>> 48 |> byte)
    out.WriteByte(n >>> 40 |> byte)
    out.WriteByte(n >>> 32 |> byte)
    out.WriteByte(n >>> 24 |> byte)
    out.WriteByte(n >>> 16 |> byte)
    out.WriteByte(n >>> 8 |> byte)
    out.WriteByte(byte n)

let inline writeNil (out: Stream) = out.WriteByte Format.Nil

let inline writeBool b (out: Stream) =
    out.WriteByte(if b then Format.True else Format.False)

let inline writeByte b (out: Stream) =
    if b < 128uy then
        out.WriteByte(Format.fixposnum b)
    else
        out.WriteByte Format.Uint8
        out.WriteByte b

let inline writeSByte (b: sbyte) (out: Stream) = writeByte (byte b) out

let inline writeArrayHeader length (out: Stream) =
    if length < 16 then
        out.WriteByte(Format.fixarr length)
    elif length < 65536 then
        out.WriteByte Format.Array16
        out.WriteByte(length >>> 8 |> byte)
        out.WriteByte(byte length)
    else
        out.WriteByte Format.Array32
        write32bitNumber length out false

let inline writeArray (array: 'a[]) (out: Stream) (elementSerializer: Action<'a, Stream>) =
    if isNull array then
        writeNil out
    else

        writeArrayHeader array.Length out

        for x in array do
            elementSerializer.Invoke(x, out)

let inline writeList (list: 'a list) (out: Stream) (elementSerializer: Action<'a, Stream>) =
    writeArrayHeader list.Length out

    for x in list do
        elementSerializer.Invoke(x, out)

let inline writeMapHeader length (out: Stream) =
    if length < 16 then
        out.WriteByte(Format.fixmap length)
    elif length < 65536 then
        out.WriteByte Format.Map16
        out.WriteByte(length >>> 8 |> byte)
        out.WriteByte(byte length)
    else
        out.WriteByte Format.Map32
        write32bitNumber length out false

let inline writeSet (set: Set<'a>) (out: Stream) (elementSerializer: Action<'a, Stream>) =
    writeArrayHeader set.Count out

    for x in set do
        elementSerializer.Invoke(x, out)

let inline writeDict
    (dict: System.Collections.Generic.Dictionary<'key, 'value>)
    (out: Stream)
    (keyWriter: Action<'key, Stream>)
    (valueWriter: Action<'value, Stream>)
    =
    writeMapHeader dict.Count out

    for kvp in dict do
        keyWriter.Invoke(kvp.Key, out)
        valueWriter.Invoke(kvp.Value, out)

// we could use just one function accepting IDictionary for both Map and Dictionary, but Map.iter is significantly faster than a foreach and doesn't allocate
let inline writeMap
    (map: Map<'key, 'value>)
    (out: Stream)
    (keyWriter: Action<'key, Stream>)
    (valueWriter: Action<'value, Stream>)
    =
    writeMapHeader map.Count out

    map
    |> Map.iter (fun k v ->
        keyWriter.Invoke(k, out)
        valueWriter.Invoke(v, out))

let inline writeUInt64 (n: UInt64) (out: Stream) =
    if n < 128UL then
        out.WriteByte(Format.fixposnum n)
    else
        write64bitNumber n out

let inline writeInt64 (n: int64) (out: Stream) =
    if n >= 0L then
        writeUInt64 (uint64 n) out
    elif n > -32L then
        out.WriteByte(Format.fixnegnum n)
    else
        out.WriteByte Format.Int64
        write64bitNumberFull n out

let inline writeSingle (n: float32) (out: Stream) =
    let mutable n = n
    out.WriteByte Format.Float32
    write32bitNumberFull (NativePtr.toNativeInt &&n |> NativePtr.ofNativeInt |> NativePtr.read<uint32>) out

let inline writeDouble (n: float) (out: Stream) =
    let mutable n = n
    out.WriteByte Format.Float64
    write64bitNumberFull (NativePtr.toNativeInt &&n |> NativePtr.ofNativeInt |> NativePtr.read<uint64>) out

#if NET5_0_OR_GREATER
[<System.Runtime.CompilerServices.SkipLocalsInit>]
#endif
let writeDecimal (n: decimal) (out: Stream) =
#if NET5_0_OR_GREATER
    // Allocated HERE, not through the `stackalloc` helper: the buffer is
    // read back across the `write32bitNumber` calls below, and a helper
    // that is not inlined hands back a popped frame. See the helper's own
    // note. `12345.6789m` came back as `123456789m` — the scale word lost
    // to whatever the intervening call left on the stack.
    let bitsPtr = NativePtr.stackalloc<int> 4
    let bits = Span<int>(NativePtr.toVoidPtr bitsPtr, 4)
    Decimal.GetBits(n, bits) |> ignore
#else
    let bits = Decimal.GetBits n
#endif

    out.WriteByte(Format.fixarr 4)

    for b in bits do
        write32bitNumber b out true

let inline writeStringHeader length (out: Stream) =
    if length < 32 then
        out.WriteByte(Format.fixstr length)
    else
        if length < 256 then out.WriteByte Format.Str8
        elif length < 65536 then out.WriteByte Format.Str16
        else out.WriteByte Format.Str32

        write32bitNumber length out false

#if NET5_0_OR_GREATER
[<System.Runtime.CompilerServices.SkipLocalsInit>]
#endif
let writeString (str: string) (out: Stream) =
    if isNull str then
        writeNil out
    else
#if NETCOREAPP3_1_OR_GREATER
        let maxLength = Encoding.UTF8.GetMaxByteCount str.Length

        // allocate space on the stack if the string is not too long
        if maxLength < 1500 then
            // Allocated HERE, not through the `stackalloc` helper. The
            // header write between filling this buffer and reading it is
            // a call, and a helper that is not inlined has already popped
            // the frame the buffer lives in — so `"probe"` went out as a
            // correct `A5` header over five bytes of unrelated memory.
            // The `ArrayPool` branch below was never affected, which is
            // why strings over ~500 characters were fine.
            let bufferPtr = NativePtr.stackalloc<byte> maxLength
            let buffer = Span<byte>(NativePtr.toVoidPtr bufferPtr, maxLength)
            let bytesWritten = Encoding.UTF8.GetBytes(String.op_Implicit str, buffer)

            writeStringHeader bytesWritten out
            out.Write(Span.op_Implicit (buffer.Slice(0, bytesWritten)))
            ()
        else
            let buffer = System.Buffers.ArrayPool.Shared.Rent maxLength

            try
                let bytesWritten = Encoding.UTF8.GetBytes(str, 0, str.Length, buffer, 0)

                writeStringHeader bytesWritten out
                out.Write(buffer, 0, bytesWritten)
            finally
                System.Buffers.ArrayPool.Shared.Return buffer
#else
        let str = Encoding.UTF8.GetBytes str
        writeStringHeader str.Length out
        out.Write(str, 0, str.Length)
#endif

let writeBin (data: byte[]) (out: Stream) =
    if isNull data then
        writeNil out
    else

        if data.Length < 256 then out.WriteByte Format.Bin8
        elif data.Length < 65536 then out.WriteByte Format.Bin16
        else out.WriteByte Format.Bin32

        write32bitNumber data.Length out false
        out.Write(data, 0, data.Length)

#if NET6_0_OR_GREATER
let inline writeDateOnly (date: DateOnly) (out: Stream) =
    write32bitNumber date.DayNumber out true

let inline writeTimeOnly (time: TimeOnly) (out: Stream) = writeInt64 time.Ticks out
#endif

let inline writeDateTime (dt: DateTime) (out: Stream) =
    out.WriteByte(Format.fixarr 2uy)
    writeInt64 dt.Ticks out
    writeInt64 (int64 dt.Kind) out

let inline writeDateTimeOffset (dto: DateTimeOffset) (out: Stream) =
    out.WriteByte(Format.fixarr 2uy)
    writeInt64 dto.Ticks out
    writeInt64 (int64 dto.Offset.TotalMinutes) out

let inline writeTimeSpan (ts: TimeSpan) out = writeInt64 ts.Ticks out

#if NET5_0_OR_GREATER
[<System.Runtime.CompilerServices.SkipLocalsInit>]
#endif
let writeGuid (g: Guid) (out: Stream) =
#if NETCOREAPP2_1_OR_GREATER
    // Allocated HERE for the same reason as the two above. This one was
    // CORRECT before the fix — it reads its buffer back with no
    // intervening call, so nothing had reused the popped frame yet — and
    // it is changed anyway: "correct by luck of instruction order" is not
    // a property to leave a wire writer resting on.
    let bufferPtr = NativePtr.stackalloc<byte> 16
    let buffer = Span<byte>(NativePtr.toVoidPtr bufferPtr, 16)
    g.TryWriteBytes buffer |> ignore
    out.WriteByte Format.Bin8
    out.WriteByte 16uy
    out.Write buffer
    ()
#else
    writeBin (g.ToByteArray()) out
#endif

let inline writeBigInteger (i: bigint) out = writeBin (i.ToByteArray()) out

// todo necessary to take the underlying type into account?
let inline writeEnum (enum: 'enum when 'enum: enum<'underlying>) out =
    writeInt64 (Convert.ChangeType(enum, typeof<int64>) :?> int64) out

let inline writeRecord record out (fieldSerializers: Action<'a, Stream>[]) =
    writeArrayHeader fieldSerializers.Length out

    for f in fieldSerializers do
        f.Invoke(record, out)

let inline writeUnion union (out: Stream) (caseSerializers: Action<'a, Stream>[][]) tagReader =
    let tag = tagReader union
    let fieldSerializers = caseSerializers.[tag]

    if fieldSerializers.Length = 0 then
        out.WriteByte(Format.fixarr 1uy)
        out.WriteByte(Format.fixposnum tag)
    else
        out.WriteByte(Format.fixarr 2uy)
        out.WriteByte(Format.fixposnum tag)

        // write the field directly instead of using an array if the union case has a single field
        // saves 1 byte
        if fieldSerializers.Length = 1 then
            let serializer = fieldSerializers.[0]
            serializer.Invoke(union, out)
        else
            writeArrayHeader fieldSerializers.Length out

            for serializer in fieldSerializers do
                serializer.Invoke(union, out)

let inline writeStringEnum union out (caseNames: string[]) tagReader =
    writeString caseNames.[tagReader union] out

let inline writeTuple tuple (out: Stream) (elementSerializers: Action<'a, Stream>[]) =
    writeArrayHeader elementSerializers.Length out

    for s in elementSerializers do
        s.Invoke(tuple, out)

let inline writeDataTable (table: System.Data.DataTable) out =
    let schema, data =
        use stringWriter1 = new StringWriter()
        use stringWriter2 = new StringWriter()
        table.WriteXmlSchema stringWriter1
        table.WriteXml stringWriter2
        string stringWriter1, string stringWriter2

    writeArray [| schema; data |] out (Action<_, _>(writeString))

let inline writeDataSet (dataset: System.Data.DataSet) out =
    let schema, data =
        use stringWriter1 = new StringWriter()
        use stringWriter2 = new StringWriter()
        dataset.WriteXmlSchema stringWriter1
        dataset.WriteXml stringWriter2
        string stringWriter1, string stringWriter2

    writeArray [| schema; data |] out (Action<_, _>(writeString))

let rec makeSerializer<'T> () : Action<'T, Stream> =
    if typeof<'T> = typeof<obj> then
        failwithf
            "Cannot serialize System.Object. If you are unable specify the generic parameter for 'makeSerializer', use 'makeSerializerObj' instead."

    let ctx = new TypeGenerationContext()
    serializerCached<'T> ctx

and private serializerCached<'T> (ctx: TypeGenerationContext) : Action<'T, Stream> =
    let delay (c: Cell<Action<'T, Stream>>) : Action<'T, Stream> =
        Action<'T, Stream>(fun x out -> c.Value.Invoke(x, out))

    match ctx.InitOrGetCachedValue<Action<'T, Stream>> delay with
    | Cached(value, _) -> value
    | NotCached x ->
        let serializer = makeSerializerAux<'T> ctx
        ctx.Commit x serializer

and private makeSerializerAux<'T> (ctx: TypeGenerationContext) : Action<'T, Stream> =
    let w (p: Action<'a, Stream>) = unbox<Action<'T, Stream>> p

    let makeMemberVisitor (m: IShapeReadOnlyMember<'T>) =
        m.Accept
            { new IReadOnlyMemberVisitor<'T, Action<'T, Stream>> with
                member _.Visit(field: ReadOnlyMember<'T, 'a>) =
                    let s = serializerCached<'a> ctx
                    Action<_, _>(fun (x: 'T) out -> s.Invoke(field.Get x, out)) |> w
            }

    match shapeof<'T> with
    | Shape.Unit -> Action<_, _>(fun () out -> writeNil out) |> w
    | Shape.Bool -> Action<_, _> writeBool |> w
    | Shape.Byte -> Action<_, _> writeByte |> w
    | Shape.SByte -> Action<_, _> writeSByte |> w
    | Shape.Char -> Action<_, _>(fun (c: char) out -> writeString (c.ToString()) out) |> w
    | Shape.String -> Action<_, _> writeString |> w
    | Shape.Int16 -> Action<_, _>(fun (i: int16) out -> writeInt64 (int64 i) out) |> w
    | Shape.Int32 -> Action<_, _>(fun (i: int32) out -> writeInt64 (int64 i) out) |> w
    | Shape.Int64 -> Action<_, _> writeInt64 |> w
    | Shape.UInt16 -> Action<_, _>(fun (i: uint16) out -> writeUInt64 (uint64 i) out) |> w
    | Shape.UInt32 -> Action<_, _>(fun (i: uint32) out -> writeUInt64 (uint64 i) out) |> w
    | Shape.UInt64 -> Action<_, _> writeUInt64 |> w
    | Shape.Single -> Action<_, _> writeSingle |> w
    | Shape.Double -> Action<_, _> writeDouble |> w
    | Shape.Decimal -> Action<_, _> writeDecimal |> w
    | Shape.BigInt -> Action<_, _> writeBigInteger |> w
    | Shape.DateTime -> Action<_, _> writeDateTime |> w
    | Shape.DateTimeOffset -> Action<_, _> writeDateTimeOffset |> w
    | Shape.TimeSpan -> Action<_, _> writeTimeSpan |> w
    | Shape.Guid -> Action<_, _> writeGuid |> w
    | Shape.Array s when s.Rank = 1 ->
        s.Element.Accept
            { new ITypeVisitor<Action<'T, Stream>> with
                member _.Visit<'a>() =
                    if typeof<'a> = typeof<byte> then
                        Action<_, _> writeBin |> w
                    else
                        let s = serializerCached<'a> ctx
                        Action<_, _>(fun x out -> writeArray x out s) |> w
            }
    | Shape.FSharpMap m ->
        m.Accept
            { new IFSharpMapVisitor<Action<'T, Stream>> with
                member _.Visit<'key, 'value when 'key: comparison>() =
                    let keyWriter = serializerCached<'key> ctx
                    let valueWriter = serializerCached<'value> ctx
                    Action<_, _>(fun x out -> writeMap x out keyWriter valueWriter) |> w
            }
    | Shape.FSharpSet s ->
        s.Accept
            { new IFSharpSetVisitor<Action<'T, Stream>> with
                member _.Visit<'a when 'a: comparison>() =
                    let s = serializerCached<'a> ctx
                    Action<_, _>(fun x out -> writeSet x out s) |> w
            }
    | Shape.Dictionary d ->
        d.Accept
            { new IDictionaryVisitor<Action<'T, Stream>> with
                member _.Visit<'key, 'value when 'key: equality>() =
                    let keyWriter = serializerCached<'key> ctx
                    let valueWriter = serializerCached<'value> ctx
                    Action<_, _>(fun x out -> writeDict x out keyWriter valueWriter) |> w
            }
    | Shape.FSharpList s ->
        s.Element.Accept
            { new ITypeVisitor<Action<'T, Stream>> with
                member _.Visit<'a>() =
                    let s = serializerCached<'a> ctx
                    Action<_, _>(fun x out -> writeList x out s) |> w
            }
    | Shape.FSharpRecord(:? ShapeFSharpRecord<'T> as shape) ->
        let fieldSerializers = shape.Fields |> Array.map makeMemberVisitor

        Action<_, _>(fun (record: 'T) out -> writeRecord record out fieldSerializers)
        |> w
    | Shape.FSharpUnion(:? ShapeFSharpUnion<'T> as shape) ->
        if
            typeof<'T>.CustomAttributes
            |> Seq.exists (fun a -> a.AttributeType.Name = "StringEnumAttribute")
        then
            let caseNames =
                shape.UnionCases
                |> Array.map (fun c ->
                    sprintf "%c%s" (Char.ToLowerInvariant c.CaseInfo.Name.[0]) (c.CaseInfo.Name.Substring 1))

            Action<_, _>(fun (union: 'T) out -> writeStringEnum union out caseNames shape.GetTag)
            |> w
        else
            let caseSerializers =
                shape.UnionCases |> Array.map (fun c -> Array.map makeMemberVisitor c.Fields)

            Action<_, _>(fun (union: 'T) out -> writeUnion union out caseSerializers shape.GetTag)
            |> w
    | Shape.Enum e ->
        e.Accept
            { new IEnumVisitor<Action<'T, Stream>> with
                member _.Visit<'enum, 'underlying
                    when 'enum: enum<'underlying>
                    and 'enum: struct
                    and 'enum :> ValueType
                    and 'enum: (new: unit -> 'enum)>
                    ()
                    =
                    Action<_, _>(fun (e: 'enum) out -> writeEnum e out) |> w
            }
    | Shape.Tuple(:? ShapeTuple<'T> as shape) ->
        let elementSerializers = shape.Elements |> Array.map makeMemberVisitor

        Action<_, _>(fun (tuple: 'T) out -> writeTuple tuple out elementSerializers)
        |> w
#if NET6_0_OR_GREATER
    | DateOnly -> Action<_, _>(fun date out -> writeDateOnly date out) |> w
    | TimeOnly -> Action<_, _>(fun time out -> writeTimeOnly time out) |> w
#endif
    | BclIsInstanceOfSystemDataSet ->
        Action<_, _>(fun (dataset: System.Data.DataSet) out -> writeDataSet dataset out)
        |> w
    | BclIsInstanceOfSystemDataTable ->
        Action<_, _>(fun (table: System.Data.DataTable) out -> writeDataTable table out)
        |> w
    | _ -> failwithf "Cannot serialize %s." typeof<'T>.Name

// TypeShape requires generic types at compile time, but some applications will only know the types at runtime
// This method bypasses that restriction by emitting IL to construct the delegate on demand
let makeSerializerObj (t: Type) =
    let makeSerializerMi =
        this.GetMethod(nameof (makeSerializer), BindingFlags.Public ||| BindingFlags.Static).MakeGenericMethod t

    let specializedActionType =
        typedefof<Action<_, _>>.MakeGenericType [| t; typeof<Stream> |]

    let instance = Expression.Parameter(typeof<obj>, "instance")
    let stream = Expression.Parameter(typeof<Stream>, "stream")
    let serializer = Expression.Variable specializedActionType

    let expr =
        Expression.Lambda<Func<Action<obj, Stream>>>(
            Expression.Block(
                [ serializer ],
                // create the serializer here
                Expression.Assign(serializer, Expression.Call(makeSerializerMi, [])),
                // return a delegate for serialization with the specialized serializer embedded and cast obj to the right type
                Expression.Lambda<Action<obj, Stream>>(
                    Expression.Invoke(serializer, Expression.Convert(instance, t), stream),
                    [ instance; stream ]
                )
            ),
            []
        )

    expr.Compile().Invoke()

let private serializerCache = ConcurrentDictionary<Type, Action<obj, Stream>>()

let serializeObj (x: obj) (out: Stream) =
    if isNull x then
        writeNil out
    else
        let t = x.GetType()

        match serializerCache.TryGetValue t with
        | true, serializer -> serializer.Invoke(x, out)
        | _ ->
            let serializer = makeSerializerObj t
            serializerCache.[t] <- serializer
            serializer.Invoke(x, out)