// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

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
    UserId: string
    /// The active team of a `TeamMember` subject; `None` otherwise.
    ActiveTeamId: string option
    /// Whether the request resolved `PlatformRole.PlatformAdmin`.
    IsPlatformAdmin: bool
}

/// Reads a `RequestViewer` off a request's items.
module RequestViewer =
    /// The viewer the platform's scope resolution stamped on a request's
    /// items (`ToolUp.Subject`, `ToolUp.UserId`, `ToolUp.PlatformRole`), or
    /// `None` when it resolved no subject. A background context that copies
    /// those items forward reads back the same viewer.
    let ofItems (items: System.Collections.Generic.IDictionary<obj, obj>) : RequestViewer option =
        let item (key: string) =
            match items.TryGetValue(box key) with
            | true, value when not (isNull value) -> Some value
            | _ -> None

        match item "ToolUp.Subject" with
        | Some(:? Subject as subject) ->
            let fromSubject =
                match subject with
                | AnonymousSession sessionId -> sessionId
                | AuthenticatedUser userId
                | TeamMember(userId, _) -> userId
                | ClaimBearer claim -> claim.AttributedHandle |> Option.defaultValue claim.IssuedBy

            Some {
                UserId =
                    match item "ToolUp.UserId" with
                    | Some(:? string as userId) when not (System.String.IsNullOrWhiteSpace userId) -> userId
                    | _ -> fromSubject
                ActiveTeamId =
                    match subject with
                    | TeamMember(_, teamId) -> Some teamId
                    | _ -> None
                IsPlatformAdmin =
                    match item "ToolUp.PlatformRole" with
                    | Some(:? PlatformRole as role) -> role = PlatformRole.PlatformAdmin
                    | _ -> false
            }
        | _ -> None

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