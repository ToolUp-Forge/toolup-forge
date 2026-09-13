// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Generator

open System
open System.Text
open Microsoft.FSharp.Reflection

// =============================================================================
// Phase 69k.B — the SERVER half: a typed dispatch table
// =============================================================================
//
// 69k.B asks for a dispatcher whose "argument record deserialiser + handler
// invocation + result serialiser" are direct, non-reflective calls. Exactly one
// third of that is expressible today, and the reason matters enough to state
// here rather than in a phase note, because it is the thing a later reader will
// otherwise try to "finish".
//
// **The argument side cannot compose the closed decoder algebra, and will not
// until the JSON follow-on ships.** Server arguments do not arrive as MsgPack:
// `Read.Reader` has exactly ONE production call site in this tree — the Fable
// client decoding the server's binary RESPONSE — and server arguments arrive as
// `Choice<byte[], JsonElement>` and decode through System.Text.Json. Phase 785
// decided (785.F) that the same algebra extends to JSON in a follow-on phase
// and NOT over the JSON value model as it stands, because that model's numeric
// case cannot carry an int64 past 2^53, an exact decimal, or a source width.
// So a generated argument decoder composed of `Decode` combinators would be a
// decoder for bytes that never reach it.
//
// What IS expressible, and what this emitter therefore emits, is the typed
// half: the argument parse as `FableConverters.tryDeserialise<'inp>` — the
// Phase 783 statically-typed STJ seam — rather than as a reflective
// `MethodInfo` walk over a boxed `obj`. That is a real removal of reflection
// from the argument path, it is wire-identical by construction (the same seam,
// the same options, the same `DecodeError` mapping), and it is the shape the
// follow-on will swap a `Decode`-composed body into without changing a single
// call site.
//
// The handler invocation and result serialisation are NOT emitted. Both live
// behind `Proxy.fs`'s private composition and the adapter's `HttpContext`
// plumbing; emitting them would mean widening that surface, which is the
// irreversible call this phase escalates rather than takes.

/// One method on an API record, as the dispatch emitter sees it.
type DispatchMethod = {
    MethodName: string
    /// The method's argument types in order — the curried function chain's
    /// domains, ending before the `Async<_>`.
    ArgumentTypes: string list
    /// The `Async<'r>` result's `'r`, spelled as F# source.
    ReturnSpelling: string
}

/// One API record's dispatch table.
type DispatchTable = {
    ApiRecordName: string
    ApiRecordSpelling: string
    Methods: DispatchMethod list
}

[<RequireQualifiedAccess>]
module Dispatch =

    /// The curried domains of a function chain, outermost first, stopping
    /// at the `Async<_>` result.
    let rec private domains (fieldType: Type) : Type list =
        if FSharpType.IsFunction fieldType then
            let domain, range = FSharpType.GetFunctionElements fieldType
            domain :: domains range
        else
            []

    /// Read an API record's methods off the same metadata the dispatcher's
    /// reflective classifier reads.
    let tableFor (apiRecord: Type) : DispatchTable =
        let methods =
            FSharpType.GetRecordFields(apiRecord, true)
            |> Array.toList
            |> List.choose (fun f ->
                Plan.returnTypeOf f.PropertyType
                |> Option.map (fun returnType -> {
                    MethodName = f.Name
                    ArgumentTypes = domains f.PropertyType |> List.map Plan.typeSpelling
                    ReturnSpelling = Plan.typeSpelling returnType
                }))

        {
            ApiRecordName = Plan.simpleName apiRecord
            ApiRecordSpelling = Plan.typeSpelling apiRecord
            Methods = methods
        }

    let private methodBody (table: DispatchTable) (m: DispatchMethod) =
        let arity = List.length m.ArgumentTypes

        let binders =
            m.ArgumentTypes |> List.mapi (fun i _ -> sprintf "a%d" i) |> String.concat "; "

        let decodes =
            m.ArgumentTypes
            |> List.mapi (fun i spelling ->
                sprintf
                    "            match FableConverters.tryDeserialise<%s> a%d options with\n            | Error e -> Error(DecodeError.under \"%s(args)[%d]\" e)\n            | Ok v%d ->"
                    spelling
                    i
                    m.MethodName
                    i
                    i)
            |> String.concat "\n"

        let tupled =
            if arity = 1 then
                "v0"
            else
                m.ArgumentTypes |> List.mapi (fun i _ -> sprintf "v%d" i) |> String.concat ", "

        [
            sprintf "    /// Typed argument parse for `%s.%s`." table.ApiRecordName m.MethodName
            sprintf "    let decode%sArgs (options: JsonSerializerOptions) (args: JsonElement list) =" m.MethodName
            "        match args with"
            sprintf "        | [ %s ] ->" binders
            decodes
            sprintf "            Ok(%s)" tupled
            "        | _ ->"
            sprintf
                "            Error(DecodeError.at [ \"%s(args)\" ] \"%d argument(s)\" (sprintf \"%%d\" (List.length args)))"
                m.MethodName
                arity
            ""
        ]

    /// Render a dispatch table as F# source.
    let compilationUnit (namespaceName: string) (opens: string list) (table: DispatchTable) : string =
        let sb = StringBuilder()

        let write (lines: string list) =
            lines |> List.iter (fun l -> sb.Append(l).Append('\n') |> ignore)

        write [
            "// SPDX-License-Identifier: Apache-2.0"
            "// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)"
            "//"
            "// <auto-generated>"
            sprintf "//   Phase 69k.B — typed argument parse for %s." table.ApiRecordName
            "//   Every argument decodes through the Phase 783 statically-typed STJ"
            "//   seam, never a reflective MethodInfo walk over a boxed obj."
            "// </auto-generated>"
            ""
            sprintf "namespace %s" namespaceName
            ""
            "open System.Text.Json"
            "open ToolUp.Remoting"
            "open ToolUp.Remoting.Json.SystemTextJson"
        ]

        write (opens |> List.map (sprintf "open %s"))

        write [
            ""
            "[<RequireQualifiedAccess>]"
            sprintf "module %sDispatch =" table.ApiRecordName
            ""
            "    /// Every method this record declares, with its arity. The"
            "    /// manifest the adapter registers routes from — a list, not a"
            "    /// reflective walk over the record's fields at startup."
            "    let methods: (string * int) list = ["
        ]

        write (
            table.Methods
            |> List.map (fun m -> sprintf "        \"%s\", %d" m.MethodName (List.length m.ArgumentTypes))
        )

        write [ "    ]"; "" ]
        table.Methods |> List.iter (methodBody table >> write)
        sb.ToString()