// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text
open System.Text.Json
open ToolUp.Platform.BlobStorage

// ─── Phase 1107 — in-platform publication reader grants ───────────────
//
// The store an Owner or Admin of a publishing scope writes through the
// admin client (`IPublicationReaderApi`), read on two paths:
//
//   - sign-in (`OidcAuthProvider`): a principal the deployment's member
//     rule refuses, but who holds at least one grant, is admitted as a
//     publication reader;
//   - every page request (`ScopeResolutionMiddleware`): the principal's
//     grants ride into `AccessContext.PublicationGrants`, which only the
//     `Publication` page audience reads. Read per request, so a revocation
//     bites at the reader's next one.
//
// One blob per grant, `publication-readers/{user}/{scope}/{role}`, under
// `_platform`: a grant and a revocation are one upload and one delete, so
// two admins never lose each other's writes to a read-modify-write, and a
// principal's grants are one prefix listing.

/// The source of the reader roles a publishing scope's publications
/// declare — the only roles a grant may name. Composed by the public
/// rendering host, which owns the pages; absent, no role is declared and
/// every grant is refused.
type IPublicationReaderRoleSource =
    /// The distinct reader roles of every `Publication` audience whose
    /// publishing scope is `publishingScope`.
    abstract DeclaredRoles: publishingScope: string -> Async<string list>

/// In-platform publication reader grants (Phase 1107).
type IPublicationReaderGrantStore =
    /// Every grant `userId` holds, across publishing scopes. A store that
    /// cannot be read answers no grants (fail closed: nothing is admitted).
    abstract GrantsFor: userId: string -> Async<PublicationReaderGrant list>
    /// Every grant in `publishingScope`, ordered by role then user id.
    abstract ListScope: publishingScope: string -> Async<PublicationReaderEntry list>

    /// Record a grant. Idempotent. `grantedBy` is kept beside it.
    abstract Grant:
        publishingScope: string * entry: PublicationReaderEntry * grantedBy: string -> Async<Result<unit, string>>

    /// Remove a grant. Idempotent.
    abstract Revoke: publishingScope: string * entry: PublicationReaderEntry -> Async<Result<unit, string>>

module PublicationReaderGrants =
    /// `Ok trimmed` for a usable grant field, `Error` for a blank value or
    /// a wildcard: a grant names exactly one scope, one role, one principal.
    let validateToken (label: string) (value: string) : Result<string, string> =
        if String.IsNullOrWhiteSpace value then
            Error $"{label} is blank"
        else
            let trimmed = value.Trim()

            if trimmed.Contains '*' then
                Error $"{label} may not be a wildcard"
            else
                Ok trimmed

/// The SDK default `IPublicationReaderGrantStore` over `IBlobStorage`.
type BlobPublicationReaderGrantStore(storage: IBlobStorage, logger: ILogger option) =
    static let container = ScopeKey.PlatformContainer
    static let root = "publication-readers"
    static let seg (value: string) = Uri.EscapeDataString value
    static let userPrefix (userId: string) = $"{root}/{seg userId}/"

    static let blobName (userId: string) (scope: string) (role: string) =
        $"{userPrefix userId}{seg scope}/{seg role}"

    /// `(userId, scope, role)` of a grant blob name; `None` for any other shape.
    static let parse (name: string) =
        match name.Split('/') with
        | [| r; user; scope; role |] when r = root ->
            Some(Uri.UnescapeDataString user, Uri.UnescapeDataString scope, Uri.UnescapeDataString role)
        | _ -> None

    let listAll (prefix: string) = async {
        try
            let! names = storage.List(container, prefix)
            return names |> List.choose parse
        with ex ->
            logger
            |> Option.iter (fun l ->
                l.Error($"BlobPublicationReaderGrantStore: listing '{prefix}' failed; answering no grants.", Some ex))

            return []
    }

    let validated (scope: string) (entry: PublicationReaderEntry) =
        PublicationReaderGrants.validateToken "publishing scope" scope
        |> Result.bind (fun s ->
            PublicationReaderGrants.validateToken "reader role" entry.Role
            |> Result.bind (fun role ->
                PublicationReaderGrants.validateToken "user id" entry.UserId
                |> Result.map (fun user -> s, role, user)))

    interface IPublicationReaderGrantStore with
        member _.GrantsFor userId = async {
            if String.IsNullOrWhiteSpace userId then
                return []
            else
                let! grants = listAll (userPrefix userId)

                return
                    grants
                    |> List.filter (fun (user, _, _) -> user = userId)
                    |> List.map (fun (_, scope, role) -> { PublishingScope = scope; Role = role })
                    |> List.distinct
        }

        member _.ListScope publishingScope = async {
            let! grants = listAll $"{root}/"

            return
                grants
                |> List.filter (fun (_, scope, _) -> scope = publishingScope)
                |> List.map (fun (user, _, role) -> { UserId = user; Role = role })
                |> List.distinct
                |> List.sortBy (fun e -> e.Role, e.UserId)
        }

        member _.Grant(publishingScope, entry, grantedBy) = async {
            match validated publishingScope entry with
            | Error reason -> return Error reason
            | Ok(scope, role, user) ->
                let body =
                    JsonSerializer.Serialize(
                        {|
                            grantedBy = grantedBy
                            grantedAt = DateTimeOffset.UtcNow
                        |}
                    )

                match! storage.Upload(container, blobName user scope role, Encoding.UTF8.GetBytes body) with
                | Ok _ -> return Ok()
                | Error e -> return Error e
        }

        member _.Revoke(publishingScope, entry) = async {
            match validated publishingScope entry with
            | Error reason -> return Error reason
            | Ok(scope, role, user) -> return! storage.Delete(container, blobName user scope role)
        }