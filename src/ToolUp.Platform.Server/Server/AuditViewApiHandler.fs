// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AuditViewApiHandler

open System
open System.Globalization
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

// ─── Phase 529 — audit-trail viewer handler ──────────────────────────
//
// Read-only, Owner/Admin, caller-scope-only surface over the rows
// `IAuditLog` writes. RBAC shape is `UsageQueryApiHandler`'s, verbatim
// and for the same reason: Anonymous refused outright, team callers
// gated on `TeamRoles.canWriteTeamConfig`, single-scope callers own the
// scope they read.
//
// ─── Why this reads `IEventStore` and not `IAuditLog` ────────────────
//
// `IAuditLog.GetAuditTrail` is the natural seam and it is LOSSY for a
// viewer: it decodes each persisted `ModuleEvent` into its `AuditEvent`
// case and returns only that, dropping `Id` and `OccurredAt`. The
// table's time column and the pagination cursor both need those, and
// most payload cases carry no timestamp of their own — so a viewer
// built on `GetAuditTrail` could show WHAT happened but not WHEN, and
// could not page stably.
//
// So this handler makes the identical read one layer down —
// `IEventStore.ReadBySource(scopeId, AuditSourceModule.value)`, which
// is literally the first line of `EventStoreAuditLog.GetAuditTrail` —
// and keeps the envelope. It reads the same rows through the same
// store; what it does NOT do is re-derive the decode, because the
// viewer wants the payload as stored (see `AuditEventView.Payload`).
//
// The one property that has to be restored by hand is the pairing with
// `ServerConfig.AuditLog`: reading the store directly would otherwise
// show residue on a deployment that has since turned the audit log off.
// `auditViewApi` therefore takes the mode captured at compose time and
// short-circuits `NoAuditLog` to an empty result — the same answer
// `NoOpAuditLog` would have given.
//
// ─── Scope (GP 4) ─────────────────────────────────────────────────────
//
// The scope is `AccessContext.configScope`'s id, falling back to
// `_platform` for callers with no persistent scope — the same
// resolution `/dev/auth-denials` uses, and it is where scope-less audit
// rows are written. No method takes a scope from the wire, and
// `IEventStore` reads are structurally single-scope, so a cross-team
// read is not merely refused but unrepresentable.

/// Resolve the caller's `AccessContext`. Falls back to an anonymous
/// context (which the role gate then refuses) when
/// `ScopeResolutionMiddleware` has not run — the same fallback
/// `ModuleVisibilityApiHandler` and `UsageQueryApiHandler` take, and it
/// fails CLOSED here because anonymous is the denied case.
let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        AccessContext.unrestricted (AnonymousSession userId)

/// Scope to read the trail from: the caller's own container id, else
/// `_platform` for a caller with no persistent scope. Mirrors
/// `AuthDenialsDiagnosticsHandler.callerScopeId`.
let private callerScopeId (accessContext: AccessContext) : string =
    match AccessContext.configScope accessContext with
    | Some scope -> scope.ScopeId
    | None -> "_platform"

// ─── Cursor ───────────────────────────────────────────────────────────
//
// Rows are ordered `(OccurredAt desc, Id desc)` — a total order, so a
// page boundary is a single row rather than a set of ties. The cursor
// names that row; the next page is everything strictly after it. Encoded
// so a client cannot construct one that means something else, and
// decoded strictly: a token this server cannot read is REFUSED rather
// than treated as "start from the top", because a silently-restarted
// page looks exactly like a complete result.

let private encodeCursor (occurredAt: DateTime) (id: Guid) : string =
    let raw = sprintf "%d:%s" (occurredAt.Ticks) (id.ToString "N")
    Convert.ToBase64String(Encoding.UTF8.GetBytes raw)

let private decodeCursor (token: string) : Result<int64 * Guid, string> =
    let decoded =
        try
            Some(Encoding.UTF8.GetString(Convert.FromBase64String token))
        with _ ->
            None

    match decoded with
    | None -> Error "The page cursor could not be read. Reload the audit view to start from the newest page."
    | Some raw ->
        match raw.Split(':') with
        | [| ticks; id |] ->
            match Int64.TryParse(ticks, NumberStyles.Integer, CultureInfo.InvariantCulture), Guid.TryParse id with
            | (true, t), (true, g) -> Ok(t, g)
            | _ -> Error "The page cursor could not be read. Reload the audit view to start from the newest page."
        | _ -> Error "The page cursor could not be read. Reload the audit view to start from the newest page."

/// Strictly-after test in the `(OccurredAt desc, Id desc)` order — the
/// same order `readTrail` sorts into, expressed over the two fields the
/// cursor carries.
let private isAfterCursor (ticks: int64, id: Guid) (occurredAt: DateTime) (rowId: Guid) =
    if occurredAt.Ticks <> ticks then
        occurredAt.Ticks < ticks
    else
        String.Compare(rowId.ToString "N", id.ToString "N", StringComparison.Ordinal) < 0

// ─── Payload projections ──────────────────────────────────────────────

/// Read the payload JSON's root object once. `None` for a payload that
/// is not a JSON object — a tombstoned row (`Erasure.TombstoneMarker`)
/// is exactly that, and it must still appear in the trail with its
/// envelope intact rather than being filtered out of it.
let private tryRoot (payload: string) : JsonElement option =
    try
        let doc = JsonDocument.Parse payload

        if doc.RootElement.ValueKind = JsonValueKind.Object then
            Some(doc.RootElement.Clone())
        else
            None
    with _ ->
        None

/// Best-effort actor, probed by field name in declared order.
let private projectActor (root: JsonElement option) : string option =
    match root with
    | None -> None
    | Some root ->
        AuditViewProjection.actorFieldNames
        |> List.tryPick (fun name ->
            match root.TryGetProperty name with
            | true, value when value.ValueKind = JsonValueKind.String ->
                match value.GetString() with
                | null -> None
                | s when String.IsNullOrWhiteSpace s -> None
                | s -> Some s
            | _ -> None)

/// One-line rendering of the payload's scalar fields, bounded in both
/// field count and length. Nested objects and arrays are skipped rather
/// than flattened — the detail expansion shows them in full, and a
/// half-flattened object in a table cell reads as data loss.
let private projectSummary (root: JsonElement option) (payload: string) : string =
    match root with
    | None ->
        // Not a JSON object: show the raw payload, bounded. This is the
        // tombstone case, and a tombstone marker is the whole message.
        if payload.Length <= AuditViewProjection.SummaryMaxLength then
            payload
        else
            payload.Substring(0, AuditViewProjection.SummaryMaxLength - 1) + "…"
    | Some root ->
        let rendered =
            root.EnumerateObject()
            |> Seq.choose (fun prop ->
                match prop.Value.ValueKind with
                | JsonValueKind.String ->
                    match prop.Value.GetString() with
                    | null -> None
                    | s when String.IsNullOrWhiteSpace s -> None
                    | s -> Some(sprintf "%s=%s" prop.Name s)
                | JsonValueKind.Number -> Some(sprintf "%s=%s" prop.Name (prop.Value.GetRawText()))
                | JsonValueKind.True -> Some(sprintf "%s=true" prop.Name)
                | JsonValueKind.False -> Some(sprintf "%s=false" prop.Name)
                | _ -> None)
            |> Seq.truncate AuditViewProjection.SummaryMaxFields
            |> String.concat "; "

        if rendered.Length <= AuditViewProjection.SummaryMaxLength then
            rendered
        else
            rendered.Substring(0, AuditViewProjection.SummaryMaxLength - 1) + "…"

let private toView (evt: ModuleEvent) : AuditEventView =
    let root = tryRoot evt.Payload

    {
        Id = evt.Id
        OccurredAt = evt.OccurredAt
        EventType = evt.EventType
        Actor = projectActor root
        Summary = projectSummary root evt.Payload
        Payload = evt.Payload
    }

// ─── Filtering ────────────────────────────────────────────────────────

let private matchesQuery (query: AuditTrailQuery) (view: AuditEventView) =
    let inWindow =
        (match query.From with
         | Some f -> view.OccurredAt >= f
         | None -> true)
        && (match query.To with
            | Some t -> view.OccurredAt <= t
            | None -> true)

    let matchesType =
        match query.EventType with
        | Some et -> String.Equals(view.EventType, et, StringComparison.Ordinal)
        | None -> true

    // Substring, case-insensitive: an operator filtering by actor is
    // typing a fragment of a user id, not reproducing one exactly. A row
    // with no projected actor never matches a filter that names one —
    // "unattributed" is not a wildcard.
    let matchesActor =
        match query.Actor with
        | Some a when not (String.IsNullOrWhiteSpace a) ->
            match view.Actor with
            | Some actor -> actor.Contains(a.Trim(), StringComparison.OrdinalIgnoreCase)
            | None -> false
        | _ -> true

    inWindow && matchesType && matchesActor

// ─── CSV ──────────────────────────────────────────────────────────────

let private escapeCsvField (raw: string) =
    if raw.Contains '"' || raw.Contains ',' || raw.Contains '\n' || raw.Contains '\r' then
        "\"" + raw.Replace("\"", "\"\"") + "\""
    else
        raw

let private formatCsvDate (ts: DateTime) =
    ts.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)

/// Render the matched window. The payload rides along verbatim so the
/// export is a faithful copy of the trail rather than a summary of it —
/// an export an auditor cannot reconcile against the source is worse
/// than no export.
let internal renderCsv (views: AuditEventView list) : byte[] =
    let header = "Id,OccurredAt,EventType,Actor,Summary,Payload"

    let lines =
        views
        |> List.map (fun v ->
            [
                v.Id.ToString "N"
                formatCsvDate v.OccurredAt
                v.EventType
                v.Actor |> Option.defaultValue ""
                v.Summary
                v.Payload
            ]
            |> List.map escapeCsvField
            |> String.concat ",")

    let body = (header :: lines) |> String.concat "\r\n"
    Encoding.UTF8.GetBytes(body + "\r\n")

// ─── Handler ──────────────────────────────────────────────────────────

/// Build the `IAuditViewApi` Fable.Remoting handler.
///
/// `auditLogMode` is captured at compose time from
/// `ServerConfig.AuditLog`. On the default `NoAuditLog` the deployment
/// records nothing, so every method answers empty — the same answer
/// `NoOpAuditLog` gives the rest of the SDK, restored here because this
/// handler reads the event store rather than the audit log (see the
/// header). The route stays mounted either way, mirroring
/// `usageQueryApiHandler`: a client whose proxy 404s cannot tell "off"
/// from "broken", and the module's empty state says "off" plainly.
let auditViewApi (auditLogMode: AuditLogMode) (ctx: HttpContext) : IAuditViewApi =

    let accessContext = resolveAccessContext ctx
    let scopeId = callerScopeId accessContext

    let eventStoreOpt: IEventStore option =
        match ctx.RequestServices.GetService(typeof<IEventStore>) with
        | :? IEventStore as es -> Some es
        | _ -> None

    /// Owner/Admin read gate. `UsageQueryApiHandler.ensureReadAllowed`'s
    /// shape: the audit trail is at least as sensitive as the spend
    /// figures that predicate already guards, and using a second
    /// predicate for it would be a second thing to keep in step.
    let ensureReadAllowed () : Async<Result<unit, string>> = async {
        match accessContext.Subject with
        | AnonymousSession _ -> return Error "The audit trail is not available in this mode."
        | TeamMember(userId, teamId) ->
            match ctx.RequestServices.GetService(typeof<ITeamStore>) with
            | :? ITeamStore as ts ->
                let! role = ts.GetMemberRole(teamId, userId)

                match role with
                | Some r when TeamRoles.canWriteTeamConfig r -> return Ok()
                | Some r ->
                    return
                        Error
                            $"Only team owners and admins can view the audit trail. Your role: {TeamRoles.displayName r}."
                | None -> return Error "You are not a member of this team."
            | _ -> return Error "Team management is not available in this deployment."
        | AuthenticatedUser _
        | ClaimBearer _ -> return Ok()
    }

    /// Every row in the caller's scope, newest first, in the total order
    /// the cursor is expressed in. Empty under `NoAuditLog` and when no
    /// event store is registered (a test path that wired neither).
    let readTrail () : Async<AuditEventView list> = async {
        match auditLogMode, eventStoreOpt with
        | NoAuditLog, _
        | _, None -> return []
        | EnabledAuditLog, Some store ->
            let! events = store.ReadBySource(scopeId, AuditSourceModule.value)

            return
                events
                |> List.sortByDescending (fun e -> e.OccurredAt, e.Id.ToString "N")
                |> List.map toView
    }

    let withGate (f: unit -> Async<Result<'T, string>>) : Async<Result<'T, string>> = async {
        match! ensureReadAllowed () with
        | Error msg -> return Error msg
        | Ok() -> return! f ()
    }

    /// The filtered window, before paging — shared by `Query` (which
    /// then takes one page of it) and `ExportCsv` (which takes all of
    /// it), so the two can never disagree about what the filter means.
    let matchedWindow (query: AuditTrailQuery) = async {
        let! all = readTrail ()
        return all |> List.filter (matchesQuery query)
    }

    {
        Query =
            fun query ->
                withGate (fun () -> async {
                    let! matched = matchedWindow query
                    let pageSize = AuditViewApi.clampPageSize query.PageSize

                    let afterCursor =
                        match query.Cursor with
                        | None -> Ok matched
                        | Some token ->
                            decodeCursor token
                            |> Result.map (fun cursor ->
                                matched |> List.filter (fun v -> isAfterCursor cursor v.OccurredAt v.Id))

                    match afterCursor with
                    | Error msg -> return Error msg
                    | Ok remaining ->
                        let page = remaining |> List.truncate pageSize

                        let nextCursor =
                            if List.length remaining > pageSize then
                                page
                                |> List.tryLast
                                |> Option.map (fun last -> encodeCursor last.OccurredAt last.Id)
                            else
                                None

                        return
                            Ok {
                                Events = page
                                NextCursor = nextCursor
                                MatchedCount = List.length matched
                            }
                })

        ListEventTypes =
            fun () ->
                withGate (fun () -> async {
                    let! all = readTrail ()

                    return
                        Ok(
                            all
                            |> List.map _.EventType
                            |> List.distinct
                            |> List.sortWith (fun a b -> String.Compare(a, b, StringComparison.Ordinal))
                        )
                })

        ExportCsv =
            fun query ->
                withGate (fun () -> async {
                    let! matched = matchedWindow query
                    return Ok(renderCsv matched)
                })
    }