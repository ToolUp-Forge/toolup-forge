// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Json

open System
open ToolUp.Remoting

/// Phase 799 — a JSON decoder erased to `obj`, as the registry holds it.
type RegisteredJsonDecoder = JsonValue -> Result<obj, DecodeError>


// Phase 902 — the JSON gate RUNS on .NET only (a reflective draw, the
// server's converter set, a .NET mirror of the browser's writer), so it
// lives in ToolUp.Platform.Server: `JsonDecoderOracle`, `BrowserJsonWriter`
// and the gate half of `JsonDecoders` (`verifyWith`, `verifyBothWith`,
// `gateWith`, …), namespaces preserved. This tier keeps the gate's
// vocabulary — the records it returns and `JsonDecoderGate`, the gate as a
// value a generated module's `verifyAll` takes.

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


/// Phase 902 — the JSON wire's differential gate, as a value: what a
/// generated module's `verifyAll` runs each of its decoders through.
///
/// The gate draws values of a type by reflection and writes them with the
/// server's and the browser's writers, which only .NET can do, so this
/// tier declares its shape and the server tier supplies it:
/// `JsonDecoders.gateWith oracle` (ToolUp.Platform.Server) runs Phase 885's
/// two-writer gate against `oracle`. Erased over the type, like
/// `RegisteredJsonDecoder`, so one value serves every decoder a module
/// registers: it compares an erased candidate with the oracle over `draws`
/// draws (first argument) of the given type, taken from `seed` (second
/// argument). A function type, for `DecoderGate`'s reason.
type JsonDecoderGate = int -> int -> Type -> RegisteredJsonDecoder -> Result<JsonDecoderVerification, DecoderRefusal>


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


    /// Phase 902 — run `gate` over `'T`'s decoder: `draws` draws of `'T`
    /// from `seed`. The typed form a generated module's `verifyAll` emits,
    /// one call per decoder; `inline` for `register`'s reason.
    let inline verifyThrough<'T>
        (gate: JsonDecoderGate)
        (draws: int)
        (seed: int)
        (decoder: JsonDecoder<'T>)
        : Result<JsonDecoderVerification, DecoderRefusal> =
        gate draws seed typeof<'T> (fun value -> decoder value |> Result.map box)