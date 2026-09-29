// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting

open System
open System.IO
open System.Collections.Concurrent
open ToolUp.Remoting.MsgPack

// ─── Phase 801 — the differential gate, in the server tier ────────────
//
// Phase 902 moved it here from ToolUp.Platform.Core, where it shipped to
// every Fable consumer behind a `#if !FABLE_COMPILER` guard. Drawing a
// value of an arbitrary type needs `FSharp.Reflection`, and encoding it
// needs the TypeShape-backed writer, which moved here with it; neither
// runs in a browser. Namespaces and module paths are preserved:
// `DecoderShapes` moved whole, and `RemotingDecoders` is split, the
// registration table staying in Core and the gate declared here in a
// module of the same name, which F# resolves as one.
//
// The gate takes the writer it checks against as an argument
// (`verifyWith`, `gateWith`), as the JSON wire's gate takes its oracle;
// `verify` and `gate` are the same against the shipped writer. A
// generated module's `verifyAll` takes a `DecoderGate` (Core), so it
// compiles in a project that references Core alone, and a server-side
// build gate hands it `RemotingDecoders.gate`.

/// Phase 902 — the writer the MessagePack gate encodes each draw with,
/// as a value: the JSON gate's `JsonDecoderOracle.Write`, on this wire.
/// The shipped one is `RemotingDecoders.shippedWriter`; a test can supply
/// its own, to show the gate checks against the writer it is given.
///
/// `Write` may throw, and is applied to a type before any value, so an
/// implementation can build its serializer once per type.
type DecoderWriter = {
    /// A value of the given type to its MessagePack bytes.
    Write: Type -> obj -> byte[]
}

/// Phase 801 — a deterministic, reflection-driven draw of a value of any
/// wire-representable type.
///
/// Reaches exactly the shapes the closed algebra expresses, which is the
/// set a registered decoder can be for; a type outside it is a named
/// `DecoderUndrawable`, never a silently skipped draw. Depth-bounded so a
/// recursive type terminates, and seeded so a divergence is reproducible
/// by its draw index alone.
[<RequireQualifiedAccess>]
module DecoderShapes =

    open System.Reflection
    open Microsoft.FSharp.Reflection

    let private alphabet = [| "a"; "Z"; "0"; " "; "\""; "\\"; "\n"; "é"; "Ω"; "\U0001F600" |]

    let private isGenericOf (definition: Type) (t: Type) =
        t.IsGenericType && t.GetGenericTypeDefinition() = definition

    /// A value of `t`, or the reason none can be drawn. `depth` is spent
    /// on every container so a recursive union bottoms out at a
    /// field-less or leaf case.
    let rec draw (rng: Random) (depth: int) (t: Type) : Result<obj, string> =
        let sub = draw rng (depth - 1)
        let size = if depth <= 0 then 0 else rng.Next 4

        let randomString () =
            String.Join("", Array.init (rng.Next 6) (fun _ -> alphabet[rng.Next alphabet.Length]))

        let collect (element: Type) : Result<obj list, string> =
            let rec go n acc =
                if n = 0 then
                    Ok(List.rev acc)
                else
                    match sub element with
                    | Ok v -> go (n - 1) (v :: acc)
                    | Error e -> Error e

            go size []

        if t = typeof<bool> then
            Ok(box (rng.Next 2 = 1))
        elif t = typeof<unit> then
            Ok(box ())
        elif t = typeof<string> then
            Ok(box (randomString ()))
        elif t = typeof<char> then
            Ok(box (alphabet[rng.Next 4].[0]))
        elif t = typeof<int> then
            Ok(box (rng.Next(Int32.MinValue, Int32.MaxValue)))
        elif t = typeof<int64> then
            Ok(box (rng.NextInt64(Int64.MinValue, Int64.MaxValue)))
        elif t = typeof<int16> then
            Ok(box (int16 (rng.Next(int Int16.MinValue, int Int16.MaxValue + 1))))
        elif t = typeof<sbyte> then
            Ok(box (sbyte (rng.Next(-128, 128))))
        elif t = typeof<byte> then
            Ok(box (byte (rng.Next 256)))
        elif t = typeof<uint16> then
            Ok(box (uint16 (rng.Next 65536)))
        elif t = typeof<uint32> then
            Ok(box (uint32 (rng.NextInt64(0L, 4294967296L))))
        elif t = typeof<uint64> then
            Ok(
                box (
                    uint64 (rng.NextInt64())
                    + (if rng.Next 2 = 0 then 0UL else 9223372036854775808UL)
                )
            )
        elif t = typeof<float> then
            Ok(box (rng.NextDouble() * 2000.0 - 1000.0))
        elif t = typeof<float32> then
            Ok(box (float32 (rng.Next(-1000, 1000)) / 8.0f))
        elif t = typeof<decimal> then
            Ok(box (decimal (rng.Next(-1_000_000, 1_000_000)) / 100m))
        elif t = typeof<Guid> then
            Ok(box (Guid(Array.init 16 (fun _ -> byte (rng.Next 256)))))
        elif t = typeof<TimeSpan> then
            Ok(box (TimeSpan.FromTicks(rng.NextInt64(-864_000_000_000L, 864_000_000_000L))))
        elif t = typeof<DateTime> then
            Ok(box (DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(float (rng.Next 100_000_000))))
        elif t = typeof<DateTimeOffset> then
            Ok(box (DateTimeOffset(DateTime(2000, 1, 1).AddMinutes(float (rng.Next 1_000_000)), TimeSpan.Zero)))
        elif t = typeof<DateOnly> then
            Ok(box (DateOnly.FromDayNumber(rng.Next 700_000)))
        elif t = typeof<TimeOnly> then
            Ok(box (TimeOnly(rng.NextInt64(0L, 864_000_000_000L))))
        elif t = typeof<byte[]> then
            Ok(box (Array.init (size * 3) (fun _ -> byte (rng.Next 256))))
        elif t.IsArray then
            let element = t.GetElementType()

            collect element
            |> Result.map (fun items ->
                let array = Array.CreateInstance(element, items.Length)
                items |> List.iteri (fun i v -> array.SetValue(v, i))
                box array)
        elif isGenericOf typedefof<option<_>> t then
            let inner = t.GetGenericArguments()[0]

            if rng.Next 3 = 0 then
                Ok null
            else
                sub inner
                |> Result.map (fun v ->
                    let case = FSharpType.GetUnionCases(t) |> Array.find (fun c -> c.Name = "Some")
                    FSharpValue.MakeUnion(case, [| v |]))
        elif isGenericOf typedefof<list<_>> t then
            let element = t.GetGenericArguments()[0]

            collect element
            |> Result.map (fun items ->
                let empty =
                    t.GetProperty("Empty", BindingFlags.Public ||| BindingFlags.Static).GetValue null

                let cons = t.GetMethod("Cons", BindingFlags.Public ||| BindingFlags.Static)
                List.foldBack (fun v acc -> cons.Invoke(null, [| v; acc |])) items empty)
        elif isGenericOf typedefof<Set<_>> t then
            let element = t.GetGenericArguments()[0]

            collect element
            |> Result.map (fun items ->
                let listType = typedefof<list<_>>.MakeGenericType element

                let empty =
                    listType.GetProperty("Empty", BindingFlags.Public ||| BindingFlags.Static).GetValue null

                let cons = listType.GetMethod("Cons", BindingFlags.Public ||| BindingFlags.Static)

                let asList =
                    List.foldBack (fun v acc -> cons.Invoke(null, [| v; acc |])) items empty

                let ofList =
                    typeof<Set<_>>.Assembly
                        .GetType("Microsoft.FSharp.Collections.SetModule")
                        .GetMethod("OfList")
                        .MakeGenericMethod
                        element

                ofList.Invoke(null, [| asList |]))
        elif isGenericOf typedefof<Map<_, _>> t then
            let args = t.GetGenericArguments()
            let pairType = FSharpType.MakeTupleType args

            collect pairType
            |> Result.map (fun pairs ->
                let listType = typedefof<list<_>>.MakeGenericType pairType

                let empty =
                    listType.GetProperty("Empty", BindingFlags.Public ||| BindingFlags.Static).GetValue null

                let cons = listType.GetMethod("Cons", BindingFlags.Public ||| BindingFlags.Static)

                let asList =
                    List.foldBack (fun v acc -> cons.Invoke(null, [| v; acc |])) pairs empty

                let ofList =
                    typeof<Map<_, _>>.Assembly
                        .GetType("Microsoft.FSharp.Collections.MapModule")
                        .GetMethod("OfList")
                        .MakeGenericMethod
                        args

                ofList.Invoke(null, [| asList |]))
        elif FSharpType.IsTuple t then
            let elements = FSharpType.GetTupleElements t

            let drawn =
                elements
                |> Array.map sub
                |> Array.fold
                    (fun acc r ->
                        match acc, r with
                        | Ok vs, Ok v -> Ok(v :: vs)
                        | Error e, _
                        | _, Error e -> Error e)
                    (Ok [])

            drawn
            |> Result.map (fun vs -> FSharpValue.MakeTuple(vs |> List.rev |> List.toArray, t))
        elif FSharpType.IsRecord(t, true) then
            let fields = FSharpType.GetRecordFields(t, true)

            let drawn =
                fields
                |> Array.map (fun f -> sub f.PropertyType)
                |> Array.fold
                    (fun acc r ->
                        match acc, r with
                        | Ok vs, Ok v -> Ok(v :: vs)
                        | Error e, _
                        | _, Error e -> Error e)
                    (Ok [])

            drawn
            |> Result.map (fun vs -> FSharpValue.MakeRecord(t, vs |> List.rev |> List.toArray, true))
        elif FSharpType.IsUnion(t, true) then
            let cases = FSharpType.GetUnionCases(t, true)

            // Out of depth, prefer a case that carries no fields so a
            // recursive union terminates; if there is none, the sub-draws
            // bottom out on their own leaves.
            let candidates =
                if depth <= 0 then
                    match cases |> Array.filter (fun c -> c.GetFields().Length = 0) with
                    | [||] -> cases
                    | leaves -> leaves
                else
                    cases

            let case = candidates[rng.Next candidates.Length]

            let drawn =
                case.GetFields()
                |> Array.map (fun f -> sub f.PropertyType)
                |> Array.fold
                    (fun acc r ->
                        match acc, r with
                        | Ok vs, Ok v -> Ok(v :: vs)
                        | Error e, _
                        | _, Error e -> Error e)
                    (Ok [])

            drawn
            |> Result.map (fun vs -> FSharpValue.MakeUnion(case, vs |> List.rev |> List.toArray, true))
        else
            Error(sprintf "%s is not a shape the closed algebra expresses, so no draw of it can be compared" t.FullName)

    /// The default draw depth: enough for a nested record holding a list
    /// of unions holding a tuple, and for a recursive union to branch
    /// three times before it bottoms out.
    [<Literal>]
    let DefaultDepth = 4

/// Phase 801 — the differential gate (Phase 902: the server-tier half of
/// `RemotingDecoders`; the registration table is in ToolUp.Platform.Core).
[<RequireQualifiedAccess>]
module RemotingDecoders =

    /// Phase 801 — one decode, rendered for comparison: outcome class,
    /// value and runtime type at once (the `ProofOracleTests` shape), so
    /// a value that is equal-looking at every representation but its
    /// type is a disagreement. Never throws: a throw from either side is
    /// a finding the gate reports, not a crash it takes.
    ///
    /// Phase 840 — `internal` rather than private so the JSON wire's gate
    /// (`JsonDecoders.verifyWith`) renders its two decodes in exactly
    /// this shape: one rendering, so "the same" means the same thing on
    /// both wires.
    let internal renderDecode (decode: unit -> Result<obj, DecodeError>) : string =
        try
            match decode () with
            | Ok value ->
                let runtime = if isNull value then "null" else value.GetType().FullName
                sprintf "Ok %A : %s" value runtime
            | Error error ->
                sprintf
                    "Error expected=%s found=%s path=%s"
                    error.Expected
                    error.Found
                    (DecodeError.renderPath error.Path)
        with ex ->
            sprintf "THREW %s: %s" (ex.GetType().Name) ex.Message

    /// Phase 902 — the shipped writer: the TypeShape-backed
    /// `Write.makeSerializer` for the given type, built once per type and
    /// cached, as the client's binary-response path writes it.
    let shippedWriter: DecoderWriter =
        let serializers = ConcurrentDictionary<Type, Action<obj, Stream>>()

        {
            Write =
                fun (target: Type) ->
                    let serializer = serializers.GetOrAdd(target, Write.makeSerializerObj)

                    fun (value: obj) ->
                        use buffer = new MemoryStream()
                        serializer.Invoke(value, buffer)
                        buffer.ToArray()
        }

    /// Phase 801 — the differential gate. Draw `draws` values of `target`
    /// (deterministically, from `seed`), encode each with `writer`,
    /// decode the bytes through `decoder` AND through the reflection
    /// reader, and compare outcome class, value and runtime type. The
    /// first disagreement is a typed refusal naming the type and the
    /// draw; a type the shape generator cannot draw is a refusal too,
    /// because a gate that could not run is not a gate that passed.
    ///
    /// The candidate's decode goes through the same one-pass
    /// bytes-to-`Value` read the client's binary response path uses, so
    /// what is compared is the decode a deployment would actually see.
    ///
    /// Phase 902 — the erased form over the writer it checks against, the
    /// MessagePack twin of `JsonDecoders.verifyByTypeWith`: what
    /// `gateWith` wraps. `verifyWith` is the typed form.
    let verifyByTypeWith
        (writer: DecoderWriter)
        (draws: int)
        (seed: int)
        (target: Type)
        (decoder: RegisteredDecoder)
        : Result<DecoderVerification, DecoderRefusal> =
        let wireType = RemotingDecoders.keyFor target
        let rng = Random seed

        // Built lazily: the shipped writer refuses a type it cannot
        // serialise (`obj` is the first) by throwing, and that refusal
        // belongs in the gate's own vocabulary rather than on the stack.
        let write = lazy (writer.Write target)

        let record divergence = {
            WireType = wireType
            Draws = draws
            Seed = seed
            Divergence = divergence
        }

        let rec go (i: int) =
            if i >= draws then
                Ok(record None)
            else
                match DecoderShapes.draw rng DecoderShapes.DefaultDepth target with
                | Error reason -> Error(DecoderUndrawable(wireType, reason))
                | Ok drawn ->
                    let bytes = write.Value drawn

                    let candidate =
                        renderDecode (fun () -> Read.Reader(bytes).TryReadValue() |> Result.bind decoder)

                    let reflection = renderDecode (fun () -> Read.Reader(bytes).TryRead target)

                    if candidate = reflection then
                        go (i + 1)
                    else
                        Error(
                            DecoderDiverges(
                                record (
                                    Some {
                                        Draw = i
                                        Candidate = candidate
                                        Reflection = reflection
                                    }
                                )
                            )
                        )

        go 0

    /// Phase 902 — `verifyByTypeWith` for `'T`: the gate over a typed
    /// decoder, draws taken of `'T` itself, checked against `writer`.
    let verifyWith<'T>
        (writer: DecoderWriter)
        (draws: int)
        (seed: int)
        (decoder: Decoder<'T>)
        : Result<DecoderVerification, DecoderRefusal> =
        verifyByTypeWith writer draws seed typeof<'T> (fun value -> decoder value |> Result.map box)

    /// Phase 801 — `verifyWith` against the shipped writer: the gate as
    /// every deployment's client writes the bytes. Phase 902 — the
    /// writer-free form exists only here, where the writer does.
    let verify<'T> (draws: int) (seed: int) (decoder: Decoder<'T>) : Result<DecoderVerification, DecoderRefusal> =
        verifyWith<'T> shippedWriter draws seed decoder

    /// Phase 902 — the gate over `writer`, as the value a generated
    /// module's `verifyAll` takes.
    let gateWith (writer: DecoderWriter) : DecoderGate = verifyByTypeWith writer

    /// Phase 902 — the gate against the shipped writer: what a build gate
    /// hands a generated module's `verifyAll` / `registerAllVerified`.
    let gate: DecoderGate = gateWith shippedWriter

    /// The draw count `registerVerified` uses: enough that every union
    /// case and every option arm of a typical platform record is drawn
    /// several times, cheap enough to run at boot.
    [<Literal>]
    let DefaultDraws = 64

    /// The seed `registerVerified` uses. Fixed, so a refusal at boot is
    /// reproducible from its draw index alone.
    [<Literal>]
    let DefaultSeed = 801

    /// Phase 801 — register the algebra decoder for `'T` ONLY if it
    /// agrees with the reflection reader over `DefaultDraws` draws of
    /// `'T`. A disagreement refuses the registration and returns why;
    /// the table is untouched, so the type keeps the reflection path it
    /// had. The boot-time form of the gate, for a hand-written decoder a
    /// composition root registers; a generated module's `registerAll`
    /// registers decoders its own `verifyAll` (and the SDK's pack, for
    /// the platform set) has already held to the same comparison.
    let registerVerified<'T> (decoder: Decoder<'T>) : Result<DecoderVerification, DecoderRefusal> =
        verify<'T> DefaultDraws DefaultSeed decoder
        |> Result.map (fun verification ->
            RemotingDecoders.registerByKey (RemotingDecoders.keyFor typeof<'T>) (fun value ->
                decoder value |> Result.map box)

            verification)