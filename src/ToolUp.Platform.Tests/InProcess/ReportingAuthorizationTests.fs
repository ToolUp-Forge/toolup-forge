module ToolUp.Platform.Tests.InProcess.ReportingAuthorizationTests

open System
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Remoting.Server
open ToolUp.Reporting

// ─── Phase 619 — secure-by-default authorization for `IReportApi` ─────
//
// Every method on `IReportApi` used to carry `[<AllowAnonymous>]` while
// its doc comments described Owner / Admin gating as "deployment
// wiring". Phase 564 made a rendered report a disclosure EGRESS door —
// narrative placeholders carrying fact refs leave the deployment through
// `Render` — and an egress door whose default classification is "anyone"
// contradicts the default-deny posture the rest of the authorization
// surface is built on. This is the same defect class Phase 229 closed on
// the DSR export/erasure endpoints, including the identical tell: a
// comment asserting the very thing that was false.
//
// What this pack pins, and why each part is here rather than assumed:
//
//   A — the per-method classification itself. Four methods, four
//       `[<RequiresClaim "scope">]` gates, ZERO anonymous. Keyed by
//       method name so adding a fifth method without a gate fails here
//       (the record-wide assertion), not only at a deployment's startup.
//   B — an anonymous subject is actually REFUSED. Driven through the
//       real `ApiSeams.defaultForgeAuthContextResolver` against a bare
//       `HttpContext` — the shape a genuinely unauthenticated request
//       produces — not a hand-rolled `IsAnonymous() = true` double that
//       could agree with the assertion for the wrong reason.
//   B' — the falsifier. The same probe run against a fixture record
//       still annotated the pre-619 way must ALLOW. Without it, a
//       passing deny test proves nothing about whether the deny came
//       from the new gate or from some unrelated fail-closed path, and
//       "a passing auth test that never exercises the anonymous path" is
//       precisely this phase's failure mode.
//   C — an authenticated caller PASSES. A gate that denies everyone is
//       not a fix; it is a Phase 132 dead gate wearing a fix's clothes.
//   D — no dead gate was introduced (`unemittableRoles` empty) and every
//       field round-trips the route builder, so the per-request
//       classification lookup cannot miss and deny-on-miss everything.
//   E — the Phase 438 `AuthorizationSurface` manifest, which reads the
//       SAME classification the dispatcher enforces, reports the record
//       with an empty `anonymousReachable` set. This is the artefact a
//       security review actually opens with; before 619 all four methods
//       were in its headline list.
//   F — the in-handler management gate (`withManagementGate`), the
//       229-shaped second gate on the mutating half.

// ── the record under test ───────────────────────────────────────────

let private reportApiType = typeof<IReportApi>

let private reportApiMethods = [ "ListTemplates"; "SaveTemplate"; "DeleteTemplate"; "Render" ]

/// The pre-619 shape, kept as a live fixture rather than as prose. It is
/// what makes the deny assertions in section B falsifiable: the same
/// probe, the same resolver, the opposite verdict.
type private PreviousReportApiShape = {
    [<AllowAnonymous>]
    ListTemplates: unit -> Async<int>
    [<AllowAnonymous>]
    SaveTemplate: string -> Async<int>
    [<AllowAnonymous>]
    DeleteTemplate: string -> Async<int>
    [<AllowAnonymous>]
    Render: string -> Async<int>
}

// ── auth-context plumbing ───────────────────────────────────────────

/// Bridge `ForgeAuthContext` → `IAuthContext`, member for member — the
/// same adapter `Api.make` composes. Using the real resolver plus the
/// real bridge means this pack evaluates exactly what a deployment
/// evaluates.
let private bridge (forge: ForgeAuthContext) : IAuthContext =
    { new IAuthContext with
        member _.HasRole role = forge.HasRole role
        member _.HasClaim(claim, value) = forge.HasClaim(claim, value)
        member _.HasTenant() = forge.HasTenant()
        member _.IsAnonymous() = forge.IsAnonymous()
        member _.SubjectId = forge.SubjectId
    }

/// The auth context a genuinely unauthenticated request produces: no
/// `ToolUp.User` item stamped, so the resolver falls back to
/// `AuthenticatedUser.anonymous`.
let private anonymousContext () = async {
    let ctx = DefaultHttpContext() :> HttpContext
    let! forge = ApiSeams.defaultForgeAuthContextResolver ctx
    return bridge forge
}

/// The auth context an ordinary signed-in, non-admin caller produces —
/// the `ScopeResolutionMiddleware` stamp, reproduced.
let private authenticatedContext () = async {
    let ctx = DefaultHttpContext() :> HttpContext

    // Fully qualified: `Subject.AuthenticatedUser` is a DU case in
    // scope, so a bare `AuthenticatedUser.anonymous` resolves to the
    // case constructor, not the module.
    let user: ToolUp.Platform.Auth.AuthenticatedUser = {
        UserId = "u-619"
        DisplayName = "Ordinary Caller"
        Email = Some "caller@example.test"
        TenantId = None
        Roles = []
    }

    ctx.Items["ToolUp.User"] <- box user
    ctx.Items["ToolUp.Subject"] <- box (Subject.AuthenticatedUser "u-619")
    let! forge = ApiSeams.defaultForgeAuthContextResolver ctx
    return bridge forge
}

let private classificationOf (apiType: Type) (methodName: string) =
    AuthClassifier.classify apiType
    |> Map.tryFind methodName
    |> Option.defaultWith (fun () -> failtestf "method %s not found on %s" methodName apiType.Name)

let private expectDeny (decision: AuthDecision) (label: string) =
    match decision with
    | Deny _ -> ()
    | Allow -> failtestf "%s: expected Deny, got Allow" label

let private expectAllow (decision: AuthDecision) (label: string) =
    match decision with
    | Allow -> ()
    | Deny reason -> failtestf "%s: expected Allow, got Deny (%s)" label reason

// ── section F fixtures ──────────────────────────────────────────────

/// A minimal `IReportApi` that records which methods ran, so the
/// management-gate decorator can be observed to short-circuit rather
/// than merely to return an error the inner api might have produced
/// anyway.
let private recordingApi (calls: ResizeArray<string>) : IReportApi = {
    ListTemplates =
        fun () -> async {
            calls.Add "ListTemplates"
            return []
        }
    SaveTemplate =
        fun template -> async {
            calls.Add "SaveTemplate"
            return Result.Ok template
        }
    DeleteTemplate =
        fun _ -> async {
            calls.Add "DeleteTemplate"
            return Result.Ok()
        }
    Render =
        fun _ -> async {
            calls.Add "Render"
            return Result.Ok(RenderedInline([||], "text/plain"))
        }
}

let private sampleTemplate: ReportTemplate = {
    Id = "t-1"
    DisplayName = "Quarterly"
    Format = Markdown
    Body = Text.Encoding.UTF8.GetBytes "hello"
    Placeholders = []
    Version = 1
}

[<Tests>]
let tests =
    testList "Phase 619 — IReportApi secure-by-default authorization" [

        // ── A — the classification ──────────────────────────────────
        testList "A — per-method classification" [
            test "no method on IReportApi is AllowAnonymous or PublicEndpoint" {
                // Record-wide, not a hand-listed four: a fifth method
                // added without a gate fails here.
                let reachableAnonymously =
                    AuthClassifier.classify reportApiType
                    |> Map.toList
                    |> List.choose (fun (name, cls) ->
                        match cls with
                        | Anonymous -> Some(name + " (AllowAnonymous)")
                        | Public -> Some(name + " (PublicEndpoint)")
                        | Unclassified -> Some(name + " (unclassified)")
                        | RequiresAuth _ -> None)

                Expect.isEmpty
                    reachableAnonymously
                    "Phase 619: every IReportApi method must carry a real gate. A rendered report is a disclosure egress door (Phase 564); an anonymous-reachable method on it is the defect this phase closed. If a share-token-bearing public surface is genuinely wanted, it belongs on a separate [<PublicEndpoint>] contract taking the token as a parameter (the IPublicFormApi shape), not as a relaxation here."
            }

            test "every method requires the 'scope' claim" {
                for methodName in reportApiMethods do
                    match classificationOf reportApiType methodName with
                    | RequiresAuth requirements ->
                        Expect.equal
                            requirements
                            [ ClaimRequired("scope", None) ]
                            (sprintf
                                "%s must carry exactly [<RequiresClaim \"scope\">] — the forge-conventional gate for a scope-owned surface that is never anonymous"
                                methodName)
                    | other -> failtestf "%s: expected RequiresAuth, got %A" methodName other
            }

            test "the classifier sees all four methods and no others" {
                let classified =
                    AuthClassifier.classify reportApiType |> Map.toList |> List.map fst |> List.sort

                Expect.equal classified (List.sort reportApiMethods) "IReportApi's classified field set"
            }
        ]

        // ── B — the anonymous path is actually refused ───────────────
        testList "B — an anonymous subject is refused" [
            for methodName in reportApiMethods do
                testCaseAsync (sprintf "%s denies an anonymous caller" methodName)
                <| async {
                    let! anon = anonymousContext ()

                    expectDeny
                        (AuthClassifier.evaluate (classificationOf reportApiType methodName) (Some anon))
                        (sprintf "IReportApi.%s against the real default resolver with no authenticated user" methodName)
                }

            testCaseAsync "a missing auth-context resolver also denies (fail-closed)"
            <| async {
                // The other anonymous shape: a deployment with no
                // resolver armed at all. `RequiresAuth` + `None` is a
                // deny by construction; pinned so a future refactor
                // cannot quietly make an unresolved context permissive.
                for methodName in reportApiMethods do
                    expectDeny
                        (AuthClassifier.evaluate (classificationOf reportApiType methodName) None)
                        (sprintf "IReportApi.%s with no auth-context resolver" methodName)
            }
        ]

        // ── B' — the falsifier ──────────────────────────────────────
        testList "B' — the probe can fail" [
            testCaseAsync "the SAME probe ALLOWS the pre-619 [<AllowAnonymous>] shape"
            <| async {
                // If this ever starts denying, section B has stopped
                // measuring the gate and started measuring something
                // else — at which point section B passing means nothing.
                let! anon = anonymousContext ()

                for methodName in reportApiMethods do
                    expectAllow
                        (AuthClassifier.evaluate
                            (classificationOf typeof<PreviousReportApiShape> methodName)
                            (Some anon))
                        (sprintf
                            "PreviousReportApiShape.%s is [<AllowAnonymous>] — it MUST allow, or section B's deny proves nothing about the new gate"
                            methodName)
            }
        ]

        // ── C — an authenticated caller passes ──────────────────────
        testList "C — an authenticated caller passes" [
            testCaseAsync "every method allows an ordinary signed-in caller"
            <| async {
                // Deliberately NOT an admin, and deliberately with no
                // tenant: a scope-owned surface must stay reachable by
                // the caller who owns the scope. A gate that only a
                // PlatformAdmin (or nobody at all) clears would be a
                // regression dressed as a hardening.
                let! authed = authenticatedContext ()

                for methodName in reportApiMethods do
                    expectAllow
                        (AuthClassifier.evaluate (classificationOf reportApiType methodName) (Some authed))
                        (sprintf "IReportApi.%s for an authenticated non-admin, non-tenant caller" methodName)
            }
        ]

        // ── D — no dead gate, no lookup miss ────────────────────────
        testList "D — the gate is live, not dead" [
            test "no unemittable role gate was introduced" {
                // Phase 132: the default resolver only ever emits
                // "PlatformAdmin". `[<RequiresRole "Owner">]` — the role
                // the pre-619 doc comments named — would deny EVERY
                // caller with no compose-time signal.
                let dead =
                    AuthClassifier.unemittableRoles
                        (fun role -> role = "PlatformAdmin")
                        (AuthClassifier.classify reportApiType)

                Expect.isEmpty dead "IReportApi must not gate on a role the default resolver can never emit"
            }

            test "every classified field round-trips the default route builder" {
                let divergences =
                    AuthClassifier.nonRoundTripping
                        (sprintf "/api/%s/%s")
                        "IReportApi"
                        (AuthClassifier.classify reportApiType)

                Expect.isEmpty
                    divergences
                    "a field whose route's trailing segment diverges from its name makes the per-request classification lookup miss — and Phase 132 deny-on-miss then denies every call"
            }
        ]

        // ── E — the security-review artefact ────────────────────────
        testList "E — the Phase 438 authorization surface" [
            test "IReportApi contributes nothing to the anonymous-reachable headline" {
                let componentId = ComponentId.create "toolup.reporting"
                let surface = AuthorizationSurface.ofApiRecord<IReportApi> componentId

                let anonymous =
                    AuthorizationSurface.anonymousReachable surface
                    |> List.map AuthorizationSurface.describe

                Expect.isEmpty
                    anonymous
                    "before Phase 619 all four IReportApi methods sat in this headline list — the set a security review opens with"
            }

            test "every IReportApi entry is an explicit requirement" {
                let componentId = ComponentId.create "toolup.reporting"
                let surface = AuthorizationSurface.ofApiRecord<IReportApi> componentId

                let notExplicit =
                    surface.Exposed
                    |> List.filter (fun entry -> entry.Access <> ExplicitRequirement)
                    |> List.map AuthorizationSurface.describe

                Expect.isEmpty notExplicit "every IReportApi endpoint declares a gate"

                Expect.equal
                    (surface.Exposed |> List.collect _.Requires |> List.distinct)
                    [ "claim:scope" ]
                    "the manifest reports the same requirement the dispatcher enforces — there is only one classification"
            }

            test "the pre-619 shape diffs as a CRITICAL weakening against the shipped one" {
                // The direction check: swapping today's record for the
                // old one is exactly the class the composition gate is
                // meant to shout about. Endpoint keys are
                // `<RecordName>.<Field>`, so the two records are
                // compared under one identity by projecting the old
                // entries onto the shipped endpoint names.
                let componentId = ComponentId.create "toolup.reporting"
                let shipped = AuthorizationSurface.ofApiRecord<IReportApi> componentId

                let regressed: AuthorizationSurface = {
                    Exposed =
                        AuthorizationSurface.ofApiRecord<PreviousReportApiShape> componentId
                        |> _.Exposed
                        |> List.map (fun entry -> {
                            entry with
                                Endpoint = entry.Endpoint.Replace("PreviousReportApiShape.", "IReportApi.")
                        })
                }

                let delta = AuthorizationSurface.diff shipped regressed

                Expect.equal
                    (AuthorizationSurface.severity delta)
                    CriticalAuthorizationDrift
                    "reverting IReportApi to [<AllowAnonymous>] must read as CRITICAL drift, not as noise"

                Expect.isNonEmpty delta.RequirementsWeakened "the four methods diff as weakened, endpoint for endpoint"
            }
        ]

        // ── F — the 229-shaped in-handler gate ──────────────────────
        testList "F — withManagementGate (the in-handler second gate)" [
            testCaseAsync "a denied caller cannot save"
            <| async {
                let calls = ResizeArray<string>()

                let api =
                    recordingApi calls
                    |> ReportApiHandler.withManagementGate (fun () -> async { return false })

                let! result = api.SaveTemplate sampleTemplate

                Expect.equal
                    result
                    (Result.Error ReportApiHandler.TemplateManagementDenied)
                    "a denied caller gets the named refusal"

                Expect.isEmpty
                    calls
                    "the inner handler must not run at all — the gate short-circuits, it does not filter a result"
            }

            testCaseAsync "a denied caller cannot delete"
            <| async {
                let calls = ResizeArray<string>()

                let api =
                    recordingApi calls
                    |> ReportApiHandler.withManagementGate (fun () -> async { return false })

                let! result = api.DeleteTemplate "t-1"
                Expect.equal result (Result.Error ReportApiHandler.TemplateManagementDenied) "delete refused"
                Expect.isEmpty calls "the inner handler must not run"
            }

            testCaseAsync "a permitted caller saves and deletes"
            <| async {
                let calls = ResizeArray<string>()

                let api =
                    recordingApi calls
                    |> ReportApiHandler.withManagementGate (fun () -> async { return true })

                let! saved = api.SaveTemplate sampleTemplate
                Expect.equal saved (Result.Ok sampleTemplate) "an Owner / Admin caller saves"

                let! deleted = api.DeleteTemplate "t-1"
                Expect.equal deleted (Result.Ok()) "an Owner / Admin caller deletes"

                Expect.sequenceEqual calls [ "SaveTemplate"; "DeleteTemplate" ] "both reached the inner handler"
            }

            testCaseAsync "reads and renders are untouched by the management gate"
            <| async {
                // Rendering is the ordinary user-facing operation the
                // companion exists for; the fact-level FactExport gate
                // (Phase 564.B) is what decides which VALUES a principal
                // may egress. Admin-gating Render would break the
                // feature, not harden it.
                let calls = ResizeArray<string>()

                let api =
                    recordingApi calls
                    |> ReportApiHandler.withManagementGate (fun () -> async { return false })

                let! templates = api.ListTemplates()
                Expect.isEmpty templates "list still reaches the inner handler"

                let! rendered = api.Render("t-1", Map.empty)
                Expect.isTrue (Result.isOk rendered) "render still reaches the inner handler"

                Expect.sequenceEqual calls [ "ListTemplates"; "Render" ] "both ran despite the management denial"
            }

            testCaseAsync "the predicate is read per call, not snapshotted at composition"
            <| async {
                // The build-once / read-per-call seam mismatch: a gate
                // that snapshots its verdict at construction keeps
                // admitting a caller whose rights were revoked.
                let permitted = ref false
                let calls = ResizeArray<string>()

                let api =
                    recordingApi calls
                    |> ReportApiHandler.withManagementGate (fun () -> async { return permitted.Value })

                let! first = api.SaveTemplate sampleTemplate
                Expect.equal first (Result.Error ReportApiHandler.TemplateManagementDenied) "denied while revoked"

                permitted.Value <- true
                let! second = api.SaveTemplate sampleTemplate
                Expect.equal second (Result.Ok sampleTemplate) "permitted once granted — the predicate was re-read"

                permitted.Value <- false
                let! third = api.SaveTemplate sampleTemplate

                Expect.equal
                    third
                    (Result.Error ReportApiHandler.TemplateManagementDenied)
                    "denied again once revoked — a snapshot would still be allowing here"
            }
        ]
    ]