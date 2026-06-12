module ToolUp.Platform.Tests.InProcess.AuditTests

open System
open System.IO
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe

// ─── Phase 69h.tail — [<Audit>] annotation sweep + dispatcher-emitted
// audit ──────────────────────────────────────────────────────────────
//
// Contract tests for the audit half of the Phase 69 family tails:
//   * audit classification recognises BOTH attribute families (the
//     server-tier `AuditAttribute` and the `ToolUp.Platform` mirror),
//     including `Custom:<name>` kind decoding;
//   * `Audit.inputTypes` caches first-argument record types for audited
//     methods so payload extraction works without ValidationAttributes;
//   * `payloadFromInputRecord` redacts non-`[<PiiSafe>]` fields and
//     honours the mirror-family `PiiSafe` too;
//   * the admin-must-be-audited startup gate
//     (`Remoting.withAuditRequiredOnRoleGated`, composed by `Api.make`
//     from `TOOLUP_AUDIT_ADMIN_REQUIRED`) refuses role-gated methods
//     without an `[<Audit>]` annotation;
//   * `Api.make` composes the default `IAuditEmitter` → `IAuditLog`
//     bridge and the platform `AuditEvent` DU round-trips the
//     `RemotingMethodAudited` case (source-audit pins, same idiom as
//     the Phase 69l / 69o packs).
//
// Idempotency-replay audit suppression (69h.tail Task C) is pinned
// structurally below: the replay path replays the cached response and
// returns BEFORE the proxy invocation whose Success branch owns audit
// emission. The integration-shape test (TestServer + composed
// IIdempotencyStore + counting emitter) shares the TestServer-scaffold
// gap tracked in TIDY-UP "ToolUp.Remoting per-request cleanups".

// ── Fixture types ───────────────────────────────────────────────────

type private ServerAuditedInput = {
    [<PiiSafe>]
    WidgetId: string
    Email: string
}

type private MirrorAuditedInput = {
    [<ToolUp.Platform.PiiSafe>]
    WidgetId: string
    Notes: string
}

type private ServerAuditedApi = {
    [<AllowAnonymous>]
    [<Audit "PolicyChanged">]
    ChangePolicy: ServerAuditedInput -> Async<unit>
    [<AllowAnonymous>]
    [<Audit "Custom:WidgetFrobbed">]
    Frob: string -> Async<unit>
    [<AllowAnonymous>]
    Unaudited: unit -> Async<unit>
}

type private MirrorAuditedApi = {
    [<ToolUp.Platform.AllowAnonymous>]
    [<ToolUp.Platform.Audit "PolicyChanged">]
    ChangePolicy: MirrorAuditedInput -> Async<unit>
    [<ToolUp.Platform.AllowAnonymous>]
    [<ToolUp.Platform.Audit "Custom:WidgetFrobbed">]
    Frob: string -> Async<unit>
    [<ToolUp.Platform.AllowAnonymous>]
    Unaudited: unit -> Async<unit>
}

type private RoleGatedUnauditedApi = {
    [<RequiresRole "Admin">]
    Promote: string -> Async<unit>
}

type private RoleGatedAuditedApi = {
    [<RequiresRole "Admin">]
    [<Audit "PermissionGranted">]
    Promote: string -> Async<unit>
}

let private dummyResolver (_: HttpContext) : Async<IAuthContext> = async {
    return
        { new IAuthContext with
            member _.HasRole _ = true
            member _.HasClaim(_, _) = true
            member _.HasTenant() = true
            member _.IsAnonymous() = false
            member _.SubjectId = "test-subject"
        }
}

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private serverSource (relative: string list) =
    File.ReadAllText(
        Path.Combine(
            repoRoot () :: "src" :: "ToolUp.Platform.Server" :: "Server" :: relative
            |> List.toArray
        )
    )

[<Tests>]
let tests =
    testList "Phase 69h.tail — audit annotation sweep" [

        // ── Classification, both families ──
        test "server-family [<Audit>] classifies with decoded kinds" {
            let audits = Audit.classify typeof<ServerAuditedApi>
            Expect.equal (audits |> Map.tryFind "ChangePolicy") (Some AuditKind.PolicyChanged) "well-known kind"
            Expect.equal (audits |> Map.tryFind "Frob") (Some(AuditKind.Custom "WidgetFrobbed")) "custom kind"
            Expect.isNone (audits |> Map.tryFind "Unaudited") "unaudited methods are absent"
        }

        test "mirror-family [<Audit>] classifies identically (family-agnostic recognition)" {
            let server = Audit.classify typeof<ServerAuditedApi>
            let mirror = Audit.classify typeof<MirrorAuditedApi>

            Expect.equal
                mirror
                server
                "the ToolUp.Platform.Audit mirror must classify exactly like the \
                 server-tier attribute — Fable-compiled API records carry the mirror"
        }

        test "Audit.inputTypes caches record-shaped first arguments of audited methods only" {
            let inputTypes = Audit.inputTypes typeof<ServerAuditedApi>

            Expect.equal
                (inputTypes |> Map.tryFind "ChangePolicy")
                (Some typeof<ServerAuditedInput>)
                "audited method with a record input is cached"

            Expect.isNone (inputTypes |> Map.tryFind "Frob") "audited method with a string input is absent"
            Expect.isNone (inputTypes |> Map.tryFind "Unaudited") "unaudited method is absent"
        }

        // ── PII redaction, both families ──
        test "payloadFromInputRecord stringifies PiiSafe fields and redacts the rest" {
            let payload =
                Audit.payloadFromInputRecord typeof<ServerAuditedInput> {
                    WidgetId = "w-42"
                    Email = "user@example.com"
                }

            Expect.equal (payload |> Map.tryFind "WidgetId") (Some "w-42") "PiiSafe field carries its value"

            Expect.equal
                (payload |> Map.tryFind "Email")
                (Some "<redacted:String>")
                "non-PiiSafe field is redacted — fail-safe default"
        }

        test "mirror-family [<PiiSafe>] is honoured" {
            let payload =
                Audit.payloadFromInputRecord typeof<MirrorAuditedInput> {
                    WidgetId = "w-7"
                    Notes = "customer rang about the invoice"
                }

            Expect.equal (payload |> Map.tryFind "WidgetId") (Some "w-7") "mirror PiiSafe field carries its value"
            Expect.equal (payload |> Map.tryFind "Notes") (Some "<redacted:String>") "unmarked field redacted"
        }

        // ── Admin-must-be-audited startup gate ──
        test "withAuditRequiredOnRoleGated refuses a role-gated method without [<Audit>]" {
            let impl: RoleGatedUnauditedApi = {
                Promote = fun _ -> async { return () }
            }

            let compose () =
                Remoting.createApi ()
                |> Remoting.withAuthContext dummyResolver
                |> Remoting.withAuditRequiredOnRoleGated
                |> Remoting.fromValue impl
                |> Remoting.buildHttpHandler
                |> ignore

            // Expecto's `Expect.throwsT` returns unit — capture the
            // exception manually to assert on the diagnostic text.
            let ex =
                try
                    compose ()
                    failtest "expected the admin-must-be-audited gate to refuse with InvalidOperationException"
                with :? InvalidOperationException as ex ->
                    ex

            Expect.stringContains ex.Message "RoleGatedUnauditedApi" "diagnostic names the record"
            Expect.stringContains ex.Message "Promote" "diagnostic names the method"
        }

        test "withAuditRequiredOnRoleGated passes when role-gated methods carry [<Audit>]" {
            let impl: RoleGatedAuditedApi = {
                Promote = fun _ -> async { return () }
            }

            Remoting.createApi ()
            |> Remoting.withAuthContext dummyResolver
            |> Remoting.withAuditRequiredOnRoleGated
            |> Remoting.fromValue impl
            |> Remoting.buildHttpHandler
            |> ignore
        }

        test "the gate is off by default (GP 11)" {
            let impl: RoleGatedUnauditedApi = {
                Promote = fun _ -> async { return () }
            }

            // Without the gate, a role-gated unaudited method composes fine.
            Remoting.createApi ()
            |> Remoting.withAuthContext dummyResolver
            |> Remoting.fromValue impl
            |> Remoting.buildHttpHandler
            |> ignore
        }

        // ── Api.make default audit bridge (source-audit pins) ──
        test "Api.make composes the default audit emitter for audited records only" {
            let contents = serverSource [ "Api.fs" ]

            Expect.stringContains
                contents
                "Remoting.withAudit ApiSeams.defaultAuditEmitter"
                "Api.make must bridge dispatcher audit events to IAuditLog by default"

            Expect.stringContains
                contents
                "if hasAuditedMethods then"
                "the bridge composition must be conditional on the record carrying \
                 [<Audit>] annotations — zero cost for unaudited records (GP 13)"
        }

        test "Api.make reads TOOLUP_AUDIT_ADMIN_REQUIRED for the admin gate" {
            let contents = serverSource [ "Api.fs" ]

            Expect.stringContains contents "TOOLUP_AUDIT_ADMIN_REQUIRED" "env-var gate must be read"

            Expect.stringContains
                contents
                "Remoting.withAuditRequiredOnRoleGated"
                "the env var must compose the dispatcher-level gate"
        }

        test "platform AuditEvent DU round-trips RemotingMethodAudited through EventStoreAuditLog" {
            let auditLog = serverSource [ "AuditLog.fs" ]

            Expect.stringContains
                auditLog
                "| RemotingMethodAudited p -> toAuditJson p"
                "serialise must handle the new case — the match is partial and an \
                 unlisted case throws at emission time, silently losing the row"

            Expect.stringContains
                auditLog
                "\"RemotingMethodAudited\" ->"
                "decode must round-trip the case for GetAuditTrail read-back"
        }

        // ── Idempotency-replay suppression (structural pin) ──
        test "idempotency replay path precedes invocation; audit emission rides the Success branch" {
            let adapter = serverSource [ "Remoting"; "Giraffe"; "GiraffeAdapter.fs" ]

            // The replay branch replays the memoised response and never
            // reaches the proxy invocation. Audit emission is keyed off
            // the AuditEmitter + audits map inside the Success branch of
            // the invocation result — so a replayed call cannot emit a
            // fresh audit row (69f.E semantics). The replay branch's
            // adapter-side marker is the `x-idempotency-replay` response
            // header (the `MemoisedResponse` type itself lives in
            // Idempotency.fs, not the adapter).
            let replayIdx = adapter.IndexOf "x-idempotency-replay"
            let auditEmissionIdx = adapter.IndexOf "options.AuditEmitter, audits |> Map.tryFind"

            Expect.isGreaterThan replayIdx -1 "the idempotency replay path must exist"
            Expect.isGreaterThan auditEmissionIdx -1 "the audit emission block must exist"

            Expect.isLessThan
                replayIdx
                auditEmissionIdx
                "the replay early-return must precede the audit-emission block — a replayed \
                 call short-circuits before the Success branch and cannot double-audit"

            Expect.stringContains
                adapter
                "Phase 69h — audit emission after successful invocation"
                "audit emission must stay anchored to the successful-invocation branch; \
                 moving it upstream would make idempotency replays double-audit"
        }
    ]