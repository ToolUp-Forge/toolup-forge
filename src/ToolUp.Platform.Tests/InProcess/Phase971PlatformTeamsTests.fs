module ToolUp.Platform.Tests.InProcess.Phase971PlatformTeamsTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.MembershipDoctor
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.DataObjectStoreTests

// ─── Phase 971 — Teams: a refused delete that is the operation ─────────
//
// `IBlobStorage.Delete` answers `Ok` on a missing blob, so an `Error` is a
// refusal. Where the delete IS the operation's effect — the doctor clearing
// a dangling pointer, a member leaving the team their pointer names, an
// archive bumping members off the team, a user purge — the refusal reaches
// the caller, and the order leaves a re-run something to find.

/// A refusal that can be lifted, so a test can show the re-run finishing.
let private switchable (refusedName: string) =
    let refusing = ref true

    let storage =
        DeleteRefusingBlobStorage(InMemoryBlobStorage(), fun n -> refusing.Value && n = refusedName)

    storage :> IBlobStorage, refusing

let private exists (storage: IBlobStorage) (blobName: string) =
    match storage.Download(platformContainer, blobName) |> Async.RunSynchronously with
    | Ok _ -> true
    | Error _ -> false

let private teamStoreOver (storage: IBlobStorage) =
    TeamStore(storage, InMemoryNotificationChannel(None) :> INotificationChannel)

/// A team with `owner` as Owner and `bob` as a Member whose active team it is.
let private seedPointedMember (ts: TeamStore) = async {
    let teamId = Guid.NewGuid().ToString("N")
    let! created = ts.CreateTeam(teamId, "Phase 971")
    Expect.isOk created "team created"
    let! owner = ts.AddMember(teamId, "owner", Owner)
    Expect.isOk owner "owner added"
    let! bob = ts.AddMember(teamId, "bob", Member)
    Expect.isOk bob "bob added"
    let! pointed = ts.SetActiveTeam("bob", teamId)
    Expect.isOk pointed "bob's active team set"
    return teamId
}

let private raises (work: Async<'a>) = async {
    match! Async.Catch work with
    | Choice1Of2 value -> return failtestf "expected the operation to raise, it answered %A" value
    | Choice2Of2 _ -> return ()
}

let tests =
    testList "Phase 971 - PlatformTeams" [
        testCaseAsync "MembershipDoctor repair raises when a dangling pointer's delete is refused"
        <| async {
            let storage, _ = switchable (activeTeamBlobName "user-1")

            let teamJson =
                "{\"teamId\":\"team-b\",\"name\":\"Team b\",\"createdAt\":\"2026-01-01T00:00:00.0000000Z\",\"archived\":false}"

            let! _ = storage.Upload(platformContainer, teamBlobName "team-b", Text.Encoding.UTF8.GetBytes teamJson)
            // A pointer at a team the user holds no row for: dangling.
            let! _ =
                storage.Upload(platformContainer, activeTeamBlobName "user-1", Text.Encoding.UTF8.GetBytes "team-b")

            do!
                raises (
                    repair
                        (MembershipDoctorStorage.reads storage)
                        (MembershipDoctorStorage.writes storage None None "doctor-actor")
                )

            Expect.isTrue (exists storage (activeTeamBlobName "user-1")) "the pointer is still there for a re-run"
        }

        testCaseAsync "RemoveMember answers Error on a refused pointer delete and leaves the row for the retry"
        <| async {
            let storage, refusing = switchable (activeTeamBlobName "bob")
            let ts = teamStoreOver storage
            let! teamId = seedPointedMember ts

            let! removed = ts.RemoveMember(teamId, "bob")
            Expect.isError removed "the refusal reaches the caller"

            let! teams = ts.GetTeamsForUser "bob"
            Expect.contains (teams |> List.map _.TeamId) teamId "bob is still a member, so a retry re-finds him"
            let! active = ts.GetActiveTeam "bob"
            Expect.equal active (Some teamId) "the pointer is untouched"

            refusing.Value <- false
            let! retried = ts.RemoveMember(teamId, "bob")
            Expect.isOk retried "the plain retry finishes"
            let! after = ts.GetTeamsForUser "bob"
            Expect.isEmpty after "the row is gone"
            let! pointer = ts.GetActiveTeam "bob"
            Expect.isNone pointer "the pointer is gone"
        }

        testCaseAsync "SetArchived answers Error when a member's pointer delete is refused, and a re-run finishes"
        <| async {
            let storage, refusing = switchable (activeTeamBlobName "bob")
            let ts = teamStoreOver storage
            let! teamId = seedPointedMember ts

            let! archived = ts.SetArchived(teamId, true)
            Expect.isError archived "the refusal reaches the caller"
            Expect.isTrue (exists storage (activeTeamBlobName "bob")) "the pointer is still there"

            refusing.Value <- false
            let! retried = ts.SetArchived(teamId, true)
            Expect.isOk retried "the re-run finishes"
            let! pointer = ts.GetActiveTeam "bob"
            Expect.isNone pointer "the pointer is gone"
        }

        testCaseAsync "PurgeTeam answers Error on a refused pointer delete, keeps the team and the member's row"
        <| async {
            let storage, refusing = switchable (activeTeamBlobName "bob")
            let ts = teamStoreOver storage
            let! teamId = seedPointedMember ts

            let! purged = ts.PurgeTeam teamId
            Expect.isError purged "the refusal reaches the caller"
            let! team = ts.GetTeam teamId
            Expect.isSome team "the team record stays, so the purge can be re-run"
            let! members = ts.GetTeamMembers teamId
            Expect.contains (members |> List.map _.UserId) "bob" "bob's row stays, so the re-run revisits his pointer"

            refusing.Value <- false
            let! retried = ts.PurgeTeam teamId
            Expect.isOk retried "the re-run finishes"
            let! pointer = ts.GetActiveTeam "bob"
            Expect.isNone pointer "the pointer is gone"
            let! gone = ts.GetTeam teamId
            Expect.isNone gone "the team is purged"
        }

        testCaseAsync "PurgeUser answers Error on a refused pointer delete; pointer and membership record stay"
        <| async {
            let storage, refusing = switchable (activeTeamBlobName "bob")
            let ts = teamStoreOver storage
            let! teamId = seedPointedMember ts

            let! purged = ts.PurgeUser "bob"
            Expect.isError purged "the refusal reaches the caller"
            Expect.isTrue (exists storage (activeTeamBlobName "bob")) "the pointer is still readable"
            Expect.isTrue (exists storage (membershipBlobName "bob")) "the membership record is what a re-run finds"

            refusing.Value <- false
            let! retried = ts.PurgeUser "bob"
            Expect.isOk retried "the re-run finishes"
            Expect.isFalse (exists storage (activeTeamBlobName "bob")) "pointer gone"
            Expect.isFalse (exists storage (membershipBlobName "bob")) "membership gone"
            let! members = ts.GetTeamMembers teamId
            Expect.equal (members |> List.map _.UserId) [ "owner" ] "only the owner remains"
        }

        testCaseAsync "PurgeUser answers Error on a refused membership delete; the record stays readable"
        <| async {
            let storage, refusing = switchable (membershipBlobName "bob")
            let ts = teamStoreOver storage
            let! _ = seedPointedMember ts

            let! purged = ts.PurgeUser "bob"
            Expect.isError purged "the refusal reaches the caller"
            Expect.isTrue (exists storage (membershipBlobName "bob")) "the membership record is still readable"

            refusing.Value <- false
            let! retried = ts.PurgeUser "bob"
            Expect.isOk retried "the re-run finishes"
            Expect.isFalse (exists storage (membershipBlobName "bob")) "membership gone"
            let! again = ts.PurgeUser "bob"
            Expect.isOk again "a re-purge of a purged user stays an Ok no-op"
        }
    ]