// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Generator

open System
open System.Reflection
open Microsoft.FSharp.Reflection

// =============================================================================
// Phase 69k — the generation PLAN
// =============================================================================
//
// Given a set of root wire types, decide — total, offline, and without
// emitting a character — which closed-algebra decoder each one needs, in an
// order where every decoder's dependencies are already bound, and which types
// the algebra CANNOT express. The emitter (`Emit`) renders a plan; it makes no
// decisions of its own, so everything this phase claims about generated
// decoders is a claim about this file.
//
// ─── Why the plan is read off REFLECTION and not off the syntax tree ────
//
// The repository already carries an F# untyped-AST walker
// (`ToolUp.Remoting.Analyzers/Extraction.fs`), so reading record declarations
// out of source was the obvious route. It is the wrong one, for a reason that
// is about correctness rather than convenience.
//
// Records go onto this wire POSITIONALLY: `Write.writeRecord` enumerates
// `FSharpType.GetRecordFields` and emits the values in that order, and
// `Decode.field name position` exists precisely because the name is a path
// label while the position is the wire fact. A generator reading the SYNTAX
// would be re-deriving that order from a second source, and a second
// derivation of an ordering can disagree with the first — silently, since a
// swapped pair of same-typed fields still decodes. A generator reading the
// SAME metadata the writer reads cannot disagree with it: the order is not
// re-derived, it is the identical enumeration.
//
// The same argument settles generics. `Result<HealthSnapshot, string>` and
// `DegradedCapability list` arrive from reflection already instantiated;
// from the untyped AST they arrive as syntax that would have to be resolved
// against an invented symbol table.
//
// The cost is that generation is a dev-time act over built assemblies rather
// than an in-compiler one. That costs the phase nothing it claimed: the
// RUNTIME is what is reflection-free, and it is — an emitted decoder is a
// closed composition of `Decode` combinators with no reflective call on any
// path.
//
// ─── The refusal discipline ────────────────────────────────────────────
//
// A type the algebra cannot express is REFUSED BY NAME, never guessed at.
// A refusal is not a failure of the generator: a wire type with no generated
// decoder simply keeps the reflection path, which is what it had before, and
// `RemotingDecoders.tryGet` returning `None` is that path (see
// `DecoderRegistry.fs` — "a miss costs the algebra, never correctness"). A
// generator that guessed would instead produce a decoder that compiles, runs,
// and is wrong, which is the one outcome worse than not generating.

/// One record field's decode, as planned.
type FieldPlan = {
    /// The field's name — the path label `Decode.field` carries.
    FieldName: string
    /// The field's index in `FSharpType.GetRecordFields` order, which IS
    /// the order `Write.writeRecord` emits.
    Position: int
    /// The rendered decoder expression for the field's type.
    Decoder: string
    /// The F# source spelling of the field's type, for the emitted
    /// construction lambda's readability only.
    TypeSpelling: string
}

/// One union case's decode, as planned.
type UnionCasePlan = {
    CaseName: string
    /// The case's tag — its index in `FSharpType.GetUnionCases` order,
    /// which is the tag `Write.writeUnion` emits.
    Tag: int
    /// `None` for a field-less case (`Decode.case0`); `Some expr` for a
    /// single-payload case (`Decode.payload`).
    Payload: string option
}

/// One type's planned decoder.
type TypePlan =
    | RecordDecoder of binding: string * typeSpelling: string * fields: FieldPlan list
    | UnionDecoder of binding: string * typeSpelling: string * cases: UnionCasePlan list

[<RequireQualifiedAccess>]
module TypePlan =
    let binding =
        function
        | RecordDecoder(b, _, _) -> b
        | UnionDecoder(b, _, _) -> b

    let typeSpelling =
        function
        | RecordDecoder(_, t, _) -> t
        | UnionDecoder(_, t, _) -> t

/// A wire type the algebra cannot express, and why. Carried rather than
/// thrown: one run reports every refusal at once, and a caller deciding
/// whether a record can go on the algebra path needs the whole list.
type Refusal = { RefusedType: string; Why: string }

/// A root the caller asked to have registered — typically an API record
/// method's return type — with the expression that decodes it.
type RootPlan = {
    /// The F# source spelling of the registered type, e.g.
    /// `Result<HealthSnapshot, string>`.
    RootSpelling: string
    /// `Type.FullName` — the key `RemotingDecoders` stores under.
    RootFullName: string
    /// The decoder expression, which for a generic root is an inline
    /// composition over already-bound decoders.
    RootDecoder: string
}

/// Everything one generation run decided.
type GenerationPlan = {
    /// Named decoders, dependencies first. Emitting them in this order is
    /// what makes plain `let` bindings sufficient.
    Bindings: TypePlan list
    /// The roots the caller asked for that could be planned.
    Roots: RootPlan list
    /// The roots that could not be, and every type underneath them that
    /// could not be, deduplicated and ordered by name.
    Refusals: Refusal list
}

[<RequireQualifiedAccess>]
module Plan =

    // ─── Naming ──────────────────────────────────────────────────────

    /// F# keywords that would make an emitted lambda parameter a syntax
    /// error. Not the full keyword set — only what a PascalCase record
    /// field can collide with once lowered.
    let private reservedParameterNames =
        set [
            "type"
            "module"
            "namespace"
            "val"
            "let"
            "in"
            "to"
            "for"
            "do"
            "if"
            "then"
            "else"
            "match"
            "with"
            "function"
            "when"
            "and"
            "or"
            "not"
            "new"
            "base"
            "class"
            "end"
            "open"
            "rec"
            "mutable"
            "static"
            "member"
            "override"
            "abstract"
            "default"
            "inherit"
            "interface"
            "internal"
            "private"
            "public"
            "try"
            "finally"
            "while"
            "yield"
            "return"
            "fun"
            "of"
            "as"
            "begin"
            "done"
            "downcast"
            "upcast"
            "elif"
            "exception"
            "extern"
            "global"
            "inline"
            "lazy"
            "null"
            "struct"
            "use"
            "void"
            "const"
            "false"
            "true"
        ]

    /// Lower the leading acronym of a PascalCase name.
    ///
    /// `HealthProbeView` → `healthProbeView`; `AIDenialGroupCount` →
    /// `aiDenialGroupCount`. The rule is the usual one: lower the whole
    /// leading uppercase run, except that when the run is followed by a
    /// lowercase letter its LAST character starts the next word and stays
    /// capital. Matched against the hand-written Phase 785 decoder names,
    /// which is what the fidelity test asserts.
    let camelCase (name: string) : string =
        if String.IsNullOrEmpty name then
            name
        else
            let isUpper i = Char.IsUpper name[i]

            let run =
                let mutable i = 0

                while i < name.Length && isUpper i do
                    i <- i + 1

                i

            if run = 0 then
                name
            elif run = 1 then
                string (Char.ToLowerInvariant name[0]) + name.Substring 1
            elif run = name.Length then
                name.ToLowerInvariant()
            else
                // The run is followed by a lowercase letter, so the run's
                // last character begins the next word.
                let keep = run - 1
                name.Substring(0, keep).ToLowerInvariant() + name.Substring keep

    /// A type's simple name with any generic-arity backtick suffix removed.
    let simpleName (t: Type) : string =
        let n = t.Name
        let tick = n.IndexOf '`'
        if tick >= 0 then n.Substring(0, tick) else n

    /// The simple name QUALIFIED by any enclosing F# modules.
    ///
    /// A type declared inside a module is a nested CLR type, so its `Name`
    /// alone (`ColumnExpr`) does not resolve at the emitted module's scope
    /// unless that module was `open`ed — and a generator cannot know which
    /// opens a caller passed. Walking `DeclaringType` emits
    /// `ColumnMappingTypes.ColumnExpr`, which resolves from the namespace
    /// alone. Namespaces themselves are NOT walked: they are what the
    /// caller's `--open` list is for, and qualifying to the full name would
    /// make every emitted line unreadable.
    let rec qualifiedName (t: Type) : string =
        match t.DeclaringType with
        | null -> simpleName t
        | parent -> qualifiedName parent + "." + simpleName t

    /// The decoder binding name for a type.
    let bindingName (t: Type) : string = camelCase (simpleName t)

    /// A lambda parameter name for a record field.
    let parameterName (fieldName: string) : string =
        let c = camelCase fieldName
        if reservedParameterNames.Contains c then c + "'" else c

    // ─── Structural tests ────────────────────────────────────────────

    let private isGenericOf (definition: Type) (t: Type) =
        t.IsGenericType && t.GetGenericTypeDefinition() = definition

    let private optionDef = typedefof<option<_>>
    let private listDef = typedefof<list<_>>
    let private setDef = typedefof<Set<_>>
    let private mapDef = typedefof<Map<_, _>>
    let private resultDef = typedefof<Result<_, _>>
    let private asyncDef = typedefof<Async<_>>

    let private hasAttributeNamed (name: string) (t: Type) =
        t.GetCustomAttributes(false) |> Array.exists (fun a -> a.GetType().Name = name)

    /// Fable's `[<StringEnum>]`, which `Write.makeSerializerAux` tests for
    /// and nothing else.
    let private isStringEnumUnion (t: Type) =
        hasAttributeNamed "StringEnumAttribute" t

    let private isRecord (t: Type) = FSharpType.IsRecord(t, true)

    let private isPlainUnion (t: Type) =
        FSharpType.IsUnion(t, true)
        && not (isGenericOf optionDef t)
        && not (isGenericOf listDef t)
        && not (isGenericOf resultDef t)

    // ─── Type spelling ───────────────────────────────────────────────

    let private primitiveSpellings =
        dict [
            typeof<bool>, "bool"
            typeof<unit>, "unit"
            typeof<string>, "string"
            typeof<char>, "char"
            typeof<int>, "int"
            typeof<int64>, "int64"
            typeof<int16>, "int16"
            typeof<sbyte>, "sbyte"
            typeof<byte>, "byte"
            typeof<uint16>, "uint16"
            typeof<uint32>, "uint32"
            typeof<uint64>, "uint64"
            typeof<float>, "float"
            typeof<float32>, "float32"
            typeof<decimal>, "decimal"
            typeof<Guid>, "Guid"
            typeof<TimeSpan>, "TimeSpan"
            typeof<DateTime>, "DateTime"
            typeof<DateTimeOffset>, "DateTimeOffset"
        ]

    /// The F# source spelling of a type, assuming the emitted module opens
    /// `System` and the namespaces its wire types live in.
    let rec typeSpelling (t: Type) : string =
        match primitiveSpellings.TryGetValue t with
        | true, s -> s
        | _ ->
            if t = typeof<byte[]> then
                "byte[]"
            elif t.IsArray then
                typeSpelling (t.GetElementType()) + "[]"
            elif isGenericOf optionDef t then
                sprintf "%s option" (parenthesised (t.GetGenericArguments()[0]))
            elif isGenericOf listDef t then
                sprintf "%s list" (parenthesised (t.GetGenericArguments()[0]))
            elif isGenericOf setDef t then
                sprintf "Set<%s>" (typeSpelling (t.GetGenericArguments()[0]))
            elif isGenericOf mapDef t then
                let args = t.GetGenericArguments()
                sprintf "Map<%s, %s>" (typeSpelling args[0]) (typeSpelling args[1])
            elif isGenericOf resultDef t then
                let args = t.GetGenericArguments()
                sprintf "Result<%s, %s>" (typeSpelling args[0]) (typeSpelling args[1])
            elif t.IsGenericType then
                let args = t.GetGenericArguments() |> Array.map typeSpelling |> String.concat ", "
                sprintf "%s<%s>" (qualifiedName t) args
            else
                qualifiedName t

    /// A spelling safe to place left of a postfix type operator.
    and private parenthesised (t: Type) : string =
        let s = typeSpelling t
        if s.Contains " " then "(" + s + ")" else s

    // ─── Planning ────────────────────────────────────────────────────

    let private primitiveDecoders =
        dict [
            typeof<bool>, "Decode.asBool"
            typeof<unit>, "Decode.asUnit"
            typeof<string>, "Decode.asString"
            typeof<char>, "Decode.asChar"
            typeof<int>, "Decode.asInt32"
            typeof<int64>, "Decode.asInt64"
            typeof<int16>, "Decode.asInt16"
            typeof<sbyte>, "Decode.asSByte"
            typeof<byte>, "Decode.asByte"
            typeof<uint16>, "Decode.asUInt16"
            typeof<uint32>, "Decode.asUInt32"
            typeof<uint64>, "Decode.asUInt64"
            typeof<float>, "Decode.asFloat"
            typeof<float32>, "Decode.asFloat32"
            typeof<decimal>, "Decode.asDecimal"
            typeof<Guid>, "Decode.asGuid"
            typeof<TimeSpan>, "Decode.asTimeSpan"
            typeof<DateTime>, "Decode.asDateTime"
            typeof<DateTimeOffset>, "Decode.asDateTimeOffset"
        ]

    /// Mutable planning state, private to one `plan` call.
    type private State = {
        mutable Ordered: TypePlan list // reverse order
        Bound: Collections.Generic.HashSet<Type>
        InProgress: Collections.Generic.HashSet<Type>
        Refused: Collections.Generic.Dictionary<string, string>
        /// The binding name allocated to each planned type, and the names
        /// already taken. Two types declared in different modules can share
        /// a simple name, and two `let` bindings of one name in one module
        /// is a compile error rather than a subtle one — so the second
        /// claimant is qualified by its declaring module, and a third by a
        /// numeric suffix.
        Names: Collections.Generic.Dictionary<Type, string>
        Used: Collections.Generic.HashSet<string>
    }

    let private allocateName (state: State) (t: Type) : string =
        match state.Names.TryGetValue t with
        | true, n -> n
        | _ ->
            let preferred = bindingName t

            let qualified =
                match t.DeclaringType with
                | null -> preferred
                | parent -> camelCase (simpleName parent) + simpleName t

            let rec pick candidate n =
                if not (state.Used.Contains candidate) then candidate
                elif n = 0 then pick qualified 1
                else pick (sprintf "%s%d" preferred n) (n + 1)

            let chosen = pick preferred 0
            state.Used.Add chosen |> ignore
            state.Names[t] <- chosen
            chosen

    let private refuse (state: State) (t: Type) (why: string) =
        let key = if isNull t.FullName then t.Name else t.FullName

        if not (state.Refused.ContainsKey key) then
            state.Refused[key] <- why

        None

    /// Wrap a decoder expression for use as an argument.
    let private arg (expr: string) =
        if expr.Contains " " then "(" + expr + ")" else expr

    /// The decoder expression for `t`, binding any named decoders it needs
    /// along the way. `None` means refused — the refusal is recorded on
    /// `state`, so a caller never has to invent a reason.
    let rec private decoderFor (state: State) (t: Type) : string option =
        match primitiveDecoders.TryGetValue t with
        | true, d -> Some d
        | _ ->

            if t = typeof<byte[]> then
                Some "Decode.asBytes"
            elif t.IsArray then
                decoderFor state (t.GetElementType())
                |> Option.map (fun e -> sprintf "Decode.array %s" (arg e))
            elif isGenericOf optionDef t then
                decoderFor state (t.GetGenericArguments()[0])
                |> Option.map (fun e -> sprintf "Decode.option %s" (arg e))
            elif isGenericOf listDef t then
                decoderFor state (t.GetGenericArguments()[0])
                |> Option.map (fun e -> sprintf "Decode.list %s" (arg e))
            elif isGenericOf setDef t then
                decoderFor state (t.GetGenericArguments()[0])
                |> Option.map (fun e -> sprintf "Decode.asSet %s" (arg e))
            elif isGenericOf mapDef t then
                let args = t.GetGenericArguments()

                match decoderFor state args[0], decoderFor state args[1] with
                | Some k, Some v -> Some(sprintf "Decode.asMap %s %s" (arg k) (arg v))
                | _ -> None
            elif isGenericOf resultDef t then
                let args = t.GetGenericArguments()

                match decoderFor state args[0], decoderFor state args[1] with
                | Some ok, Some err -> Some(sprintf "Decode.result %s %s" (arg ok) (arg err))
                | _ -> None
            elif isRecord t then
                bindNamed state t
            elif isPlainUnion t then
                if isStringEnumUnion t then
                    // `Decode.stringEnum` exists, and this is still a refusal.
                    // The wire spelling of a `[<StringEnum>]` case is decided by
                    // Fable's casing rule plus any `[<CompiledName>]` override,
                    // and neither is recoverable from .NET metadata with enough
                    // confidence to emit. Refusing leaves the type on the
                    // reflection path it is already on; guessing would emit a
                    // decoder that compiles, runs, and reads the wrong case name.
                    refuse
                        state
                        t
                        "a [<StringEnum>] union — its wire spelling is Fable's casing rule, not recoverable from .NET metadata"
                else
                    bindNamed state t
            else
                refuse state t "no combinator in the closed algebra decodes this type"

    /// Ensure `t` has a named binding, planning it if it does not, and
    /// return the binding name.
    and private bindNamed (state: State) (t: Type) : string option =
        if state.Bound.Contains t then
            Some(allocateName state t)
        elif state.InProgress.Contains t then
            // A cycle. `let` bindings emitted in dependency order cannot
            // express one, and `let rec` over `Decoder<'T>` values would
            // need an explicit thunk at every back-edge. Refused rather
            // than half-supported.
            refuse state t "a recursive type — a cycle cannot be emitted as dependency-ordered let bindings"
        else

            state.InProgress.Add t |> ignore

            let planned =
                if isRecord t then
                    let fields = FSharpType.GetRecordFields(t, true)

                    let planned =
                        fields
                        |> Array.mapi (fun i (f: PropertyInfo) ->
                            decoderFor state f.PropertyType
                            |> Option.map (fun d -> {
                                FieldName = f.Name
                                Position = i
                                Decoder = d
                                TypeSpelling = typeSpelling f.PropertyType
                            }))

                    if planned |> Array.forall Option.isSome then
                        Some(
                            RecordDecoder(
                                allocateName state t,
                                typeSpelling t,
                                planned |> Array.map Option.get |> Array.toList
                            )
                        )
                    else
                        refuse state t "at least one field's type has no decoder" |> ignore
                        None
                else
                    let cases = FSharpType.GetUnionCases(t, true)

                    let planned =
                        cases
                        |> Array.map (fun c ->
                            match c.GetFields() with
                            | [||] ->
                                Some {
                                    CaseName = c.Name
                                    Tag = c.Tag
                                    Payload = None
                                }
                            | [| single |] ->
                                decoderFor state single.PropertyType
                                |> Option.map (fun d -> {
                                    CaseName = c.Name
                                    Tag = c.Tag
                                    Payload = Some d
                                })
                            | _ -> None)

                    if planned |> Array.forall Option.isSome then
                        Some(
                            UnionDecoder(
                                allocateName state t,
                                typeSpelling t,
                                planned |> Array.map Option.get |> Array.toList
                            )
                        )
                    else
                        refuse state t "a union case carries more than one field, or a case payload has no decoder"
                        |> ignore

                        None

            state.InProgress.Remove t |> ignore

            match planned with
            | Some p ->
                state.Bound.Add t |> ignore
                state.Ordered <- p :: state.Ordered
                Some(TypePlan.binding p)
            | None -> None

    /// Plan decoders for `roots` — the wire types to be registered.
    ///
    /// Total: a root the algebra cannot express appears in `Refusals` and
    /// nowhere else, and the run still returns a plan for every root that
    /// could be expressed.
    let forTypes (roots: Type seq) : GenerationPlan =
        let state = {
            Ordered = []
            Bound = Collections.Generic.HashSet<Type>(HashIdentity.Reference)
            Names = Collections.Generic.Dictionary<Type, string>(HashIdentity.Reference)
            Used = Collections.Generic.HashSet<string>()
            InProgress = Collections.Generic.HashSet<Type>(HashIdentity.Reference)
            Refused = Collections.Generic.Dictionary<string, string>()
        }

        let rootPlans =
            roots
            |> Seq.distinct
            |> Seq.choose (fun t ->
                decoderFor state t
                |> Option.map (fun d -> {
                    RootSpelling = typeSpelling t
                    RootFullName = (if isNull t.FullName then t.Name else t.FullName)
                    RootDecoder = d
                }))
            |> Seq.toList

        {
            Bindings = List.rev state.Ordered
            Roots = rootPlans
            Refusals =
                state.Refused
                |> Seq.map (fun kv -> { RefusedType = kv.Key; Why = kv.Value })
                |> Seq.sortBy _.RefusedType
                |> Seq.toList
        }

    // ─── API records ─────────────────────────────────────────────────

    /// A Remoting API contract: a record with ≥1 field, every field a
    /// function type. The same shape test the analyzer applies to the
    /// syntax tree, applied here to metadata.
    let isApiRecord (t: Type) : bool =
        isRecord t
        && let fields = FSharpType.GetRecordFields(t, true) in

           fields.Length > 0
           && fields |> Array.forall (fun f -> FSharpType.IsFunction f.PropertyType)

    /// The type a method's field ultimately returns: walk the curried
    /// function chain to its `Async<'r>` result and take `'r`.
    ///
    /// `None` when the field does not end in an `Async<_>` — which is not
    /// a Remoting method shape, so nothing is registered for it.
    let rec returnTypeOf (fieldType: Type) : Type option =
        if FSharpType.IsFunction fieldType then
            let _, range = FSharpType.GetFunctionElements fieldType
            returnTypeOf range
        elif isGenericOf asyncDef fieldType then
            Some(fieldType.GetGenericArguments()[0])
        else
            None

    /// Every wire type an API record's methods return, in declaration
    /// order, deduplicated.
    let returnTypes (apiRecord: Type) : Type list =
        FSharpType.GetRecordFields(apiRecord, true)
        |> Array.toList
        |> List.choose (fun f -> returnTypeOf f.PropertyType)
        |> List.distinct

    /// Every API record an assembly declares, ordered by name.
    ///
    /// The census the Phase 785 facet's coverage question needs: the facet
    /// classifies the records a composition root DECLARES, so "how many did
    /// it not declare" cannot be read from the facet itself.
    let apiRecordsIn (assembly: Assembly) : Type list =
        assembly.GetTypes()
        |> Array.filter (fun t -> not t.IsGenericTypeDefinition && isApiRecord t)
        |> Array.sortBy _.FullName
        |> Array.toList