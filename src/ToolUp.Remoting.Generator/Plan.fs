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
    /// The rendered decoder expression for the field's type. Phase 841 —
    /// on the JSON wire (`Plan.forJsonTypes`) it is the field's whole
    /// member read instead: `JsonDecode.field` / `optionalField` by name,
    /// or `JsonDecode.index` by position inside a union case.
    Decoder: string
    /// The F# source spelling of the field's type, for the emitted
    /// construction lambda's readability only.
    TypeSpelling: string
}

/// What a union case carries, as planned — one of the three wire shapes
/// `Write.writeUnion` emits, and the combinator each one takes.
[<RequireQualifiedAccess>]
type CasePayload =
    /// `[tag]` — `Decode.case0`.
    | NoFields
    /// `[tag; field]` — `Decode.payload`; the writer puts the one field
    /// DIRECTLY in the payload slot.
    | OneField of decoder: string
    /// `[tag; [field; …]]` — `Decode.fields n` over a `field` pipeline in
    /// the case's own declared field order, which is the order
    /// `Write.writeUnion` emits (Phase 800). The pipeline is the record
    /// shape, because on the wire the inner array IS a record of the
    /// case's fields; `fields` adds the arity check that makes a case of
    /// the wrong width a named refusal.
    | SeveralFields of fields: FieldPlan list

/// One union case's decode, as planned.
type UnionCasePlan = {
    CaseName: string
    /// The case's tag — its index in `FSharpType.GetUnionCases` order,
    /// which is the tag `Write.writeUnion` emits.
    Tag: int
    Payload: CasePayload
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
    /// The recursive binding groups (Phase 816): one entry per strongly
    /// connected component of the binding dependency graph that carries a
    /// cycle, each listing its members' binding names in emission order.
    /// `Bindings` is ordered so every group is CONTIGUOUS, and the emitter
    /// renders a group as one eta-expanded `let rec … and …` while every
    /// binding outside a group stays the plain `let` it always was — a
    /// plan with no cycle emits byte for byte what it did before this
    /// field existed. Named rather than counted so the census can say
    /// WHICH types recurse.
    RecursiveGroups: string list list
}

/// Phase 853 — one API record method as a generated client proxy calls it.
type ClientMethodPlan = {
    /// The record field's name — the route's method segment.
    MethodName: string
    /// One encoder expression per curried argument, in declaration order;
    /// a `unit` argument is `JsonEncode.unit`, as the reflective proxy
    /// writes it. Empty for a method that is an `Async<_>` value.
    ArgumentEncoders: string list
    /// Whether the call carries a body — `false` for `Async<_>` and
    /// `unit -> Async<_>`, which the reflective proxy sends as a GET.
    SendsBody: bool
    /// The F# source spelling of the response type.
    ReturnSpelling: string
    /// The decoder expression that reads the response.
    ReturnDecoder: string
}

/// Phase 853 — one method's read policy, as its `[<Cacheable>]` /
/// `[<Invalidates>]` attributes declare it. Read off the attribute DATA,
/// so the generator needs no reference to the assembly declaring them.
type ClientReadPolicy = {
    /// The method's field name.
    PolicyMethod: string
    /// `[<Cacheable(n)>]`'s `n`; `None` for a method that is not a read.
    MaxAgeSeconds: int option
    /// `[<Invalidates(…)>]`'s method names, in declaration order.
    Invalidates: string list
}

/// Phase 853 — one API record's generated client proxy, as planned.
type ClientRecordPlan = {
    /// `Type.Name` — the route's type segment.
    RecordName: string
    /// The key the client's proxy registry and read-policy table use for
    /// the record: its `FullName` with a nested type's `+` written `.`,
    /// which is how the Fable host spells a type's full name.
    RecordKey: string
    /// The record's F# source spelling.
    RecordSpelling: string
    /// Every method, in the record's declaration order.
    Methods: ClientMethodPlan list
    /// The read policies its attributes declare (Phase 854), emitted as
    /// the registration a Fable client cannot read off the record itself.
    ReadPolicies: ClientReadPolicy list
}

/// Phase 853 — everything one client-proxy generation run decided.
type ClientPlan = {
    /// The argument encoders (`Plan.forJsonEncoders`).
    Encoders: GenerationPlan
    /// The response decoders (`Plan.forJsonResponseDecoders`).
    Decoders: GenerationPlan
    /// The records whose proxies are generated, in the order asked.
    Records: ClientRecordPlan list
    /// The records NOT generated, each with its reason. Each keeps the
    /// reflective proxy, built on its first call.
    Skipped: Refusal list
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

    /// A reference tuple. `FSharpType.IsTuple` also answers true for a
    /// struct tuple, whose CLR type (`ValueTuple`) is not what the
    /// `tupleN` combinators construct — so a struct tuple is refused by
    /// name rather than registered under a type it does not produce.
    let private isReferenceTuple (t: Type) =
        FSharpType.IsTuple t && not t.IsValueType

    /// The widest tuple the algebra has a combinator for. Phase 800 shipped
    /// `tuple2`..`tuple4`; the platform's own API surface reaches arity 2.
    let private widestTuple = 4

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

    /// Phase 841 — the JSON wire's primitives: `JsonDecode`'s arms, which
    /// are the MessagePack set's twins plus `DateOnly` / `TimeOnly`
    /// (the STJ converter set writes both; the MessagePack writer does
    /// not, so that wire keeps refusing them).
    let private jsonPrimitiveDecoders =
        dict [
            typeof<bool>, "JsonDecode.asBool"
            typeof<unit>, "JsonDecode.asUnit"
            typeof<string>, "JsonDecode.asString"
            typeof<char>, "JsonDecode.asChar"
            typeof<int>, "JsonDecode.asInt32"
            typeof<int64>, "JsonDecode.asInt64"
            typeof<int16>, "JsonDecode.asInt16"
            typeof<sbyte>, "JsonDecode.asSByte"
            typeof<byte>, "JsonDecode.asByte"
            typeof<uint16>, "JsonDecode.asUInt16"
            typeof<uint32>, "JsonDecode.asUInt32"
            typeof<uint64>, "JsonDecode.asUInt64"
            typeof<float>, "JsonDecode.asFloat"
            typeof<float32>, "JsonDecode.asFloat32"
            typeof<decimal>, "JsonDecode.asDecimal"
            typeof<Guid>, "JsonDecode.asGuid"
            typeof<TimeSpan>, "JsonDecode.asTimeSpan"
            typeof<DateTime>, "JsonDecode.asDateTime"
            typeof<DateTimeOffset>, "JsonDecode.asDateTimeOffset"
            typeof<DateOnly>, "JsonDecode.asDateOnly"
            typeof<TimeOnly>, "JsonDecode.asTimeOnly"
        ]

    /// Phase 841 — the map keys the JSON wire can read back: the writer
    /// emits a map as an object whose member NAMES are the keys, a
    /// non-string key as its own JSON text, and `JsonDecode.Key` parses
    /// exactly these four.
    let private jsonKeyDecoders =
        dict [
            typeof<string>, "JsonDecode.Key.string"
            typeof<int>, "JsonDecode.Key.int32"
            typeof<int64>, "JsonDecode.Key.int64"
            typeof<Guid>, "JsonDecode.Key.guid"
        ]

    /// Phase 853 — the JSON wire's primitives WRITTEN: `JsonEncode`'s
    /// scalars, one per `jsonPrimitiveDecoders` arm, so the two runs admit
    /// exactly the same primitive set.
    let private jsonPrimitiveEncoders =
        dict [
            typeof<bool>, "JsonEncode.bool"
            typeof<unit>, "JsonEncode.unit"
            typeof<string>, "JsonEncode.string"
            typeof<char>, "JsonEncode.char"
            typeof<int>, "JsonEncode.int32"
            typeof<int64>, "JsonEncode.int64"
            typeof<int16>, "JsonEncode.int16"
            typeof<sbyte>, "JsonEncode.sbyte"
            typeof<byte>, "JsonEncode.byte"
            typeof<uint16>, "JsonEncode.uint16"
            typeof<uint32>, "JsonEncode.uint32"
            typeof<uint64>, "JsonEncode.uint64"
            typeof<float>, "JsonEncode.float"
            typeof<float32>, "JsonEncode.float32"
            typeof<decimal>, "JsonEncode.decimal"
            typeof<Guid>, "JsonEncode.guid"
            typeof<TimeSpan>, "JsonEncode.timeSpan"
            typeof<DateTime>, "JsonEncode.dateTime"
            typeof<DateTimeOffset>, "JsonEncode.dateTimeOffset"
            typeof<DateOnly>, "JsonEncode.dateOnly"
            typeof<TimeOnly>, "JsonEncode.timeOnly"
        ]

    /// Phase 853 — the map keys written: the inverse of each
    /// `jsonKeyDecoders` entry.
    let private jsonKeyEncoders =
        dict [
            typeof<string>, "JsonEncode.Key.string"
            typeof<int>, "JsonEncode.Key.int32"
            typeof<int64>, "JsonEncode.Key.int64"
            typeof<Guid>, "JsonEncode.Key.guid"
        ]

    /// Phase 841 — which wire a planning run renders combinators for.
    /// The type walk, the naming, the ordering and the cycle grouping are
    /// one algorithm; only the combinator each shape takes differs, so
    /// the wire is an axis of the one planner rather than a second one
    /// that could drift from it.
    type private Wire =
        /// `Decode` over `MsgPack.Value` — positional records, tagged
        /// unions (Phases 69k–817).
        | MessagePack
        /// `JsonDecode` over `JsonValue` — NAMED records, unions on the
        /// case name (Phase 799), the argument side's wire (Phase 841).
        | Json
        /// Phase 853 — `JsonEncode` into `JsonValue`: the same wire
        /// WRITTEN, by the client's generated proxies. The walk, the
        /// refusals and the naming are the `Json` run's; only the
        /// combinator each shape takes is the writing one.
        | JsonEncoding

    /// Mutable planning state, private to one `plan` call.
    type private State = {
        /// The wire this run plans for.
        Wire: Wire
        /// Phase 853 — a prefix every binding name carries (`encode`,
        /// `decode`), so an encoder and a decoder of one type can share a
        /// module. Empty for the pre-853 runs, whose names are unchanged.
        NamePrefix: string
        mutable Ordered: (Type * TypePlan) list // reverse order
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
        /// The planning path — the in-progress types, innermost first —
        /// so a reference can be recorded as an EDGE from the type being
        /// planned to the type it reaches (Phase 816). The edges are what
        /// the recursive groups are computed from.
        mutable Path: Type list
        Edges: Collections.Generic.Dictionary<Type, Collections.Generic.HashSet<Type>>
    }

    let private recordEdge (state: State) (target: Type) =
        match state.Path with
        | source :: _ ->
            match state.Edges.TryGetValue source with
            | true, targets -> targets.Add target |> ignore
            | _ ->
                let targets = Collections.Generic.HashSet<Type>(HashIdentity.Reference)
                targets.Add target |> ignore
                state.Edges[source] <- targets
        | [] -> ()

    let private allocateName (state: State) (t: Type) : string =
        match state.Names.TryGetValue t with
        | true, n -> n
        | _ ->
            let preferred =
                if state.NamePrefix = "" then
                    bindingName t
                else
                    state.NamePrefix + simpleName t

            let qualified =
                match t.DeclaringType with
                | null -> preferred
                | parent when state.NamePrefix = "" -> camelCase (simpleName parent) + simpleName t
                | parent -> state.NamePrefix + simpleName parent + simpleName t

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
        match state.Wire with
        | MessagePack -> messagePackDecoderFor state t
        | Json
        | JsonEncoding -> jsonDecoderFor state t

    /// The MessagePack wire's decoder expression for `t` — Phases 69k–817,
    /// unchanged by Phase 841's wire axis.
    and private messagePackDecoderFor (state: State) (t: Type) : string option =
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
            elif isReferenceTuple t then
                // Phase 800 — a tuple is the positional array a record is,
                // so its decoder is one `tupleN` over the element decoders,
                // inline: there is no name to bind it under, and the
                // registration key is the instantiated tuple type itself.
                let elements = FSharpType.GetTupleElements t

                if elements.Length > widestTuple then
                    refuse
                        state
                        t
                        (sprintf
                            "a tuple of %d elements — the algebra's tuple combinators reach arity %d"
                            elements.Length
                            widestTuple)
                else
                    let planned = elements |> Array.map (decoderFor state)

                    if planned |> Array.forall Option.isSome then
                        let arguments = planned |> Array.map (Option.get >> arg) |> String.concat " "
                        Some(sprintf "Decode.tuple%d %s" elements.Length arguments)
                    else
                        None
            elif FSharpType.IsTuple t then
                refuse state t "a struct tuple — the tuple combinators construct reference tuples"
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

    /// Phase 841 — the JSON wire's decoder expression for `t`: the
    /// `JsonDecode` twin of each MessagePack arm, and two refusals this
    /// wire has that the binary one does not.
    and private jsonDecoderFor (state: State) (t: Type) : string option =
        // Phase 853 — the combinator names are the only thing the writing
        // run changes; every branch, and so every refusal, is shared.
        let writing = state.Wire = JsonEncoding
        let name (decode: string) (encode: string) = if writing then encode else decode

        let primitives =
            if writing then
                jsonPrimitiveEncoders
            else
                jsonPrimitiveDecoders

        match primitives.TryGetValue t with
        | true, d -> Some d
        | _ ->

            if t = typeof<byte[]> then
                Some(name "JsonDecode.asBytes" "JsonEncode.bytes")
            elif t.IsArray then
                jsonDecoderFor state (t.GetElementType())
                |> Option.map (fun e -> sprintf "%s %s" (name "JsonDecode.array" "JsonEncode.array") (arg e))
            elif isGenericOf optionDef t then
                let inner = t.GetGenericArguments()[0]

                if isGenericOf optionDef inner then
                    // `JsonDecode.option` documents it: the writer
                    // flattens `Some None` to `null`, so the wire cannot
                    // carry the distinction the type makes.
                    refuse
                        state
                        t
                        "an option of an option — the JSON writer flattens `Some None` to `null`, so the wire cannot carry what the type distinguishes"
                else
                    jsonDecoderFor state inner
                    |> Option.map (fun e -> sprintf "%s %s" (name "JsonDecode.option" "JsonEncode.option") (arg e))
            elif isGenericOf listDef t then
                jsonDecoderFor state (t.GetGenericArguments()[0])
                |> Option.map (fun e -> sprintf "%s %s" (name "JsonDecode.list" "JsonEncode.list") (arg e))
            elif isGenericOf setDef t then
                jsonDecoderFor state (t.GetGenericArguments()[0])
                |> Option.map (fun e -> sprintf "%s %s" (name "JsonDecode.asSet" "JsonEncode.set") (arg e))
            elif isGenericOf mapDef t then
                let args = t.GetGenericArguments()

                let keys = if writing then jsonKeyEncoders else jsonKeyDecoders

                match keys.TryGetValue args[0] with
                | true, key ->
                    jsonDecoderFor state args[1]
                    |> Option.map (fun v -> sprintf "%s %s %s" (name "JsonDecode.asMap" "JsonEncode.map") key (arg v))
                | _ ->
                    refuse
                        state
                        t
                        (sprintf
                            "a map keyed by %s — the JSON writer emits a key as an object member NAME, and `JsonDecode.Key` reads string, int32, int64 and Guid names"
                            (typeSpelling args[0]))
            elif isGenericOf resultDef t then
                let args = t.GetGenericArguments()

                match jsonDecoderFor state args[0], jsonDecoderFor state args[1] with
                | Some ok, Some err ->
                    Some(sprintf "%s %s %s" (name "JsonDecode.result" "JsonEncode.result") (arg ok) (arg err))
                | _ -> None
            elif isReferenceTuple t then
                // A tuple is an array of its elements on this wire too
                // (`FSharpTupleConverter`).
                let elements = FSharpType.GetTupleElements t

                if elements.Length > widestTuple then
                    refuse
                        state
                        t
                        (sprintf
                            "a tuple of %d elements — the algebra's tuple combinators reach arity %d"
                            elements.Length
                            widestTuple)
                else
                    let planned = elements |> Array.map (jsonDecoderFor state)

                    if planned |> Array.forall Option.isSome then
                        let arguments = planned |> Array.map (Option.get >> arg) |> String.concat " "
                        Some(sprintf "%s%d %s" (name "JsonDecode.tuple" "JsonEncode.tuple") elements.Length arguments)
                    else
                        None
            elif FSharpType.IsTuple t then
                refuse state t "a struct tuple — the tuple combinators construct reference tuples"
            elif isRecord t then
                bindNamed state t
            elif isPlainUnion t then
                if isStringEnumUnion t then
                    // The MessagePack arm's reason holds on this wire too:
                    // `JsonDecode.stringEnum` takes the wire names from its
                    // caller, and Fable's casing rule plus any
                    // `[<CompiledName>]` is not recoverable from metadata.
                    refuse
                        state
                        t
                        "a [<StringEnum>] union — its wire spelling is Fable's casing rule, not recoverable from .NET metadata"
                else
                    bindNamed state t
            else
                refuse state t "no combinator in the closed algebra decodes this type"

    /// Phase 841 — how a planned field is READ, as the expression the
    /// emitted pipeline applies. On MessagePack that is the field's type
    /// decoder (the emitter wraps it in the positional `Decode.field`); on
    /// the JSON wire the combinator depends on the field's DECLARATION,
    /// not only its type — a record member is read by NAME, with
    /// `optionalField` for a member declared `option` (the writer's `None`
    /// is `null` and an older client's omission must read the same way),
    /// and a union case's several fields by POSITION in the payload array
    /// (`index`). The plan decides that choice, so it is carried here.
    and private fieldRead (state: State) (inUnionCase: bool) (position: int) (f: PropertyInfo) : string option =
        match state.Wire with
        | MessagePack -> decoderFor state f.PropertyType
        // Phase 853 — a written member is its type's encoder; the emitter
        // supplies the name (a record member) or the position (a case's
        // field), so the plan carries only the value's writing.
        | JsonEncoding -> jsonDecoderFor state f.PropertyType
        | Json when inUnionCase ->
            jsonDecoderFor state f.PropertyType
            |> Option.map (fun d -> sprintf "JsonDecode.index %d %s" position (arg d))
        | Json ->
            let t = f.PropertyType

            if
                isGenericOf optionDef t
                && not (isGenericOf optionDef (t.GetGenericArguments()[0]))
            then
                jsonDecoderFor state (t.GetGenericArguments()[0])
                |> Option.map (fun d -> sprintf "JsonDecode.optionalField \"%s\" %s" f.Name (arg d))
            else
                jsonDecoderFor state t
                |> Option.map (fun d -> sprintf "JsonDecode.field \"%s\" %s" f.Name (arg d))

    /// Ensure `t` has a named binding, planning it if it does not, and
    /// return the binding name.
    and private bindNamed (state: State) (t: Type) : string option =
        recordEdge state t

        if state.Bound.Contains t then
            Some(allocateName state t)
        elif state.InProgress.Contains t then
            // A cycle — Phase 816. Until then this was refused, on the
            // grounds that `let` bindings in dependency order cannot
            // express one. They cannot; a `let rec` group can, and a
            // recursive decoder is an ordinary value once its body is
            // eta-expanded so the back-edge is read under a lambda rather
            // than at initialisation. Which bindings form the group is
            // decided after planning, from the recorded edges, so that
            // only a cycle's own members change shape. Termination is the
            // algebra's own: a recursive reference is only ever reached
            // through `field` / `index` / `list` / `fields`, each of which
            // descends into a strictly smaller subterm. The name is bound
            // now, ahead of the type's own completion, so the reference
            // and the definition agree on it.
            Some(allocateName state t)
        else

            state.InProgress.Add t |> ignore
            state.Path <- t :: state.Path

            let planned =
                if isRecord t then
                    let fields = FSharpType.GetRecordFields(t, true)

                    let planned =
                        fields
                        |> Array.mapi (fun i (f: PropertyInfo) ->
                            fieldRead state false i f
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
                                    Payload = CasePayload.NoFields
                                }
                            | [| single |] ->
                                decoderFor state single.PropertyType
                                |> Option.map (fun d -> {
                                    CaseName = c.Name
                                    Tag = c.Tag
                                    Payload = CasePayload.OneField d
                                })
                            | several ->
                                // Phase 800 — the several-field case. Its
                                // fields are planned exactly as a record's
                                // are, in `GetFields` order, which is the
                                // order `Write.writeUnion` writes the inner
                                // array; the names are the case's own
                                // (`min` / `max`, or `Item1` / `Item2` for
                                // an unnamed field), which is what the path
                                // of a refusal beneath the case reads.
                                let fields =
                                    several
                                    |> Array.mapi (fun i (f: PropertyInfo) ->
                                        fieldRead state true i f
                                        |> Option.map (fun d -> {
                                            FieldName = f.Name
                                            Position = i
                                            Decoder = d
                                            TypeSpelling = typeSpelling f.PropertyType
                                        }))

                                if fields |> Array.forall Option.isSome then
                                    Some {
                                        CaseName = c.Name
                                        Tag = c.Tag
                                        Payload =
                                            CasePayload.SeveralFields(fields |> Array.map Option.get |> Array.toList)
                                    }
                                else
                                    None)

                    if planned |> Array.forall Option.isSome then
                        Some(
                            UnionDecoder(
                                allocateName state t,
                                typeSpelling t,
                                planned |> Array.map Option.get |> Array.toList
                            )
                        )
                    else
                        refuse state t "a union case's field has no decoder" |> ignore
                        None

            state.InProgress.Remove t |> ignore
            state.Path <- List.tail state.Path

            match planned with
            | Some p ->
                state.Bound.Add t |> ignore
                state.Ordered <- (t, p) :: state.Ordered
                Some(TypePlan.binding p)
            | None -> None

    /// Phase 816 — the recursive groups, and the binding order that keeps
    /// each one contiguous.
    ///
    /// The strongly connected components of the recorded binding graph,
    /// by Tarjan's algorithm over the planned types. A component is
    /// RECURSIVE when it has more than one member or its one member reaches
    /// itself; every other component is a single ordinary binding.
    ///
    /// Ordering: the planner's post-order already places every dependency
    /// of a binding before it, EXCEPT that a cycle's members may have
    /// non-members completed between them (a dependency of one member
    /// visited after another member). Each component is therefore placed
    /// at the position of its LAST-completing member. That is sound in
    /// both directions: a non-member completed between two members cannot
    /// depend on a member (it would then lie on a cycle with them and be
    /// a member itself), and a member cannot depend on anything completed
    /// after its component's last member (dependencies complete first).
    /// A plan with no cycle has every component a singleton at its own
    /// position, so its order is untouched.
    let private groupCycles (state: State) : TypePlan list * string list list =
        let ordered = List.rev state.Ordered
        let planned = ordered |> List.map fst |> List.toArray

        let position = Collections.Generic.Dictionary<Type, int>(HashIdentity.Reference)
        planned |> Array.iteri (fun i t -> position[t] <- i)

        let successors (t: Type) =
            match state.Edges.TryGetValue t with
            | true, targets -> targets |> Seq.filter position.ContainsKey |> Seq.toList
            | _ -> []

        // Tarjan. Components come out with every member's index and
        // whether the component carries a cycle.
        let index = Collections.Generic.Dictionary<Type, int>(HashIdentity.Reference)
        let low = Collections.Generic.Dictionary<Type, int>(HashIdentity.Reference)
        let onStack = Collections.Generic.HashSet<Type>(HashIdentity.Reference)
        let stack = Collections.Generic.Stack<Type>()
        let components = ResizeArray<Type list>()
        let mutable next = 0

        let rec strongConnect (v: Type) =
            index[v] <- next
            low[v] <- next
            next <- next + 1
            stack.Push v
            onStack.Add v |> ignore

            for w in successors v do
                if not (index.ContainsKey w) then
                    strongConnect w
                    low[v] <- min low[v] low[w]
                elif onStack.Contains w then
                    low[v] <- min low[v] index[w]

            if low[v] = index[v] then
                let members = ResizeArray<Type>()
                let mutable finished = false

                while not finished do
                    let w = stack.Pop()
                    onStack.Remove w |> ignore
                    members.Add w
                    finished <- Object.ReferenceEquals(w, v)

                components.Add(members |> Seq.sortBy (fun t -> position[t]) |> Seq.toList)

        for t in planned do
            if not (index.ContainsKey t) then
                strongConnect t

        let isRecursive (members: Type list) =
            match members with
            | [ only ] -> successors only |> List.exists (fun s -> Object.ReferenceEquals(s, only))
            | _ -> true

        let placed =
            components
            |> Seq.sortBy (fun members -> members |> List.map (fun t -> position[t]) |> List.max)
            |> Seq.toList

        let plansByType =
            Collections.Generic.Dictionary<Type, TypePlan>(HashIdentity.Reference)

        ordered |> List.iter (fun (t, p) -> plansByType[t] <- p)

        let bindings = placed |> List.collect (List.map (fun t -> plansByType[t]))

        let groups =
            placed
            |> List.filter isRecursive
            |> List.map (List.map (fun t -> state.Names[t]))

        bindings, groups

    /// One planning run over `roots`, for `wire`.
    let private forTypesOn (wire: Wire) (namePrefix: string) (roots: Type seq) : GenerationPlan =
        let state = {
            Wire = wire
            NamePrefix = namePrefix
            Ordered = []
            Bound = Collections.Generic.HashSet<Type>(HashIdentity.Reference)
            Names = Collections.Generic.Dictionary<Type, string>(HashIdentity.Reference)
            Used = Collections.Generic.HashSet<string>()
            InProgress = Collections.Generic.HashSet<Type>(HashIdentity.Reference)
            Refused = Collections.Generic.Dictionary<string, string>()
            Path = []
            Edges = Collections.Generic.Dictionary<Type, Collections.Generic.HashSet<Type>>(HashIdentity.Reference)
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

        let ordered, groups = groupCycles state

        {
            Bindings = ordered
            Roots = rootPlans
            Refusals =
                state.Refused
                |> Seq.map (fun kv -> { RefusedType = kv.Key; Why = kv.Value })
                |> Seq.sortBy _.RefusedType
                |> Seq.toList
            RecursiveGroups = groups
        }

    /// Plan decoders for `roots` — the wire types to be registered.
    ///
    /// Total: a root the algebra cannot express appears in `Refusals` and
    /// nowhere else, and the run still returns a plan for every root that
    /// could be expressed.
    let forTypes (roots: Type seq) : GenerationPlan = forTypesOn MessagePack "" roots

    /// Phase 841 — `forTypes` for the JSON wire: the same walk, naming,
    /// ordering, cycle grouping and refusal discipline, rendering
    /// `JsonDecode` combinators (Phase 799's surface) instead of `Decode`
    /// ones. A record field's `FieldPlan.Decoder` is its whole member read
    /// on this wire (`JsonDecode.field` / `optionalField` by name, or
    /// `JsonDecode.index` for a union case's several fields), because the
    /// combinator depends on how the field is declared, not only on its
    /// type. Refused beyond the MessagePack set: an option of an option
    /// (the writer flattens `Some None`) and a map whose key `JsonDecode.Key`
    /// cannot parse.
    let forJsonTypes (roots: Type seq) : GenerationPlan = forTypesOn Json "" roots

    /// Phase 853 — the JSON wire WRITTEN: `forJsonTypes`' walk over
    /// `roots` rendering `JsonEncode` combinators, every binding named
    /// `encode<Type>`. A record member's `FieldPlan.Decoder` is its
    /// value's encoder expression and a one-field case's payload is the
    /// field's; the emitter adds the member name. The refusals are
    /// exactly `forJsonTypes`' over the same roots — one walk, two
    /// vocabularies — so a type is written by a generated encoder if and
    /// only if the server's argument seam can read it through a generated
    /// decoder.
    let forJsonEncoders (roots: Type seq) : GenerationPlan = forTypesOn JsonEncoding "encode" roots

    /// Phase 853 — `forJsonTypes` with every binding named `decode<Type>`,
    /// for a module that also carries `forJsonEncoders`' bindings (the
    /// generated client proxies decode each method's RESPONSE through it).
    let forJsonResponseDecoders (roots: Type seq) : GenerationPlan = forTypesOn Json "decode" roots

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

    /// Phase 841 — the ARGUMENT types a method's field takes: walk the
    /// curried function chain to its `Async<_>` result, collecting every
    /// DOMAIN on the way. `unit` contributes nothing — a `unit -> Async<_>`
    /// method decodes no argument at all. A tupled parameter is ONE domain
    /// (the tuple), exactly as the server's argument seam reads it.
    ///
    /// `None` when the field does not end in an `Async<_>` — which is not
    /// a Remoting method shape, so nothing is registered for it (the same
    /// rule `returnTypeOf` applies).
    let argumentTypesOf (fieldType: Type) : Type list option =
        let rec walk (t: Type) (acc: Type list) =
            if FSharpType.IsFunction t then
                let domain, range = FSharpType.GetFunctionElements t
                walk range (domain :: acc)
            elif isGenericOf asyncDef t then
                Some(acc |> List.rev |> List.filter (fun d -> d <> typeof<unit>))
            else
                None

        walk fieldType []

    /// Phase 841 — every wire type an API record's methods TAKE, in
    /// declaration order, deduplicated: the argument side's roots, the
    /// keys the server's argument seam looks a decoder up under (scoped to
    /// this record, Phase 839).
    let argumentTypes (apiRecord: Type) : Type list =
        FSharpType.GetRecordFields(apiRecord, true)
        |> Array.toList
        |> List.collect (fun f -> argumentTypesOf f.PropertyType |> Option.defaultValue [])
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

    // ─── Phase 853 — the opens an emission needs ────────────────────

    /// Phase 853 — every namespace `roots` reach through array elements,
    /// generic arguments, record members and union case fields, sorted:
    /// the `open` lines an emitted module over them needs, since the
    /// emitter spells a type by its module-qualified name and leaves the
    /// namespace to an `open`. `System`, `System.*`, `Microsoft.FSharp.*`
    /// and the three namespaces every emission already opens
    /// (`ToolUp.Remoting`, `ToolUp.Remoting.Json`, `ToolUp.Remoting.Client`)
    /// are left out. The generator's CLI uses it when no `--open` is given.
    let namespacesReachedBy (roots: Type seq) : string list =
        let seen = Collections.Generic.HashSet<Type>()

        let rec walk (t: Type) =
            if seen.Add t then
                if t.IsArray then
                    walk (t.GetElementType())
                elif t.IsGenericType then
                    t.GetGenericArguments() |> Array.iter walk
                elif FSharpType.IsRecord(t, true) then
                    FSharpType.GetRecordFields(t, true) |> Array.iter (fun f -> walk f.PropertyType)
                elif FSharpType.IsUnion(t, true) then
                    FSharpType.GetUnionCases(t, true)
                    |> Array.iter (fun c -> c.GetFields() |> Array.iter (fun f -> walk f.PropertyType))

        roots |> Seq.iter walk

        seen
        |> Seq.choose (fun t -> Option.ofObj t.Namespace)
        |> Seq.filter (fun ns ->
            ns <> "System"
            && ns <> "ToolUp.Remoting"
            && ns <> "ToolUp.Remoting.Json"
            && ns <> "ToolUp.Remoting.Client"
            && not (ns.StartsWith "Microsoft.FSharp")
            && not (ns.StartsWith "System."))
        |> Seq.distinct
        |> Seq.sort
        |> List.ofSeq

    // ─── Phase 853 — client proxies ─────────────────────────────────

    /// Phase 853 — the namespaces a client-proxy emission over `apiRecords`
    /// opens: every namespace the records, their argument types and their
    /// response types reach (`namespacesReachedBy`).
    let clientNamespaces (apiRecords: Type list) : string list =
        namespacesReachedBy (
            apiRecords
            @ (apiRecords |> List.collect argumentTypes)
            @ (apiRecords |> List.collect returnTypes)
        )

    /// The widest method the reflective proxy builds (`Remoting.buildProxy`).
    let private widestMethod = 8

    /// A record's key in the client's proxy registry and read-policy
    /// table: `FullName`, a nested type's `+` written `.` as the Fable
    /// host writes it.
    let clientKey (apiRecord: Type) : string = apiRecord.FullName.Replace('+', '.')

    /// Phase 853 — every method of `apiRecord` as the reflective client
    /// proxy calls it: the name, EVERY curried domain (`unit` included —
    /// the reflective proxy writes it), and the response type. `Error`
    /// names why the record cannot have a generated proxy; it then keeps
    /// the reflective one, so the reasons are the shapes the generated
    /// transport does not reproduce: a method that is not `… -> Async<_>`
    /// (a streaming or promise-shaped method), a `byte[]` response (the
    /// binary read path), or more arguments than the reflective proxy
    /// supports.
    let clientMethodsOf (apiRecord: Type) : Result<(string * Type list * Type) list, string> =
        let rec walk (t: Type) (acc: Type list) =
            if FSharpType.IsFunction t then
                let domain, range = FSharpType.GetFunctionElements t
                walk range (domain :: acc)
            elif isGenericOf asyncDef t then
                Some(List.rev acc, t.GetGenericArguments()[0])
            else
                None

        let methods =
            FSharpType.GetRecordFields(apiRecord, true)
            |> Array.toList
            |> List.map (fun f ->
                match walk f.PropertyType [] with
                | None ->
                    Error(
                        sprintf
                            "method %s does not return Async<_> — a streaming or promise-shaped method keeps the reflective proxy"
                            f.Name
                    )
                | Some(_, returns) when returns = typeof<byte[]> ->
                    Error(
                        sprintf "method %s returns byte[] — the binary response path keeps the reflective proxy" f.Name
                    )
                | Some(domains, _) when List.length domains > widestMethod ->
                    Error(
                        sprintf
                            "method %s takes %d arguments — the proxy supports %d"
                            f.Name
                            (List.length domains)
                            widestMethod
                    )
                | Some(domains, returns) -> Ok(f.Name, domains, returns))

        match
            methods
            |> List.tryPick (function
                | Error why -> Some why
                | Ok _ -> None)
        with
        | Some why -> Error why
        | None ->
            Ok(
                methods
                |> List.choose (function
                    | Ok m -> Some m
                    | Error _ -> None)
            )

    /// Phase 853 — the read policies `apiRecord`'s attributes declare,
    /// read by the attributes' NAMES off the metadata (the attributes live
    /// in `ToolUp.Platform`, which the generator does not reference).
    let readPoliciesOf (apiRecord: Type) : ClientReadPolicy list = [
        for f in FSharpType.GetRecordFields(apiRecord, true) do
            let data = f.GetCustomAttributesData()

            let maxAge =
                data
                |> Seq.tryFind (fun a -> a.AttributeType.Name = "CacheableAttribute")
                |> Option.map (fun a -> a.ConstructorArguments[0].Value :?> int)

            let invalidates =
                data
                |> Seq.filter (fun a -> a.AttributeType.Name = "InvalidatesAttribute")
                |> Seq.collect (fun a ->
                    match a.ConstructorArguments[0].Value with
                    | :? Collections.Generic.IReadOnlyCollection<CustomAttributeTypedArgument> as items ->
                        items |> Seq.map (fun i -> i.Value :?> string)
                    | _ -> Seq.empty)
                |> List.ofSeq

            if maxAge.IsSome || not invalidates.IsEmpty then
                yield {
                    PolicyMethod = f.Name
                    MaxAgeSeconds = maxAge
                    Invalidates = invalidates
                }
    ]

    /// Phase 853 — plan generated client proxies for `apiRecords`.
    ///
    /// A record is generated when every method has a shape the generated
    /// transport reproduces (`clientMethodsOf`), every argument type has an
    /// encoder and every response type a decoder; otherwise it is in
    /// `Skipped` with the reason, and keeps the reflective proxy. The
    /// encoders and decoders are planned over the GENERATED records' types
    /// only, so a skipped record contributes no binding nobody calls.
    let forClientProxies (apiRecords: Type seq) : ClientPlan =
        let records = apiRecords |> Seq.distinct |> Seq.toList

        let shapes = records |> List.map (fun r -> r, clientMethodsOf r)

        let argumentRootsOf (methods: (string * Type list * Type) list) =
            methods
            |> List.collect (fun (_, domains, _) -> domains)
            |> List.filter (fun d -> d <> typeof<unit>)
            |> List.distinct

        let returnRootsOf (methods: (string * Type list * Type) list) =
            methods |> List.map (fun (_, _, r) -> r) |> List.distinct

        let spellingOf (t: Type) =
            if isNull t.FullName then t.Name else t.FullName

        // Pass 1: which records every type of which is expressible.
        let shaped =
            shapes
            |> List.choose (fun (r, shape) ->
                match shape with
                | Ok methods -> Some(r, methods)
                | Error _ -> None)

        let probeEncoders =
            forJsonEncoders (shaped |> List.collect (snd >> argumentRootsOf))

        let probeDecoders =
            forJsonResponseDecoders (shaped |> List.collect (snd >> returnRootsOf))

        let plannedIn (plan: GenerationPlan) =
            plan.Roots |> List.map _.RootFullName |> Set.ofList

        let refusalsOf (plan: GenerationPlan) =
            plan.Refusals
            |> List.map (fun r -> sprintf "%s: %s" r.RefusedType r.Why)
            |> String.concat "; "

        let encodable = plannedIn probeEncoders
        let decodable = plannedIn probeDecoders

        let why (r: Type) =
            match List.find (fst >> (=) r) shapes |> snd with
            | Error why -> Some why
            | Ok methods ->
                let unwritable =
                    argumentRootsOf methods
                    |> List.filter (fun t -> not (encodable.Contains(spellingOf t)))

                let unreadable =
                    returnRootsOf methods
                    |> List.filter (fun t -> not (decodable.Contains(spellingOf t)))

                match unwritable, unreadable with
                | [], [] -> None
                | t :: _, _ ->
                    Some(
                        sprintf
                            "argument type %s has no JSON encoder (%s) — the record keeps the reflective proxy"
                            (typeSpelling t)
                            (refusalsOf probeEncoders)
                    )
                | [], t :: _ ->
                    Some(
                        sprintf
                            "response type %s has no JSON decoder (%s) — the record keeps the reflective proxy"
                            (typeSpelling t)
                            (refusalsOf probeDecoders)
                    )

        let generated = shaped |> List.filter (fun (r, _) -> (why r).IsNone)

        // Pass 2: the bindings the generated records actually call.
        let encoders = forJsonEncoders (generated |> List.collect (snd >> argumentRootsOf))

        let decoders =
            forJsonResponseDecoders (generated |> List.collect (snd >> returnRootsOf))

        let expressionIn (plan: GenerationPlan) (t: Type) =
            plan.Roots
            |> List.find (fun p -> p.RootFullName = spellingOf t)
            |> _.RootDecoder

        {
            Encoders = encoders
            Decoders = decoders
            Records =
                generated
                |> List.map (fun (r, methods) -> {
                    RecordName = r.Name
                    RecordKey = clientKey r
                    RecordSpelling = typeSpelling r
                    Methods =
                        methods
                        |> List.map (fun (name, domains, returns) -> {
                            MethodName = name
                            ArgumentEncoders =
                                domains
                                |> List.map (fun d ->
                                    if d = typeof<unit> then
                                        "JsonEncode.unit"
                                    else
                                        expressionIn encoders d)
                            SendsBody = not (List.isEmpty domains || domains = [ typeof<unit> ])
                            ReturnSpelling = typeSpelling returns
                            ReturnDecoder = expressionIn decoders returns
                        })
                    ReadPolicies = readPoliciesOf r
                })
            Skipped =
                records
                |> List.choose (fun r ->
                    why r
                    |> Option.map (fun reason -> {
                        RefusedType = r.FullName
                        Why = reason
                    }))
        }