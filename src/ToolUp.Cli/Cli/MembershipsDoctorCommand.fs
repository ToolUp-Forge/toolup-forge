// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `toolup memberships doctor` — detect (and, with `--repair`, fix the
/// provably-safe subset of) membership-integrity drift in a deployment's
/// local-file blob layout: email-string-keyed membership blobs from the
/// pre-invite-flow add path, blobs keyed by an id the identity sanitiser
/// refuses (e.g. a raw provider-prefixed JWT `sub`), rows naming a
/// purged team, and active-team pointers at a team the user is not a
/// member of.
///
/// Pure BCL (GP 2 — the base CLI carries no SDK dependency): the walk,
/// the JSON parse, the id sanitisation, and the classification each
/// mirror the server-side `MembershipDoctor` substrate and MUST match
/// it — a fixture-tree parity test in `ToolUp.Cli.Tests` pins the two
/// to the same classification (the Phase 166 stamp round-trip
/// anti-drift mechanism). The server substrate is the composition-
/// friendly form (injected reads/writes, audit + cache-evict emission);
/// this command is the offline thin driver for local-file deployments.
///
/// Offline repair note: `--repair` edits the blob files directly, so it
/// writes no `MemberRemoved` audit events and publishes no cache-evict
/// envelopes — run it against a stopped deployment (or accept that live
/// resolver caches evict by TTL). An audited live repair drives
/// `MembershipDoctor.repair` in-process instead.
module ToolUp.Cli.MembershipsDoctorCommand

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Cli.Dispatch

// ── Identity sanitisation (mirror) ───────────────────────────────────
// MUST match `ToolUp.Platform.Auth.IdentitySanitiser.sanitiseScopeId`
// (accept/reject AND reason category) — the Cli.Tests parity test pins
// both. Conservative: alphanumerics, hyphen, underscore, period, length
// 1..256, no leading period, no Windows reserved device name.

[<Literal>]
let private MaxIdLength = 256

let private windowsReserved = [
    "CON"
    "PRN"
    "AUX"
    "NUL"
    "COM1"
    "COM2"
    "COM3"
    "COM4"
    "COM5"
    "COM6"
    "COM7"
    "COM8"
    "COM9"
    "LPT1"
    "LPT2"
    "LPT3"
    "LPT4"
    "LPT5"
    "LPT6"
    "LPT7"
    "LPT8"
    "LPT9"
]

/// Mirror of the server-side scope-id sanitiser. `Error reason` uses the
/// same categorised reasons (no attacker-controlled bytes in the reason).
let sanitiseScopeId (value: string) : Result<string, string> =
    if isNull value then
        Error "null"
    elif value = "" then
        Error "empty"
    elif value.Length > MaxIdLength then
        Error(sprintf "length %d exceeds maximum %d" value.Length MaxIdLength)
    elif value.StartsWith "." then
        Error "leading period (Unix dotfile / Windows reserved)"
    else
        let invalid =
            value
            |> Seq.tryFind (fun ch -> not (Char.IsLetterOrDigit ch || ch = '-' || ch = '_' || ch = '.'))

        match invalid with
        | Some ch ->
            let category =
                if ch = '/' || ch = '\\' then "path separator"
                elif ch = '\u0000' then "NUL byte"
                elif Char.IsControl ch then "control character"
                elif ch = ' ' then "whitespace"
                else "disallowed character"

            Error(sprintf "contains %s" category)
        | None ->
            let bareName =
                let dotIdx = value.IndexOf '.'
                if dotIdx >= 0 then value.Substring(0, dotIdx) else value

            if
                List.exists (fun (r: string) -> r.Equals(bareName, StringComparison.OrdinalIgnoreCase)) windowsReserved
            then
                Error "Windows reserved device name"
            else
                Ok value

// ── Findings (mirror of MembershipDoctor's report shape) ─────────────
// Stringly-kinded because the CLI is presentation + exit-code; the
// parity test maps the server DU onto these tokens.

[<Literal>]
let KindEmailKeyedRow = "email-keyed-row"

[<Literal>]
let KindUnresolvableRow = "unresolvable-row"

[<Literal>]
let KindOrphanTeamRow = "orphan-team-row"

[<Literal>]
let KindDanglingActiveTeam = "dangling-active-team"

[<Literal>]
let RepairDeleteRow = "delete-row"

[<Literal>]
let RepairClearPointer = "clear-pointer"

[<Literal>]
let RepairReportOnly = "report-only"

type Finding = {
    Kind: string
    UserId: string
    TeamId: string option
    Evidence: string
    Repair: string
}

// ── Local-file blob layout walk ──────────────────────────────────────
// MUST match `LocalFileStorage` ({baseDir}/{container}/{blobName}) over
// the `_platform` membership layout `TeamStore` owns:
// memberships/{userId}.json, teams/{teamId}.json, active-team/{userId}.txt.

let private platformDir (dataRoot: string) = Path.Combine(dataRoot, "_platform")

let private idsIn (dir: string) (extension: string) : string list =
    if not (Directory.Exists dir) then
        []
    else
        Directory.EnumerateFiles(dir, "*" + extension)
        |> Seq.map Path.GetFileName
        |> Seq.filter (fun n -> n.Length > extension.Length)
        |> Seq.map (fun n -> n.Substring(0, n.Length - extension.Length))
        |> List.ofSeq

/// The team ids a membership blob's rows name, in row order. MUST match
/// `TeamManagement.Json.deserializeMemberships`'s wire shape (camelCase
/// `teamId` in a JSON array).
let private rowTeamIds (membershipFile: string) : string list =
    let doc = JsonDocument.Parse(File.ReadAllText membershipFile)

    [
        for elem in doc.RootElement.EnumerateArray() do
            elem.GetProperty("teamId").GetString()
    ]

let private pointerFile (dataRoot: string) (userId: string) =
    Path.Combine(platformDir dataRoot, "active-team", userId + ".txt")

let private membershipFile (dataRoot: string) (userId: string) =
    Path.Combine(platformDir dataRoot, "memberships", userId + ".json")

let private readPointer (dataRoot: string) (userId: string) : string option =
    let file = pointerFile dataRoot userId

    if not (File.Exists file) then
        None
    else
        match File.ReadAllText(file).Trim() with
        | "" -> None
        | teamId -> Some teamId

// ── Classification (mirror of MembershipDoctor.diagnose) ─────────────
// MUST match the server-side classification: email-keyed takes
// precedence over the sanitiser; a bad blob key taints everything under
// it (report-only, pointer included); a pointer dangles when the user
// holds no row for it naming an existing team.

let private classifyUserIdKey (userId: string) : (string * string) option =
    if userId.Contains '@' then
        Some(KindEmailKeyedRow, "blob is keyed by an email address (pre-invite-flow add path)")
    else
        match sanitiseScopeId userId with
        | Error reason -> Some(KindUnresolvableRow, sprintf "blob key fails identity sanitisation: %s" reason)
        | Ok _ -> None

/// Walk the layout under `dataRoot` and classify the drift. Deterministic
/// order (sorted by user id); a clean tree yields `[]`.
let diagnoseTree (dataRoot: string) : Finding list =
    let root = platformDir dataRoot
    let teamIds = idsIn (Path.Combine(root, "teams")) ".json" |> Set.ofList
    let membershipUsers = idsIn (Path.Combine(root, "memberships")) ".json"
    let pointerUsers = idsIn (Path.Combine(root, "active-team")) ".txt"

    membershipUsers @ pointerUsers
    |> List.distinct
    |> List.sort
    |> List.collect (fun userId ->
        let rows =
            let file = membershipFile dataRoot userId
            if File.Exists file then rowTeamIds file else []

        match classifyUserIdKey userId with
        | Some(kind, evidence) ->
            match rows with
            | [] -> [
                {
                    Kind = kind
                    UserId = userId
                    TeamId = None
                    Evidence = evidence
                    Repair = RepairReportOnly
                }
              ]
            | rows ->
                rows
                |> List.map (fun teamId -> {
                    Kind = kind
                    UserId = userId
                    TeamId = Some teamId
                    Evidence = evidence
                    Repair = RepairReportOnly
                })
        | None ->
            let orphanFindings =
                rows
                |> List.filter (fun teamId -> not (teamIds.Contains teamId))
                |> List.map (fun teamId -> {
                    Kind = KindOrphanTeamRow
                    UserId = userId
                    TeamId = Some teamId
                    Evidence = "membership row names a team with no team record"
                    Repair = RepairDeleteRow
                })

            let pointerFindings =
                match readPointer dataRoot userId with
                | None -> []
                | Some teamId when rows |> List.exists (fun t -> t = teamId && teamIds.Contains teamId) -> []
                | Some teamId ->
                    let evidence =
                        if rows |> List.contains teamId then
                            "active-team pointer names a team whose membership row is itself orphaned"
                        elif List.isEmpty rows then
                            "active-team pointer exists but the user has no membership rows"
                        else
                            "active-team pointer names a team the user is not a member of"

                    [
                        {
                            Kind = KindDanglingActiveTeam
                            UserId = userId
                            TeamId = Some teamId
                            Evidence = evidence
                            Repair = RepairClearPointer
                        }
                    ]

            orphanFindings @ pointerFindings)

// ── Repair (safe subset only) ────────────────────────────────────────
// MUST match `MembershipDoctor.repair`'s safe subset: strip orphan-team
// rows (JsonNode edit — untouched rows keep their exact fields), delete
// dangling pointer files. Report-only findings are never acted on.

/// Apply the safe subset for `findings` under `dataRoot`, returning the
/// findings actually repaired.
let repairTree (dataRoot: string) (findings: Finding list) : Finding list =
    let safe =
        findings
        |> List.filter (fun f -> f.Repair = RepairDeleteRow || f.Repair = RepairClearPointer)

    let rowDeletions =
        safe
        |> List.filter (fun f -> f.Repair = RepairDeleteRow)
        |> List.groupBy _.UserId

    for (userId, userFindings) in rowDeletions do
        let orphanTeams = userFindings |> List.choose _.TeamId |> Set.ofList
        let file = membershipFile dataRoot userId

        match JsonNode.Parse(File.ReadAllText file) with
        | :? JsonArray as rows ->
            let kept = JsonArray()

            for row in rows |> Seq.toList do
                let teamId =
                    row["teamId"] |> Option.ofObj |> Option.map (fun n -> n.GetValue<string>())

                match teamId with
                | Some t when orphanTeams.Contains t -> ()
                | _ ->
                    rows.Remove row |> ignore
                    kept.Add row

            File.WriteAllText(file, kept.ToJsonString())
        | _ -> ()

    for f in safe |> List.filter (fun f -> f.Repair = RepairClearPointer) do
        let file = pointerFile dataRoot f.UserId

        if File.Exists file then
            File.Delete file

    safe

// ── Rendering + command ──────────────────────────────────────────────

let private renderTable (findings: Finding list) : string list =
    let rows =
        findings
        |> List.map (fun f -> [
            f.Kind
            f.UserId
            (f.TeamId |> Option.defaultValue "-")
            f.Repair
            f.Evidence
        ])

    let header = [ "KIND"; "USER"; "TEAM"; "REPAIR"; "EVIDENCE" ]
    let all = header :: rows

    let widths =
        [ 0..3 ]
        |> List.map (fun i -> all |> List.map (fun r -> r[i].Length) |> List.max)

    let renderRow (r: string list) =
        let padded = widths |> List.mapi (fun i w -> r[i].PadRight w)
        String.concat "  " (padded @ [ r[4] ])

    all |> List.map renderRow

let private helpText = [
    "Usage: toolup memberships doctor --data-root <dir> [--repair]"
    ""
    "Detects membership-integrity drift in a deployment's local-file blob layout"
    "(<data-root>/_platform/{memberships,teams,active-team}): email-keyed membership"
    "blobs, blobs keyed by an unresolvable id, rows naming a purged team, and"
    "dangling active-team pointers. Exits non-zero when findings exist (CI-friendly)."
    ""
    "Options:"
    "  --data-root <dir>   The deployment's local blob-storage root. (required)"
    "  --repair            Apply the provably-safe subset: delete rows naming a"
    "                      nonexistent team; clear dangling active-team pointers."
    "                      Email-keyed / unresolvable blobs are never touched — they"
    "                      need an operator to re-add the member under the resolved id."
    ""
    "Offline repair edits blob files directly: no audit events, no cache-evict"
    "publications. Run against a stopped deployment, or drive the in-process"
    "membership doctor for an audited live repair."
]

let private usageError (message: string) =
    eprintfn "toolup memberships doctor: %s" message
    eprintfn ""
    helpText |> List.iter (eprintfn "%s")
    ExitUsage

type private Options = {
    DataRoot: string option
    Repair: bool
}

let rec private parse (opts: Options) (args: string list) : Result<Options, string> =
    match args with
    | [] -> Ok opts
    | "--data-root" :: v :: rest -> parse { opts with DataRoot = Some v } rest
    | [ "--data-root" ] -> Error "missing value for --data-root"
    | "--repair" :: rest -> parse { opts with Repair = true } rest
    | unknown :: _ -> Error(sprintf "unrecognised argument: %s" unknown)

let private runWith (opts: Options) : int =
    match opts.DataRoot with
    | None -> usageError "--data-root is required"
    | Some dataRoot ->
        if not (Directory.Exists dataRoot) then
            eprintfn "toolup memberships doctor: data root '%s' does not exist" dataRoot
            ExitRuntimeError
        else
            let findings = diagnoseTree dataRoot

            let remaining =
                if opts.Repair && not (List.isEmpty findings) then
                    let repaired = repairTree dataRoot findings

                    for f in repaired do
                        printfn
                            "repaired %s: %s%s"
                            f.Kind
                            f.UserId
                            (f.TeamId |> Option.map (sprintf " (team %s)") |> Option.defaultValue "")

                    // Re-diagnose so the report (and the exit code) reflect
                    // the post-repair store, exactly like a fresh run would.
                    diagnoseTree dataRoot
                else
                    findings

            if List.isEmpty remaining then
                printfn "membership store is clean — no findings"
                ExitOk
            else
                renderTable remaining |> List.iter (printfn "%s")
                printfn ""
                printfn "%d finding(s)" (List.length remaining)
                ExitRuntimeError

let command = {
    Path = [ "memberships"; "doctor" ]
    Summary = "Detect (and optionally repair) membership-integrity drift."
    Help = helpText
    Run =
        fun args ->
            match parse { DataRoot = None; Repair = false } args with
            | Error message -> usageError message
            | Ok opts -> runWith opts
}