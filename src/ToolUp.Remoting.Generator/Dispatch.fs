// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Generator

open System
open System.IO
open System.Reflection
open System.Text
open Microsoft.FSharp.Reflection

// =============================================================================
// The SERVER half: a typed dispatch table
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
// The handler INVOCATION is now emitted too, as a
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
    /// The field's type flattened through its curried chain, each part
    /// spelled as F# source: the method contract's `Arguments` then its
    /// `Returns`, so the list is the server's own flattening, ending in the
    /// `Async<'r>`. Emitted so the generated table reflects over nothing when
    /// it is built.
    FlattenedTypes: string list
}

/// One API record's dispatch table.
type DispatchTable = {
    ApiRecordName: string
    /// The record's F# source spelling, FULLY QUALIFIED: the
    /// emitted invocation table names the record itself, and the emitted
    /// module's namespace and opens need not reach the record's own.
    ApiRecordSpelling: string
    Methods: DispatchMethod list
    /// The record's `ApiContract`, as F# source: the value the server's
    /// `Contract.read` yields, emitted as data beside the invocation table.
    Contract: string
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

    let private spellFlattened (t: Type) : string =
        if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Async<_>> then
            sprintf "Async<%s>" (Plan.typeSpelling (t.GetGenericArguments()[0]))
        else
            Plan.typeSpelling t

    [<Literal>]
    let private ServerAssembly = "ToolUp.Platform.Server.Remoting"

    /// The server's `Contract.read`, from the remoting assembly already
    /// loaded (the CLI loads `--server-assembly` first) or the one beside the
    /// API record's own assembly. The generator references no platform
    /// assembly: the contract it emits is the one the server reads, read by
    /// the server's own reader, never a second reading of the record.
    let private contractReader (apiRecord: Type) : MethodInfo =
        let loaded =
            AppDomain.CurrentDomain.GetAssemblies()
            |> Array.tryFind (fun a -> a.GetName().Name = ServerAssembly)

        let assembly =
            match loaded with
            | Some assembly -> assembly
            | None ->
                let beside =
                    Path.Combine(Path.GetDirectoryName apiRecord.Assembly.Location, ServerAssembly + ".dll")

                if File.Exists beside then
                    Assembly.LoadFrom beside
                else
                    failwithf
                        "dispatch needs the server's contract reader to emit %s's contract: %s.dll is neither loaded nor beside %s. Pass --server-assembly <path to %s.dll>."
                        apiRecord.Name
                        ServerAssembly
                        apiRecord.Assembly.Location
                        ServerAssembly

        assembly.GetType("ToolUp.Remoting.Server.Contract", true).GetMethod("read")

    let private quoted (text: string) =
        "\""
        + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")
        + "\""

    let private isGeneric (definition: Type) (t: Type) =
        t.IsGenericType && t.GetGenericTypeDefinition() = definition

    let private sourceName (t: Type) = t.FullName.Replace('+', '.')

    let private items (value: obj) =
        (value :?> Collections.IEnumerable) |> Seq.cast<obj> |> List.ofSeq

    /// A contract value as F# source. `Some column` lays records and lists of
    /// records out one member a line (the layout the formatter gives them);
    /// `None`, and every union, option, tuple and map argument, is one line.
    /// Every record label is qualified by its type, because the contract's
    /// records require qualified access.
    let rec private spell (indent: int option) (t: Type) (value: obj) : string =
        let one = spell None

        if t = typeof<string> then
            quoted (value :?> string)
        elif t = typeof<bool> then
            (if value :?> bool then "true" else "false")
        elif t = typeof<int> then
            string (value :?> int)
        elif t = typeof<Type> then
            sprintf "typeof<%s>" (spellFlattened (value :?> Type))
        elif t = typeof<DateTime> then
            let d = value :?> DateTime
            sprintf "DateTime(%dL, DateTimeKind.%O)" d.Ticks d.Kind
        elif isGeneric typedefof<_ option> t then
            match value with
            | null -> "None"
            | _ ->
                let _, fields = FSharpValue.GetUnionFields(value, t)
                sprintf "Some(%s)" (one (t.GetGenericArguments()[0]) fields[0])
        elif isGeneric typedefof<_ list> t then
            let element = t.GetGenericArguments()[0]

            match items value, indent with
            | [], _ -> "[]"
            | xs, Some column when FSharpType.IsRecord(element, true) ->
                let pad = String(' ', column + 4)

                let lines = xs |> List.map (fun x -> pad + spell (Some(column + 4)) element x)

                "[\n" + String.concat "\n" lines + "\n" + String(' ', column) + "]"
            | xs, _ -> "[ " + (xs |> List.map (one element) |> String.concat "; ") + " ]"
        elif isGeneric typedefof<Map<_, _>> t then
            let args = t.GetGenericArguments()

            let pairs =
                items value
                |> List.map (fun kv ->
                    let kvType = kv.GetType()

                    sprintf
                        "%s, %s"
                        (one args[0] (kvType.GetProperty("Key").GetValue kv))
                        (one args[1] (kvType.GetProperty("Value").GetValue kv)))

            match pairs with
            | [] -> "Map.empty"
            | _ -> "Map.ofList [ " + String.concat "; " pairs + " ]"
        elif FSharpType.IsTuple t then
            let elements = FSharpType.GetTupleElements t
            let values = FSharpValue.GetTupleFields value
            "(" + (Array.map2 one elements values |> String.concat ", ") + ")"
        elif FSharpType.IsRecord(t, true) then
            let member' (f: PropertyInfo) (inner: int option) =
                sprintf "%s.%s = %s" (sourceName t) f.Name (spell inner f.PropertyType (f.GetValue value))

            let fields = FSharpType.GetRecordFields(t, true)

            match indent with
            | Some column ->
                let pad = String(' ', column + 4)
                let lines = fields |> Array.map (fun f -> pad + member' f (Some(column + 4)))
                "{\n" + String.concat "\n" lines + "\n" + String(' ', column) + "}"
            | None ->
                "{ "
                + (fields |> Array.map (fun f -> member' f None) |> String.concat "; ")
                + " }"
        elif FSharpType.IsUnion(t, true) then
            let case, fields = FSharpValue.GetUnionFields(value, t, true)
            let name = sprintf "%s.%s" (sourceName t) case.Name

            match fields with
            | [||] -> name
            | _ ->
                let types = case.GetFields() |> Array.map _.PropertyType
                sprintf "%s(%s)" name (Array.map2 one types fields |> String.concat ", ")
        elif t.FullName = "ToolUp.Remoting.Server.JobCompanionCalls" then
            let resultType = t.GetProperty("ResultType").GetValue value :?> Type
            sprintf "%s.For<%s>()" (sourceName t) (spellFlattened resultType)
        else
            failwithf "the dispatch emitter cannot spell a contract value of type %s" t.FullName

    /// Read an API record's methods and contract off the same metadata the
    /// dispatcher reads: the contract by the server's own reader, and each
    /// method's flattened types from the contract.
    let tableFor (apiRecord: Type) : DispatchTable =
        let contract = (contractReader apiRecord).Invoke(null, [| box apiRecord |])
        let contractType = contract.GetType()

        let flattenedByName =
            items (contractType.GetProperty("Methods").GetValue contract)
            |> List.map (fun m ->
                let mType = m.GetType()
                let get name = mType.GetProperty(name).GetValue m

                let arguments = items (get "Arguments") |> List.map (fun t -> t :?> Type)

                get "Name" :?> string, arguments @ [ get "Returns" :?> Type ])
            |> Map.ofList

        let methods =
            FSharpType.GetRecordFields(apiRecord, true)
            |> Array.toList
            |> List.choose (fun f ->
                Plan.returnTypeOf f.PropertyType
                |> Option.map (fun returnType -> {
                    MethodName = f.Name
                    ArgumentTypes = domains f.PropertyType |> List.map Plan.typeSpelling
                    ReturnSpelling = Plan.typeSpelling returnType
                    FlattenedTypes = flattenedByName[f.Name] |> List.map spellFlattened
                }))

        {
            ApiRecordName = Plan.simpleName apiRecord
            ApiRecordSpelling = apiRecord.FullName.Replace('+', '.')
            Methods = methods
            Contract = spell (Some 4) contractType contract
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

    /// One method's entry in the emitted invocation table.
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
            // `System` too, as the decoders emitter already does:
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
            "    /// The record's contract, as the server's `Contract.read` read it when"
            "    /// this module was generated: what a registered table hands the"
            "    /// dispatcher, so the record is not walked at boot."
            sprintf "    let contract: ToolUp.Remoting.Server.ApiContract = %s" table.Contract
            ""
        ]

        write [
            "    /// Phase 906 — every method's generated invocation. The server's"
            "    /// remoting proxy composes it as the INNERMOST stage of the adapter's"
            "    /// pre-flight chain (auth, rate limit, validation, idempotency and"
            "    /// audit all run around it); arguments decode through the proxy's"
            "    /// own seam, and only the call and the result's type are typed here."
            sprintf "    let invocations: ToolUp.Remoting.Server.GeneratedInvocationTable<%s> =" table.ApiRecordSpelling
            "        ToolUp.Remoting.Server.GeneratedInvocation.tableWith contract ["
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