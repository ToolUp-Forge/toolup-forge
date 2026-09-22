// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting

open System
open System.IO
open ToolUp.Remoting.MsgPack

// ─── Phase 785 — the opt-in seam ─────────────────────────────────────
//
// The algebra is an OPT-IN path, never a replacement. A type with a
// registered decoder decodes through `Decode`'s total combinators; a
// type without one decodes through the reflection reader exactly as it
// did before (GP 11) — so adopting the SDK version carrying this phase
// changes no deployment's behaviour until it registers something.
//
// **Why this is not `GeneratedDispatchRegistry`, which Phase 785's
// shard proposed.** That registry is keyed by API-RECORD type and holds
// `('TContext -> 'TImpl -> Task) list` route handlers for the server
// dispatcher; this one is keyed by WIRE type and holds a decoder. They
// are a different key and a different payload. More decisively, the
// server dispatcher is not on the MsgPack path at all: `Read.Reader`
// has exactly one production call site in the tree — the client's
// binary-response decode (`Client/Remoting/Remoting.fs`) — because
// server arguments arrive as `Choice<byte[], JsonElement>` and decode
// through System.Text.Json. A MsgPack decoder registered for a server
// dispatch branch would never be consulted. The opt-in branch is
// therefore wired where the bytes actually are, and
// `GeneratedDispatchRegistry` is left exactly as Phase 69k shipped it.
//
// **Keyed by `Type.FullName`, and the failure mode is the safe one.**
// The client's response serializer holds a `System.Type` and nothing
// finer, and `FullName` is the one identity both hosts can compute
// (the reflection reader's own Fable branch already resolves types that
// way). A key that does not match — a generic instantiation Fable
// renders differently, a type from a rewritten assembly — resolves to
// `None`, which is the reflection path: a miss costs the algebra, never
// correctness.

/// Phase 785 — a registered decoder, erased to `obj` so one table holds
/// decoders for many types.
///
/// The erasure is the eighth boundary of the kind `CLAUDE.md`
/// enumerates, and it is symmetric in the same way as the others: the
/// only way to put a decoder in is `register<'T>`, which boxes the
/// result of a `Decoder<'T>`, and the only way to get one out is a
/// lookup by that same `'T`'s identity. Nothing casts blind.
type RegisteredDecoder = Value -> Result<obj, DecodeError>

#if !FABLE_COMPILER
// ─── Phase 801 — the differential gate ───────────────────────────────
//
// A registered decoder WINS over reflection, so a wrong one is worse
// than none: it decodes differently from what every deployment ran
// before, silently, for exactly the records someone cared enough about
// to opt in. What makes opting in safe is running the candidate beside
// the reflection reader over draws of the record's own shape BEFORE it
// is registered, and refusing by name on the first disagreement.
//
// .NET only, and structurally so: drawing a value of an arbitrary type
// and encoding it with the shipped writer both need `FSharp.Reflection`
// and `Write.makeSerializer`, neither of which the Fable host has. The
// browser registers decoders that were verified elsewhere — by the
// SDK's own test pack for the generated platform set, or by a
// consumer's build gate calling its generated module's `verifyAll`.

/// Phase 801 — how one draw diverged. `Candidate` and `Reflection` are
/// the two decodes rendered the way `ProofOracleTests` renders them:
/// outcome class, value and runtime type at once, so a width narrowed on
/// the way back — equal-looking at every representation but its type —
/// is a disagreement and not a pass.
type DecoderDivergence = {
    /// The zero-based draw that diverged; every earlier draw agreed.
    Draw: int
    /// The candidate's decode of the draw's bytes.
    Candidate: string
    /// The reflection reader's decode of the same bytes.
    Reflection: string
}

/// Phase 801 — the record of one verification: which type, how many
/// draws, under which seed, and the first divergence if there was one.
type DecoderVerification = {
    /// `RemotingDecoders.keyFor` of the verified type.
    WireType: string
    /// How many draws were compared.
    Draws: int
    /// The seed the draws were taken from, so a refusal is reproducible.
    Seed: int
    /// `None` when every draw agreed; the first disagreement otherwise.
    Divergence: DecoderDivergence option
}

/// Phase 801 — a typed refusal: the registration did not happen, and
/// this is why, naming the record and the first diverging draw.
type DecoderRefusal =
    /// The candidate and the reflection reader disagreed.
    | DecoderDiverges of verification: DecoderVerification
    /// No draw of the type could be produced, so nothing was compared —
    /// a type the shape generator does not reach. Named rather than
    /// passed: a gate that could not run is not a gate that passed.
    | DecoderUndrawable of wireType: string * reason: string

/// Renders a refusal for a boot log or a test failure.
[<RequireQualifiedAccess>]
module DecoderRefusal =
    /// One line naming the type and, for a divergence, the draw and both
    /// decodes; for an undrawable type, why it could not be drawn.
    let describe (refusal: DecoderRefusal) : string =
        match refusal with
        | DecoderDiverges v ->
            match v.Divergence with
            | Some d ->
                sprintf
                    "decoder for %s refused: draw %d of %d (seed %d) decodes differently from the reflection reader — candidate: %s; reflection: %s"
                    v.WireType
                    d.Draw
                    v.Draws
                    v.Seed
                    d.Candidate
                    d.Reflection
            | None -> sprintf "decoder for %s refused (no divergence recorded)" v.WireType
        | DecoderUndrawable(wireType, reason) ->
            sprintf "decoder for %s refused: no draw of the type could be produced — %s" wireType reason

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
#endif

/// Phase 785 — the process-wide table of algebra decoders.
///
/// **Module-level mutable state, justified.** The table is written once
/// per process during composition and read on every response decode
/// afterwards. It is an immutable `Map` behind a single mutable
/// binding, so a read is lock-free and always sees a whole table rather
/// than a half-updated one: registration replaces the binding with a
/// new map, and a reference assignment is atomic. That is the same
/// once-at-startup shape `GeneratedDispatchRegistry` uses, in the form
/// `Platform.Core` can carry — `ConcurrentDictionary` is not available
/// under Fable, and this file ships under `fable/` (GP 10).
[<RequireQualifiedAccess>]
module RemotingDecoders =

    let mutable private table: Map<string, RegisteredDecoder> = Map.empty

    /// The table's key. Deliberately one function rather than an
    /// expression at each call site: "what identifies a wire type here"
    /// must have exactly one answer, or a registration and a lookup can
    /// disagree while both look right.
    ///
    /// Public rather than private because `register` below is `inline`
    /// and an inline function may not reach a private binding.
    let keyFor (wireType: Type) : string =
        let name = wireType.FullName
        if isNull name then wireType.Name else name

    /// Register an already-erased decoder under a key.
    ///
    /// The non-inline half of `register`, and the only writer of the
    /// table. It exists so `register` can be `inline` without the table
    /// or the key function having to be public state: an inline function
    /// may not reach a private binding, and the mutable table must stay
    /// one.
    let registerByKey (key: string) (decoder: RegisteredDecoder) : unit = table <- Map.add key decoder table

    /// Register the algebra decoder for `'T`. Idempotent — a second
    /// registration for the same type replaces the first, which is what
    /// a hot-reload or a re-run of a composition root has to mean.
    ///
    /// **`inline`, and it has to be.** Fable erases generics at runtime,
    /// so `typeof<'T>` inside a non-inline generic function has no type
    /// to report — the compiler refuses it outright ("Cannot get type
    /// info of generic parameter T"). Inlining resolves `'T` at each call
    /// site, which is the same fix `WireCorpus.case` needed for the same
    /// reason. `verify.ps1` has no Fable leg, so this is caught only by
    /// compiling the client tier.
    let inline register<'T> (decoder: Decoder<'T>) : unit =
        registerByKey (keyFor typeof<'T>) (fun value -> decoder value |> Result.map box)

    /// The algebra decoder for `wireType`, or `None` — which means the
    /// reflection path, not an error.
    let tryGet (wireType: Type) : RegisteredDecoder option = Map.tryFind (keyFor wireType) table

    /// Whether `wireType` decodes through the algebra in this process.
    let isRegistered (wireType: Type) : bool = Map.containsKey (keyFor wireType) table

    /// Every registered type's key, ordered. The enumeration the Phase
    /// 785 composition-profile facet classifies an API record against.
    let registered () : string list = table |> Map.toList |> List.map fst

    /// How many types decode through the algebra in this process.
    let count () : int = Map.count table

    /// Test-only: empty the table. Production code never calls this —
    /// registration is once-per-process at composition.
    ///
    /// Public rather than `internal` because `Platform.Core` declares
    /// exactly one friend assembly (`Platform.Server`, for the MsgPack
    /// `TypeShape` module) and the test pack is not it. The name is the
    /// guard: a member called `resetForTests` in a shipped API is a
    /// statement about who may call it that no access modifier available
    /// here could make instead.
    let resetForTests () : unit = table <- Map.empty

#if !FABLE_COMPILER
    /// Phase 801 — one decode, rendered for comparison: outcome class,
    /// value and runtime type at once (the `ProofOracleTests` shape), so
    /// a value that is equal-looking at every representation but its
    /// type is a disagreement. Never throws: a throw from either side is
    /// a finding the gate reports, not a crash it takes.
    let private renderDecode (decode: unit -> Result<obj, DecodeError>) : string =
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

    /// Phase 801 — the differential gate. Draw `draws` values of `'T`
    /// (deterministically, from `seed`), encode each with the shipped
    /// writer, decode the bytes through `decoder` AND through the
    /// reflection reader, and compare outcome class, value and runtime
    /// type. The first disagreement is a typed refusal naming the type
    /// and the draw; a type the shape generator cannot draw is a refusal
    /// too, because a gate that could not run is not a gate that passed.
    ///
    /// The candidate's decode goes through the same one-pass
    /// bytes-to-`Value` read the client's binary response path uses, so
    /// what is compared is the decode a deployment would actually see.
    let verify<'T> (draws: int) (seed: int) (decoder: Decoder<'T>) : Result<DecoderVerification, DecoderRefusal> =
        let wireType = keyFor typeof<'T>
        let rng = Random seed

        // Built lazily: the shipped writer refuses a type it cannot
        // serialise (`obj` is the first) by throwing, and that refusal
        // belongs in the gate's own vocabulary rather than on the stack.
        let serializer = lazy (Write.makeSerializer<'T> ())

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
                match DecoderShapes.draw rng DecoderShapes.DefaultDepth typeof<'T> with
                | Error reason -> Error(DecoderUndrawable(wireType, reason))
                | Ok drawn ->
                    let bytes =
                        use buffer = new MemoryStream()
                        serializer.Value.Invoke(unbox<'T> drawn, buffer)
                        buffer.ToArray()

                    let candidate =
                        renderDecode (fun () ->
                            Read.Reader(bytes).TryReadValue() |> Result.bind decoder |> Result.map box)

                    let reflection = renderDecode (fun () -> Read.Reader(bytes).TryRead typeof<'T>)

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
            registerByKey (keyFor typeof<'T>) (fun value -> decoder value |> Result.map box)
            verification)
#endif