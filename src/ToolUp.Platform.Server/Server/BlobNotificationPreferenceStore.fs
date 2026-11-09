// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Jobs.BlobNotificationPreferenceStore

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra

// ─── Phase 441 — blob-backed INotificationPreferenceStore ────────────
//
// The SDK default over `IBlobStorage`, in the reserved `_platform`
// container (the `FeatureFlagStore` / `NotificationAddressBook` layout):
//
//   notification-preferences/{scope}/prefs/{user}.json
//   notification-preferences/{scope}/pending/{user}/{ticks}-{id}.json
//   notification-preferences/{scope}/digest/{user}.json
//
// Every key starts with the scope, so cross-scope reads are impossible
// by construction (GP 4). Scope and user segments are
// `Uri.EscapeDataString`-encoded so an id carrying `/` cannot escape
// its directory; the common `team-…` / `user-…` ids are unchanged.
//
// **One blob per pending item is the concurrency design, not a
// convenience.** The filter writes items while the digest job drains
// them; with a single per-user list blob both sides would read-modify-
// write the same bytes and one of them would lose. Separate blobs make
// an enqueue a create and a drain a set of deletes, which never
// conflict. The `ticks-` prefix keeps `List` results in queue order on
// any backend that sorts names, and the sort here makes it true on the
// ones that do not.
//
// Every document reads and writes through `BlobDocumentStore`. A record
// that cannot be READ refuses (preferences) or is logged and skipped (a
// listing). One that does not DECODE is set aside by the store; a corrupt
// preference record then reads as "nothing chosen" by decision (a corrupt
// record must not block every send to the user), and the next save — or,
// for watermarks, the digest job's next write — starts it again.

let private platformContainer = ScopeKey.PlatformContainer

let private root = "notification-preferences"

let private seg (value: string) : string = Uri.EscapeDataString value
let private unseg (value: string) : string = Uri.UnescapeDataString value

let private prefsBlob (scopeId: string) (userId: string) =
    $"{root}/{seg scopeId}/prefs/{seg userId}.json"

let private digestBlob (scopeId: string) (userId: string) =
    $"{root}/{seg scopeId}/digest/{seg userId}.json"

let private pendingPrefix (scopeId: string) (userId: string) =
    $"{root}/{seg scopeId}/pending/{seg userId}/"

let private scopePendingPrefix (scopeId: string) = $"{root}/{seg scopeId}/pending/"

let private pendingBlob (scopeId: string) (item: PendingNotification) =
    let ticks = item.QueuedAt.ToUniversalTime().Ticks.ToString("D19")
    $"{pendingPrefix scopeId item.UserId.Value}{ticks}-{item.Id:N}.json"

/// Split a `root/{scope}/pending/{user}/{file}` name into its decoded
/// scope and user; `None` for any other shape.
let private parsePendingName (name: string) : (string * string) option =
    match name.Split('/') with
    | [| r; scope; "pending"; user; _file |] when r = root -> Some(unseg scope, unseg user)
    | _ -> None

/// The SDK default `INotificationPreferenceStore` over `IBlobStorage`.
type BlobNotificationPreferenceStore(storage: IBlobStorage, logger: ILogger option) =
    let warn (message: string) =
        logger |> Option.iter (fun l -> l.Warn message)

    let storeLogger =
        logger
        |> Option.defaultWith (fun () -> JsonConsoleLogger.JsonConsoleLogger() :> ILogger)

    let preferences =
        BlobDocumentStore<UserNotificationPreferences>(
            storage,
            BlobCodec.json<UserNotificationPreferences>,
            storeLogger
        )

    let pending =
        BlobDocumentStore<PendingNotification>(storage, BlobCodec.json<PendingNotification>, storeLogger)

    let watermarks =
        BlobDocumentStore<Map<string, DateTime>>(storage, BlobCodec.json<Map<string, DateTime>>, storeLogger)

    let listNames (prefix: string) = async {
        let! names = storage.List(platformContainer, prefix)
        return names |> List.sort
    }

    interface INotificationPreferenceStore with
        member _.GetPreferences(scopeId, userId) = async {
            let path = DocPath.ofString (prefsBlob scopeId userId.Value)

            match! preferences.Read(ScopeKey.platform, path) with
            | Ok(Some prefs) -> return Ok prefs
            | Ok None -> return Ok UserNotificationPreferences.empty
            | Error(BlobMapStoreError.Quarantined _ as e) ->
                // A corrupt record reads as "nothing chosen" rather than
                // blocking every send to the user; the store set its bytes
                // aside, and the warning is the operator's cue.
                warn $"[NotificationPreferences] {BlobMapStoreError.describe e}; treating as empty"
                return Ok UserNotificationPreferences.empty
            | Error e ->
                return Error $"Could not read notification preferences for {userId}: {BlobMapStoreError.describe e}"
        }

        member _.SavePreferences(scopeId, userId, value) = async {
            let path = DocPath.ofString (prefsBlob scopeId userId.Value)

            match! preferences.Replace(ScopeKey.platform, path, value) with
            | Ok() -> return Ok()
            | Error e ->
                return Error $"Could not save notification preferences for {userId}: {BlobMapStoreError.describe e}"
        }

        member _.Enqueue(scopeId, item) = async {
            match!
                storage.Upload(
                    platformContainer,
                    pendingBlob scopeId item,
                    BlobCodec.json<PendingNotification>.Encode item
                )
            with
            | Ok _ -> return Ok()
            | Error e -> return Error $"Could not hold notification {item.Id} for {item.UserId}: {e}"
        }

        member _.ListPending(scopeId, userId) = async {
            let! names = listNames (pendingPrefix scopeId userId.Value)

            let! items =
                BlobDocuments.readAvailable pending storeLogger "NotificationPreferences" ScopeKey.platform names

            return items |> List.sortBy (fun i -> i.QueuedAt, i.Id)
        }

        member _.Remove(scopeId, userId, ids) = async {
            if List.isEmpty ids then
                return Ok()
            else
                let wanted = ids |> List.map (fun id -> id.ToString "N") |> Set.ofList
                let! names = listNames (pendingPrefix scopeId userId.Value)

                let targets =
                    names
                    |> List.filter (fun name ->
                        let file = name.Substring(name.LastIndexOf '/' + 1)
                        // `{ticks}-{id}.json` — the id is the 32 chars before `.json`.
                        file.Length > 37 && wanted.Contains(file.Substring(file.Length - 37, 32)))

                // A ref cell rather than `let mutable`: the async body is a
                // closure, and a captured mutable is a compile error.
                let failure = ref None

                for name in targets do
                    match! storage.Delete(platformContainer, name) with
                    | Ok() -> ()
                    | Error e -> failure.Value <- Some $"Could not remove held notification {name}: {e}"

                return
                    match failure.Value with
                    | None -> Ok()
                    | Some e -> Error e
        }

        member _.ListPendingUsers scopeId = async {
            let! names = listNames (scopePendingPrefix scopeId)

            return
                names
                |> List.choose parsePendingName
                |> List.filter (fun (scope, _) -> scope = scopeId)
                |> List.map snd
                |> List.distinct
        }

        member _.ListScopesWithPending() = async {
            let! names = listNames $"{root}/"
            return names |> List.choose parsePendingName |> List.map fst |> List.distinct
        }

        member _.GetDigestWatermarks(scopeId, userId) = async {
            match! watermarks.Read(ScopeKey.platform, DocPath.ofString (digestBlob scopeId userId.Value)) with
            | Ok(Some marks) -> return marks
            | Ok None -> return Map.empty
            | Error e ->
                warn $"[NotificationPreferences] digest watermarks unavailable: {BlobMapStoreError.describe e}"
                return Map.empty
        }

        member _.SetDigestWatermark(scopeId, userId, frequency, sentAt) = async {
            let key = DigestFrequency.toWireString frequency
            let path = DocPath.ofString (digestBlob scopeId userId.Value)

            // One guarded update: a read failure writes nothing (it used to
            // read as "no watermarks" and erase the other frequencies'),
            // and an undecodable map is set aside and started again — the
            // cost is one digest window, never a send to an opted-out user.
            let! written =
                watermarks.UpdateOrRestart(
                    ScopeKey.platform,
                    path,
                    fun current ->
                        let marks =
                            current
                            |> Option.defaultValue Map.empty
                            |> Map.add key (sentAt.ToUniversalTime())

                        BlobUpdate.Write(marks, ())
                )

            match written with
            | Ok() -> return Ok()
            | Error e -> return Error $"Could not record digest watermark for {userId}: {BlobMapStoreError.describe e}"
        }

/// Construct the default store over the composed `IBlobStorage`.
let create (storage: IBlobStorage) (logger: ILogger option) : INotificationPreferenceStore =
    BlobNotificationPreferenceStore(storage, logger) :> INotificationPreferenceStore