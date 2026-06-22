module ToolUp.Platform.Tests.InProcess.AdversarialFailClosedTests

open System
open System.IO
open System.Collections.Generic
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe
open ToolUp.Platform.Tests.Contracts.FailClosedContract

// ─── Phase 196 — adversarial fail-closed test pack ──────────────────────
//
// Closes the gap where Epoch-1 Wave 12 (Phase 69d/69h) proved annotated
// methods *allow* and *audit*, but never proved that an un-annotated or
// mis-annotated method *refuses*. This pack codifies the "forgot the guard
// / forgot to audit" defect class as structurally impossible to ship
// silently: every adversarial fixture either fails to start or fails
// closed.
//
// Pure test-tier work — no production source changes. The pack drives the
// real classifier (`AuthClassifier.evaluate` / `Audit.classify`) and the
// real composition path (`Remoting.buildHttpHandler` / `Api.make`), and
// source-pins the one path that needs a TestServer scaffold the runner
// doesn't have (the dispatcher's deny early-return ordering + the audit
// emission gate). The classifier fail-closed *contract* itself lives in
// Contracts/FailClosedContract.fs and is instantiated here against both
// attribute families.
//
// Sections:
//   A — classifier fail-closed contract (server + mirror families)
//   B — un-annotated method refuses to start (both compose paths)
//   C — the deny path returns before the handler runs (ordering pin)
//   D — the deny signal on the wire is the opaque generic category
//   E — a money-moving method without [<Audit>] emits zero events; the
//       inverse emits exactly one (omission is observable)
//   F — forgetting [<PiiSafe>] keeps PII out of the audit row

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

let private dummyResolver (_: HttpContext) : Async<IAuthContext> = async { return godMode }

/// A non-anonymous, under-credentialled caller used to read the distinct
/// server-side deny reasons (anonymous would short-circuit every cause to
/// the same `anonymous-not-permitted` reason before the predicates run).
let private reasonProbeCtx (roles: string list) (hasTenant: bool) : IAuthContext =
    { new IAuthContext with
        member _.HasRole r = List.contains r roles
        member _.HasClaim(_, _) = false
        member _.HasTenant() = hasTenant
        member _.IsAnonymous() = false
        member _.SubjectId = "probe"
    }

// ── Section B fixtures — un-annotated methods ───────────────────────────

/// One classified method, one money-moving method with NO authorization
/// attribute. With a resolver armed, composition must refuse — naming the
/// record AND the naked method.
type private OneUnclassifiedApi = {
    [<AllowAnonymous>]
    Fine: unit -> Async<int>
    MoveMoney: decimal -> Async<unit>
}

type private FullyUnclassifiedApi = { Naked: unit -> Async<int> }

// ── Section C/E fixtures — deny-reason discriminability + audit ──────────

/// Distinct deny causes used to show the reason is computed server-side
/// (and differs per cause) while the wire envelope carries no such field.
type private ReasonProbeApi = {
    [<RequiresRole "Admin">]
    NeedsRole: unit -> Async<int>
    [<TenantScoped>]
    NeedsTenant: unit -> Async<int>
}

type private MoneyMoverInput = {
    [<PiiSafe>]
    TransferId: string
    AccountNumber: string
    Amount: decimal
}

/// A money-moving method WITHOUT `[<Audit>]` — the "forgot to audit"
/// defect. The dispatcher's emission gate (`audits |> Map.tryFind`) yields
/// None, so it can never emit a row.
type private UnauditedMoneyApi = {
    [<RequiresRole "Treasurer">]
    MoveMoney: MoneyMoverInput -> Async<unit>
}

/// The inverse — correctly annotated, emits exactly one MoneyMoved row.
type private AuditedMoneyApi = {
    [<RequiresRole "Treasurer">]
    [<Audit "MoneyMoved">]
    MoveMoney: MoneyMoverInput -> Async<unit>
}

/// Counting `IAuditEmitter` double.
type private CountingEmitter() =
    let events = List<AuditEvent>()
    member _.Events = List.ofSeq events

    interface IAuditEmitter with
        member _.Emit e = async { events.Add e }

// Faithful mirror of the dispatcher's Success-branch emission rule
// (GiraffeAdapter.fs: `match options.AuditEmitter, audits |> Map.tryFind
// methodName with | Some emitter, Some kind -> ...`). The source-pin in
// Section E asserts the real dispatcher uses exactly this `audits |>
// Map.tryFind` gate, so a method absent from `Audit.classify`'s map can
// NEVER emit — the omission is structurally observable through this mirror.
let private simulateSuccessfulInvocation
    (emitter: IAuditEmitter)
    (apiType: Type)
    (methodName: string)
    (input: obj)
    : Async<unit> =
    async {
        let audits = Audit.classify apiType

        match audits |> Map.tryFind methodName with
        | Some kind ->
            let payload =
                match Audit.inputTypes apiType |> Map.tryFind methodName with
                | Some inputType -> Audit.payloadFromInputRecord inputType input
                | None -> Map.empty

            do!
                emitter.Emit {
                    Kind = kind
                    MethodName = methodName
                    SubjectId = "test-subject"
                    CorrelationId = None
                    Timestamp = DateTimeOffset.UnixEpoch
                    Payload = payload
                }
        | None -> ()
    }

[<Tests>]
let tests =
    testList "Phase 196 — adversarial fail-closed pack" [

        // ── A — classifier fail-closed contract, both families ──
        testList "classifier fail-closed contract" [
            yield! classifierFailClosedContract "server-family" serverFamilyClassification
            yield! classifierFailClosedContract "mirror-family" mirrorFamilyClassification
        ]

        // ── B — un-annotated method refuses to start ──
        test "buildHttpHandler refuses to start when a resolver is armed and a method is unclassified" {
            let impl: OneUnclassifiedApi = {
                Fine = fun () -> async { return 1 }
                MoveMoney = fun _ -> async { return () }
            }

            let compose () =
                Remoting.createApi ()
                |> Remoting.withAuthContext dummyResolver
                |> Remoting.fromValue impl
                |> Remoting.buildHttpHandler
                |> ignore

            let ex =
                try
                    compose ()
                    failtest "expected the classifier to refuse composition with InvalidOperationException"
                with :? InvalidOperationException as ex ->
                    ex

            Expect.stringContains ex.Message "OneUnclassifiedApi" "diagnostic names the record"
            Expect.stringContains ex.Message "MoveMoney" "diagnostic names the unclassified money-moving method"

            Expect.stringContains
                ex.Message
                "Phase 69d"
                "diagnostic cites the classifier contract so the operator knows what to annotate"
        }

        test "Api.make refuses a fully-unannotated record (default-on classifier)" {
            let builder (_: HttpContext) : FullyUnclassifiedApi = { Naked = fun () -> async { return 1 } }

            Expect.throwsT<InvalidOperationException>
                (fun () -> ToolUp.Platform.Api.make builder |> ignore)
                "Api.make composes a resolver by default, so an unannotated record must refuse at composition"
        }

        // ── C — the deny path returns before the handler runs ──
        test "the dispatcher deny path builds an Auth envelope and returns BEFORE the proxy invocation" {
            // No TestServer scaffold in this runner; pin the ordering in the
            // adapter source instead. The deny branch (`if denyReason.IsSome
            // then`) builds an `ErrorCategory.Auth` envelope and `return!`s —
            // it sits textually before `match! proxy propsWithCache`, the
            // single site the handler body runs. So a denied call can never
            // reach the handler: fail-closed before dispatch.
            let adapter = serverSource [ "Remoting"; "Giraffe"; "GiraffeAdapter.fs" ]

            let denyIdx = adapter.IndexOf "if denyReason.IsSome then"
            let authEnvelopeIdx = adapter.IndexOf "ErrorCategory.Auth"
            let proxyIdx = adapter.IndexOf "match! proxy propsWithCache"

            Expect.isGreaterThan denyIdx -1 "the deny branch must exist"
            Expect.isGreaterThan authEnvelopeIdx -1 "the Auth envelope must exist"
            Expect.isGreaterThan proxyIdx -1 "the proxy invocation must exist"

            Expect.isLessThan
                denyIdx
                proxyIdx
                "the deny branch must precede the proxy invocation — a denied call returns \
                 before the handler body runs (fail-closed before dispatch)"

            Expect.isLessThan
                authEnvelopeIdx
                proxyIdx
                "the categorised Auth envelope is built on the deny path, before invocation"

            Expect.stringContains
                adapter
                "ctx.Response.StatusCode <- 401"
                "the deny path sets 401 — the call is rejected, not handled"
        }

        // ── D — the deny signal on the wire is the opaque generic category ──
        test "the deny envelope category is the opaque generic 'auth' (clients branch on category, not the rule)" {
            // Every deny cause — missing-role, missing-tenant, anonymous,
            // no-resolver — routes through the same `ErrorCategory.Auth`
            // envelope, so the client-branchable signal never enumerates
            // *which* authorization rule failed.
            let envelope = Errors.categorisedWithSchema 1 ErrorCategory.Auth (box "opaque")
            Expect.equal envelope.category "auth" "the wire category for a denial is the generic 'auth'"
        }

        test "the wire error envelope carries no structured deny-reason field (the reason is server-side data)" {
            // `AuthClassifier.evaluate` returns the rich reason as F# data so
            // server logs / telemetry can discriminate the cause; but the
            // wire envelope type (`CategorisedErrorResult`) has only
            // `category` + an opaque `error` payload — there is no typed
            // `denyReason` field a client could branch on to enumerate the
            // authorization rules.
            let fields =
                Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<CategorisedErrorResult<obj>>)
                |> Array.map _.Name

            Expect.isFalse
                (fields |> Array.exists (fun n -> n.ToLowerInvariant().Contains "reason"))
                "the wire envelope must not expose a structured deny-reason field"

            Expect.contains fields "category" "the only structured auth signal on the wire is the generic category"
        }

        test "evaluate computes a distinct server-side reason per deny cause (discriminable off the wire)" {
            // The reason exists — server-side — and differs by cause. This
            // is what makes the wire opacity a deliberate choice rather than
            // information loss: operators still see WHY in logs.
            let cls = AuthClassifier.classify typeof<ReasonProbeApi>

            let reasonOf name context =
                match AuthClassifier.evaluate cls[name] context with
                | Deny r -> r
                | Allow -> failtestf "%s: expected Deny" name

            let roleReason = reasonOf "NeedsRole" (Some(reasonProbeCtx [] false))

            let tenantReason = reasonOf "NeedsTenant" (Some(reasonProbeCtx [] false))

            Expect.stringContains roleReason "role" "the missing-role denial cites the role requirement server-side"

            Expect.stringContains
                tenantReason
                "tenant"
                "the missing-tenant denial cites the tenant requirement server-side"

            Expect.notEqual roleReason tenantReason "distinct causes produce distinct server-side reasons"
        }

        // ── E — audit omission is observable; the inverse emits one ──
        test "a money-moving method with no [<Audit>] is absent from the classification map (zero events)" {
            let audits = Audit.classify typeof<UnauditedMoneyApi>

            Expect.isNone
                (audits |> Map.tryFind "MoveMoney")
                "an unannotated money-mover is NOT in the audit map — the dispatcher's \
                 `audits |> Map.tryFind` gate yields None, so zero AuditEvents are emitted"

            let emitter = CountingEmitter()

            simulateSuccessfulInvocation
                emitter
                typeof<UnauditedMoneyApi>
                "MoveMoney"
                (box {
                    TransferId = "t-1"
                    AccountNumber = "ACCT-1"
                    Amount = 100m
                })
            |> Async.RunSynchronously

            Expect.isEmpty
                emitter.Events
                "no audit row is emitted for the un-annotated money-mover — the omission is \
                 observable, and this test fails the moment a stray annotation silently changes it"
        }

        test "the same method WITH [<Audit \"MoneyMoved\">] emits exactly one MoneyMoved event" {
            let audits = Audit.classify typeof<AuditedMoneyApi>

            Expect.equal
                (audits |> Map.tryFind "MoveMoney")
                (Some AuditKind.MoneyMoved)
                "the annotated money-mover decodes to the MoneyMoved kind"

            let emitter = CountingEmitter()

            simulateSuccessfulInvocation
                emitter
                typeof<AuditedMoneyApi>
                "MoveMoney"
                (box {
                    TransferId = "t-1"
                    AccountNumber = "ACCT-1"
                    Amount = 100m
                })
            |> Async.RunSynchronously

            Expect.equal (List.length emitter.Events) 1 "exactly one audit row is emitted"
            Expect.equal emitter.Events[0].Kind AuditKind.MoneyMoved "the row is a MoneyMoved row"
            Expect.equal emitter.Events[0].MethodName "MoveMoney" "the row names the method"
        }

        test "the dispatcher gates audit emission on the `audits |> Map.tryFind methodName` lookup" {
            // Anchors the emission mirror above to the real dispatcher: a
            // method absent from `Audit.classify`'s map can never emit
            // because the adapter's emission block is gated on exactly this
            // lookup. (Twin of the existing AuditTests pin, kept here so the
            // omission-observability claim is self-contained.)
            let adapter = serverSource [ "Remoting"; "Giraffe"; "GiraffeAdapter.fs" ]

            Expect.stringContains
                adapter
                "options.AuditEmitter, audits |> Map.tryFind methodName"
                "audit emission is gated on the classification-map lookup — a method absent \
                 from Audit.classify can never emit"
        }

        // ── F — forgetting [<PiiSafe>] keeps PII out of the row ──
        test "forgetting [<PiiSafe>] keeps PII out of the audit payload (fail-safe default)" {
            let payload =
                Audit.payloadFromInputRecord typeof<MoneyMoverInput> {
                    TransferId = "t-7"
                    AccountNumber = "ACCT-SECRET-001"
                    Amount = 999.99m
                }

            Expect.equal
                (payload |> Map.tryFind "TransferId")
                (Some "t-7")
                "the [<PiiSafe>] field carries its value into the row"

            Expect.equal
                (payload |> Map.tryFind "AccountNumber")
                (Some "<redacted:String>")
                "the unmarked account number is redacted — fail-safe default"

            Expect.equal
                (payload |> Map.tryFind "Amount")
                (Some "<redacted:Decimal>")
                "the unmarked amount is redacted — forgetting [<PiiSafe>] keeps it out of the row"
        }
    ]