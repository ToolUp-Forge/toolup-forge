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
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

let private teamId = "acme"

/// The key the request middleware (`scope.ScopeId`) and
/// `PlatformApiHandler` (`teamId`) record this team's rows under.
let private bareKey = teamId

type private RecordingAuditLog() =
    let recorded = ConcurrentQueue<string * AuditEvent>()
    member _.Keys = recorded |> Seq.map fst |> Set.ofSeq
    member _.Count = recorded.Count

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Enqueue(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Wait for a fire-and-forget emission (the grant guard's) to land.
let private settle (audit: RecordingAuditLog) (expected: int) = async {
    let waited = ref 0

    while audit.Count < expected && waited.Value < 100 do
        do! Async.Sleep 20
        waited.Value <- waited.Value + 1
}

let private grantGuardKey () = async {
    let audit = RecordingAuditLog()

    let registry =
        GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations [ "Partner", GrantPolicy.RequiresAcknowledgement ]

    let store =
        GrantPolicyGuard.GrantPolicyPermissionStore(PermissionStore(InMemoryBlobStorage()), registry, audit, "admin")
        :> IPermissionStore

    let! _ = store.SetMemberPermissions(teamId, "alice", "Partner", [ ModulePermission.Read ])
    do! settle audit 1
    return audit.Keys
}

let private expiryHookKey () = async {
    let audit = RecordingAuditLog()

    let hook =
        ToolUp.Platform.Teams.PendingInviteExpiryHook.build silentLogger (Some(audit :> IAuditLog)) None

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

    return audit.Keys
}

let private classificationGateKey () = async {
    let audit = RecordingAuditLog()

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

    let ctx = AccessContext.unrestricted (TeamMember("alice", teamId))
    do! ClassificationGate.recordWrites classifier audit ctx "Employee" [ "salary" ]
    return audit.Keys
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
    ]