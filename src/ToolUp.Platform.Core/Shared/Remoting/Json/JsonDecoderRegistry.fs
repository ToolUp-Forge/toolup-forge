// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Json

open System
open ToolUp.Remoting

/// Phase 799 — a JSON decoder erased to `obj`, as the registry holds it.
type RegisteredJsonDecoder = JsonValue -> Result<obj, DecodeError>

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