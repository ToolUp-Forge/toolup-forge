module ToolUp.Platform.PermissionStore

open System
open System.Text
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

/// Raised when a team's permission document is present but cannot be
/// read or parsed. Resolving permissions to `TeamPermissions.empty`
/// (= unrestricted) on a storage blip or a corrupt blob is fail-OPEN
/// on an authorization primitive — a single transient error would
/// silently grant every member full access. The store raises this
/// instead so the request fails (access denied) rather than escalating.
/// "No document at all" is NOT this case — that stays fail-open by
/// design (bootstrap: a brand-new team behaves as pre-Phase-4
/// unrestricted until an admin writes its first grants).
exception PermissionStoreUnavailableException of string

// ─── Interface ───────────────────────────────────────────────────────

/// Team-scoped permission store. Persists per-team permission documents
/// and resolves effective permissions for individual users by merging
/// their explicit overrides with team-wide defaults.
type IPermissionStore =
    /// Fetch the raw per-team document. Returns `TeamPermissions.empty`
    /// ONLY when no document exists — `canAccessModule` treats an empty
    /// map as unrestricted, so teams without explicit config behave as
    /// they did pre-Phase 4. A document that exists but cannot be read
    /// or parsed raises `PermissionStoreUnavailableException` rather
    /// than degrading to the unrestricted empty map (fail closed on a
    /// storage blip / corrupt blob — never silently grant access).
    abstract GetTeamPermissions: teamId: string -> Async<TeamPermissions>

    /// Replace the entire per-team document. Used for bulk admin
    /// operations — bootstrapping a new team, importing config from
    /// another team, or rolling back a bad set of grants.
    abstract SetTeamPermissions: teamId: string * permissions: TeamPermissions -> Async<Result<unit, string>>

    /// Compute a user's effective per-module permissions for a team:
    /// their explicit `Members[userId]` entries merged with team
    /// `Defaults` on any module the user has no explicit entry for.
    /// Returned shape matches `AccessContext.ModulePermissions`.
    abstract GetEffectivePermissions: userId: string * teamId: string -> Async<Map<string, ModulePermission list>>

    /// Set one member's permissions for one module. Pass an empty
    /// list to revoke all access for that user on that module; to
    /// fall the user back to team defaults instead, `SetTeamPermissions`
    /// with the member entry removed.
    abstract SetMemberPermissions:
        teamId: string * userId: string * moduleName: string * permissions: ModulePermission list ->
            Async<Result<unit, string>>

    /// Replace the team's default permissions. Defaults apply to
    /// members who lack an explicit per-module entry.
    abstract SetTeamDefaults:
        teamId: string * defaults: Map<string, ModulePermission list> -> Async<Result<unit, string>>

// ─── JSON serialisation ──────────────────────────────────────────────

module private Json =
    let private options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let private permissionToString =
        function
        | ModulePermission.Read -> "Read"
        | ModulePermission.Write -> "Write"
        | ModulePermission.Admin -> "Admin"
        | ModulePermission.SchemaOnly -> "SchemaOnly"

    let private stringToPermission =
        function
        | "Read" -> Some ModulePermission.Read
        | "Write" -> Some ModulePermission.Write
        | "Admin" -> Some ModulePermission.Admin
        | "SchemaOnly" -> Some ModulePermission.SchemaOnly
        | _ -> None

    let private permsToStrings (perms: ModulePermission list) =
        perms |> List.map permissionToString |> List.toArray

    let private stringsToPerms (strs: JsonElement) = [
        for elem in strs.EnumerateArray() do
            if elem.ValueKind = JsonValueKind.String then
                match stringToPermission (elem.GetString()) with
                | Some p -> p
                | None -> ()
    ]

    let private modulesFromObject (obj: JsonElement) : Map<string, ModulePermission list> =
        if obj.ValueKind <> JsonValueKind.Object then
            Map.empty
        else
            [
                for prop in obj.EnumerateObject() do
                    if prop.Value.ValueKind = JsonValueKind.Array then
                        prop.Name, stringsToPerms prop.Value
            ]
            |> Map.ofList

    let private modulesToObject (modules: Map<string, ModulePermission list>) =
        let dict = System.Collections.Generic.Dictionary<string, string[]>()

        for KeyValue(k, v) in modules do
            dict[k] <- permsToStrings v

        dict

    let serialize (perms: TeamPermissions) : byte[] =
        let membersDict =
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string[]>>()

        for KeyValue(userId, modulesMap) in perms.Members do
            membersDict[userId] <- modulesToObject modulesMap

        let dto = {|
            defaults = modulesToObject perms.Defaults
            members = membersDict
        |}

        JsonSerializer.Serialize(dto, options) |> Encoding.UTF8.GetBytes

    /// Non-swallowing parse. `None` means the bytes are present but
    /// not a valid permission document — the caller decides whether
    /// that is fail-open (absent) or fail-closed (corrupt). The old
    /// swallow-to-empty behaviour silently turned a corrupt RBAC
    /// document into "unrestricted"; callers must not do that.
    let tryDeserialize (bytes: byte[]) : TeamPermissions option =
        try
            let doc = JsonDocument.Parse(Encoding.UTF8.GetString bytes)
            let root = doc.RootElement

            let defaults =
                match root.TryGetProperty "defaults" with
                | true, d -> modulesFromObject d
                | _ -> Map.empty

            let members =
                match root.TryGetProperty "members" with
                | true, m when m.ValueKind = JsonValueKind.Object ->
                    [
                        for prop in m.EnumerateObject() do
                            prop.Name, modulesFromObject prop.Value
                    ]
                    |> Map.ofList
                | _ -> Map.empty

            Some {
                Defaults = defaults
                Members = members
            }
        with _ ->
            None

// ─── Effective-permission merge ──────────────────────────────────────

/// Merge a user's explicit entries with the team defaults. Per-module,
/// the user's explicit grant wins if present; otherwise the default
/// applies. Modules with neither entry are absent from the result
/// (= no access).
let private mergeEffective
    (defaults: Map<string, ModulePermission list>)
    (userOverrides: Map<string, Map<string, ModulePermission list>>)
    (userId: string)
    : Map<string, ModulePermission list> =

    let userEntries =
        userOverrides |> Map.tryFind userId |> Option.defaultValue Map.empty

    // Start from defaults, overlay the user's explicit entries.
    (defaults, userEntries)
    ||> Map.fold (fun acc moduleName perms -> Map.add moduleName perms acc)

// ─── Blob-backed implementation ──────────────────────────────────────

let private platformContainer = "_platform"
let private teamBlobName teamId = $"permissions/{teamId}.json"

/// Blob-backed `IPermissionStore`. Reads and writes one JSON
/// document per team under `_platform/permissions/{teamId}.json`.
/// Thread-safe via the underlying `IBlobStorage` — no internal
/// caching; callers that care about latency wrap this with a
/// caching layer (or we add one here if production telemetry shows
/// it's needed).
type PermissionStore(storage: IBlobStorage, ?logger: ILogger) =
    let logError (msg: string) =
        match logger with
        | Some l -> l.Error(msg, None)
        | None -> ()

    let load (teamId: string) = async {
        let blobName = teamBlobName teamId
        let! result = storage.Download(platformContainer, blobName)

        match result with
        | Ok bytes ->
            match Json.tryDeserialize bytes with
            | Some perms -> return perms
            | None ->
                // Present but unparseable. Failing OPEN here treats a
                // corrupt RBAC document as unrestricted — fail closed.
                logError
                    $"PermissionStore: team '{teamId}' permission document is present but unparseable. Failing closed (request denied) rather than treating it as unrestricted access."

                return
                    raise (
                        PermissionStoreUnavailableException
                            $"Permission document for team '{teamId}' is corrupt and cannot be parsed."
                    )
        | Error downloadErr ->
            // `Error` is ambiguous: blob genuinely absent (intended
            // bootstrap fail-open — pre-Phase-4 unrestricted) vs a
            // transient/permission read failure (must fail closed; a
            // storage blip must not silently grant unrestricted access).
            let! existence = async {
                try
                    let! e = storage.Exists(platformContainer, blobName)
                    return Some e
                with _ ->
                    return None
            }

            match existence with
            | Some false ->
                // Genuinely no document — bootstrap default.
                return TeamPermissions.empty
            | Some true ->
                logError
                    $"PermissionStore: team '{teamId}' permission document exists but could not be read ({downloadErr}). Failing closed (request denied) rather than treating it as unrestricted access."

                return
                    raise (
                        PermissionStoreUnavailableException
                            $"Permission document for team '{teamId}' exists but could not be read: {downloadErr}"
                    )
            | None ->
                // Could not even confirm existence — storage unreachable.
                // Cannot prove the document is absent, so fail closed.
                logError
                    $"PermissionStore: storage unreachable while resolving permissions for team '{teamId}' (download error: {downloadErr}). Failing closed (request denied)."

                return
                    raise (
                        PermissionStoreUnavailableException
                            $"Storage unreachable while resolving permissions for team '{teamId}'."
                    )
    }

    let save (teamId: string) (perms: TeamPermissions) = async {
        let bytes = Json.serialize perms
        let! result = storage.Upload(platformContainer, teamBlobName teamId, bytes)

        match result with
        | Ok _ -> return Ok()
        | Error e -> return Error e
    }

    interface IPermissionStore with
        member _.GetTeamPermissions teamId = load teamId

        member _.SetTeamPermissions(teamId, permissions) = save teamId permissions

        member _.GetEffectivePermissions(userId, teamId) = async {
            let! perms = load teamId
            return mergeEffective perms.Defaults perms.Members userId
        }

        member _.SetMemberPermissions(teamId, userId, moduleName, permissions) = async {
            let! existing = load teamId

            let userEntries =
                existing.Members |> Map.tryFind userId |> Option.defaultValue Map.empty

            let updatedUserEntries =
                if List.isEmpty permissions then
                    userEntries |> Map.remove moduleName
                else
                    userEntries |> Map.add moduleName permissions

            let updatedMembers =
                if Map.isEmpty updatedUserEntries then
                    existing.Members |> Map.remove userId
                else
                    existing.Members |> Map.add userId updatedUserEntries

            return!
                save teamId {
                    existing with
                        Members = updatedMembers
                }
        }

        member _.SetTeamDefaults(teamId, defaults) = async {
            let! existing = load teamId
            return! save teamId { existing with Defaults = defaults }
        }