module ToolUp.Platform.Scope.PermissionStore

open System
open System.Text
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra

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
    /// they did before module RBAC. A document that exists but cannot be read
    /// or parsed raises `PermissionStoreUnavailableException` rather
    /// than degrading to the unrestricted empty map (fail closed on a
    /// storage blip / corrupt blob — never silently grant access).
    abstract GetTeamPermissions: teamId: TeamId -> Async<TeamPermissions>

    /// Replace the entire per-team document. Used for bulk admin
    /// operations — bootstrapping a new team, importing config from
    /// another team, or rolling back a bad set of grants.
    abstract SetTeamPermissions: teamId: TeamId * permissions: TeamPermissions -> Async<Result<unit, string>>

    /// Compute a user's effective per-module permissions for a team:
    /// their explicit `Members[userId]` entries merged with team
    /// `Defaults` on any module the user has no explicit entry for.
    /// Returned shape matches `AccessContext.ModulePermissions`.
    abstract GetEffectivePermissions: userId: UserId * teamId: TeamId -> Async<Map<string, ModulePermission list>>

    /// Set one member's permissions for one module. Pass an empty
    /// list to revoke all access for that user on that module; to
    /// fall the user back to team defaults instead, `SetTeamPermissions`
    /// with the member entry removed.
    abstract SetMemberPermissions:
        teamId: TeamId * userId: UserId * moduleName: string * permissions: ModulePermission list ->
            Async<Result<unit, string>>

    /// Replace the team's default permissions. Defaults apply to
    /// members who lack an explicit per-module entry.
    abstract SetTeamDefaults:
        teamId: TeamId * defaults: Map<string, ModulePermission list> -> Async<Result<unit, string>>

    /// The team's per-module exposure map (the per-team exposure axis).
    /// A module absent from the map is `Available`; entries hold the
    /// non-default `Hidden` / `Unavailable` states. Empty when no
    /// document exists or every module is Available. Read per-request by
    /// the scope-resolution middleware to populate
    /// `AccessContext.ModuleExposure`.
    abstract GetModuleExposure: teamId: TeamId -> Async<Map<string, ModuleExposure>>

    /// Set one module's exposure state for the team. `Available` clears
    /// the entry (back to the default); `Hidden` / `Unavailable` record
    /// it. Visibility/availability only — does not touch the permission
    /// maps.
    abstract SetModuleExposure:
        teamId: TeamId * moduleName: string * state: ModuleExposure -> Async<Result<unit, string>>

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

    /// The non-default exposure entries as a `module → token` object.
    /// `Available` is the default and is never serialised — absence ⇒
    /// Available on read.
    let private exposureToObject (exposure: Map<string, ModuleExposure>) =
        let dict = System.Collections.Generic.Dictionary<string, string>()

        for KeyValue(k, v) in exposure do
            if v <> ModuleExposure.Available then
                dict[k] <- ModuleExposure.toToken v

        dict

    /// Grant records as `userId → moduleName → record`.
    /// Absent entirely on a document that predates them, and omitted on write when
    /// no module declares a policy stricter than `AdminDiscretion`, so
    /// the serialised bytes of an unchanged deployment are unchanged.
    let private grantsToObject (grants: Map<string, Map<string, ModuleGrantRecord>>) =
        let outer =
            System.Collections.Generic.Dictionary<
                string,
                System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>
             >()

        for KeyValue(userId, byModule) in grants do
            let inner =
                System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>()

            for KeyValue(moduleName, record) in byModule do
                let fields = System.Collections.Generic.Dictionary<string, string>()
                fields["state"] <- GrantState.toToken record.State
                fields["policy"] <- GrantPolicy.toToken record.SatisfiedPolicy
                fields["justification"] <- record.Justification

                match record.ConsentedBy with
                | Some who -> fields["consentedBy"] <- who
                | None -> ()

                inner[moduleName] <- fields

            if inner.Count > 0 then
                outer[userId] <- inner

        outer

    let private grantsFromObject (obj: JsonElement) : Map<string, Map<string, ModuleGrantRecord>> =
        if obj.ValueKind <> JsonValueKind.Object then
            Map.empty
        else
            [
                for userProp in obj.EnumerateObject() do
                    if userProp.Value.ValueKind = JsonValueKind.Object then
                        let byModule = [
                            for moduleProp in userProp.Value.EnumerateObject() do
                                if moduleProp.Value.ValueKind = JsonValueKind.Object then
                                    let field name =
                                        match moduleProp.Value.TryGetProperty(name: string) with
                                        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                                        | _ -> None

                                    moduleProp.Name,
                                    {
                                        // Both parses are fail-closed: an
                                        // unreadable state is inert and an
                                        // unreadable policy token never
                                        // reads as AdminDiscretion.
                                        State = field "state" |> Option.defaultValue "" |> GrantState.ofToken
                                        SatisfiedPolicy =
                                            field "policy" |> Option.defaultValue "" |> GrantPolicy.ofToken
                                        Justification = field "justification" |> Option.defaultValue ""
                                        ConsentedBy = field "consentedBy"
                                    }
                        ]

                        if not (List.isEmpty byModule) then
                            userProp.Name, Map.ofList byModule
            ]
            |> Map.ofList

    /// Page-save grants as `userId → [token]`, members holding none omitted.
    let private pagesToObject (pages: Map<string, PageGrant list>) =
        let dict = System.Collections.Generic.Dictionary<string, string[]>()

        for KeyValue(userId, grants) in pages do
            if not (List.isEmpty grants) then
                dict[userId] <- grants |> List.map PageGrant.toToken |> List.distinct |> List.toArray

        dict

    /// The inverse of `pagesToObject`. An unknown token is dropped, never
    /// read as a grant; a member left with none is absent.
    let private pagesFromObject (obj: JsonElement) : Map<string, PageGrant list> =
        if obj.ValueKind <> JsonValueKind.Object then
            Map.empty
        else
            [
                for prop in obj.EnumerateObject() do
                    if prop.Value.ValueKind = JsonValueKind.Array then
                        let grants = [
                            for elem in prop.Value.EnumerateArray() do
                                if elem.ValueKind = JsonValueKind.String then
                                    match PageGrant.ofToken (elem.GetString()) with
                                    | Some grant -> grant
                                    | None -> ()
                        ]

                        if not (List.isEmpty grants) then
                            prop.Name, List.distinct grants
            ]
            |> Map.ofList

    let serialize (perms: TeamPermissions) : byte[] =
        let membersDict =
            System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string[]>>()

        for KeyValue(userId, modulesMap) in perms.Members do
            membersDict[userId] <- modulesToObject modulesMap

        let dto = {|
            defaults = modulesToObject perms.Defaults
            members = membersDict
            // Tri-state exposure map (Available omitted).
            exposure = exposureToObject perms.Exposure
            // Dual-write the legacy `hidden` array (= every non-Available
            // module, both Hidden and Unavailable) so a pre-tri-state
            // reader still keeps them off the sidebar. The `exposure` map
            // wins on read; `hidden` is the back-compat fallback only.
            hidden =
                perms.Exposure
                |> Map.toArray
                |> Array.choose (fun (k, v) -> if ModuleExposure.isExposed v then None else Some k)
            // Grant-policy evidence. Empty on every document that predates it
            // document and on every deployment where no module declares a
            // policy, in which case the property serialises as `{}`.
            grants = grantsToObject perms.Grants
        |}

        // Page-save grants ride as `pages`, written only when a member holds
        // one: a document with none serialises exactly as it did before the
        // field existed, so its bytes do not move (GP 11).
        let pages = if isNull (box perms.Pages) then Map.empty else perms.Pages

        if Map.isEmpty pages then
            JsonSerializer.Serialize(dto, options) |> Encoding.UTF8.GetBytes
        else
            let node = JsonSerializer.SerializeToNode(dto, options).AsObject()
            node["pages"] <- JsonSerializer.SerializeToNode(pagesToObject pages, options)
            node.ToJsonString options |> Encoding.UTF8.GetBytes

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

            // Tri-state exposure map. Prefer the `exposure` object when
            // present; entries that parse back to `Available` are dropped
            // (absence ⇒ Available). Fall back to the legacy `hidden`
            // string[] (pre-tri-state documents) by mapping each hidden
            // module to the cosmetic `Hidden` state — this preserves the
            // legacy "Expose in team" off behaviour (off the sidebar,
            // data still mappable). Both absent ⇒ empty ⇒ all Available.
            let exposure =
                match root.TryGetProperty "exposure" with
                | true, e when e.ValueKind = JsonValueKind.Object ->
                    [
                        for prop in e.EnumerateObject() do
                            if prop.Value.ValueKind = JsonValueKind.String then
                                let state = ModuleExposure.ofToken (prop.Value.GetString())

                                if state <> ModuleExposure.Available then
                                    prop.Name, state
                    ]
                    |> Map.ofList
                | _ ->
                    match root.TryGetProperty "hidden" with
                    | true, h when h.ValueKind = JsonValueKind.Array ->
                        [
                            for elem in h.EnumerateArray() do
                                if elem.ValueKind = JsonValueKind.String then
                                    elem.GetString(), ModuleExposure.Hidden
                        ]
                        |> Map.ofList
                    | _ -> Map.empty

            // Grant records. Absent on a document that predates them ⇒
            // empty ⇒ every module behaves as `AdminDiscretion` unless one
            // declares otherwise (GP 11).
            let grants =
                match root.TryGetProperty "grants" with
                | true, g -> grantsFromObject g
                | _ -> Map.empty

            // Page-save grants. Absent ⇒ empty ⇒ nothing beyond what a role
            // implies (deny by default).
            let pages =
                match root.TryGetProperty "pages" with
                | true, p -> pagesFromObject p
                | _ -> Map.empty

            Some {
                Defaults = defaults
                Members = members
                Exposure = exposure
                Grants = grants
                Pages = pages
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

let private platformContainer = ScopeKey.PlatformContainer
let private teamBlobName teamId = $"permissions/{teamId}.json"

/// Blob-backed `IPermissionStore`: one JSON document per team under
/// `_platform/permissions/{teamId}.json`, a codec and a path over the
/// platform's durable document store (`BlobDocumentStore`). An absent
/// document is the bootstrap default (no restrictions); a document that
/// exists but cannot be read, or does not decode (it is quarantined beside
/// itself and never overwritten), raises `PermissionStoreUnavailableException`
/// so the request fails closed rather than reading a corrupt RBAC document as
/// unrestricted. Every write is one guarded read-modify-write, conditional
/// where the backend has the ETag seam, so two concurrent permission edits on
/// different nodes do not lose one another. No internal caching.
type PermissionStore(storage: IBlobStorage, ?logger: ILogger) =
    let logError (msg: string) =
        match logger with
        | Some l -> l.Error(msg, None)
        | None -> ()

    // The document's own codec: it keeps the `hidden` dual-write.
    let codec: BlobCodec<TeamPermissions> = {
        Encode = Json.serialize
        Decode =
            fun bytes ->
                match Json.tryDeserialize bytes with
                | Some perms -> Ok perms
                | None -> Error "the permission document does not parse"
    }

    let docs =
        BlobDocumentStore<TeamPermissions>(
            storage,
            codec,
            defaultArg logger (Config.ConsoleLogger.ConsoleLogger() :> ILogger)
        )

    let teamPath (teamId: string) = DocPath.ofString (teamBlobName teamId)

    let unavailable (teamId: string) (error: BlobMapStoreError) =
        logError
            $"PermissionStore: team '{teamId}': {BlobMapStoreError.describe error}. Failing closed (request denied) rather than treating it as unrestricted access."

        PermissionStoreUnavailableException
            $"Permission document for team '{teamId}' is unavailable: {BlobMapStoreError.describe error}"

    let load (teamId: string) = async {
        match! docs.Read(ScopeKey.platform, teamPath teamId) with
        | Ok(Some perms) -> return perms
        | Ok None -> return TeamPermissions.empty
        | Error e -> return raise (unavailable teamId e)
    }

    /// One guarded read-modify-write of the team's document. A document that
    /// cannot be established refuses the write (`PermissionStoreUnavailableException`,
    /// as a read does); a write the storage refuses is `Error`.
    let change (teamId: string) (transform: TeamPermissions -> TeamPermissions) = async {
        let! outcome =
            docs.Update(
                ScopeKey.platform,
                teamPath teamId,
                fun current -> BlobUpdate.Write(current |> Option.defaultValue TeamPermissions.empty |> transform, ())
            )

        match outcome with
        | Ok() -> return Ok()
        | Error(BlobMapStoreError.WriteFailed(_, reason)) -> return Error reason
        | Error(BlobMapStoreError.Contended _ as e) -> return Error(BlobMapStoreError.describe e)
        | Error e -> return raise (unavailable teamId e)
    }

    interface IPermissionStore with
        member _.GetTeamPermissions teamId = load teamId.Value

        member _.SetTeamPermissions(teamId, permissions) =
            change teamId.Value (fun _ -> permissions)

        member _.GetEffectivePermissions(userId, teamId) = async {
            let! perms = load teamId.Value
            return mergeEffective perms.Defaults perms.Members userId.Value
        }

        member _.SetMemberPermissions(teamId, userId, moduleName, permissions) =
            change teamId.Value (fun existing ->
                let userEntries =
                    existing.Members |> Map.tryFind userId.Value |> Option.defaultValue Map.empty

                let updatedUserEntries =
                    if List.isEmpty permissions then
                        userEntries |> Map.remove moduleName
                    else
                        userEntries |> Map.add moduleName permissions

                let updatedMembers =
                    if Map.isEmpty updatedUserEntries then
                        existing.Members |> Map.remove userId.Value
                    else
                        existing.Members |> Map.add userId.Value updatedUserEntries

                {
                    existing with
                        Members = updatedMembers
                })

        member _.SetTeamDefaults(teamId, defaults) =
            change teamId.Value (fun existing -> { existing with Defaults = defaults })

        member _.GetModuleExposure teamId = async {
            let! perms = load teamId.Value
            return perms.Exposure
        }

        member _.SetModuleExposure(teamId, moduleName, state) =
            change teamId.Value (fun existing ->
                let updatedExposure =
                    match state with
                    | ModuleExposure.Available -> existing.Exposure |> Map.remove moduleName
                    | s -> existing.Exposure |> Map.add moduleName s

                {
                    existing with
                        Exposure = updatedExposure
                })