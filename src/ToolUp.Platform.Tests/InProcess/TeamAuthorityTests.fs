module ToolUp.Platform.Tests.InProcess.TeamAuthorityTests

open System
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Config.TeamManagement
open ToolUp.Platform.Scope
open ToolUp.Platform.Scope.PermissionStore
open ToolUp.Platform.Scope.TeamAuthority
open ToolUp.Platform.Scope.AdminMutationApproval

// ─── The one team-authority gate, proved equivalent to the six it replaced ──
//
// Six handlers each decided "may this caller act on this team" by hand. The
// table below states each site's verdict as that site's own code computed
// it (the `before` column: its bypass, then a `GetMemberRole` read, then its
// predicate) and asserts `TeamAuthority.require` reaches the same verdict for
// every role a caller can hold, platform admin or not. The second list pins
// that the gate reads the team store exactly once per decision — and not at
// all for a platform admin at a bypassing gate — so no site re-reads it.

/// A team store answering one caller's role, counting its role reads.
type private CountingTeamStore(role: TeamRole option) =
    let mutable reads = 0
    member _.Reads = reads

    interface ITeamStore with
        member _.CreateTeam(_, _) = async { return Error "unused" }
        member _.DeleteTeam _ = async { return Error "unused" }
        member _.GetTeam _ = async { return None }
        member _.ListTeams() = async { return [] }
        member _.AddMember(_, _, _) = async { return Error "unused" }
        member _.RemoveMember(_, _) = async { return Error "unused" }
        member _.ChangeMemberRole(_, _, _) = async { return Error "unused" }
        member _.GetTeamsForUser _ = async { return [] }
        member _.GetTeamMembers _ = async { return [] }

        member _.GetMemberRole(_, _) = async {
            Interlocked.Increment(&reads) |> ignore
            return role
        }

        member _.GetActiveTeam _ = async { return None }
        member _.SetActiveTeam(_, _) = async { return Error "unused" }
        member _.SetArchived(_, _) = async { return Error "unused" }
        member _.PurgeTeam _ = async { return Error "unused" }
        member _.PurgeUser _ = async { return Error "unused" }

let private team = TeamId "acme"

let private principal (platformAdmin: bool) : AccessContext = {
    AccessContext.ofSubject ModulePermissions.Unrestricted (Subject.TeamMember("alice", "acme")) with
        PlatformRole =
            if platformAdmin then
                Some PlatformRole.PlatformAdmin
            else
                None
}

/// A site, as its own code decided: whether a platform admin was the team's
/// Owner there, and the predicate it applied to the role it read.
type private Site = {
    Name: string
    Bypass: AdminBypass
    Permits: TeamRole -> bool
}

let private sites: Site list = [
    // PlatformApiHandler.authoriseManageRole over effectiveCallerRole: a
    // platform admin is Owner, then `canManageRole caller target`.
    for target in [ Owner; Admin; Member ] do
        {
            Name = $"team membership (manage a {TeamRoles.displayName target})"
            Bypass = AdminBypass.PlatformAdminIsOwner
            Permits = fun r -> TeamRoles.canManageRole r target
        }
    // permissionApiHandler's four inline checks: membership, canManageMembers.
    {
        Name = "team permissions"
        Bypass = AdminBypass.MembershipOnly
        Permits = TeamRoles.canManageMembers
    }
    // TeamInvitationHandler.ensureManageMembers.
    {
        Name = "invitations"
        Bypass = AdminBypass.MembershipOnly
        Permits = TeamRoles.canManageMembers
    }
    // ServiceAccountApiHandler.authorised's role step.
    {
        Name = "service accounts"
        Bypass = AdminBypass.MembershipOnly
        Permits = TeamRoles.canWriteTeamConfig
    }
    // FileManagement.ensureCanResetDataStore.
    {
        Name = "data-store reset"
        Bypass = AdminBypass.MembershipOnly
        Permits = TeamRoles.canWriteTeamConfig
    }
    // TeamPolicyViewer.resolve's role step: the raw membership role.
    {
        Name = "team policy viewer"
        Bypass = AdminBypass.MembershipOnly
        Permits = fun _ -> true
    }
]

/// The site's verdict as it was written before the gate existed.
let private before (site: Site) (platformAdmin: bool) (memberRole: TeamRole option) : Result<TeamRole, unit> =
    let effective =
        match site.Bypass with
        | AdminBypass.PlatformAdminIsOwner when platformAdmin -> Some Owner
        | _ -> memberRole

    match effective with
    | Some r when site.Permits r -> Ok r
    | _ -> Error()

let private equivalence =
    testList "TeamAuthority.require — the six sites' verdicts" [
        for site in sites do
            for platformAdmin in [ false; true ] do
                for memberRole in [ None; Some Member; Some Admin; Some Owner ] do
                    testCaseAsync $"{site.Name}: platform admin {platformAdmin}, role {memberRole}"
                    <| async {
                        let store = CountingTeamStore(memberRole)
                        let! verdict = require store site.Bypass (principal platformAdmin) team site.Permits
                        let expected = before site platformAdmin memberRole

                        match verdict, expected with
                        | Ok role, Ok expectedRole -> Expect.equal role expectedRole "the same effective role"
                        | Error(AuthorityRefusal.NotAMember t), Error() ->
                            Expect.equal t team "names the team"
                            Expect.isNone memberRole "refused as a non-member only when the caller holds no role"
                        | Error(AuthorityRefusal.InsufficientRole(_, role)), Error() ->
                            Expect.equal (Some role) memberRole "refused naming the role the caller holds"
                        | v, e -> failtestf "the gate and the site disagree: gate %A, site %A" v e
                    }
    ]

let private oneRead =
    testList "TeamAuthority.require — one role read per decision" [
        for site in sites do
            testCaseAsync $"{site.Name}: a member's decision reads GetMemberRole once"
            <| async {
                let store = CountingTeamStore(Some Admin)
                let! _ = require store site.Bypass (principal false) team site.Permits
                Expect.equal store.Reads 1 "one read"
            }

        testCaseAsync "a platform admin at a bypassing gate reads no role"
        <| async {
            let store = CountingTeamStore None

            let! verdict =
                require store AdminBypass.PlatformAdminIsOwner (principal true) team (fun r ->
                    TeamRoles.canManageRole r Admin)

            Expect.equal verdict (Ok Owner) "Owner by the bypass"
            Expect.equal store.Reads 0 "the platform role is the principal's"
        }

        test "describe keeps the sites' wording" {
            Expect.equal
                (describe "owners and admins" "manage invitations" (AuthorityRefusal.InsufficientRole(team, Member)))
                $"Only team owners and admins can manage invitations. Your role: {TeamRoles.displayName Member}."
                "insufficient role"

            Expect.equal
                (describe "owners and admins" "manage invitations" (AuthorityRefusal.NotAMember team))
                "You are not a member of this team."
                "non-member"
        }
    ]

// ─── PermissionWrites.asPrincipal — the write chain's order is a function ──

/// A store accepting every write, recording what reached it.
type private RecordingPermissionStore() =
    let mutable writes = 0
    member _.Writes = writes

    interface IPermissionStore with
        member _.GetTeamPermissions _ = async { return Ok TeamPermissions.empty }
        member _.SetTeamPermissions(_, _) = async { return Ok() }

        member _.UpdateTeamPermissions(_, change) = async {
            match change TeamPermissions.empty with
            | Ok _ ->
                Interlocked.Increment(&writes) |> ignore
                return Ok()
            | Error e -> return Error e
        }

        member _.GetEffectivePermissions(_, _) = async { return Ok ModulePermissions.Unrestricted }

        member _.SetMemberPermissions(_, _, _, _) = async {
            Interlocked.Increment(&writes) |> ignore
            return Ok()
        }

        member _.SetTeamDefaults(_, _) = async {
            Interlocked.Increment(&writes) |> ignore
            return Ok()
        }

        member _.GetModuleExposure _ = async { return Ok Map.empty }
        member _.SetModuleExposure(_, _, _) = async { return Ok() }

/// An approval queue that records every proposal it is handed.
type private RecordingApprovals() =
    let mutable proposals = 0
    member _.Proposals = proposals

    interface IAdminMutationApprovalStore with
        member _.Propose _ = async {
            Interlocked.Increment(&proposals) |> ignore
            return Ok()
        }

        member _.TryGet(_, _) = async { return Ok None }
        member _.List _ = async { return Ok [] }
        member _.Remove(_, _) = async { return Ok() }

let private writesTests =
    testList "PermissionWrites.asPrincipal — grant policy outside dual control" [
        testCaseAsync "a write the module's grant policy refuses is refused, never parked for approval"
        <| async {
            let store = RecordingPermissionStore()
            let approvals = RecordingApprovals()

            let writes: PermissionWrites = {
                Store = store
                DualControl =
                    Some {
                        Settings = DualControlSettings.defaults
                        Approvals = approvals
                        IsPolicyBearing = fun _ -> true
                        Now = fun () -> DateTimeOffset.UtcNow
                        AuditLog = NoOpAuditLog()
                    }
                GrantPolicy =
                    Some {
                        Registry =
                            GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations [
                                "Partner", GrantPolicy.RequiresAcknowledgement
                            ]
                        Consent = GrantPolicyGuard.CounterpartyConsentOracle.denyAll
                        AuditLog = NoOpAuditLog()
                    }
                Observe = None
            }

            let admin = {
                AccessContext.ofSubject ModulePermissions.Unrestricted (Subject.TeamMember("admin", "t1")) with
                    PlatformRole = None
            }

            let chain = PermissionWrites.asPrincipal admin writes

            match! chain.SetMemberPermissions(TeamId "t1", UserId "alice", "Partner", [ ModulePermission.Read ]) with
            | Ok() -> failtest "an unacknowledged grant on a policy-bearing module must refuse"
            | Error msg ->
                Expect.stringContains msg "GRANT-POLICY" "refused by the grant policy"
                Expect.equal approvals.Proposals 0 "nothing parked: grant policy ran first"
                Expect.equal store.Writes 0 "nothing written"
        }

        testCaseAsync "a principal naming no person proposes nothing under dual control"
        <| async {
            let store = RecordingPermissionStore()
            let approvals = RecordingApprovals()

            let writes: PermissionWrites = {
                PermissionWrites.direct store with
                    DualControl =
                        Some {
                            Settings = DualControlSettings.defaults
                            Approvals = approvals
                            IsPolicyBearing = fun _ -> true
                            Now = fun () -> DateTimeOffset.UtcNow
                            AuditLog = NoOpAuditLog()
                        }
            }

            let chain = PermissionWrites.asPrincipal (AccessContext.system "test") writes

            match! chain.SetMemberPermissions(TeamId "t1", UserId "alice", "Partner", [ ModulePermission.Read ]) with
            | Ok() -> failtest "an unattributed gated write must refuse"
            | Error _ ->
                Expect.equal approvals.Proposals 0 "an unattributable proposal is refused, never parked"
                Expect.equal store.Writes 0 "nothing written"
        }
    ]

let tests = testList "TeamAuthority" [ equivalence; oneRead; writesTests ]