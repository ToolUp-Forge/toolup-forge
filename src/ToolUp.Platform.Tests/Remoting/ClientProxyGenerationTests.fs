// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Remoting.ClientProxyGenerationTests

// ─── Phase 853 — generated client proxies and encoders ──────────────────
//
// `src/ToolUp.Platform.Client/Client/Remoting/PlatformClientProxies.fs` is
// the generator's `client-proxies` emission over every API record
// `ToolUp.Platform.Core` declares: argument ENCODERS, response DECODERS and
// one proxy builder per record, which `Api.makeProxy` uses in place of the
// reflective proxy. The cases below hold:
//
//   853.C — the committed file IS the emission (regeneration switch), every
//           binding documented, and the census of generated vs skipped
//           records against a DECLARED list;
//   853.D — cross-host agreement over deterministic draws: every generated
//           ENCODER's text decodes, on the server, through the argument
//           seam's BOTH paths — the Phase 841 algebra decoder the record's
//           registration uses, and the System.Text.Json converter set's own
//           reader — to the value drawn; and every generated RESPONSE
//           decoder reads the converter set's writing of a drawn value back
//           to that value;
//   853.A — one walk, two vocabularies: the encoder plan refuses exactly
//           what the decoder plan refuses;
//   854 finding — the read policies a record's attributes declare are
//           emitted as the registration, and equal `ReadPolicies.ofAttributes`
//           (the committed `ReadCatalogClientProxies.fs` fixture is that
//           emission, compiled by both packs).

open System
open System.IO
open Expecto
open Microsoft.FSharp.Reflection
open ToolUp.Platform
open ToolUp.Remoting
open ToolUp.Remoting.Json
open ToolUp.Remoting.Generator

let private repoRoot () =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then
            failwith "could not find the repository root (ToolUp.Forge.sln) above the test's base directory"
        elif File.Exists(Path.Combine(dir.FullName, "ToolUp.Forge.sln")) then
            dir.FullName
        else
            up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let private platformCoreAssembly = typeof<IHealthMonitorApi>.Assembly

let private platformRecords () = Plan.apiRecordsIn platformCoreAssembly

/// The emission `PlatformClientProxies.fs` must equal: the CLI's
/// `client-proxies` over ToolUp.Platform.Core with this namespace and module.
let private platformEmission () : string * ClientPlan =
    let records = platformRecords ()
    let plan = Plan.forClientProxies records

    let options = {
        ClientNamespace = "ToolUp.Remoting.Client"
        ClientModuleName = "PlatformClientProxies"
        ClientOpens = Plan.clientNamespaces records
    }

    Emit.clientCompilationUnit options plan, plan

let private fixtureEmission () : string * ClientPlan =
    let records = [ typeof<ReadPolicyFixture.ReadCatalogApi> ]
    let plan = Plan.forClientProxies records

    let options = {
        ClientNamespace = "ToolUp.Platform.Tests.Remoting"
        ClientModuleName = "ReadCatalogClientProxies"
        ClientOpens = Plan.clientNamespaces records
    }

    Emit.clientCompilationUnit options plan, plan

let private normalise (text: string) = text.Replace("\r\n", "\n").TrimEnd()

/// Hold `relative` to `expected`, rewriting it first when `switch` is set.
let private pinned (switch: string) (relative: string) (expected: string) =
    let path = Path.Combine(repoRoot (), relative)

    if Environment.GetEnvironmentVariable switch = "1" then
        File.WriteAllText(path, expected.Replace("\r\n", "\n"))

    let committed = File.ReadAllText path

    if normalise committed <> normalise expected then
        let e = (normalise expected).Split '\n'
        let c = (normalise committed).Split '\n'

        let first =
            Seq.zip e c
            |> Seq.tryFindIndex (fun (a, b) -> a <> b)
            |> Option.defaultValue (min e.Length c.Length)

        failtestf
            "%s is not what the generator emits (first difference at line %d; %d committed lines vs %d expected). Regenerate and commit it with your change:\n  $env:%s = \"1\"\n  dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-case \"853.C\"\n  $env:%s = $null\nthen rebuild and re-run this pack WITHOUT the variable."
            relative
            (first + 1)
            c.Length
            e.Length
            switch
            switch

/// The records the census DECLARES skipped, with the class of reason. A
/// record entering or leaving this list is a deliberate edit here.
let private declaredSkipped = [
    "ToolUp.Platform.IExternalContactApi", "has no JSON decoder"
    "ToolUp.Platform.Usage.IUsageQueryApi", "returns byte[]"
]

// ─── reflection over the COMPILED generated modules (test-side only) ──

let private clientAssembly =
    typeof<ToolUp.Remoting.Client.RemoteBuilderOptions>.Assembly

let private coreAssembly = typeof<JsonValue>.Assembly

/// A generated binding, read off its compiled module, as a function to
/// apply. F# compiles a binding whose body is a lambda (every generated
/// encoder, and every eta-expanded recursive decoder) as a static METHOD,
/// and one built from combinators as a static PROPERTY holding an
/// `FSharpFunc`; both are callable here.
let private bindingValue (assembly: Reflection.Assembly) (moduleName: string) (binding: string) : obj -> obj =
    let moduleType = assembly.GetType(moduleName, true)
    let flags = Reflection.BindingFlags.Public ||| Reflection.BindingFlags.Static

    match moduleType.GetProperty(binding, flags) with
    | null ->
        match
            moduleType.GetMethods(flags)
            |> Array.tryFind (fun m -> m.Name = binding && m.GetParameters().Length = 1)
        with
        | Some m -> fun argument -> m.Invoke(null, [| argument |])
        | None -> failwithf "%s declares no binding %s" moduleName binding
    | property ->
        let f = property.GetValue null

        let invoke =
            f.GetType().GetMethods()
            |> Array.find (fun m -> m.Name = "Invoke" && m.GetParameters().Length = 1)

        fun argument -> invoke.Invoke(f, [| argument |])

let private apply (f: obj -> obj) (argument: obj) : obj = f argument

/// A decoder's boxed `Result<'T, DecodeError>`, erased to `obj`.
let private resultOf (boxed: obj) : Result<obj, DecodeError> =
    let case, fields = FSharpValue.GetUnionFields(boxed, boxed.GetType())

    match case.Name with
    | "Ok" -> Ok fields.[0]
    | _ -> Error(fields.[0] :?> DecodeError)

/// The plan's binding for a type, by its spelling, or None for an inline root.
let private bindingFor (plan: GenerationPlan) (t: Type) : string option =
    plan.Bindings
    |> List.tryFind (fun b -> TypePlan.typeSpelling b = Plan.typeSpelling t)
    |> Option.map TypePlan.binding

/// Every named type a plan binds, with the binding.
let private namedTypes (plan: GenerationPlan) (roots: Type list) : (Type * string) list =
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

    roots |> List.iter walk

    seen
    |> Seq.choose (fun t -> bindingFor plan t |> Option.map (fun b -> t, b))
    |> Seq.sortBy (snd)
    |> List.ofSeq

let private oracle =
    ToolUp.Remoting.Json.SystemTextJson.FableConverters.decoderOracle

let private draws = 24
let private seed = 853

/// Draw `draws` values of `t`, each as (index, value).
let private drawn (t: Type) : (int * obj) list =
    let rng = Random(seed)

    [
        for i in 0 .. draws - 1 do
            match DecoderShapes.draw rng DecoderShapes.DefaultDepth t with
            | Ok v -> yield i, v
            | Error reason -> failtestf "%s cannot be drawn: %s" t.Name reason
    ]

/// Two values of `t` agree when the converter set writes them identically.
let private agree (t: Type) (a: obj) (b: obj) = oracle.Write t a = oracle.Write t b

[<Tests>]
let tests =
    testList "Phase 853 — generated client proxies and encoders" [

        testList "853.C — the platform's records, generated and pinned" [

            testCase "853.C — the committed PlatformClientProxies.fs is what the generator emits"
            <| fun () ->
                let expected, _ = platformEmission ()

                pinned
                    "TOOLUP_REGEN_PLATFORM_CLIENT_PROXIES"
                    "src/ToolUp.Platform.Client/Client/Remoting/PlatformClientProxies.fs"
                    expected

            testCase "853.C — the committed ReadCatalogClientProxies.fs fixture is what the generator emits"
            <| fun () ->
                let expected, _ = fixtureEmission ()

                pinned
                    "TOOLUP_REGEN_PLATFORM_CLIENT_PROXIES"
                    "src/ToolUp.Platform.Tests/Remoting/ReadCatalogClientProxies.fs"
                    expected

            testCase "853.C — every generated binding carries a doc comment"
            <| fun () ->
                let source, _ = platformEmission ()
                let lines = (normalise source).Split '\n'

                let undocumented = [
                    for i in 1 .. lines.Length - 1 do
                        let line = lines[i]

                        if
                            line.StartsWith "    let "
                            || line.StartsWith "    let rec "
                            || line.StartsWith "    and "
                        then
                            if not (lines[i - 1].TrimStart().StartsWith "///") then
                                yield line.Trim()
                ]

                Expect.isEmpty undocumented "each generated binding is tracked public surface and needs its doc line"

            testCase
                "853.C — the census: every platform record is generated except the declared ones, each for its reason"
            <| fun () ->
                let _, plan = platformEmission ()
                let records = platformRecords ()

                let skipped = plan.Skipped |> List.map (fun r -> r.RefusedType, r.Why)

                Expect.equal
                    (skipped |> List.map fst)
                    (declaredSkipped |> List.map fst)
                    (sprintf
                        "the skipped set moved without its declaration (`declaredSkipped`):\n%s"
                        (Emit.clientSkipReport plan))

                for (name, reason) in declaredSkipped do
                    let why = skipped |> List.find (fst >> (=) name) |> snd
                    Expect.stringContains why reason (sprintf "%s is skipped for the declared reason" name)

                Expect.equal
                    (List.length plan.Records)
                    (List.length records - List.length declaredSkipped)
                    "every other record is generated"

                Expect.isGreaterThan (List.length plan.Records) 30 "the census reaches the platform's API records"

                Expect.equal
                    ToolUp.Remoting.Client.PlatformClientProxies.coveredApiRecords
                    (plan.Records |> List.map _.RecordKey)
                    "the committed module covers exactly the generated records"

            testCase "853.B — a generated record's path names no reflective call"
            <| fun () ->
                let source, _ = platformEmission ()

                for forbidden in
                    [
                        "createTypeInfo"
                        "Convert.serialize"
                        "Convert.fromJsonAs"
                        "SimpleJson"
                        "typeof<"
                    ] do
                    Expect.isFalse (source.Contains forbidden) (sprintf "the generated module never calls %s" forbidden)
        ]

        testList "853.A — one walk, two vocabularies" [

            testCase "the encoder plan refuses exactly what the decoder plan refuses, over the same roots"
            <| fun () ->
                let roots = platformRecords () |> List.collect Plan.argumentTypes |> List.distinct

                let decode = Plan.forJsonTypes roots
                let encode = Plan.forJsonEncoders roots

                Expect.equal encode.Refusals decode.Refusals "the refusals are one walk's"

                Expect.equal
                    (encode.Roots |> List.map _.RootFullName)
                    (decode.Roots |> List.map _.RootFullName)
                    "the planned roots are one walk's"

                Expect.equal
                    (encode.Bindings |> List.map TypePlan.typeSpelling)
                    (decode.Bindings |> List.map TypePlan.typeSpelling)
                    "the bound types, in order, are one walk's"

            testCase "the encoder vocabulary: the writer's conventions, by name and by case name"
            <| fun () ->
                let json v = JsonEncode.toText v

                Expect.equal (json (JsonEncode.int64 42L)) "\"+42\"" "int64 is the signed string"
                Expect.equal (json (JsonEncode.int64 -42L)) "\"-42\"" "a negative int64 keeps its sign"
                Expect.equal (json (JsonEncode.uint64 42UL)) "\"42\"" "uint64 is the digit string"
                Expect.equal (json (JsonEncode.float 1.0)) "1.0" "an integral double keeps the writer's .0"
                Expect.equal (json (JsonEncode.option JsonEncode.int32 None)) "null" "None is null"
                Expect.equal (json (JsonEncode.option JsonEncode.int32 (Some 3))) "3" "Some is its value"
                Expect.equal (json (JsonEncode.case0 "A")) "\"A\"" "a field-less case is its name"
                Expect.equal (json (JsonEncode.payload "B" (JsonEncode.int32 1))) "{\"B\":1}" "one field"

                Expect.equal
                    (json (JsonEncode.fields "C" [ JsonEncode.int32 1; JsonEncode.string "x" ]))
                    "{\"C\":[1,\"x\"]}"
                    "several fields"

                Expect.equal
                    (json (JsonEncode.string "q\"\\\n\u0001é"))
                    "\"q\\\"\\\\\\n\\u0001é\""
                    "strings escape as StringConverter.Write does"

                Expect.equal
                    (JsonEncode.arguments [ JsonEncode.int32 1; JsonEncode.unit () ])
                    "[1,null]"
                    "the request body is the argument array"

                // The writer itself, for the same values.
                for (t, v, text) in
                    [
                        typeof<int64>, box 42L, "\"+42\""
                        typeof<float>, box 1.0, "1.0"
                        typeof<int option>, box (None: int option), "null"
                    ] do
                    Expect.equal (oracle.Write t v) text (sprintf "the converter set writes %s the same way" t.Name)
        ]

        testList "853.D — cross-host agreement" [

            testCase
                "853.D — every generated argument encoder's text decodes through the argument seam's both paths to the value drawn"
            <| fun () ->
                let _, plan = platformEmission ()
                let generated = plan.Records |> List.map _.RecordName |> Set.ofList

                let records = platformRecords () |> List.filter (fun r -> generated.Contains r.Name)

                let roots = records |> List.collect Plan.argumentTypes |> List.distinct
                let decoderPlan = Plan.forJsonTypes roots
                let named = namedTypes plan.Encoders roots

                Expect.isGreaterThan (List.length named) 20 "the platform's composite argument types are exercised"

                let mutable checkedDraws = 0

                for (t, binding) in named do
                    let encoder =
                        bindingValue clientAssembly "ToolUp.Remoting.Client.PlatformClientProxies" binding

                    let decoder =
                        match bindingFor decoderPlan t with
                        | Some d -> bindingValue coreAssembly "ToolUp.Remoting.Json.PlatformJsonDecoders" d
                        | None -> failtestf "%s has an encoder but no Phase 841 decoder" t.Name

                    for (i, value) in drawn t do
                        let text = JsonEncode.toText (apply encoder value :?> JsonValue)

                        // The algebra path: the value model, then the
                        // record's registered (generated) decoder.
                        match oracle.Read text with
                        | Error e ->
                            failtestf
                                "%s draw %d: the generated text did not parse: %s\n%s"
                                t.Name
                                i
                                (DecodeError.render e)
                                text
                        | Ok model ->
                            match resultOf (apply decoder model) with
                            | Error e ->
                                failtestf
                                    "%s draw %d: the algebra decoder refused the generated text: %s\n%s"
                                    t.Name
                                    i
                                    (DecodeError.render e)
                                    text
                            | Ok decoded ->
                                Expect.isTrue
                                    (agree t decoded value)
                                    (sprintf
                                        "%s draw %d: the algebra path read back a different value from\n%s"
                                        t.Name
                                        i
                                        text)

                        // The converter set's own reader — the path a
                        // deployment that registers nothing takes. It is
                        // held to what it reads from the WRITER's own text
                        // for the same value, so the one loss it declares
                        // (`decoderOracle.Losses`: a `TimeSpan` read through
                        // a double comes back a tick off) is the reader's on
                        // both sides and never scored against the encoder.
                        match oracle.Decode t text, oracle.Decode t (oracle.Write t value) with
                        | Error e, _ ->
                            failtestf
                                "%s draw %d: System.Text.Json refused the generated text: %s\n%s"
                                t.Name
                                i
                                (DecodeError.render e)
                                text
                        | _, Error e ->
                            failtestf
                                "%s draw %d: System.Text.Json refused its own writing: %s"
                                t.Name
                                i
                                (DecodeError.render e)
                        | Ok decoded, Ok reference ->
                            Expect.isTrue
                                (agree t decoded reference)
                                (sprintf
                                    "%s draw %d: System.Text.Json read the generated text differently from the writer's own\n%s"
                                    t.Name
                                    i
                                    text)

                        checkedDraws <- checkedDraws + 1

                Expect.isGreaterThan checkedDraws 500 "the agreement ran over real draws"

            testCase
                "853.D — every generated response decoder reads the converter set's writing back to the value drawn"
            <| fun () ->
                let _, plan = platformEmission ()
                let generated = plan.Records |> List.map _.RecordName |> Set.ofList

                let roots =
                    platformRecords ()
                    |> List.filter (fun r -> generated.Contains r.Name)
                    |> List.collect Plan.returnTypes
                    |> List.distinct

                let named = namedTypes plan.Decoders roots
                Expect.isGreaterThan (List.length named) 50 "the platform's response types are exercised"

                for (t, binding) in named do
                    let decoder =
                        bindingValue clientAssembly "ToolUp.Remoting.Client.PlatformClientProxies" binding

                    for (i, value) in drawn t do
                        let text = oracle.Write t value

                        match oracle.Read text with
                        | Error e -> failtestf "%s draw %d: %s" t.Name i (DecodeError.render e)
                        | Ok model ->
                            match resultOf (apply decoder model) with
                            | Error e ->
                                failtestf
                                    "%s draw %d: the generated decoder refused the writer's text: %s\n%s"
                                    t.Name
                                    i
                                    (DecodeError.render e)
                                    text
                            | Ok decoded ->
                                Expect.isTrue
                                    (agree t decoded value)
                                    (sprintf
                                        "%s draw %d: the generated decoder read a different value from\n%s"
                                        t.Name
                                        i
                                        text)

            testCase
                "853.D — the pinned fixture (compiled into both packs): the .NET encoder's text, read by the server's argument seam"
            <| fun () ->
                Expect.isNonEmpty ClientEncoderFixture.cases "the fixture is not vacuous"

                for c in ClientEncoderFixture.cases do
                    Expect.equal
                        (c.Encode())
                        c.Pinned
                        (sprintf "%s: the generated encoder writes the pinned text" c.Name)

                    Expect.equal
                        (c.DecodesToValue c.Pinned)
                        (Ok())
                        (sprintf "%s: the Phase 841 decoder reads it back" c.Name)

                    use document = Text.Json.JsonDocument.Parse c.Pinned

                    match
                        ToolUp.Remoting.Json.SystemTextJson.FableConverters.tryDeserialiseElement
                            (Some c.Record)
                            document.RootElement
                            c.ValueType
                            ToolUp.Remoting.Json.SystemTextJson.FableConverters.shared
                    with
                    | Ok decoded ->
                        Expect.isTrue
                            (agree c.ValueType decoded c.Value)
                            (sprintf "%s: the server's argument seam reads the value" c.Name)
                    | Error e -> failtestf "%s: the server's argument seam refused it: %s" c.Name (DecodeError.render e)

            testCase "853.D — the go-red case: a perturbed encoder is caught by the agreement"
            <| fun () ->
                // Write `ExportRequestInput`-shaped text with two members
                // swapped in VALUE: the agreement must see it.
                let _, plan = platformEmission ()
                let roots = platformRecords () |> List.collect Plan.argumentTypes |> List.distinct

                let (t, binding) =
                    namedTypes plan.Encoders roots
                    |> List.find (fun (t, _) -> FSharpType.IsRecord t)

                let encoder =
                    bindingValue clientAssembly "ToolUp.Remoting.Client.PlatformClientProxies" binding

                let perturbed (value: obj) =
                    match apply encoder value :?> JsonValue with
                    | JsonValue.Object((name, _) :: rest) -> JsonValue.Object((name, JsonValue.Null) :: rest)
                    | other -> other

                let caught =
                    drawn t
                    |> List.exists (fun (_, value) ->
                        match oracle.Decode t (JsonEncode.toText (perturbed value)) with
                        | Error _ -> true
                        | Ok decoded -> not (agree t decoded value))

                Expect.isTrue caught (sprintf "nulling %s's first member is caught" t.Name)
        ]

        testList "854 finding — read policies are emitted from the attributes" [

            testCase "the plan reads a record's attributes into exactly ReadPolicies.ofAttributes"
            <| fun () ->
                let _, plan = fixtureEmission ()
                let record = plan.Records |> List.exactlyOne

                let planned =
                    record.ReadPolicies
                    |> List.map (fun p ->
                        p.PolicyMethod,
                        ({
                            MaxAgeSeconds = p.MaxAgeSeconds
                            Invalidates = p.Invalidates
                        }
                        : ToolUp.Remoting.Client.ReadPolicy))

                Expect.equal
                    planned
                    (ToolUp.Remoting.Client.ReadPolicies.ofAttributes typeof<ReadPolicyFixture.ReadCatalogApi>)
                    "the generator reads the attributes the .NET host reads"

                Expect.equal
                    planned
                    ReadPolicyFixture.declarations
                    "and the declaration the Fable pack registers by hand"

            testCase "a record with no declared policy registers nothing — the platform's records are unchanged"
            <| fun () ->
                let source, plan = platformEmission ()
                Expect.isTrue (plan.Records |> List.forall (fun r -> List.isEmpty r.ReadPolicies)) "no platform policy"
                Expect.isFalse (source.Contains "ReadPolicies.register") "so no registration is emitted"
        ]
    ]