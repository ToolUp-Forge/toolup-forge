// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The Fuaran reference scan, proved offline in both directions.
///
/// Each case builds its assemblies in memory with `MetadataBuilder`: a library
/// shaped like the published substrate after a release, and a package referencing
/// it the way a package built on the release before would. The first case is the
/// `Fuaran.DB` 0.2.0 break in miniature: a record constructor the library no longer
/// has. Every case decided in the scan's header is pinned as the runtime binds it,
/// and every binding case has a twin that does not bind, because a scan that reports
/// nothing satisfies every green case on its own. The last list scans real F#
/// compiler output (the assemblies beside this pack) against the `FSharp.Core` they
/// run on: a scan that reports a reference the runtime binds fails there.
module ToolUp.Platform.Build.Tests.FuaranReferenceScanTests

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open Expecto
open ToolUp.Forge.FuaranCoResolution
open ToolUp.Forge.FuaranReferenceScan

// ─── A metadata-only assembly, built in memory ───────────────────────────────

/// A type as a signature names it.
type private Ty =
    | P of PrimitiveTypeCode
    /// A class, by its assembly and full name.
    | T of string * string
    /// A generic class instantiated.
    | G of string * string * Ty list
    | TV of int
    | MV of int

type private MethodDef = {
    Name: string
    Static: bool
    Arity: int
    Ret: Ty option
    Params: Ty list
    Access: MethodAttributes
}

type private TypeDef = {
    Full: string
    Visibility: TypeAttributes
    Base: Ty option
    Methods: MethodDef list
    Fields: (string * Ty * FieldAttributes) list
}

type private Ref =
    | MethodRef of parent: Ty * MethodDef
    | FieldRef of parent: Ty * name: string * Ty
    | TypeRef of string * string

type private Image = {
    Name: string
    Types: TypeDef list
    Refs: Ref list
    /// Full name -> the assembly the type is forwarded to.
    Forwards: (string * string) list
    /// The assemblies granted `InternalsVisibleTo`.
    Friends: string list
}

let private Runtime = "System.Runtime"
let private Object = T(Runtime, "System.Object")

let private image name : Image = {
    Name = name
    Types = []
    Refs = []
    Forwards = []
    Friends = []
}

let private publicClass full methods : TypeDef = {
    Full = full
    Visibility = TypeAttributes.Public
    Base = Some Object
    Methods = methods
    Fields = []
}

let private instance name ret ps : MethodDef = {
    Name = name
    Static = false
    Arity = 0
    Ret = ret
    Params = ps
    Access = MethodAttributes.Public
}

let private ctor ps = instance ".ctor" None ps

let private staticMethod name ret ps = {
    instance name ret ps with
        Static = true
}

let private str = P PrimitiveTypeCode.String
let private i32 = P PrimitiveTypeCode.Int32

let private split (full: string) =
    match full.LastIndexOf '.' with
    | -1 -> "", full
    | i -> full.Substring(0, i), full.Substring(i + 1)

let private build (img: Image) : byte[] =
    let mb = MetadataBuilder()
    let text (s: string) = mb.GetOrAddString s

    let guid =
        Guid(Security.Cryptography.MD5.HashData(Text.Encoding.UTF8.GetBytes img.Name))

    mb.AddModule(0, text (img.Name + ".dll"), mb.GetOrAddGuid guid, GuidHandle(), GuidHandle())
    |> ignore

    mb.AddAssembly(
        text img.Name,
        System.Version(1, 0, 0, 0),
        StringHandle(),
        BlobHandle(),
        AssemblyFlags(),
        AssemblyHashAlgorithm.None
    )
    |> ignore

    let assemblyRefs = Dictionary<string, AssemblyReferenceHandle>()

    let assemblyRef name =
        match assemblyRefs.TryGetValue name with
        | true, h -> h
        | _ ->
            let h =
                mb.AddAssemblyReference(
                    text name,
                    System.Version(1, 0, 0, 0),
                    StringHandle(),
                    BlobHandle(),
                    AssemblyFlags(),
                    BlobHandle()
                )

            assemblyRefs[name] <- h
            h

    let typeRefs = Dictionary<string * string, TypeReferenceHandle>()

    let typeRef (asm, full) =
        match typeRefs.TryGetValue((asm, full)) with
        | true, h -> h
        | _ ->
            let ns, name = split full

            let h =
                mb.AddTypeReference(
                    (AssemblyReferenceHandle.op_Implicit (assemblyRef asm): EntityHandle),
                    text ns,
                    text name
                )

            typeRefs[(asm, full)] <- h
            h

    let defs =
        img.Types
        |> List.mapi (fun i t -> t.Full, MetadataTokens.TypeDefinitionHandle(i + 2))
        |> dict

    let handleOf (asm: string, full: string) : EntityHandle =
        if asm = img.Name then
            (TypeDefinitionHandle.op_Implicit (defs[full]): EntityHandle)
        else
            (TypeReferenceHandle.op_Implicit (typeRef (asm, full)): EntityHandle)

    let rec encode (e: SignatureTypeEncoder) (t: Ty) =
        match t with
        | P code -> e.PrimitiveType code
        | T(a, f) -> e.Type(handleOf (a, f), false)
        | G(a, f, args) ->
            let enc = e.GenericInstantiation(handleOf (a, f), args.Length, false)

            for x in args do
                encode (enc.AddArgument()) x
        | TV i -> e.GenericTypeParameter i
        | MV i -> e.GenericMethodTypeParameter i

    let methodSig (m: MethodDef) =
        let b = BlobBuilder()

        let ret, ps =
            BlobEncoder(b)
                .MethodSignature(SignatureCallingConvention.Default, m.Arity, not m.Static)
                .Parameters(m.Params.Length)

        match m.Ret with
        | None -> ret.Void()
        | Some t -> encode (ret.Type()) t

        for p in m.Params do
            encode (ps.AddParameter().Type()) p

        mb.GetOrAddBlob b

    let fieldSig (t: Ty) =
        let b = BlobBuilder()
        encode (BlobEncoder(b).Field().Type()) t
        mb.GetOrAddBlob b

    let parentOf (t: Ty) : EntityHandle =
        match t with
        | T(a, f) -> handleOf (a, f)
        | G _ ->
            let b = BlobBuilder()
            encode (BlobEncoder(b).TypeSpecificationSignature()) t
            (TypeSpecificationHandle.op_Implicit (mb.AddTypeSpecification(mb.GetOrAddBlob b)): EntityHandle)
        | other -> failwithf "not a parent: %A" other

    mb.AddTypeDefinition(
        TypeAttributes(),
        StringHandle(),
        text "<Module>",
        EntityHandle(),
        MetadataTokens.FieldDefinitionHandle 1,
        MetadataTokens.MethodDefinitionHandle 1
    )
    |> ignore

    let mutable fields = 1
    let mutable methods = 1

    for t in img.Types do
        let ns, name = split t.Full

        mb.AddTypeDefinition(
            t.Visibility,
            text ns,
            text name,
            (match t.Base with
             | Some b -> parentOf b
             | None -> EntityHandle()),
            MetadataTokens.FieldDefinitionHandle fields,
            MetadataTokens.MethodDefinitionHandle methods
        )
        |> ignore

        for name, ty, access in t.Fields do
            mb.AddFieldDefinition(access, text name, fieldSig ty) |> ignore
            fields <- fields + 1

        for m in t.Methods do
            let attrs =
                m.Access
                ||| (if m.Static then
                         MethodAttributes.Static
                     else
                         MethodAttributes())

            mb.AddMethodDefinition(
                attrs,
                MethodImplAttributes.IL,
                text m.Name,
                methodSig m,
                -1,
                MetadataTokens.ParameterHandle 1
            )
            |> ignore

            methods <- methods + 1

    for r in img.Refs do
        match r with
        | MethodRef(parent, m) -> mb.AddMemberReference(parentOf parent, text m.Name, methodSig m) |> ignore
        | FieldRef(parent, name, t) -> mb.AddMemberReference(parentOf parent, text name, fieldSig t) |> ignore
        | TypeRef(asm, full) -> typeRef (asm, full) |> ignore

    for full, target in img.Forwards do
        let ns, name = split full

        mb.AddExportedType(
            enum<TypeAttributes> 0x00200000,
            text ns,
            text name,
            (AssemblyReferenceHandle.op_Implicit (assemblyRef target): EntityHandle),
            0
        )
        |> ignore

    for friend in img.Friends do
        let ivt =
            typeRef (Runtime, "System.Runtime.CompilerServices.InternalsVisibleToAttribute")

        let ctorSig = methodSig (ctor [ str ])

        let ctorRef =
            mb.AddMemberReference((TypeReferenceHandle.op_Implicit (ivt): EntityHandle), text ".ctor", ctorSig)

        let value = BlobBuilder()
        value.WriteUInt16 1us
        value.WriteSerializedString friend
        value.WriteUInt16 0us

        mb.AddCustomAttribute(
            EntityHandle.AssemblyDefinition,
            (MemberReferenceHandle.op_Implicit (ctorRef): EntityHandle),
            mb.GetOrAddBlob value
        )
        |> ignore

    let pe =
        ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), MetadataRootBuilder mb, BlobBuilder())

    let out = BlobBuilder()
    pe.Serialize out |> ignore
    out.ToArray()

// ─── The substrate after a release, and a package built on the one before ────

let private Lib = "Fuaran.Core.Lib"
let private Pkg = "Fuaran.Pkg"

let private rule = T(Lib, "Fuaran.Core.Rule")

/// The released library: a record whose constructor gained a field, a method with
/// overloads, a generic method, a member on a base type, a generic type, an F#
/// union's members, an internal type and member, and a type forwarded on.
let private library (friends: string list) =
    build {
        image Lib with
            Friends = friends
            Forwards = [ "Fuaran.Core.Moved", "Fuaran.Core.Other" ]
            Types = [
                {
                    publicClass "Fuaran.Core.Rule" [
                        ctor [ str; i32 ]
                        instance "Check" (Some(P PrimitiveTypeCode.Boolean)) [ str ]
                        instance "Add" None [ i32 ]
                        instance "Add" None [ str ]
                        {
                            staticMethod "Lift" (Some(MV 0)) [ MV 0 ] with
                                Arity = 1
                        }
                        {
                            instance "Hidden" None [] with
                                Access = MethodAttributes.Assembly
                        }
                        {
                            instance "Secret" None [] with
                                Access = MethodAttributes.Private
                        }
                    ] with
                        Fields = [ "Name", str, FieldAttributes.Public ]
                }
                publicClass "Fuaran.Core.Base" [ ctor []; instance "Inherited" (Some i32) [] ]
                {
                    publicClass "Fuaran.Core.Derived" [] with
                        Base = Some(T(Lib, "Fuaran.Core.Base"))
                }
                publicClass "Fuaran.Core.Gen`2" [ instance "Map" (Some(TV 1)) [ TV 0 ] ]
                publicClass "Fuaran.Core.Union" [
                    staticMethod "NewCase" (Some(T(Lib, "Fuaran.Core.Union"))) [ str ]
                    instance "get_Tag" (Some i32) []
                ]
                {
                    publicClass "Fuaran.Core.Internal" [] with
                        Visibility = TypeAttributes.NotPublic
                }
            ]
    }

let private other =
    build {
        image "Fuaran.Core.Other" with
            Types = [ publicClass "Fuaran.Core.Moved" [] ]
    }

/// A package making exactly `refs`.
let private package (refs: Ref list) = build { image Pkg with Refs = refs }

let private scanned (friends: string list) (refs: Ref list) =
    scan (index [ library friends; other ]) isFuaranAssembly (package refs)

let private findingsOf friends refs = (scanned friends refs).Findings

let private binds (refs: Ref list) =
    let report = scanned [] refs
    Expect.isEmpty report.Findings "every reference binds"
    Expect.isEmpty report.Unverified "and none is left unverified"
    Expect.isGreaterThan report.Checked 0 "the references were judged"

let private refused kind (refs: Ref list) =
    match findingsOf [] refs with
    | [ f ] ->
        Expect.equal f.Kind kind "the kind of break"
        f
    | other -> failtestf "expected one %A finding, got %A" kind other

[<Tests>]
let tests =
    testList "FuaranReferenceScan" [

        testList "a constructor the release removed (the Fuaran.DB break)" [
            testCase "the old constructor is reported, named as the runtime names it" (fun () ->
                let f = refused MemberAbsent [ MethodRef(rule, ctor [ str ]) ]
                Expect.equal f.Subject "Void Fuaran.Core.Rule..ctor(System.String)" "the member, runtime-spelt"
                Expect.equal f.Target Lib "the assembly that lacks it"
                Expect.equal f.Assembly Pkg "the assembly that references it")

            testCase "the constructor the release has binds" (fun () -> binds [ MethodRef(rule, ctor [ str; i32 ]) ])

            testCase "a constructor never binds on a base type" (fun () ->
                refused MemberAbsent [ MethodRef(T(Lib, "Fuaran.Core.Derived"), ctor []) ]
                |> ignore)
        ]

        testList "a member binds by its whole signature" [
            testCase "the overload with that signature binds" (fun () ->
                binds [
                    MethodRef(rule, instance "Add" None [ str ])
                    MethodRef(rule, instance "Add" None [ i32 ])
                ])

            testCase "another overload of the name does not satisfy it" (fun () ->
                refused MemberAbsent [ MethodRef(rule, instance "Add" None [ P PrimitiveTypeCode.Double ]) ]
                |> ignore)

            testCase "the return type is part of it" (fun () ->
                binds [
                    MethodRef(rule, instance "Check" (Some(P PrimitiveTypeCode.Boolean)) [ str ])
                ]

                refused MemberAbsent [ MethodRef(rule, instance "Check" (Some i32) [ str ]) ]
                |> ignore)

            testCase "static or instance is part of it" (fun () ->
                refused MemberAbsent [
                    MethodRef(rule, staticMethod "Check" (Some(P PrimitiveTypeCode.Boolean)) [ str ])
                ]
                |> ignore)

            testCase "a generic method binds on its arity" (fun () ->
                binds [
                    MethodRef(
                        rule,
                        {
                            staticMethod "Lift" (Some(MV 0)) [ MV 0 ] with
                                Arity = 1
                        }
                    )
                ]

                refused MemberAbsent [
                    MethodRef(
                        rule,
                        {
                            staticMethod "Lift" (Some(MV 0)) [ MV 0 ] with
                                Arity = 2
                        }
                    )
                ]
                |> ignore)

            testCase "a member of a generic instantiation binds in the type's own parameters" (fun () ->
                let gen = G(Lib, "Fuaran.Core.Gen`2", [ str; i32 ])
                binds [ MethodRef(gen, instance "Map" (Some(TV 1)) [ TV 0 ]) ]

                refused MemberAbsent [ MethodRef(gen, instance "Map" (Some(TV 0)) [ TV 1 ]) ]
                |> ignore)

            testCase "a field binds by name and type" (fun () ->
                binds [ FieldRef(rule, "Name", str) ]
                refused MemberAbsent [ FieldRef(rule, "Name", i32) ] |> ignore)
        ]

        testList "a member that moved to a base type" [
            testCase "binds through the base, as the runtime's lookup walks it" (fun () ->
                binds [ MethodRef(T(Lib, "Fuaran.Core.Derived"), instance "Inherited" (Some i32) []) ])

            testCase "a member neither the type nor a base has does not" (fun () ->
                refused MemberAbsent [ MethodRef(T(Lib, "Fuaran.Core.Derived"), instance "Gone" (Some i32) []) ]
                |> ignore)

            testCase "a runtime member reached through System.Object binds" (fun () ->
                binds [ MethodRef(rule, instance "ToString" (Some str) []) ])

            testCase "a runtime-shaped member System.Object does not have does not" (fun () ->
                refused MemberAbsent [ MethodRef(rule, instance "ToString" (Some str) [ i32 ]) ]
                |> ignore)
        ]

        testList "an F# union's compiled members bind as any member" [
            testCase "NewCase and get_Tag bind" (fun () ->
                binds [
                    MethodRef(
                        T(Lib, "Fuaran.Core.Union"),
                        staticMethod "NewCase" (Some(T(Lib, "Fuaran.Core.Union"))) [ str ]
                    )
                    MethodRef(T(Lib, "Fuaran.Core.Union"), instance "get_Tag" (Some i32) [])
                ])

            testCase "a case whose fields changed does not" (fun () ->
                refused MemberAbsent [
                    MethodRef(
                        T(Lib, "Fuaran.Core.Union"),
                        staticMethod "NewCase" (Some(T(Lib, "Fuaran.Core.Union"))) [ str; i32 ]
                    )
                ]
                |> ignore)
        ]

        testList "types" [
            testCase "a forwarded type binds where it is forwarded to" (fun () ->
                binds [ TypeRef(Lib, "Fuaran.Core.Moved") ])

            testCase "a type neither defined nor forwarded is reported" (fun () ->
                let f = refused TypeAbsent [ TypeRef(Lib, "Fuaran.Core.Gone") ]
                Expect.equal f.Subject "Fuaran.Core.Gone" "the type")

            testCase "an assembly the graph does not resolve is reported" (fun () ->
                let f =
                    refused AssemblyAbsent [ TypeRef("Fuaran.Core.Missing", "Fuaran.Core.Anything") ]

                Expect.equal f.Target "Fuaran.Core.Missing" "the assembly")

            testCase "a reference outside the Fuaran assemblies is not judged" (fun () ->
                let report = scanned [] [ TypeRef("Some.Other", "Some.Other.Type") ]
                Expect.isEmpty report.Findings "not a Fuaran reference"
                Expect.equal report.Checked 0 "and not counted")
        ]

        testList "accessibility" [
            testCase "an internal type is reported" (fun () ->
                refused TypeInaccessible [ TypeRef(Lib, "Fuaran.Core.Internal") ] |> ignore)

            testCase "an internal member is reported" (fun () ->
                refused MemberInaccessible [ MethodRef(rule, instance "Hidden" None []) ]
                |> ignore)

            testCase "both bind where the library grants InternalsVisibleTo" (fun () ->
                Expect.isEmpty
                    (findingsOf [ Pkg ] [
                        TypeRef(Lib, "Fuaran.Core.Internal")
                        MethodRef(rule, instance "Hidden" None [])
                    ])
                    "a friend reaches both")

            testCase "a private member binds for no one" (fun () ->
                Expect.equal
                    (findingsOf [ Pkg ] [ MethodRef(rule, instance "Secret" None []) ]
                     |> List.map _.Kind)
                    [ MemberInaccessible ]
                    "private is private")
        ]

        testList "real compiler output binds against the FSharp.Core it runs on" [
            let dir = AppContext.BaseDirectory
            let fsharpCore = File.ReadAllBytes(Path.Combine(dir, "FSharp.Core.dll"))
            let ix = index [ fsharpCore ]

            for name in
                [
                    "Expecto.dll"
                    "ToolUp.Platform.Build.Tests.dll"
                    "ToolUp.Platform.Build.dll"
                ] do
                testCase name (fun () ->
                    let report =
                        scan ix (fun a -> a = "FSharp.Core") (File.ReadAllBytes(Path.Combine(dir, name)))

                    Expect.isGreaterThan report.Checked 100 "it references FSharp.Core throughout"
                    Expect.isEmpty report.Findings "every reference the runtime binds, the scan binds")
        ]

        testList "the packages around the scan" [
            testCase "a request names packages and lifts families" (fun () ->
                match requestOf [ "Fuaran.DB=0.2.0"; "Fuaran.Core.*=0.45.0" ] with
                | Ok r ->
                    Expect.equal r.Packages [ { Id = "Fuaran.DB"; Version = "0.2.0" } ] "the package"

                    Expect.equal
                        r.Lifts
                        [
                            {
                                Id = "Fuaran.Core"
                                Version = "0.45.0"
                            }
                        ]
                        "the lift"
                | Error e -> failtestf "refused: %A" e)

            testCase "a request that is not one is refused by name" (fun () ->
                match requestOf [ "Newtonsoft.Json=13.0.1"; "Fuaran.DB" ] with
                | Error e -> Expect.equal e.Length 2 "both named"
                | Ok r -> failtestf "accepted %A" r)

            testCase "a lift re-pins every resolved package of the family" (fun () ->
                let resolved = [
                    for id, v in
                        [
                            "Fuaran.DB", "0.2.0"
                            "Fuaran.Core.Tree", "0.42.0"
                            "Fuaran.Compute.DataFrame", "0.44.0"
                        ] do
                        {
                            Id = id
                            Version = v
                            Path = id.ToLowerInvariant()
                            Runtime = []
                        }
                ]

                let request: Request = {
                    Packages = [ { Id = "Fuaran.DB"; Version = "0.2.0" } ]
                    Lifts = [
                        {
                            Id = "Fuaran.Core"
                            Version = "0.45.0"
                        }
                    ]
                }

                Expect.equal
                    (lifted request resolved)
                    [
                        { Id = "Fuaran.DB"; Version = "0.2.0" }
                        {
                            Id = "Fuaran.Core.Tree"
                            Version = "0.45.0"
                        }
                    ]
                    "Core lifted, Compute left where it resolved")

            testCase "an assets file gives the folder and each package's runtime assemblies" (fun () ->
                let assets =
                    """{ "targets": { "net10.0": {
                          "Fuaran.DB/0.2.0": { "type": "package", "runtime": { "lib/net10.0/Fuaran.DB.dll": {} } },
                          "Fuaran.Core.Observer/0.42.0": { "type": "package", "runtime": { "lib/net10.0/_._": {} } },
                          "Local/1.0.0": { "type": "project" } } },
                        "libraries": { "Fuaran.DB/0.2.0": { "path": "fuaran.db/0.2.0" },
                                       "Fuaran.Core.Observer/0.42.0": { "path": "fuaran.core.observer/0.42.0" } },
                        "packageFolders": { "C:/packages/": {} } }"""

                match packagesOfAssets "net10.0" assets with
                | Ok(folder, packages) ->
                    Expect.equal folder "C:/packages/" "the folder"

                    Expect.equal
                        (packages |> List.map (fun p -> p.Id, p.Path, p.Runtime))
                        [
                            "Fuaran.DB", "fuaran.db/0.2.0", [ "lib/net10.0/Fuaran.DB.dll" ]
                            "Fuaran.Core.Observer", "fuaran.core.observer/0.42.0", []
                        ]
                        "packages only, with their dlls"
                | Error e -> failtest e)

            testCase "a refusal line names the package, the member, both versions" (fun () ->
                let db: ResolvedPackage = {
                    Id = "Fuaran.DB"
                    Version = "0.2.0"
                    Path = ""
                    Runtime = []
                }

                let validator: ResolvedPackage = {
                    Id = "Fuaran.Core.Validator"
                    Version = "0.45.0"
                    Path = ""
                    Runtime = []
                }

                let finding: Finding = {
                    Assembly = "Fuaran.DB"
                    Target = "Fuaran.Core.Validator"
                    Kind = MemberAbsent
                    Subject = "Void Fuaran.Core.RuleFamily`2..ctor(System.String)"
                }

                let verdict: PackageVerdict = {
                    Package = db
                    BuiltOn = Map [ "Fuaran.Core.Validator", "0.42.0" ]
                    Reports = [
                        ({
                            Assembly = "Fuaran.DB"
                            Checked = 1
                            Findings = [ finding ]
                            Unverified = []
                        }
                        : Report)
                    ]
                }

                Expect.isTrue (isRefused verdict) "refused"

                let line =
                    describeFinding
                        (fun a -> if a = "Fuaran.Core.Validator" then Some validator else None)
                        verdict
                        finding

                for part in [ "Fuaran.DB"; "RuleFamily`2..ctor"; "Fuaran.Core.Validator 0.45.0"; "0.42.0" ] do
                    Expect.stringContains line part (sprintf "the line names %s" part))
        ]
    ]