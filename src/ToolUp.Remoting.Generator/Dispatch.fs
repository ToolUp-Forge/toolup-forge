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
// Phase 906 — the handler INVOCATION is now emitted too, as a
// `GeneratedInvocationTable` the server's remoting proxy composes (see
// `SourceGenDispatch.fs` in ToolUp.Platform.Server for the whole account).
// What the emitted code owns is exactly the part that needs static types:
// the call of the record's field with each argument taken, in order,
// through `GeneratedArguments.Next<'T>`, and the handler's `Async<'r>` handed
// to `GeneratedArguments.Complete`. Both are the proxy's own steps, so the
// verb check, the argument decode (record-scoped, as the reflective proxy
// decodes), the refusals and the result's serialise stay the proxy's single
// implementation, and the adapter's pre-flight chain runs around the call
// because the call is INSIDE the proxy the chain already wraps. The
// `decode<Method>Args` parses above are unchanged and still emitted: they
// are a standalone typed parse, not the dispatch path.

/// One method on an API record, as the dispatch emitter sees it.
type DispatchMethod = {
    MethodName: string
    /// The method's argument types in order — the curried function chain's
    /// domains, ending before the `Async<_>`.
    ArgumentTypes: string list
    /// The `Async<'r>` result's `'r`, spelled as F# source.
    ReturnSpelling: string
    /// Phase 906 — the field's type flattened through its curried chain,
    /// each part spelled as F# source: the same list, in the same order, as
    /// the server's reflective proxy computes (`TypeInfo.flattenFuncTypes`),
    /// ending in the `Async<'r>`. Emitted so the generated table reflects
    /// over nothing when it is built.
    FlattenedTypes: string list
}

/// One API record's dispatch table.
type DispatchTable = {
    ApiRecordName: string
    /// The record's F# source spelling, FULLY QUALIFIED (Phase 906): the
    /// emitted invocation table names the record itself, and the emitted
    /// module's namespace and opens need not reach the record's own.
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

    /// The server proxy's flattening rule (`TypeInfo.flattenFuncTypes`):
    /// a function type contributes its domain's parts, then its range's.
    let rec private flatten (t: Type) : Type list =
        if FSharpType.IsFunction t then
            let domain, range = FSharpType.GetFunctionElements t
            flatten domain @ flatten range
        else
            [ t ]

    let private spellFlattened (t: Type) : string =
        if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Async<_>> then
            sprintf "Async<%s>" (Plan.typeSpelling (t.GetGenericArguments()[0]))
        else
            Plan.typeSpelling t

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
                    FlattenedTypes = flatten f.PropertyType |> List.map spellFlattened
                }))

        {
            ApiRecordName = Plan.simpleName apiRecord
            ApiRecordSpelling = apiRecord.FullName.Replace('+', '.')
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

    /// Phase 906 — one method's entry in the emitted invocation table.
    let private invocationEntry (table: DispatchTable) (m: DispatchMethod) =
        let builder =
            match m.ArgumentTypes with
            | first :: _ when first <> "unit" ->
                sprintf
                    "ToolUp.Remoting.Server.GeneratedInvocation.forMethodWithFirst<%s, %s>"
                    table.ApiRecordSpelling
                    first
            | _ -> sprintf "ToolUp.Remoting.Server.GeneratedInvocation.forMethod<%s>" table.ApiRecordSpelling

        let flattened =
            m.FlattenedTypes |> List.map (sprintf "typeof<%s>") |> String.concat "; "

        let takes =
            m.ArgumentTypes
            |> List.mapi (fun i spelling -> sprintf "                    let a%d = args.Next<%s>()" i spelling)

        let call =
            match m.ArgumentTypes with
            | [] -> sprintf "api.%s" m.MethodName
            | _ ->
                sprintf
                    "api.%s %s"
                    m.MethodName
                    (m.ArgumentTypes |> List.mapi (fun i _ -> sprintf "a%d" i) |> String.concat " ")

        [
            sprintf "            %s" builder
            sprintf "                \"%s\"" m.MethodName
            sprintf "                [| %s |]" flattened
            sprintf
                "                (fun (args: ToolUp.Remoting.Server.GeneratedArguments) (api: %s) ->"
                table.ApiRecordSpelling
        ]
        @ takes
        @ [ sprintf "                    args.Complete(%s))" call ]

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
            "//   Phase 906 — and the generated invocation of every method, which"
            "//   the server's remoting proxy composes inside the adapter's"
            "//   pre-flight chain once `register ()` has run."
            "// </auto-generated>"
            ""
            sprintf "namespace %s" namespaceName
            ""
            // Phase 804 — `System` too, as the decoders emitter already does:
            // an argument spelled `Guid` or `DateOnly` resolves through it,
            // and the first consumer to COMPILE an emitted table
            // (samples/HelloWorld-AOT) failed on exactly those. (Phase 914 —
            // a tuple argument spells as `int * string`, not `Tuple<int,
            // string>`, so it no longer needs this open on its own account.)
            "open System"
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

        write [
            "    /// Phase 906 — every method's generated invocation. The server's"
            "    /// remoting proxy composes it as the INNERMOST stage of the adapter's"
            "    /// pre-flight chain (auth, rate limit, validation, idempotency and"
            "    /// audit all run around it); arguments decode through the proxy's"
            "    /// own seam, and only the call and the result's type are typed here."
            sprintf "    let invocations: ToolUp.Remoting.Server.GeneratedInvocationTable<%s> =" table.ApiRecordSpelling
            "        ToolUp.Remoting.Server.GeneratedInvocation.table ["
        ]

        table.Methods |> List.iter (invocationEntry table >> write)

        write [
            "        ]"
            ""
            "    /// Register `invocations` with the server's remoting proxy. Call"
            "    /// once from the composition root, BEFORE the remoting handler is"
            "    /// built: the proxy reads the registry when it is built."
            "    let register () ="
            "        ToolUp.Remoting.Server.GeneratedInvocation.register invocations"
        ]

        sb.ToString()