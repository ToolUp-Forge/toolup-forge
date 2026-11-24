// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Whether every Fuaran package this repository's graph resolves can bind, at run
/// time, the Fuaran types and members its assemblies reference.
///
/// **The defect this decides.** A Fuaran package is compiled against the Fuaran
/// substrate it declared as its floor. NuGet resolves one version of each id for the
/// whole graph, the highest floor any package declares, so a package built on an
/// older substrate runs against the newer one this repository pins. Pre-1.0, a minor
/// release may remove or retype a member. The solution still compiles, because this
/// repository's own code never calls that member, and the package fails when it runs:
/// `MissingMethodException`, `MissingFieldException` or `TypeLoadException`, often
/// inside a type initialiser. `Fuaran.DB` 0.2.0, built on `Fuaran.Core` 0.42.0, calls a
/// `RuleFamily` constructor that `Fuaran.Core` 0.45.0 no longer has.
///
/// **The rule.** Every type reference and member reference an assembly makes into a
/// Fuaran assembly is resolved against the Fuaran assemblies the graph resolves, the
/// way the runtime binds it. A reference that does not resolve, or resolves to
/// something the referencing assembly may not access, is a finding. The scan reads
/// metadata only and never loads or runs the package.
///
/// **What counts as resolving.** A scan that reports a reference the runtime would
/// bind is worse than no scan, so each case below is decided as the runtime decides it,
/// and where the scan cannot decide it says so rather than guessing either way:
///
/// - **A member reference binds by name and by its whole signature**, as the runtime
///   binds it: calling convention (static or instance), return type, generic arity
///   and every required parameter. Types in a signature compare by full name, because
///   every type reference is also resolved on its own (below), so a type that moved
///   assembly is judged where it is referenced, not twice.
/// - **An overload chosen by signature** resolves only to the overload with exactly
///   that signature. Another overload of the same name does not satisfy it.
/// - **A generic method** is a member reference whose signature carries its arity and
///   `!!n` parameters; it binds exactly as any other. Its instantiation (a method
///   specification) names that same member reference, so it is not scanned again.
/// - **A member of a generic type instantiation** is looked up on the generic type,
///   with its `!n` parameters unsubstituted, which is how the definition spells them.
/// - **A method or field that moved to a base type** still binds: the lookup walks the
///   base types, as the runtime's member lookup does. A constructor never does; it
///   binds on the type it names. Where a base type is a generic instantiation, a
///   member found there matches on name, generic arity and parameter count only (its
///   signature is spelt in the base's own parameters). Where the chain leaves the
///   Fuaran assemblies, the base is read from the running runtime's own types (the
///   shared framework the package runs on) by name and parameter count; a base the
///   runtime cannot load is recorded as unverified, never as a finding.
/// - **A type forwarded to another assembly** resolves through the forwarder, across
///   the resolved assemblies, to the type it forwards to. A forwarder into an assembly
///   outside the Fuaran set is unverified.
/// - **A member an F# union or record compiles to** (its constructor, `NewCase`,
///   `Tag`, `get_Item1`, a property getter) is ordinary metadata and binds exactly as
///   above. A record gaining a field therefore changes its constructor's signature,
///   and the old one is reported, which is the `RuleFamily` break.
/// - **Accessibility.** A type or member that still exists but is no longer
///   accessible fails at run time too. Public, protected and protected-or-internal
///   are accessible (a protected member is reachable from a derived type, which the
///   scan does not follow). Internal and private-protected are accessible only where
///   the target assembly grants `InternalsVisibleTo` to the referencing assembly.
///   Private is never accessible.
/// - **An assembly reference** to a Fuaran assembly the graph does not resolve at all
///   is a finding: the runtime cannot load it.
///
/// **What it cannot see.** A break that is not a missing or inaccessible type or
/// member: changed behaviour, a type named in a string (reflection, an attribute's
/// `typeof` argument, which metadata stores as text), a type or member referenced only
/// by a non-Fuaran assembly, an assembly version above the resolved one, and a
/// member reached only through a base type the runtime here cannot load. Those are
/// still found only by running the package.
///
/// Pure over assembly bytes and BCL-only (`System.Reflection.Metadata` ships with the
/// runtime). Compiled into the build driver and source-linked into
/// `ToolUp.Platform.Build.Tests`, so the gate and its go-red proofs run one
/// implementation. Nothing here fetches: the driver restores, this file reads.
module ToolUp.Forge.FuaranReferenceScan

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open System.Text.Json
open ToolUp.Forge.FuaranCoResolution

// ─── Names and signatures, spelt the way the runtime spells them ─────────────

/// `Ns.Name`, a nested type `Ns.Outer+Inner`.
let rec private typeDefName (r: MetadataReader) (h: TypeDefinitionHandle) : string =
    let d = r.GetTypeDefinition h
    let name = r.GetString d.Name
    let declaring = d.GetDeclaringType()

    if declaring.IsNil then
        match r.GetString d.Namespace with
        | "" -> name
        | ns -> ns + "." + name
    else
        typeDefName r declaring + "+" + name

let rec private typeRefName (r: MetadataReader) (h: TypeReferenceHandle) : string =
    let t = r.GetTypeReference h
    let name = r.GetString t.Name

    if t.ResolutionScope.Kind = HandleKind.TypeReference then
        typeRefName r (TypeReferenceHandle.op_Explicit t.ResolutionScope) + "+" + name
    else
        match r.GetString t.Namespace with
        | "" -> name
        | ns -> ns + "." + name

/// The assembly a type reference resolves in, through any enclosing type; None for
/// a reference into this module or another module of it.
let rec private refScope (r: MetadataReader) (h: TypeReferenceHandle) : string option =
    let t = r.GetTypeReference h

    match t.ResolutionScope.Kind with
    | HandleKind.AssemblyReference ->
        Some(r.GetString(r.GetAssemblyReference(AssemblyReferenceHandle.op_Explicit t.ResolutionScope).Name))
    | HandleKind.TypeReference -> refScope r (TypeReferenceHandle.op_Explicit t.ResolutionScope)
    | _ -> None

/// The generic type a `GenericInst` type specification instantiates.
let private specHead (r: MetadataReader) (h: TypeSpecificationHandle) : EntityHandle option =
    let mutable blob = r.GetBlobReader(r.GetTypeSpecification(h).Signature)

    match blob.ReadSignatureTypeCode() with
    | SignatureTypeCode.GenericTypeInstance ->
        blob.ReadSignatureTypeCode() |> ignore // CLASS or VALUETYPE
        Some(blob.ReadTypeHandle())
    | _ -> None

/// A type handle as (assembly, full name, through a generic instantiation), where
/// `self` is the assembly the reader belongs to.
let private typeKey (r: MetadataReader) (self: string) (h: EntityHandle) : (string * string * bool) option =
    let direct (h: EntityHandle) =
        match h.Kind with
        | HandleKind.TypeDefinition -> Some(self, typeDefName r (TypeDefinitionHandle.op_Explicit h))
        | HandleKind.TypeReference ->
            let t = TypeReferenceHandle.op_Explicit h
            Some(refScope r t |> Option.defaultValue self, typeRefName r t)
        | _ -> None

    if h.IsNil then
        None
    else
        match h.Kind with
        | HandleKind.TypeSpecification ->
            specHead r (TypeSpecificationHandle.op_Explicit h)
            |> Option.bind direct
            |> Option.map (fun (a, n) -> a, n, true)
        | _ -> direct h |> Option.map (fun (a, n) -> a, n, false)

let private primitiveName (code: PrimitiveTypeCode) =
    match code with
    | PrimitiveTypeCode.Void -> "Void"
    | c -> "System." + string c

/// Renders a signature type as text: the comparison key and the message alike.
type private Render() =
    member private this.Provider = this :> ISignatureTypeProvider<string, unit>

    interface ISignatureTypeProvider<string, unit> with
        member _.GetPrimitiveType code = primitiveName code
        member _.GetTypeFromDefinition(r, h, _) = typeDefName r h
        member _.GetTypeFromReference(r, h, _) = typeRefName r h

        member this.GetTypeFromSpecification(r, ctx, h, _) =
            r.GetTypeSpecification(h).DecodeSignature(this.Provider, ctx)

        member _.GetGenericInstantiation(g, args) = g + "<" + String.Join(",", args) + ">"

        member _.GetArrayType(e, shape) =
            e + "[" + String(',', shape.Rank - 1) + "]"

        member _.GetSZArrayType e = e + "[]"
        member _.GetByReferenceType e = e + "&"
        member _.GetPointerType e = e + "*"
        member _.GetPinnedType e = e

        member _.GetModifiedType(modifier, unmodified, required) =
            unmodified + (if required then " modreq(" else " modopt(") + modifier + ")"

        member _.GetGenericTypeParameter(_, i) = "!" + string i
        member _.GetGenericMethodParameter(_, i) = "!!" + string i

        member _.GetFunctionPointerType s =
            "method " + s.ReturnType + " *(" + String.Join(", ", s.ParameterTypes) + ")"

let private render = Render()

/// `[static ]<return> <owner.>name[``arity](<required parameters>)`.
let private methodText (owner: string option) (name: string) (s: MethodSignature<string>) =
    let ps = s.ParameterTypes |> Seq.take s.RequiredParameterCount |> String.concat ", "

    let arity =
        if s.GenericParameterCount > 0 then
            sprintf "``%d" s.GenericParameterCount
        else
            ""

    let owner = owner |> Option.map (fun o -> o + ".") |> Option.defaultValue ""
    let stat = if s.Header.IsInstance then "" else "static "
    sprintf "%s%s %s%s%s(%s)" stat s.ReturnType owner name arity ps

// ─── The resolved assemblies, indexed ────────────────────────────────────────

/// Who may reach a type or member: anyone, the assembly and its friends, nobody
/// outside it. Ordered from least to most restrictive.
type Access =
    | Visible
    | Friends
    | Hidden

type private MethodInfo' = {
    Key: string
    Params: int
    Arity: int
    Access: Access
}

type private TypeInfo' = {
    Access: Access
    /// (assembly, full name, through a generic instantiation).
    Base: (string * string * bool) option
    Methods: Dictionary<string, ResizeArray<MethodInfo'>>
    Fields: Dictionary<string, string * Access>
}

type private AssemblyInfo = {
    Name: string
    Types: Dictionary<string, TypeInfo'>
    /// Full name -> the assembly a type forwarder sends it to.
    Forwards: Dictionary<string, string>
    /// The assemblies `InternalsVisibleTo` names.
    Friends: HashSet<string>
}

/// The resolved assemblies a scan binds against.
type Index = private {
    Assemblies: Dictionary<string, AssemblyInfo>
}

let private openImage (bytes: byte[]) =
    new PEReader(ImmutableArray.Create<byte>(bytes))

/// An assembly's own name, from its metadata.
let assemblyNameOf (bytes: byte[]) : string =
    use pe = openImage bytes
    let r = pe.GetMetadataReader()
    r.GetString(r.GetAssemblyDefinition().Name)

let private typeAccess (attrs: TypeAttributes) =
    match attrs &&& TypeAttributes.VisibilityMask with
    | TypeAttributes.Public
    | TypeAttributes.NestedPublic
    | TypeAttributes.NestedFamily
    | TypeAttributes.NestedFamORAssem -> Visible
    | TypeAttributes.NotPublic
    | TypeAttributes.NestedAssembly
    | TypeAttributes.NestedFamANDAssem -> Friends
    | _ -> Hidden

let private methodAccess (attrs: MethodAttributes) =
    match attrs &&& MethodAttributes.MemberAccessMask with
    | MethodAttributes.Public
    | MethodAttributes.Family
    | MethodAttributes.FamORAssem -> Visible
    | MethodAttributes.Assembly
    | MethodAttributes.FamANDAssem -> Friends
    | _ -> Hidden

let private fieldAccess (attrs: FieldAttributes) =
    match attrs &&& FieldAttributes.FieldAccessMask with
    | FieldAttributes.Public
    | FieldAttributes.Family
    | FieldAttributes.FamORAssem -> Visible
    | FieldAttributes.Assembly
    | FieldAttributes.FamANDAssem -> Friends
    | _ -> Hidden

/// The full name of the type an attribute's constructor belongs to.
let private attributeType (r: MetadataReader) (a: CustomAttribute) =
    match a.Constructor.Kind with
    | HandleKind.MemberReference ->
        let parent =
            r.GetMemberReference(MemberReferenceHandle.op_Explicit a.Constructor).Parent

        if parent.Kind = HandleKind.TypeReference then
            Some(typeRefName r (TypeReferenceHandle.op_Explicit parent))
        else
            None
    | HandleKind.MethodDefinition ->
        Some(typeDefName r (r.GetMethodDefinition(MethodDefinitionHandle.op_Explicit a.Constructor).GetDeclaringType()))
    | _ -> None

let private friendsOf (r: MetadataReader) =
    let friends = HashSet<string>(StringComparer.OrdinalIgnoreCase)

    for h in r.GetAssemblyDefinition().GetCustomAttributes() do
        let a = r.GetCustomAttribute h

        if attributeType r a = Some "System.Runtime.CompilerServices.InternalsVisibleToAttribute" then
            let mutable blob = r.GetBlobReader a.Value

            if blob.Length > 2 && blob.ReadUInt16() = 1us then
                match blob.ReadSerializedString() with
                | null -> ()
                | text -> friends.Add(text.Split(',').[0].Trim()) |> ignore

    friends

let rec private exportedName (r: MetadataReader) (h: ExportedTypeHandle) : string * string option =
    let e = r.GetExportedType h
    let name = r.GetString e.Name

    match e.Implementation.Kind with
    | HandleKind.ExportedType ->
        let outer, target = exportedName r (ExportedTypeHandle.op_Explicit e.Implementation)
        outer + "+" + name, target
    | HandleKind.AssemblyReference ->
        let full =
            match r.GetString e.Namespace with
            | "" -> name
            | ns -> ns + "." + name

        full, Some(r.GetString(r.GetAssemblyReference(AssemblyReferenceHandle.op_Explicit e.Implementation).Name))
    | _ -> name, None

let private readAssembly (bytes: byte[]) : AssemblyInfo =
    use pe = openImage bytes
    let r = pe.GetMetadataReader()
    let self = r.GetString(r.GetAssemblyDefinition().Name)
    let types = Dictionary<string, TypeInfo'>(StringComparer.Ordinal)

    let rec effectiveAccess (h: TypeDefinitionHandle) =
        let d = r.GetTypeDefinition h
        let own = typeAccess d.Attributes
        let declaring = d.GetDeclaringType()

        if declaring.IsNil then
            own
        else
            max own (effectiveAccess declaring)

    for h in r.TypeDefinitions do
        let d = r.GetTypeDefinition h
        let methods = Dictionary<string, ResizeArray<MethodInfo'>>(StringComparer.Ordinal)

        for mh in d.GetMethods() do
            let m = r.GetMethodDefinition mh
            let name = r.GetString m.Name
            let s = m.DecodeSignature(render, ())

            let overloads =
                match methods.TryGetValue name with
                | true, list -> list
                | _ ->
                    let list = ResizeArray()
                    methods[name] <- list
                    list

            overloads.Add {
                Key = methodText None name s
                Params = s.RequiredParameterCount
                Arity = s.GenericParameterCount
                Access = methodAccess m.Attributes
            }

        let fields = Dictionary<string, string * Access>(StringComparer.Ordinal)

        for fh in d.GetFields() do
            let f = r.GetFieldDefinition fh
            fields[r.GetString f.Name] <- (f.DecodeSignature(render, ()), fieldAccess f.Attributes)

        types[typeDefName r h] <- {
            Access = effectiveAccess h
            Base = typeKey r self d.BaseType
            Methods = methods
            Fields = fields
        }

    let forwards = Dictionary<string, string>(StringComparer.Ordinal)

    for h in r.ExportedTypes do
        match exportedName r h with
        | full, Some target -> forwards[full] <- target
        | _, None -> ()

    {
        Name = self
        Types = types
        Forwards = forwards
        Friends = friendsOf r
    }

/// Index the resolved assemblies. A second image of one assembly name replaces the
/// first.
let index (images: byte[] seq) : Index =
    let assemblies = Dictionary<string, AssemblyInfo>(StringComparer.OrdinalIgnoreCase)

    for bytes in images do
        let a = readAssembly bytes
        assemblies[a.Name] <- a

    { Assemblies = assemblies }

// ─── The scan ────────────────────────────────────────────────────────────────

/// Why a reference does not bind.
type FindingKind =
    /// The graph resolves no assembly of that name.
    | AssemblyAbsent
    /// The assembly has no such type, and forwards none.
    | TypeAbsent
    /// The type exists, and the referencing assembly may not reach it.
    | TypeInaccessible
    /// The type has no member of that name and signature.
    | MemberAbsent
    /// The member exists, and the referencing assembly may not reach it.
    | MemberInaccessible

/// One reference that will not bind at run time.
type Finding = {
    /// The referencing assembly.
    Assembly: string
    /// The assembly the reference names.
    Target: string
    Kind: FindingKind
    /// The type, or the member as `<return> <type>.<name>(<parameters>)`.
    Subject: string
}

/// One assembly's references, judged.
type Report = {
    Assembly: string
    /// How many references into the target set were judged.
    Checked: int
    Findings: Finding list
    /// References the scan could not judge either way, each with why.
    Unverified: string list
}

type private Resolution =
    | Found of AssemblyInfo * TypeInfo'
    | Missing of FindingKind * target: string
    | Unseen of string

let rec private resolveType (ix: Index) (isTarget: string -> bool) (asm: string) (name: string) (depth: int) =
    match ix.Assemblies.TryGetValue asm with
    | true, a ->
        match a.Types.TryGetValue name with
        | true, t -> Found(a, t)
        | _ ->
            match a.Forwards.TryGetValue name with
            | true, next when depth < 8 ->
                if ix.Assemblies.ContainsKey next || isTarget next then
                    resolveType ix isTarget next name (depth + 1)
                else
                    Unseen(sprintf "%s is forwarded to %s, outside the resolved set" name next)
            | _ -> Missing(TypeAbsent, asm)
    | _ when isTarget asm -> Missing(AssemblyAbsent, asm)
    | _ -> Unseen(sprintf "%s lives in %s, outside the resolved set" name asm)

let private reachable (target: AssemblyInfo) (referencing: string) (access: Access) =
    match access with
    | Visible -> true
    | Friends -> target.Friends.Contains referencing
    | Hidden -> false

type private Member =
    | Method of name: string * key: string * paramCount: int * arity: int
    | Field of name: string * fieldType: string

type private Lookup =
    | Bound of AssemblyInfo * Access
    | NotFound
    | Undecided of string

/// A base type outside the resolved set, read from the running runtime: does it,
/// or a type it derives from, declare a member of this name and shape?
let private runtimeHas (asm: string) (typeName: string) (m: Member) : bool option =
    let load (name: string) =
        try
            Type.GetType(name, false)
        with _ ->
            null

    let t =
        match load (typeName + ", " + asm) with
        | null -> load typeName
        | t -> t

    match t with
    | null -> None
    | t ->
        let flags =
            BindingFlags.Public
            ||| BindingFlags.NonPublic
            ||| BindingFlags.Instance
            ||| BindingFlags.Static
            ||| BindingFlags.DeclaredOnly

        let rec onChain (t: Type) =
            not (isNull t)
            && (match m with
                | Method(name, _, count, arity) ->
                    t.GetMethods flags
                    |> Array.exists (fun mi ->
                        mi.Name = name
                        && mi.GetParameters().Length = count
                        && mi.GetGenericArguments().Length = arity)
                | Field(name, _) -> not (isNull (t.GetField(name, flags))))
            || onChain t.BaseType

        Some(onChain t)

/// Find `m` on the type, then up its base types (never for a constructor). Once
/// the walk passes through a generic instantiation it matches by shape only.
let rec private lookup (ix: Index) isTarget (a: AssemblyInfo) (t: TypeInfo') (m: Member) (exact: bool) : Lookup =
    let here =
        match m with
        | Method(name, key, count, arity) ->
            match t.Methods.TryGetValue name with
            | true, overloads ->
                overloads
                |> Seq.tryFind (fun o ->
                    if exact then
                        o.Key = key
                    else
                        o.Params = count && o.Arity = arity)
                |> Option.map _.Access
            | _ -> None
        | Field(name, fieldType) ->
            match t.Fields.TryGetValue name with
            | true, (ty, access) when not exact || ty = fieldType -> Some access
            | _ -> None

    match here, m with
    | Some access, _ -> Bound(a, access)
    | None, Method((".ctor" | ".cctor"), _, _, _) -> NotFound
    | None, _ ->
        match t.Base with
        | None -> NotFound
        | Some(baseAsm, baseName, generic) ->
            match resolveType ix isTarget baseAsm baseName 0 with
            | Found(a', t') -> lookup ix isTarget a' t' m (exact && not generic)
            | Missing _
            | Unseen _ when isTarget baseAsm ->
                Undecided(sprintf "its base type %s (%s) is not in the resolved set" baseName baseAsm)
            | Missing _
            | Unseen _ ->
                match runtimeHas baseAsm baseName m with
                | Some true -> Bound(a, Visible)
                | Some false -> NotFound
                | None -> Undecided(sprintf "its base type %s (%s) is not readable here" baseName baseAsm)

/// Every reference `subject` makes into an assembly `isTarget` accepts, resolved
/// against `ix`.
let scan (ix: Index) (isTarget: string -> bool) (subject: byte[]) : Report =
    use pe = openImage subject
    let r = pe.GetMetadataReader()
    let self = r.GetString(r.GetAssemblyDefinition().Name)
    let findings = ResizeArray<Finding>()
    let unverified = ResizeArray<string>()
    let mutable checkedCount = 0

    let finding target kind subjectText =
        findings.Add {
            Assembly = self
            Target = target
            Kind = kind
            Subject = subjectText
        }

    for h in r.TypeReferences do
        match refScope r h with
        | Some asm when isTarget asm ->
            checkedCount <- checkedCount + 1
            let name = typeRefName r h

            match resolveType ix isTarget asm name 0 with
            | Found(a, t) ->
                if not (reachable a self t.Access) then
                    finding a.Name TypeInaccessible name
            | Missing(kind, target) -> finding target kind (if kind = AssemblyAbsent then target else name)
            | Unseen why -> unverified.Add why
        | _ -> ()

    for h in r.MemberReferences do
        let mr = r.GetMemberReference h

        match typeKey r self mr.Parent with
        | Some(asm, typeName, _) when asm <> self && isTarget asm ->
            // An unresolvable parent type is the type pass's finding already.
            match resolveType ix isTarget asm typeName 0 with
            | Found(a, t) ->
                checkedCount <- checkedCount + 1
                let name = r.GetString mr.Name

                let m, shown =
                    match mr.GetKind() with
                    | MemberReferenceKind.Method ->
                        let s = mr.DecodeMethodSignature(render, ())

                        Method(name, methodText None name s, s.RequiredParameterCount, s.GenericParameterCount),
                        methodText (Some typeName) name s
                    | _ ->
                        let ty = mr.DecodeFieldSignature(render, ())
                        Field(name, ty), sprintf "%s %s.%s" ty typeName name

                match lookup ix isTarget a t m true with
                | Bound(owner, access) ->
                    if not (reachable owner self access) then
                        finding owner.Name MemberInaccessible shown
                | NotFound -> finding a.Name MemberAbsent shown
                | Undecided why -> unverified.Add(sprintf "%s: %s" shown why)
            | Missing _
            | Unseen _ -> ()
        | _ -> ()

    {
        Assembly = self
        Checked = checkedCount
        Findings = findings |> Seq.distinct |> List.ofSeq
        Unverified = unverified |> Seq.distinct |> List.ofSeq
    }

/// The assemblies a scan of this repository's graph judges references into.
let isFuaranAssembly (name: string) = (familyOf name).IsSome

// ─── The packages: what to restore, and what the restore resolved ────────────

/// A package the restore resolved.
type ResolvedPackage = {
    Id: string
    Version: string
    /// The package's directory under the package folder, `/`-separated.
    Path: string
    /// Its runtime assemblies for the target framework, relative to `Path`.
    Runtime: string list
}

/// What a scan is asked about: the packages to restore, and families lifted to a
/// version (every package of the family the first restore resolves, re-pinned there).
type Request = { Packages: Pin list; Lifts: Pin list }

/// `Id=Version` names a package; `Family.*=Version` lifts a family (`Fuaran.Core.*`).
let requestOf (args: string list) : Result<Request, string list> =
    let parsed =
        args
        |> List.map (fun (a: string) ->
            match a.Split('=', 2) with
            | [| id; version |] when
                (familyOf (id.TrimEnd('*').TrimEnd('.'))).IsSome
                && (SemVer.tryParse version).IsSome
                ->
                if id.EndsWith(".*", StringComparison.Ordinal) then
                    Ok(
                        Choice2Of2 {
                            Id = id.Substring(0, id.Length - 2)
                            Version = version
                        }
                    )
                else
                    Ok(Choice1Of2 { Id = id; Version = version })
            | _ -> Error(sprintf "`%s` is neither `Fuaran.<Id>=<version>` nor `Fuaran.<Family>.*=<version>`" a))

    match
        parsed
        |> List.choose (function
            | Error e -> Some e
            | Ok _ -> None)
    with
    | [] ->
        let ok =
            parsed
            |> List.choose (function
                | Ok c -> Some c
                | Error _ -> None)

        Ok {
            Packages =
                ok
                |> List.choose (function
                    | Choice1Of2 p -> Some p
                    | Choice2Of2 _ -> None)
            Lifts =
                ok
                |> List.choose (function
                    | Choice2Of2 p -> Some p
                    | Choice1Of2 _ -> None)
        }
    | errors -> Error errors

/// The packages to reference on the second restore: the first restore's
/// resolution, with every package of a lifted family re-pinned at the lift, over
/// the requested packages.
let lifted (request: Request) (resolved: ResolvedPackage list) : Pin list =
    let liftOf (id: string) =
        request.Lifts
        |> List.tryFind (fun l -> familyOf id = Some l.Id)
        |> Option.map _.Version

    let raised = [
        for p in resolved do
            match liftOf p.Id with
            | Some v -> { Id = p.Id; Version = v }
            | None -> ()
    ]

    let named =
        request.Packages
        |> List.filter (fun p -> not (raised |> List.exists (fun r -> r.Id = p.Id)))

    named @ raised

/// A project that only restores: every package referenced at its version, from
/// nuget.org alone (the nuget.config beside it clears every other source).
let restoreProject (packages: Pin list) : string =
    let refs =
        packages
        |> List.map (fun p ->
            sprintf
                "    <PackageReference Include=\"%s\" Version=\"%s\" />"
                (Security.SecurityElement.Escape p.Id)
                (Security.SecurityElement.Escape p.Version))
        |> String.concat "\n"

    "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <!-- Fuaran reference scan: the packages to resolve, restored and never built. -->\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n    <WarningsAsErrors>NU1603;NU1605</WarningsAsErrors>\n  </PropertyGroup>\n  <ItemGroup>\n"
    + refs
    + "\n  </ItemGroup>\n</Project>\n"

/// The package folder and the packages a `project.assets.json` resolved for
/// `framework`, each with its runtime assemblies.
let packagesOfAssets (framework: string) (json: string) : Result<string * ResolvedPackage list, string> =
    try
        use doc = JsonDocument.Parse json
        let root = doc.RootElement

        let folder =
            root.GetProperty("packageFolders").EnumerateObject()
            |> Seq.map _.Name
            |> Seq.tryHead

        match folder, root.GetProperty("targets").TryGetProperty framework with
        | None, _ -> Error "the assets file names no package folder"
        | _, (false, _) -> Error(sprintf "the assets file has no `%s` target" framework)
        | Some folder, (true, target) ->
            let libraries = root.GetProperty "libraries"

            let packages = [
                for entry in target.EnumerateObject() do
                    match entry.Value.TryGetProperty "type" with
                    | true, kind when kind.GetString() = "package" ->
                        let id, version =
                            match entry.Name.Split('/', 2) with
                            | [| id; version |] -> id, version
                            | _ -> entry.Name, ""

                        let runtime =
                            match entry.Value.TryGetProperty "runtime" with
                            | true, files ->
                                files.EnumerateObject()
                                |> Seq.map _.Name
                                |> Seq.filter (fun f -> f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                |> List.ofSeq
                            | _ -> []

                        {
                            Id = id
                            Version = version
                            Path = libraries.GetProperty(entry.Name).GetProperty("path").GetString()
                            Runtime = runtime
                        }
                    | _ -> ()
            ]

            Ok(folder, packages)
    with
    | :? JsonException as e -> Error(sprintf "the assets file is not JSON: %s" e.Message)
    | :? KeyNotFoundException as e -> Error(sprintf "the assets file lacks a member: %s" e.Message)

// ─── Verdicts ────────────────────────────────────────────────────────────────

/// One Fuaran package of the resolved graph, judged.
type PackageVerdict = {
    Package: ResolvedPackage
    /// The `Fuaran.*` floors its nuspec declares: what it was built on.
    BuiltOn: Map<string, string>
    Reports: Report list
}

let isRefused (v: PackageVerdict) =
    v.Reports |> List.exists (fun r -> not r.Findings.IsEmpty)

/// The lowest floor `v` declares in each family it depends on.
let private familyFloors (v: PackageVerdict) : (string * string) list =
    v.BuiltOn
    |> Map.toList
    |> List.choose (fun (id, floor) -> familyOf id |> Option.map (fun f -> f, floor))
    |> List.groupBy fst
    |> List.map (fun (family, floors) ->
        floors
        |> List.map snd
        |> List.sortWith (fun a b ->
            match SemVer.tryParse a, SemVer.tryParse b with
            | Some x, Some y -> SemVer.compare x y
            | _ -> String.CompareOrdinal(a, b))
        |> List.head
        |> fun floor -> family, floor)

/// The floor `v` was built on for the package owning `assembly`: that id's own
/// floor, else the lowest floor it declares in the same family.
let private builtOnFor (v: PackageVerdict) (owner: ResolvedPackage option) (assembly: string) =
    let id = owner |> Option.map _.Id |> Option.defaultValue assembly

    match v.BuiltOn.TryFind id with
    | Some floor -> Some(id, floor)
    | None -> familyFloors v |> List.tryFind (fun (family, _) -> familyOf id = Some family)

let private kindText =
    function
    | AssemblyAbsent -> "an assembly the graph does not resolve"
    | TypeAbsent -> "a type it does not have"
    | TypeInaccessible -> "a type it no longer exposes"
    | MemberAbsent -> "a member it does not have"
    | MemberInaccessible -> "a member it no longer exposes"

/// One line per finding: the package and its version, what it references, the
/// assembly's package and version that lacks it, and the version the package was
/// built on. `owners` maps an assembly name to the package that resolved it.
let describeFinding (owners: string -> ResolvedPackage option) (v: PackageVerdict) (f: Finding) : string =
    let owner = owners f.Target

    let resolved =
        owner
        |> Option.map (fun p -> sprintf "%s %s" p.Id p.Version)
        |> Option.defaultValue f.Target

    let builtOn =
        match builtOnFor v owner f.Target with
        | Some(id, floor) -> sprintf "; %s %s was built on %s %s" v.Package.Id v.Package.Version id floor
        | None -> ""

    sprintf "%s references `%s`, %s in %s%s" f.Assembly f.Subject (kindText f.Kind) resolved builtOn

/// The verdict line: `ok` with what was judged, or the count refused.
let describeVerdict (v: PackageVerdict) : string =
    let judged = v.Reports |> List.sumBy _.Checked
    let findings = v.Reports |> List.sumBy _.Findings.Length
    let unverified = v.Reports |> List.sumBy _.Unverified.Length

    let built =
        match familyFloors v with
        | [] -> "no Fuaran floor"
        | floors ->
            floors
            |> List.map (fun (f, floor) -> sprintf "%s %s" f floor)
            |> String.concat ", "

    let tail =
        if unverified = 0 then
            ""
        else
            sprintf "; %d left unverified" unverified

    if findings = 0 then
        sprintf "%s %s (built on %s): %d Fuaran reference(s) bind%s" v.Package.Id v.Package.Version built judged tail
    else
        sprintf
            "%s %s (built on %s): %d of %d Fuaran reference(s) do not bind%s"
            v.Package.Id
            v.Package.Version
            built
            findings
            judged
            tail