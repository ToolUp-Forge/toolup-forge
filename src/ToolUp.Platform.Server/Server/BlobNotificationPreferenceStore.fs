// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.BlobNotificationPreferenceStore

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

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
// Watermarks (`digest/{user}.json`) are written by the digest job
// alone, so a plain overwrite is safe.

let private platformContainer = "_platform"
let private root = "notification-preferences"

let private jsonOptions = FableConverters.create ()

let private serialise (value: 'T) : byte[] =
    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, jsonOptions))

let private tryDeserialise<'T> (bytes: byte[]) : 'T option =
    try
        Some(JsonSerializer.Deserialize<'T>(Encoding.UTF8.GetString bytes, jsonOptions))
    with _ ->
        None

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
    $"{pendingPrefix scopeId item.UserId}{ticks}-{item.Id:N}.json"

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

    let listNames (prefix: string) = async {
        let! names = storage.List(platformContainer, prefix)
        return names |> List.sort
    }

    interface INotificationPreferenceStore with
        member _.GetPreferences(scopeId, userId) = async {
            let name = prefsBlob scopeId userId
            let! exists = storage.Exists(platformContainer, name)

            if not exists then
                return Ok UserNotificationPreferences.empty
            else
                match! storage.Download(platformContainer, name) with
                | Error e -> return Error $"Could not read notification preferences for {userId}: {e}"
                | Ok bytes ->
                    match tryDeserialise<UserNotificationPreferences> bytes with
                    | Some prefs -> return Ok prefs
                    | None ->
                        // A corrupt record reads as "nothing chosen" rather
                        // than blocking every send to the user; the warning
                        // is the operator's cue to inspect the blob.
                        warn $"[NotificationPreferences] unreadable preference record at {name}; treating as empty"
                        return Ok UserNotificationPreferences.empty
        }

        member _.SavePreferences(scopeId, userId, preferences) = async {
            match! storage.Upload(platformContainer, prefsBlob scopeId userId, serialise preferences) with
            | Ok _ -> return Ok()
            | Error e -> return Error $"Could not save notification preferences for {userId}: {e}"
        }

        member _.Enqueue(scopeId, item) = async {
            match! storage.Upload(platformContainer, pendingBlob scopeId item, serialise item) with
            | Ok _ -> return Ok()
            | Error e -> return Error $"Could not hold notification {item.Id} for {item.UserId}: {e}"
        }

        member _.ListPending(scopeId, userId) = async {
            let! names = listNames (pendingPrefix scopeId userId)
            let items = ResizeArray<PendingNotification>()

            for name in names do
                match! storage.Download(platformContainer, name) with
                | Ok bytes ->
                    match tryDeserialise<PendingNotification> bytes with
                    | Some item -> items.Add item
                    | None -> warn $"[NotificationPreferences] unreadable pending item at {name}; skipped"
                | Error e -> warn $"[NotificationPreferences] could not read pending item at {name}: {e}"

            return items |> Seq.sortBy (fun i -> i.QueuedAt, i.Id) |> List.ofSeq
        }

        member _.Remove(scopeId, userId, ids) = async {
            if List.isEmpty ids then
                return Ok()
            else
                let wanted = ids |> List.map (fun id -> id.ToString "N") |> Set.ofList
                let! names = listNames (pendingPrefix scopeId userId)

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
            let name = digestBlob scopeId userId
            let! exists = storage.Exists(platformContainer, name)

            if not exists then
                return Map.empty
            else
                match! storage.Download(platformContainer, name) with
                | Ok bytes -> return tryDeserialise<Map<string, DateTime>> bytes |> Option.defaultValue Map.empty
                | Error e ->
                    warn $"[NotificationPreferences] could not read digest watermarks at {name}: {e}"
                    return Map.empty
        }

        member this.SetDigestWatermark(scopeId, userId, frequency, sentAt) = async {
            let! current = (this :> INotificationPreferenceStore).GetDigestWatermarks(scopeId, userId)

            let updated =
                current
                |> Map.add (DigestFrequency.toWireString frequency) (sentAt.ToUniversalTime())

            match! storage.Upload(platformContainer, digestBlob scopeId userId, serialise updated) with
            | Ok _ -> return Ok()
            | Error e -> return Error $"Could not record digest watermark for {userId}: {e}"
        }

/// Construct the default store over the composed `IBlobStorage`.
let create (storage: IBlobStorage) (logger: ILogger option) : INotificationPreferenceStore =
    BlobNotificationPreferenceStore(storage, logger) :> INotificationPreferenceStore