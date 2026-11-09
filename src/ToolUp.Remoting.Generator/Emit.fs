// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Generator

open System
open System.Text

// =============================================================================
// Rendering a plan as F# source
// =============================================================================
//
// The emitter renders and decides NOTHING. Every choice about which combinator
// decodes what, in which order, and what is refused, was made in `Plan`; this
// file turns that record into text. Keeping the split sharp is what lets the
// fidelity test assert against the PLAN — a structural claim about decoder
// expressions per field — rather than against formatted source, which would
// make the test a whitespace pin.

/// What to emit, beyond the plan itself.
type EmitOptions = {
    /// The namespace the module is declared in. `ToolUp.Remoting` puts the
    /// module beside `RemotingDecoders` and `Decode`, which is where a
    /// registration module belongs.
    Namespace: string
    /// The module's name.
    ModuleName: string
    /// `open` lines beyond `System`, in the order they should appear.
    Opens: string list
    /// One entry per API record the emitted `registerAll` covers:
    /// the record's name, the F# SOURCE SPELLING of every wire type it
    /// carries, and whether the wire corpus draws those shapes.
    ///
    /// Spellings rather than `Type.FullName` strings, because the facet
    /// compares against `RemotingDecoders.registered ()`, which is keyed by
    /// `FullName` — and a generic type's `FullName` embeds the assembly
    /// VERSION of every type argument. Emitting those as literals would
    /// bake `Version=0.23.0.0` into generated source and silently stop
    /// matching at the next version bump; emitting `typeof<…>.FullName`
    /// computes the same string at runtime and cannot go stale.
    ///
    /// The corpus flag is a DECLARATION the caller makes, never something
    /// the generator infers — `false` is the honest default, and the facet
    /// renders `Observed` rather than `Verified` for it.
    ApiRecords: (string * string list * bool) list
}

/// What to emit for the JSON ARGUMENT side, beyond the plan.
///
/// The argument side registers per `(API record, argument type)`, scoped to
/// the record, so the emitter needs each record's own argument list, not
/// only the union of every record's types the plan was made over.
type JsonEmitOptions = {
    /// The namespace the module is declared in. `ToolUp.Remoting.Json`
    /// puts it beside `JsonDecoders` and `JsonDecode`.
    JsonNamespace: string
    /// The module's name.
    JsonModuleName: string
    /// `open` lines beyond `System`, `ToolUp.Remoting` and
    /// `ToolUp.Remoting.Json`, in the order they should appear.
    JsonOpens: string list
    /// One entry per API record: the record's name as the server's
    /// argument seam scopes it (`Type.Name`), and the F# SOURCE SPELLING
    /// of every argument type its methods take (`Plan.argumentTypes`), in
    /// declaration order. A spelling the plan could not express is not
    /// registered — the plan's `Refusals` name it and why — and keeps the
    /// record out of `coveredApiRecords`.
    ArgumentRecords: (string * string list) list
}

/// What to emit for the JSON RESPONSE side — the encoders the server's
/// response writer consults before System.Text.Json — beyond the plan.
type JsonEncoderEmitOptions = {
    /// The namespace the module is declared in.
    EncoderNamespace: string
    /// The module's name.
    EncoderModuleName: string
    /// `open` lines beyond `System`, `ToolUp.Remoting` and
    /// `ToolUp.Remoting.Json`, in order.
    EncoderOpens: string list
    /// One entry per API record: its name and the F# SOURCE SPELLING of every
    /// type its methods return. A record every one of whose return types the
    /// plan expressed is in `coveredApiRecords`.
    ReturnRecords: (string * string list) list
}

/// What to emit for generated CLIENT proxies, beyond the plan.
type ClientEmitOptions = {
    /// The namespace the module is declared in.
    ClientNamespace: string
    /// The module's name.
    ClientModuleName: string
    /// `open` lines beyond `System`, `ToolUp.Remoting`,
    /// `ToolUp.Remoting.Json` and `ToolUp.Remoting.Client`, in order.
    ClientOpens: string list
}

[<RequireQualifiedAccess>]
module Emit =

    // ─── The rendering table ─────────────────────────────────────────
    //
    // One emission, rendered through a `Dialect`: the combinator names a
    // body calls, whether a record member is read by position or by the
    // read the plan chose, which way the binding runs, and the module's
    // header. The planner already decides every member read
    // (`FieldPlan.Decoder`), so the dialects differ only in what this table
    // names; a new emission is a new table value, not a fourth copy of the
    // renderers.

    /// How a dialect reads a record member and keys a union case.
    type private Reading =
        /// MessagePack: a member at its position (`Decode.field name position`),
        /// a case by its tag.
        | Positional
        /// JSON: a member by the read the plan chose (by name, or by index in a
        /// case's payload), a case by its name.
        | ByName

    /// Which way a dialect's bindings run.
    type private Direction =
        | Decoding
        | Encoding

    type private Dialect = {
        /// The combinator module every body calls.
        Combinators: string
        /// The binding's type constructor.
        BindingType: string
        Reading: Reading
        Direction: Direction
        /// What each binding is, in its doc line ("decoder", "JSON encoder", …).
        Noun: string
        /// The doc line's tail, after the type.
        DocTail: string
        /// The `<auto-generated>` banner's provenance lines.
        Provenance: string list
        /// The `open` lines every module of this dialect needs, after `System`.
        Opens: string list
        /// The module's own doc comment.
        ModuleDoc: string list
    }

    let private messagePack = {
        Combinators = "Decode"
        BindingType = "Decoder"
        Reading = Positional
        Direction = Decoding
        Noun = "decoder"
        DocTail = "one combinator per field or case, read off the type's own shape."
        Provenance = [
            "//   Emitted by ToolUp.Remoting.Generator from the SAME"
            "//   record/union metadata `Write.writeRecord` and `Write.writeUnion`"
            "//   enumerate, so field positions and union tags are the writer's own"
            "//   ordering rather than a second derivation of it."
        ]
        // The algebra's home is opened explicitly: a consumer emitting into
        // its own namespace finds `Decode`, `Decoder` and `RemotingDecoders`
        // through it.
        Opens = [ "ToolUp.Remoting" ]
        ModuleDoc = [
            "/// Generated algebra decoders: one per wire type the covered API records"
            "/// carry, registered by `registerAll` (or, verified against the reflection"
            "/// reader first, by `registerAllVerified`). Emitted by ToolUp.Remoting.Generator;"
            "/// do not edit."
        ]
    }

    let private json = {
        Combinators = "JsonDecode"
        BindingType = "JsonDecoder"
        Reading = ByName
        Direction = Decoding
        Noun = "JSON decoder"
        DocTail = "members by name, cases by case name, read off the type's own shape."
        Provenance = [
            "//   Emitted by ToolUp.Remoting.Generator from the SAME"
            "//   record/union metadata the System.Text.Json converter set reads:"
            "//   record members by NAME, union cases by case NAME, one decoder per"
            "//   argument type an API record's methods take."
        ]
        Opens = [ "ToolUp.Remoting"; "ToolUp.Remoting.Json" ]
        ModuleDoc = [
            "/// Generated JSON algebra decoders for the ARGUMENTS the covered API"
            "/// records' methods take, each registered scoped to its own record by"
            "/// `registerAll` (or, verified against the System.Text.Json converter set"
            "/// first, by `registerAllVerified`). Emitted by ToolUp.Remoting.Generator;"
            "/// do not edit."
        ]
    }

    let private jsonEncoding = {
        Combinators = "JsonEncode"
        BindingType = "JsonEncoder"
        Reading = ByName
        Direction = Encoding
        Noun = "JSON encoder"
        DocTail = "members by name, cases by case name, as the writer emits them."
        Provenance = [
            "//   Emitted by ToolUp.Remoting.Generator from the SAME"
            "//   record/union metadata the System.Text.Json converter set reads:"
            "//   one encoder per argument type and one decoder per response type an"
            "//   API record's methods carry, and one proxy builder per record."
        ]
        Opens = [ "ToolUp.Remoting"; "ToolUp.Remoting.Json"; "ToolUp.Remoting.Client" ]
        ModuleDoc = [
            "/// Generated client proxies: each API record's methods build their request"
            "/// through generated encoders and read their response through generated"
            "/// decoders, with no reflection at construction or per call. Emitted by"
            "/// ToolUp.Remoting.Generator; do not edit."
        ]
    }

    let private jsonResponseEncoding = {
        jsonEncoding with
            Provenance = [
                "//   Emitted by ToolUp.Remoting.Generator from the SAME"
                "//   record/union metadata the System.Text.Json converter set writes:"
                "//   one encoder per type an API record's methods return, which the"
                "//   server's response writer consults before the converter set."
            ]
            Opens = [ "ToolUp.Remoting"; "ToolUp.Remoting.Json" ]
            ModuleDoc = [
                "/// Generated JSON algebra encoders for the RESPONSES the covered API"
                "/// records' methods return, registered unscoped by `registerAll` (or,"
                "/// verified byte for byte against the System.Text.Json writer first,"
                "/// by `registerAllVerified`). Emitted by ToolUp.Remoting.Generator;"
                "/// do not edit."
            ]
    }

    let private parenthesise (expr: string) =
        if expr.Contains " " then "(" + expr + ")" else expr

    // ─── The renderers ───────────────────────────────────────────────

    /// The compilation unit's head: banner, namespace, opens, module.
    let private header (dialect: Dialect) (ns: string) (opens: string list) (moduleName: string) =
        [
            "// SPDX-License-Identifier: Apache-2.0"
            "// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)"
            "//"
            "// <auto-generated>"
        ]
        @ dialect.Provenance
        @ [
            "//"
            "//   DO NOT EDIT. Re-run the generator; a contract test in"
            "//   ToolUp.Platform.Tests holds this file to what the generator emits."
            "// </auto-generated>"
            ""
            sprintf "namespace %s" ns
            ""
            "open System"
        ]
        @ ((dialect.Opens @ opens) |> List.map (sprintf "open %s"))
        @ [ "" ]
        @ dialect.ModuleDoc
        @ [ "[<RequireQualifiedAccess>]"; sprintf "module %s =" moduleName; "" ]

    /// How a member is read inside the applicative chain.
    let private memberRead (dialect: Dialect) (f: FieldPlan) =
        match dialect.Reading with
        | Positional ->
            sprintf "%s.field \"%s\" %d %s" dialect.Combinators f.FieldName f.Position (parenthesise f.Decoder)
        | ByName -> f.Decoder

    let private caseKey (dialect: Dialect) (c: UnionCasePlan) =
        match dialect.Reading with
        | Positional -> string c.Tag
        | ByName -> sprintf "\"%s\"" c.CaseName

    let private parameters (fields: FieldPlan list) =
        fields |> List.map (fun f -> Plan.parameterName f.FieldName)

    /// A record binding. Decoding: an applicative chain over the
    /// construction, annotated with the record's own spelling so labels of a
    /// type declared inside a module resolve type-directed. Encoding: an
    /// object of the members, in declaration order.
    let private recordBody (dialect: Dialect) (keyword: string) (binding: string) (spelling: string) fields =
        let c = dialect.Combinators

        let signature =
            sprintf "    %s %s: %s<%s> =" keyword binding dialect.BindingType spelling

        match dialect.Direction with
        | Decoding -> [
            signature
            sprintf "        %s.succeed (fun %s -> ({" c (String.concat " " (parameters fields))
            fields
            |> List.map (fun f -> sprintf "            %s = %s" f.FieldName (Plan.parameterName f.FieldName))
            |> String.concat "\n"
            sprintf "        }: %s))" spelling
            fields
            |> List.map (fun f -> sprintf "        |> %s.apply (%s)" c (memberRead dialect f))
            |> String.concat "\n"
            ""
          ]
        | Encoding ->
            [
                signature
                sprintf "        fun (value: %s) ->" spelling
                sprintf "            %s.record [" c
            ]
            @ (fields
               |> List.map (fun f ->
                   sprintf "                \"%s\", %s value.%s" f.FieldName (parenthesise f.Decoder) f.FieldName))
            @ [ "            ]"; "" ]

    /// A union binding. Decoding: a total match on the case key, an
    /// unrecognised key refusing rather than falling through. Encoding: a
    /// total match writing each case by its name.
    let private unionBody (dialect: Dialect) (keyword: string) (binding: string) (spelling: string) cases =
        let c = dialect.Combinators

        let signature =
            sprintf "    %s %s: %s<%s> =" keyword binding dialect.BindingType spelling

        match dialect.Direction with
        | Decoding ->
            let arm (case: UnionCasePlan) =
                let key = caseKey dialect case

                match case.Payload with
                | CasePayload.NoFields -> [
                    sprintf "            | %s -> Some(%s.case0 %s.%s)" key c spelling case.CaseName
                  ]
                | CasePayload.OneField payload -> [
                    sprintf
                        "            | %s -> Some(%s.payload (%s |> %s.map %s.%s))"
                        key
                        c
                        (parenthesise payload)
                        c
                        spelling
                        case.CaseName
                  ]
                | CasePayload.SeveralFields fields ->
                    // `fields n` over the record-shaped pipeline the case's
                    // payload array is, one read per declared field, in order.
                    let applies =
                        fields
                        |> List.map (fun f ->
                            sprintf "                         |> %s.apply (%s)" c (memberRead dialect f))

                    [
                        sprintf "            | %s ->" key
                        "                Some("
                        sprintf "                    %s.fields" c
                        sprintf "                        %d" (List.length fields)
                        sprintf
                            "                        (%s.succeed (fun %s -> %s.%s(%s))"
                            c
                            (String.concat " " (parameters fields))
                            spelling
                            case.CaseName
                            (String.concat ", " (parameters fields))
                    ]
                    @ (applies
                       |> List.mapi (fun i line -> if i = List.length applies - 1 then line + ")" else line))
                    @ [ "                )" ]

            [ signature; sprintf "        %s.union \"%s\" (function" c spelling ]
            @ (cases |> List.collect arm)
            @ [ "            | _ -> None)"; "" ]
        | Encoding ->
            let arm (case: UnionCasePlan) =
                match case.Payload with
                | CasePayload.NoFields ->
                    sprintf "        | %s.%s -> %s.case0 \"%s\"" spelling case.CaseName c case.CaseName
                | CasePayload.OneField encoder ->
                    sprintf
                        "        | %s.%s field -> %s.payload \"%s\" (%s field)"
                        spelling
                        case.CaseName
                        c
                        case.CaseName
                        (parenthesise encoder)
                | CasePayload.SeveralFields fields ->
                    let values =
                        List.zip fields (parameters fields)
                        |> List.map (fun (f, p) -> sprintf "%s %s" (parenthesise f.Decoder) p)
                        |> String.concat "; "

                    sprintf
                        "        | %s.%s(%s) -> %s.fields \"%s\" [ %s ]"
                        spelling
                        case.CaseName
                        (String.concat ", " (parameters fields))
                        c
                        case.CaseName
                        values

            [ signature; "        function" ] @ (cases |> List.map arm) @ [ "" ]

    let private bindingBody (dialect: Dialect) (keyword: string) =
        function
        | RecordDecoder(b, t, fields) -> recordBody dialect keyword b t fields
        | UnionDecoder(b, t, cases) -> unionBody dialect keyword b t cases

    /// A binding of a `let rec` group, eta-expanded: the body is wrapped in
    /// `fun value -> (…) value`, so a recursive reference inside it is read on
    /// the first call rather than while the group is being built. That keeps
    /// a recursive plan an ordinary value: without it F# would reject the
    /// group or guard every back-edge with a runtime initialisation check
    /// (FS0040). Applied to every member of a recursive group and to nothing
    /// else; the cost is one closure allocation per member at module load.
    let private etaExpand (lines: string list) =
        // A body line may carry several source lines joined by `\n`, so
        // flatten first; the trailing blank separator is re-appended.
        let flat = lines |> List.collect (fun l -> l.Split '\n' |> List.ofArray)

        match flat with
        | header :: body ->
            let body = body |> List.filter (fun l -> l <> "")

            // Every body line sits at eight spaces or deeper; the wrap
            // re-homes it under the lambda at twelve, one further for the
            // continuation lines so they stay right of the opening paren.
            let wrapped =
                body
                |> List.mapi (fun i l ->
                    let content = l.Substring 8
                    let shifted = (if i = 0 then "            (" else "             ") + content
                    if i = List.length body - 1 then shifted + ")" else shifted)

            [ header; "        fun value ->" ] @ wrapped @ [ "                value"; "" ]
        | [] -> []

    /// One plan's bindings, dependencies first, each under its doc line —
    /// plain `let`s, and each recursive group (contiguous in `Bindings`) as
    /// one eta-expanded `let rec … and …`.
    let private renderBindings (dialect: Dialect) (plan: GenerationPlan) : string list =
        let groupOf (name: string) =
            plan.RecursiveGroups |> List.tryFind (List.contains name)

        let doc binding =
            sprintf "    /// Generated %s for `%s` — %s" dialect.Noun (TypePlan.typeSpelling binding) dialect.DocTail

        plan.Bindings
        |> List.collect (fun binding ->
            let name = TypePlan.binding binding

            match groupOf name with
            | None -> doc binding :: bindingBody dialect "let" binding
            | Some group ->
                let keyword = if List.head group = name then "let rec" else "and"
                doc binding :: etaExpand (bindingBody dialect keyword binding))

    // ─── The register block ──────────────────────────────────────────
    //
    // Every emitted module ends in the same sections — what it covers, an
    // explicit idempotent `registerAll`, and (for the decoders) a gated
    // `registerAllVerified` that registers everything or nothing. What each
    // dialect registers differs in kind (wire types; `(API record, argument
    // type)` pairs; proxy builders), so the entries are each emission's data
    // and the sections are rendered once.

    let private listBinding (doc: string list) (declaration: string) (entries: string list) =
        doc @ [ declaration ] @ entries @ [ "    ]"; "" ]

    let private registerAll (doc: string list) (registrations: string list) =
        doc
        @ [ "    let registerAll () : unit =" ]
        @ (if List.isEmpty registrations then
               [ "        ()" ]
           else
               registrations)
        @ [ "" ]

    let private registerAllVerified (doc: string list) (gate: string) (verification: string) (outcomes: string) =
        doc
        @ [
            "    let registerAllVerified"
            sprintf "        (gate: %s)" gate
            "        (draws: int)"
            "        (seed: int)"
            sprintf "        : Result<%s list, DecoderRefusal list> =" verification
            sprintf "        let outcomes = %s" outcomes
            ""
            "        let refusals ="
            "            outcomes"
            "            |> List.choose (function"
            "                | Error refusal -> Some refusal"
            "                | Ok _ -> None)"
            ""
            "        if List.isEmpty refusals then"
            "            registerAll ()"
            ""
            "            Ok("
            "                outcomes"
            "                |> List.choose (function"
            "                    | Ok verification -> Some verification"
            "                    | Error _ -> None)"
            "            )"
            "        else"
            "            Error refusals"
            ""
        ]

    let private render (lines: string list list) =
        let sb = StringBuilder()

        for l in List.concat lines do
            sb.Append(l).Append('\n') |> ignore

        sb.ToString()

    // ─── The MessagePack response side ───────────────────────────────

    /// Every key the emitted `registerAll` registers, bindings first then
    /// roots, deduplicated by `Type.FullName` and order-preserving.
    let coveredSpellings (plan: GenerationPlan) : (string * string) list =
        let bindingEntries =
            plan.Bindings |> List.map (fun b -> TypePlan.typeSpelling b, TypePlan.binding b)

        let bound = bindingEntries |> List.map fst |> Set.ofList

        let rootEntries =
            plan.Roots
            |> List.filter (fun r -> not (bound.Contains r.RootSpelling))
            |> List.map (fun r -> r.RootSpelling, r.RootDecoder)

        bindingEntries @ rootEntries

    let private messagePackRegistration (options: EmitOptions) (plan: GenerationPlan) =
        let covered = coveredSpellings plan

        let each (format: string -> string -> string) =
            covered
            |> List.map (fun (spelling, decoder) -> format spelling (parenthesise decoder))

        // The differential gate is an ARGUMENT (`DecoderGate`): drawing and
        // encoding a value need reflection and the shipped writer, which live
        // in the server tier, so the module names no server symbol and
        // compiles wherever its decoders do.
        listBinding
            [ "    /// The wire types this module covers, in registration order." ]
            "    let covered: string list = ["
            (covered
             |> List.map (fun (spelling, _) -> sprintf "        typeof<%s>.FullName" spelling))
        @ registerAll
            [
                "    /// Register every decoder above. Idempotent, and explicit —"
                "    /// a static initialiser would fire at a moment nothing states,"
                "    /// so a deployment could not say whether the algebra path was live."
            ]
            (each (sprintf "        Codecs.register<%s> Scope.Unscoped (Codec.msgPack %s)"))
        @ listBinding
            [
                "    /// Every decoder above beside the reflection reader,"
                "    /// over `draws` draws of its own type from `seed`, through `gate`"
                "    /// (the shipped one is `RemotingDecoders.gate`, in the server tier)."
                "    /// One outcome per registration, in registration order; a refusal"
                "    /// names the type and the first diverging draw."
            ]
            "    let verifyAll (gate: DecoderGate) (draws: int) (seed: int) : Result<DecoderVerification, DecoderRefusal> list = ["
            (each (sprintf "        RemotingDecoders.verifyThrough<%s> gate draws seed %s"))
        @ registerAllVerified
            [
                "    /// `registerAll`, gated: registers every decoder above"
                "    /// only when every one verifies, and registers NOTHING otherwise, so"
                "    /// a disagreement can never leave the table half-adopted."
            ]
            "DecoderGate"
            "DecoderVerification"
            "verifyAll gate draws seed"
        @ (if List.isEmpty options.ApiRecords then
               []
           else
               listBinding
                   [
                       "    /// The API records this module covers, shaped exactly as"
                       "    /// `RemotingDecoderFacet.inspect` takes them. The corpus flag is"
                       "    /// DECLARED by whoever generated this file, never inferred."
                   ]
                   "    let coveredApiRecords: (string * string list * bool) list = ["
                   (options.ApiRecords
                    |> List.map (fun (record, wireTypes, corpusCovered) ->
                        let types =
                            wireTypes |> List.map (sprintf "typeof<%s>.FullName") |> String.concat "; "

                        sprintf "        \"%s\", [ %s ], %b" record types corpusCovered)))

    /// Render a MessagePack response plan (`Plan.forTypes`) as an F#
    /// compilation unit: named decoders in dependency order, then `covered`,
    /// the registrations and the gate, then `coveredApiRecords` — the shape of
    /// a hand-written decoder module, consumed by `RemotingDecoderFacet.inspect`
    /// the same way.
    let compilationUnit (options: EmitOptions) (plan: GenerationPlan) : string =
        render [
            header messagePack options.Namespace options.Opens options.ModuleName
            renderBindings messagePack plan
            messagePackRegistration options plan
        ]

    // ─── The JSON argument side ──────────────────────────────────────

    /// Every `(API record, argument type spelling, decoder expression)` the
    /// emitted `registerAll` registers, in `ArgumentRecords` order then each
    /// record's own argument order. A spelling the plan has no root for is
    /// absent: the plan refused it, by name.
    let jsonRegistrations (options: JsonEmitOptions) (plan: GenerationPlan) : (string * string * string) list =
        let decoders =
            plan.Roots |> List.map (fun r -> r.RootSpelling, r.RootDecoder) |> Map.ofList

        options.ArgumentRecords
        |> List.collect (fun (record, spellings) ->
            spellings
            |> List.choose (fun spelling ->
                decoders |> Map.tryFind spelling |> Option.map (fun d -> record, spelling, d)))

    /// The API records every one of whose arguments the plan expressed, in
    /// `ArgumentRecords` order: the argument-side census. A record that takes
    /// no argument at all is in it — it has nothing for the converter set to
    /// decode — exactly as the served argument facet classifies it.
    let jsonCoveredApiRecords (options: JsonEmitOptions) (plan: GenerationPlan) : string list =
        let planned = plan.Roots |> List.map _.RootSpelling |> Set.ofList

        options.ArgumentRecords
        |> List.filter (fun (_, spellings) -> spellings |> List.forall planned.Contains)
        |> List.map fst

    let private jsonRegistration (options: JsonEmitOptions) (plan: GenerationPlan) =
        let registrations = jsonRegistrations options plan

        // Every registration is SCOPED to its API record, and the gate's
        // oracle is a parameter: the converter set it compares against is
        // composed in the server tier, which this module cannot reference.
        listBinding
            [
                "    /// The `(API record, argument type)` pairs this module registers, in"
                "    /// registration order — each decoder scoped to the one record whose"
                "    /// methods take it, never to every record of the same type."
            ]
            "    let covered: (string option * string) list = ["
            (registrations
             |> List.map (fun (record, spelling, _) ->
                 sprintf "        Some \"%s\", typeof<%s>.FullName" record spelling))
        @ listBinding
            [
                "    /// The API records every one of whose arguments this module decodes"
                "    /// through the JSON algebra — the argument-side census. A record taking"
                "    /// no argument at all is included: nothing of it reaches the converter set."
            ]
            "    let coveredApiRecords: string list = ["
            (jsonCoveredApiRecords options plan |> List.map (sprintf "        \"%s\""))
        @ registerAll
            [
                "    /// Register every decoder above, each scoped to its API record. Idempotent,"
                "    /// and explicit — a static initialiser would fire at a moment nothing"
                "    /// states, so a deployment could not say whether the algebra path was live."
            ]
            (registrations
             |> List.map (fun (record, spelling, decoder) ->
                 sprintf
                     "        Codecs.register<%s> (Scope.ForRecord \"%s\") (Codec.jsonDecoder %s)"
                     spelling
                     record
                     (parenthesise decoder)))
        @ [
            "    /// The gate over every registration above: each decoder through"
            "    /// `gate` over `draws` draws of its own type from `seed`, keyed as"
            "    /// `covered` is. The shipped gate is `JsonDecoders.gateWith"
            "    /// FableConverters.decoderOracle`, in the server tier: every draw"
            "    /// written by the server's writer AND the browser's."
            "    let verifyAll"
            "        (gate: JsonDecoderGate)"
            "        (draws: int)"
            "        (seed: int)"
            "        : ((string option * string) * Result<JsonDecoderVerification, DecoderRefusal>) list ="
            "        ["
        ]
        @ (registrations
           |> List.map (fun (record, spelling, decoder) ->
               sprintf
                   "            (Some \"%s\", typeof<%s>.FullName), JsonDecoders.verifyThrough<%s> gate draws seed %s"
                   record
                   spelling
                   spelling
                   (parenthesise decoder)))
        @ [ "        ]"; "" ]
        @ registerAllVerified
            [
                "    /// `registerAll`, gated: registers every decoder above only when every"
                "    /// one passes `gate`, and registers NOTHING otherwise, so a"
                "    /// disagreement can never leave the table half-adopted."
            ]
            "JsonDecoderGate"
            "JsonDecoderVerification"
            "verifyAll gate draws seed |> List.map snd"

    /// Render a JSON-wire plan (`Plan.forJsonTypes`) as the argument side's F#
    /// compilation unit. Every generated binding carries a doc comment: each
    /// is tracked public surface of the module that hosts it.
    let jsonCompilationUnit (options: JsonEmitOptions) (plan: GenerationPlan) : string =
        render [
            header json options.JsonNamespace options.JsonOpens options.JsonModuleName
            renderBindings json plan
            jsonRegistration options plan
        ]

    // ─── The JSON response side ──────────────────────────────────────

    /// The API records every one of whose return types the plan expressed,
    /// in `ReturnRecords` order: the response-side census.
    let jsonEncoderCoveredApiRecords (options: JsonEncoderEmitOptions) (plan: GenerationPlan) : string list =
        let planned = plan.Roots |> List.map _.RootSpelling |> Set.ofList

        options.ReturnRecords
        |> List.filter (fun (_, spellings) -> spellings |> List.forall planned.Contains)
        |> List.map fst

    let private jsonEncoderRegistration (options: JsonEncoderEmitOptions) (plan: GenerationPlan) =
        let each (format: string -> string -> string) =
            plan.Roots
            |> List.map (fun r -> format r.RootSpelling (parenthesise r.RootDecoder))

        // Registered UNSCOPED: a response type is written the same way
        // whichever record returns it. The gate is an argument, as on the
        // decoder side: the writer it compares against is the server tier's.
        listBinding
            [
                "    /// The wire types this module registers an encoder for, in registration order."
            ]
            "    let covered: string list = ["
            (plan.Roots
             |> List.map (fun r -> sprintf "        typeof<%s>.FullName" r.RootSpelling))
        @ listBinding
            [
                "    /// The API records every one of whose return types this module"
                "    /// encodes through the JSON algebra — the response-side census."
            ]
            "    let coveredApiRecords: string list = ["
            (jsonEncoderCoveredApiRecords options plan |> List.map (sprintf "        \"%s\""))
        @ registerAll
            [
                "    /// Register every encoder above, unscoped. Idempotent, and explicit —"
                "    /// a static initialiser would fire at a moment nothing states, so a"
                "    /// deployment could not say whether the algebra path was live."
            ]
            (each (sprintf "        Codecs.register<%s> Scope.Unscoped (Codec.jsonEncoder %s)"))
        @ listBinding
            [
                "    /// Every encoder above through `gate` over `draws` draws of its own type"
                "    /// from `seed`: its text against the System.Text.Json writer's, byte for"
                "    /// byte (the shipped gate is `JsonEncoders.gateWith"
                "    /// FableConverters.decoderOracle`, in the server tier). One outcome per"
                "    /// registration, in registration order."
            ]
            "    let verifyAll (gate: JsonEncoderGate) (draws: int) (seed: int) : Result<DecoderVerification, DecoderRefusal> list = ["
            (each (sprintf "        JsonEncoders.verifyThrough<%s> gate draws seed %s"))
        @ registerAllVerified
            [
                "    /// `registerAll`, gated: registers every encoder above only when every"
                "    /// one passes `gate`, and registers NOTHING otherwise, so a"
                "    /// disagreement can never leave the table half-adopted."
            ]
            "JsonEncoderGate"
            "DecoderVerification"
            "verifyAll gate draws seed"

    /// Render a JSON-encoding plan (`Plan.forJsonEncoders`) over API records'
    /// RETURN types as the response side's F# compilation unit.
    let jsonEncoderCompilationUnit (options: JsonEncoderEmitOptions) (plan: GenerationPlan) : string =
        render [
            header jsonResponseEncoding options.EncoderNamespace options.EncoderOpens options.EncoderModuleName
            renderBindings jsonResponseEncoding plan
            jsonEncoderRegistration options plan
        ]

    // ─── Generated client proxies ────────────────────────────────────
    //
    // One module per run: the argument ENCODERS, the response DECODERS, one
    // proxy builder per API record — a record VALUE whose fields are closures
    // over the encoders, the transport (`Proxy.generatedMethod`) and the
    // decoder — then the lookup `Api.makeProxy` consults and `registerAll`.
    // Nothing in it reflects.
    //
    // Its decoders are BUILT AT IMPORT, deliberately. Building
    // each on first use instead — the body inside a `lazy` — was emitted and
    // measured under Node, interleaved A/B in fresh processes: evaluating the
    // module ROSE from 9.3 to 10.7 ms (min of 8), the shell's import moved
    // inside the run-to-run spread, and the production bundle grew 10.7 KB. A
    // `Lazy` plus its wrapper costs about as much as it defers. Re-measure
    // with ClientBench's generated-module import lever
    // (`generatedModuleImportMsMin`) before changing this.

    /// The proxy builder's binding name for a record.
    let private proxyBinding (record: ClientRecordPlan) =
        Plan.camelCase record.RecordName + "Proxy"

    let private policyExpr (policy: ClientReadPolicy) =
        let quoted = policy.Invalidates |> List.map (sprintf "\"%s\"") |> String.concat "; "

        match policy.MaxAgeSeconds with
        | Some seconds when List.isEmpty policy.Invalidates ->
            sprintf "\"%s\", ReadPolicy.cacheable %d" policy.PolicyMethod seconds
        | None -> sprintf "\"%s\", ReadPolicy.invalidates [ %s ]" policy.PolicyMethod quoted
        | Some seconds ->
            sprintf
                "\"%s\", ({ MaxAgeSeconds = Some %d; Invalidates = [ %s ] }: ReadPolicy)"
                policy.PolicyMethod
                seconds
                quoted

    let private proxyBody (record: ClientRecordPlan) =
        let call (m: ClientMethodPlan) = "call" + m.MethodName

        let calls =
            record.Methods
            |> List.map (fun m ->
                sprintf
                    "        let %s = Proxy.generatedMethod<%s> api \"%s\" %s %s"
                    (call m)
                    m.ReturnSpelling
                    m.MethodName
                    (if m.SendsBody then "true" else "false")
                    (parenthesise m.ReturnDecoder))

        let registration =
            if List.isEmpty record.ReadPolicies then
                []
            else
                [ sprintf "        ReadPolicies.register \"%s\" [" record.RecordKey ]
                @ (record.ReadPolicies |> List.map (policyExpr >> sprintf "            %s"))
                @ [ "        ]"; "" ]

        let fields =
            record.Methods
            |> List.map (fun m ->
                match m.ArgumentEncoders with
                // A GET's body text is only the read-policy key's tail —
                // the one the reflective proxy uses (`[]` / `{}`).
                | [] -> sprintf "            %s = %s \"[]\"" m.MethodName (call m)
                | _ when not m.SendsBody -> sprintf "            %s = fun () -> %s \"{}\"" m.MethodName (call m)
                | encoders ->
                    let parameters = encoders |> List.mapi (fun i _ -> sprintf "a%d" i)

                    let values =
                        List.zip encoders parameters
                        |> List.map (fun (e, p) -> sprintf "%s %s" (parenthesise e) p)
                        |> String.concat "; "

                    sprintf
                        "            %s = fun %s -> %s (JsonEncode.arguments [ %s ])"
                        m.MethodName
                        (String.concat " " parameters)
                        (call m)
                        values)

        [
            sprintf
                "    /// Generated client proxy for `%s` — %d method(s), each a closure over its"
                record.RecordSpelling
                (List.length record.Methods)
            "    /// generated encoders, the transport and its generated response decoder."
            sprintf "    let %s (options: RemoteBuilderOptions) : %s =" (proxyBinding record) record.RecordSpelling
        ]
        @ registration
        @ [
            sprintf "        let api = Proxy.generatedApi \"%s\" \"%s\" options" record.RecordKey record.RecordName
        ]
        @ calls
        @ [ ""; "        {" ]
        @ fields
        @ [ "        }"; "" ]

    let private clientRegistration (plan: ClientPlan) =
        listBinding
            [
                "    /// The API records this module builds a generated proxy for, by the key"
                "    /// `Api.makeProxy` looks a record up under."
            ]
            "    let coveredApiRecords: string list = ["
            (plan.Records |> List.map (fun r -> sprintf "        \"%s\"" r.RecordKey))
        @ [
            "    /// The generated proxy for the record `apiKey` names, built over"
            "    /// `options`; `None` for a record this module does not cover."
            "    let tryBuild (apiKey: string) (options: RemoteBuilderOptions) : obj option ="
            "        match apiKey with"
        ]
        @ (plan.Records
           |> List.map (fun r -> sprintf "        | \"%s\" -> Some(box (%s options))" r.RecordKey (proxyBinding r)))
        @ [ "        | _ -> None"; "" ]
        @ registerAll
            [
                "    /// Register every proxy builder above with `GeneratedProxies`, where"
                "    /// `Api.makeProxy` finds it. Idempotent; call it at composition. A proxy"
                "    /// built before it runs resolves on its first call instead."
            ]
            (plan.Records
             |> List.map (fun r ->
                 sprintf
                     "        GeneratedProxies.register \"%s\" (fun options -> box (%s options))"
                     r.RecordKey
                     (proxyBinding r)))

    /// Render a client plan (`Plan.forClientProxies`) as the generated
    /// client-proxy compilation unit. Every binding carries a doc comment.
    let clientCompilationUnit (options: ClientEmitOptions) (plan: ClientPlan) : string =
        render [
            header jsonEncoding options.ClientNamespace options.ClientOpens options.ClientModuleName
            renderBindings jsonEncoding plan.Encoders
            renderBindings json plan.Decoders
            plan.Records |> List.collect proxyBody
            clientRegistration plan
        ]

    /// The records a client plan did NOT generate, rendered for a human (the
    /// CLI prints it), with each plan's refusals beneath.
    let clientSkipReport (plan: ClientPlan) : string =
        if List.isEmpty plan.Skipped then
            "every requested API record has a generated proxy"
        else
            plan.Skipped
            |> List.map (fun r -> sprintf "  %s\n    %s" r.RefusedType r.Why)
            |> String.concat "\n"

    /// The refusals, rendered for a human. Printed by the CLI rather than
    /// emitted into the file: a refused type keeps the reflection path, and
    /// a comment in a generated file is not where an operator looks.
    let refusalReport (plan: GenerationPlan) : string =
        if List.isEmpty plan.Refusals then
            "no refusals — every requested wire type is expressible in the closed algebra"
        else
            plan.Refusals
            |> List.map (fun r -> sprintf "  %s\n    %s" r.RefusedType r.Why)
            |> String.concat "\n"
            |> sprintf "%d wire type(s) kept the reflection path:\n%s" (List.length plan.Refusals)