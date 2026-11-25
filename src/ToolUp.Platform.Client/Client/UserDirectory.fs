// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The reverse directory lookup (user id → display name and email) the
/// admin modules render ids through, as one sub-model. A parent holds
/// `Directory: UserDirectory.Model`, routes `DirectoryMsg of
/// UserDirectory.Msg` to `UserDirectory.update`, and asks for ids with
/// `UserDirectory.resolve ids model.Directory |> Cmd.map DirectoryMsg`
/// (or, from a ported parent, `resolveSteps` with `Step.map` and
/// `RemoteEffect.map`).
///
/// A failed lookup (no directory companion wired, a provider hiccup) leaves
/// the entries as they were, so the ids keep rendering raw: the directory
/// only ever makes a page friendlier, never blocks it.
module UserDirectory

open Fuaran.MVU
open System
open ToolUp.Elmish
open ToolUp.Platform

type Model = {
    /// Resolved entries keyed by user id. An id absent here renders raw.
    Entries: Map<string, UserSummary>
}

type Msg = Resolved of Result<UserSummary list, string>

let empty: Model = { Entries = Map.empty }

let private directoryApi: IUserDirectoryApi =
    Api.makeProxy<IUserDirectoryApi> (customOptions = UserSession.withRequestHeaders)

/// The ids worth asking for: non-blank, distinct, not yet resolved.
let unresolved (ids: string list) (model: Model) =
    ids
    |> List.filter (fun id -> not (String.IsNullOrWhiteSpace id) && not (Map.containsKey id model.Entries))
    |> List.distinct

/// The lookup of the ids `model` does not hold yet, as an effect value;
/// no step when there are none.
let internal resolveSteps (ids: string list) (model: Model) : Step<Msg, RemoteEffect<Msg>> list =
    match unresolved ids model with
    | [] -> []
    | pending -> [
        Do(
            RemoteEffect.call
                "IUserDirectoryApi.ResolveUsers"
                directoryApi.ResolveUsers
                pending
                (RemoteEffect.flatten >> Resolved)
        )
      ]

/// Resolve the ids `model` does not hold yet; `Cmd.none` when there are none.
let resolve (ids: string list) (model: Model) : Cmd<Msg> =
    resolveSteps ids model |> RemoteEffect.toCmd

/// The transitions; a reply asks for nothing further.
let internal step (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | Resolved(Ok summaries) ->
        {
            Entries = summaries |> List.fold (fun acc s -> Map.add s.UserId s acc) model.Entries
        },
        []
    | Resolved(Error _) -> model, []

/// The transitions, on today's loop.
let update (msg: Msg) (model: Model) : Model * Cmd<Msg> = step msg model |> RemoteEffect.run

let private nonBlank (value: string option) =
    value |> Option.filter (String.IsNullOrWhiteSpace >> not)

/// The display name when the directory has one, else the email, else the id.
let label (model: Model) (userId: string) : string =
    match Map.tryFind userId model.Entries with
    | Some s ->
        nonBlank s.DisplayName
        |> Option.orElse (nonBlank s.Email)
        |> Option.defaultValue userId
    | None -> userId

/// `label`, with the email beside it when the label is the display name.
let labels (model: Model) (userId: string) : string * string option =
    match Map.tryFind userId model.Entries with
    | Some s ->
        match nonBlank s.DisplayName, nonBlank s.Email with
        | Some name, email -> name, email
        | None, Some email -> email, None
        | None, None -> userId, None
    | None -> userId, None

/// The email when the directory has one, else the display name, else the id.
let email (model: Model) (userId: string) : string =
    match Map.tryFind userId model.Entries with
    | Some s ->
        nonBlank s.Email
        |> Option.orElse (nonBlank s.DisplayName)
        |> Option.defaultValue userId
    | None -> userId