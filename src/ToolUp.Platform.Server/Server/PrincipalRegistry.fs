module ToolUp.Platform.Compose.PrincipalRegistry

open System
open System.Text
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.Config
open ToolUp.Platform.Infra

// ─── Phase 543 — derived principal enumeration (aggregation) ─────────
//
// Answers "who has ever signed in, and which of them belong to no
// team?" as a **derived, read-only projection** over three existing
// stores — never a stored registry blob, so a stale entry cannot exist
// (a fresh call reflects a membership added one second ago):
//
//   (a) `_platform/memberships/{userId}.json` blob owners via
//       `IBlobStorage.List` — the membership evidence. The blob wire
//       shape is `TeamManagement`'s stored-membership format
//       (camelCase `teamId` / `role` / `joinedAt`); parsed here
//       independently so this read-only sweep takes no dependency on
//       the store's write-side internals.
//   (b) `user-{userId}` scopes via `IEventStore.ListScopes` — the
//       portable user-scope enumeration seam. `IBlobStorage`
//       deliberately has no container enumeration (containers are
//       scope-derived, GP 4), so a per-candidate blob probe
//       (`List(container, "")`) supplies the blob-side
//       `HasUserScopeData` evidence instead. A principal whose ONLY
//       trace is blobs in their scope container (no membership row, no
//       event, no login inside the window) is therefore not
//       independently discoverable — in practice user activity that
//       writes blobs also writes scope events (`FileUploaded` audit
//       rows land in the event store), so the event fabric is the
//       reliable discovery surface.
//   (c) distinct `UserLoggedIn` subjects across every scope's audit
//       rows (`SourceModule = AuditSourceModule.value`), bounded by a
//       configurable look-back window. Logins are recorded under the
//       caller's *resolved* scope (a team scope when an active team is
//       set), so the sweep reads every scope, not just `user-*` ones.
//       The envelope's `OccurredAt` supplies `LastSeenAt`.
//
// Read-only by construction: no write path, nothing to drift. GP 12
// audit: identity by value, async at every boundary, stateless between
// calls. Bounded parallelism (cap = 32) per the `GetTeamMembers`
// precedent. GP 13: pure per-call cost — no hosted service, no cache,
// zero cost when never invoked.

let private platformContainer = ScopeKey.PlatformContainer
let private membershipsPrefix = "memberships/"

/// Default `UserLoggedIn` look-back window. Bounds how far the audit
/// sweep reaches when deriving `LastSeenAt` — a login older than this
/// contributes no last-seen evidence. Deployments needing a different
/// bound call `listPrincipalsWith` directly.
let defaultAuditLookBack = TimeSpan.FromDays 90.0

// Same converter set as the audit write path (`EventStoreAuditLog`),
// so the payload decode here tracks the persisted wire shape.
let private auditJsonOptions = PlatformJson.options

let private bounded (comps: Async<'a> list) : Async<'a[]> =
    Async.Parallel(comps, maxDegreeOfParallelism = 32)

/// Enumerate every principal the substrate has evidence for, merging
/// the three evidence sources per `UserId`. `auditLookBack` bounds the
/// `UserLoggedIn` sweep (see `defaultAuditLookBack`). Results are
/// sorted by `UserId` for stable output.
let listPrincipalsWith
    (storage: IBlobStorage)
    (events: IEventStore)
    (auditLookBack: TimeSpan)
    : Async<PrincipalSummary list> =
    async {
        let cutoff = DateTime.UtcNow - auditLookBack

        // (a) membership document owners → per-user (teamId, role) rows,
        // read through TeamManagement's own codec so the shapes cannot
        // drift. A document that cannot be read or decoded still evidences
        // its principal but contributes no membership row, logged (and
        // quarantined by the store): a single bad document never fails the
        // sweep, and never hides who holds an account.
        let docs =
            BlobDocumentStore<TeamManagement.StoredMembership list>(
                storage,
                TeamManagement.membershipCodec,
                ConsoleLogger.ConsoleLogger() :> ILogger
            )

        let! membershipRows = docs.List(ScopeKey.platform, membershipsPrefix, 32)

        let memberships =
            membershipRows
            |> List.map (fun (path, row) ->
                let userId =
                    (DocPath.value path).Replace(membershipsPrefix, "").Replace(".json", "")

                match row with
                | Ok rows -> userId, rows |> List.map (fun m -> m.TeamId.Value, m.Role)
                | Error e ->
                    (ConsoleLogger.ConsoleLogger() :> ILogger)
                        .Warn($"[PrincipalRegistry] memberships of {userId} left out: {BlobMapStoreError.describe e}")

                    userId, [])
            |> Map.ofList

        // (b) user-scope event evidence + the scope list the audit
        // sweep fans out over.
        let! scopes = events.ListScopes()

        let userScopeIds =
            scopes
            |> List.choose (fun s ->
                match ScopeKey.tryParseContainer s with
                | Some(ScopeKey.User userId) -> Some userId.Value
                | _ -> None)
            |> Set.ofList

        // (c) distinct UserLoggedIn subjects within the look-back
        // window, across every scope's audit rows. A payload that no
        // longer decodes (schema drift, tombstoned by erasure) is
        // skipped rather than failing the sweep.
        let! loginBatches =
            scopes
            |> List.map (fun scope -> async {
                let! rows = events.ReadByType(scope, "UserLoggedIn")

                return
                    rows
                    |> List.filter (fun e -> e.SourceModule = AuditSourceModule.value && e.OccurredAt >= cutoff)
                    |> List.choose (fun e ->
                        try
                            let payload =
                                JsonSerializer.Deserialize<UserLoggedInPayload>(e.Payload, auditJsonOptions)

                            Some(payload.UserId, e.OccurredAt)
                        with _ ->
                            None)
            })
            |> bounded

        let lastSeen =
            loginBatches
            |> Seq.collect id
            |> Seq.groupBy fst
            |> Seq.map (fun (userId, hits) -> userId, hits |> Seq.map snd |> Seq.max)
            |> Map.ofSeq

        // Union of the three evidence sources, merged per UserId.
        let candidates =
            Set.unionMany [
                memberships |> Map.keys |> Set.ofSeq
                userScopeIds
                lastSeen |> Map.keys |> Seq.map UserId.value |> Set.ofSeq
            ]

        // Blob-side probe for candidates not already evidenced by
        // user-scope events (see the module header on why this is a
        // probe, not an enumeration).
        let! scopeDataPairs =
            candidates
            |> Set.toList
            |> List.map (fun userId -> async {
                if Set.contains userId userScopeIds then
                    return userId, true
                else
                    let! blobs = storage.List(ScopeKey.container (ScopeKey.User(UserId userId)), "")
                    return userId, not (List.isEmpty blobs)
            })
            |> bounded

        let hasScopeData = Map.ofArray scopeDataPairs

        return
            candidates
            |> Set.toList
            |> List.sort
            |> List.map (fun userId -> {
                UserId = userId
                Memberships = memberships |> Map.tryFind userId |> Option.defaultValue []
                LastSeenAt = lastSeen |> Map.tryFind (UserId userId)
                HasUserScopeData = hasScopeData |> Map.tryFind userId |> Option.defaultValue false
            })
    }

/// `listPrincipalsWith` under the default look-back window.
let listPrincipals (storage: IBlobStorage) (events: IEventStore) : Async<PrincipalSummary list> =
    listPrincipalsWith storage events defaultAuditLookBack