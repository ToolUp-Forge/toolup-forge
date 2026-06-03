// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Text.Json
open System.Text.Json.Nodes
open Microsoft.FSharp.Reflection

// ─── Layer 4 — symmetric arg marshalling ─────────────────────────────
//
// Shared reflection helpers for the two halves of the typed RPC: the
// client proxy (`JsonRpcPeerClient.create`) and the server-side contract
// builder (`JsonRpcPeerHost.contract`). Both must agree on the wire
// shape of a method's positional arguments, so the marshalling lives in
// one place. Arguments cross the wire as a JSON array; each element is
// (de)serialised with the universal `FableConverters` set (the same set
// the rest of the substrate uses), so F# records / DUs / options
// round-trip without bespoke converters.

/// Raised inside a typed proxy method when a peer call returns a
/// structured `PeerError`. The proxy presents contract methods as
/// `Async<'T>` (not `Async<Result<_,_>>`), so a peer-side failure
/// surfaces as this exception carrying the original `PeerError`.
/// Server-side only — never crosses the Fable boundary.
exception PeerInvocationException of PeerError

module internal PeerReflection =

    /// Split a (possibly curried) function type `a -> b -> Async<'R>`
    /// into its argument types `[a; b]` and the inner result type `'R`.
    /// A bare `Async<'R>` field (zero-arg method) yields `([], 'R)`.
    let functionSignature (t: Type) : Type list * Type =
        let rec loop acc (cur: Type) =
            if FSharpType.IsFunction cur then
                let dom, range = FSharpType.GetFunctionElements cur
                loop (dom :: acc) range
            else
                List.rev acc, cur.GetGenericArguments().[0]

        loop [] t

    let private serializeArg (arg: obj) (t: Type) : string =
        if t = typeof<unit> then
            "null"
        else
            JsonSerializer.Serialize(arg, t, JsonRpc.options)

    let private deserializeArg (json: string) (t: Type) : obj =
        if t = typeof<unit> then
            box ()
        else
            JsonSerializer.Deserialize(json, t, JsonRpc.options)

    /// Marshal positional arguments into a JSON array string.
    let marshalArgs (args: obj list) (argTypes: Type list) : string =
        let arr = JsonArray()

        List.zip args argTypes
        |> List.iter (fun (arg, t) -> arr.Add(JsonNode.Parse(serializeArg arg t)))

        arr.ToJsonString()

    /// Unmarshal a JSON array string back into positional argument values.
    let unmarshalArgs (argsJson: string) (argTypes: Type list) : obj list =
        let arr = JsonNode.Parse(argsJson).AsArray()

        argTypes
        |> List.mapi (fun i t ->
            let node = arr[i]
            let json = if isNull node then "null" else node.ToJsonString()
            deserializeArg json t)

    /// Apply a curried F# function value to a positional argument list,
    /// one arg at a time, returning the final (boxed) `Async<'R>`.
    let applyFunction (func: obj) (args: obj list) : obj =
        args
        |> List.fold
            (fun (f: obj) (arg: obj) ->
                let invoke =
                    f.GetType().GetMethods()
                    |> Array.find (fun m -> m.Name = "Invoke" && m.GetParameters().Length = 1)

                invoke.Invoke(f, [| arg |]))
            func