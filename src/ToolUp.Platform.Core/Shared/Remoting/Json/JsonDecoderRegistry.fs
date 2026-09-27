// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Json

open System
open ToolUp.Remoting

/// Phase 799 — a JSON decoder erased to `obj`, as the registry holds it.
type RegisteredJsonDecoder = JsonValue -> Result<obj, DecodeError>

#if !FABLE_COMPILER
/// Phase 840 — a place the oracle is KNOWN to lose information the wire
/// carries, declared rather than discovered: the JSON twin of 799's
/// refuse-path table, for the accept path. A value of `Type` anywhere in
/// a drawn value's shape may come back through the oracle altered where
/// the algebra reads it exactly.
type JsonOracleLoss = {
    /// The CLR type the oracle reads lossily, matched anywhere in the
    /// verified type's shape (a record field, a union case field, an
    /// option, a collection element).
    Type: Type
    /// Why, for a reader of a verification record.
    Reason: string
}

/// Phase 840 — what the JSON wire's registration gate compares a
/// candidate decoder against.
///
/// On the MessagePack wire the oracle is the reflection reader, which
/// lives beside the registry in this assembly. On this wire it is the
/// System.Text.Json converter set — the very thing a registered decoder
/// replaces — and that set is composed in `Platform.Server`, which this
/// assembly cannot reference. So the gate takes its oracle as a value:
/// the server supplies the shipped one (`FableConverters.decoderOracle`),
/// and a test can supply its own.
///
/// `Write`, `Read` and `Decode` may throw; the gate turns a throw into a
/// finding in its own vocabulary rather than letting it escape.
type JsonDecoderOracle = {
    /// The shipped writer: a value of the given type to its JSON text,
    /// exactly as a client writes an argument.
    Write: Type -> obj -> string
    /// The text-to-value-model pass the argument seam decodes through —
    /// the candidate decoder reads what this produces.
    Read: string -> Result<JsonValue, DecodeError>
    /// The oracle's own typed decode of the same text. Must bypass the
    /// decoder table: consulting it would compare a candidate with
    /// itself.
    Decode: Type -> string -> Result<obj, DecodeError>
    /// Where `Decode` is known to lose what the writer wrote. Empty means
    /// every difference from the oracle is a divergence.
    Losses: JsonOracleLoss list
}

/// Phase 840 — one draw on which the candidate and the oracle differed
/// ONLY by a declared loss: the candidate recovered the written value
/// exactly, the oracle did not, and the verified type's shape reaches the
/// loss's type. Said, never refused, and never silently passed.
type JsonDeclaredDifference = {
    /// The zero-based draw.
    Draw: int
    /// The declared loss the difference is attributed to.
    Loss: JsonOracleLoss
    /// The candidate's decode — equal to the written value.
    Candidate: string
    /// The oracle's decode.
    Oracle: string
}

/// Phase 840 — the JSON gate's record of an agreeing verification: the
/// shared `DecoderVerification` (its `Divergence` is `None`), beside every
/// draw that differed from the oracle only by a declared loss.
type JsonDecoderVerification = {
    /// Which type, how many draws, under which seed.
    Verification: DecoderVerification
    /// The draws the oracle's declared losses account for, in draw order.
    DeclaredDifferences: JsonDeclaredDifference list
}
#endif

/// Phase 799 — the JSON twin of `RemotingDecoders`: the process-wide
/// table of algebra decoders the server's ARGUMENT seam consults before
/// falling back to System.Text.Json's typed deserialise.
///
/// Same shape, same rules, same key (`RemotingDecoders.keyFor`, so a
/// type is identified identically on both wires): registration is
/// explicit and idempotent; a miss is the STJ path, never an error; the
/// table is written at composition and read per request. One table per
/// wire rather than one table with a wire axis, because the two decoders
/// for a type are different functions over different value models and
/// nothing ever asks for "the decoder for `'T`" without knowing which
/// wire it is standing on.
///
/// Phase 839 — the key widens from a bare wire-type key to
/// `(API record name option) * (wire-type key)`. `register` keeps
/// registering UNSCOPED (record = `None`), exactly as before this
/// phase — so nothing registered before Phase 839 changes meaning.
/// `registerFor` registers a decoder scoped to one named API record;
/// the seam (`tryGet`) tries the record-scoped key first and falls
/// back to the unscoped one, so a caller with no record to name (a
/// test, a non-dispatch path) passes `None` and gets exactly the
/// bare-type lookup it always had.
[<RequireQualifiedAccess>]
module JsonDecoders =

    let mutable private table: Map<string option * string, RegisteredJsonDecoder> =
        Map.empty

    /// Register an already-erased decoder under a wire-type key, scoped
    /// to `recordName` (`None` = unscoped, consulted for every record).
    /// The non-inline half of `register` / `registerFor`, and the only
    /// writer of the table.
    let registerByKey (recordName: string option) (key: string) (decoder: RegisteredJsonDecoder) : unit =
        table <- Map.add (recordName, key) decoder table

    /// Register the JSON algebra decoder for `'T`, UNSCOPED — consulted
    /// for every API record's arguments of this type, exactly as before
    /// Phase 839. Idempotent. `inline` for the reason
    /// `RemotingDecoders.register` is: Fable erases generics, so
    /// `typeof<'T>` must resolve at the call site.
    let inline register<'T> (decoder: JsonDecoder<'T>) : unit =
        registerByKey None (RemotingDecoders.keyFor typeof<'T>) (fun value -> decoder value |> Result.map box)

    /// Phase 839 — register the JSON algebra decoder for `'T`, scoped to
    /// arguments of the named API record ONLY. A registration for
    /// `(Some "IPresenceApi", key)` is never consulted for another
    /// record's arguments of the same type — a strict decoder registered
    /// for one record does not leak into another's arguments of the same
    /// wire type, which is the isolation this phase exists to establish.
    /// Idempotent, `inline` for the same reason `register` is.
    let inline registerFor<'T> (recordName: string) (decoder: JsonDecoder<'T>) : unit =
        registerByKey (Some recordName) (RemotingDecoders.keyFor typeof<'T>) (fun value ->
            decoder value |> Result.map box)

    /// The JSON decoder for `wireType`, scoped to `recordName` — the
    /// record-scoped registration first, then the unscoped (bare-type)
    /// one, or `None` for the STJ path. `recordName = None` looks up
    /// only the unscoped registration: exactly the pre-839 lookup.
    let tryGet (recordName: string option) (wireType: Type) : RegisteredJsonDecoder option =
        let key = RemotingDecoders.keyFor wireType

        match recordName with
        | Some _ ->
            match Map.tryFind (recordName, key) table with
            | Some decoder -> Some decoder
            | None -> Map.tryFind (None, key) table
        | None -> Map.tryFind (None, key) table

    /// Whether `wireType` decodes through the JSON algebra for
    /// `recordName` (record-scoped or the unscoped fallback) in this
    /// process.
    let isRegistered (recordName: string option) (wireType: Type) : bool = (tryGet recordName wireType).IsSome

    /// Every registered `(record, type)` key, ordered. `fst` is `None`
    /// for an unscoped registration.
    let registered () : (string option * string) list = table |> Map.toList |> List.map fst

    /// How many `(record, type)` pairs decode through the JSON algebra
    /// in this process.
    let count () : int = Map.count table

    /// Test-only: empty the table. See `RemotingDecoders.resetForTests`
    /// for why this is public.
    let resetForTests () : unit = table <- Map.empty

#if !FABLE_COMPILER
    // ─── Phase 840 — the registration gate ───────────────────────────
    //
    // Phase 801's rule, on this wire: a registered decoder WINS over the
    // converter set, so a wrong one is worse than none — it accepts the
    // text and yields a well-typed value that is not what the client
    // sent. Nothing is registered through `registerVerifiedWith` until it
    // has been run beside the oracle over seeded draws of its own type.
    //
    // **Where the two decoders may differ, and how each difference is
    // said rather than papered over.** Two kinds, both declared:
    //
    //   1. STRICTNESS — the converter set is more lenient than the
    //      algebra: a quoted number at a width the writer emits bare, an
    //      absent member read as null, `null` for a record. Every draw is
    //      written by the SHIPPED writer, which never produces those texts
    //      (it quotes only the widths whose algebra arms read the quoted
    //      form, writes every member with `None` as `null`, and never
    //      writes `null` for a record), so strictness is unreachable from
    //      a draw and needs no exemption here. It is declared where it is
    //      exercised: the refuse-path table in the test pack
    //      (`JsonDecoderAlgebraTests.algebraOutcomes`).
    //   2. ORACLE LOSS — the converter set reads a value the writer wrote
    //      exactly into something else (its `TimeSpan` read goes through
    //      a double and can land a tick short, Phase 784; the algebra
    //      reads the millisecond text exactly, 785.F). Reachable from a
    //      draw, so the oracle DECLARES it (`JsonDecoderOracle.Losses`),
    //      and a draw on which the candidate recovers the written value
    //      exactly, the oracle does not, and the type reaches a declared
    //      loss is recorded as a `JsonDeclaredDifference` — said in the
    //      verification record, not refused and not silently passed. A
    //      candidate that differs from the written value is refused
    //      whatever the oracle declares.
    //
    // .NET only, for the MessagePack gate's reason: drawing a value of an
    // arbitrary type needs `FSharp.Reflection`, and the oracle is .NET's.
    // The Fable client registers with plain `register` / `registerFor`,
    // what a build gate already verified.

    /// Whether `target`'s shape reaches `loss` anywhere: itself, an array
    /// element, a generic argument (option, list, map, set, …), a record
    /// field, a union case field or a tuple element. Cycle-safe.
    let private reaches (loss: Type) (target: Type) : bool =
        let seen = System.Collections.Generic.HashSet<Type>()

        let rec go (t: Type) =
            if t = loss then
                true
            elif isNull t || not (seen.Add t) then
                false
            else
                let flags =
                    System.Reflection.BindingFlags.Public
                    ||| System.Reflection.BindingFlags.NonPublic

                let children = [
                    if t.IsArray then
                        yield t.GetElementType()
                    if t.IsGenericType then
                        yield! t.GetGenericArguments()
                    if FSharp.Reflection.FSharpType.IsRecord(t, flags) then
                        for f in FSharp.Reflection.FSharpType.GetRecordFields(t, flags) do
                            yield f.PropertyType
                    if FSharp.Reflection.FSharpType.IsUnion(t, flags) then
                        for case in FSharp.Reflection.FSharpType.GetUnionCases(t, flags) do
                            for f in case.GetFields() do
                                yield f.PropertyType
                ]

                List.exists go children

        go target

    /// Phase 840 — the differential gate for the JSON wire. Draw `draws`
    /// values of `target` (`DecoderShapes.draw`, deterministically from
    /// `seed`), write each with `oracle.Write`, then decode the text
    /// through the candidate (`oracle.Read`, then `decoder`) AND through
    /// `oracle.Decode`, comparing outcome class, value and runtime type
    /// (`RemotingDecoders.renderDecode`, the MessagePack gate's rendering).
    ///
    /// The first disagreement not accounted for by a declared oracle loss
    /// (see the block comment above) is `DecoderDiverges` naming the type
    /// and the draw; on this wire the divergence's `Reflection` member
    /// carries the ORACLE's decode (the record is shared with the
    /// MessagePack gate so one refusal vocabulary serves both wires). A
    /// type that cannot be drawn, or a draw the writer refuses, is
    /// `DecoderUndrawable` — a gate that could not run is not a gate that
    /// passed.
    ///
    /// The erased form, over a `Type` and an already-erased decoder — what
    /// a table-shaped caller (a test pack verifying a list of
    /// registrations) can hold. `verifyWith` is the typed form.
    let verifyByTypeWith
        (oracle: JsonDecoderOracle)
        (draws: int)
        (seed: int)
        (target: Type)
        (decoder: RegisteredJsonDecoder)
        : Result<JsonDecoderVerification, DecoderRefusal> =
        let wireType = RemotingDecoders.keyFor target
        let rng = Random seed
        let reachable = oracle.Losses |> List.filter (fun loss -> reaches loss.Type target)

        let record divergence = {
            WireType = wireType
            Draws = draws
            Seed = seed
            Divergence = divergence
        }

        let rec go (i: int) (declared: JsonDeclaredDifference list) =
            if i >= draws then
                Ok {
                    Verification = record None
                    DeclaredDifferences = List.rev declared
                }
            else
                match DecoderShapes.draw rng DecoderShapes.DefaultDepth target with
                | Error reason -> Error(DecoderUndrawable(wireType, reason))
                | Ok drawn ->
                    // The writer may refuse a shape by throwing: that
                    // refusal belongs in the gate's vocabulary, not on the
                    // stack — 801's lazy-writer lesson, applied per draw.
                    let written =
                        try
                            Ok(oracle.Write target drawn)
                        with ex ->
                            Error(sprintf "the shipped writer refused a draw: %s: %s" (ex.GetType().Name) ex.Message)

                    match written with
                    | Error reason -> Error(DecoderUndrawable(wireType, reason))
                    | Ok text ->
                        let candidate =
                            RemotingDecoders.renderDecode (fun () -> oracle.Read text |> Result.bind decoder)

                        let reference = RemotingDecoders.renderDecode (fun () -> oracle.Decode target text)

                        if candidate = reference then
                            go (i + 1) declared
                        else
                            let original = RemotingDecoders.renderDecode (fun () -> Ok drawn)

                            match reachable with
                            | loss :: _ when candidate = original && reference <> original ->
                                go
                                    (i + 1)
                                    ({
                                        Draw = i
                                        Loss = loss
                                        Candidate = candidate
                                        Oracle = reference
                                     }
                                     :: declared)
                            | _ ->
                                Error(
                                    DecoderDiverges(
                                        record (
                                            Some {
                                                Draw = i
                                                Candidate = candidate
                                                Reflection = reference
                                            }
                                        )
                                    )
                                )

        go 0 []

    /// Phase 840 — `verifyByTypeWith` for `'T`: the gate over a typed
    /// decoder, draws taken of `'T` itself.
    let verifyWith<'T>
        (oracle: JsonDecoderOracle)
        (draws: int)
        (seed: int)
        (decoder: JsonDecoder<'T>)
        : Result<JsonDecoderVerification, DecoderRefusal> =
        verifyByTypeWith oracle draws seed typeof<'T> (fun value -> decoder value |> Result.map box)

    /// Phase 840 — a refusal rendered for a boot log or a test failure,
    /// naming the ORACLE this wire compares against (the shared
    /// `DecoderRefusal.describe` names the MessagePack wire's reflection
    /// reader). Names the type and, for a divergence, the draw and both
    /// decodes.
    let describeRefusal (refusal: DecoderRefusal) : string =
        match refusal with
        | DecoderDiverges v ->
            match v.Divergence with
            | Some d ->
                sprintf
                    "JSON decoder for %s refused: draw %d of %d (seed %d) decodes differently from the STJ converter set — candidate: %s; converter set: %s"
                    v.WireType
                    d.Draw
                    v.Draws
                    v.Seed
                    d.Candidate
                    d.Reflection
            | None -> sprintf "JSON decoder for %s refused (no divergence recorded)" v.WireType
        | DecoderUndrawable(wireType, reason) ->
            sprintf "JSON decoder for %s refused: no draw of the type could be produced — %s" wireType reason

    /// Phase 840 — register the JSON decoder for `'T`, UNSCOPED, ONLY if
    /// it agrees with `oracle` over `RemotingDecoders.DefaultDraws` draws
    /// (seed `RemotingDecoders.DefaultSeed` — the MessagePack gate's
    /// pair). A refusal leaves the table untouched, so the type keeps the
    /// converter-set path it had. Plain `register` stays unverified, as it
    /// does on the MessagePack wire: the Fable client has no oracle and
    /// registers what a build gate verified.
    let registerVerifiedWith<'T>
        (oracle: JsonDecoderOracle)
        (decoder: JsonDecoder<'T>)
        : Result<JsonDecoderVerification, DecoderRefusal> =
        verifyWith<'T> oracle RemotingDecoders.DefaultDraws RemotingDecoders.DefaultSeed decoder
        |> Result.map (fun verification ->
            registerByKey None (RemotingDecoders.keyFor typeof<'T>) (fun value -> decoder value |> Result.map box)
            verification)

    /// Phase 840 — the record-scoped form of `registerVerifiedWith`: the
    /// verified twin of `registerFor`, registering for `recordName`'s
    /// arguments only, and only on agreement.
    let registerVerifiedForWith<'T>
        (oracle: JsonDecoderOracle)
        (recordName: string)
        (decoder: JsonDecoder<'T>)
        : Result<JsonDecoderVerification, DecoderRefusal> =
        verifyWith<'T> oracle RemotingDecoders.DefaultDraws RemotingDecoders.DefaultSeed decoder
        |> Result.map (fun verification ->
            registerByKey (Some recordName) (RemotingDecoders.keyFor typeof<'T>) (fun value ->
                decoder value |> Result.map box)

            verification)
#endif