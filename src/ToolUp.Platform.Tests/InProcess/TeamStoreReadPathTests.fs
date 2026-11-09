// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.TeamStoreReadPathTests

open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Config.NotificationChannel
open ToolUp.Platform.Config.TeamManagement
open ToolUp.Platform.Infra
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── A storage fault is never "not a member", "no owners" or "no admins" ───
//
// The membership and admin READ paths used to map a failed `Download` to an
// empty row: one transient fault on the sole owner's membership blob made
// `IsLastOwner` count no owners, so `RemoveMember` removed the last Owner;
// it made `GetMemberRole` answer `None`, which the subject resolver reports as
// `NotTeamMember`; and it made the admin list read as `[]`, so
// `AssignPlatformAdmin` rewrote it as `[target]`. Each case below injects ONE
// failed read of a blob that is present, and asserts the operation refuses.

/// Fails the FIRST `Download` of each blob `shouldFail` names, then passes
/// through. `Exists` passes through: the blob really is present.
type private FailOnceBlobStorage(inner: IBlobStorage, shouldFail: string -> bool) =
    let failed = ConcurrentDictionary<string, bool>()

    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member _.Download(container, blobName) =
            if shouldFail blobName && failed.TryAdd(blobName, true) then
                async { return Error "simulated transient read failure" }
            else
                inner.Download(container, blobName)

        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

type private SilentAuditLog() =
    interface IAuditLog with
        member _.Record(_, _) = async { return () }
        member _.GetAuditTrail(_, _, _) = async { return [] }

let private teamStoreOver (storage: IBlobStorage) =
    TeamStore(storage, InMemoryNotificationChannel(None) :> INotificationChannel) :> ITeamStore

let private adminStoreOver (storage: IBlobStorage) =
    Jobs.PlatformAdminStore.BlobBackedPlatformAdminStore(storage, SilentAuditLog() :> IAuditLog) :> IPlatformAdminStore

let private team = TeamId "t-read-path"
let private alice = UserId "alice"
let private bob = UserId "bob"

/// A team whose sole Owner is alice, with bob as a Member.
let private seededTeam () =
    let backing = InMemoryBlobStorage() :> IBlobStorage
    let store = teamStoreOver backing

    async {
        let! created = store.CreateTeam(team, "Read path")
        Expect.isOk created "seed: create team"
        let! owner = store.AddMember(team, alice, Owner)
        Expect.isOk owner "seed: alice as Owner"
        let! mem = store.AddMember(team, bob, Member)
        Expect.isOk mem "seed: bob as Member"
    }
    |> Async.RunSynchronously

    backing

/// Run `work`; a raised exception is a refusal, and so is an `Error`.
let private outcome (work: Async<'T>) : Choice<'T, exn> =
    try
        Choice1Of2(Async.RunSynchronously work)
    with ex ->
        Choice2Of2 ex

let tests =
    testList "Phase 1061 — a storage fault on a read path is a refusal, never an empty row" [
        test "IsLastOwner: one failed read of the sole Owner's blob does not let RemoveMember remove them" {
            let backing = seededTeam ()

            let faulted =
                teamStoreOver (FailOnceBlobStorage(backing, fun name -> name = "memberships/alice.json"))

            match outcome (faulted.RemoveMember(team, alice)) with
            | Choice1Of2(Ok()) -> failtest "the last Owner was removed on a storage fault"
            | _ -> ()

            let role =
                (teamStoreOver backing).GetMemberRole(team, alice) |> Async.RunSynchronously

            Expect.equal role (Some Owner) "alice is still the team's Owner"
        }

        test "GetMemberRole: one failed read of a member's blob is not 'not a member'" {
            let backing = seededTeam ()

            let faulted =
                teamStoreOver (FailOnceBlobStorage(backing, fun name -> name = "memberships/bob.json"))

            match outcome (faulted.GetMemberRole(team, bob)) with
            | Choice1Of2 None -> failtest "a failed read answered 'not a member'"
            | Choice1Of2(Some role) -> failtest $"a failed read answered a role ({role}) it never read"
            | Choice2Of2 _ -> ()
        }

        test "GetTeamMembers: one failed member blob refuses the listing rather than dropping the row" {
            let backing = seededTeam ()

            let faulted =
                teamStoreOver (FailOnceBlobStorage(backing, fun name -> name = "memberships/bob.json"))

            match outcome (faulted.GetTeamMembers team) with
            | Choice1Of2 members ->
                failtest $"the listing answered {List.length members} row(s) with one member's blob unread"
            | Choice2Of2 _ -> ()
        }

        test "PlatformAdminStore: a failed read of the admin list does not let AssignPlatformAdmin rewrite it" {
            let backing = InMemoryBlobStorage() :> IBlobStorage
            let healthy = adminStoreOver backing

            Expect.isOk
                (healthy.AssignPlatformAdmin(UserId "_bootstrap", UserId "root")
                 |> Async.RunSynchronously)
                "seed: root is an admin"

            let faulted =
                adminStoreOver (FailOnceBlobStorage(backing, fun name -> name = "platform-admins.json"))

            match outcome (faulted.AssignPlatformAdmin(UserId "root", bob)) with
            | Choice1Of2(Ok()) -> failtest "the admin list was written over a read that failed"
            | _ -> ()

            let admins = healthy.ListPlatformAdmins() |> Async.RunSynchronously
            Expect.equal admins [ "root" ] "the admin list is unchanged"
        }

        test "PlatformAdminStore: IsPlatformAdmin on an unreadable list refuses rather than answering false" {
            let backing = InMemoryBlobStorage() :> IBlobStorage

            Expect.isOk
                ((adminStoreOver backing).AssignPlatformAdmin(UserId "_bootstrap", UserId "root")
                 |> Async.RunSynchronously)
                "seed: root is an admin"

            let faulted =
                adminStoreOver (FailOnceBlobStorage(backing, fun name -> name = "platform-admins.json"))

            match outcome (faulted.IsPlatformAdmin(UserId "root")) with
            | Choice1Of2 answer -> failtest $"an unreadable admin list answered {answer}"
            | Choice2Of2 _ -> ()
        }
    ]