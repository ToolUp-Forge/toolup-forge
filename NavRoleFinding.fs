// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The `effectiveNavRole` reliance finding.
///
/// A client module that declares no `NavRole` is gated by its sidebar GROUP:
/// `SidebarVisibility.effectiveNavRole` maps a platform-scoped group onto
/// `PlatformAdminOnly`. That fallback is deprecated and is removed at 1.0,
/// together with this finding, on the evidence the finding gathers: the
/// modules in this tree that would lose their gate when it goes.
///
/// The finding never fails the gate. It reads every module declaration under
/// `src/`, `samples/` and `templates/`, and prints the ids of the modules that
/// declare a platform-scoped group and no nav role, or "none".
///
/// The platform-scoped groups are read from `SidebarVisibility.fs` itself, the
/// one place they are declared, so the finding cannot disagree with the rule
/// it reports on. FAKE-free and source-linked into the Platform test pack,
/// where `SidebarVisibilityContractTests` pins its reading against the rule.
module ToolUp.Forge.NavRoleFinding

open System
open System.IO
open System.Text.RegularExpressions
open ToolUp.Platform

/// A module relying on the group-name fallback.
type Reliance = {
    /// The module id the file declares (its `moduleId`, `withId` or `Id`),
    /// or the file name when it declares none.
    ModuleId: string
    /// The platform-scoped group it is gated by.
    Group: string
    /// The file, relative to the scanned root, forward slashes.
    File: string
}

/// The relative path of the rule's source under the repository root.
let sidebarVisibilityPath =
    Path.Combine("src", "ToolUp.Platform.Client", "Client", "SidebarVisibility.fs")

let private literal =
    Regex(@"^\s*let\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*""([^""]*)""", RegexOptions.Multiline)

let private groupSet =
    Regex(@"let\s+private\s+platformAdminSidebarGroups[^=]*=\s*Set\.ofList\s*\[([^\]]*)\]")

/// The platform-scoped sidebar groups `SidebarVisibility.fs` declares, its
/// `[<Literal>]` names resolved. `Error` when the declaration cannot be read:
/// a finding over no groups would report "none" about nothing.
let platformGroups (sidebarVisibilitySource: string) : Result<string list, string> =
    let literals =
        literal.Matches sidebarVisibilitySource
        |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value)
        |> Map.ofSeq

    let m = groupSet.Match sidebarVisibilitySource

    if not m.Success then
        Error "SidebarVisibility.fs declares no `platformAdminSidebarGroups` set the finding can read"
    else
        let items =
            m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> List.ofArray

        let resolved =
            items
            |> List.map (fun item ->
                if item.StartsWith "\"" && item.EndsWith "\"" then
                    Ok(item.Trim '"')
                else
                    match literals.TryFind item with
                    | Some value -> Ok value
                    | None -> Error $"the platform group `{item}` names no literal in SidebarVisibility.fs")

        match
            resolved
            |> List.tryPick (function
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some e -> Error e
        | None when List.isEmpty resolved -> Error "SidebarVisibility.fs declares an empty platform group set"
        | None ->
            Ok(
                resolved
                |> List.choose (function
                    | Ok g -> Some g
                    | Error _ -> None)
            )

let private declaresModule =
    Regex(@"ClientModule\.create\b|ClientModule\.register\b|:\s*BuiltIn<")

let private groupDeclaration =
    Regex(@"withGroup\s+""([^""]+)""|\bGroup\s*=\s*(?:Some\s*)?""([^""]+)""")

let private navRoleDeclaration =
    Regex(@"withNavRole\b|\bNavRole\s*=\s*(?:Some\b|NavRole\.)")

let private moduleIdDeclaration =
    Regex(@"let\s+moduleId\s*=\s*""([^""]+)""|withId\s+""([^""]+)""|\bId\s*=\s*""([^""]+)""")

let private codeOnly (source: string) =
    source.Split('\n')
    |> Array.filter (fun line -> not (line.TrimStart().StartsWith "//"))
    |> String.concat "\n"

let private firstGroup (m: Match) =
    m.Groups
    |> Seq.skip 1
    |> Seq.tryFind _.Success
    |> Option.map _.Value
    |> Option.defaultValue ""

/// The reliance one module source declares: it declares a module, gates it by
/// a platform-scoped group, and declares no nav role. `file` names it.
let read (groups: string list) (file: string) (source: string) : Reliance option =
    let code = codeOnly source

    if not (declaresModule.IsMatch code) || navRoleDeclaration.IsMatch code then
        None
    else
        groupDeclaration.Matches code
        |> Seq.map firstGroup
        |> Seq.tryFind (fun g -> List.contains g groups)
        |> Option.map (fun group -> {
            ModuleId =
                match moduleIdDeclaration.Match code with
                | m when m.Success -> firstGroup m
                | _ -> Path.GetFileNameWithoutExtension file
            Group = group
            File = file
        })

let private scanned = [ "src"; "samples"; "templates" ]

let private excludedSegment (relative: string) =
    relative.Split('/')
    |> Array.exists (fun s -> s = "bin" || s = "obj" || s = "node_modules" || s = "output")

/// Every reliance under `root`'s `src/`, `samples/` and `templates/`, in path
/// order, or the reason the rule's groups could not be read.
let scan (root: string) : Result<Reliance list, string> =
    let rule = Path.Combine(root, sidebarVisibilityPath)

    if not (File.Exists rule) then
        Error $"no {sidebarVisibilityPath} under {root}"
    else
        platformGroups (File.ReadAllText rule)
        |> Result.map (fun groups -> [
            for dir in scanned do
                let full = Path.Combine(root, dir)

                if Directory.Exists full then
                    for f in Directory.EnumerateFiles(full, "*.fs", SearchOption.AllDirectories) |> Seq.sort do
                        let relative = Path.GetRelativePath(root, f).Replace('\\', '/')

                        if not (excludedSegment relative) && not (relative.Contains "Tests/") then
                            match read groups relative (File.ReadAllText f) with
                            | Some reliance -> yield reliance
                            | None -> ()
        ])

/// The finding's lines, as the gate prints them.
let render (reliances: Reliance list) : string list =
    match reliances with
    | [] -> [
        "effectiveNavRole fallback: none — every module in the tree declares its nav role."
      ]
    | _ ->
        $"effectiveNavRole fallback: {List.length reliances} module(s) gated by their sidebar group alone (declare `withNavRole` before 1.0 removes the fallback):"
        :: (reliances
            |> List.map (fun r -> $"  {r.ModuleId} (group \"{r.Group}\", {r.File})"))

/// The gate leg: prints the finding through `print` and passes. It fails only
/// when the rule's groups cannot be read, since "none" would then be a claim
/// about nothing.
let leg (print: string -> unit) (root: string) : Verdict =
    match scan root with
    | Error why -> Verdict.Fail $"effectiveNavRole finding: {why}"
    | Ok reliances ->
        render reliances |> List.iter print
        Verdict.Pass None