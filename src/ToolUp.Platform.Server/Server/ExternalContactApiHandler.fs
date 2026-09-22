// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ExternalContactApiHandler

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

// ─── IExternalContactApi handler (Phase 6f.A) ────────────────────────
//
// The surface behind `ExternalContactManagerUI`. Resolves
// `IExternalContactStore`, `ITeamStore` and `AccessContext` from
// per-request DI (the `ServiceAccountApiHandler` idiom) and acts only
// within the caller's server-resolved scope — no method takes a scope
// parameter, so a caller cannot name one (GP 4).
//
// **Three gates, in this order.**
//
//   1. **Store composed.** `NoExternalContactStore` means the route is
//      not mounted at all, so this arm only fires in a deployment that
//      mounted the route and then failed to register the store —
//      named rather than 500'd.
//   2. **Server-resolved scope.** A persistent scope is required: an
//      anonymous session's address book would vanish with the session.
//   3. **Read vs write.** Reads need membership only — a team member
//      composing a message needs the contact list. Writes need
//      `TeamRoles.canWriteTeamConfig`, because "who may assert that
//      this person consented" is exactly the authority that must not
//      be ambient. In a personal (non-team) scope the caller owns the
//      scope outright and there is no role to check.
//
// **`RecordInbound` is the one member a machine caller may reach.** It
// is driven by a channel's inbound webhook, not by a person, and it
// writes one timestamp that grants no new authority. Every other member
// refuses a `ClaimBearer`: a machine credential that can record a
// consent can manufacture the lawful basis for its own sends.
//
// **The audit rows are emitted by the STORE, not here.** The store is
// the chokepoint every caller passes through — a job, an intake
// pipeline, an inbound webhook — and an audit emitted per caller is an
// audit some caller will forget.

/// The owner an authenticated caller's writes default to: the team when
/// the caller is acting in one, otherwise themselves.
let private ownerFor (accessContext: AccessContext) : ContactOwner =
    match accessContext.Subject with
    | TeamMember(_, teamId) -> ContactOwner.Team teamId
    | _ -> ContactOwner.User accessContext.UserId

/// Parse a channel string from the wire into the shipped routing key.
let private parseChannel (wire: string) : Result<NotificationKind.SinkKind, ExternalContactError> =
    match NotificationKind.SinkKind.tryParse wire with
    | Some kind -> Ok kind
    | None -> Error(ExternalContactError.UnknownChannel wire)

/// Build the per-request `IExternalContactApi` over the composed store.
let externalContactApi (ctx: HttpContext) : IExternalContactApi =
    let store =
        match ctx.RequestServices.GetService(typeof<IExternalContactStore>) with
        | :? IExternalContactStore as s -> Some s
        | _ -> None

    let accessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac
        | _ ->
            // Fallback for tests that bypass ScopeResolutionMiddleware —
            // the same shape ServiceAccountApiHandler uses. An anonymous
            // floor, so the gates below refuse rather than admit.
            AccessContext.unrestricted (AnonymousSession "anonymous")

    let describe = ExternalContactError.describe

    /// Gates 1 + 2, shared by reads and writes.
    let scoped (f: IExternalContactStore -> string -> Async<Result<'T, string>>) : Async<Result<'T, string>> = async {
        match store with
        | None -> return Error "External contacts are not enabled in this deployment."
        | Some s ->
            match accessContext.Subject with
            | AnonymousSession _ -> return Error "Sign in to use the address book."
            | _ ->
                match AccessContext.configScope accessContext with
                | None -> return Error "The address book needs a persistent scope. Sign in or join a team."
                | Some scope -> return! f s scope.ScopeId
    }

    /// Reads: membership is enough, but a machine credential is not a
    /// member. A service account with no consent-write authority still
    /// must not be able to enumerate a team's clients.
    let readable (f: IExternalContactStore -> string -> Async<Result<'T, string>>) : Async<Result<'T, string>> =
        scoped (fun s scopeId ->
            match accessContext.Subject with
            | ClaimBearer _ -> async {
                return Error "The address book cannot be read with a token credential. Sign in as a team member."
              }
            | _ -> f s scopeId)

    /// Writes: gate 3. Owner/Admin in a team scope; the scope owner in a
    /// personal one; never a machine credential.
    let writable (f: IExternalContactStore -> string -> Async<Result<'T, string>>) : Async<Result<'T, string>> =
        scoped (fun s scopeId -> async {
            match accessContext.Subject with
            | ClaimBearer _ ->
                return Error "Contacts cannot be managed with a token credential. Sign in as a team owner or admin."
            | TeamMember(userId, teamId) ->
                match ctx.RequestServices.GetService(typeof<ITeamStore>) with
                | :? ITeamStore as teams ->
                    let! role = teams.GetMemberRole(teamId, userId)

                    match role with
                    | Some r when TeamRoles.canWriteTeamConfig r -> return! f s scopeId
                    | Some r ->
                        return
                            Error
                                $"Only team owners and admins can manage contacts. Your role: {TeamRoles.displayName r}."
                    | None -> return Error "You are not a member of this team."
                | _ -> return Error "Team management is not available in this deployment."
            | _ ->
                // Personal / non-team scope: the caller owns the scope
                // outright, so there is no role to check.
                return! f s scopeId
        })

    let toResult (r: Result<'a, ExternalContactError>) : Result<'a, string> =
        match r with
        | Ok v -> Ok v
        | Error e -> Error(describe e)

    let actor = accessContext.UserId

    {
        ListContacts =
            fun () ->
                readable (fun s scopeId -> async {
                    let! contacts = s.List scopeId
                    return Ok contacts
                })

        GetContact =
            fun contactId ->
                readable (fun s scopeId -> async {
                    let! contact = s.Get(scopeId, contactId)
                    return toResult contact
                })

        CreateContact =
            fun request ->
                writable (fun s scopeId -> async {
                    // The owner is derived from the caller's own
                    // subject, never taken from the request: a request
                    // that could name an owner could file a contact
                    // into someone else's address book.
                    let! created = s.Create(scopeId, actor, ownerFor accessContext, request)
                    return toResult created
                })

        UpdateContact =
            fun request ->
                writable (fun s scopeId -> async {
                    let! updated = s.Update(scopeId, actor, request)
                    return toResult updated
                })

        DeleteContact =
            fun contactId ->
                writable (fun s scopeId -> async {
                    let! deleted = s.Delete(scopeId, actor, contactId)
                    return deleted |> Result.map ignore |> toResult
                })

        RecordOptIn =
            fun request ->
                writable (fun s scopeId -> async {
                    match parseChannel request.Channel with
                    | Error e -> return Error(describe e)
                    | Ok channel ->
                        if String.IsNullOrWhiteSpace request.Source then
                            // The Article 7(1) evidence is the whole
                            // point of the record. A consent whose
                            // provenance nobody stated cannot be
                            // demonstrated later, so it is refused at
                            // the boundary rather than stored hollow.
                            return
                                Error
                                    "A consent needs a source — how it was obtained (a form submission id, a signed form, or a note that an admin entered it by hand)."
                        else
                            let record: OptInRecord = {
                                GrantedAt = DateTime.UtcNow
                                Source = request.Source.Trim()
                                ExpiresAt = request.ExpiresAt
                            }

                            let! updated = s.RecordOptIn(scopeId, actor, request.ContactId, channel, record)
                            return toResult updated
                })

        WithdrawOptIn =
            fun request ->
                writable (fun s scopeId -> async {
                    match parseChannel request.Channel with
                    | Error e -> return Error(describe e)
                    | Ok channel ->
                        let reason =
                            if String.IsNullOrWhiteSpace request.Reason then
                                "unspecified"
                            else
                                request.Reason.Trim()

                        let! updated = s.WithdrawOptIn(scopeId, actor, request.ContactId, channel, reason)
                        return toResult updated
                })

        RecordInbound =
            fun contactId ->
                // Deliberately `scoped` and not `writable`: an inbound
                // webhook is a machine caller by construction, and the
                // write it makes is one timestamp that grants no
                // authority. Every other member refuses a machine.
                scoped (fun s scopeId -> async {
                    let! updated = s.RecordInbound(scopeId, contactId, DateTime.UtcNow)
                    return toResult updated
                })
    }