// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.IdentitySeamCensusTests

// Every interface member the platform's server tier and Core declare that
// takes a user id or a team id takes it TYPED: a parameter named `…userId`
// is a `UserId` and one named `…teamId` a `TeamId` (or an option / list of
// one), so passing a team id where a user id is expected is a compile error
// at the seam. A new member that takes either as a `string` reddens this
// list; a deliberate exception is named below with its reason.

open System
open System.Reflection
open Expecto
open ToolUp.Platform

/// The assemblies whose interfaces are the platform's store and service seams.
let private assemblies =
    [
        typeof<UserId>.Assembly // ToolUp.Platform.Core
        typeof<ToolUp.Platform.Infra.BlobMapStoreError>.Assembly
        typeof<ToolUp.Platform.Config.TeamManagement.ITeamStore>.Assembly
        typeof<ToolUp.Platform.Scope.DefaultSubjectResolver.DefaultSubjectResolver>.Assembly
        typeof<ToolUp.Platform.Jobs.IPresenceTracker>.Assembly
        typeof<ToolUp.Platform.Audit.IDataExporter.IDataExporter>.Assembly
        typeof<ToolUp.Platform.Compose.ServerApp>.Assembly
        typeof<ToolUp.AI.IPlatformAIKeyStore>.Assembly
    ]
    |> List.distinct

/// `(interface, member, parameter)` triples that keep a string id on purpose.
let private exceptions: Map<string * string * string, string> = Map.empty

let private kindOf (name: string) =
    if isNull name then
        None
    elif name.EndsWith("userId", StringComparison.OrdinalIgnoreCase) then
        Some typeof<UserId>
    elif name.EndsWith("teamId", StringComparison.OrdinalIgnoreCase) then
        Some typeof<TeamId>
    else
        None

/// The id type a parameter carries, through `option` / `list` / `Set`.
let rec private carried (t: Type) =
    if t.IsGenericType && t.GetGenericArguments().Length = 1 then
        carried (t.GetGenericArguments()[0])
    else
        t

let private offenders () = [
    for asm in assemblies do
        let types =
            try
                asm.GetTypes()
            with :? ReflectionTypeLoadException as e ->
                e.Types |> Array.filter (isNull >> not)

        for t in
            types
            |> Array.filter (fun t -> t.IsInterface && t.IsPublic || t.IsInterface && t.IsNestedPublic) do
            for m in t.GetMethods() do
                for p in m.GetParameters() do
                    match kindOf p.Name with
                    | Some expected when carried p.ParameterType <> expected ->
                        let key = t.FullName, m.Name, p.Name

                        if not (exceptions.ContainsKey key) then
                            yield sprintf "%s.%s(%s: %s)" t.FullName m.Name p.Name p.ParameterType.Name
                    | _ -> ()
]

let tests =
    testList "typed identity — every seam takes a typed id" [
        testCase "no interface member takes a user or team id as a string"
        <| fun _ ->
            let found = offenders ()
            Expect.isEmpty found (sprintf "untyped identity parameters:\n%s" (String.concat "\n" found))

        testCase "the census reaches the seams it guards"
        <| fun _ ->
            // A census that saw no interface would pass vacuously.
            let addMember =
                typeof<ToolUp.Platform.Config.TeamManagement.ITeamStore>.GetMethod("AddMember")

            let types = addMember.GetParameters() |> Array.map _.ParameterType
            Expect.equal types [| typeof<TeamId>; typeof<UserId>; typeof<TeamRole> |] "AddMember is typed"
            Expect.isGreaterThan assemblies.Length 4 "the server parts are all scanned"
    ]