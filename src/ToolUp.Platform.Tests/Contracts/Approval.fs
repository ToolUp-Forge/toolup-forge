// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The one reading of "regenerate this projection instead of checking it".
///
/// Every generated artefact the tests hold — the API baselines, the
/// composition baselines, the generated decoders and proxies, the generated
/// references, the corpora — is a row of `projections.json` at the repository
/// root, and each row names the STEM that approves it. One variable admits
/// stems:
///
///   TOOLUP_APPROVE=api                        every API baseline
///   TOOLUP_APPROVE=api:ToolUp.Platform.Core   that one baseline
///   TOOLUP_APPROVE=composition,config-reference
///   TOOLUP_APPROVE=1                          every stem
///
/// A value is a comma- or semicolon-separated list. `1` or `true` admits
/// everything; any other entry is a stem, or `stem:name` to admit one name
/// within it (a name is matched case-insensitively, with a trailing
/// `.approved.txt` tolerated so a name pasted from a failure works). An
/// unset or empty variable admits nothing.
///
/// The switches this replaces (`TOOLUP_APPROVE_API`, `TOOLUP_REGEN_*`,
/// `TOOLUP_EMIT_WIRE_FIXTURES`, …) are each still read, under the same
/// truthiness, until 0.43.0; reading one prints which stem it admitted and
/// how to spell it (GP 11).
///
/// BCL-only, so it is source-linked into every pack that holds a projection
/// and into the build driver, which renders `.fantomasignore` from the
/// registry (`Projections`).
namespace ToolUp.Platform.Tests.Contracts

open System
open System.IO
open System.Text.Json

[<RequireQualifiedAccess>]
module Approve =

    /// What the environment admits for one stem.
    [<RequireQualifiedAccess>]
    type Admission =
        | Nothing
        | Everything
        /// These names within the stem, and no others.
        | Names of Set<string>

    /// The variable every projection reads.
    [<Literal>]
    let Variable = "TOOLUP_APPROVE"

    /// The retired switches, by the stem each now spells. Accepted until 0.43.0.
    let legacy: (string * string) list = [
        "TOOLUP_APPROVE_API", "api"
        "TOOLUP_APPROVE_COMPOSITION", "composition"
        "TOOLUP_APPROVE_BRANDKIT", "brandkit"
        "TOOLUP_APPROVE_OPENXML_GOLDENS", "openxml-goldens"
        "TOOLUP_REGEN_PLATFORM_DECODERS", "platform-decoders"
        "TOOLUP_REGEN_PLATFORM_JSON_DECODERS", "platform-json-decoders"
        "TOOLUP_REGEN_PLATFORM_CLIENT_PROXIES", "platform-client-proxies"
        "TOOLUP_REGEN_LOCALIZATION_SKELETON", "localization-skeleton"
        "TOOLUP_REGEN_ELMISH_PROOF_CORPUS", "elmish-proof-corpus"
        "TOOLUP_REGEN_CONFIG_REFERENCE", "config-reference"
        "TOOLUP_REGEN_AUDIT_EVENT_REFERENCE", "audit-event-reference"
        "TOOLUP_REGEN_TRUST_BOUNDARIES", "trust-boundaries"
        "TOOLUP_EMIT_WIRE_FIXTURES", "wire-fixtures"
    ]

    let private isEverything (entry: string) =
        entry = "1" || entry.Equals("true", StringComparison.OrdinalIgnoreCase)

    let private entries (value: string) =
        value.Split([| ','; ';' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map _.Trim()
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> List.ofArray

    let private bareName (name: string) =
        if name.EndsWith(".approved.txt", StringComparison.OrdinalIgnoreCase) then
            name.Substring(0, name.Length - ".approved.txt".Length)
        else
            name

    let private combine (a: Admission) (b: Admission) =
        match a, b with
        | Admission.Everything, _
        | _, Admission.Everything -> Admission.Everything
        | Admission.Nothing, x
        | x, Admission.Nothing -> x
        | Admission.Names x, Admission.Names y -> Admission.Names(Set.union x y)

    /// What a `TOOLUP_APPROVE` value admits for `stem`. Pure, so the rule is
    /// testable without touching the environment.
    let read (stem: string) (value: string) : Admission =
        match value with
        | null
        | "" -> Admission.Nothing
        | v ->
            entries v
            |> List.map (fun entry ->
                if isEverything entry || entry.Equals(stem, StringComparison.OrdinalIgnoreCase) then
                    Admission.Everything
                else
                    match entry.IndexOf ':' with
                    | i when i > 0 && entry.Substring(0, i).Equals(stem, StringComparison.OrdinalIgnoreCase) ->
                        Admission.Names(Set.singleton (bareName (entry.Substring(i + 1).Trim())))
                    | _ -> Admission.Nothing)
            |> List.fold combine Admission.Nothing

    /// What a retired switch's value admits: `1` / `true` is everything, `0` /
    /// `false` nothing, any other value a list of names (the API switch's
    /// assembly scope).
    let readLegacy (value: string) : Admission =
        match value with
        | null
        | "" -> Admission.Nothing
        | v when isEverything (v.Trim()) -> Admission.Everything
        | v when v.Trim() = "0" || v.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) -> Admission.Nothing
        | v ->
            match entries v |> List.map bareName with
            | [] -> Admission.Nothing
            | names -> Admission.Names(Set.ofList names)

    let private announced = Collections.Concurrent.ConcurrentDictionary<string, unit>()

    /// What the environment admits for `stem`: `TOOLUP_APPROVE`, joined with the
    /// retired switch for the stem when one is set (which prints, once per
    /// process, what it admitted and how to spell it now).
    let admission (stem: string) : Admission =
        let current = read stem (Environment.GetEnvironmentVariable Variable)

        legacy
        |> List.filter (fun (_, s) -> s = stem)
        |> List.map (fun (name, _) ->
            let value = Environment.GetEnvironmentVariable name
            let admitted = readLegacy value

            if admitted <> Admission.Nothing && announced.TryAdd(name, ()) then
                let spelled =
                    match admitted with
                    | Admission.Names names -> names |> Seq.map (sprintf "%s:%s" stem) |> String.concat ","
                    | _ -> stem

                eprintfn
                    "%s=%s admitted stem `%s`; spell it %s=%s (the old name is read until 0.43.0)."
                    name
                    value
                    stem
                    Variable
                    spelled

            admitted)
        |> List.fold combine current

    /// Whether anything of `stem` is to be regenerated on this run.
    let admit (stem: string) : bool = admission stem <> Admission.Nothing

    /// Whether `name` within `stem` is to be regenerated on this run.
    let admitName (stem: string) (name: string) : bool =
        match admission stem with
        | Admission.Nothing -> false
        | Admission.Everything -> true
        | Admission.Names names ->
            names
            |> Set.exists (fun n -> n.Equals(name, StringComparison.OrdinalIgnoreCase))

    /// The names a run is scoped to within `stem`, or `None` for every name.
    let scope (stem: string) : Set<string> option =
        match admission stem with
        | Admission.Names names -> Some names
        | Admission.Nothing
        | Admission.Everything -> None

/// The registry of generated artefacts, `projections.json` at the repository
/// root, and the renders it drives.
[<RequireQualifiedAccess>]
module Projections =

    /// How a projection is regenerated.
    [<RequireQualifiedAccess>]
    type Regen =
        /// Run this test pack with the row's stem admitted, optionally only
        /// the lists whose name contains `filter`.
        | Pack of project: string * filter: string option
        /// Run this build-driver target with these arguments.
        | Target of name: string * args: string list
        /// Run this script.
        | Script of path: string
        /// Build this project; its generator writes the projection.
        | Build of project: string

    /// One generated artefact.
    type Row = {
        /// Repository-relative; `*` matches within one path segment, and a
        /// trailing `/` names a directory.
        Path: string
        /// What renders it.
        Render: string
        /// The stem that approves a regeneration.
        Stem: string
        /// The test or check that holds it to its renderer.
        HeldBy: string
        Regen: Regen
        /// Listed in `.fantomasignore`: an F# file nobody wrote.
        FantomasIgnore: bool
        /// The variable naming the tree it lives in, when not this repository.
        External: string option
        /// Committed to the repository (a render on demand is not).
        Committed: bool
    }

    /// A working directory of a generator, which Fantomas must not read.
    type Output = { Path: string; Why: string }

    type Registry = { Rows: Row list; Outputs: Output list }

    /// The registry's file name, at the repository root.
    [<Literal>]
    let File = "projections.json"

    let private str (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let private required (e: JsonElement) (name: string) =
        match str e name with
        | Some v -> v
        | None -> failwithf "%s: a row has no `%s`: %s" File name (e.GetRawText())

    let private flag (e: JsonElement) (name: string) (fallback: bool) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.True -> true
        | true, v when v.ValueKind = JsonValueKind.False -> false
        | _ -> fallback

    let private regenOf (row: JsonElement) =
        let r = row.GetProperty "regen"

        let args =
            match r.TryGetProperty "args" with
            | true, a -> [ for x in a.EnumerateArray() -> x.GetString() ]
            | _ -> []

        match str r "project", str r "target", str r "script", str r "build" with
        | Some p, None, None, None -> Regen.Pack(p, str r "filter")
        | None, Some t, None, None -> Regen.Target(t, args)
        | None, None, Some s, None -> Regen.Script s
        | None, None, None, Some b -> Regen.Build b
        | _ ->
            failwithf
                "%s: a row's `regen` names exactly one of project, target, script or build: %s"
                File
                (r.GetRawText())

    /// The registry a `projections.json` text declares.
    let parse (json: string) : Registry =
        use doc = JsonDocument.Parse json
        let root = doc.RootElement

        {
            Rows = [
                for row in root.GetProperty("projections").EnumerateArray() ->
                    {
                        Path = required row "path"
                        Render = required row "render"
                        Stem = required row "stem"
                        HeldBy = required row "heldBy"
                        Regen = regenOf row
                        FantomasIgnore = flag row "fantomasIgnore" false
                        External = str row "external"
                        Committed = flag row "committed" true
                    }
            ]
            Outputs = [
                for o in root.GetProperty("outputs").EnumerateArray() ->
                    {
                        Path = required o "path"
                        Why = required o "why"
                    }
            ]
        }

    /// The registry at `<root>/projections.json`.
    let read (root: string) : Registry =
        parse (IO.File.ReadAllText(Path.Combine(root, File)))

    /// `.fantomasignore`, whole: every F# projection and every generator's
    /// working directory, each under the line that says what it is. Fantomas
    /// reads this file rather than `.gitignore`, and formatting a generated
    /// file would break the byte comparison that holds it.
    let renderFantomasIgnore (r: Registry) : string =
        [
            "# GENERATED from projections.json — do not edit. Add or change a row there, then"
            "# `dotnet run --project Build.fsproj -- Projections --render`."
            "#"
            "# Fantomas reads this file rather than .gitignore. Every entry is source nobody"
            "# wrote: a projection held byte for byte to its renderer, or a generator's working"
            "# directory. Formatting one would break the comparison that holds it."
            for row in r.Rows |> List.filter _.FantomasIgnore do
                ""
                sprintf "# %s (held by %s)" row.Render row.HeldBy
                row.Path
            for o in r.Outputs do
                ""
                sprintf "# %s" o.Why
                o.Path
        ]
        |> List.map (fun l -> l + "\n")
        |> String.concat ""