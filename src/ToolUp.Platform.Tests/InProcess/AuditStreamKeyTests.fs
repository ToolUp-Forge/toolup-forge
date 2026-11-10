module ToolUp.Platform.Tests.InProcess.AuditStreamKeyTests

// Phase 1059 — one team's audit events land in ONE stream, keyed by the bare
// team id.
//
// The event store keys a stream on the `scopeId` passed to
// `IAuditLog.Record` verbatim. The request middleware's login audit, the
// platform admin API and file management write a team's rows under the BARE
// id (`scope.ScopeId` / `teamId`) — that is the key every row already at rest
// carries. The writers below used `team-{teamId}` or the storage
// `Container`, so the same team's trail was split across streams, and a
// reader of one key never saw rows written under the other.
//
// Each writer is driven through its own production entry point against a
// recording audit log; the assertion is the key it chose.

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Scope.PermissionStore
open ToolUp.Platform.Config.TeamManagement
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Scope
open ToolUp.Platform.Scope.Teams
open ToolUp.Platform.Audit
open ToolUp.Platform.Testing

let private teamId = "acme"

/// The key the request middleware (`scope.ScopeId`) and
/// `PlatformApiHandler` (`teamId`) record this team's rows under —
/// Phase 1060: the typed rendering, `AuditScope.streamKey`, of the team's
/// `ScopeKey`. Its bytes are pinned below against the literal at rest.
let private bareKey =
    AuditScope.streamKey (AuditScope.Scoped(ScopeKey.Team(TeamId teamId)))

/// The distinct stream keys the log was asked to record under.
let private keys (audit: TestAuditLog) =
    audit.Records |> List.map _.StreamKey |> Set.ofList

/// Wait for a fire-and-forget emission (the grant guard's) to land.
let private settle (audit: TestAuditLog) (expected: int) = async {
    audit.Recording.WaitUntil((fun rows -> List.length rows >= expected), TimeSpan.FromSeconds 2.0)
    |> ignore
}

let private grantGuardKey () = async {
    let audit = TestAuditLog()

    let registry =
        GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations [ "Partner", GrantPolicy.RequiresAcknowledgement ]

    let store =
        GrantPolicyGuard.GrantPolicyPermissionStore(PermissionStore(InMemoryBlobStorage()), registry, audit, "admin")
        :> IPermissionStore

    let! _ = store.SetMemberPermissions(TeamId teamId, UserId "alice", "Partner", [ ModulePermission.Read ])
    do! settle audit 1
    return keys audit
}

let private expiryHookKey () = async {
    let audit = TestAuditLog()

    let hook =
        ToolUp.Platform.Scope.Teams.PendingInviteExpiryHook.build Logger.silent ((audit :> IAuditLog)) None

    do!
        hook [
            "late@example.test",
            {
                TeamId = teamId
                Role = Member
                ExpiresAt = DateTime.UtcNow.AddDays -1.0
                InviterUserId = "alice"
                IssuedAt = DateTime.UtcNow.AddDays -8.0
            }
        ]

    return keys audit
}

let private classificationGateKey () = async {
    let audit = TestAuditLog()

    let classifier =
        { new IFieldClassifier with
            member _.Classify entity = async {
                return [
                    {
                        EntityName = entity
                        FieldPath = "salary"
                        Level = ClassificationLevel.Financial
                        AuditOnRead = true
                        EncryptionKeyId = None
                    }
                ]
            }

            member _.LookupBySubject _ = async { return [] }
            member _.IsClassified(_, _) = async { return true }
        }

    let ctx = AccessContexts.forSubject (TeamMember("alice", teamId))
    let! _ = ClassificationGate.recordWrites classifier audit ctx "Employee" [ "salary" ]
    return keys audit
}

let tests =
    testList "Phase 1059 — one team's audit trail is one stream" [

        for name, keyOf in
            [
                "GrantPolicyGuard", grantGuardKey
                "PendingInviteExpiryHook", expiryHookKey
                "ClassificationGate", classificationGateKey
            ] do
            testCaseAsync (sprintf "%s records a team's row under the bare team id" name)
            <| async {
                let! keys = keyOf ()
                Expect.equal keys (Set.singleton bareKey) "the stream key every other writer of this team uses"
            }

        testCaseAsync "the three writers and the bare-id writers share one stream"
        <| async {
            let! grant = grantGuardKey ()
            let! expiry = expiryHookKey ()
            let! classification = classificationGateKey ()

            let streams = Set.unionMany [ grant; expiry; classification; Set.singleton bareKey ]

            Expect.equal streams (Set.singleton bareKey) "one team, one audit stream"
        }

        // Phase 1060 — the typed rendering IS the bytes at rest: a team's
        // stream is its bare id, never its container, and the 1059 helper
        // spells the same key.
        testCase "AuditScope.streamKey renders the bare id the rows at rest carry"
        <| fun _ ->
            Expect.equal bareKey "acme" "the bare team id"

            Expect.equal
                (AuditScope.streamKey (AuditScope.team (TeamId teamId)))
                bareKey
                "the team constructor renders the bare id"

            Expect.equal
                (AuditScope.streamKey (StorageScope.auditScope (StorageScope.ofKey true (ScopeKey.Team(TeamId teamId)))))
                bareKey
                "a team's StorageScope keys the same stream"

            Expect.notEqual bareKey (ScopeKey.container (ScopeKey.Team(TeamId teamId))) "never the container"
    ]