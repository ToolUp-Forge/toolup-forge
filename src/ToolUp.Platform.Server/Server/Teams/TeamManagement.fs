module ToolUp.Platform.TeamManagement

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Storage constants ───────────────────────────────────────────
// `internal`, not `private` — `MembershipDoctor` (Phase 546) walks the
// same layout and must not mirror the literals.

let internal platformContainer = "_platform"
let internal teamBlobName teamId = $"teams/{teamId}.json"
let internal membershipBlobName userId = $"memberships/{userId}.json"
let internal activeTeamBlobName userId = $"active-team/{userId}.txt"

// ─── User membership record (stored per user) ───────────────────

type StoredMembership = {
    TeamId: string
    Role: TeamRole
    JoinedAt: DateTime
}

// ─── JSON serialisation helpers ──────────────────────────────────
// `internal` — the membership wire codec is shared with
// `MembershipDoctor` (Phase 546) so the two can never drift.

module internal Json =
    let private options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let serializeTeam (team: TeamInfo) =
        let obj = {|
            teamId = team.TeamId
            name = team.Name
            createdAt = team.CreatedAt.ToString("o")
            archived = team.Archived
        |}

        JsonSerializer.Serialize(obj, options) |> Encoding.UTF8.GetBytes

    let deserializeTeam (bytes: byte[]) : TeamInfo =
        let doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes))
        let root = doc.RootElement

        // `archived` is optional for back-compat — team blobs written
        // before the field existed have no `archived` property and
        // deserialize as not-archived.
        let archived =
            match root.TryGetProperty("archived") with
            | true, prop -> prop.GetBoolean()
            | false, _ -> false

        {
            TeamId = root.GetProperty("teamId").GetString()
            Name = root.GetProperty("name").GetString()
            CreatedAt = DateTime.Parse(root.GetProperty("createdAt").GetString())
            Archived = archived
        }

    let private roleToString =
        function
        | Owner -> "Owner"
        | Admin -> "Admin"
        | Member -> "Member"

    let private stringToRole =
        function
        | "Owner" -> Owner
        | "Admin" -> Admin
        | _ -> Member

    let serializeMemberships (memberships: StoredMembership list) =
        let dtos =
            memberships
            |> List.map (fun m -> {|
                teamId = m.TeamId
                role = roleToString m.Role
                joinedAt = m.JoinedAt.ToString("o")
            |})

        JsonSerializer.Serialize(dtos, options) |> Encoding.UTF8.GetBytes

    let deserializeMemberships (bytes: byte[]) : StoredMembership list =
        let doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes))

        [
            for elem in doc.RootElement.EnumerateArray() do
                {
                    TeamId = elem.GetProperty("teamId").GetString()
                    Role = stringToRole (elem.GetProperty("role").GetString())
                    JoinedAt = DateTime.Parse(elem.GetProperty("joinedAt").GetString())
                }
        ]

// ─── ITeamStore ──────────────────────────────────────────────────

/// Replaceable backend for team metadata, memberships, and active-team
/// tracking. Mirrors every other core SDK infrastructure concern
/// (`IBlobStorage`, `IEventStore`, `INotificationChannel`, `IAIProvider`,
/// `IEmbeddingProvider`, `IVectorStore`, `IConfigStore`, `IPermissionStore`,
/// `IFeatureFlagStore`, `IProviderProfile`) so distributed deployments
/// (Orleans grain, Akka.Persistence actor, Postgres-backed store, external
/// team directory) can drop in without patching SDK consumers.
///
/// All methods are `Async<_>` — GP 12 rule 2 (portability).
/// Identity is carried by value (`string` teamId/userId) — GP 12 rule 1.
type ITeamStore =
    /// Create a new team with the given id and display name. Persists to
    /// the backing store. Returns the constructed `TeamInfo` on success.
    abstract CreateTeam: teamId: string * name: string -> Async<Result<TeamInfo, string>>
    /// Delete a team's metadata blob. Used to roll back a half-created
    /// team when the owner-membership write fails, so a partial create
    /// can't leave an orphan (owner-less, inaccessible) team behind.
    abstract DeleteTeam: teamId: string -> Async<Result<unit, string>>
    /// Look up a team by id. `None` when the team does not exist.
    abstract GetTeam: teamId: string -> Async<TeamInfo option>
    /// List every team in the store. Used by admin surfaces and diagnostics.
    abstract ListTeams: unit -> Async<TeamInfo list>
    /// Add `userId` to `teamId` with `role`. Returns `Error` when the user
    /// is already a member.
    abstract AddMember: teamId: string * userId: string * role: TeamRole -> Async<Result<unit, string>>
    /// Remove `userId` from `teamId`. Refuses to remove the last Owner —
    /// a team without an Owner would be unmanageable. Clears the user's
    /// active-team pointer when it matched `teamId`.
    abstract RemoveMember: teamId: string * userId: string -> Async<Result<unit, string>>
    /// Change an existing member's role on a team. Idempotent when the
    /// role is unchanged. Refuses to demote the last Owner.
    abstract ChangeMemberRole: teamId: string * userId: string * newRole: TeamRole -> Async<Result<unit, string>>
    /// List every team `userId` is a member of.
    abstract GetTeamsForUser: userId: string -> Async<TeamInfo list>
    /// List every member of `teamId` as a `TeamMembership` record.
    abstract GetTeamMembers: teamId: string -> Async<TeamMembership list>
    /// Look up `userId`'s role on `teamId`. `None` when the user is not
    /// a member.
    abstract GetMemberRole: teamId: string * userId: string -> Async<TeamRole option>
    /// Return the user's currently-selected active team id. `None` when
    /// the user has no active team set.
    abstract GetActiveTeam: userId: string -> Async<string option>
    /// Set the user's active team. Returns `Error` when the user is not
    /// a member of the requested team.
    abstract SetActiveTeam: userId: string * teamId: string -> Async<Result<unit, string>>
    /// Set a team's archived flag. Archiving (`archived = true`) also
    /// bumps every member whose active-team pointer is this team to the
    /// no-active-team state (clears the pointer + publishes
    /// `MembershipChanged` so resolver caches evict) — an archived team
    /// must not remain a member's active scope. Restoring
    /// (`archived = false`) flips the flag only; members re-select the
    /// team themselves. `Error` when the team does not exist.
    abstract SetArchived: teamId: string * archived: bool -> Async<Result<unit, string>>
    /// **Irreversibly** purge a team: delete the team record AND strip
    /// the team from every member's membership rows + clear active-team
    /// pointers referencing it. Distinct from `DeleteTeam`, which only
    /// removes the team blob (the create-rollback primitive). `Error`
    /// when the team does not exist.
    abstract PurgeTeam: teamId: string -> Async<Result<unit, string>>
    /// **Irreversibly** purge a user's platform membership state — the
    /// user-scope twin of `PurgeTeam` (Phase 545): strip every
    /// membership row, delete the active-team pointer, and publish
    /// `MembershipChanged.Removed` per affected team so resolver caches
    /// evict. Refuses when the user is the last Owner of any team (the
    /// error names the team) — the operator must transfer ownership or
    /// purge that team first, the same unmanageable-team safeguard as
    /// `RemoveMember`. Idempotent — purging a user with no membership
    /// state returns `Ok`.
    abstract PurgeUser: userId: string -> Async<Result<unit, string>>

// ─── TeamStore ───────────────────────────────────────────────────

/// SDK-owned default `ITeamStore` implementation, backed by `IBlobStorage`.
/// All team metadata lives under the `_platform` container.
/// Auth providers supply identity only — team CRUD, membership,
/// and active-team tracking are entirely SDK-owned.
///
/// `notifications` is required, not optional: every successful
/// membership write publishes a `MembershipChanged` envelope on
/// `PlatformReservedScope` so caches that depend on membership
/// (`TeamScopeResolver` first, future `ConfigHandler` /
/// `FeatureFlagHandler` caches as they appear) invalidate
/// structurally rather than via 5-minute TTL. Phase 5d's whole
/// point is that publication can't be skipped — making the channel
/// optional would re-introduce the silent-breakage failure mode.
///
/// Membership writes and team creation are guarded read-modify-writes
/// (`BlobMapStore`, Phase 864): a membership blob that cannot be read or
/// decoded fails the operation and is never overwritten, and on a
/// backend implementing `IConditionalBlobStorage` concurrent writers on
/// any number of nodes lose no update. `logger` receives the one-time
/// warning when the backend cannot do conditional writes, and the
/// quarantine report for an undecodable blob.
type TeamStore(storage: IBlobStorage, notifications: INotificationChannel, logger: ILogger) =

    // Per-user serialisation lock around the membership read-modify-write
    // cycle. Since Phase 864 the cross-node guarantee is the backend's
    // conditional write (`BlobMapStore` below): two concurrent `AddMember`
    // calls for the same user on two replicas both land, the loser of the
    // precondition re-reading and replaying its change. The semaphore
    // remains as the in-process serialisation — it keeps same-node writers
    // from spending the CAS retry budget on each other, and it is the whole
    // guarantee on a backend WITHOUT conditional writes, where `BlobMapStore`
    // falls back to unconditional writes (single-instance only).
    //
    // Unbounded by design: `userId` cardinality is bounded by registered
    // users, a `SemaphoreSlim(1, 1)` is small (~200 bytes), and a sweeper
    // would add complexity for negligible memory. If userid cardinality
    // ever grows unbounded (e.g. anonymous-user fan-out), revisit.
    let userLocks = ConcurrentDictionary<string, SemaphoreSlim>()

    // Guarded read-modify-write of one user's membership list, and of a
    // team record at creation. A read failure or a decode failure is an
    // error, never an empty list (GP 9).
    let membershipCodec: BlobCodec<StoredMembership list> = {
        Encode = Json.serializeMemberships
        Decode =
            fun content ->
                try
                    Ok(Json.deserializeMemberships content)
                with ex ->
                    Error ex.Message
    }

    let teamCodec: BlobCodec<TeamInfo> = {
        Encode = Json.serializeTeam
        Decode =
            fun content ->
                try
                    Ok(Json.deserializeTeam content)
                with ex ->
                    Error ex.Message
    }

    let membershipDocs =
        BlobMapStore<StoredMembership list>(storage, membershipCodec, logger)

    let teamDocs = BlobMapStore<TeamInfo>(storage, teamCodec, logger)

    /// Read-modify-write `userId`'s memberships (absent = no memberships).
    /// A storage-level failure surfaces as `Error`; nothing is written.
    let updateMemberships
        (userId: string)
        (transform: StoredMembership list -> BlobUpdate<StoredMembership list, 'R>)
        : Async<Result<'R, string>> =
        async {
            let! result =
                membershipDocs.Update(
                    platformContainer,
                    membershipBlobName userId,
                    fun current -> transform (Option.defaultValue [] current)
                )

            return result |> Result.mapError BlobMapStoreError.describe
        }

    let withUserLock (userId: string) (work: Async<'a>) : Async<'a> = async {
        let sem = userLocks.GetOrAdd(userId, fun _ -> new SemaphoreSlim(1, 1))
        do! sem.WaitAsync() |> Async.AwaitTask

        try
            return! work
        finally
            sem.Release() |> ignore
    }

    // Publish a `MembershipChanged` envelope after a successful
    // membership write. Failures here do not roll back the write —
    // the underlying state is durable in blob storage and the cache
    // re-check on every request is defense-in-depth. We log via the
    // channel's own exception path (handlers swallow on the
    // subscriber side) and proceed.
    let publishChange (teamId: string) (userId: string) (kind: MembershipChangeKind) = async {
        let payload: MembershipChangedPayload = {
            TeamId = teamId
            AffectedUserId = userId
            ChangeKind = kind
            PublishedAt = DateTime.UtcNow
        }

        do! notifications.Publish(NotificationKind.PlatformReservedScope, MembershipChanged payload)
    }

    /// Default logger: the console. Composition roots that hold a
    /// deployment logger pass it through the three-argument constructor.
    new(storage: IBlobStorage, notifications: INotificationChannel) =
        TeamStore(storage, notifications, ConsoleLogger.ConsoleLogger() :> ILogger)

    // ── Team CRUD ────────────────────────────────────────────

    member _.CreateTeam(teamId: string, name: string) = async {
        // Fail-closed on an already-existing team blob. The team id is the
        // data partition key (`team-{teamId}`), so silently overwriting an
        // existing team would co-tenant two distinct teams onto one
        // container — a tenant-isolation breach (GP 4). The existence check
        // and the create are ONE act (Phase 864): the write carries
        // `IfAbsent` on a conditional backend, so two concurrent creates of
        // one id admit exactly one. A team blob that exists but cannot be
        // read or decoded is an error, never "absent, so overwrite".
        let team: TeamInfo = {
            TeamId = teamId
            Name = name
            CreatedAt = DateTime.UtcNow
            Archived = false
        }

        let! result =
            teamDocs.Update(
                platformContainer,
                teamBlobName teamId,
                function
                | Some _ -> BlobUpdate.Keep(Error $"Team '{teamId}' already exists")
                | None -> BlobUpdate.Write(team, Ok team)
            )

        match result with
        | Ok outcome -> return outcome
        | Error e -> return Error(BlobMapStoreError.describe e)
    }

    /// Delete the team's metadata blob. Used by the create path to roll
    /// back a team whose owner-membership write failed; deletes only the
    /// team record (a half-created team has no members to clean up).
    member _.DeleteTeam(teamId: string) = async { return! storage.Delete(platformContainer, teamBlobName teamId) }

    member _.GetTeam(teamId: string) = async {
        let! result = storage.Download(platformContainer, teamBlobName teamId)

        return
            match result with
            | Ok bytes -> Some(Json.deserializeTeam bytes)
            | Error _ -> None
    }

    member _.ListTeams() = async {
        let! blobNames = storage.List(platformContainer, "teams/")

        let! teams =
            blobNames
            |> List.map (fun name -> async {
                let! result = storage.Download(platformContainer, name)

                return
                    match result with
                    | Ok bytes -> Some(Json.deserializeTeam bytes)
                    | Error _ -> None
            })
            // Bounded parallelism (cap = 32) keeps a 1000-blob List from
            // saturating the cloud backend's connection pool while still
            // collapsing 32x of the per-blob round-trip latency. Sequential
            // was visibly slow even on a 100-user platform.
            |> fun comps -> Async.Parallel(comps, maxDegreeOfParallelism = 32)

        return teams |> Array.choose id |> Array.toList
    }

    // ── Membership ───────────────────────────────────────────

    member private _.LoadMemberships(userId: string) = async {
        let! result = storage.Download(platformContainer, membershipBlobName userId)

        return
            match result with
            | Ok bytes -> Json.deserializeMemberships bytes
            | Error _ -> []
    }

    member _.AddMember(teamId: string, userId: string, role: TeamRole) =
        withUserLock
            userId
            (async {
                let entry = {
                    TeamId = teamId
                    Role = role
                    JoinedAt = DateTime.UtcNow
                }

                let! outcome =
                    updateMemberships userId (fun existing ->
                        if existing |> List.exists (fun m -> m.TeamId = teamId) then
                            BlobUpdate.Keep(Error "User is already a member of this team")
                        else
                            BlobUpdate.Write(entry :: existing, Ok()))

                match outcome with
                | Error storageFailure -> return Error storageFailure
                | Ok(Error refusal) -> return Error refusal
                | Ok(Ok()) ->
                    do! publishChange teamId userId MembershipChangeKind.Added
                    return Ok()
            })

    /// Would removing/demoting this user leave the team with no
    /// Owners? If yes, the operation must be rejected — a team
    /// without an Owner is unmanageable (no one can add members,
    /// change roles, or recover).
    member private this.IsLastOwner(teamId: string, userId: string) : Async<bool> = async {
        let! (members: TeamMembership list) = this.GetTeamMembers(teamId)

        let owners = members |> List.filter (fun m -> m.Role = Owner)

        return owners.Length = 1 && owners[0].UserId = userId
    }

    member this.RemoveMember(teamId: string, userId: string) =
        withUserLock
            userId
            (async {
                // IsLastOwner reads other users' membership blobs (cross-user
                // scan). It runs OUTSIDE the per-user lock so we don't deadlock
                // on a recursive lock acquisition pattern in future refactors.
                // The cross-user concurrent-Owner-removal race (two admins
                // simultaneously removing the second-to-last owner) is a Phase
                // 9c concern requiring distributed primitives — flagged in the
                // class-level comment.
                let! isLastOwner = this.IsLastOwner(teamId, userId)

                if isLastOwner then
                    return Error "Cannot remove the last Owner from a team"
                else
                    // Phase 971 — the pointer goes BEFORE the row. The row is
                    // what a retry finds the member by: removed first, a
                    // refused pointer delete would leave a retry answering
                    // "not a member" and never reaching the pointer. So a
                    // refusal answers Error with the row untouched, and a
                    // plain retry re-runs both. Only a member's pointer is
                    // cleared; a non-member keeps the "not a member" answer.
                    let! read = membershipDocs.Read(platformContainer, membershipBlobName userId)

                    let! pointerCleared = async {
                        match read |> Result.map (Option.defaultValue []) with
                        | Error e -> return Error(BlobMapStoreError.describe e)
                        | Ok existing when not (existing |> List.exists (fun m -> m.TeamId = teamId)) ->
                            return Error "User is not a member of this team"
                        | Ok _ ->
                            let! activeTeam = this.GetActiveTeam(userId)

                            match activeTeam with
                            | Some active when active = teamId ->
                                match! storage.Delete(platformContainer, activeTeamBlobName userId) with
                                | Ok() -> return Ok()
                                | Error storageError ->
                                    return
                                        Error(
                                            sprintf
                                                "The member was not removed: their active-team pointer %s could not be cleared: %s"
                                                (activeTeamBlobName userId)
                                                storageError
                                        )
                            | _ -> return Ok()
                    }

                    match pointerCleared with
                    | Error e -> return Error e
                    | Ok() ->
                        let! outcome =
                            updateMemberships userId (fun existing ->
                                let updated = existing |> List.filter (fun m -> m.TeamId <> teamId)

                                if existing.Length = updated.Length then
                                    BlobUpdate.Keep(Error "User is not a member of this team")
                                else
                                    BlobUpdate.Write(updated, Ok()))

                        match outcome with
                        | Error storageFailure -> return Error storageFailure
                        | Ok(Error refusal) -> return Error refusal
                        | Ok(Ok()) ->
                            do! publishChange teamId userId MembershipChangeKind.Removed
                            return Ok()
            })

    /// Change an existing member's role on a team. Idempotent — no-op
    /// when the member already holds the requested role. Returns
    /// `Error` when the user is not a member of the team, or when the
    /// change would leave the team with no Owners (demoting the last
    /// Owner).
    member this.ChangeMemberRole(teamId: string, userId: string, newRole: TeamRole) =
        withUserLock
            userId
            (async {
                let! read = membershipDocs.Read(platformContainer, membershipBlobName userId)

                match read |> Result.map (Option.defaultValue []) with
                | Error e -> return Error(BlobMapStoreError.describe e)
                | Ok existing ->
                    match existing |> List.tryFind (fun m -> m.TeamId = teamId) with
                    | None -> return Error "User is not a member of this team"
                    | Some current when current.Role = newRole -> return Ok()
                    | Some _ ->
                        // Promotions and same-role no-ops are safe. Only demotions
                        // from Owner need the safeguard check.
                        let demotingOwner = newRole <> Owner

                        let! blockDemote =
                            if demotingOwner then
                                this.IsLastOwner(teamId, userId)
                            else
                                async { return false }

                        if blockDemote then
                            return Error "Cannot demote the last Owner of a team"
                        else
                            // The write re-validates against what it reads: a
                            // concurrent writer may have removed the member or
                            // already applied the role since the read above.
                            let! outcome =
                                updateMemberships userId (fun latest ->
                                    match latest |> List.tryFind (fun m -> m.TeamId = teamId) with
                                    | None -> BlobUpdate.Keep(Error "User is not a member of this team")
                                    | Some m when m.Role = newRole -> BlobUpdate.Keep(Ok false)
                                    | Some _ ->
                                        let updated =
                                            latest
                                            |> List.map (fun m ->
                                                if m.TeamId = teamId then { m with Role = newRole } else m)

                                        BlobUpdate.Write(updated, Ok true))

                            match outcome with
                            | Error storageFailure -> return Error storageFailure
                            | Ok(Error refusal) -> return Error refusal
                            | Ok(Ok false) -> return Ok()
                            | Ok(Ok true) ->
                                do! publishChange teamId userId MembershipChangeKind.RoleChanged
                                return Ok()
            })

    member this.GetTeamsForUser(userId: string) = async {
        let! memberships = this.LoadMemberships(userId)

        // Bounded parallelism (cap = 32) — see `ListTeams` for rationale.
        let! teams =
            memberships
            |> List.map (fun m -> this.GetTeam(m.TeamId))
            |> fun comps -> Async.Parallel(comps, maxDegreeOfParallelism = 32)

        return teams |> Array.choose id |> Array.toList
    }

    member this.GetTeamMembers(teamId: string) = async {
        let! blobNames = storage.List(platformContainer, "memberships/")

        let! allMemberships =
            blobNames
            |> List.map (fun name -> async {
                let! result = storage.Download(platformContainer, name)

                return
                    match result with
                    | Ok bytes ->
                        let userId = name.Replace("memberships/", "").Replace(".json", "")

                        Json.deserializeMemberships bytes
                        |> List.filter (fun m -> m.TeamId = teamId)
                        |> List.map (fun m -> {
                            TeamId = m.TeamId
                            UserId = userId
                            Role = m.Role
                            JoinedAt = m.JoinedAt
                        })
                    | Error _ -> []
            })
            // Bounded parallelism (cap = 32) keeps a 1000-blob List from
            // saturating the cloud backend's connection pool while still
            // collapsing 32x of the per-blob round-trip latency. Sequential
            // was visibly slow even on a 100-user platform.
            |> fun comps -> Async.Parallel(comps, maxDegreeOfParallelism = 32)

        return allMemberships |> Array.toList |> List.collect id
    }

    member this.GetMemberRole(teamId: string, userId: string) = async {
        let! memberships = this.LoadMemberships(userId)

        return memberships |> List.tryFind (fun m -> m.TeamId = teamId) |> Option.map _.Role
    }

    // ── Active team ──────────────────────────────────────────

    member _.GetActiveTeam(userId: string) = async {
        let! result = storage.Download(platformContainer, activeTeamBlobName userId)

        return
            match result with
            | Ok bytes ->
                let teamId = Encoding.UTF8.GetString(bytes).Trim()

                if String.IsNullOrEmpty(teamId) then None else Some teamId
            | Error _ -> None
    }

    member this.SetActiveTeam(userId: string, teamId: string) = async {
        let! memberships = this.LoadMemberships(userId)

        if memberships |> List.exists (fun m -> m.TeamId = teamId) then
            // Phase 863 — a refused write is the caller's failure, and
            // nothing is published for a change that did not happen.
            match! storage.Upload(platformContainer, activeTeamBlobName userId, Encoding.UTF8.GetBytes(teamId)) with
            | Ok _ ->
                do! publishChange teamId userId MembershipChangeKind.ActiveTeamSet
                return Ok()
            | Error storageError -> return Error(sprintf "The active team could not be saved: %s" storageError)
        else
            return Error "User is not a member of this team"
    }

    // ── Archive / purge (Platform-Admin team lifecycle) ──────

    /// Clear `userId`'s active-team pointer iff it currently points at
    /// `teamId`, then publish `MembershipChanged` so the resolver caches
    /// evict and the member drops to the no-active-team state. Shared by
    /// `SetArchived` (archive bump) and `PurgeTeam` (hard delete).
    /// Phase 971 — a refused delete answers `Error` naming the pointer
    /// still at rest (both callers have a `Result` to carry it), and
    /// nothing is published for a clear that did not happen.
    member private _.ClearActiveTeamIfMatches(userId: string, teamId: string) : Async<Result<unit, string>> = async {
        let! result = storage.Download(platformContainer, activeTeamBlobName userId)

        match result with
        | Ok bytes when Encoding.UTF8.GetString(bytes).Trim() = teamId ->
            match! storage.Delete(platformContainer, activeTeamBlobName userId) with
            | Ok() ->
                do! publishChange teamId userId MembershipChangeKind.ActiveTeamSet
                return Ok()
            | Error storageError ->
                return
                    Error(
                        sprintf
                            "user '%s': active-team pointer %s could not be cleared: %s"
                            userId
                            (activeTeamBlobName userId)
                            storageError
                    )
        | _ -> return Ok()
    }

    member this.SetArchived(teamId: string, archived: bool) = async {
        let! existing = this.GetTeam(teamId)

        match existing with
        | None -> return Error $"Team '{teamId}' not found"
        | Some team ->
            let updated = { team with Archived = archived }
            let! result = storage.Upload(platformContainer, teamBlobName teamId, Json.serializeTeam updated)

            match result with
            | Error e -> return Error e
            | Ok _ ->
                // On archive, bump every member whose active team is this
                // one — an archived team must not remain a member's active
                // scope. Restore (archived = false) leaves pointers alone;
                // the team is simply un-hidden and members re-select it.
                //
                // Phase 971 — every member is attempted and every refused
                // pointer clear collected; any refusal answers Error naming
                // the pointers still at rest. The team stays archived, and a
                // re-run of SetArchived(teamId, true) re-reads the members and
                // retries exactly those pointers (an idempotent re-upload of
                // the same record, then a no-op for every cleared pointer).
                if archived then
                    let! members = this.GetTeamMembers(teamId)

                    let! cleared =
                        members
                        |> List.map (fun m -> this.ClearActiveTeamIfMatches(m.UserId, teamId))
                        |> Async.Sequential

                    let failures =
                        cleared
                        |> Array.choose (function
                            | Ok() -> None
                            | Error e -> Some e)
                        |> Array.toList

                    match failures with
                    | [] -> return Ok()
                    | failures ->
                        return
                            Error(
                                sprintf
                                    "Team '%s' was archived, but %d member(s) still have it as their active team — %s"
                                    teamId
                                    failures.Length
                                    (String.concat "; " failures)
                            )
                else
                    return Ok()
    }

    member this.PurgeTeam(teamId: string) = async {
        let! existing = this.GetTeam(teamId)

        match existing with
        | None -> return Error $"Team '{teamId}' not found"
        | Some _ ->
            // Strip the team from every member's membership rows + clear
            // active-team pointers. Each per-user membership edit is a
            // guarded read-modify-write under the per-user lock, so a
            // concurrent AddMember/RemoveMember for the same user can't lose
            // the strip.
            let! members = this.GetTeamMembers(teamId)

            let! stripped =
                members
                |> List.map (fun m ->
                    withUserLock
                        m.UserId
                        (async {
                            // Phase 971 — the pointer goes BEFORE the row: the
                            // row is what lists this user as a member, so a
                            // refused pointer delete keeps it and the re-run
                            // (which re-reads the members) revisits the pointer.
                            match! this.ClearActiveTeamIfMatches(m.UserId, teamId) with
                            | Error e -> return Error e
                            | Ok() ->
                                let! outcome =
                                    updateMemberships m.UserId (fun current ->
                                        let remaining = current |> List.filter (fun x -> x.TeamId <> teamId)

                                        if remaining.Length <> current.Length then
                                            BlobUpdate.Write(remaining, ())
                                        else
                                            BlobUpdate.Keep())

                                match outcome with
                                | Error e -> return Error $"user '{m.UserId}': {e}"
                                | Ok() ->
                                    do! publishChange teamId m.UserId MembershipChangeKind.Removed
                                    return Ok()
                        }))
                |> Async.Sequential

            let failures =
                stripped
                |> Array.choose (function
                    | Ok() -> None
                    | Error e -> Some e)
                |> Array.toList

            match failures with
            | [] ->
                // Delete the team record last — once it's gone, GetTeam returns
                // None and the team is fully purged.
                return! storage.Delete(platformContainer, teamBlobName teamId)
            | failures ->
                // Fail closed: the team record stays, so the purge can be
                // re-run and finish once the membership blobs are readable and
                // every member's pointer could be cleared.
                return
                    Error(
                        sprintf
                            "Team '%s' was not purged: %d member(s) could not be detached — %s"
                            teamId
                            failures.Length
                            (String.concat "; " failures)
                    )
    }

    member this.PurgeUser(userId: string) = async {
        // Last-Owner safeguard: purging the sole Owner of a team would
        // leave it unmanageable — the operator must transfer ownership
        // or purge that team first. `IsLastOwner` is a cross-user scan,
        // so (like `RemoveMember`) it runs OUTSIDE the per-user lock;
        // the same accepted concurrent-Owner-removal race applies (a
        // Phase 9c concern requiring distributed primitives).
        let! memberships = this.LoadMemberships(userId)

        let! lastOwnerChecks =
            memberships
            |> List.map (fun m -> async {
                let! isLast = this.IsLastOwner(m.TeamId, userId)
                return if isLast then Some m.TeamId else None
            })
            |> Async.Sequential

        let blocking = lastOwnerChecks |> Array.choose id |> Array.toList

        if not (List.isEmpty blocking) then
            let teams = blocking |> List.map (sprintf "'%s'") |> String.concat ", "

            return
                Error(
                    sprintf
                        "Cannot purge user '%s' — they are the last Owner of team(s) %s. Transfer ownership or purge the team first."
                        userId
                        teams
                )
        else
            return!
                withUserLock
                    userId
                    (async {
                        // Re-read under the lock so a concurrent membership
                        // write for this user can't be lost by the purge.
                        let! current = this.LoadMemberships(userId)

                        // Phase 971 — a refused delete answers Error naming
                        // what is still at rest, and the membership record
                        // goes LAST: it is what a re-run (and the offboard
                        // hook's pre-purge team snapshot) finds the user's
                        // teams by, so it stays until the pointer went.
                        //
                        // Unconditional pointer delete — `IBlobStorage.Delete`
                        // is idempotent (Ok on a missing blob), which is what
                        // makes a re-purge of an already-purged user succeed.
                        match! storage.Delete(platformContainer, activeTeamBlobName userId) with
                        | Error storageError ->
                            return
                                Error(
                                    sprintf
                                        "User '%s' was not purged: the active-team pointer %s could not be deleted (%s); the membership record %s was left in place so a re-run re-finds it"
                                        userId
                                        (activeTeamBlobName userId)
                                        storageError
                                        (membershipBlobName userId)
                                )
                        | Ok() ->
                            let! membershipDeleted =
                                if current.IsEmpty then
                                    async { return Ok() }
                                else
                                    storage.Delete(platformContainer, membershipBlobName userId)

                            match membershipDeleted with
                            | Error storageError ->
                                return
                                    Error(
                                        sprintf
                                            "User '%s' was not purged: the membership record %s could not be deleted (%s)"
                                            userId
                                            (membershipBlobName userId)
                                            storageError
                                    )
                            | Ok() ->
                                for m in current do
                                    do! publishChange m.TeamId userId MembershipChangeKind.Removed

                                return Ok()
                    })
    }

    // ── ITeamStore routing ───────────────────────────────────
    // Public members above are the implementation; the interface
    // block delegates to them so consumers typed against the
    // interface see the same behaviour. Concrete tests keep calling
    // the public members directly.

    interface ITeamStore with
        member this.CreateTeam(teamId, name) = this.CreateTeam(teamId, name)
        member this.DeleteTeam(teamId) = this.DeleteTeam(teamId)
        member this.GetTeam(teamId) = this.GetTeam(teamId)
        member this.ListTeams() = this.ListTeams()
        member this.AddMember(teamId, userId, role) = this.AddMember(teamId, userId, role)
        member this.RemoveMember(teamId, userId) = this.RemoveMember(teamId, userId)

        member this.ChangeMemberRole(teamId, userId, newRole) =
            this.ChangeMemberRole(teamId, userId, newRole)

        member this.GetTeamsForUser(userId) = this.GetTeamsForUser(userId)
        member this.GetTeamMembers(teamId) = this.GetTeamMembers(teamId)
        member this.GetMemberRole(teamId, userId) = this.GetMemberRole(teamId, userId)
        member this.GetActiveTeam(userId) = this.GetActiveTeam(userId)
        member this.SetActiveTeam(userId, teamId) = this.SetActiveTeam(userId, teamId)
        member this.SetArchived(teamId, archived) = this.SetArchived(teamId, archived)
        member this.PurgeTeam(teamId) = this.PurgeTeam(teamId)
        member this.PurgeUser(userId) = this.PurgeUser(userId)

// ─── First-team-becomes-active policy ────────────────────────────

/// Onboarding policy applied after every successful membership write:
/// a user whose active-team pointer is unset gets it pointed at the
/// team they were just confirmed into. Without this, a member added
/// by an admin / invite link / pending-invite consumption resolves as
/// `AuthenticatedUser` (personal scope) on every request — they see
/// none of the team's data, and `GetAccessibleModules` returns an
/// empty module list for the no-active-team state. The team *creator*
/// never hit this because `CreateTeam` chains `SetActiveTeam` for the
/// caller; this module extends the same courtesy to everyone else.
///
/// Policy, not store primitive — `ITeamStore.AddMember` stays a pure
/// membership write so alternative store implementations don't have
/// to replicate onboarding behaviour; the SDK's add paths
/// (`TeamApi.AddTeamMember`, invite acceptance, pending-invite
/// consumption) call this after the membership is confirmed.
module ActiveTeamPolicy =

    /// Where a swallowed `ensureActiveTeam` failure is reported. The
    /// pointer write is best-effort (it must not fail the membership
    /// add), but a failure leaves a freshly-provisioned user in the
    /// no-active-team state where every `TenantScoped` route 401s
    /// `missing-tenant` — previously silent. The default emits one line
    /// to stderr so the condition is diagnosable; override at composition
    /// to route into structured logging. Mirrors
    /// `Cmd.OfRemoting.Interceptors.errorReporter`.
    let mutable errorReporter: string -> exn -> unit =
        fun userId ex ->
            try
                eprintfn
                    "[ActiveTeamPolicy.ensureActiveTeam] could not set an active team for user '%s' — they will land in the no-active-team state (TenantScoped routes 401 until they pick a team): %s"
                    userId
                    ex.Message
            with _ ->
                ()

    /// Point `userId`'s active team at `teamId` iff no active team is
    /// currently set. Never re-points an existing selection — a user
    /// who deliberately switched teams keeps their choice when they
    /// are later added to another team.
    ///
    /// Best-effort: the membership write this follows has already
    /// succeeded and is the durable fact; a pointer-write failure must
    /// not fail the add. On success `SetActiveTeam` publishes
    /// `MembershipChanged.ActiveTeamSet`, which both evicts the
    /// resolver-side active-team caches and live-switches any
    /// connected client via the `MembershipActiveTeamSet` push.
    let ensureActiveTeam (store: ITeamStore) (userId: string) (teamId: string) : Async<unit> = async {
        try
            let! current = store.GetActiveTeam userId

            match current with
            | Some _ -> ()
            | None ->
                let! _ = store.SetActiveTeam(userId, teamId)
                ()
        with ex ->
            // Best-effort: don't fail the membership add. But report it —
            // the user lands in the no-active-team state (recoverable via
            // the SetActiveTeam route / onboarding surface from the
            // client), and a silent failure here is exactly the
            // "TenantScoped 401s and nobody knows why" trap.
            errorReporter userId ex
    }