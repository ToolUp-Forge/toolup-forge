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
[<RequireQualifiedAccess>]
module JsonDecoders =

    let mutable private table: Map<string, RegisteredJsonDecoder> = Map.empty

    /// Register an already-erased decoder under a key. The non-inline
    /// half of `register`, and the only writer of the table.
    let registerByKey (key: string) (decoder: RegisteredJsonDecoder) : unit = table <- Map.add key decoder table

    /// Register the JSON algebra decoder for `'T`. Idempotent. `inline`
    /// for the reason `RemotingDecoders.register` is: Fable erases
    /// generics, so `typeof<'T>` must resolve at the call site.
    let inline register<'T> (decoder: JsonDecoder<'T>) : unit =
        registerByKey (RemotingDecoders.keyFor typeof<'T>) (fun value -> decoder value |> Result.map box)

    /// The JSON decoder for `wireType`, or `None` — the STJ path.
    let tryGet (wireType: Type) : RegisteredJsonDecoder option =
        Map.tryFind (RemotingDecoders.keyFor wireType) table

    /// Whether `wireType` decodes through the JSON algebra in this process.
    let isRegistered (wireType: Type) : bool =
        Map.containsKey (RemotingDecoders.keyFor wireType) table

    /// Every registered type's key, ordered.
    let registered () : string list = table |> Map.toList |> List.map fst

    /// How many types decode through the JSON algebra in this process.
    let count () : int = Map.count table

    /// Test-only: empty the table. See `RemotingDecoders.resetForTests`
    /// for why this is public.
    let resetForTests () : unit = table <- Map.empty