// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Cli.Tests.MembershipsDoctorTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Cli
open ToolUp.Cli.Dispatch

// ─── Phase 546 — memberships-doctor ↔ MembershipDoctor parity ────────
//
// The `toolup memberships doctor` CLI reimplements the server-side
// `MembershipDoctor` classification (and the Phase 131 id sanitiser) in
// pure BCL over the local-file blob layout. These tests pin the two to
// the same behaviour — the same anti-drift mechanism as the Phase 166
// stamp round-trip: seed one fixture tree, diagnose it through BOTH the
// CLI walk and the real server substrate (over `LocalFileStorage`), and
// require identical classifications. Test-only references — the CLI
// itself stays pure-BCL.

let private tempRoot () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-doctor-" + Guid.NewGuid().ToString("N"))

    for sub in [ "teams"; "memberships"; "active-team" ] do
        Directory.CreateDirectory(Path.Combine(dir, "_platform", sub)) |> ignore

    dir

let private seedTeam (root: string) (teamId: string) =
    File.WriteAllText(
        Path.Combine(root, "_platform", "teams", teamId + ".json"),
        sprintf
            "{\"teamId\":\"%s\",\"name\":\"Team %s\",\"createdAt\":\"2026-01-01T00:00:00.0000000Z\",\"archived\":false}"
            teamId
            teamId
    )

let private seedMemberships (root: string) (userId: string) (teamIds: string list) =
    let rows =
        teamIds
        |> List.map (sprintf "{\"teamId\":\"%s\",\"role\":\"Member\",\"joinedAt\":\"2026-01-01T00:00:00.0000000Z\"}")
        |> String.concat ","

    File.WriteAllText(Path.Combine(root, "_platform", "memberships", userId + ".json"), "[" + rows + "]")

let private seedPointer (root: string) (userId: string) (teamId: string) =
    File.WriteAllText(Path.Combine(root, "_platform", "active-team", userId + ".txt"), teamId)

/// Seed the shared drift fixture: a clean user, an email-keyed blob, an
/// unresolvable (whitespace) blob key, an orphan row + a pointer at the
/// orphaned team, and a pointer-only user. `|`-shaped raw JWT subs are
/// not seedable here — Windows filenames cannot carry `|`, so the
/// local-file layout can only hold filename-legal unresolvable ids.
let private seedDriftFixture (root: string) =
    seedTeam root "team-a"
    seedTeam root "team-b"
    seedMemberships root "user-ok" [ "team-a" ]
    seedPointer root "user-ok" "team-a"
    seedMemberships root "ghost@example.com" [ "team-a" ]
    seedMemberships root "user id" [ "team-a" ]
    seedMemberships root "user-drift" [ "team-a"; "team-purged" ]
    seedPointer root "user-drift" "team-purged"
    seedPointer root "user-gone" "team-b"

/// Map a server-side diagnosis onto the CLI's stringly-kinded shape so
/// the two reports compare directly.
let private tokenise (d: MembershipDoctor.MembershipDiagnosis) =
    let kind =
        match d.Kind with
        | MembershipDoctor.EmailKeyedRow -> MembershipsDoctorCommand.KindEmailKeyedRow
        | MembershipDoctor.UnresolvableRow -> MembershipsDoctorCommand.KindUnresolvableRow
        | MembershipDoctor.OrphanTeamRow -> MembershipsDoctorCommand.KindOrphanTeamRow
        | MembershipDoctor.DanglingActiveTeam -> MembershipsDoctorCommand.KindDanglingActiveTeam

    let repair =
        match d.ProposedRepair with
        | MembershipDoctor.DeleteMembershipRow -> MembershipsDoctorCommand.RepairDeleteRow
        | MembershipDoctor.ClearActiveTeamPointer -> MembershipsDoctorCommand.RepairClearPointer
        | MembershipDoctor.ReAddUnderResolvedId -> MembershipsDoctorCommand.RepairReportOnly

    kind, d.UserId, d.TeamId, d.Evidence, repair

/// Diagnose `root` through the real server substrate over the real
/// local-file storage backend.
let private serverDiagnose (root: string) =
    let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage

    MembershipDoctor.diagnose (MembershipDoctor.MembershipDoctorStorage.reads storage)
    |> Async.RunSynchronously

let private cliTokens (root: string) =
    MembershipsDoctorCommand.diagnoseTree root
    |> List.map (fun f -> f.Kind, f.UserId, f.TeamId, f.Evidence, f.Repair)
    |> Set.ofList

let tests =
    testList "Phase 546 — memberships doctor" [

        test "CLI diagnose matches MembershipDoctor.diagnose over the same tree" {
            let root = tempRoot ()

            try
                seedDriftFixture root

                let server = serverDiagnose root |> List.map tokenise |> Set.ofList
                let cli = cliTokens root

                Expect.isNonEmpty (Set.toList cli) "the fixture tree carries drift"
                Expect.equal cli server "CLI and server substrate classify identically"
            finally
                Directory.Delete(root, true)
        }

        test "a clean tree yields an empty report from both sides" {
            let root = tempRoot ()

            try
                seedTeam root "team-a"
                seedMemberships root "user-ok" [ "team-a" ]
                seedPointer root "user-ok" "team-a"

                Expect.isEmpty (serverDiagnose root) "server substrate reports clean"
                Expect.isEmpty (MembershipsDoctorCommand.diagnoseTree root) "CLI reports clean"
            finally
                Directory.Delete(root, true)
        }

        test "CLI sanitiser mirror matches IdentitySanitiser.sanitiseScopeId" {
            let corpus = [
                Guid.NewGuid().ToString("N")
                "user-1"
                "a.b-c_d"
                "ghost@example.com"
                "user id"
                "auth0|abc123"
                "..%2f..%2fetc"
                "trailing."
                ".leading"
                "CON"
                "con.txt"
                "lpt3"
                ""
                String('x', 257)
                "tab\tchar"
                string (char 0)
            ]

            for id in corpus do
                Expect.equal
                    (MembershipsDoctorCommand.sanitiseScopeId id)
                    (IdentitySanitiser.sanitiseScopeId id)
                    (sprintf "sanitiser parity for %A" id)
        }

        test "CLI repair applies exactly the safe subset the substrate re-diagnoses as fixed" {
            let root = tempRoot ()

            try
                seedDriftFixture root

                let findings = MembershipsDoctorCommand.diagnoseTree root
                let repaired = MembershipsDoctorCommand.repairTree root findings

                Expect.all
                    repaired
                    (fun f ->
                        f.Repair = MembershipsDoctorCommand.RepairDeleteRow
                        || f.Repair = MembershipsDoctorCommand.RepairClearPointer)
                    "only safe repairs applied"

                // The substrate's post-repair view: every remaining finding
                // is report-only (email-keyed / unresolvable), and the safe
                // drift is gone.
                let remaining = serverDiagnose root

                Expect.all
                    remaining
                    (fun d -> d.ProposedRepair = MembershipDoctor.ReAddUnderResolvedId)
                    "only report-only findings survive repair"

                Expect.equal
                    (remaining |> List.map _.UserId |> List.distinct |> List.sort)
                    [ "ghost@example.com"; "user id" ]
                    "the bad-key blobs are untouched and still reported"

                let emailBlob =
                    File.ReadAllText(Path.Combine(root, "_platform", "memberships", "ghost@example.com.json"))

                Expect.stringContains emailBlob "team-a" "email-keyed rows survive repair"
            finally
                Directory.Delete(root, true)
        }

        test "command exits 0 on a clean tree, 1 on findings, 2 on usage error" {
            let root = tempRoot ()

            try
                seedTeam root "team-a"
                seedMemberships root "user-ok" [ "team-a" ]

                Expect.equal (MembershipsDoctorCommand.command.Run [ "--data-root"; root ]) ExitOk "clean tree exits 0"

                seedMemberships root "user-drift" [ "team-purged" ]

                Expect.equal
                    (MembershipsDoctorCommand.command.Run [ "--data-root"; root ])
                    ExitRuntimeError
                    "findings exit non-zero (CI-friendly)"

                Expect.equal
                    (MembershipsDoctorCommand.command.Run [ "--data-root"; root; "--repair" ])
                    ExitOk
                    "safe drift repaired, re-diagnose clean, exit 0"

                Expect.equal (MembershipsDoctorCommand.command.Run []) ExitUsage "missing --data-root exits 2"

                Expect.equal
                    (MembershipsDoctorCommand.command.Run [ "--data-root"; Path.Combine(root, "nope") ])
                    ExitRuntimeError
                    "missing data root is a runtime error"
            finally
                Directory.Delete(root, true)
        }
    ]