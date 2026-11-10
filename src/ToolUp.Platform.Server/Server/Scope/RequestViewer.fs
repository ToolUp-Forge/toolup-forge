// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Scope

open ToolUp.Platform

// ─── Phase 896 — the ambient request viewer ──────────────────────
//
// Server tier only: who is asking, as the platform's scope resolution
// resolved it, carried on the async chain so the disclosure gate can decide
// a fact for the viewer without any door passing the viewer on.

/// The viewer of the current request, as the platform resolved it (Phase
/// 896): who is asking, their active team, and whether they are a platform
/// admin. Read from the items scope resolution stamps on the request, never
/// from anything a request handler passes on. Server tier only.
type RequestViewer = {
    /// The resolved user id.
    UserId: UserId
    /// The active team of a `TeamMember` subject; `None` otherwise.
    ActiveTeamId: TeamId option
    /// Whether the request resolved `PlatformRole.PlatformAdmin`.
    IsPlatformAdmin: bool
}

/// A projection of the request's principal.
module RequestViewer =
    /// The viewer an access context stands for, or `None` for a publication
    /// reader: it is no viewer of an app scope, so no team role, ownership or
    /// platform-admin standing is looked up for it, every requester-audience
    /// check is decided for the least-privileged viewer and every owner check
    /// refuses. The user id is `AccessContext.callerId`: a machine principal
    /// is named by its account, never "anonymous".
    let ofAccess (access: AccessContext) : RequestViewer option =
        match access.Subject with
        | PublicationReader _ -> None
        | subject ->
            Some {
                UserId = UserId(AccessContext.callerId access)
                ActiveTeamId =
                    match subject with
                    | TeamMember(_, teamId) -> Some(TeamId teamId)
                    | _ -> None
                IsPlatformAdmin = access.PlatformRole = Some PlatformRole.PlatformAdmin
            }

    /// The viewer of the principal stamped on a request's items, or `None`
    /// when none was stamped. A background context that copies the principal
    /// forward reads back the same viewer.
    let ofItems (items: System.Collections.Generic.IDictionary<obj, obj>) : RequestViewer option =
        AccessContext.ofItems items |> Option.bind ofAccess

/// The ambient request viewer (Phase 896) — the same `AsyncLocal` carrier
/// shape the platform's other request-scoped context takes (GP 7). The
/// platform's request plumbing establishes it: the disclosure companion's
/// middleware for an HTTP request, and a background worker that runs a turn
/// on a user's behalf from the items it carried forward. The disclosure gate
/// reads it; nothing that CALLS the gate supplies it. With nothing
/// established (a job, a webhook, a sweep) `current` is `None`, and the gate
/// evaluates the least-privileged viewer.
module RequestViewerContext =
    let private ambient = System.Threading.AsyncLocal<unit -> RequestViewer option>()

    /// The viewer of the current async chain, if one was established.
    let current () : RequestViewer option =
        let read = ambient.Value

        if isNull (box read) then None else read ()

    /// Establish how the current async chain reads its viewer; disposing the
    /// result restores what was established before. For the platform's
    /// request plumbing only.
    let establish (read: unit -> RequestViewer option) : System.IDisposable =
        let previous = ambient.Value
        ambient.Value <- read

        { new System.IDisposable with
            member _.Dispose() = ambient.Value <- previous
        }