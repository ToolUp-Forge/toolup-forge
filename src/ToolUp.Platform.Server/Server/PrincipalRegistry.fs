module ToolUp.Platform.PrincipalRegistry

open System
open System.Text
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Remoting.Json.SystemTextJson

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

let private platformContainer = "_platform"
let private membershipsPrefix = "memberships/"
let private userScopePrefix = "user-"

/// Default `UserLoggedIn` look-back window. Bounds how far the audit
/// sweep reaches when deriving `LastSeenAt` — a login older than this
/// contributes no last-seen evidence. Deployments needing a different
/// bound call `listPrincipalsWith` directly.
let defaultAuditLookBack = TimeSpan.FromDays 90.0

// Same converter set as the audit write path (`EventStoreAuditLog`),
// so the payload decode here tracks the persisted wire shape.
let private auditJsonOptions = FableConverters.create ()

/// Parse one stored membership blob (TeamManagement's wire shape) to
/// `(teamId, role)` rows. A malformed / tombstoned blob degrades to
/// `[]` — the blob still evidences the principal, but contributes no
/// membership row; a single corrupt blob never fails the sweep.
let private parseMemberships (bytes: byte[]) : (string * TeamRole) list =
    try
        let doc = JsonDocument.Parse(Encoding.UTF8.GetString bytes)

        [
            for elem in doc.RootElement.EnumerateArray() do
                let role =
                    match elem.GetProperty("role").GetString() with
                    | "Owner" -> Owner
                    | "Admin" -> Admin
                    | _ -> Member

                elem.GetProperty("teamId").GetString(), role
        ]
    with _ -> []

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

        // (a) membership blob owners → per-user (teamId, role) rows.
        let! membershipBlobs = storage.List(platformContainer, membershipsPrefix)

        let! membershipPairs =
            membershipBlobs
            |> List.map (fun name -> async {
                let userId = name.Replace(membershipsPrefix, "").Replace(".json", "")
                let! result = storage.Download(platformContainer, name)

                return
                    match result with
                    | Ok bytes -> Some(userId, parseMemberships bytes)
                    | Error _ -> None
            })
            |> bounded

        let memberships = membershipPairs |> Array.choose id |> Map.ofArray

        // (b) user-scope event evidence + the scope list the audit
        // sweep fans out over.
        let! scopes = events.ListScopes()

        let userScopeIds =
            scopes
            |> List.choose (fun s ->
                if s.StartsWith(userScopePrefix, StringComparison.Ordinal) then
                    Some(s.Substring userScopePrefix.Length)
                else
                    None)
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
                lastSeen |> Map.keys |> Set.ofSeq
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
                    let! blobs = storage.List(userScopePrefix + userId, "")
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
                LastSeenAt = lastSeen |> Map.tryFind userId
                HasUserScopeData = hasScopeData |> Map.tryFind userId |> Option.defaultValue false
            })
    }

/// `listPrincipalsWith` under the default look-back window.
let listPrincipals (storage: IBlobStorage) (events: IEventStore) : Async<PrincipalSummary list> =
    listPrincipalsWith storage events defaultAuditLookBack