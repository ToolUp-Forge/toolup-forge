module ToolUp.Platform.Tests.InProcess.ContentAdminAuthorizationTests

open System
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Server
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe
open ToolUp.ContentAuthoring

// ─── Phase 627 — `IContentAdminApi` authentication + a live classifier ─
//
// Phase 619's neighbour sweep asked whether any other API record carried
// a blanket `[<AllowAnonymous>]` that had outlived its justification. The
// instance it found was materially worse than the one 619 fixed, because
// three facts compounded:
//
//   1. all six methods on `IContentAdminApi` were `[<AllowAnonymous>]`;
//   2. `ContentAdminCompose.withContentAdmin` mounted through raw
//      `Remoting.buildHttpHandler`, so NO auth-context resolver was
//      composed and the Phase 69d classifier never ran — the attributes
//      were inert metadata and tightening them alone would have changed
//      nothing;
//   3. `ContentAdminApiImpl.create` binds a FIXED
//      `PublicPageEntity.PublicScope`, so the `StorageScope` isolation
//      that defends every other blanket-anonymous record in the tree had
//      nothing to isolate.
//
// Composed: an unauthenticated, cross-scope write to the publicly-served
// page overlay — including `SetStatus`, which carries
// `[<Audit "PolicyChanged">]` and, on the bare mount, emitted no audit
// row either.
//
// What this pack pins, section by section, and why each is here rather
// than assumed:
//
//   A — the load-bearing half (627.A). The classifier is ARMED. Proved
//       the only way it can be: `Api.make` over a deliberately
//       unclassified contract REFUSES, and the pre-627 bare
//       `buildHttpHandler` shape over the very same contract does NOT.
//       Without that second half, "it refuses" could be a property of
//       the fixture rather than of the mount.
//   B — the per-method classification (627.B). Six methods, six
//       `[<RequiresRole "PlatformAdmin">]`, ZERO anonymous. Record-wide,
//       so a seventh method added without a gate fails here.
//   C — an anonymous subject is actually REFUSED, driven through the
//       real `ApiSeams.defaultForgeAuthContextResolver` against a bare
//       `HttpContext` — not a hand-rolled double that could agree with
//       the assertion for the wrong reason (627.D).
//   C' — the falsifier. The SAME probe against a fixture still annotated
//       the pre-627 way must ALLOW, or section C's deny proves nothing
//       about the new gate.
//   D — a `PlatformAdmin` caller PASSES, and an ordinary authenticated
//       caller is DENIED. Both halves matter: the first says the gate is
//       not a Phase 132 dead gate, the second is this phase's deliberate
//       breaking change and would otherwise go unpinned.
//   E — no dead gate introduced, and every field round-trips the route
//       builder this API actually mounts on (not the SDK default — this
//       contract carries its own `ContentAdminApi.routeBuilder`, and a
//       lookup miss is Phase 132 deny-on-miss).
//   F — the Phase 438 `AuthorizationSurface` reports nothing anonymous.
//   G — 627.E: the surface can now distinguish "anonymous at the
//       attribute layer, gated in-handler" from a genuinely open door,
//       so `anonymousReachable` means what its name says.

// ── the record under test ───────────────────────────────────────────

let private contentAdminApiType = typeof<IContentAdminApi>

let private contentAdminMethods = [
    "ListPages"
    "GetPage"
    "SavePage"
    "SetStatus"
    "ListRevisions"
    "RestoreRevision"
]

/// The pre-627 shape, kept as a live fixture rather than as prose. It is
/// what makes the deny assertions in section C falsifiable: the same
/// probe, the same resolver, the opposite verdict.
type private PreviousContentAdminShape = {
    [<AllowAnonymous>]
    ListPages: unit -> Async<int>
    [<AllowAnonymous>]
    GetPage: string -> Async<int>
    [<AllowAnonymous>]
    SavePage: string -> Async<int>
    [<AllowAnonymous>]
    SetStatus: string -> Async<int>
    [<AllowAnonymous>]
    ListRevisions: string -> Async<int>
    [<AllowAnonymous>]
    RestoreRevision: string -> Async<int>
}

/// A contract with one method carrying no authorization attribute at
/// all. Section A drives this through both mount shapes; it is the probe
/// that shows which of them reads attributes.
type private UnclassifiedAdminShape = {
    [<RequiresRole "PlatformAdmin">]
    Classified: unit -> Async<int>
    // deliberately bare — no [<RequiresRole>] / [<RequiresClaim>] /
    // [<TenantScoped>] / [<AllowAnonymous>] / [<PublicEndpoint>]
    Unclassified: string -> Async<int>
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

/// An ordinary signed-in caller who is NOT a platform admin — the
/// `ScopeResolutionMiddleware` stamp without the `ToolUp.PlatformRole`
/// grant.
let private authenticatedContext () = async {
    let ctx = DefaultHttpContext() :> HttpContext

    // Fully qualified: `Subject.AuthenticatedUser` is a DU case in
    // scope, so a bare `AuthenticatedUser.anonymous` resolves to the
    // case constructor, not the module.
    let user: ToolUp.Platform.Auth.AuthenticatedUser = {
        UserId = "u-627"
        DisplayName = "Ordinary Caller"
        Email = Some "caller@example.test"
        TenantId = None
        Roles = []
    }

    ctx.Items["ToolUp.User"] <- box user
    ctx.Items["ToolUp.Subject"] <- box (Subject.AuthenticatedUser "u-627")
    let! forge = ApiSeams.defaultForgeAuthContextResolver ctx
    return bridge forge
}

/// A platform admin: the same stamp PLUS the server-resolved
/// `ToolUp.PlatformRole` the Phase 132 bridge reads. This is the ONLY
/// way `HasRole "PlatformAdmin"` returns true against the default
/// resolver — `AuthenticatedUser.Roles` is never consulted for it.
let private platformAdminContext () = async {
    let ctx = DefaultHttpContext() :> HttpContext

    let user: ToolUp.Platform.Auth.AuthenticatedUser = {
        UserId = "admin-627"
        DisplayName = "Platform Admin"
        Email = Some "admin@example.test"
        TenantId = None
        Roles = []
    }

    ctx.Items["ToolUp.User"] <- box user
    ctx.Items["ToolUp.Subject"] <- box (Subject.AuthenticatedUser "admin-627")
    ctx.Items["ToolUp.PlatformRole"] <- box PlatformRole.PlatformAdmin
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

let private componentId = ComponentId.create "toolup.contentauthoring"

[<Tests>]
let tests =
    testList "Phase 627 — IContentAdminApi authentication + armed classifier" [

        // ── A — the classifier is ARMED (627.A, load-bearing) ────────
        testList "A — the mount arms the Phase 69d classifier" [
            test "Api.make REFUSES a contract carrying an unclassified method" {
                // This is what `withContentAdmin` now mounts through. The
                // refusal is raised while BUILDING the handler, so a
                // deployment finds out at startup rather than at the
                // first unguarded request.
                let build () =
                    Api.make<UnclassifiedAdminShape> (
                        (fun _ -> {
                            Classified = fun () -> async { return 0 }
                            Unclassified = fun _ -> async { return 0 }
                        }),
                        routeBuilder = ContentAdminApi.routeBuilder
                    )
                    |> ignore

                let raised =
                    try
                        build ()
                        None
                    with e ->
                        Some e

                match raised with
                | None -> failtest "Api.make must refuse a contract carrying an unclassified method"
                | Some e ->
                    Expect.stringContains
                        e.Message
                        "Unclassified"
                        "the refusal must name the offending field, so a startup failure is actionable"
            }

            test "the PRE-627 bare mount shape accepts the very same contract" {
                // The falsifier for section A's first case. `Remoting`
                // composes no auth-context resolver, so the classifier is
                // dormant and an unclassified method sails through — which
                // is precisely why the six `[<AllowAnonymous>]` attributes
                // on `IContentAdminApi` were inert before this phase, and
                // why 627.A had to change the MOUNT and not just the
                // attributes. If this ever starts throwing, the first case
                // has stopped measuring the arming.
                let build () =
                    Remoting.createApi ()
                    |> Remoting.withRouteBuilder ContentAdminApi.routeBuilder
                    |> Remoting.fromContext (fun (_: HttpContext) -> {
                        Classified = fun () -> async { return 0 }
                        Unclassified = fun _ -> async { return 0 }
                    })
                    |> Remoting.buildHttpHandler
                    |> ignore

                build ()
            }

            test "withContentAdmin composes the real contract through the armed path" {
                // End to end: the shipped record is fully classified, so
                // the armed mount builds cleanly and appends exactly one
                // handler. A regression that dropped a gate from the
                // record would fail this by throwing, not by returning a
                // wrong count.
                let before = ServerApp.empty
                let after = ContentAdminCompose.withContentAdmin before

                Expect.equal
                    (List.length after.Extensions.Handlers)
                    (List.length before.Extensions.Handlers + 1)
                    "withContentAdmin appends exactly one handler"
            }
        ]

        // ── B — the classification (627.B) ───────────────────────────
        testList "B — per-method classification" [
            test "no method on IContentAdminApi is AllowAnonymous or PublicEndpoint" {
                // Record-wide, not a hand-listed six: a seventh method
                // added without a gate fails here.
                let reachableAnonymously =
                    AuthClassifier.classify contentAdminApiType
                    |> Map.toList
                    |> List.choose (fun (name, cls) ->
                        match cls with
                        | Anonymous -> Some(name + " (AllowAnonymous)")
                        | Public -> Some(name + " (PublicEndpoint)")
                        | Unclassified -> Some(name + " (unclassified)")
                        | RequiresAuth _ -> None)

                Expect.isEmpty
                    reachableAnonymously
                    "Phase 627: every IContentAdminApi method must carry a real gate. This surface writes the publicly-served `_public` page overlay at a FIXED scope, so there is no StorageScope isolation behind the attribute gate — the gate carries the whole weight. The published READ path for anonymous visitors is the ToolUp.PublicRendering overlay renderer, not this authoring contract."
            }

            test "every method requires the PlatformAdmin role" {
                for methodName in contentAdminMethods do
                    match classificationOf contentAdminApiType methodName with
                    | RequiresAuth requirements ->
                        Expect.equal
                            requirements
                            [ RoleRequired "PlatformAdmin" ]
                            (sprintf
                                "%s must carry exactly [<RequiresRole \"PlatformAdmin\">] — the overlay this surface writes is deployment-wide and platform-owned, so the gate is platform-level, not scope-level"
                                methodName)
                    | other -> failtestf "%s: expected RequiresAuth, got %A" methodName other
            }

            test "the classifier sees all six methods and no others" {
                let classified =
                    AuthClassifier.classify contentAdminApiType
                    |> Map.toList
                    |> List.map fst
                    |> List.sort

                Expect.equal classified (List.sort contentAdminMethods) "IContentAdminApi's classified field set"
            }

            test "SetStatus still carries its audit annotation" {
                // The audit annotation was inert before 627 for the same
                // reason the auth attributes were — the bare mount
                // composed no IAuditEmitter. `Api.make` composes one when
                // the record carries [<Audit>], so losing the annotation
                // would silently un-audit the policy lever again.
                let audited = Audit.classify contentAdminApiType

                Expect.isTrue
                    (Map.containsKey "SetStatus" audited)
                    "SetStatus is the policy-changing method on this surface; without [<Audit>] the dispatcher composes no audit emitter for the record at all"
            }
        ]

        // ── C — the anonymous path is actually refused (627.D) ───────
        testList "C — an anonymous subject is refused" [
            for methodName in contentAdminMethods do
                testCaseAsync (sprintf "%s denies an anonymous caller" methodName)
                <| async {
                    let! anon = anonymousContext ()

                    expectDeny
                        (AuthClassifier.evaluate (classificationOf contentAdminApiType methodName) (Some anon))
                        (sprintf
                            "IContentAdminApi.%s against the real default resolver with no authenticated user"
                            methodName)
                }

            testCaseAsync "a missing auth-context resolver also denies (fail-closed)"
            <| async {
                // The other anonymous shape: a deployment with no
                // resolver armed at all. `RequiresAuth` + `None` is a
                // deny by construction; pinned so a future refactor
                // cannot quietly make an unresolved context permissive.
                for methodName in contentAdminMethods do
                    expectDeny
                        (AuthClassifier.evaluate (classificationOf contentAdminApiType methodName) None)
                        (sprintf "IContentAdminApi.%s with no auth-context resolver" methodName)
            }
        ]

        // ── C' — the falsifier ───────────────────────────────────────
        testList "C' — the probe can fail" [
            testCaseAsync "the SAME probe ALLOWS the pre-627 [<AllowAnonymous>] shape"
            <| async {
                // If this ever starts denying, section C has stopped
                // measuring the gate and started measuring something
                // else — at which point section C passing means nothing.
                let! anon = anonymousContext ()

                for methodName in contentAdminMethods do
                    expectAllow
                        (AuthClassifier.evaluate
                            (classificationOf typeof<PreviousContentAdminShape> methodName)
                            (Some anon))
                        (sprintf
                            "PreviousContentAdminShape.%s is [<AllowAnonymous>] — it MUST allow, or section C's deny proves nothing about the new gate"
                            methodName)
            }
        ]

        // ── D — admin passes, ordinary caller does not ───────────────
        testList "D — the gate admits exactly the intended caller" [
            testCaseAsync "every method allows a PlatformAdmin caller"
            <| async {
                // The dead-gate check with teeth. `"PlatformAdmin"` is
                // the one role string the default resolver can emit, and
                // only via the server-resolved `ToolUp.PlatformRole`
                // bridge — so this case fails if someone "simplifies" the
                // fixture to set `AuthenticatedUser.Roles`, which the
                // bridge deliberately does not consult for this role.
                let! admin = platformAdminContext ()

                for methodName in contentAdminMethods do
                    expectAllow
                        (AuthClassifier.evaluate (classificationOf contentAdminApiType methodName) (Some admin))
                        (sprintf "IContentAdminApi.%s for a genuine platform admin" methodName)
            }

            testCaseAsync "every method DENIES an ordinary authenticated caller"
            <| async {
                // This phase's deliberate breaking change, pinned so it
                // cannot be softened by accident. An authenticated
                // non-admin clears `[<RequiresClaim "scope">]` — the gate
                // Phase 619 chose for the scope-owned IReportApi — and
                // must NOT clear this one: the overlay is deployment-wide
                // and bound to a fixed scope, so "any signed-in caller may
                // rewrite the public site" is not the policy.
                let! ordinary = authenticatedContext ()

                for methodName in contentAdminMethods do
                    expectDeny
                        (AuthClassifier.evaluate (classificationOf contentAdminApiType methodName) (Some ordinary))
                        (sprintf
                            "IContentAdminApi.%s for an authenticated NON-admin — the fixed-PublicScope binding means there is no per-caller isolation behind this gate"
                            methodName)
            }
        ]

        // ── E — no dead gate, no lookup miss ─────────────────────────
        testList "E — the gate is live, not dead" [
            test "no unemittable role gate was introduced" {
                // Phase 132: the default resolver only ever emits
                // "PlatformAdmin". `[<RequiresRole "Owner">]` / `"Admin"`
                // — the strings the pre-627 comment gestured at — would
                // deny EVERY caller with no compose-time signal. This
                // phase's gate happens to be the one emittable string,
                // which is exactly what makes it usable here.
                let dead =
                    AuthClassifier.unemittableRoles
                        (fun role -> role = "PlatformAdmin")
                        (AuthClassifier.classify contentAdminApiType)

                Expect.isEmpty dead "IContentAdminApi must not gate on a role the default resolver can never emit"
            }

            test "every classified field round-trips THIS contract's own route builder" {
                // Not the SDK default `/api/<type>/<method>`:
                // `withContentAdmin` mounts on
                // `ContentAdminApi.routeBuilder` (`/api/content-admin/…`),
                // and a field whose route's trailing segment diverges from
                // its name makes the per-request classification lookup
                // miss — which Phase 132 deny-on-miss then turns into a
                // total outage of the surface.
                let divergences =
                    AuthClassifier.nonRoundTripping
                        ContentAdminApi.routeBuilder
                        "IContentAdminApi"
                        (AuthClassifier.classify contentAdminApiType)

                Expect.isEmpty
                    divergences
                    "every IContentAdminApi field must round-trip /api/content-admin/<Method>, or the classification lookup misses and denies every call"
            }
        ]

        // ── F — the security-review artefact ─────────────────────────
        testList "F — the Phase 438 authorization surface" [
            test "IContentAdminApi contributes nothing to the anonymous-reachable headline" {
                let surface = AuthorizationSurface.ofApiRecord<IContentAdminApi> componentId

                let anonymous =
                    AuthorizationSurface.anonymousReachable surface
                    |> List.map AuthorizationSurface.describe

                Expect.isEmpty
                    anonymous
                    "before Phase 627 all six IContentAdminApi methods sat in this headline list — and, because the mount composed no resolver, they were reachable in fact and not merely in classification"
            }

            test "every IContentAdminApi entry is an explicit requirement" {
                let surface = AuthorizationSurface.ofApiRecord<IContentAdminApi> componentId

                let notExplicit =
                    surface.Exposed
                    |> List.filter (fun entry -> entry.Access <> ExplicitRequirement)
                    |> List.map AuthorizationSurface.describe

                Expect.isEmpty notExplicit "every IContentAdminApi endpoint declares a gate"

                Expect.equal
                    (surface.Exposed |> List.collect _.Requires |> List.distinct)
                    [ "role:PlatformAdmin" ]
                    "the manifest reports the same requirement the dispatcher enforces — there is only one classification"
            }

            test "the pre-627 shape diffs as a CRITICAL weakening against the shipped one" {
                let shipped = AuthorizationSurface.ofApiRecord<IContentAdminApi> componentId

                let regressed: AuthorizationSurface = {
                    Exposed =
                        AuthorizationSurface.ofApiRecord<PreviousContentAdminShape> componentId
                        |> _.Exposed
                        |> List.map (fun entry -> {
                            entry with
                                Endpoint = entry.Endpoint.Replace("PreviousContentAdminShape.", "IContentAdminApi.")
                        })
                }

                let delta = AuthorizationSurface.diff shipped regressed

                Expect.equal
                    (AuthorizationSurface.severity delta)
                    CriticalAuthorizationDrift
                    "reverting IContentAdminApi to [<AllowAnonymous>] must read as CRITICAL drift, not as noise"

                Expect.isNonEmpty delta.RequirementsWeakened "the six methods diff as weakened, endpoint for endpoint"
            }
        ]

        // ── G — 627.E: the headline set means what its name says ─────
        testList "G — attribute-anonymous-but-gated is distinguishable" [
            let gatedComponent = ComponentId.create "toolup.forms"

            let anonymousSurface () =
                AuthorizationSurface.ofApiRecord<PreviousContentAdminShape> gatedComponent

            let declaration endpoint rationale : InHandlerGateDeclaration = {
                GatedComponent = gatedComponent
                GatedEndpoint = endpoint
                GatedRationale = rationale
            }

            test "a declared in-handler gate leaves the anonymous-reachable headline" {
                // The 627.E motivation in one case: forge ships records
                // that are blanket-anonymous but genuinely gated inside
                // the handler (IFormApi 16 methods, JobApi 8,
                // ModelExecutionApi 7, IModuleQueryBusApi 1). All of them
                // landed in `anonymousReachable`, so a genuine open door
                // did not stand out from the noise — plausibly why this
                // phase's defect hid.
                let surface = anonymousSurface ()

                Expect.equal
                    (List.length (AuthorizationSurface.anonymousReachable surface))
                    6
                    "all six start in the headline set"

                let resolved =
                    surface
                    |> AuthorizationSurface.resolveWithInHandlerGates [
                        declaration "PreviousContentAdminShape.SavePage" "handler checks the share-token claim"
                    ]

                Expect.equal
                    (AuthorizationSurface.anonymousReachable resolved |> List.length)
                    5
                    "the declared endpoint left the headline set"

                Expect.equal
                    (AuthorizationSurface.gatedInHandler resolved |> List.map _.Endpoint)
                    [ "PreviousContentAdminShape.SavePage" ]
                    "and landed in the gated-in-handler set instead"

                Expect.equal
                    (AuthorizationSurface.anonymousAtAttributeLayer resolved |> List.length)
                    6
                    "the dispatcher-level question is unchanged — all six are still anonymous at the ATTRIBUTE layer"
            }

            test "the declared rationale rides the entry" {
                let resolved =
                    anonymousSurface ()
                    |> AuthorizationSurface.resolveWithInHandlerGates [
                        declaration "PreviousContentAdminShape.SavePage" "handler checks the share-token claim"
                    ]

                let entry = AuthorizationSurface.gatedInHandler resolved |> List.exactlyOne

                Expect.contains
                    entry.Requires
                    "gate:in-handler=handler checks the share-token claim"
                    "a reviewer who stops seeing the entry must be able to find out why in one line"
            }

            test "a blank rationale is ignored — an unnamed gate is no gate" {
                let resolved =
                    anonymousSurface ()
                    |> AuthorizationSurface.resolveWithInHandlerGates [
                        declaration "PreviousContentAdminShape.SavePage" "   "
                    ]

                Expect.equal
                    (AuthorizationSurface.anonymousReachable resolved |> List.length)
                    6
                    "a declaration that names no check must not quietly empty the headline set"
            }

            test "a declaration can never WEAKEN an entry that already has a real gate" {
                // The `resolveWithPolicy` rule, applied here: only
                // AnonymousReachable entries are refined. A stray
                // declaration against a properly gated endpoint must be
                // inert, or the mechanism becomes a way to downgrade a
                // real attribute gate to a claim.
                let shipped = AuthorizationSurface.ofApiRecord<IContentAdminApi> gatedComponent

                let resolved =
                    shipped
                    |> AuthorizationSurface.resolveWithInHandlerGates [
                        declaration "IContentAdminApi.SavePage" "a declaration that should not apply"
                    ]

                Expect.isEmpty
                    (AuthorizationSurface.gatedInHandler resolved)
                    "an explicitly gated endpoint is never reclassified by a declaration"

                Expect.equal resolved shipped "the surface is untouched"
            }

            test "a stale declaration naming an unknown endpoint is inert, not an error" {
                // Declarations are authored beside handlers; the surface
                // is derived from records. A rename leaves a stale
                // declaration behind, and that is an ordinary consequence
                // rather than a reason to fail a composition.
                let surface = anonymousSurface ()

                let resolved =
                    surface
                    |> AuthorizationSurface.resolveWithInHandlerGates [
                        declaration "SomeOtherApi.VanishedMethod" "renamed away"
                    ]

                Expect.equal resolved surface "an unmatched declaration changes nothing"
            }

            test "losing a declared gate diffs as a CRITICAL weakening" {
                // The direction that matters. GatedInHandler sits ABOVE
                // AnonymousReachable in `strength`, so a surface that
                // stops declaring its handler gate is a weakening — which
                // is what stops 627.E from being a way to launder an open
                // door into a quiet one.
                let open' = anonymousSurface ()

                let gated =
                    open'
                    |> AuthorizationSurface.resolveWithInHandlerGates [
                        declaration "PreviousContentAdminShape.SavePage" "handler checks the share-token claim"
                    ]

                let delta = AuthorizationSurface.diff gated open'

                Expect.equal
                    (AuthorizationSurface.severity delta)
                    CriticalAuthorizationDrift
                    "gated-in-handler -> anonymous-reachable is a weakening, and must read as critical"

                Expect.isNonEmpty delta.RequirementsWeakened "the endpoint diffs as weakened"
            }

            test "declaring a gate on a previously-open endpoint is a strengthening" {
                let open' = anonymousSurface ()

                let gated =
                    open'
                    |> AuthorizationSurface.resolveWithInHandlerGates [
                        declaration "PreviousContentAdminShape.SavePage" "handler checks the share-token claim"
                    ]

                let delta = AuthorizationSurface.diff open' gated

                Expect.isNonEmpty delta.RequirementsStrengthened "declaring a gate strengthens the entry"
                Expect.isEmpty delta.RequirementsWeakened "and weakens nothing"
            }

            test "the gated classification round-trips the wire projection" {
                // The golden-file gate compares a committed baseline
                // against a live derivation through `diff`, so a
                // classification that did not round-trip would read as
                // permanent drift.
                let gated =
                    anonymousSurface ()
                    |> AuthorizationSurface.resolveWithInHandlerGates [
                        declaration "PreviousContentAdminShape.SavePage" "handler checks the share-token claim"
                    ]

                let roundTripped =
                    gated |> AuthorizationSurface.toWire |> AuthorizationSurface.ofWire

                Expect.equal roundTripped gated "toWire >> ofWire is the identity on a gated-in-handler surface"

                Expect.equal
                    (AccessClassification.ofLabel (AccessClassification.label GatedInHandler))
                    GatedInHandler
                    "the label round-trips on its own too"
            }

            test "gated-in-handler sits between anonymous and the default-deny floor" {
                // Pinned because the ordering is what every diff verdict
                // above rests on, and it is a single integer away from
                // reporting a real regression as an improvement.
                Expect.isGreaterThan
                    (AccessClassification.strength GatedInHandler)
                    (AccessClassification.strength AnonymousReachable)
                    "a declared gate is stronger than nothing at all"

                Expect.isLessThan
                    (AccessClassification.strength GatedInHandler)
                    (AccessClassification.strength InheritedDefaultDeny)
                    "but weaker than the composition's fail-closed floor — the dispatcher genuinely lets the caller through, and nothing here can verify handler code"
            }
        ]
    ]